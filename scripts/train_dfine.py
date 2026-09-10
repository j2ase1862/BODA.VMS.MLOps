#!/usr/bin/env python3
"""
VMS D-FINE Detection 학습 스크립트 (Apache-2.0 백본).

D-FINE (Peterande/D-FINE, ICLR 2025) 를 HuggingFace transformers 구현으로 Fine-tuning 하고
VMS DetectionTool 이 읽는 ONNX(deploy 규약) 로 변환합니다.
Ultralytics(AGPL-3.0) 의존이 없어 상용 배포 시 소스 공개 의무가 발생하지 않습니다.

입력 데이터셋: 기존 YOLO 형식 그대로 (data.yaml + images/{train,val} + labels/{train,val}, 정규화 cx cy w h)
  - 세그멘테이션 폴리곤 라벨(5개 초과 값)은 외접 박스로 자동 변환

stdout 프로토콜 (TrainingService 가 파싱):
    [EPOCH] 5/100
    [LOSS] 0.0234
    [ACC] 0.87          (torchmetrics 설치 시 val mAP50)
    [PROGRESS] 45.5
    [ONNX] C:\\output\\best.onnx
    [DONE]
    [ERROR] message

사전 준비:
    pip install torch torchvision "transformers>=4.52" onnx onnxruntime pyyaml
    (선택) pip install torchmetrics pycocotools   ← val mAP 계산

사용 예:
    python train_dfine.py --dataset ./data --output ./output --epochs 60 --batch_size 8 --lr 0.00025 --export_onnx

ONNX deploy 규약 (공식 D-FINE export_onnx.py 와 동일):
    inputs : images[N,3,imgsz,imgsz] float32 RGB 0~1 (stretch 리사이즈, letterbox 없음)
             orig_target_sizes[N,2] int64 (h, w)
    outputs: labels[N,300] int64 · boxes[N,300,4] float32 xyxy 원본 픽셀 · scores[N,300] float32 (sigmoid)
    metadata: names={0: 'a', 1: 'b'} · imgsz · model_format=dfine
"""

import argparse
import json
import math
import os
import random
import shutil
import sys
import time

DEFAULT_PRETRAINED = "ustc-community/dfine-small-obj2coco"
TOPK = 300


def emit(tag, msg=""):
    print(f"[{tag}] {msg}".rstrip(), flush=True)


def fail(msg):
    emit("ERROR", msg)
    sys.exit(1)


# ─────────────────────────── 데이터셋 ───────────────────────────

def load_data_yaml(dataset_dir):
    import yaml
    path = os.path.join(dataset_dir, "data.yaml")
    if not os.path.exists(path):
        fail(f"data.yaml을 찾을 수 없습니다: {path}")
    with open(path, "r", encoding="utf-8") as f:
        cfg = yaml.safe_load(f) or {}

    names = cfg.get("names")
    if isinstance(names, dict):
        names = [names[k] for k in sorted(names, key=lambda x: int(x))]
    if not names:
        nc = int(cfg.get("nc", 0))
        names = [f"class{i}" for i in range(nc)]
    names = [str(n) for n in names]

    def resolve(entry):
        if not entry:
            return None
        p = entry if os.path.isabs(entry) else os.path.join(dataset_dir, entry)
        return p if os.path.isdir(p) else None

    train_dir = resolve(cfg.get("train"))
    val_dir = resolve(cfg.get("val"))
    if train_dir is None:
        fail(f"train 이미지 폴더를 찾을 수 없습니다: {cfg.get('train')}")
    if val_dir is None or not any(os.scandir(val_dir)):
        emit("WARN", "val 폴더가 비어 있어 train 을 검증에도 사용합니다")
        val_dir = train_dir
    return names, train_dir, val_dir


def labels_dir_for(img_dir):
    """Ultralytics 규약: 경로의 마지막 'images' 세그먼트를 'labels' 로 치환"""
    parts = os.path.normpath(img_dir).split(os.sep)
    for i in range(len(parts) - 1, -1, -1):
        if parts[i].lower() == "images":
            parts[i] = "labels"
            return os.sep.join(parts)
    return os.path.join(os.path.dirname(img_dir), "labels")


