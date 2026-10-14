#!/usr/bin/env python3
"""wheel 번들 검증 — 번들로 만든 venv 에서 학습 스크립트들이 실제로 쓰는 import 가 되는지 확인한다.

pip install 성공만으로는 부족하다: anomalib 은 [core] 없이도 설치되지만 import 에서 lightning 이 없어 실패하고,
그러면 train_anomaly.py 가 조용히 간이 경로로 넘어간다 (2026-10-14 번들 venv 실증). make-wheel-bundle.ps1 이 부른다.
실패가 있으면 종료 코드 1.
"""
import importlib
import importlib.util
import os
import sys

here = os.path.dirname(os.path.abspath(__file__))
failures = []


def check(label, fn):
    try:
        fn()
        print(f"OK   {label}")
    except Exception as e:  # noqa: BLE001
        failures.append(label)
        print(f"FAIL {label}: {type(e).__name__}: {e}")


def imports(*names):
    def run():
        for n in names:
            importlib.import_module(n)
    return run


def anomalib_path():
    # train_anomaly.py 의 anomalib 경로와 같은 순서 — NumPy 2 호환 처리 → anomalib import → Windows 안전 처리
    spec = importlib.util.spec_from_file_location("train_anomaly", os.path.join(here, "train_anomaly.py"))
    ta = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(ta)
    ta._numpy2_compat_for_imgaug()
    from anomalib.data import Folder  # noqa: F401
    from anomalib.models import Patchcore, Fastflow, EfficientAd  # noqa: F401
    from anomalib.engine import Engine  # noqa: F401
    from anomalib.deploy import ExportType  # noqa: F401
    ta._windows_safe_engine()


check("기본 (torch · torchvision · transformers · onnx · onnxruntime · cv2 · numpy · yaml)",
      imports("torch", "torchvision", "transformers", "onnx", "onnxruntime", "cv2", "numpy", "yaml"))
check("torch CUDA 빌드", lambda: (_ for _ in ()).throw(RuntimeError("torch.version.cuda 없음"))
      if __import__("torch").version.cuda is None else None)
check("검출 학습 (train_dfine · pycocotools · torchmetrics)", imports("pycocotools", "torchmetrics", "safetensors"))
check("세그멘테이션 학습 (train_rfdetr_seg)", imports("rfdetr", "supervision", "peft"))
check("이상 탐지 간이 경로 (sklearn)", imports("sklearn"))
check("이상 탐지 anomalib 경로 (train_anomaly)", anomalib_path)

print("결과:", "모두 통과" if not failures else f"실패 {len(failures)}건 — " + ", ".join(failures))
sys.exit(1 if failures else 0)
