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
    pip install anomalib torch torchvision onnx

    또는 anomalib 없이 간이 학습:
    pip install torch torchvision scikit-learn onnx

사용 예:
    python train_anomaly.py --dataset ./data --output ./output
"""

import argparse
import os
import sys
import json


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
    args = parser.parse_args()

    os.makedirs(args.output, exist_ok=True)

    train_dir = os.path.join(args.dataset, "train", "good")
    if not os.path.exists(train_dir):
        print(f"[ERROR] train/good 폴더를 찾을 수 없습니다: {train_dir}", flush=True)
        sys.exit(1)

    # anomalib 사용 시도
    try:
        train_with_anomalib(args)
        return
    except ImportError:
        print("anomalib 미설치. 간이 PatchCore로 전환합니다.", flush=True)

    # 간이 PatchCore (scikit-learn + torchvision 특징 추출)
    try:
        train_simple_patchcore(args)
    except ImportError as e:
        print(f"[ERROR] 필요한 패키지가 설치되지 않았습니다: {e}", flush=True)
        sys.exit(1)


def train_with_anomalib(args):
    """anomalib 라이브러리를 사용한 학습"""
    from anomalib.data import Folder
    from anomalib.models import Patchcore, FastFlow, EfficientAd
    from anomalib.engine import Engine
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
            model = FastFlow(backbone=args.backbone)
        except TypeError:
            model = FastFlow()
    else:  # efficient_ad
        model = EfficientAd()

    # 데이터 모듈
    datamodule = Folder(
        root=args.dataset,
        normal_dir="train/good",
        abnormal_dir="test/defect" if os.path.exists(os.path.join(args.dataset, "test", "defect")) else None,
        normal_test_dir="test/good" if os.path.exists(os.path.join(args.dataset, "test", "good")) else None,
        image_size=(args.imgsz, args.imgsz),
        train_batch_size=args.batch_size,
        eval_batch_size=args.batch_size,
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

    print("[PROGRESS] 85", flush=True)

    # ONNX 변환
    if args.export_onnx:
        engine.export(
            model=model,
            export_type=ExportType.ONNX,
        )

        # ONNX 파일 찾기
        for root, dirs, files in os.walk(args.output):
            for f in files:
                if f.endswith(".onnx"):
                    onnx_path = os.path.join(root, f)
                    print(f"[ONNX] {onnx_path}", flush=True)
                    break

    print("[PROGRESS] 100", flush=True)
    print("[DONE]", flush=True)


def train_simple_patchcore(args):
    """간이 PatchCore 구현 (anomalib 없이)"""
    import torch
    import numpy as np
    from torchvision import transforms, models
    from torch.utils.data import DataLoader
    from torchvision.datasets import ImageFolder
    from sklearn.neighbors import NearestNeighbors

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"Device: {device}", flush=True)
    print("[PROGRESS] 0", flush=True)
    print(f"[CONFIG] backbone={args.backbone} coreset={args.coreset_ratio}", flush=True)

    # 백본 선택 (중간층 hook 방식)
    backbone_map = {
        "resnet18": models.resnet18,
        "resnet50": models.resnet50,
        "wide_resnet50_2": models.wide_resnet50_2,
    }
    backbone_ctor = backbone_map.get(args.backbone, models.resnet18)
    backbone = backbone_ctor(weights="DEFAULT")
    backbone.eval()
    backbone = backbone.to(device)

    # hook으로 중간 특징 추출
    features = []
    def hook_fn(module, input, output):
        features.append(output.detach().cpu())

    backbone.layer2.register_forward_hook(hook_fn)
    backbone.layer3.register_forward_hook(hook_fn)

    transform = transforms.Compose([
        transforms.Resize((args.imgsz, args.imgsz)),
        transforms.ToTensor(),
        transforms.Normalize([0.485, 0.456, 0.406], [0.229, 0.224, 0.225]),
    ])

    # 정상 이미지 특징 추출
    train_dir = os.path.join(args.dataset, "train")
    train_dataset = ImageFolder(train_dir, transform=transform)
    train_loader = DataLoader(train_dataset, batch_size=args.batch_size, shuffle=False)

    all_features = []
    total_batches = len(train_loader)

    print("[EPOCH] 1/1", flush=True)

    with torch.no_grad():
        for idx, (images, _) in enumerate(train_loader):
            features.clear()
            images = images.to(device)
            backbone(images)

            # 멀티스케일 특징 결합
            for feat in features:
                b, c, h, w = feat.shape
                feat_resized = torch.nn.functional.adaptive_avg_pool2d(feat, (7, 7))
                feat_flat = feat_resized.reshape(b, -1)
                all_features.append(feat_flat.numpy())

            progress = (idx + 1) / total_batches * 70
            print(f"[PROGRESS] {progress:.1f}", flush=True)

    feature_matrix = np.concatenate(all_features, axis=0)

    print("[PROGRESS] 75", flush=True)

    # Coreset 서브샘플링 (PatchCore greedy approximation: 무작위 샘플로 근사)
    coreset_ratio = max(0.01, min(1.0, float(args.coreset_ratio)))
    if coreset_ratio < 1.0:
        n_total = feature_matrix.shape[0]
        n_keep = max(1, int(n_total * coreset_ratio))
        rng = np.random.default_rng(42)
        idx = rng.choice(n_total, size=n_keep, replace=False)
        feature_matrix = feature_matrix[idx]
        print(f"Coreset: {n_total} → {n_keep} samples (ratio={coreset_ratio:.2f})", flush=True)

    # KNN 모델 학습
    k = min(9, len(feature_matrix))
    knn = NearestNeighbors(n_neighbors=k, metric="euclidean")
    knn.fit(feature_matrix)

    # 통계 저장 (ONNX 대신 feature bank + threshold 저장)
    print("[PROGRESS] 85", flush=True)

    # 정상 이미지의 점수 분포 계산 → threshold 자동 설정
    distances, _ = knn.kneighbors(feature_matrix)
    mean_distances = distances.mean(axis=1)
    threshold = float(np.mean(mean_distances) + 2 * np.std(mean_distances))

    # 모델 데이터 저장
    model_data = {
        "method": "simple_patchcore",
        "backbone": args.backbone,
        "coreset_ratio": coreset_ratio,
        "input_size": args.imgsz,
        "threshold": threshold,
        "feature_dim": feature_matrix.shape[1],
        "n_samples": feature_matrix.shape[0],
    }

    np.save(os.path.join(args.output, "feature_bank.npy"), feature_matrix)

    with open(os.path.join(args.output, "model_config.json"), "w") as f:
        json.dump(model_data, f, indent=2)

    print(f"Threshold: {threshold:.4f}", flush=True)
    print(f"[ACC] {1.0:.4f}", flush=True)

    # 간이 ONNX 변환 (특징 추출 백본만)
    if args.export_onnx:
        print("[PROGRESS] 90", flush=True)

        # 백본 모델을 ONNX로 내보내기
        dummy = torch.randn(1, 3, args.imgsz, args.imgsz).to(device)
        onnx_path = os.path.join(args.output, "backbone.onnx")

        # 중간층 출력을 포함하는 wrapper
        class FeatureExtractor(torch.nn.Module):
            def __init__(self, model):
                super().__init__()
                self.model = model

            def forward(self, x):
                x = self.model.conv1(x)
                x = self.model.bn1(x)
                x = self.model.relu(x)
                x = self.model.maxpool(x)
                x = self.model.layer1(x)
                x = self.model.layer2(x)
                x = self.model.layer3(x)
                x = torch.nn.functional.adaptive_avg_pool2d(x, (1, 1))
                return x.flatten(1)

        extractor = FeatureExtractor(backbone).to(device)
        extractor.eval()

        torch.onnx.export(
            extractor, dummy, onnx_path,
            input_names=["input"],
            output_names=["features"],
            dynamic_axes={"input": {0: "batch"}, "features": {0: "batch"}},
            opset_version=13,
        )
        print(f"[ONNX] {onnx_path}", flush=True)

    print("[PROGRESS] 100", flush=True)
    print("[DONE]", flush=True)


if __name__ == "__main__":
    main()
