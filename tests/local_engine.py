import importlib.util
import json
from pathlib import Path
import sys
import tempfile

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
