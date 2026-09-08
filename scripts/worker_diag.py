"""
워커 자기진단 (MLOps Phase 3 §6). stdout 에 JSON 한 덩어리를 출력한다.
  python_version · cuda_available · gpu_name · gpu_mem_mb · cuda_version · packages{name: version}
  onnxruntime_providers[] · failures[]  (failures 가 비어 있지 않으면 서버가 워커를 Disabled 로 표시)
의존성 없이 동작해야 하므로 모든 import 는 try/except 로 감싼다.
"""
import json
import platform
import sys

result = {
    "python_version": platform.python_version(),
    "cuda_available": False,
    "gpu_name": None,
    "gpu_mem_mb": 0,
    "cuda_version": None,
    "packages": {},
    "onnxruntime_providers": [],
    "failures": [],
}

if sys.version_info < (3, 10):
    result["failures"].append(f"python >= 3.10 필요 (현재 {platform.python_version()})")

try:
    import torch  # noqa
    result["packages"]["torch"] = torch.__version__
    result["cuda_available"] = bool(torch.cuda.is_available())
    if result["cuda_available"]:
        result["gpu_name"] = torch.cuda.get_device_name(0)
        result["gpu_mem_mb"] = int(torch.cuda.get_device_properties(0).total_memory // (1024 * 1024))
        result["cuda_version"] = torch.version.cuda
except Exception as e:  # noqa
    result["failures"].append(f"torch import 실패: {e}")

try:
    import transformers  # noqa
    result["packages"]["transformers"] = transformers.__version__
    major, minor = (int(x) for x in transformers.__version__.split(".")[:2])
    if (major, minor) < (4, 52):
        result["failures"].append(f"transformers >= 4.52 필요 (현재 {transformers.__version__})")
except Exception as e:  # noqa
    result["failures"].append(f"transformers import 실패: {e}")

try:
    import onnxruntime as ort  # noqa
    result["packages"]["onnxruntime"] = ort.__version__
    result["onnxruntime_providers"] = list(ort.get_available_providers())
except Exception as e:  # noqa
    result["failures"].append(f"onnxruntime import 실패: {e}")

for name in ("torchvision", "onnx", "numpy", "pyyaml", "torchmetrics", "pycocotools", "anomalib", "paddleocr", "safetensors"):
    try:
        from importlib.metadata import version
        result["packages"][name] = version(name if name != "pyyaml" else "PyYAML")
    except Exception:  # noqa
        pass

print(json.dumps(result, ensure_ascii=False))
