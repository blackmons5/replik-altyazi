"""Local index of the published English dialogue reference."""
import csv
from contextlib import closing
import hashlib
import os
from pathlib import Path
import re
import sqlite3
import unicodedata
from urllib.request import Request, urlopen


REVISION = "6c4cd177a46fa4cb08eac4da69dffcfc8bf01949"
SOURCE = f"https://raw.githubusercontent.com/psviethoa/gta6-text-en/{REVISION}/all_rows.tsv"
SIZE = 72063506
SHA256 = "39a002c0f7c0bac392a56eb61f7f7dc11e3ad2377a654367037058a76d2e2a55"
SCHEMA = 1
GLYPHS = {"i": "l", "l": "i", "rn": "m", "m": "rn", "vv": "w", "w": "vv"}
AMBIGUOUS = re.compile(r"rn|vv|[ilmw]")
WORDS = re.compile(r"(?<!\S)[a-z]{5,}(?!\S)")
TOKENS = re.compile(r"[^\W_]+(?:'[^\W_]+)*")
FORMAT = re.compile(r"<[^<>]{0,40}>|~[\w]+~")
SPEAKER = re.compile(r"^([^:\n]{1,32}:\s+)(.+)$")


def normalized(text):
    text = FORMAT.sub(" ", text)
    text = unicodedata.normalize("NFKC", text).lower().replace("’", "'")
    return " ".join(TOKENS.findall(text))


def folder():
    return Path(os.environ["LOCALAPPDATA"]) / "Replik" / "english-reference"


class EnglishReference:
    def __init__(self, directory):
        self.directory = directory
        self.path = directory / f"dialogue-{REVISION[:12]}-{SCHEMA}.sqlite3"
        self.connection = None

    def _open(self):
        return sqlite3.connect(self.path.resolve().as_uri() + "?mode=ro&immutable=1", uri=True)

    def ready(self):
        if not self.path.is_file():
            return False
        try:
            with closing(self._open()) as connection:
                return connection.execute("PRAGMA user_version").fetchone() == (SCHEMA,) and bool(
                    connection.execute("SELECT 1 FROM dialogue LIMIT 1").fetchone()
                )
        except sqlite3.Error:
            return False

    def _download(self, target):
        request = Request(SOURCE, headers={"User-Agent": "AccelSubs-English-Reference/1"})
        hash_state = hashlib.sha256()
        size = 0
        with urlopen(request, timeout=20) as stream, target.open("wb") as output:
            while block := stream.read(256 * 1024):
                size += len(block)
                if size > SIZE:
                    raise ValueError("English reference exceeded its declared size")
                hash_state.update(block)
                output.write(block)
        if size != SIZE or hash_state.hexdigest() != SHA256:
            raise ValueError("English reference checksum mismatch")

    def prepare(self):
        if self.ready():
            return True
        lock = None
        try:
            self.directory.mkdir(parents=True, exist_ok=True)
            lock = sqlite3.connect(self.directory / "prepare.lock", timeout=0)
            lock.execute("BEGIN EXCLUSIVE")
            if self.ready():
                return True
            source = self.directory / "source.tsv.part"
            index = self.directory / "dialogue.sqlite3.part"
            index.unlink(missing_ok=True)
            try:
                self._download(source)
                self.build(source, index)
                os.replace(index, self.path)
            finally:
                source.unlink(missing_ok=True)
                index.unlink(missing_ok=True)
            return True
        except (OSError, ValueError, sqlite3.Error, csv.Error):
            return False
        finally:
            if lock is not None:
                lock.close()

    @staticmethod
    def build(source, index):
        def rows():
            with source.open(encoding="utf-8-sig", newline="") as stream:
                sheet = csv.DictReader(stream, delimiter="\t")
                if not {"category", "en"} <= set(sheet.fieldnames or ()):
                    raise ValueError("Unknown English reference schema")
                for row in sheet:
                    if row["category"] not in ("dialogue", "subtitle"):
                        continue
                    value = row["en"] or ""
                    text = " ".join(FORMAT.sub(" ", value).split())
                    if not 1 <= len(text) <= 550:
                        continue
                    key = normalized(text)
                    if key:
                        yield key, text

        connection = sqlite3.connect(index)
        try:
            connection.execute("PRAGMA journal_mode=OFF")
            with connection:
                connection.execute("CREATE TABLE dialogue (key TEXT PRIMARY KEY, text TEXT NOT NULL) WITHOUT ROWID")
                connection.executemany("INSERT OR IGNORE INTO dialogue VALUES (?, ?)", rows())
                if not connection.execute("SELECT 1 FROM dialogue LIMIT 1").fetchone():
                    raise ValueError("The English reference contains no dialogue")
                connection.execute(f"PRAGMA user_version = {SCHEMA}")
        finally:
            connection.close()

    def correct(self, text):
        speaker = SPEAKER.fullmatch(text)
        body = speaker[2] if speaker else text
        key = normalized(body)
        if not 24 <= len(key) <= 220 or len(key.split()) < 5:
            return text
        if self.connection is None and not self.path.is_file():
            return text
        try:
            if self.connection is None:
                self.connection = self._open()
            if self.connection.execute("SELECT 1 FROM dialogue WHERE key = ?", (key,)).fetchone():
                return text
            variants = set()
            for word in WORDS.finditer(key):
                for glyph in AMBIGUOUS.finditer(word.group()):
                    if glyph.start() == 0 or glyph.end() == len(word.group()):
                        continue
                    start, end = (word.start() + i for i in glyph.span())
                    variants.add(key[:start] + GLYPHS[glyph.group()] + key[end:])
            if not variants:
                return text
            params = ",".join("?" for _ in variants)
            matches = self.connection.execute(
                f"SELECT text FROM dialogue WHERE key IN ({params}) LIMIT 2", tuple(variants)
            ).fetchall()
            if len(matches) == 1:
                return (speaker[1] if speaker else "") + matches[0][0]
        except (OSError, sqlite3.Error):
            if self.connection is not None:
                self.connection.close()
                self.connection = None
        return text


if __name__ == "__main__":
    raise SystemExit(0 if EnglishReference(folder()).prepare() else 1)
