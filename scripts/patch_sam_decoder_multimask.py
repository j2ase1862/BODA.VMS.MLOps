#!/usr/bin/env python3
"""
MobileSAM 디코더에서 '후보 마스크 4개'를 꺼내 쓸 수 있게 그래프에 출력을 더한다.

왜 필요한가
-----------
`export_mobile_sam.py` 는 공식 `SamOnnxModel(return_single_mask=True)` 로 내보낸다.
그러면 모델이 4개 후보 중 하나를 스스로 골라 그것만 돌려준다. 클릭 한 번의 뜻이 애매할 때
(예: 사선이 다른 객체 위를 지나갈 때) 모델의 선택이 사람이 원한 것과 다르면 손쓸 방법이 없다.

원래는 `return_single_mask=False` 로 다시 내보내는 것이 정석이지만, 그러려면 torch 와
MobileSAM 패키지가 필요하다. 그래프에서 고르기 직전 지점을 출력으로 노출하기만 하면
같은 값을 얻을 수 있고, 가중치를 건드리지 않으므로 결과도 동일하다.

무엇을 하는가
-------------
그래프에는 ArgMax 가 딱 하나 있고 그것이 `select_masks` 다. 그 앞의 두 텐서를 출력으로 더한다.

  all_low_res_masks : [1, 4, 256, 256]  후보 마스크 (저해상도, 후처리 전)
  all_iou_predictions : [1, 4]          후보별 IoU 예측

기존 출력(masks·iou_predictions·low_res_masks)은 그대로 둔다. 그래서 이 파일은
원래 디코더를 그대로 대신할 수 있고, 서버는 새 출력이 있으면 쓰고 없으면 예전처럼 돈다.

저해상도 마스크를 화면 좌표로 올리는 일(256 → 1024 이중선형 확대 후 잘라내기)은 서버가 한다.
공식 `mask_postprocessing` 과 같은 계산이고, `MaskUpscaler` 가 그 규약을 지킨다.

사용법
------
    python scripts/patch_sam_decoder_multimask.py \
        --input  src/BODA.VMS.MLOps.Server/models/sam/mobile_sam_decoder.onnx \
        --output src/BODA.VMS.MLOps.Server/models/sam/mobile_sam_decoder_multi.onnx
"""

import argparse
import os
import sys

MASKS_OUTPUT = "all_low_res_masks"
SCORES_OUTPUT = "all_iou_predictions"


def find_tap_points(graph):
    """고르기(ArgMax) 직전의 후보 마스크·점수 텐서 이름을 찾는다."""
    argmax = [n for n in graph.node if n.op_type == "ArgMax"]
    if len(argmax) != 1:
        raise SystemExit(
            f"[ERROR] ArgMax 노드가 {len(argmax)}개입니다. 1개여야 합니다 — "
            "return_single_mask=True 로 내보낸 디코더가 맞는지 확인하세요."
        )

    # 점수: ArgMax 의 입력은 Add(iou_head_output, 재가중치) 다. 그 Add 의 첫 입력이 원래 점수.
    add_name = argmax[0].input[0]
    add = next((n for n in graph.node if add_name in n.output), None)
    if add is None or add.op_type != "Add":
        raise SystemExit(f"[ERROR] ArgMax 앞의 Add 를 찾지 못했습니다 ({add_name}).")
    scores = add.input[0]

    # 마스크: 고른 마스크를 뽑기 위해 Flatten 을 거친다. 그 Flatten 의 입력이 후보 4개다.
    flattens = [n for n in graph.node if n.op_type == "Flatten"]
    masks = None
    for node in flattens:
        consumer = next((c for c in graph.node if node.output[0] in c.input and c.op_type == "Gather"), None)
        if consumer is None:
            continue
        # 점수 쪽 Flatten 이 아니라 마스크 쪽을 고른다 (입력이 4차원)
        if node.input[0] != scores:
            masks = node.input[0]
            break
    if masks is None:
        raise SystemExit("[ERROR] 후보 마스크 텐서를 찾지 못했습니다.")

    return masks, scores


