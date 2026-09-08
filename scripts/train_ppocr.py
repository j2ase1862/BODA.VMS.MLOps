#!/usr/bin/env python3
"""
VMS PaddleOCR Fine-tuning 학습 스크립트.
PaddleX가 설치한 PaddleOCR 소스 repo의 tools/train.py를 직접 호출.

stdout 프로토콜 (라인 prefix):
    [EPOCH] cur/total
    [LOSS] value
    [ACC] value
    [PROGRESS] 0~100
    [ONNX] absolute_path
    [DONE]
    [ERROR] message

사전 준비:
    pip install paddlepaddle paddleocr paddle2onnx onnx
    (첫 실행 시 paddlex --install PaddleOCR -y 자동 실행 — repo 클론)

데이터셋 (VMS SynthData PaddleOCR rec 형식):
    dataset/
      train_crops/...png
      val_crops/...png
      train_rec.txt    (image_path<TAB>label)
      val_rec.txt
      dict.txt
      ppocr_keys_v1.txt

사용 예:
    python train_ppocr.py --target recognition --dataset ./data --output ./out --epochs 20 --export_onnx
"""

import argparse
import os
import re
import subprocess
import sys
import traceback
import yaml


DEFAULT_REC_BASE = ("rec", "PP-OCRv3", "en_PP-OCRv3_rec.yml")
DEFAULT_DET_BASE = ("det", "ch_PP-OCRv3", "ch_PP-OCRv3_det_student.yml")


def parse_args():
    p = argparse.ArgumentParser(description="VMS PaddleOCR Fine-tuning")
    p.add_argument("--target", choices=["recognition", "detection"], default="recognition")
    p.add_argument("--dataset", required=True, help="VMS SynthData 출력 폴더")
    p.add_argument("--output", required=True, help="학습 결과 저장 폴더")
    p.add_argument("--pretrained", default="", help="(선택) 사전학습 모델 prefix (.pdparams 제외)")
    p.add_argument("--epochs", type=int, default=20)
    p.add_argument("--lr", type=float, default=0.001)
    p.add_argument("--batch_size", type=int, default=8)
    p.add_argument("--base_config", default="", help="(선택) 베이스 YAML 절대 경로")
    p.add_argument("--export_onnx", action="store_true")
    return p.parse_args()


def emit(tag: str, value=""):
    print(f"[{tag}] {value}".rstrip(), flush=True)


def check_deps():
    missing = []
    for mod in ["paddle", "paddleocr"]:
        try: __import__(mod)
        except ImportError: missing.append(mod)
    if missing:
        emit("ERROR", f"필수 패키지 누락: {', '.join(missing)}. pip install paddlepaddle paddleocr paddle2onnx")
        sys.exit(1)


def get_repo_dir() -> str:
    """PaddleOCR 소스 repo 경로 결정. 우선순위:
    1) 환경변수 PADDLEOCR_REPO_DIR
    2) paddlex가 클론한 위치 (paddlex 설치돼 있고 repos/PaddleOCR 존재 시)
    3) site-packages/paddlex/repo_manager/repos/PaddleOCR (paddlex import 실패해도 경로 추정)
    4) <venv>/PaddleOCR (사용자 수동 clone)
    """
    env = os.environ.get("PADDLEOCR_REPO_DIR")
    if env and os.path.exists(os.path.join(env, "tools", "train.py")):
        return env

    try:
        import paddlex
        p = os.path.join(os.path.dirname(paddlex.__file__),
                         "repo_manager", "repos", "PaddleOCR")
        if os.path.exists(os.path.join(p, "tools", "train.py")):
            return p
    except Exception:
        pass

    # site-packages 추정 (paddlex import 실패해도 폴더는 남아있을 수 있음)
    try:
        import paddle
        site_pkg = os.path.dirname(os.path.dirname(paddle.__file__))
        candidates = [
            os.path.join(site_pkg, "paddlex", "repo_manager", "repos", "PaddleOCR"),
            os.path.join(site_pkg, "..", "PaddleOCR"),
            os.path.join(os.path.dirname(sys.executable), "..", "PaddleOCR"),
        ]
        for c in candidates:
            full = os.path.abspath(c)
            if os.path.exists(os.path.join(full, "tools", "train.py")):
                return full
    except Exception:
        pass

    # 마지막 fallback — 기본 paddlex 위치 반환 (존재 여부는 ensure에서 체크)
    try:
        import paddle
        return os.path.join(os.path.dirname(os.path.dirname(paddle.__file__)),
                            "paddlex", "repo_manager", "repos", "PaddleOCR")
    except Exception:
        return ""


