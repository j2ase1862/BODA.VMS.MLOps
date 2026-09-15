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
import csv
import json
import os
import sys
import shutil


def read_results_csv(run_dir):
    """ultralytics 가 에폭마다 갱신하는 results.csv 를 history 로 읽는다.

    열 이름은 판마다 다르다(`metrics/mAP50(B)`, `val/box_loss`, 공백 섞임 …). 그래서 이름을 정규화한 뒤
    접두사로 찾는다. 없는 값은 None 으로 두고 그리지 않는다 — 0 으로 채우면 손실이 0 인 것처럼 보인다.
    """
    path = os.path.join(run_dir, "results.csv")
    if not os.path.exists(path):
        return []
    try:
        with open(path, newline="", encoding="utf-8") as f:
            rows = list(csv.DictReader(f))
    except Exception:  # noqa: BLE001
        return []

    def pick(row, *names):
        for key, value in row.items():
            if key is None:
                continue
            k = key.strip().lower()
            if any(k == n or k.startswith(n) for n in names):
                try:
                    return float(value)
                except (TypeError, ValueError):
                    return None
        return None

    def total_loss(row, prefix):
        parts = []
        for key, value in row.items():
            if key is None:
                continue
            k = key.strip().lower()
            if k.startswith(prefix) and k.endswith("_loss"):
                try:
                    parts.append(float(value))
                except (TypeError, ValueError):
                    pass
        return round(sum(parts), 6) if parts else None

    history = []
    for i, row in enumerate(rows, start=1):
        epoch = pick(row, "epoch")
        history.append({
            "epoch": int(epoch) if epoch is not None else i,
            "train_loss": total_loss(row, "train/"),
            "val_loss": total_loss(row, "val/"),
            "map50": pick(row, "metrics/map50(", "metrics/map50"),
            "map50_95": pick(row, "metrics/map50-95("),
            "lr": pick(row, "lr/pg0"),
            "elapsed_sec": pick(row, "time"),
        })
    return history


def write_history_artifacts(output, history, epochs):
    """에폭별 기록을 metrics.json(요약 숫자 + history) 과 curves.png(학습 곡선) 으로 남긴다.

    MLOps 워커가 output 폴더에서 이 두 파일을 찾아 아티팩트로 올린다(metrics·curve). 서버는 metrics.json 의
    최상위 숫자만 ModelVersion.Metrics 로 읽으므로 요약은 평평하게 두고, 에폭별 기록은 history 아래에 둔다.
    train_dfine.py 와 같은 규약 (스크립트는 워커가 파일 하나씩 받아 돌리므로 공용 모듈을 쓸 수 없다).

    best_epoch 은 표시용이다 — 실제 best.pt 선택은 ultralytics 의 fitness 가 한다.
    """
    if not history:
        return
    last = history[-1]
    scored = [h for h in history if h.get("map50") is not None]
    best = max(scored, key=lambda h: h["map50"]) if scored else last
    summary = {
        "epochs": epochs,
        "epochs_done": last["epoch"],
        "best_epoch": best["epoch"],
    }
    for key in ("train_loss", "val_loss", "map50", "map50_95", "elapsed_sec"):
        if last.get(key) is not None:
            summary[key] = last[key]
    if scored:
        summary["best_map50"] = best["map50"]
    summary["history"] = history
    tmp = os.path.join(output, "metrics.json.tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(summary, f, ensure_ascii=False, indent=2)
    os.replace(tmp, os.path.join(output, "metrics.json"))

    try:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
        from matplotlib.ticker import MaxNLocator
    except Exception as ex:  # noqa: BLE001
        print(f"[WARN] matplotlib 없음 — curves.png 생략 ({ex})", flush=True)
        return
    ep = [h["epoch"] for h in history]
    fig, ax = plt.subplots(figsize=(7, 4), dpi=110)
    for key, label in (("train_loss", "train loss"), ("val_loss", "val loss")):
        if any(h.get(key) is not None for h in history):
            ax.plot(ep, [h[key] if h.get(key) is not None else float("nan") for h in history],
                    marker="o", ms=3, label=label)
    ax.set_xlabel("epoch")
    ax.set_ylabel("loss")
    ax.grid(True, alpha=0.3)
    ax.xaxis.set_major_locator(MaxNLocator(integer=True))
    handles, labels = ax.get_legend_handles_labels()
    if scored:
        ax2 = ax.twinx()
        ax2.plot(ep, [h.get("map50") if h.get("map50") is not None else float("nan") for h in history],
                 color="tab:green", marker="s", ms=3, label="val mAP50")
        ax2.set_ylabel("mAP50")
        ax2.set_ylim(0, 1)
        h2, l2 = ax2.get_legend_handles_labels()
        handles += h2
        labels += l2
        ax.axvline(best["epoch"], color="gray", ls="--", lw=0.8)
    if handles:
        ax.legend(handles, labels, loc="best", fontsize=8)
    title = "YOLO training"
    if scored:
        title += f" — best epoch {best['epoch']}/{epochs}"
    ax.set_title(title)
    fig.tight_layout()
    tmp_png = os.path.join(output, "curves.png.tmp")
    fig.savefig(tmp_png, format="png")
    plt.close(fig)
    os.replace(tmp_png, os.path.join(output, "curves.png"))


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

    run_dir = os.path.join(args.output, "train")

    # 학습 콜백 등록
    def on_train_epoch_end(trainer):
        epoch = trainer.epoch + 1
        total = trainer.epochs
        loss = trainer.loss.item() if hasattr(trainer.loss, 'item') else float(trainer.loss)
        print(f"[EPOCH] {epoch}/{total}", flush=True)
        print(f"[LOSS] {loss:.4f}", flush=True)
        print(f"[PROGRESS] {epoch / total * 100:.1f}", flush=True)

    def on_fit_epoch_end(trainer):
        # 검증까지 끝난 뒤라 results.csv 에 그 에폭 줄이 들어 있다. 매번 다시 써 두면
        # 취소되거나 죽어도 그때까지의 곡선이 남는다.
        try:
            write_history_artifacts(args.output, read_results_csv(run_dir), args.epochs)
        except Exception as ex:  # noqa: BLE001
            print(f"[WARN] 학습 곡선 기록 실패 (계속 진행): {ex}", flush=True)

    model.add_callback("on_train_epoch_end", on_train_epoch_end)
    model.add_callback("on_fit_epoch_end", on_fit_epoch_end)

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

    # 학습이 끝난 뒤 마지막 한 번 — 콜백이 안 붙는 판이어도 여기서는 남는다
    try:
        write_history_artifacts(args.output, read_results_csv(run_dir), args.epochs)
    except Exception as ex:  # noqa: BLE001
        print(f"[WARN] 학습 곡선 기록 실패 (계속 진행): {ex}", flush=True)

    # 최적 모델 복사
    best_pt = os.path.join(run_dir, "weights", "best.pt")

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
