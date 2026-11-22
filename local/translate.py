"""Replik's persistent CPU translator. The parent exchanges ASCII JSON lines."""
import base64
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
from reference import EnglishReference, folder as reference_folder

CATALOG = "https://couchsubs-packs.enmeraapps.com/v1/packs"
MIRROR = "https://couchsubs-packs.enmera.workers.dev/v1/packs"
MODEL = Path(__file__).resolve().parent / "model"
MAX_PACK = 96 * 1024 * 1024


def normalize(text):
    text = re.sub(r"<[^<>]{0,40}>", " ", text)
    text = unicodedata.normalize("NFD", unicodedata.normalize("NFKC", text).lower())
    letters = (" " if not c.isalnum() else c for c in text if not unicodedata.category(c).startswith("M"))
    return " ".join("".join(letters).split())


def digest(normalized):
    return hashlib.sha256(normalized.encode()).hexdigest()[:16]


def key(text):
    return digest(normalize(text))


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
    glyphs = {"i": "l", "l": "i", "rn": "m", "m": "rn", "vv": "w", "w": "vv"}
    ambiguous_glyph = re.compile(r"rn|vv|[ilmw]")
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

    def restore(self):
        index = self.folder / "current.json"
        try:
            cached = json.loads(index.read_bytes())
            if not re.fullmatch("[0-9a-f]{64}", cached["sha256"]):
                return None
            self.load((self.folder / cached["sha256"]).read_bytes(), cached)
            return cached
        except (OSError, ValueError, KeyError, TypeError, EOFError):
            return None

    def refresh(self, cached):
        index = self.folder / "current.json"
        try:
            if cached and 0 <= time.time() - index.stat().st_mtime < 86400:
                return
        except OSError:
            pass
        for endpoint in (CATALOG, MIRROR):
            try:
                self.folder.mkdir(parents=True, exist_ok=True)
                catalog = json.loads(fetch(endpoint, 4 * 1024 * 1024))
                item = next(p for p in catalog["packs"] if p["id"] == "gta-6")
                if cached and int(item["version"]) < int(cached["version"]):
                    continue
                if not cached or item["sha256"] != cached["sha256"]:
                    data = fetch(endpoint + "/gta-6", MAX_PACK)
                    self.load(data, item)
                    atomic_write(self.folder / item["sha256"], data)
                atomic_write(index, json.dumps(item).encode())
                if cached and item["sha256"] != cached["sha256"]:
                    (self.folder / cached["sha256"]).unlink(missing_ok=True)
                return
            except (OSError, ValueError, KeyError, TypeError, StopIteration, EOFError):
                continue
        if not self.tables[0]:
            self.status = "GTA 6 paketi çevrimdışı veya kullanılamıyor"

    def sync(self):
        self.refresh(self.restore())

    def lookup(self, text, translate_missing=None):
        entries, segments = self.tables
        if not entries and not segments:
            return None

        def find(code):
            return entries.get(code) or segments.get(code)

        def match(line):
            return find(key(line))

        def nearby(line):
            normalized = normalize(line)
            if not 24 <= len(normalized) <= 200 or len(normalized.split()) < 5:
                return None
            found = None
            for word in re.finditer(r"(?<!\S)[a-z]{5,}(?!\S)", normalized):
                letters = word.group()
                for glyph in self.ambiguous_glyph.finditer(letters):
                    # Change one interior glyph in one word. Short words such as
                    # "no" and separate numbers never participate.
                    if glyph.start() == 0 or glyph.end() == len(letters):
                        continue
                    start, end = (word.start() + i for i in glyph.span())
                    candidate = normalized[:start] + self.glyphs[glyph.group()] + normalized[end:]
                    translated = find(digest(candidate))
                    if translated:
                        if found is not None:
                            return None
                        found = translated
            return found

        direct = match(text)
        if direct:
            return direct
        speaker = re.match(r"^([^:\n]{1,32}):\s+(.+)$", text)
        body = speaker[2] if speaker else text
        translation = match(body) if speaker else None
        if not translation:
            parts = re.split(r"\n+|(?<=[.!?…])\s+(?=[A-Z0-9])", body)
            if len(parts) > 1:
                matches = [match(part) for part in parts]
                if all(matches):
                    translation = " ".join(matches)
            if not translation:
                translation = nearby(text)
                if translation:
                    return translation
                translation = nearby(body) if speaker else None
            if not translation and len(parts) > 1:
                matches = [value or nearby(part) for part, value in zip(parts, matches)]
                missing = [i for i, value in enumerate(matches) if not value]
                if len(missing) == 1 and translate_missing is not None:
                    i = missing[0]
                    matches[i] = translate_missing(parts[i])
                if all(matches):
                    translation = " ".join(matches)
        if speaker and translation:
            return speaker[1] + ": " + translation
        return translation