def ensure_paddleocr_repo():
    """PaddleOCR repo (tools/train.py 포함)가 사용 가능한지 확인.
    없으면 git clone release/2.7. paddlex 의존 없음.
    """
    repo_dir = get_repo_dir()
    if repo_dir and os.path.exists(os.path.join(repo_dir, "tools", "train.py")):
        print(f"PaddleOCR repo 존재: {repo_dir}", flush=True)
        emit("PROGRESS", "5")
        return repo_dir

    clone_target = repo_dir or os.path.abspath(os.path.join(
        os.path.dirname(sys.executable), "..", "PaddleOCR"))

    print(f"PaddleOCR repo 미설치 — git clone 시도 (~2-3분)", flush=True)
    print(f"clone target: {clone_target}", flush=True)
    emit("PROGRESS", "2")

    os.makedirs(os.path.dirname(clone_target), exist_ok=True)
    cmd = ["git", "clone", "--depth=1", "-b", "release/2.7",
           "https://github.com/PaddlePaddle/PaddleOCR.git", clone_target]
    print(f"실행: {' '.join(cmd)}", flush=True)
    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            text=True, encoding="utf-8", errors="replace", bufsize=1)
    for line in proc.stdout:
        print(line.rstrip(), flush=True)
    proc.wait()
    if proc.returncode != 0:
        emit("ERROR", f"git clone 실패 (exit={proc.returncode}). 수동: {' '.join(cmd)}")
        sys.exit(1)

    if not os.path.exists(os.path.join(clone_target, "tools", "train.py")):
        emit("ERROR", f"clone 후에도 tools/train.py 없음: {clone_target}")
        sys.exit(1)

    print(f"PaddleOCR repo clone 완료", flush=True)
    emit("PROGRESS", "5")
    return clone_target


def find_base_config(repo_dir: str, args) -> str:
    """모델별 기본 YAML 경로 결정. --base_config로 override 가능."""
    if args.base_config and os.path.exists(args.base_config):
        return args.base_config

    sub = DEFAULT_REC_BASE if args.target == "recognition" else DEFAULT_DET_BASE
    path = os.path.join(repo_dir, "configs", *sub)
    if not os.path.exists(path):
        raise FileNotFoundError(f"베이스 config를 찾을 수 없습니다: {path}")
    return path