IMG_EXTS = (".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".webp")


def read_yolo_label(path, nc):
    """YOLO txt → (classes, boxes cxcywh 정규화). 폴리곤은 외접 박스로 변환."""
    classes, boxes = [], []
    if not os.path.exists(path):
        return classes, boxes
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            vals = line.strip().split()
            if len(vals) < 5:
                continue
            try:
                cls = int(float(vals[0]))
                nums = [float(v) for v in vals[1:]]
            except ValueError:
                continue
            if cls < 0 or cls >= nc:
                continue
            if len(nums) == 4:
                cx, cy, w, h = nums
            else:
                xs, ys = nums[0::2], nums[1::2]
                x1, x2, y1, y2 = min(xs), max(xs), min(ys), max(ys)
                cx, cy, w, h = (x1 + x2) / 2, (y1 + y2) / 2, x2 - x1, y2 - y1
            if w <= 0 or h <= 0:
                continue
            classes.append(cls)
            boxes.append([min(max(cx, 0.0), 1.0), min(max(cy, 0.0), 1.0),
                          min(max(w, 0.0), 1.0), min(max(h, 0.0), 1.0)])
    return classes, boxes


def build_dataset_class():
    import torch
    from torch.utils.data import Dataset
    from PIL import Image
    from torchvision import transforms as T

    class YoloDetDataset(Dataset):
        def __init__(self, img_dir, nc, imgsz, train, hsv_h, hsv_s, hsv_v, flip_p=0.5):
            self.img_dir = img_dir
            self.lbl_dir = labels_dir_for(img_dir)
            self.nc = nc
            self.imgsz = imgsz
            self.train = train
            self.flip_p = flip_p if train else 0.0
            self.files = sorted(f for f in os.listdir(img_dir) if f.lower().endswith(IMG_EXTS))
            if not self.files:
                fail(f"이미지가 없습니다: {img_dir}")
            self.jitter = None
            if train and (hsv_h > 0 or hsv_s > 0 or hsv_v > 0):
                self.jitter = T.ColorJitter(brightness=hsv_v, saturation=hsv_s, hue=min(hsv_h, 0.5))

        def __len__(self):
            return len(self.files)

        def __getitem__(self, idx):
            name = self.files[idx]
            img = Image.open(os.path.join(self.img_dir, name)).convert("RGB")
            stem = os.path.splitext(name)[0]
            classes, boxes = read_yolo_label(os.path.join(self.lbl_dir, stem + ".txt"), self.nc)

            if self.jitter is not None:
                img = self.jitter(img)
            if self.flip_p > 0 and random.random() < self.flip_p:
                img = img.transpose(Image.FLIP_LEFT_RIGHT)
                boxes = [[1.0 - b[0], b[1], b[2], b[3]] for b in boxes]

            img = img.resize((self.imgsz, self.imgsz), Image.BILINEAR)
            pixel = T.functional.to_tensor(img)  # RGB 0~1, 정규화 없음 (D-FINE 규약)

            target = {
                "class_labels": torch.tensor(classes, dtype=torch.int64),
                "boxes": torch.tensor(boxes, dtype=torch.float32).reshape(-1, 4),
            }
            return pixel, target

    def collate(batch):
        pixels = torch.stack([b[0] for b in batch])
        targets = [b[1] for b in batch]
        return pixels, targets

    return YoloDetDataset, collate


# ─────────────────────────── EMA ───────────────────────────

class ModelEma:
    def __init__(self, model, decay=0.9999):
        import copy
        self.module = copy.deepcopy(model).eval()
        for p in self.module.parameters():
            p.requires_grad_(False)
        self.decay = decay
        self.updates = 0

    def update(self, model):
        import torch
        self.updates += 1
        d = min(self.decay, (1 + self.updates) / (10 + self.updates))
        with torch.no_grad():
            msd = model.state_dict()
            for k, v in self.module.state_dict().items():
                if v.dtype.is_floating_point:
                    v.mul_(d).add_(msd[k].detach(), alpha=1 - d)
                else:
                    v.copy_(msd[k])


# ─────────────────────────── 검증 ───────────────────────────

def evaluate(model, loader, device, use_amp, names, metric_cls):
    import torch
    model.eval()
    total, n = 0.0, 0
    metric = metric_cls(iou_type="bbox") if metric_cls is not None else None
    with torch.no_grad():
        for pixels, targets in loader:
            pixels = pixels.to(device, non_blocking=True)
            tg = [{k: v.to(device) for k, v in t.items()} for t in targets]
            with torch.autocast(device_type=device.type, enabled=use_amp):
                out = model(pixel_values=pixels, labels=tg)
            if out.loss is not None:
                total += float(out.loss.item())
                n += 1
            if metric is not None:
                scores = out.logits.sigmoid()
                conf, lab = scores.max(-1)
                boxes = out.pred_boxes
                cx, cy, w, h = boxes.unbind(-1)
                xyxy = torch.stack([cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2], -1)
                preds, gts = [], []
                for b in range(pixels.shape[0]):
                    preds.append({"boxes": xyxy[b].float().cpu(), "scores": conf[b].float().cpu(),
                                  "labels": lab[b].cpu()})
                    gb = tg[b]["boxes"]
                    gcx, gcy, gw, gh = gb.unbind(-1) if gb.numel() else (gb[:, 0],) * 4
                    gxyxy = torch.stack([gcx - gw / 2, gcy - gh / 2, gcx + gw / 2, gcy + gh / 2], -1) \
                        if gb.numel() else gb.reshape(-1, 4)
                    gts.append({"boxes": gxyxy.float().cpu(), "labels": tg[b]["class_labels"].cpu()})
                metric.update(preds, gts)
    val_loss = total / max(n, 1)
    map50 = None
    if metric is not None:
        try:
            r = metric.compute()
            map50 = float(r["map_50"])
        except Exception as ex:  # noqa: BLE001
            emit("WARN", f"mAP 계산 실패: {ex}")
    model.train()
    return val_loss, map50


# ─────────────────────────── ONNX export ───────────────────────────

def _patch_double_trig(model):
    """float64 입력 Sin/Cos 노드를 Cast(float32) → Sin/Cos → Cast(float64) 로 감싼다. 패치한 노드 수 반환."""
    from onnx import TensorProto, helper, shape_inference

    try:
        inferred = shape_inference.infer_shapes(model)
    except Exception:  # noqa: BLE001
        inferred = model
    dtypes = {}
    for coll in (inferred.graph.value_info, inferred.graph.input, inferred.graph.output):
        for v in coll:
            dtypes[v.name] = v.type.tensor_type.elem_type
    for t in model.graph.initializer:
        dtypes[t.name] = t.data_type

    new_nodes = []
    patched = 0
    casted = {}  # 원본 double 텐서 → float32 Cast 출력 (Sin/Cos 가 같은 입력을 공유하므로 1회만 생성)
    for n in model.graph.node:
        if n.op_type in ("Sin", "Cos") and dtypes.get(n.input[0]) == TensorProto.DOUBLE:
            x, y = n.input[0], n.output[0]
            if x not in casted:
                casted[x] = x + "_f32"
                new_nodes.append(helper.make_node("Cast", [x], [casted[x]], to=TensorProto.FLOAT, name=x + "_castin"))
            yf = y + "_f32"
            n.input[0] = casted[x]
            n.output[0] = yf
            new_nodes.append(n)
            new_nodes.append(helper.make_node("Cast", [yf], [y], to=TensorProto.DOUBLE, name=y + "_castout"))
            patched += 1
        else:
            new_nodes.append(n)
    if patched:
        del model.graph.node[:]
        model.graph.node.extend(new_nodes)
    return patched


def write_history_artifacts(output, history, epochs, best_epoch):
    """에폭별 기록을 metrics.json(요약 숫자 + history) 과 curves.png(학습 곡선) 으로 남긴다.

    MLOps 워커가 output 폴더에서 이 두 파일을 찾아 아티팩트로 올린다(metrics·curve). 서버는 metrics.json 의
    최상위 숫자만 ModelVersion.Metrics 로 읽으므로 요약은 평평하게 두고, 에폭별 기록은 history 아래에 둔다.
    매 에폭마다 다시 쓴다 — 취소되거나 죽어도 그때까지의 곡선이 남는다. 그림은 matplotlib 이 없으면 건너뛴다.
    """
    if not history:
        return
    last = history[-1]
    best = next((h for h in history if h["epoch"] == best_epoch), last)
    summary = {
        "epochs": epochs,
        "epochs_done": last["epoch"],
        "best_epoch": best_epoch,
        "train_loss": last["train_loss"],
        "val_loss": last["val_loss"],
        "best_val_loss": best["val_loss"],
        "elapsed_sec": last["elapsed_sec"],
    }
    if last.get("map50") is not None:
        summary["map50"] = last["map50"]
        summary["best_map50"] = max(h["map50"] for h in history if h.get("map50") is not None)
    summary["history"] = history
    tmp = os.path.join(output, "metrics.json.tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(summary, f, ensure_ascii=False, indent=2)
    os.replace(tmp, os.path.join(output, "metrics.json"))

    try:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
    except Exception as ex:  # noqa: BLE001
        emit("WARN", f"matplotlib 없음 — curves.png 생략 ({ex})")
        return
    ep = [h["epoch"] for h in history]
    fig, ax = plt.subplots(figsize=(7, 4), dpi=110)
    ax.plot(ep, [h["train_loss"] for h in history], marker="o", ms=3, label="train loss")
    ax.plot(ep, [h["val_loss"] for h in history], marker="o", ms=3, label="val loss")
    ax.set_xlabel("epoch")
    ax.set_ylabel("loss")
    ax.grid(True, alpha=0.3)
    from matplotlib.ticker import MaxNLocator
    ax.xaxis.set_major_locator(MaxNLocator(integer=True))
    handles, labels = ax.get_legend_handles_labels()
    if any(h.get("map50") is not None for h in history):
        ax2 = ax.twinx()
        ax2.plot(ep, [h.get("map50") if h.get("map50") is not None else float("nan") for h in history],
                 color="tab:green", marker="s", ms=3, label="val mAP50")
        ax2.set_ylabel("mAP50")
        ax2.set_ylim(0, 1)
        h2, l2 = ax2.get_legend_handles_labels()
        handles += h2
        labels += l2
    ax.axvline(best_epoch, color="gray", ls="--", lw=0.8)
    ax.legend(handles, labels, loc="best", fontsize=8)
    ax.set_title(f"D-FINE training — best epoch {best_epoch}/{epochs}")
    fig.tight_layout()
    tmp_png = os.path.join(output, "curves.png.tmp")
    fig.savefig(tmp_png, format="png")
    plt.close(fig)
    os.replace(tmp_png, os.path.join(output, "curves.png"))


def export_onnx(model, names, imgsz, out_path, pretrained, device):
    import torch
    import torch.nn as nn

    nc = len(names)

    class DeployWrapper(nn.Module):
        """HF D-FINE → 공식 deploy 규약 (labels / boxes xyxy 원본 픽셀 / scores)"""

        def __init__(self, m, topk):
            super().__init__()
            self.m = m
            self.topk = topk

        def forward(self, images, orig_target_sizes):
            out = self.m(pixel_values=images)
            logits, boxes = out.logits, out.pred_boxes          # [B,Q,C], [B,Q,4] cxcywh 정규화
            num_classes = logits.shape[-1]
            scores = logits.sigmoid().flatten(1)                 # [B, Q*C]
            k = min(self.topk, scores.shape[1])
            top_scores, idx = scores.topk(k, dim=1)
            labels = idx % num_classes
            q_idx = idx // num_classes
            sel = boxes.gather(1, q_idx.unsqueeze(-1).expand(-1, -1, 4))
            cx, cy, w, h = sel.unbind(-1)
            xyxy = torch.stack([cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2], dim=-1)
            sz = orig_target_sizes.to(xyxy.dtype)                # [B,2] (h, w)
            scale = torch.stack([sz[:, 1], sz[:, 0], sz[:, 1], sz[:, 0]], dim=1).unsqueeze(1)
            return labels, xyxy * scale, top_scores

    model = model.to("cpu").eval().float()
    wrapper = DeployWrapper(model, TOPK).eval()
    dummy = torch.zeros(1, 3, imgsz, imgsz, dtype=torch.float32)
    sizes = torch.tensor([[imgsz, imgsz]], dtype=torch.int64)

    export_kwargs = dict(
        input_names=["images", "orig_target_sizes"],
        output_names=["labels", "boxes", "scores"],
        dynamic_axes={"images": {0: "N"}, "orig_target_sizes": {0: "N"},
                      "labels": {0: "N"}, "boxes": {0: "N"}, "scores": {0: "N"}},
        opset_version=17,
        do_constant_folding=True,
    )
    try:
        torch.onnx.export(wrapper, (dummy, sizes), out_path, dynamo=False, **export_kwargs)
    except TypeError:
        torch.onnx.export(wrapper, (dummy, sizes), out_path, **export_kwargs)

    import onnx
    m = onnx.load(out_path)

    # HF D-FINE 의 2D sin/cos 위치 임베딩이 float64 로 trace 되는데 ONNX Runtime CPU/CUDA 에는
    # Sin/Cos(double) 커널이 없다 (NOT_IMPLEMENTED Cos(7)). Sin/Cos 앞뒤에 float32↔double Cast 를 끼워 넣는다.
    patched = _patch_double_trig(m)
    if patched:
        emit("INFO", f"ONNX 후처리: float64 Sin/Cos {patched}개를 float32 로 감쌈")

    names_str = "{" + ", ".join(f"{i}: '{n}'" for i, n in enumerate(names)) + "}"
    for key, value in (
        ("names", names_str),
        ("imgsz", f"[{imgsz}, {imgsz}]"),
        ("model_format", "dfine"),
        ("input_layout", "images: RGB 0-1 stretch resize (no letterbox); orig_target_sizes: (h, w) int64"),
        ("output_layout", "labels[N,300] int64; boxes[N,300,4] xyxy pixels; scores[N,300] sigmoid"),
        ("backbone", "D-FINE (HGNetV2) via HuggingFace transformers"),
        ("pretrained", str(pretrained)),
        ("license", "Apache-2.0 (D-FINE / transformers)"),
        ("nc", str(nc)),
    ):
        p = m.metadata_props.add()
        p.key = key
        p.value = value
    onnx.save(m, out_path)

    # 검증 (onnxruntime 설치 시)
    try:
        import numpy as np
        import onnxruntime as ort
        sess = ort.InferenceSession(out_path, providers=["CPUExecutionProvider"])
        res = sess.run(None, {"images": np.zeros((1, 3, imgsz, imgsz), np.float32),
                              "orig_target_sizes": np.array([[480, 640]], np.int64)})
        emit("INFO", f"ONNX 검증 OK: labels{res[0].shape} boxes{res[1].shape} scores{res[2].shape}")
    except Exception as ex:  # noqa: BLE001
        emit("WARN", f"onnxruntime 검증 생략/실패: {ex}")


# ─────────────────────────── main ───────────────────────────

def main():
    parser = argparse.ArgumentParser(description="VMS D-FINE Training (Apache-2.0)")
    parser.add_argument("--dataset", required=True, help="YOLO 형식 데이터셋 폴더 (data.yaml 포함)")
    parser.add_argument("--output", required=True, help="출력 폴더")
    parser.add_argument("--pretrained", default="",
                        help=f"HF 모델 ID 또는 로컬 폴더 (기본 {DEFAULT_PRETRAINED}; nano/small/medium/large/xlarge)")
    parser.add_argument("--epochs", type=int, default=60)
    parser.add_argument("--lr", type=float, default=0.00025, help="기본 학습률 (D-FINE 권장 2.5e-4, 백본은 ×0.5)")
    parser.add_argument("--batch_size", type=int, default=8)
    parser.add_argument("--imgsz", type=int, default=640)
    parser.add_argument("--workers", type=int, default=0, help="DataLoader worker (Windows 기본 0)")
    parser.add_argument("--export_onnx", action="store_true")
    parser.add_argument("--export_only", action="store_true",
                        help="학습 없이 <output>/best_model 을 ONNX 로 다시 export 만 수행")
    parser.add_argument("--device", default="", help="cuda / cpu (기본 자동)")
    parser.add_argument("--weight_decay", type=float, default=1.25e-4)
    parser.add_argument("--warmup_epochs", type=float, default=1.0)
    parser.add_argument("--seed", type=int, default=0)

    # Augmentation — TrainingService 가 YOLO 와 같은 이름으로 전달 (mosaic/mixup 은 D-FINE 미적용)
    parser.add_argument("--mosaic", type=float, default=0.0)
    parser.add_argument("--mixup", type=float, default=0.0)
    parser.add_argument("--hsv_h", type=float, default=0.015)
    parser.add_argument("--hsv_s", type=float, default=0.7)
    parser.add_argument("--hsv_v", type=float, default=0.4)
    parser.add_argument("--flip", type=float, default=0.5, help="좌우 반전 확률")

    args = parser.parse_args()
    os.makedirs(args.output, exist_ok=True)

    try:
        import torch
        from torch.utils.data import DataLoader
        import transformers
        from transformers import DFineForObjectDetection
    except ImportError as ex:
        fail(f"필수 패키지 누락 ({ex}). pip install torch torchvision \"transformers>=4.52\" onnx pyyaml")

    # 사내 SSL 검사(프록시 CA) 환경에서 HF 다운로드가 CERTIFICATE_VERIFY_FAILED 로 실패하는 경우 대응:
    # truststore 가 설치돼 있으면 OS 인증서 저장소를 사용한다 (pip install truststore).
    # 그래도 안 되면 config.json / preprocessor_config.json / model.safetensors 를 curl 로 받아 --pretrained <폴더> 로 지정.
    try:
        import truststore  # type: ignore
        truststore.inject_into_ssl()
    except Exception:  # noqa: BLE001
        pass

    random.seed(args.seed)
    torch.manual_seed(args.seed)

    if args.export_only:
        names, _, _ = load_data_yaml(args.dataset)
        best_dir = os.path.join(args.output, "best_model")
        if not os.path.isdir(best_dir):
            fail(f"best_model 폴더가 없습니다: {best_dir}")
        try:
            best = DFineForObjectDetection.from_pretrained(best_dir)
            onnx_path = os.path.join(args.output, "best.onnx")
            export_onnx(best, names, args.imgsz, onnx_path, args.pretrained or best_dir, torch.device("cpu"))
            emit("ONNX", onnx_path)
        except Exception as ex:  # noqa: BLE001
            import traceback
            traceback.print_exc()
            fail(f"ONNX export 실패: {ex}")
        emit("PROGRESS", "100")
        emit("DONE")
        return

    if args.mosaic > 0 or args.mixup > 0:
        emit("WARN", "D-FINE 학습은 mosaic/mixup 을 사용하지 않습니다 (무시)")
    if args.lr > 5e-4:
        emit("WARN", f"lr={args.lr} 는 D-FINE(AdamW) 권장값 0.00025 보다 큽니다 — 발산 시 낮추세요")

    device = torch.device(args.device) if args.device else \
        torch.device("cuda" if torch.cuda.is_available() else "cpu")
    use_amp = device.type == "cuda"
    if device.type == "cpu":
        emit("WARN", "CUDA 를 찾지 못해 CPU 로 학습합니다 — 매우 느립니다")

    names, train_dir, val_dir = load_data_yaml(args.dataset)
    nc = len(names)
    if nc == 0:
        fail("data.yaml 의 names/nc 가 비어 있습니다")
    emit("INFO", f"classes={nc} {names} train={train_dir} val={val_dir} device={device} transformers={transformers.__version__}")

    YoloDetDataset, collate = build_dataset_class()
    train_ds = YoloDetDataset(train_dir, nc, args.imgsz, True, args.hsv_h, args.hsv_s, args.hsv_v, args.flip)
    val_ds = YoloDetDataset(val_dir, nc, args.imgsz, False, 0, 0, 0, 0)
    pin = device.type == "cuda"
    train_loader = DataLoader(train_ds, batch_size=args.batch_size, shuffle=True, num_workers=args.workers,
                              collate_fn=collate, pin_memory=pin, drop_last=len(train_ds) > args.batch_size)
    val_loader = DataLoader(val_ds, batch_size=args.batch_size, shuffle=False, num_workers=args.workers,
                            collate_fn=collate, pin_memory=pin)

    pretrained = args.pretrained.strip() or DEFAULT_PRETRAINED
    id2label = {i: n for i, n in enumerate(names)}
    try:
        model = DFineForObjectDetection.from_pretrained(
            pretrained, num_labels=nc, id2label=id2label, label2id={n: i for i, n in id2label.items()},
            ignore_mismatched_sizes=True)
    except Exception as ex:  # noqa: BLE001
        fail(f"사전학습 모델 로드 실패 ({pretrained}): {ex}. 인터넷 연결 또는 로컬 경로를 확인하세요")
    model.to(device).train()

    backbone_params = [p for n, p in model.named_parameters() if "backbone" in n and p.requires_grad]
    other_params = [p for n, p in model.named_parameters() if "backbone" not in n and p.requires_grad]
    optimizer = torch.optim.AdamW([
        {"params": backbone_params, "lr": args.lr * 0.5},
        {"params": other_params, "lr": args.lr},
    ], lr=args.lr, weight_decay=args.weight_decay)

    steps_per_epoch = max(1, len(train_loader))
    total_steps = steps_per_epoch * args.epochs
    warmup_steps = int(steps_per_epoch * args.warmup_epochs)

    def lr_lambda(step):
        if step < warmup_steps:
            return (step + 1) / max(1, warmup_steps)
        prog = (step - warmup_steps) / max(1, total_steps - warmup_steps)
        return 0.01 + 0.99 * 0.5 * (1 + math.cos(math.pi * min(1.0, prog)))

    scheduler = torch.optim.lr_scheduler.LambdaLR(optimizer, lr_lambda)
    scaler = torch.cuda.amp.GradScaler(enabled=use_amp)
    ema = ModelEma(model)

    metric_cls = None
    try:
        from torchmetrics.detection import MeanAveragePrecision
        metric_cls = MeanAveragePrecision
    except Exception:  # noqa: BLE001
        emit("INFO", "torchmetrics 미설치 — val loss 로 best 선택 (mAP 는 [ACC] 미출력)")

    best_dir = os.path.join(args.output, "best_model")
    best_score = None
    best_epoch = 0
    history = []
    step = 0
    emit("PROGRESS", "0")
    t0 = time.time()

    for epoch in range(1, args.epochs + 1):
        model.train()
        running, count = 0.0, 0
        for pixels, targets in train_loader:
            pixels = pixels.to(device, non_blocking=True)
            tg = [{k: v.to(device) for k, v in t.items()} for t in targets]
            with torch.autocast(device_type=device.type, enabled=use_amp):
                out = model(pixel_values=pixels, labels=tg)
                loss = out.loss
            if loss is None or not torch.isfinite(loss):
                emit("WARN", f"epoch {epoch}: 비정상 loss 배치 건너뜀")
                optimizer.zero_grad(set_to_none=True)
                continue
            optimizer.zero_grad(set_to_none=True)
            scaler.scale(loss).backward()
            scaler.unscale_(optimizer)
            torch.nn.utils.clip_grad_norm_(model.parameters(), 0.1)
            scaler.step(optimizer)
            scaler.update()
            scheduler.step()
            ema.update(model)
            step += 1
            running += float(loss.item())
            count += 1

        train_loss = running / max(count, 1)
        val_loss, map50 = evaluate(ema.module, val_loader, device, use_amp, names, metric_cls)

        emit("EPOCH", f"{epoch}/{args.epochs}")
        emit("LOSS", f"{train_loss:.4f}")
        if map50 is not None:
            emit("ACC", f"{map50:.4f}")
        emit("INFO", f"val_loss={val_loss:.4f}" + (f" mAP50={map50:.4f}" if map50 is not None else "")
             + f" elapsed={time.time() - t0:.0f}s")
        emit("PROGRESS", f"{epoch / args.epochs * 90:.1f}")

        score = map50 if map50 is not None else -val_loss
        if best_score is None or score > best_score:
            best_score = score
            best_epoch = epoch
            ema.module.save_pretrained(best_dir)
            with open(os.path.join(best_dir, "vms_train_info.json"), "w", encoding="utf-8") as f:
                json.dump({"epoch": epoch, "train_loss": train_loss, "val_loss": val_loss, "map50": map50,
                           "names": names, "imgsz": args.imgsz, "pretrained": pretrained}, f,
                          ensure_ascii=False, indent=2)

        history.append({"epoch": epoch, "train_loss": train_loss, "val_loss": val_loss, "map50": map50,
                        "lr": float(optimizer.param_groups[0]["lr"]), "elapsed_sec": round(time.time() - t0, 1)})
        try:
            write_history_artifacts(args.output, history, args.epochs, best_epoch)
        except Exception as ex:  # noqa: BLE001
            emit("WARN", f"학습 곡선 기록 실패 (계속 진행): {ex}")

    if args.export_onnx and os.path.isdir(best_dir):
        emit("PROGRESS", "95")
        try:
            best = DFineForObjectDetection.from_pretrained(best_dir)
            onnx_path = os.path.join(args.output, "best.onnx")
            export_onnx(best, names, args.imgsz, onnx_path, pretrained, device)
            emit("ONNX", onnx_path)
        except Exception as ex:  # noqa: BLE001
            import traceback
            traceback.print_exc()
            fail(f"ONNX export 실패: {ex}")

    emit("PROGRESS", "100")
    emit("DONE")


if __name__ == "__main__":
    main()
