import importlib.util
import base64
import json
from pathlib import Path
import subprocess
import sys
import tempfile
from statistics import median
from time import perf_counter
from types import SimpleNamespace

base = Path(sys.argv[1]).resolve()
spec = importlib.util.spec_from_file_location("translate", base / "translate.py")
engine = importlib.util.module_from_spec(spec)
spec.loader.exec_module(engine)
model = engine.Opus()
lines = ["Thank you very much.", "Open the door."]
results = [model.translate(line) for line in lines]
assert "teşekkür" in results[0].casefold(), results
assert "kap" in results[1].casefold(), results
print("Bundled CPU translation passed:", ascii(results))

samples = [
    "Did you see what happened?",
    "We should wait here until the shop closes.",
    "I left the keys on the table, but you can take my car.",
    "Keep going straight. I'll call you when I get there.",
]
durations = []
for sample in samples:
    start = perf_counter()
    translated = model.translate(sample)
    durations.append((perf_counter() - start) * 1000)
    assert translated and not model.placeholder.search(translated), (sample, translated)
print("Warm local dialogue translation median ms:", round(median(durations)))

class ScriptedDecoder:
    def __init__(self, responses):
        self.responses = iter(responses)
        self.beams = []

    def translate_batch(self, inputs, *, beam_size, **options):
        self.beams.append(beam_size)
        return [SimpleNamespace(hypotheses=[[next(self.responses)]])]

probe = engine.Opus.__new__(engine.Opus)
probe.source = SimpleNamespace(encode=lambda text, **options: text.split())
probe.target = SimpleNamespace(decode=lambda tokens: tokens[0])
probe.engine = ScriptedDecoder(["⁇ Dikkat et.", "Arkadan gelen araca dikkat et."])
assert probe.translate("Watch out for the car behind you.") == "Arkadan gelen araca dikkat et."
assert probe.engine.beams == [1, 2]
probe.engine = ScriptedDecoder(["Gel.", "Kapıyı kilitle ve dışarıda beni bekle."])
assert probe.translate("Lock the front door and wait for me right outside.") == "Kapıyı kilitle ve dışarıda beni bekle."
assert probe.engine.beams == [1, 2]
probe.engine = ScriptedDecoder(["Dur!"])
assert probe.translate("Stop!") == "Dur!"
assert probe.engine.beams == [1]

def request(text):
    return json.dumps({"line": base64.b64encode(text.encode()).decode(), "local": True, "pack": False})

with subprocess.Popen([sys.executable, "-B", "-u", str(base / "translate.py")],
                      stdin=subprocess.PIPE, stdout=subprocess.PIPE) as worker:
    requests = "\n".join([request(lines[0]), request(lines[1]), ""]).encode("utf-8-sig")
    replies, _ = worker.communicate(requests, timeout=90)
    first, second = map(json.loads, replies.splitlines())
    assert worker.returncode == 0 and "teşekkür" in first["text"].casefold() and "kap" in second["text"].casefold(), replies
with tempfile.TemporaryDirectory() as tmp:
    folder = Path(tmp)
    pack = engine.GamePack(folder)
    pack.sync()
    meta = json.loads((folder / "current.json").read_bytes())
    assert meta["id"] == "gta-6" and len(pack.tables[0]) > 0
    print("Verified GTA 6 pack version", meta["version"], "catalog entries", meta["entries"])
    second = engine.GamePack(folder)
    def offline(*args):
        raise AssertionError("Unexpected network use")
    engine.fetch = offline
    second.sync()
    assert second.tables[0] and second.status == pack.status
    second.tables = ({engine.key("Open the door!"): "Kapıyı aç!"}, {})
    assert second.lookup("Lucia: OPEN the door.") == "Lucia: Kapıyı aç!"
    assert second.lookup("A different sentence.") is None
