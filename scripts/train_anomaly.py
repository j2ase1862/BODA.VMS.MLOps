#!/usr/bin/env python3
"""
VMS Anomaly Detection 학습 스크립트.
anomalib PatchCore 기반 이상 탐지 모델을 학습하고 ONNX로 변환합니다.

데이터셋 폴더 구조 (MVTec 스타일):
    dataset/
    ├── train/
    │   └── good/         (정상 이미지만)
    └── test/
        ├── good/         (정상 테스트)
        └── defect/       (불량 테스트)

stdout 프로토콜:
    [EPOCH] 1/1
    [PROGRESS] 50.0
    [ACC] 0.95
    [ONNX] C:\\output\\anomaly.onnx
    [DONE]
    [ERROR] message

사전 준비:
    pip install "anomalib[core]>=1.1,<2" "pandas<3" "matplotlib<3.10" python-dotenv scikit-learn torch torchvision onnx
      (anomalib 1.x 는 pandas 3 · matplotlib 3.10 과 맞지 않고, 모델 패키지 import 에 python-dotenv · scikit-learn 이 필요하다.
       NumPy 2 에서 imgaug 가 import 되도록 하는 처리는 스크립트가 한다)

    또는 anomalib 없이 간이 학습 (--backend simple, anomalib 이 없으면 자동):
    pip install torch torchvision opencv-python onnx

출력 ONNX 규약 (두 경로 같음 — VMS AnomalyTool 이 그대로 읽는다):
    입력 input[1,3,S,S] (VMS 가 리사이즈 · ImageNet 정규화한 RGB) →
    출력 anomaly_map[1,1,S,S] · pred_score[1]. 점수는 학습 때 정한 임계값이 0.5 가 되도록 정규화돼 있다.
    메타데이터 model_format=anomaly · method · imgsz · threshold · backbone.

사용 예:
    python train_anomaly.py --dataset ./data --output ./output
"""

import argparse
import os
import sys
import json


