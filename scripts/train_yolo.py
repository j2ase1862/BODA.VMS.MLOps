#!/usr/bin/env python3
"""
VMS YOLO Detection 학습 스크립트.
Ultralytics YOLOv8 기반 객체 검출 모델을 Fine-tuning하고 ONNX로 변환합니다.

stdout 프로토콜:
    [EPOCH] 5/100
    [LOSS] 0.0234
    [PROGRESS] 45.5
    [ONNX] C:\\output\\best.onnx
    [DONE]
    [ERROR] message

사전 준비:
    pip install ultralytics onnx

사용 예:
    python train_yolo.py --dataset ./data --output ./output --epochs 100 \
        --mosaic 1.0 --mixup 0.1 --hsv_h 0.015 --hsv_s 0.7 --hsv_v 0.4
"""

import argparse
import os
import sys
import shutil


def main():
    parser = argparse.ArgumentParser(description="VMS YOLO Training")
    parser.add_argument("--dataset", required=True, help="Dataset path (YOLO format with data.yaml)")
    parser.add_argument("--output", required=True, help="Output directory")
    parser.add_argument("--pretrained", default="yolov8n.pt", help="Pretrained model")
    parser.add_argument("--epochs", type=int, default=100)
    parser.add_argument("--lr", type=float, default=0.01)
    parser.add_argument("--batch_size", type=int, default=16)
    parser.add_argument("--imgsz", type=int, default=640, help="Input image size")
    parser.add_argument("--export_onnx", action="store_true", help="Export to ONNX after training")

    # ── Augmentation (Ultralytics 파라미터 패스스루) ──
    parser.add_argument("--mosaic", type=float, default=1.0,
                        help="Mosaic augmentation probability (0.0~1.0). 작은 객체 검출 향상")
    parser.add_argument("--mixup", type=float, default=0.0,
                        help="Mixup augmentation probability (0.0~1.0)")
    parser.add_argument("--hsv_h", type=float, default=0.015,
                        help="HSV Hue augmentation range (0.0~1.0)")
    parser.add_argument("--hsv_s", type=float, default=0.7,
                        help="HSV Saturation augmentation range (0.0~1.0)")
    parser.add_argument("--hsv_v", type=float, default=0.4,
                        help="HSV Value/Brightness augmentation range (0.0~1.0) — 공장 조명 변동 대응에 중요")

    args = parser.parse_args()

    os.makedirs(args.output, exist_ok=True)

    try:
        from ultralytics import YOLO
    except ImportError:
        print("[ERROR] ultralytics가 설치되지 않았습니다. pip install ultralytics", flush=True)
        sys.exit(1)

    # data.yaml 경로 확인
    data_yaml = os.path.join(args.dataset, "data.yaml")
    if not os.path.exists(data_yaml):
        print(f"[ERROR] data.yaml을 찾을 수 없습니다: {data_yaml}", flush=True)
        sys.exit(1)

    print(f"[PROGRESS] 0", flush=True)
    print(f"[AUG] mosaic={args.mosaic} mixup={args.mixup} hsv_h={args.hsv_h} hsv_s={args.hsv_s} hsv_v={args.hsv_v}",
          flush=True)

    # 모델 로드
    model = YOLO(args.pretrained)

    # 학습 콜백 등록
    def on_train_epoch_end(trainer):
        epoch = trainer.epoch + 1
        total = trainer.epochs
        loss = trainer.loss.item() if hasattr(trainer.loss, 'item') else float(trainer.loss)
        print(f"[EPOCH] {epoch}/{total}", flush=True)
        print(f"[LOSS] {loss:.4f}", flush=True)
        print(f"[PROGRESS] {epoch / total * 100:.1f}", flush=True)

    model.add_callback("on_train_epoch_end", on_train_epoch_end)

    # 학습 시작
    results = model.train(
        data=data_yaml,
        epochs=args.epochs,
        imgsz=args.imgsz,
        batch=args.batch_size,
        lr0=args.lr,
        project=args.output,
        name="train",
        exist_ok=True,
        verbose=False,
        # ── Augmentation ──
        mosaic=args.mosaic,
        mixup=args.mixup,
        hsv_h=args.hsv_h,
        hsv_s=args.hsv_s,
        hsv_v=args.hsv_v,
    )

    # 최적 모델 복사
    best_pt = os.path.join(args.output, "train", "weights", "best.pt")

    # ONNX 변환
    if args.export_onnx and os.path.exists(best_pt):
        print("[PROGRESS] 95", flush=True)
        best_model = YOLO(best_pt)
        onnx_path = best_model.export(format="onnx", imgsz=args.imgsz, simplify=True)

        if onnx_path and os.path.exists(onnx_path):
            # 출력 폴더로 복사
            dest_onnx = os.path.join(args.output, "best.onnx")
            shutil.copy2(onnx_path, dest_onnx)
            print(f"[ONNX] {dest_onnx}", flush=True)

    print("[PROGRESS] 100", flush=True)
    print("[DONE]", flush=True)


if __name__ == "__main__":
    main()