def build_runtime_yaml(args, base_yaml_path: str) -> str:
    """베이스 YAML을 로드해 사용자 파라미터로 덮어쓴 runtime config를 output_dir에 저장."""
    with open(base_yaml_path, "r", encoding="utf-8") as f:
        cfg = yaml.safe_load(f)

    dataset_dir = os.path.abspath(args.dataset)
    output_dir = os.path.abspath(args.output)
    os.makedirs(output_dir, exist_ok=True)

    # GPU 자동 감지
    try:
        import paddle
        use_gpu = paddle.is_compiled_with_cuda()
    except Exception:
        use_gpu = False

    # Global 섹션 수정
    g = cfg.setdefault("Global", {})
    g["use_gpu"] = use_gpu
    g["epoch_num"] = args.epochs
    g["save_model_dir"] = output_dir
    g["save_inference_dir"] = os.path.join(output_dir, "inference")
    g["pretrained_model"] = args.pretrained or ""
    g["save_epoch_step"] = max(1, args.epochs // 5)
    g["eval_batch_step"] = [0, max(50, args.epochs * 5)]
    # 사전 경로 — VMS SynthData가 dict.txt + ppocr_keys_v1.txt 모두 출력
    dict_candidates = [os.path.join(dataset_dir, n) for n in ("ppocr_keys_v1.txt", "dict.txt")]
    for d in dict_candidates:
        if os.path.exists(d):
            g["character_dict_path"] = d
            break

    # Optimizer
    if "Optimizer" in cfg and "lr" in cfg["Optimizer"]:
        cfg["Optimizer"]["lr"]["learning_rate"] = args.lr

    # Train/Eval dataset 경로 — 라벨 파일에 'train_crops/xxx.png' prefix가 이미 포함되므로
    # data_dir는 dataset_dir 자체 (parent). 그렇지 않으면 'train_crops\train_crops/...' 중복.
    if "Train" in cfg and "dataset" in cfg["Train"]:
        cfg["Train"]["dataset"]["data_dir"] = dataset_dir
        cfg["Train"]["dataset"]["label_file_list"] = [os.path.join(dataset_dir, "train_rec.txt")]
        if "loader" in cfg["Train"]:
            cfg["Train"]["loader"]["batch_size_per_card"] = args.batch_size

    if "Eval" in cfg and "dataset" in cfg["Eval"]:
        cfg["Eval"]["dataset"]["data_dir"] = dataset_dir
        cfg["Eval"]["dataset"]["label_file_list"] = [os.path.join(dataset_dir, "val_rec.txt")]
        if "loader" in cfg["Eval"]:
            cfg["Eval"]["loader"]["batch_size_per_card"] = args.batch_size

    runtime_yaml = os.path.join(output_dir, "runtime_config.yml")
    with open(runtime_yaml, "w", encoding="utf-8") as f:
        yaml.safe_dump(cfg, f, allow_unicode=True, sort_keys=False)
    return runtime_yaml


_EPOCH_RE = re.compile(r"epoch:\s*\[?\s*(\d+)\s*/\s*(\d+)\s*\]?", re.IGNORECASE)
_LOSS_RE = re.compile(r"loss:\s*([-\d.]+)", re.IGNORECASE)
_ACC_RE = re.compile(r"acc:\s*([-\d.]+)", re.IGNORECASE)


def run_training(repo_dir: str, runtime_yaml: str, args):
    """tools/train.py 실행. 라인별 stdout 파싱하여 [EPOCH]/[LOSS]/[ACC] 프로토콜로 변환."""
    train_script = os.path.join(repo_dir, "tools", "train.py")
    cmd = [sys.executable, train_script, "-c", runtime_yaml]
    print(f"학습 명령: {' '.join(cmd)}", flush=True)
    emit("PROGRESS", "10")

    env = os.environ.copy()
    env["PYTHONPATH"] = repo_dir + os.pathsep + env.get("PYTHONPATH", "")

    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            text=True, encoding="utf-8", errors="replace",
                            bufsize=1, env=env, cwd=repo_dir)
    last_epoch = 0
    for line in proc.stdout:
        line = line.rstrip()
        if not line: continue
        print(line, flush=True)

        m = _EPOCH_RE.search(line)
        if m:
            cur, total = int(m.group(1)), int(m.group(2))
            emit("EPOCH", f"{cur}/{total}")
            if total > 0:
                emit("PROGRESS", f"{10 + cur / total * 75:.1f}")
            last_epoch = cur

        m = _LOSS_RE.search(line)
        if m:
            try: emit("LOSS", m.group(1))
            except: pass

        m = _ACC_RE.search(line)
        if m:
            try: emit("ACC", m.group(1))
            except: pass

    proc.wait()
    if proc.returncode != 0:
        emit("ERROR", f"학습 종료 (exit={proc.returncode})")
        sys.exit(proc.returncode)
    emit("PROGRESS", "85")


