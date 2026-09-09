#!/usr/bin/env python3
"""
VMS RF-DETR Segmentation 학습 스크립트 (Apache-2.0 백본).

RF-DETR (roboflow/rf-detr) 의 세그멘테이션 모델을 Fine-tuning 하고
VMS SegmentationTool 이 읽는 ONNX(rfdetrseg 규약) 로 변환합니다.
Ultralytics(AGPL-3.0) 의존이 없어 상용 배포 시 소스 공개 의무가 발생하지 않습니다
— 검출에서 D-FINE 을 고른 것과 같은 이유입니다.

입력 데이터셋: MLOps 서버의 coco 내보내기 그대로
    images/{train,val[,test]}/*          + annotations/instances_{train,val[,test]}.json

RF-DETR 은 Roboflow 배치(<dir>/{train,valid,test}/ 안에 이미지와 _annotations.coco.json)를
기대하므로, 이 스크립트가 학습 전에 그 모양으로 다시 깔아 줍니다. 이미지는 하드링크로 잇고
(같은 볼륨이면 자리를 더 쓰지 않습니다), 안 되면 복사합니다.

stdout 프로토콜 (TrainingService·워커가 파싱):
    [EPOCH] 5/100
    [LOSS] 0.0234
    [ACC] 0.87          (val mAP50 — 콜백이 주는 경우)
    [PROGRESS] 45.5
    [ONNX] C:\\output\\best.onnx
    [DONE]
    [ERROR] message

사전 준비:
    pip install rfdetr onnx onnxruntime

사용 예:
    python train_rfdetr_seg.py --dataset ./data --output ./output --epochs 40 --batch_size 4 --imgsz 560 --export_onnx

ONNX 규약 (rfdetrseg — roboflow/rf-detr 의 export 가 정하는 이름 그대로):
    inputs : input[N,3,H,W] float32 RGB, ImageNet 정규화 (mean .485 .456 .406 / std .229 .224 .225)
             H·W 는 모델 블록 크기(patch_size × num_windows)의 배수여야 한다
    outputs: dets[N,Q,4] float32 정규화 cxcywh
             labels[N,Q,C] float32 클래스 로짓 — 시그모이드(소프트맥스 아님).
                 C = 클래스 수 + 1 이고 마지막 열이 배경이다 (rf-detr lwdetr.py 의
                 "background slot (index detection_num_classes-1)"). 그 열은 빼고 봐야 한다.
             masks[N,Q,mh,mw] float32 마스크 로짓 — 압축 해상도. 이중선형으로 키운 뒤 0 에서 자른다
    metadata: names={0: 'a', 1: 'b'} · imgsz · model_format=rfdetrseg · background_class_id
              (RF-DETR 의 export 는 metadata_props 를 하나도 쓰지 않는다. 여기서 새기지 않으면
               레지스트리가 클래스 이름을 못 읽고, 라인은 배경 열을 못 빼낸다.)
"""

import argparse
import json
import os
import shutil
import sys
import time

DEFAULT_PRETRAINED = "medium"
LAYOUT_DIR = "_rfdetr"


def emit(tag, msg=""):
    print(f"[{tag}] {msg}".rstrip(), flush=True)


def fail(msg):
    emit("ERROR", msg)
    sys.exit(1)


# ─────────────────────────── 데이터셋 배치 ───────────────────────────

# MLOps coco 내보내기의 split 이름 → RF-DETR 이 찾는 폴더 이름
SPLIT_DIRS = {"train": "train", "val": "valid", "test": "test"}


def link_or_copy(src, dst):
    """
    같은 볼륨이면 하드링크로 잇는다 — 수십 GB 데이터셋을 복사하면 자리도 시간도 두 배가 된다.
    링크가 안 되는 경우(다른 볼륨·권한·파일시스템)에는 복사한다.
    """
    if os.path.exists(dst):
        return
    try:
        os.link(src, dst)
    except OSError:
        shutil.copy2(src, dst)


