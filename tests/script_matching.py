import base64
from contextlib import closing
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
from tempfile import TemporaryDirectory
from time import perf_counter

runtime = Path(sys.argv[1]).resolve()
sys.path.insert(0, str(runtime))
from script_index import ScriptIndex

db = runtime / "dialogue.sqlite3"
index = ScriptIndex(db)
assert index.db.execute("SELECT count(*) FROM cue").fetchone()[0] == 152679
assert index.resolve("When's the last time you played?", 0) == "When's the last time you played?"
assert index.position[:2] == ("ACBBL_FIRST_INSTANCE", 1)
assert index.resolve("Oof. Uh, high schooi?", 1) == "Oof. Uh, high school?"
assert index.position[:2] == ("ACBBL_FIRST_INSTANCE", 2)
assert index.resolve("Jason: You never toid me that.", 2) == "Jason: You never told me that."
assert index.resolve("No sign of Halifox.", 20) == "No sign of Halifax."
for miss in ("G0!", "Don't sell the 40 boats.", "Completely invented diagnostic phrase."):
    assert index.resolve(miss, 21) == miss and not index.matched
assert ScriptIndex(db.with_name("absent.sqlite")).resolve("Keep playing.") == "Keep playing."

# Two scene-specific lines differ by one letter from the same OCR error.
fixture = sqlite3.connect(":memory:")
fixture.row_factory = sqlite3.Row
fixture.execute("CREATE TABLE cue(id INTEGER, scene TEXT, text TEXT, k TEXT, a TEXT, b TEXT)")
fixture.execute("CREATE TABLE vocabulary(word TEXT PRIMARY KEY)")
from script_index import edges, key
for i, scene, line in [(1, "A", "Follow the coastal road."), (2, "A", "Check the poster by the gate."),
                       (3, "B", "Check the porter by the gate.")]:
    k = key(line)
    fixture.execute("INSERT INTO cue VALUES(?,?,?,?,?,?)", (i, scene, line, k, *edges(k)))
ambiguous = ScriptIndex(db.with_name("absent.sqlite"))
ambiguous.db = fixture
raw = "Check the poxter by the gate."
assert ambiguous.resolve(raw, 0) == raw
ambiguous.resolve("Follow the coastal road.", 1)
assert ambiguous.resolve(raw, 2) == "Check the poster by the gate."
assert ambiguous.resolve(raw, 20) == raw

# Compare output and source provenance through the actual embedded translation process.
with TemporaryDirectory() as profile:
    def run(text):
        request = {"line": base64.b64encode(text.encode()).decode(), "script": True, "local": True}
        result = subprocess.run([sys.executable, "-B", "-u", str(runtime / "translate.py")],
                                input=json.dumps(request).encode() + b"\n", stdout=subprocess.PIPE,
                                env={**os.environ, "LOCALAPPDATA": profile}, check=True, timeout=90)
        return json.loads(result.stdout)
    first = run("No sign of Halifox.")
    assert first["source"] == "No sign of Halifax." and first["text"] and first["provider"] == "OPUS-MT"
    with closing(sqlite3.connect(Path(profile) / "Replik/opus-en-tr-r1.sqlite3")) as cache:
        assert cache.execute("SELECT source,target FROM translations").fetchall() == [(first["source"], first["text"])]
        cache.execute("UPDATE translations SET target=?", ("Halifax görünmüyor.",))
        cache.commit()
    assert run("No sign of Halifax.")["text"] == "Halifax görünmüyor."

samples = [r[0] for r in index.db.execute("SELECT text FROM cue WHERE id%601=0")]
times = []
for line in samples:
    start = perf_counter()
    index.resolve(line)
    times.append(1000 * (perf_counter() - start))
print(f"Verified SRT index, fallback, scene order and persistent MT cache. Exact lookup p95: {sorted(times)[int(.95*len(times))]:.2f} ms")