def export_inference(repo_dir: str, runtime_yaml: str, args) -> str:
    """tools/export_model.py로 학습 결과를 inference 모델로 export.
    FLAGS_enable_pir_api=0 — PaddlePaddle 3.x 신규 PIR 포맷(inference.json) 대신
    구 포맷(inference.pdmodel) 출력 강제. paddle2onnx 1.3.1과 호환."""
    export_script = os.path.join(repo_dir, "tools", "export_model.py")
    best_prefix = os.path.join(args.output, "best_accuracy")
    latest_prefix = os.path.join(args.output, "latest")
    weight = best_prefix if os.path.exists(best_prefix + ".pdparams") else latest_prefix
    if not os.path.exists(weight + ".pdparams"):
        raise FileNotFoundError(f"학습 가중치 없음: {best_prefix}.pdparams / {latest_prefix}.pdparams")

    inference_dir = os.path.join(args.output, "inference")
    # Global.export_with_pir=False — PaddleOCR이 PIR 대신 paddle.jit.save 구 경로 사용 (Assertion 회피)
    cmd = [sys.executable, export_script, "-c", runtime_yaml,
           "-o", f"Global.pretrained_model={weight}",
           f"Global.save_inference_dir={inference_dir}",
           "Global.export_with_pir=False"]
    print(f"Export 명령: {' '.join(cmd)}", flush=True)
    env = os.environ.copy()
    env["PYTHONPATH"] = repo_dir + os.pathsep + env.get("PYTHONPATH", "")
    env["FLAGS_enable_pir_api"] = "0"   # 구 포맷 강제
    env["FLAGS_enable_pir_in_executor"] = "0"
    r = subprocess.run(cmd, capture_output=True, text=True, env=env, cwd=repo_dir,
                       encoding="utf-8", errors="replace")
    if r.stdout: print(r.stdout, flush=True)
    if r.stderr: print(r.stderr, flush=True)
    if r.returncode != 0 or not os.path.exists(inference_dir):
        raise RuntimeError(f"Export 실패 (exit={r.returncode})")
    return inference_dir


def _resolve_paddle2onnx_cmd():
    """paddle2onnx 실행파일 경로 결정.
    1) `python -m paddle2onnx` — 신규 2.x 버전만 동작
    2) <venv>/Scripts/paddle2onnx(.exe) — 1.x/2.x 모두 동작 (Windows pip 설치 시)
    3) PATH의 paddle2onnx
    """
    # 1) -m 시도 가능 여부 (paddle2onnx/__main__.py 존재 확인)
    try:
        import paddle2onnx as _p2o
        if os.path.exists(os.path.join(os.path.dirname(_p2o.__file__), "__main__.py")):
            return [sys.executable, "-m", "paddle2onnx"]
    except ImportError:
        pass

    # 2) <venv>/Scripts/paddle2onnx(.exe)
    scripts_dir = os.path.dirname(sys.executable)
    for name in ("paddle2onnx.exe", "paddle2onnx"):
        path = os.path.join(scripts_dir, name)
        if os.path.exists(path):
            return [path]

    # 3) PATH
    try:
        import shutil
        path = shutil.which("paddle2onnx")
        if path: return [path]
    except Exception:
        pass

    return None