def build_layout(dataset_dir, work_dir):
    """
    coco 내보내기를 RF-DETR 이 기대하는 모양으로 다시 깐다. 준비한 split 이름들을 돌려준다.

    원본을 건드리지 않는다 — 데이터셋 폴더는 워커의 캐시라, 여기서 고치면 다음 작업이
    이미 바뀐 것을 받는다.
    """
    ann_dir = os.path.join(dataset_dir, "annotations")
    if not os.path.isdir(ann_dir):
        fail(f"coco 내보내기가 아닙니다 (annotations 폴더 없음): {dataset_dir}")

    prepared = []
    for split, out_name in SPLIT_DIRS.items():
        ann_path = os.path.join(ann_dir, f"instances_{split}.json")
        img_dir = os.path.join(dataset_dir, "images", split)
        if not os.path.isfile(ann_path) or not os.path.isdir(img_dir):
            continue

        with open(ann_path, encoding="utf-8") as f:
            coco = json.load(f)
        if not coco.get("images"):
            continue

        dst_dir = os.path.join(work_dir, out_name)
        os.makedirs(dst_dir, exist_ok=True)

        missing = 0
        for image in coco["images"]:
            src = os.path.join(img_dir, image["file_name"])
            if not os.path.isfile(src):
                missing += 1
                continue
            link_or_copy(src, os.path.join(dst_dir, os.path.basename(image["file_name"])))
            image["file_name"] = os.path.basename(image["file_name"])
        if missing:
            emit("WARN", f"{split}: 이미지 {missing}장이 없어 건너뜁니다")

        with open(os.path.join(dst_dir, "_annotations.coco.json"), "w", encoding="utf-8") as f:
            json.dump(coco, f, ensure_ascii=False)
        prepared.append(out_name)
        emit("INFO", f"{split} → {out_name}: 이미지 {len(coco['images'])}장, 라벨 {len(coco.get('annotations', []))}개")

    if "train" not in prepared:
        fail("학습용 split(train)이 없습니다. 라벨이 붙은 이미지가 있어야 합니다.")
    if "valid" not in prepared:
        # RF-DETR 은 valid 를 요구한다. 없으면 train 을 그대로 쓴다 —
        # 검증 점수가 낙관적으로 나온다는 뜻이므로 반드시 알린다.
        emit("WARN", "검증 split 이 없어 train 을 검증에도 씁니다. mAP 를 실제 성능으로 읽지 마세요.")
        shutil.copytree(os.path.join(work_dir, "train"), os.path.join(work_dir, "valid"), dirs_exist_ok=True)
        prepared.append("valid")
    return prepared


def class_names(rfdetr, work_dir):
    """
    학습이 실제로 쓰는 순서 그대로의 클래스 이름.

    라이브러리에게 물어본다. RF-DETR 은 카테고리를 한 번 거르고(주석이 하나도 없는 상위 분류를 버린다)
    0 부터 다시 번호를 매기는데, 그 규칙을 여기서 흉내 내면 언젠가 어긋난다. 어긋나면 라벨이 통째로
    한 칸씩 밀린 채 학습이 끝나고, 아무 오류도 나지 않는다.
    """
    try:
        names = rfdetr.RFDETR._load_classes(work_dir)
        if names:
            return [str(n) for n in names]
    except Exception as ex:  # noqa: BLE001
        emit("WARN", f"라이브러리에서 클래스 순서를 못 받았습니다 ({ex}). coco categories 순서를 씁니다.")

    with open(os.path.join(work_dir, "train", "_annotations.coco.json"), encoding="utf-8") as f:
        categories = json.load(f).get("categories", [])
    if not categories:
        fail("coco annotations 에 categories 가 없습니다.")
    return [str(c["name"]) for c in sorted(categories, key=lambda c: c["id"])]


# ─────────────────────────── 해상도 ───────────────────────────