class Opus:
    placeholder = re.compile(r"[\u2047\ufffd]|<unk>|(?<!\S)\?{2,}(?!\S)", re.IGNORECASE)
    word = re.compile(r"[^\W\d_]+")

    def __init__(self):
        import ctranslate2
        import sentencepiece as sp
        self.source = sp.SentencePieceProcessor(model_file=str(MODEL / "source.spm"))
        self.target = sp.SentencePieceProcessor(model_file=str(MODEL / "target.spm"))
        self.engine = ctranslate2.Translator(str(MODEL), device="cpu", compute_type="int8",
                                            intra_threads=min(4, os.cpu_count() or 1))

    def translate(self, text):
        pieces = self.source.encode(text, out_type=str) + ["</s>"]
        limit = min(256, max(32, len(pieces) * 4))
        output = self.engine.translate_batch([pieces], beam_size=1, max_decoding_length=limit)
        result = self.target.decode(output[0].hypotheses[0]).strip()
        words = len(self.word.findall(result))
        damaged = not words or self.placeholder.search(result)
        # Turkish is often shorter, so retry only when most of a longer input
        # appears to have been omitted.
        minimum = len(self.word.findall(text)) // 4
        if not damaged and (minimum < 2 or words >= minimum):
            return result
        output = self.engine.translate_batch([pieces], beam_size=2, replace_unknowns=True, max_decoding_length=limit)
        alternative = self.target.decode(output[0].hypotheses[0]).strip()
        new_words = len(self.word.findall(alternative))
        if new_words and not self.placeholder.search(alternative) and (damaged or new_words >= minimum):
            return alternative
        return result


def serve(warm_model=False, prepare_reference=False):
    pack = GamePack(Path(os.environ["LOCALAPPDATA"]) / "Replik" / "packs")
    reference = EnglishReference(reference_folder())
    model = None
    model_lock = threading.Lock()

    def get_model():
        nonlocal model
        with model_lock:
            if model is None:
                model = Opus()
            return model

    def warm():
        try:
            get_model()
        except Exception:
            # A local request can retry. Pack-only replies remain available.
            pass

    if warm_model:
        threading.Thread(target=warm).start()
    cached = pack.restore()
    threading.Thread(target=pack.refresh, args=(cached,), daemon=True).start()
    if prepare_reference:
        threading.Thread(target=reference.prepare, daemon=True).start()

    def translate(text):
        return get_model().translate(text)

    for line in sys.stdin.buffer:
        try:
            request = json.loads(line)
            text = base64.b64decode(request["line"]).decode("utf-8")
            corrected = reference.correct(text) if request.get("reference", False) else text
            changed = corrected if corrected != text else None
            text = corrected
            translated = pack.lookup(text, translate if request["local"] else None) if request.get("pack", True) else None
            provider = "GTA 6 paketi"
            if not translated and request["local"]:
                translated = translate(text)
                provider = "OPUS-MT"
            reply = {"text": translated, "provider": provider, "pack": pack.status, "source": changed}
        except Exception as exc:
            reply = {"error": str(exc)}
        print(json.dumps(reply, ensure_ascii=True), flush=True)


if __name__ == "__main__":
    if sys.argv[1:] == ["--sync-pack"]:
        GamePack(Path(os.environ["LOCALAPPDATA"]) / "Replik" / "packs").sync()
    else:
        serve("--warm-model" in sys.argv[1:], "--prepare-reference" in sys.argv[1:])