def export_to_onnx(inference_dir: str, onnx_path: str) -> bool:
    """paddle2onnx로 ONNX 변환. 구 포맷(inference.pdmodel) + 신규 PIR(inference.json) 모두 지원."""
    pir_model = os.path.join(inference_dir, "inference.json")
    old_model = os.path.join(inference_dir, "inference.pdmodel")

    p2o_cmd = _resolve_paddle2onnx_cmd()
    if p2o_cmd is None:
        print(f"[ERROR] paddle2onnx 실행파일을 찾을 수 없습니다. pip install paddle2onnx 확인.", flush=True)
        return False
    print(f"paddle2onnx 실행: {p2o_cmd}", flush=True)

    common = ["--save_file", onnx_path, "--opset_version", "14"]

    # 구 포맷 우선 — paddle2onnx 1.3.1과 호환 (현재 환경에서 export_with_pir=False로 생성됨)
    if os.path.exists(old_model):
        print(f"구 포맷 감지: {old_model}", flush=True)
        cmd = p2o_cmd + ["--model_dir", inference_dir,
                         "--model_filename", "inference.pdmodel",
                         "--params_filename", "inference.pdiparams"] + common
    elif os.path.exists(pir_model):
        print(f"PIR 포맷 감지: {pir_model}", flush=True)
        cmd = p2o_cmd + ["--model_dir", inference_dir,
                         "--model_filename", "inference.json",
                         "--params_filename", "inference.pdiparams"] + common
    else:
        print(f"[ERROR] inference 모델 파일을 찾을 수 없습니다 (json/pdmodel 모두 없음): {inference_dir}", flush=True)
        return False

    print(f"ONNX 변환 명령: {' '.join(cmd)}", flush=True)
    r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if r.stdout: print("[paddle2onnx stdout]\n" + r.stdout, flush=True)
    if r.stderr: print("[paddle2onnx stderr]\n" + r.stderr, flush=True)

    if r.returncode == 0 and os.path.exists(onnx_path):
        return True

    print(f"1차 변환 실패 (exit={r.returncode}) — 자동 감지 모드로 재시도", flush=True)
    fallback = p2o_cmd + ["--model_dir", inference_dir] + common
    print(f"ONNX 재시도 명령: {' '.join(fallback)}", flush=True)
    r2 = subprocess.run(fallback, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if r2.stdout: print("[paddle2onnx stdout]\n" + r2.stdout, flush=True)
    if r2.stderr: print("[paddle2onnx stderr]\n" + r2.stderr, flush=True)
    return r2.returncode == 0 and os.path.exists(onnx_path)


def main():
    args = parse_args()
    print("=" * 60, flush=True)
    print(f"VMS PaddleOCR Fine-tuning", flush=True)
    print(f"  Target:    {args.target}", flush=True)
    print(f"  Dataset:   {args.dataset}", flush=True)
    print(f"  Output:    {args.output}", flush=True)
    print(f"  Epochs:    {args.epochs}", flush=True)
    print(f"  Batch:     {args.batch_size}", flush=True)
    print(f"  LR:        {args.lr}", flush=True)
    print(f"  ONNX:      {args.export_onnx}", flush=True)
    print("=" * 60, flush=True)

    try:
        check_deps()
        repo_dir = ensure_paddleocr_repo()
        base_yaml = find_base_config(repo_dir, args)
        print(f"베이스 config: {base_yaml}", flush=True)
        runtime_yaml = build_runtime_yaml(args, base_yaml)
        print(f"런타임 config: {runtime_yaml}", flush=True)

        run_training(repo_dir, runtime_yaml, args)

        if args.export_onnx:
            inference_dir = export_inference(repo_dir, runtime_yaml, args)
            print(f"Inference 모델: {inference_dir}", flush=True)
            onnx_path = os.path.abspath(os.path.join(
                args.output,
                "custom_rec.onnx" if args.target == "recognition" else "custom_det.onnx"))
            if export_to_onnx(inference_dir, onnx_path):
                emit("ONNX", onnx_path)
            else:
                emit("ERROR", "ONNX 변환 실패 — inference 모델은 정상")

        emit("PROGRESS", "100")
        emit("DONE")
    except SystemExit:
        raise
    except Exception as e:
        emit("ERROR", f"{type(e).__name__}: {e}")
        traceback.print_exc()
        sys.exit(1)


if __name__ == "__main__":
    main()
