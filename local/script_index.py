"""Build and query the offline GTA subtitle index. No network or model dependencies."""
import csv
import io
import re
import sqlite3
import sys
import time
import unicodedata
import zipfile
from pathlib import Path


def key(text):
    return " ".join(re.findall(r"[\w]+(?:'[\w]+)*", unicodedata.normalize("NFKC", text).casefold().replace("’", "'")))


def edges(value):
    # A single damaged word cannot spoil both ends of a three-word sentence.
    width = 8 if len(value) < 30 else 14
    return value[:width], value[-width:]


def build(archive, target):
    db = sqlite3.connect(target)
    with zipfile.ZipFile(archive) as source, db:
        db.execute("CREATE TABLE cue(id INTEGER PRIMARY KEY, file TEXT,cue INTEGER,start TEXT,wave TEXT,piece TEXT,scene TEXT,speaker TEXT,text TEXT,k TEXT,a TEXT,b TEXT,UNIQUE(file,cue))")
        names = {Path(n.replace(chr(92), "/")).name: n for n in source.namelist()}
        with source.open(names["map.csv"]) as stream:
            groups = {}
            for row in csv.DictReader(io.TextIOWrapper(stream, encoding="utf-8-sig", newline="")):
                groups.setdefault(row["file"], []).append(row)
        if set(groups) != {n for n in names if n.endswith(".srt")}:
            raise ValueError("SRT and map filenames differ")
        vocabulary = set()
        for filename, rows in sorted(groups.items()):
            content = source.read(names[filename]).decode("utf-8-sig").replace("\r", "").strip()
            blocks = re.split(r"\n[ \t]*\n", content)
            for block, row in zip(blocks, rows, strict=True):
                number, timing, raw = block.split("\n", 2)
                text = " ".join(raw.split())
                start = timing.split(" --> ")[0]
                if (number, start, text) != (row["cue"], row["start"], " ".join(row["text_en"].split())):
                    raise ValueError(f"Mismatched subtitle {filename}/{number}")
                k = key(text)
                vocabulary.update(k.split())
                db.execute("INSERT INTO cue VALUES(NULL,?,?,?,?,?,?,?,?,?,?,?)",
                           (filename, int(number), start, row["wave"], row["piece"], row["scene"], row["speaker"], text, k, *edges(k)))
        for col in ("k", "a", "b"):
            db.execute(f"CREATE INDEX by_{col} ON cue({col})")
        db.execute("CREATE INDEX by_scene ON cue(scene,id)")
        db.execute("CREATE TABLE vocabulary(word TEXT PRIMARY KEY) WITHOUT ROWID")
        db.executemany("INSERT INTO vocabulary VALUES(?)", ((w,) for w in sorted(vocabulary)))
    db.close()


def glyphs(word):
    return word.replace("rn", "m").replace("vv", "w").replace("1", "l").replace("i", "l").replace("0", "o")


def one_edit(a, b):
    if len(a) > len(b):
        a, b = b, a
    if len(b) - len(a) > 1:
        return False
    for i, char in enumerate(a):
        if char != b[i]:
            return a[i + (len(a) == len(b)):] == b[i + 1:]
    return True


def plausible(words, candidate):
    other = candidate.split()
    if len(words) != len(other):
        return False
    changes = [(a, b) for a, b in zip(words, other) if a != b]
    if len(changes) != 1:
        return False
    a, b = changes[0]
    if min(len(a), len(b)) < 4 or any(c.isdigit() for c in a+b) or "'" in a+b:
        return False
    return glyphs(a) == glyphs(b) or (min(len(a), len(b)) >= 5 and one_edit(a, b))


class ScriptIndex:
    def __init__(self, path):
        self.db = None
        self.position = None
        try:
            self.db = sqlite3.connect(path.resolve().as_uri() + "?mode=ro&immutable=1", uri=True)
            self.db.row_factory = sqlite3.Row
        except sqlite3.Error:
            pass

    def resolve(self, raw, now=None):
        self.matched = False
        if self.db is None:
            return raw
        now = time.monotonic() if now is None else now
        try:
            return self._resolve(raw, now)
        except sqlite3.Error:
            self.db.close()
            self.db = None
            return raw

    def _resolve(self, raw, now):
        prefix, sep, body = raw.partition(": ")
        if not sep or len(prefix) > 32:
            prefix, sep, body = "", "", raw
        k = key(body)
        if not k or len(k) > 350:
            return raw
        near = []
        if self.position and 0 <= now - self.position[2] <= 10:
            scene, previous, _ = self.position
            near = self.db.execute("SELECT * FROM cue WHERE scene=? AND id BETWEEN ? AND ?",
                                   (scene, previous, previous + 4)).fetchall()
        exact = self.db.execute("SELECT * FROM cue WHERE k=? LIMIT 33", (k,)).fetchall()
        words = k.split()
        if exact:
            rows = [r for r in near if r["k"] == k] or exact
            # Short lines and punctuation collisions retain the player's original reading.
            literal = [r for r in rows if r["text"].casefold() == body.casefold()]
            if literal:
                rows = literal
            elif len(words) < 3 or len(exact) == 33:
                return raw
        else:
            if len(words) < 3 or len(k) < 18:
                return raw
            # Protect actual English alternatives ("lights"/"nights", names, numbers).
            def accepted(r):
                if not plausible(words, r["k"]):
                    return False
                a, b = next((a,b) for a,b in zip(words,r["k"].split()) if a!=b)
                return glyphs(a)==glyphs(b) or not self.db.execute("SELECT 1 FROM vocabulary WHERE word=?", (a,)).fetchone()
            rows = [r for r in near if accepted(r)]
            if not rows:
                candidates = {}
                for col, edge in zip(("a", "b"), edges(k)):
                    batch = self.db.execute(f"SELECT * FROM cue WHERE {col}=? LIMIT 97", (edge,)).fetchall()
                    if len(batch) == 97:
                        # The unexamined portion might contain a competing match.
                        return raw
                    candidates.update((r["id"],r) for r in batch)
                rows = [r for r in candidates.values() if accepted(r)]
        if not rows or len({r["text"] for r in rows}) != 1:
            return raw
        if len(rows) == 1 and rows[0]["scene"] and len(k) >= 12:
            self.position = rows[0]["scene"], rows[0]["id"], now
        elif not any(r["id"] == n["id"] for r in rows for n in near):
            self.position = None
        self.matched = True
        return prefix + sep + rows[0]["text"]


if __name__ == "__main__":
    build(Path(sys.argv[1]), Path(sys.argv[2]))