def snap_resolution(model, requested):
    """
    RF-DETR 은 입력 변을 블록 크기의 배수로만 받는다. 라이브러리가 쓰는 정의 그대로
    <c>patch_size × num_windows</c> 로 구한다 (detr.py: "block_size ... Equals patch_size * num_windows").
    맞지 않으면 학습·export 가 예외로 죽으므로, 가장 가까운 배수로 내려 맞추고 알린다.

    모델마다 다르다 — nano 는 12 × 1 = 12, preview 는 14 × 4 = 56 이다.
    그래서 값을 고정하지 않고 모델에게 물어본다.
    """
    config = getattr(model, "model_config", None)
    patch = getattr(config, "patch_size", None)
    windows = getattr(config, "num_windows", None)
    block = (patch or 0) * (windows or 0)
    if block <= 0:
        # 못 물어보면 알려진 공배수로 맞춘다 (12 와 56 의 최소공배수는 168).
        block = 168
        emit("WARN", f"모델에서 블록 크기를 못 읽어 {block} 로 맞춥니다.")

    snapped = max(block, (requested // block) * block)
    if snapped != requested:
        emit("WARN", f"입력 크기 {requested} 는 {block} 의 배수가 아니라 {snapped} 로 맞춥니다.")
    return snapped


# ─────────────────────────── 진행 보고 ───────────────────────────

def attach_progress(rfdetr, total_epochs):
    """
    에폭마다 진행을 stdout 프로토콜로 흘려보낸다.

    RF-DETR 의 model.callbacks 는 이 판에서 아무도 부르지 않는 잔재다 (defaultdict 라 아무 키나
    조용히 받아 준다 — 붙였다고 착각하기 딱 좋다). 학습은 PyTorch Lightning 이 돌리는데
    train() 이 사용자 kwargs 를 Trainer 로 넘기지 않아 콜백을 넣을 자리가 없다.
    그래서 rfdetr.training.build_trainer 를 한 겹 감싸 우리 콜백을 끼운다
    (train() 이 호출 시점에 그 이름을 가져오므로 여기를 바꾸면 걸린다).
    <para>
    라이브러리 내부를 건드리는 일이라 실패할 수 있다. 실패하면 진행 표시만 없고 학습은 그대로 돈다 —
    조용히 넘어가지 않도록 반드시 알린다. 몇 시간짜리 학습에서 진행이 안 보이면 멈춘 것과 구별되지 않는다.
    """
    try:
        import pytorch_lightning as pl
        import rfdetr.training as training_module
        original = training_module.build_trainer
    except Exception as ex:  # noqa: BLE001
        emit("WARN", f"진행 표시를 붙이지 못했습니다 ({ex}). 학습은 그대로 진행됩니다.")
        return

    class Reporter(pl.Callback):
        """에폭이 끝날 때마다 한 줄씩. 없는 값은 내보내지 않는다 — 0 으로 채우면 손실이 0 인 것처럼 보인다."""

        def __init__(self, total):
            self.total = max(1, total)

        def on_train_epoch_end(self, trainer, module):
            done = int(trainer.current_epoch) + 1
            emit("EPOCH", f"{done}/{self.total}")
            emit("PROGRESS", f"{min(100.0, done * 100.0 / self.total):.1f}")

            metrics = {k: v for k, v in trainer.callback_metrics.items()}
            loss = _first(metrics, ("train_loss", "loss", "train/loss"))
            if loss is not None:
                emit("LOSS", f"{loss:.4f}")

        def on_validation_epoch_end(self, trainer, module):
            metrics = {k: v for k, v in trainer.callback_metrics.items()}
            # 세그멘테이션이므로 마스크 mAP 를 먼저 본다. 없으면 박스 mAP.
            score = _first(metrics, ("val_map_segm", "map_segm", "val_map", "map", "val_map_50"))
            if score is not None:
                emit("ACC", f"{score:.4f}")

    def _first(metrics, keys):
        for key in keys:
            if key in metrics:
                try:
                    return float(metrics[key])
                except (TypeError, ValueError):
                    return None
        return None

    def with_reporter(*args, **kwargs):
        callbacks = list(kwargs.pop("callbacks", None) or [])
        callbacks.append(Reporter(total_epochs))
        try:
            return original(*args, callbacks=callbacks, **kwargs)
        except TypeError:
            # callbacks 를 안 받는 판이면 진행 표시 없이 그대로 간다. 학습을 막을 이유는 없다.
            emit("WARN", "진행 표시를 붙이지 못했습니다 (build_trainer 가 callbacks 를 받지 않습니다).")
            return original(*args, **kwargs)

    training_module.build_trainer = with_reporter


# ─────────────────────────── ONNX ───────────────────────────

def stamp_metadata(onnx_path, names, imgsz, pretrained, background_class_id):
    """
    레지스트리가 규약과 클래스를 읽는 곳이다. RF-DETR 의 export 는 metadata_props 를 쓰지 않으므로
    여기서 새긴다. 이게 없으면 업로드한 모델이 unknown 으로 등록되고 클래스 이름도 사라진다.
    """
    import onnx

    model = onnx.load(onnx_path)
    names_str = "{" + ", ".join(f"{i}: '{n}'" for i, n in enumerate(names)) + "}"
    for key, value in (
        ("names", names_str),
        ("imgsz", f"[{imgsz}, {imgsz}]"),
        ("model_format", "rfdetrseg"),
        # 머리는 클래스 수 + 1 칸이고 마지막이 배경이다. 라인이 이 칸을 빼지 않으면
        # 배경이 최고 점수인 질의가 물체로 나온다.
        ("background_class_id", str(background_class_id)),
        ("input_layout", "input: RGB, ImageNet normalized (mean .485 .456 .406 / std .229 .224 .225)"),
        ("output_layout",
         "dets[N,Q,4] normalized cxcywh; labels[N,Q,C] logits (sigmoid); "
         "masks[N,Q,mh,mw] mask logits (bilinear upsample, threshold at 0). "
         "labels has len(names)+1 columns; the last one is background"),
        ("backbone", "RF-DETR segmentation (roboflow/rf-detr)"),
        ("pretrained", str(pretrained)),
        ("license", "Apache-2.0 (RF-DETR)"),
        ("nc", str(len(names))),
    ):
        p = model.metadata_props.add()
        p.key = key
        p.value = value
    onnx.save(model, onnx_path)


def verify_onnx(onnx_path, imgsz, names):
    """
    내보낸 파일이 규약대로 생겼는지 그 자리에서 본다. 여기서 걸러야 라인에 나가서 터지지 않는다.
    onnxruntime 이 없으면 건너뛴다 — 확인을 못 했다는 사실을 알린다.
    """
    try:
        import numpy as np
        import onnxruntime as ort
    except ImportError:
        emit("WARN", "onnxruntime 이 없어 ONNX 검증을 건너뜁니다.")
        return

    try:
        sess = ort.InferenceSession(onnx_path, providers=["CPUExecutionProvider"])
        outputs = {o.name: o for o in sess.get_outputs()}
        for required in ("dets", "labels", "masks"):
            if required not in outputs:
                fail(f"ONNX 출력에 '{required}' 가 없습니다. 세그멘테이션 규약이 아닙니다: {list(outputs)}")

        name = sess.get_inputs()[0].name
        res = sess.run(None, {name: np.zeros((1, 3, imgsz, imgsz), np.float32)})
        shapes = {o: tuple(r.shape) for o, r in zip([o.name for o in sess.get_outputs()], res)}
        if len(shapes["masks"]) != 4:
            fail(f"masks 는 4차원이어야 합니다: {shapes['masks']}")

        # 열이 하나 더 있어야 한다 (마지막이 배경). 이게 어긋나면 라인이 클래스를 잘못 읽는다.
        expected = len(names) + 1
        if shapes["labels"][-1] != expected:
            fail(f"labels 열이 {shapes['labels'][-1]} 개입니다. 클래스 {len(names)}개 + 배경 1개 = {expected} 이어야 합니다.")
        emit("INFO", f"ONNX 검증 OK: dets{shapes['dets']} labels{shapes['labels']} masks{shapes['masks']}")
    except SystemExit:
        raise
    except Exception as ex:  # noqa: BLE001
        fail(f"ONNX 검증 실패: {ex}")


# ─────────────────────────── main ───────────────────────────

def build_model(rfdetr, pretrained):
    """
    판마다 클래스 이름이 달라 이름 목록에서 찾는다. 세그멘테이션 모델만 고른다 —
    검출 모델을 잘못 집으면 masks 없이 학습이 끝나고, 그건 export 까지 가서야 드러난다.

    클래스 수는 넘기지 않는다. 학습이 데이터셋에서 스스로 세는데(_detect_num_classes_for_training),
    그 값과 다른 수를 밀어 넣으면 라벨 공간이 어긋난 채 학습이 돈다.
    """
    wanted = str(pretrained).strip().lower()
    candidates = [n for n in dir(rfdetr) if n.startswith("RFDETRSeg")]
    if not candidates:
        fail("설치된 rfdetr 에 세그멘테이션 모델이 없습니다. pip install -U rfdetr")

    match = next((n for n in candidates if n.lower() == f"rfdetrseg{wanted}"), None)
    if match is None:
        if os.path.exists(pretrained):
            match = candidates[0]   # 체크포인트를 직접 준 경우
        else:
            fail(f"'{pretrained}' 에 맞는 세그멘테이션 모델이 없습니다. 쓸 수 있는 것: {sorted(candidates)}")

    emit("INFO", f"모델 {match}")
    factory = getattr(rfdetr, match)
    if os.path.exists(pretrained):
        return factory(pretrain_weights=pretrained)
    return factory()


def main():
    parser = argparse.ArgumentParser(description="VMS RF-DETR Segmentation Training (Apache-2.0)")
    parser.add_argument("--dataset", required=True, help="coco 내보내기 폴더 (annotations/ + images/)")
    parser.add_argument("--output", required=True, help="출력 폴더")
    parser.add_argument("--pretrained", default=DEFAULT_PRETRAINED,
                        help=f"preview/small/medium/large 또는 체크포인트 경로 (기본 {DEFAULT_PRETRAINED})")
    parser.add_argument("--epochs", type=int, default=40)
    parser.add_argument("--lr", type=float, default=1e-4)
    parser.add_argument("--batch_size", type=int, default=4)
    parser.add_argument("--imgsz", type=int, default=560)
    parser.add_argument("--seed", type=int, default=0)
    parser.add_argument("--device", default="", help="cuda / cpu (기본 자동)")
    parser.add_argument("--export_onnx", action="store_true")

    # TrainingService 가 검출과 같은 이름으로 넘긴다. RF-DETR 은 자체 증강을 쓰므로 받기만 한다.
    parser.add_argument("--mosaic", type=float, default=0.0)
    parser.add_argument("--mixup", type=float, default=0.0)
    parser.add_argument("--hsv_h", type=float, default=0.015)
    parser.add_argument("--hsv_s", type=float, default=0.7)
    parser.add_argument("--hsv_v", type=float, default=0.4)

    args = parser.parse_args()
    os.makedirs(args.output, exist_ok=True)

    try:
        import rfdetr
        import torch
    except ImportError as ex:
        fail(f"필수 패키지 누락 ({ex}). pip install rfdetr onnx onnxruntime")

    # 사내 SSL 검사(프록시 CA) 환경에서 사전학습 가중치 내려받기가 실패하는 경우 대응
    try:
        import truststore  # type: ignore
        truststore.inject_into_ssl()
    except Exception:  # noqa: BLE001
        pass

    torch.manual_seed(args.seed)
    device = args.device or ("cuda" if torch.cuda.is_available() else "cpu")
    if device == "cpu":
        emit("WARN", "GPU 가 없어 CPU 로 학습합니다. 세그멘테이션은 매우 느립니다.")

    work_dir = os.path.join(args.output, LAYOUT_DIR)
    if os.path.isdir(work_dir):
        shutil.rmtree(work_dir, ignore_errors=True)
    os.makedirs(work_dir, exist_ok=True)

    build_layout(args.dataset, work_dir)
    names = class_names(rfdetr, work_dir)
    emit("INFO", f"클래스 {len(names)}개: {', '.join(names)}")

    model = build_model(rfdetr, args.pretrained)
    imgsz = snap_resolution(model, args.imgsz)

    attach_progress(rfdetr, args.epochs)

    emit("INFO", f"학습 시작 — epochs={args.epochs} batch={args.batch_size} lr={args.lr} imgsz={imgsz} device={device}")
    started = time.time()
    try:
        model.train(
            dataset_dir=work_dir,
            epochs=args.epochs,
            batch_size=args.batch_size,
            lr=args.lr,
            resolution=imgsz,
            output_dir=args.output,
            device=device,
        )
    except Exception as ex:  # noqa: BLE001
        fail(f"학습 실패: {ex}")

    emit("PROGRESS", "100.0")
    emit("INFO", f"학습 완료 ({time.time() - started:.0f}초)")

    if args.export_onnx:
        try:
            model.export(output_dir=args.output, shape=(imgsz, imgsz))
        except Exception as ex:  # noqa: BLE001
            fail(f"ONNX export 실패: {ex}")

        exported = os.path.join(args.output, "inference_model.onnx")
        if not os.path.isfile(exported):
            found = [f for f in os.listdir(args.output) if f.endswith(".onnx")]
            if not found:
                fail(f"export 후 ONNX 파일을 찾지 못했습니다: {args.output}")
            exported = os.path.join(args.output, found[0])

        final = os.path.join(args.output, "best.onnx")
        if os.path.abspath(exported) != os.path.abspath(final):
            shutil.move(exported, final)

        stamp_metadata(final, names, imgsz, args.pretrained, background_class_id=len(names))
        verify_onnx(final, imgsz, names)
        # 워커·WPF 가 이 줄을 그대로 파일 경로로 쓴다. 상대 경로로 내보내면
        # 작업 폴더가 다른 쪽에서 파일을 못 찾는다.
        emit("ONNX", os.path.abspath(final))

    shutil.rmtree(work_dir, ignore_errors=True)
    emit("DONE")


if __name__ == "__main__":
    main()
