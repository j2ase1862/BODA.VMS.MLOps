#!/usr/bin/env python3
"""
VMS Image Classification 학습 스크립트.
torchvision ResNet/MobileNet 기반 이미지 분류 모델을 Fine-tuning하고 ONNX로 변환합니다.

데이터셋 폴더 구조 (ImageFolder):
    dataset/
    ├── train/
    │   ├── classA/
    │   │   ├── img001.jpg
    │   │   └── ...
    │   └── classB/
    └── val/
        ├── classA/
        └── classB/

stdout 프로토콜:
    [EPOCH] 5/100
    [LOSS] 0.0234
    [ACC] 0.9512
    [PROGRESS] 45.5
    [ONNX] C:\\output\\classifier.onnx
    [DONE]
    [ERROR] message

사전 준비:
    pip install torch torchvision onnx

사용 예:
    python train_classifier.py --dataset ./data --output ./output --epochs 50
"""

import argparse
import os
import sys


def main():
    parser = argparse.ArgumentParser(description="VMS Classification Training")
    parser.add_argument("--dataset", required=True, help="Dataset path (ImageFolder format)")
    parser.add_argument("--output", required=True, help="Output directory")
    parser.add_argument("--pretrained", default="resnet18", help="Model architecture")
    parser.add_argument("--epochs", type=int, default=50)
    parser.add_argument("--lr", type=float, default=0.001)
    parser.add_argument("--batch_size", type=int, default=32)
    parser.add_argument("--imgsz", type=int, default=224, help="Input image size")
    parser.add_argument("--export_onnx", action="store_true")
    args = parser.parse_args()

    os.makedirs(args.output, exist_ok=True)

    try:
        import torch
        import torch.nn as nn
        import torch.optim as optim
        from torchvision import datasets, transforms, models
        from torch.utils.data import DataLoader
    except ImportError:
        print("[ERROR] PyTorch가 설치되지 않았습니다. pip install torch torchvision", flush=True)
        sys.exit(1)

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"Device: {device}", flush=True)

    # 데이터 로더
    train_dir = os.path.join(args.dataset, "train")
    val_dir = os.path.join(args.dataset, "val")

    if not os.path.exists(train_dir):
        print(f"[ERROR] train 폴더를 찾을 수 없습니다: {train_dir}", flush=True)
        sys.exit(1)

    transform_train = transforms.Compose([
        transforms.Resize((args.imgsz, args.imgsz)),
        transforms.RandomHorizontalFlip(),
        transforms.RandomRotation(10),
        transforms.ColorJitter(brightness=0.2, contrast=0.2),
        transforms.ToTensor(),
        transforms.Normalize([0.485, 0.456, 0.406], [0.229, 0.224, 0.225]),
    ])

    transform_val = transforms.Compose([
        transforms.Resize((args.imgsz, args.imgsz)),
        transforms.ToTensor(),
        transforms.Normalize([0.485, 0.456, 0.406], [0.229, 0.224, 0.225]),
    ])

    train_dataset = datasets.ImageFolder(train_dir, transform=transform_train)
    train_loader = DataLoader(train_dataset, batch_size=args.batch_size, shuffle=True, num_workers=2)

    val_loader = None
    if os.path.exists(val_dir):
        try:
            val_dataset = datasets.ImageFolder(val_dir, transform=transform_val)
            if len(val_dataset) > 0:
                val_loader = DataLoader(val_dataset, batch_size=args.batch_size, shuffle=False, num_workers=2)
            else:
                print("Warning: val 데이터가 비어있어 train 데이터로 검증합니다.", flush=True)
        except FileNotFoundError:
            print("Warning: val 폴더에 유효한 이미지가 없어 train 데이터로 검증합니다.", flush=True)

    num_classes = len(train_dataset.classes)
    class_names = train_dataset.classes
    print(f"Classes ({num_classes}): {class_names}", flush=True)

    # 클래스 목록 저장
    with open(os.path.join(args.output, "classes.txt"), "w") as f:
        f.write("\n".join(class_names))

    # 모델 생성
    model_fn = getattr(models, args.pretrained, None)
    if model_fn is None:
        print(f"[ERROR] 지원하지 않는 모델: {args.pretrained}", flush=True)
        sys.exit(1)

    model = model_fn(weights="DEFAULT")

    # 마지막 FC 레이어 교체
    if hasattr(model, "fc"):
        in_features = model.fc.in_features
        model.fc = nn.Linear(in_features, num_classes)
    elif hasattr(model, "classifier"):
        if isinstance(model.classifier, nn.Sequential):
            in_features = model.classifier[-1].in_features
            model.classifier[-1] = nn.Linear(in_features, num_classes)
        else:
            in_features = model.classifier.in_features
            model.classifier = nn.Linear(in_features, num_classes)

    model = model.to(device)

    criterion = nn.CrossEntropyLoss()
    optimizer = optim.Adam(model.parameters(), lr=args.lr)
    scheduler = optim.lr_scheduler.StepLR(optimizer, step_size=max(1, args.epochs // 3), gamma=0.1)

    print(f"[PROGRESS] 0", flush=True)

    best_acc = 0.0
    best_path = os.path.join(args.output, "best.pth")

    for epoch in range(args.epochs):
        # Train
        model.train()
        running_loss = 0.0
        correct = 0
        total = 0

        for images, labels in train_loader:
            images, labels = images.to(device), labels.to(device)
            optimizer.zero_grad()
            outputs = model(images)
            loss = criterion(outputs, labels)
            loss.backward()
            optimizer.step()

            running_loss += loss.item()
            _, predicted = outputs.max(1)
            total += labels.size(0)
            correct += predicted.eq(labels).sum().item()

        scheduler.step()

        train_loss = running_loss / len(train_loader)
        train_acc = correct / total

        # Validation
        val_acc = train_acc
        if val_loader:
            model.eval()
            val_correct = 0
            val_total = 0
            with torch.no_grad():
                for images, labels in val_loader:
                    images, labels = images.to(device), labels.to(device)
                    outputs = model(images)
                    _, predicted = outputs.max(1)
                    val_total += labels.size(0)
                    val_correct += predicted.eq(labels).sum().item()
            val_acc = val_correct / val_total

        print(f"[EPOCH] {epoch + 1}/{args.epochs}", flush=True)
        print(f"[LOSS] {train_loss:.4f}", flush=True)
        print(f"[ACC] {val_acc:.4f}", flush=True)
        print(f"[PROGRESS] {(epoch + 1) / args.epochs * 100:.1f}", flush=True)

        # Best 모델 저장
        if val_acc > best_acc:
            best_acc = val_acc
            torch.save(model.state_dict(), best_path)

    # ONNX 변환
    if args.export_onnx and os.path.exists(best_path):
        print("[PROGRESS] 95", flush=True)
        model.load_state_dict(torch.load(best_path, map_location=device))
        model.eval()

        dummy_input = torch.randn(1, 3, args.imgsz, args.imgsz).to(device)
        onnx_path = os.path.join(args.output, "classifier.onnx")

        torch.onnx.export(
            model, dummy_input, onnx_path,
            input_names=["input"],
            output_names=["output"],
            dynamic_axes={"input": {0: "batch"}, "output": {0: "batch"}},
            opset_version=13,
        )

        # ONNX 모델에 클래스명 메타데이터 삽입 (Ultralytics 호환 형식)
        import onnx
        onnx_model = onnx.load(onnx_path)
        names_str = "{" + ", ".join(f"{i}: '{n}'" for i, n in enumerate(class_names)) + "}"
        meta = onnx_model.metadata_props.add()
        meta.key = "names"
        meta.value = names_str
        onnx.save(onnx_model, onnx_path)

        print(f"[ONNX] {onnx_path}", flush=True)

    print("[PROGRESS] 100", flush=True)
    print("[DONE]", flush=True)


if __name__ == "__main__":
    main()
