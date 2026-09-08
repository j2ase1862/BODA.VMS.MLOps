"""
가짜 학습 스크립트 — 워커 프로토콜·상태 머신 테스트용 (Phase 3 §11). GPU·torch 불필요.
stdout 프로토콜(부록 A)만 흉내 내고, 규약 판별이 가능한 스텁 ONNX(D-FINE deploy, names good/defect, imgsz 640)를 best.onnx 로 쓴다.

환경 변수:
  FAKE_MODE   ok(기본) | error | stall | oom | crash | nodone
  FAKE_EPOCH_DELAY  에폭 사이 대기 초 (기본 0.05)
  FAKE_STALL_SEC    stall 모드에서 아무 것도 출력하지 않고 기다리는 초 (기본 3600)
"""
import argparse
import base64
import json
import os
import sys
import time

STUB_ONNX_B64 = (
    "CAg61AYKLQoRb3JpZ190YXJnZXRfc2l6ZXMSB3NpemVzX2YiBENhc3QqCQoCdG8YAaABAgpMEgRnaWR4IghDb25zdGFudCo6CgV2YWx1ZSouCAQQB0IGZ2lkeF92SiABAAAAAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAKABBAoqCgdzaXplc19mCgRnaWR4EgR3aHdoIgZHYXRoZXIqCwoEYXhpcxgBoAECCjISA3VheCIIQ29uc3RhbnQqIQoFdmFsdWUqFQgBEAdCBXVheF92SggBAAAAAAAAAKABBAodCgR3aHdoCgN1YXgSBXdod2gzIglVbnNxdWVlemUKvwESCmJveGVzX25vcm0iCENvbnN0YW50KqYBCgV2YWx1ZSqZAQgBCAgIBBABQgxib3hlc19ub3JtX3ZKgAHNzMw9zcxMPgAAAD+amRk/AAAAPwAAAD9mZmY/ZmZmP65H4T09Clc+XI8CP/YoHD8AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAKABBAofCgpib3hlc19ub3JtCgV3aHdoMxIFYm94ZXMiA011bApyEgZsYWJlbHMiCENvbnN0YW50Kl4KBXZhbHVlKlIIAQgIEAdCCGxhYmVsc192SkABAAAAAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAoAEEClISBnNjb3JlcyIIQ29uc3RhbnQqPgoFdmFsdWUqMggBCAgQAUIIc2NvcmVzX3ZKIGZmZj+amRk/AAAAPwAAAAAAAAAAAAAAAAAAAAAAAAAAoAEEEhFkZmluZV9kZXBsb3lfc3R1YloiCgZpbWFnZXMSGAoWCAESEgoCCAEKAggDCgMIgAUKAwiABVojChFvcmlnX3RhcmdldF9zaXplcxIOCgwIBxIICgIIAQoCCAJiGAoGbGFiZWxzEg4KDAgHEggKAggBCgIICGIbCgVib3hlcxISChAIARIMCgIIAQoCCAgKAggEYhgKBnNjb3JlcxIOCgwIARIICgIIAQoCCAhCBAoAEA1yIQoFbmFtZXMSGHswOiAnZ29vZCcsIDE6ICdkZWZlY3QnfXITCgVpbWdzehIKWzY0MCwgNjQwXXIVCgxtb2RlbF9mb3JtYXQSBWRmaW5l"
)


def out(tag, msg=""):
    print(f"[{tag}] {msg}".rstrip(), flush=True)


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--dataset", required=True)
    p.add_argument("--output", required=True)
    p.add_argument("--epochs", type=int, default=3)
    p.add_argument("--lr", type=float, default=0.001)
    p.add_argument("--batch_size", type=int, default=8)
    p.add_argument("--imgsz", type=int, default=640)
    p.add_argument("--pretrained", default="")
    p.add_argument("--seed", type=int, default=0)
    p.add_argument("--export_onnx", action="store_true")
    args, unknown = p.parse_known_args()

    mode = os.environ.get("FAKE_MODE", "ok").lower()
    delay = float(os.environ.get("FAKE_EPOCH_DELAY", "0.05"))
    os.makedirs(args.output, exist_ok=True)

    out("INFO", f"fake trainer start mode={mode} dataset={args.dataset} epochs={args.epochs} batch={args.batch_size} seed={args.seed} extra={unknown}")
    if not os.path.isdir(args.dataset):
        out("ERROR", f"dataset not found: {args.dataset}")
        sys.exit(2)

    if mode == "crash":
        out("INFO", "crashing before training")
        sys.exit(3)

    for e in range(1, args.epochs + 1):
        if mode == "stall" and e == 2:
            time.sleep(float(os.environ.get("FAKE_STALL_SEC", "3600")))
        if mode == "oom" and e == 2 and args.batch_size > 4:
            print("RuntimeError: CUDA out of memory. Tried to allocate 512.00 MiB", file=sys.stderr, flush=True)
            out("ERROR", "CUDA out of memory")
            sys.exit(1)
        if mode == "error" and e == 2:
            out("ERROR", "simulated failure at epoch 2")
            sys.exit(1)
        time.sleep(delay)
        out("EPOCH", f"{e}/{args.epochs}")
        out("LOSS", f"{1.0 / e:.4f}")
        out("ACC", f"{min(0.99, 0.5 + 0.1 * e):.3f}")
        out("PROGRESS", f"{100.0 * e / args.epochs:.1f}")

    best_dir = os.path.join(args.output, "best")
    os.makedirs(best_dir, exist_ok=True)
    with open(os.path.join(best_dir, "vms_train_info.json"), "w", encoding="utf-8") as f:
        json.dump({"epochs": args.epochs, "map50": min(0.99, 0.5 + 0.1 * args.epochs), "val_loss": 1.0 / args.epochs,
                   "names": {"0": "good", "1": "defect"}, "imgsz": args.imgsz, "pretrained": args.pretrained,
                   "seed": args.seed, "batch_size": args.batch_size}, f, ensure_ascii=False, indent=2)
    with open(os.path.join(args.output, "metrics.json"), "w", encoding="utf-8") as f:
        json.dump({"map50": min(0.99, 0.5 + 0.1 * args.epochs), "val_loss": 1.0 / args.epochs, "epochs": args.epochs}, f)

    if args.export_onnx:
        onnx_path = os.path.join(args.output, "best.onnx")
        with open(onnx_path, "wb") as f:
            f.write(base64.b64decode(STUB_ONNX_B64))
        out("INFO", "ONNX 검증 OK (stub)")
        out("ONNX", onnx_path)

    if mode == "nodone":
        sys.exit(0)
    out("DONE")


if __name__ == "__main__":
    main()
