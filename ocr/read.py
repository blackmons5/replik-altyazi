import base64
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT / "packages"))
sys.path.insert(0, str(ROOT.parent / "local"))
from reference import EnglishReference, folder

import cv2
import numpy as np
from rapidocr import ModelType, OCRVersion, RapidOCR


def main(variant, model_folder):
    detection = model_folder / "det.onnx"
    recognition = model_folder / "rec.onnx"
    if not detection.is_file() or not recognition.is_file():
        raise FileNotFoundError("PP-OCRv5 mobile model files are missing")
    cv2.setNumThreads(1)
    version = OCRVersion.PPOCRV5 if variant == "mobile" else OCRVersion.PPOCRV6
    model_type = ModelType(variant)
    engine = RapidOCR(params={
        "Global.use_cls": False,
        "Global.log_level": "error",
        "Det.ocr_version": version,
        "Det.model_type": model_type,
        "Det.model_path": str(detection),
        "Det.limit_type": "max",
        "Det.limit_side_len": 1280,
        "Rec.ocr_version": version,
        "Rec.model_type": model_type,
        "Rec.model_path": str(recognition),
        "Rec.rec_img_shape": [3, 48, 320],
        "EngineConfig.onnxruntime.intra_op_num_threads": 2,
        "EngineConfig.onnxruntime.inter_op_num_threads": 1,
    })
    reference = EnglishReference(folder())
    for request in sys.stdin.buffer:
        if request.startswith(b"line "):
            raw = base64.b64decode(request[5:]).decode("utf-8")
            print(json.dumps(reference.correct(raw, canonical=True), ensure_ascii=True), flush=True)
            continue
        data = np.frombuffer(base64.b64decode(request), dtype=np.uint8)
        img = cv2.imdecode(data, cv2.IMREAD_COLOR)
        result = engine(img, use_cls=False)
        rows = []
        if result.boxes is not None:
            rows = [
                {"text": text, "confidence": float(score), "points": box.reshape(-1).tolist()}
                for box, text, score in zip(result.boxes, result.txts, result.scores)
            ]
        print(json.dumps(rows, ensure_ascii=True), flush=True)


if __name__ == "__main__":
    main(sys.argv[1], Path(sys.argv[2]))
