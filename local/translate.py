"""Replik's persistent CPU translator. The parent exchanges ASCII JSON lines."""
import base64
from concurrent.futures import ThreadPoolExecutor
import gzip
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import tempfile
import threading
import time
import unicodedata
from urllib.request import Request, urlopen

CATALOG = "https://couchsubs-packs.enmeraapps.com/v1/packs"
MODEL = Path(__file__).resolve().parent / "model"
MAX_PACK = 96 * 1024 * 1024


def key(text):
    text = re.sub(r"<[^<>]{0,40}>", " ", text)
    text = unicodedata.normalize("NFD", unicodedata.normalize("NFKC", text).lower())
    letters = (" " if not c.isalnum() else c for c in text if not unicodedata.category(c).startswith("M"))
    normalized = " ".join("".join(letters).split())
    return hashlib.sha256(normalized.encode()).hexdigest()[:16]


def fetch(url, limit):
    request = Request(url, headers={"User-Agent": "Replik/0.2 (GTA-6 pack)"})
    with urlopen(request, timeout=20) as stream:
        data = stream.read(limit + 1)
    if len(data) > limit:
        raise ValueError("Paket boyut sınırını aştı")
    return data


def atomic_write(path, data):
    with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as tmp:
        name = tmp.name
    try:
        Path(name).write_bytes(data)
        os.replace(name, path)
    finally:
        Path(name).unlink(missing_ok=True)


class GamePack:
    def __init__(self, folder):
        self.folder = folder
        self.tables = ({}, {})
        self.status = "GTA 6 paketi kontrol ediliyor"

    def load(self, data, metadata):
        if hashlib.sha256(data).hexdigest() != metadata["sha256"]:
            raise ValueError("Paket özeti uyuşmuyor")
        from io import BytesIO
        with gzip.GzipFile(fileobj=BytesIO(data)) as stream:
            raw = stream.read(256 * 1024 * 1024 + 1)
        if len(raw) > 256 * 1024 * 1024:
            raise ValueError("Paket açılamadı")
        obj = json.loads(raw)
        if obj["id"] != "gta-6" or obj["format"] != 2 or not isinstance(obj["entries"], dict):
            raise ValueError("Paket biçimi desteklenmiyor")
        segments = obj.get("segments", {})
        if not isinstance(segments, dict):
            raise ValueError("Geçersiz paket")
        self.tables = (obj["entries"], segments)
        self.status = "GTA 6 paketi v" + str(metadata["version"])

    def sync(self):
        cached = None
        try:
            self.folder.mkdir(parents=True, exist_ok=True)
            index = self.folder / "current.json"
            if index.exists():
                try:
                    cached = json.loads(index.read_bytes())
                    if not re.fullmatch("[0-9a-f]{64}", cached["sha256"]):
                        raise ValueError()
                    self.load((self.folder / cached["sha256"]).read_bytes(), cached)
                    if 0 <= time.time() - index.stat().st_mtime < 86400:
                        return
                except (OSError, ValueError, KeyError, TypeError, EOFError):
                    cached = None
            catalog = json.loads(fetch(CATALOG, 4 * 1024 * 1024))
            item = next(p for p in catalog["packs"] if p["id"] == "gta-6")
            if not cached or item["sha256"] != cached["sha256"]:
                data = fetch(CATALOG + "/gta-6", MAX_PACK)
                self.load(data, item)
                atomic_write(self.folder / item["sha256"], data)
            atomic_write(index, json.dumps(item).encode())
            if cached and item["sha256"] != cached["sha256"]:
                (self.folder / cached["sha256"]).unlink(missing_ok=True)
        except Exception:
            if not self.tables[0]:
                self.status = "GTA 6 paketi çevrimdışı veya kullanılamıyor"

    def lookup(self, text):
        entries, segments = self.tables
        def match(line):
            digest = key(line)
            return entries.get(digest) or segments.get(digest)
        direct = match(text)
        if direct:
            return direct
        speaker = re.match(r"^([^:\n]{1,32}):\s+(.+)$", text)
        body = speaker[2] if speaker else text
        translation = match(body) if speaker else None
        if not translation:
            parts = re.split(r"(?<=[.!?…])\s+(?=[A-Z0-9])", body)
            if len(parts) > 1:
                translated = [match(part) for part in parts]
                if all(translated):
                    translation = " ".join(translated)
        if speaker and translation:
            return speaker[1] + ": " + translation
        return translation


class Opus:
    def __init__(self):
        import ctranslate2
        import sentencepiece as sp
        self.source = sp.SentencePieceProcessor(model_file=str(MODEL / "source.spm"))
        self.target = sp.SentencePieceProcessor(model_file=str(MODEL / "target.spm"))
        self.engine = ctranslate2.Translator(str(MODEL), device="cpu", compute_type="int8",
                                            intra_threads=min(4, os.cpu_count() or 1))

    def translate(self, text, cancelled=None):
        pieces = self.source.encode(text, out_type=str) + ["</s>"]
        output = self.engine.translate_batch([pieces], beam_size=1, max_decoding_length=256,
                                             disable_unk=True,
                                             callback=(lambda step: cancelled.is_set()) if cancelled else None)
        return self.target.decode(output[0].hypotheses[0]).strip()


def request_text(line):
    return base64.b64decode(json.loads(line)["line"]).decode("utf-8")


def send(reply):
    print(json.dumps(reply, ensure_ascii=True), flush=True)


def serve_model():
    model = None

    def translate(line, cancelled):
        nonlocal model
        try:
            text = request_text(line)
            model = model or Opus()
            result = None if cancelled.is_set() else model.translate(text, cancelled)
            send({"text": None if cancelled.is_set() else result, "provider": "OPUS-MT"})
        except Exception as exc:
            send({"error": str(exc)})

    # The parent sends its next request only after consuming the previous reply.
    with ThreadPoolExecutor(max_workers=1) as executor:
        cancelled = None
        for line in sys.stdin:
            if line.strip() == "cancel":
                if cancelled is not None:
                    cancelled.set()
            else:
                cancelled = threading.Event()
                executor.submit(translate, line, cancelled)


def serve_pack():
    pack = GamePack(Path(os.environ["LOCALAPPDATA"]) / "Replik" / "packs")
    threading.Thread(target=pack.sync, daemon=True).start()
    for line in sys.stdin:
        try:
            reply = {"text": pack.lookup(request_text(line)), "provider": "GTA 6 paketi", "pack": pack.status}
        except Exception as exc:
            reply = {"error": str(exc)}
        send(reply)


if __name__ == "__main__":
    if sys.argv[1:] == ["--sync-pack"]:
        GamePack(Path(os.environ["LOCALAPPDATA"]) / "Replik" / "packs").sync()
    elif sys.argv[1:] == ["--model"]:
        serve_model()
    elif sys.argv[1:] == ["--pack"]:
        serve_pack()