def write_metrics(output, summary, scores=None, threshold=None, title="Anomaly training"):
    """metrics.json(요약 숫자) 과 curves.png 를 남긴다.

    MLOps 워커가 output 폴더에서 이 두 파일을 찾아 아티팩트로 올린다(metrics·curve). 서버는 metrics.json 의
    최상위 숫자만 ModelVersion.Metrics 로 읽으므로 요약은 평평하게 둔다.

    <b>이 스크립트에는 에폭 루프가 없다</b> — anomalib 은 Engine 이 내부에서 돌리고, 간이 경로는 특징을
    한 번 훑어 뱅크를 만든다. 그래서 다른 스크립트 같은 학습 곡선이 성립하지 않는다. 대신 판정에 실제로
    쓰이는 것(정상 이미지의 이상 점수 분포와 그 위에 놓인 임계값)을 그린다 — 임계값이 분포의 오른쪽
    꼬리에 적절히 놓였는지가 이 모델에서 곡선보다 쓸모 있다. 점수가 없으면 그림은 건너뛴다.
    """
    tmp = os.path.join(output, "metrics.json.tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(summary, f, ensure_ascii=False, indent=2)
    os.replace(tmp, os.path.join(output, "metrics.json"))

    if scores is None or len(scores) == 0:
        return
    try:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
    except Exception as ex:  # noqa: BLE001
        print(f"[WARN] matplotlib 없음 — curves.png 생략 ({ex})", flush=True)
        return
    fig, ax = plt.subplots(figsize=(7, 4), dpi=110)
    ax.hist(scores, bins=min(40, max(5, len(scores) // 2)), color="tab:blue", alpha=0.75,
            label="normal image score")
    if threshold is not None:
        ax.axvline(threshold, color="tab:red", ls="--", lw=1.2, label=f"threshold {threshold:.4f}")
    ax.set_xlabel("anomaly score")
    ax.set_ylabel("images")
    ax.grid(True, alpha=0.3)
    ax.legend(loc="best", fontsize=8)
    ax.set_title(title)
    fig.tight_layout()
    tmp_png = os.path.join(output, "curves.png.tmp")
    fig.savefig(tmp_png, format="png")
    plt.close(fig)
    os.replace(tmp_png, os.path.join(output, "curves.png"))


def main():
    parser = argparse.ArgumentParser(description="VMS Anomaly Detection Training")
    parser.add_argument("--dataset", required=True, help="Dataset path (MVTec format)")
    parser.add_argument("--output", required=True, help="Output directory")
    parser.add_argument("--method", default="patchcore", choices=["patchcore", "fastflow", "efficient_ad"],
                        help="Anomaly detection method")
    parser.add_argument("--backbone", default="resnet18",
                        choices=["resnet18", "resnet50", "wide_resnet50_2"],
                        help="특징 추출 백본. 깊은 모델일수록 미세 결함 검출↑")
    parser.add_argument("--coreset_ratio", type=float, default=0.1,
                        help="PatchCore 코어셋 샘플링 비율 (0.01~1.0). 기본 0.1. 값↑=미검↓ + 메모리↑")
    parser.add_argument("--epochs", type=int, default=1, help="Epochs (PatchCore=1, others may vary)")
    parser.add_argument("--lr", type=float, default=0.001)
    parser.add_argument("--batch_size", type=int, default=32)
    parser.add_argument("--imgsz", type=int, default=224, help="Input image size")
    parser.add_argument("--export_onnx", action="store_true")
    parser.add_argument("--backend", default="auto", choices=["auto", "anomalib", "simple"],
                        help="auto: anomalib 이 있으면 anomalib, 없으면 간이 PatchCore · anomalib · simple: 강제")
    args = parser.parse_args()

    os.makedirs(args.output, exist_ok=True)

    # 사내 SSL 검사(프록시 CA) 환경에서 사전학습 가중치 다운로드(timm · Hugging Face · torchvision)가
    # CERTIFICATE_VERIFY_FAILED 로 실패하는 경우 대응: truststore 가 있으면 OS 인증서 저장소를 쓴다 (train_dfine.py 와 같음).
    try:
        import truststore  # type: ignore
        truststore.inject_into_ssl()
    except Exception:  # noqa: BLE001
        pass

    train_dir = os.path.join(args.dataset, "train", "good")
    if not os.path.exists(train_dir):
        print(f"[ERROR] train/good 폴더를 찾을 수 없습니다: {train_dir}", flush=True)
        sys.exit(1)

    # anomalib 사용 시도 (--backend simple 이면 건너뜀)
    if args.backend != "simple":
        try:
            train_with_anomalib(args)
            return
        except ImportError as e:
            if args.backend == "anomalib":
                print(f"[ERROR] anomalib 을 불러올 수 없습니다: {e}", flush=True)
                sys.exit(1)
            print(f"anomalib 을 불러올 수 없어 간이 PatchCore 로 전환합니다 ({e}).", flush=True)

    # 간이 PatchCore (scikit-learn + torchvision 특징 추출)
    try:
        train_simple_patchcore(args)
    except ImportError as e:
        print(f"[ERROR] 필요한 패키지가 설치되지 않았습니다: {e}", flush=True)
        sys.exit(1)


def train_with_anomalib(args):
    """anomalib 라이브러리를 사용한 학습"""
    _numpy2_compat_for_imgaug()
    from anomalib.data import Folder
    # anomalib 1.x 클래스 이름: Patchcore · Fastflow · EfficientAd (FastFlow 아님 — 틀린 이름이면 ImportError 로
    # anomalib 경로 전체가 간이 경로로 넘어가 버린다)
    from anomalib.models import Patchcore, Fastflow, EfficientAd
    from anomalib.engine import Engine
    _windows_safe_engine()
    from anomalib.deploy import ExportType

    print("[PROGRESS] 0", flush=True)
    print(f"[CONFIG] method={args.method} backbone={args.backbone} coreset={args.coreset_ratio}", flush=True)

    # 모델 선택 + 백본/coreset 파라미터 주입
    if args.method == "patchcore":
        # anomalib 최신 API: backbone / coreset_sampling_ratio
        try:
            model = Patchcore(backbone=args.backbone, coreset_sampling_ratio=args.coreset_ratio)
        except TypeError:
            # 구버전 호환 (파라미터명 차이)
            model = Patchcore()
    elif args.method == "fastflow":
        try:
            model = Fastflow(backbone=args.backbone)
        except TypeError:
            model = Fastflow()
    else:  # efficient_ad
        model = EfficientAd()

    # 데이터 모듈
    datamodule = Folder(
        name=os.path.basename(os.path.normpath(args.dataset)) or "dataset",
        root=args.dataset,
        normal_dir="train/good",
        abnormal_dir="test/defect" if os.path.exists(os.path.join(args.dataset, "test", "defect")) else None,
        normal_test_dir="test/good" if os.path.exists(os.path.join(args.dataset, "test", "good")) else None,
        task="classification",          # 불량 마스크 없이 이미지 단위 정상/불량 — 기본값(segmentation)은 mask_dir 를 요구
        image_size=(args.imgsz, args.imgsz),
        # VMS AnomalyTool 전처리와 같게 — OpenCV 선형 보간(앤티에일리어싱 없음) 정사각 리사이즈 + ImageNet 정규화.
        # 학습과 추론의 전처리가 다르면 정상 이미지도 점수가 뜬다.
        transform=_vms_like_transform(args.imgsz),
        train_batch_size=args.batch_size,
        eval_batch_size=args.batch_size,
        num_workers=0,                   # Windows 에서 DataLoader 다중 프로세스는 스크립트 재실행 문제를 일으킨다
    )

    print("[EPOCH] 1/1", flush=True)
    print("[PROGRESS] 10", flush=True)

    # 학습
    engine = Engine(
        max_epochs=args.epochs,
        default_root_dir=args.output,
    )

    engine.fit(model=model, datamodule=datamodule)

    print("[PROGRESS] 70", flush=True)

    # 테스트
    test_results = engine.test(model=model, datamodule=datamodule)
    if test_results:
        auroc = test_results[0].get("image_AUROC", 0)
        print(f"[ACC] {auroc:.4f}", flush=True)

    # 지표 남기기 — 에폭 루프가 없으므로 요약만. 숫자로 읽히는 항목만 골라 평평하게 둔다.
    try:
        summary = {"method": args.method, "backbone": args.backbone,
                   "epochs": args.epochs, "imgsz": args.imgsz}
        if test_results:
            for key, value in test_results[0].items():
                if isinstance(value, (int, float)):
                    summary[str(key)] = float(value)
        write_metrics(args.output, summary, title=f"Anomaly ({args.method}) — test metrics")
    except Exception as ex:  # noqa: BLE001
        print(f"[WARN] 지표 기록 실패 (계속 진행): {ex}", flush=True)

    print("[PROGRESS] 85", flush=True)

    # ONNX 변환 — anomalib 의 engine.export 는 전처리(크기 조정 · 정규화)를 모델 안에 넣는데 ① VMS 는 정규화한 텐서를
    # 넣으므로 정규화가 두 번 되고 ② anomalib 1.x 의 내보내기 변환이 torchvision 0.21 과 맞지 않아 NotImplementedError 가 난다.
    # 학습된 PatchCore 본체만 내보내고, 출력 규약은 간이 경로와 같다 — anomaly_map [1,1,S,S] · pred_score [1],
    # anomalib 이 시험(검증) 단계에서 정한 이미지 임계값이 0.5 가 되도록 나눈다 (AnomalyTool 기본 임계값 0.5).
    if args.export_onnx:
        import torch
        net = model.model.eval().cpu()
        thr = None
        try:
            thr = float(model.image_threshold.value.detach().cpu())
        except Exception:  # noqa: BLE001
            pass
        if not thr or thr <= 0 or thr != thr:
            print("[WARN] anomalib 임계값을 얻지 못해 점수를 정규화하지 않습니다 — VisionSetup 에서 임계값 보정(Calibrate)을 하세요.", flush=True)
            scale = 1.0
        else:
            scale = 0.5 / thr
            print(f"Threshold(raw)={thr:.4f} → 정규화 점수 임계값 0.5 (scale={scale:.6f})", flush=True)

        class AnomalibOnnx(torch.nn.Module):
            def __init__(self, core, k):
                super().__init__()
                self.core = core
                self.register_buffer("scale", torch.tensor(float(k)))

            def forward(self, x):
                out = self.core(x)
                return out["anomaly_map"] * self.scale, out["pred_score"].reshape(-1) * self.scale

        onnx_path = os.path.join(args.output, "anomaly.onnx")
        torch.onnx.export(AnomalibOnnx(net, scale).eval(), torch.zeros(1, 3, args.imgsz, args.imgsz), onnx_path,
                          input_names=["input"], output_names=["anomaly_map", "pred_score"], opset_version=14)
        try:
            import onnx
            m = onnx.load(onnx_path)
            for k, v in {"model_format": "anomaly", "method": args.method, "imgsz": str(args.imgsz),
                         "threshold": "0.5" if scale != 1.0 else "", "backbone": args.backbone}.items():
                p = m.metadata_props.add()
                p.key, p.value = k, v
            onnx.save(m, onnx_path)
        except Exception as ex:  # noqa: BLE001
            print(f"[WARN] ONNX 메타데이터 기록 실패 (모델은 정상): {ex}", flush=True)
        print(f"[ONNX] {onnx_path}", flush=True)

    print("[PROGRESS] 100", flush=True)
    print("[DONE]", flush=True)


def _vms_like_transform(imgsz):
    """anomalib 데이터 변환 — VMS AnomalyTool 의 cv2 선형 리사이즈(앤티에일리어싱 없음) + ImageNet 정규화와 같게."""
    from torchvision.transforms import v2
    return v2.Compose([
        v2.Resize((imgsz, imgsz), antialias=False),
        v2.Normalize(mean=[0.485, 0.456, 0.406], std=[0.229, 0.224, 0.225]),
    ])


def _windows_safe_engine():
    """anomalib 1.x Engine.fit 은 결과 폴더에 v0 · v1 … 버전 폴더를 만들고 latest 심볼릭 링크를 건다.
    Windows 는 관리자 권한 · 개발자 모드 없이는 링크를 못 만들어 WinError 1314 로 학습이 멈춘다 (학습 워커 서비스 계정 포함).
    버전 폴더 대신 <root>/latest 일반 폴더를 쓰게 한다 — 버전 폴더를 끈 경우 anomalib 이 원래 쓰는 경로와 같다."""
    import anomalib.engine.engine as anomalib_engine
    from pathlib import Path

    def plain_dir(root_dir):
        path = Path(root_dir).resolve() / "latest"
        path.mkdir(parents=True, exist_ok=True)
        return path

    anomalib_engine.create_versioned_dir = plain_dir

    # 시험 이미지마다 결과 그림을 <결과>/images/<데이터셋 절대 경로…> 로 저장하는 콜백은 끌 설정이 없다 —
    # 경로가 깊으면 Windows 경로 길이 제한(WinError 206)으로 학습이 멈추고, VMS 는 그 그림을 쓰지 않는다.
    from lightning.pytorch.callbacks import Callback
    anomalib_engine._VisualizationCallback = lambda *a, **k: Callback()


def _numpy2_compat_for_imgaug():
    """anomalib 1.x 는 데이터 모듈을 불러올 때 imgaug 를 import 하는데, imgaug 0.4 는 NumPy 2 에서 지워진
    np.sctypes 를 모듈 로드 시점에 읽는다 (AttributeError). PatchCore 학습 경로는 imgaug 증강을 쓰지 않으므로
    그 이름만 채워 import 를 통과시킨다. NumPy 1.x 에서는 아무것도 하지 않는다."""
    import numpy as np
    if "sctypes" in np.__dict__:
        return
    np.sctypes = {
        "int": [np.int8, np.int16, np.int32, np.int64],
        "uint": [np.uint8, np.uint16, np.uint32, np.uint64],
        "float": [np.float16, np.float32, np.float64],
        "complex": [np.complex64, np.complex128],
        "others": [bool, object, bytes, str, np.void],
    }


def _list_images(folder):
    exts = (".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff")
    if not os.path.isdir(folder):
        return []
    return sorted(os.path.join(folder, f) for f in os.listdir(folder) if f.lower().endswith(exts))


def _load_vms_tensor(path, imgsz):
    """VMS AnomalyTool 과 똑같은 전처리 — OpenCV 선형 보간 정사각 리사이즈 → BGR→RGB → /255 → ImageNet 정규화.
    학습과 추론의 전처리가 다르면 정상 이미지도 점수가 뜬다."""
    import cv2
    import numpy as np
    import torch
    img = cv2.imdecode(np.fromfile(path, dtype=np.uint8), cv2.IMREAD_COLOR)
    if img is None:
        raise ValueError(f"이미지를 읽을 수 없습니다: {path}")
    img = cv2.resize(img, (imgsz, imgsz), interpolation=cv2.INTER_LINEAR)
    img = cv2.cvtColor(img, cv2.COLOR_BGR2RGB).astype(np.float32) / 255.0
    img = (img - np.array([0.485, 0.456, 0.406], np.float32)) / np.array([0.229, 0.224, 0.225], np.float32)
    return torch.from_numpy(img.transpose(2, 0, 1).copy())


def train_simple_patchcore(args):
    """간이 PatchCore (anomalib 없이) — 패치 단위 특징 메모리 뱅크 + 최근접 거리.

    ImageNet 사전학습 백본의 layer2 · layer3 특징을 같은 격자로 맞춰 이어 붙이고(3×3 평균으로 주변 맥락),
    정상 이미지의 모든 패치 특징 중 대표(그리디 코어셋)만 메모리 뱅크로 둔다. 시험 패치의 이상도 = 뱅크까지의
    최근접 거리, 이미지 점수 = 이상 맵의 최댓값. 메모리 뱅크를 ONNX 안에 상수로 넣어 출력이 anomaly_map
    [1,1,H,W] · pred_score [1] 이 되게 내보내므로 VMS AnomalyTool 이 그대로 읽는다(별도 파일 없음).
    점수는 정상 검증 이미지로 정한 임계값이 0.5 가 되도록 나눠 둔다 — AnomalyTool 기본 임계값 0.5 와 맞춘다.
    """
    import numpy as np
    import torch
    import torch.nn.functional as F
    from torchvision import models

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"Device: {device}", flush=True)
    print("[PROGRESS] 0", flush=True)
    print(f"[CONFIG] backend=simple backbone={args.backbone} coreset={args.coreset_ratio} imgsz={args.imgsz}", flush=True)

    backbone_map = {
        "resnet18": models.resnet18,
        "resnet50": models.resnet50,
        "wide_resnet50_2": models.wide_resnet50_2,
    }
    backbone = backbone_map.get(args.backbone, models.resnet18)(weights="DEFAULT").eval().to(device)

    class PatchEmbedder(torch.nn.Module):
        """입력 [B,3,S,S] → 패치 특징 [B,C,S/8,S/8] (layer2 해상도)."""
        def __init__(self, net):
            super().__init__()
            self.stem = torch.nn.Sequential(net.conv1, net.bn1, net.relu, net.maxpool, net.layer1)
            self.layer2, self.layer3 = net.layer2, net.layer3

        def forward(self, x):
            f2 = self.layer2(self.stem(x))
            f3 = self.layer3(f2)
            f3 = F.interpolate(f3, size=f2.shape[-2:], mode="bilinear", align_corners=False)
            f = torch.cat([f2, f3], dim=1)
            return F.avg_pool2d(f, kernel_size=3, stride=1, padding=1)

    embedder = PatchEmbedder(backbone).eval().to(device)

    train_files = _list_images(os.path.join(args.dataset, "train", "good"))
    val_files = _list_images(os.path.join(args.dataset, "test", "good"))
    if not val_files:
        # 정상 검증 이미지가 따로 없으면 학습 이미지의 20 % 를 떼어 임계값 산출에 쓴다 (뱅크에 넣은 이미지로 재면 점수가 0 에 붙는다)
        n_val = max(1, len(train_files) // 5) if len(train_files) >= 5 else 0
        val_files, train_files = train_files[:n_val], train_files[n_val:]
    if not train_files:
        print("[ERROR] train/good 에 이미지가 없습니다.", flush=True)
        sys.exit(1)
    print(f"Train {len(train_files)} · Val(normal) {len(val_files)}", flush=True)

    def embed(files, start, span):
        out = []
        with torch.no_grad():
            for i in range(0, len(files), args.batch_size):
                batch = torch.stack([_load_vms_tensor(p, args.imgsz) for p in files[i:i + args.batch_size]]).to(device)
                f = embedder(batch)                                  # [B,C,h,w]
                out.append(f.permute(0, 2, 3, 1).reshape(-1, f.shape[1]))
                print(f"[PROGRESS] {start + span * min(1.0, (i + args.batch_size) / len(files)):.1f}", flush=True)
        return torch.cat(out, dim=0)

    print("[EPOCH] 1/1", flush=True)
    patches = embed(train_files, 0, 50)                              # [N·h·w, C]
    print(f"Patches: {patches.shape[0]} × {patches.shape[1]}", flush=True)

    # 그리디 코어셋 (k-center) — 정상 패치 분포를 고르게 덮는 대표만 남긴다
    ratio = max(0.01, min(1.0, float(args.coreset_ratio)))
    n_keep = max(1, int(patches.shape[0] * ratio))
    if n_keep < patches.shape[0]:
        torch.manual_seed(0)
        proj = torch.nn.functional.normalize(torch.randn(patches.shape[1], 128, device=device), dim=0)
        low = patches @ proj                                         # 거리 계산용 저차원 투영 (anomalib 과 같은 근사)
        selected = [int(torch.randint(0, low.shape[0], (1,)).item())]
        min_d = torch.cdist(low, low[selected[-1]].unsqueeze(0)).squeeze(1)
        for k in range(1, n_keep):
            idx = int(torch.argmax(min_d).item())
            selected.append(idx)
            min_d = torch.minimum(min_d, torch.cdist(low, low[idx].unsqueeze(0)).squeeze(1))
            if k % max(1, n_keep // 10) == 0:
                print(f"[PROGRESS] {50 + 20 * k / n_keep:.1f}", flush=True)
        bank = patches[selected]
    else:
        bank = patches
    print(f"Coreset: {patches.shape[0]} → {bank.shape[0]} (ratio={ratio:.2f})", flush=True)

    class PatchCoreOnnx(torch.nn.Module):
        """입력 [1,3,S,S] → anomaly_map [1,1,S,S] · pred_score [1] (임계값 = 0.5 로 정규화)."""
        def __init__(self, emb, memory, scale, imgsz):
            super().__init__()
            self.emb = emb
            self.register_buffer("bank", memory)
            self.register_buffer("bank_sq", (memory * memory).sum(dim=1))
            self.register_buffer("scale", torch.tensor(float(scale)))
            self.imgsz = imgsz
            sigma, ks = 4.0, 33                                      # anomalib 과 같은 가우시안 평활
            ax = torch.arange(ks, dtype=torch.float32) - ks // 2
            g = torch.exp(-(ax ** 2) / (2 * sigma ** 2))
            g = g / g.sum()
            self.register_buffer("gauss", (g[:, None] * g[None, :])[None, None])
            self.pad = ks // 2

        def forward(self, x):
            f = self.emb(x)                                          # [1,C,h,w]
            h, w = f.shape[-2], f.shape[-1]
            q = f.permute(0, 2, 3, 1).reshape(-1, f.shape[1])        # [h·w,C]
            d2 = (q * q).sum(dim=1, keepdim=True) - 2 * q @ self.bank.t() + self.bank_sq[None, :]
            d = torch.sqrt(torch.clamp(d2.min(dim=1).values, min=0.0))
            amap = d.reshape(1, 1, h, w)
            amap = F.interpolate(amap, size=(self.imgsz, self.imgsz), mode="bilinear", align_corners=False)
            amap = F.conv2d(amap, self.gauss, padding=self.pad) * self.scale
            score = amap.amax(dim=(1, 2, 3))                         # [1]
            return amap, score

    raw = PatchCoreOnnx(embedder, bank, 1.0, args.imgsz).eval().to(device)

    def scores(files):
        out = []
        with torch.no_grad():
            for p in files:
                _, s = raw(_load_vms_tensor(p, args.imgsz)[None].to(device))
                out.append(float(s.item()))
        return np.array(out)

    print("[PROGRESS] 75", flush=True)
    if len(val_files) > 0:
        val_scores = scores(val_files)
        raw_thr = float(val_scores.mean() + 3 * val_scores.std()) if len(val_scores) > 1 else float(val_scores.max() * 1.2)
    else:
        val_scores = scores(train_files)
        raw_thr = float(val_scores.max() * 1.5)                      # 검증 이미지가 없을 때의 보수적 근사
        print("[WARN] 정상 검증 이미지가 없어 학습 이미지 점수로 임계값을 근사합니다 — VisionSetup 에서 임계값 보정(Calibrate)을 권장합니다.", flush=True)
    raw_thr = max(raw_thr, 1e-6)
    scale = 0.5 / raw_thr
    print(f"Threshold(raw)={raw_thr:.4f} → 정규화 점수 임계값 0.5 (scale={scale:.6f})", flush=True)

    defect_files = _list_images(os.path.join(args.dataset, "test", "defect"))
    acc = 1.0
    summary = {"method": "simple_patchcore", "backbone": args.backbone, "imgsz": args.imgsz,
               "coreset_ratio": ratio, "bank_size": int(bank.shape[0]), "feature_dim": int(bank.shape[1]),
               "raw_threshold": raw_thr, "threshold": 0.5,
               "n_train": len(train_files), "n_val_normal": len(val_files)}
    norm_val = val_scores * scale
    summary.update({"val_score_mean": float(norm_val.mean()), "val_score_max": float(norm_val.max())})
    if defect_files:
        norm_def = scores(defect_files) * scale
        correct = int((norm_val < 0.5).sum() + (norm_def >= 0.5).sum())
        acc = correct / (len(norm_val) + len(norm_def))
        summary.update({"defect_score_min": float(norm_def.min()), "defect_score_mean": float(norm_def.mean()),
                        "n_test_defect": len(defect_files), "accuracy_at_0.5": acc})
        print(f"Normal max {norm_val.max():.3f} · Defect min {norm_def.min():.3f} · 정확도(0.5) {acc:.3f}", flush=True)
    print(f"[ACC] {acc:.4f}", flush=True)

    with open(os.path.join(args.output, "model_config.json"), "w", encoding="utf-8") as f:
        json.dump(summary, f, indent=2, ensure_ascii=False)
    try:
        write_metrics(args.output, summary, scores=norm_val, threshold=0.5,
                      title="Anomaly (simple PatchCore) — normal validation scores")
    except Exception as ex:  # noqa: BLE001
        print(f"[WARN] 지표 기록 실패 (계속 진행): {ex}", flush=True)

    if args.export_onnx:
        print("[PROGRESS] 90", flush=True)
        model = PatchCoreOnnx(embedder.cpu(), bank.cpu(), scale, args.imgsz).eval()
        onnx_path = os.path.join(args.output, "anomaly.onnx")
        torch.onnx.export(model, torch.zeros(1, 3, args.imgsz, args.imgsz), onnx_path,
                          input_names=["input"], output_names=["anomaly_map", "pred_score"], opset_version=13)
        try:
            import onnx
            m = onnx.load(onnx_path)
            for k, v in {"model_format": "anomaly", "method": "simple_patchcore", "imgsz": str(args.imgsz),
                         "threshold": "0.5", "backbone": args.backbone}.items():
                p = m.metadata_props.add()
                p.key, p.value = k, v
            onnx.save(m, onnx_path)
        except Exception as ex:  # noqa: BLE001
            print(f"[WARN] ONNX 메타데이터 기록 실패 (모델은 정상): {ex}", flush=True)
        print(f"[ONNX] {onnx_path}", flush=True)

    print("[PROGRESS] 100", flush=True)
    print("[DONE]", flush=True)


if __name__ == "__main__":
    main()