def main():
    parser = argparse.ArgumentParser(description="MobileSAM 디코더에 후보 마스크 출력을 더한다")
    parser.add_argument("--input", required=True, help="원본 mobile_sam_decoder.onnx")
    parser.add_argument("--output", required=True, help="만들어 낼 파일 (.onnx, 외부 데이터는 .onnx.data)")
    args = parser.parse_args()

    try:
        import onnx
        from onnx import helper, TensorProto
    except ImportError:
        raise SystemExit("[ERROR] onnx 패키지가 필요합니다: pip install onnx")

    if not os.path.exists(args.input):
        raise SystemExit(f"[ERROR] 입력 파일이 없습니다: {args.input}")

    print(f"[INFO] 읽는 중: {args.input}")
    model = onnx.load(args.input)
    graph = model.graph

    existing = {o.name for o in graph.output}
    if MASKS_OUTPUT in existing:
        print("[INFO] 이미 후보 출력이 있습니다. 그대로 둡니다.")
        return

    masks, scores = find_tap_points(graph)
    print(f"[INFO] 후보 마스크 텐서: {masks}")
    print(f"[INFO] 후보 점수 텐서:   {scores}")

    # Identity 를 하나씩 끼워 새 이름으로 낸다. 내부 텐서를 그대로 출력으로 쓰면
    # 최적화 단계에서 이름이 사라질 수 있어, 이름을 고정하는 편이 안전하다.
    graph.node.append(helper.make_node("Identity", [masks], [MASKS_OUTPUT], name="tap_all_low_res_masks"))
    graph.node.append(helper.make_node("Identity", [scores], [SCORES_OUTPUT], name="tap_all_iou_predictions"))
    # 검사기가 형상을 요구한다. 배치와 후보 수는 기호로 둔다 (모델이 정한다).
    graph.output.append(helper.make_tensor_value_info(
        MASKS_OUTPUT, TensorProto.FLOAT, ["batch", "num_masks", "mask_h", "mask_w"]))
    graph.output.append(helper.make_tensor_value_info(
        SCORES_OUTPUT, TensorProto.FLOAT, ["batch", "num_masks"]))

    onnx.checker.check_model(model)

    directory = os.path.dirname(os.path.abspath(args.output))
    os.makedirs(directory, exist_ok=True)
    location = os.path.basename(args.output) + ".data"
    for stale in (args.output, os.path.join(directory, location)):
        if os.path.exists(stale):
            os.remove(stale)

    print(f"[INFO] 쓰는 중: {args.output} (+ {location})")
    onnx.save(
        model,
        args.output,
        save_as_external_data=True,
        all_tensors_to_one_file=True,
        location=location,
        size_threshold=1024,
    )

    size = os.path.getsize(args.output) / (1024 * 1024)
    data_size = os.path.getsize(os.path.join(directory, location)) / (1024 * 1024)
    print(f"[INFO] 완료: {size:.1f} MB + {data_size:.1f} MB")
    print("[INFO] appsettings 의 Sam:DecoderPath 를 이 파일로 바꾸면 후보 마스크를 씁니다.")

    verify(args.output)


def verify(path):
    """실제로 후보 4개가 나오는지 한 번 돌려 본다."""
    try:
        import numpy as np
        import onnxruntime as ort
    except ImportError:
        print("[WARN] onnxruntime 이 없어 실행 확인을 건너뜁니다.")
        return

    print("[INFO] 실행 확인 중...")
    session = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
    outputs = session.run(None, {
        "image_embeddings": np.random.randn(1, 256, 64, 64).astype(np.float32),
        "point_coords": np.array([[[512.0, 512.0], [0.0, 0.0]]], dtype=np.float32),
        "point_labels": np.array([[1.0, -1.0]], dtype=np.float32),
        "mask_input": np.zeros((1, 1, 256, 256), dtype=np.float32),
        "has_mask_input": np.array([0.0], dtype=np.float32),
        "orig_im_size": np.array([768.0, 1024.0], dtype=np.float32),
    })
    names = [o.name for o in session.get_outputs()]
    for name, value in zip(names, outputs):
        print(f"  {name}: {value.shape}")
    index = names.index(MASKS_OUTPUT)
    if outputs[index].shape[1] < 2:
        raise SystemExit("[ERROR] 후보가 2개 미만입니다. 뽑아낸 지점이 잘못됐습니다.")
    print(f"[INFO] 후보 {outputs[index].shape[1]}개 확인")


if __name__ == "__main__":
    sys.exit(main())
