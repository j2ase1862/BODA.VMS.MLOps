#!/usr/bin/env python3
"""학습 스크립트의 곡선·지표 기록 함수를 합성 데이터로 점검한다 (GPU·torch 없이).

`scripts/train_*.py` 는 워커가 **파일 하나씩** 내려받아 돌리므로 공용 모듈을 쓸 수 없고,
`write_history_artifacts` 를 스크립트마다 복사해 둔다. 그래서 한쪽만 고치면 다른 쪽이 조용히 뒤처진다.
게다가 이 코드가 실제로 도는 곳은 GPU 워커라, 그림 그리는 줄에 오타가 있으면 **몇 시간짜리 학습이
끝날 무렵에야** 드러난다.

이 스크립트는 그 함수들만 떼어 합성 history 로 실행해 본다. 정상 경로뿐 아니라 값이 빠지는 경우
(검증 세트 없음 · mAP 안 잡힘 · results.csv 없음)도 함께 돌린다 — 실전에서 실제로 생기는 모양이다.

    python tools/check_train_curves.py

사전 준비: pip install matplotlib numpy   (torch·ultralytics·anomalib 는 필요 없다)
"""
import importlib.util
import json
import os
import shutil
import sys
import tempfile

SCRIPTS = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "scripts")
OUT = os.path.join(tempfile.gettempdir(), "vms_train_curve_check")


def load(name):
    """스크립트를 모듈로 읽는다 — 최상위는 import 뿐이고 main() 은 __main__ 가드 안이라 안전하다."""
    spec = importlib.util.spec_from_file_location(name, os.path.join(SCRIPTS, name + ".py"))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def check(tag, out_dir, want_png=True):
    metrics = os.path.join(out_dir, "metrics.json")
    curves = os.path.join(out_dir, "curves.png")
    assert os.path.exists(metrics), tag + ": metrics.json 이 없다"
    data = json.load(open(metrics, encoding="utf-8"))
    leftover = [f for f in os.listdir(out_dir) if f.endswith(".tmp")]
    assert not leftover, tag + ": 임시 파일이 남았다 " + str(leftover)
    # 서버는 metrics.json 의 최상위 숫자만 ModelVersion.Metrics 로 읽는다 — 하나도 없으면 화면이 빈다
    flat = {k: v for k, v in data.items() if isinstance(v, (int, float))}
    assert flat, tag + ": 서버가 읽을 최상위 숫자가 없다"
    assert isinstance(data.get("history", []), list), tag + ": history 가 배열이 아니다"
    if want_png:
        assert os.path.exists(curves) and os.path.getsize(curves) > 1000, tag + ": curves.png 가 없거나 비었다"
    print("    %-22s 최상위 숫자 %d개%s" % (tag, len(flat),
          " · curves.png %dKB" % (os.path.getsize(curves) // 1024) if want_png else " · (그림 없음)"))


def fresh(name):
    d = os.path.join(OUT, name)
    os.makedirs(d, exist_ok=True)
    return d


def main():
    shutil.rmtree(OUT, ignore_errors=True)

    print("train_classifier")
    m = load("train_classifier")
    hist = [{"epoch": e, "train_loss": 1.0 / e, "train_acc": min(1.0, 0.5 + e * 0.1),
             "val_loss": 1.2 / e, "val_acc": min(1.0, 0.4 + e * 0.12),
             "lr": 0.001, "elapsed_sec": e * 3.0} for e in range(1, 7)]
    m.write_history_artifacts(fresh("cls"), hist, 6, 5)
    check("정상", os.path.join(OUT, "cls"))
    m.write_history_artifacts(fresh("cls_noval"), [dict(h, val_loss=None) for h in hist], 6, 5)
    check("검증 세트 없음", os.path.join(OUT, "cls_noval"))
    m.write_history_artifacts(fresh("cls_empty"), [], 6, 0)   # 첫 에폭 전에 죽는 경우
    print("    %-22s 예외 없음" % "빈 history")

    print("train_rfdetr_seg")
    m = load("train_rfdetr_seg")
    hist = [{"epoch": e, "train_loss": 2.0 / e, "map": min(1.0, e * 0.15), "elapsed_sec": e * 9.0}
            for e in range(1, 7)]
    m.write_history_artifacts(fresh("seg"), hist, 6, 6)
    check("정상", os.path.join(OUT, "seg"))
    m.write_history_artifacts(fresh("seg_nomap"), [dict(h, map=None) for h in hist], 6, 0)
    check("mAP 키 없음", os.path.join(OUT, "seg_nomap"))
    m.write_history_artifacts(fresh("seg_noloss"), [dict(h, train_loss=None) for h in hist], 6, 6)
    check("loss 키 없음", os.path.join(OUT, "seg_noloss"))

    print("train_yolo")
    m = load("train_yolo")
    d = fresh("yolo")
    run = os.path.join(d, "train")
    os.makedirs(run, exist_ok=True)
    # ultralytics 판마다 다른 열 이름을 흉내낸다 (앞뒤 공백 · 괄호 · 대문자)
    with open(os.path.join(run, "results.csv"), "w", encoding="utf-8") as f:
        f.write("                  epoch,      train/box_loss,      train/cls_loss,        val/box_loss,"
                "   metrics/mAP50(B),metrics/mAP50-95(B),               lr/pg0,    time\n")
        for e in range(1, 6):
            f.write("%d,%.4f,%.4f,%.4f,%.4f,%.4f,0.001,%.1f\n"
                    % (e, 2.0 / e, 1.0 / e, 2.4 / e, min(1.0, e * 0.2), min(1.0, e * 0.12), e * 11.0))
    hist = m.read_results_csv(run)
    assert len(hist) == 5, hist
    assert hist[0]["train_loss"] == 3.0, "train/ 손실 합산이 틀렸다: %s" % hist[0]
    assert hist[4]["map50"] == 1.0 and hist[4]["map50_95"] == 0.6, hist[4]
    assert hist[2]["elapsed_sec"] == 33.0, hist[2]
    print("    %-22s 5에폭 · 손실 합산·mAP·time 파싱 OK" % "results.csv")
    m.write_history_artifacts(d, hist, 5)
    check("정상", d)
    assert m.read_results_csv(os.path.join(d, "없는폴더")) == []
    print("    %-22s 빈 history (예외 없음)" % "results.csv 없음")

    print("train_anomaly")
    import numpy as np
    m = load("train_anomaly")
    rng = np.random.default_rng(0)
    scores = rng.normal(1.0, 0.2, 60)
    thr = float(scores.mean() + 2 * scores.std())
    d = fresh("anom")
    m.write_metrics(d, {"method": "simple_patchcore", "threshold": thr, "n_samples": 60},
                    scores=scores, threshold=thr, title="check")
    check("간이 경로 (분포)", d)
    d = fresh("anom_lib")
    m.write_metrics(d, {"method": "patchcore", "image_AUROC": 0.97})
    check("anomalib 경로 (요약)", d, want_png=False)

    print("\n전부 통과 — 산출물: %s" % OUT)


if __name__ == "__main__":
    try:
        main()
    except AssertionError as ex:
        print("\n[실패] %s" % ex, file=sys.stderr)
        sys.exit(1)
