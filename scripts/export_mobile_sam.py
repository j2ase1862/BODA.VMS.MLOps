#!/usr/bin/env python3
"""
MobileSAM ONNX Export Script.
Downloads MobileSAM checkpoint and exports Encoder/Decoder as ONNX files.

Prerequisites:
    pip install torch torchvision onnx onnxruntime
    pip install git+https://github.com/ChaoningZhang/MobileSAM.git

Usage:
    python export_mobile_sam.py --output ./sam_models

Output:
    ./sam_models/mobile_sam_encoder.onnx
    ./sam_models/mobile_sam_decoder.onnx
"""

import argparse
import os
import sys
import urllib.request
import warnings

CHECKPOINT_URL = "https://github.com/ChaoningZhang/MobileSAM/raw/master/weights/mobile_sam.pt"
CHECKPOINT_NAME = "mobile_sam.pt"


def download_checkpoint(output_dir: str) -> str:
    checkpoint_path = os.path.join(output_dir, CHECKPOINT_NAME)
    if os.path.exists(checkpoint_path):
        print(f"[INFO] Checkpoint already exists: {checkpoint_path}")
        return checkpoint_path

    print(f"[INFO] Downloading MobileSAM checkpoint...")
    print(f"  URL: {CHECKPOINT_URL}")
    try:
        urllib.request.urlretrieve(CHECKPOINT_URL, checkpoint_path, _progress_hook)
        print(f"\n[INFO] Download complete: {checkpoint_path}")
    except Exception as e:
        print(f"[ERROR] Download failed: {e}")
        print(f"[INFO] Manual download:")
        print(f"  1. Download mobile_sam.pt from https://github.com/ChaoningZhang/MobileSAM")
        print(f"  2. Save to: {checkpoint_path}")
        sys.exit(1)

    return checkpoint_path


def _progress_hook(block_num, block_size, total_size):
    downloaded = block_num * block_size
    if total_size > 0:
        percent = min(100, downloaded * 100 / total_size)
        mb = downloaded / (1024 * 1024)
        total_mb = total_size / (1024 * 1024)
        sys.stdout.write(f"\r  {mb:.1f}/{total_mb:.1f} MB ({percent:.0f}%)")
        sys.stdout.flush()


def _onnx_export(model, args, path, **kwargs):
    """torch.onnx.export wrapper that forces the legacy TorchScript exporter."""
    import torch as _torch
    try:
        _torch.onnx.export(model, args, path, dynamo=False, **kwargs)
    except TypeError:
        # Older PyTorch without dynamo parameter
        _torch.onnx.export(model, args, path, **kwargs)


def export_encoder(sam_model, output_path: str):
    import torch

    print(f"[INFO] Exporting Encoder to ONNX...")
    encoder = sam_model.image_encoder
    encoder.eval()

    dummy_input = torch.randn(1, 3, 1024, 1024)

    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        _onnx_export(
            encoder,
            dummy_input,
            output_path,
            input_names=["input_image"],
            output_names=["image_embeddings"],
            opset_version=17,
            do_constant_folding=True,
        )

    size_mb = os.path.getsize(output_path) / (1024 * 1024)
    print(f"[INFO] Encoder export done: {output_path} ({size_mb:.1f} MB)")


def export_decoder(sam_model, output_path: str):
    import torch
    from mobile_sam.utils.onnx import SamOnnxModel

    print(f"[INFO] Exporting Decoder to ONNX (using official SamOnnxModel)...")

    onnx_model = SamOnnxModel(sam_model, return_single_mask=True)
    onnx_model.eval()

    embed_dim = sam_model.prompt_encoder.embed_dim
    embed_size = sam_model.prompt_encoder.image_embedding_size

    dummy_inputs = {
        "image_embeddings": torch.randn(1, embed_dim, *embed_size),
        "point_coords": torch.randint(0, 1024, (1, 2, 2), dtype=torch.float),
        "point_labels": torch.randint(0, 4, (1, 2), dtype=torch.float),
        "mask_input": torch.randn(1, 1, 256, 256),
        "has_mask_input": torch.tensor([1.0]),
        "orig_im_size": torch.tensor([1024, 1024], dtype=torch.float),
    }

    output_names = ["masks", "iou_predictions", "low_res_masks"]

    dynamic_axes = {
        "point_coords": {1: "num_points"},
        "point_labels": {1: "num_points"},
    }

    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        _onnx_export(
            onnx_model,
            tuple(dummy_inputs.values()),
            output_path,
            input_names=list(dummy_inputs.keys()),
            output_names=output_names,
            dynamic_axes=dynamic_axes,
            opset_version=17,
            do_constant_folding=True,
        )

    size_mb = os.path.getsize(output_path) / (1024 * 1024)
    print(f"[INFO] Decoder export done: {output_path} ({size_mb:.1f} MB)")


def verify_onnx(encoder_path: str, decoder_path: str):
    try:
        import onnx

        print(f"[INFO] Verifying ONNX models...")

        model = onnx.load(encoder_path)
        onnx.checker.check_model(model)
        print(f"  Encoder: OK")

        model = onnx.load(decoder_path)
        onnx.checker.check_model(model)
        print(f"  Decoder: OK")

    except ImportError:
        print(f"[WARN] onnx package not found, skipping verification")
    except Exception as e:
        print(f"[WARN] Verification failed (can be ignored): {e}")


def verify_inference(encoder_path: str, decoder_path: str):
    try:
        import onnxruntime as ort
        import numpy as np

        print(f"[INFO] Running OnnxRuntime inference test...")

        enc_sess = ort.InferenceSession(encoder_path)
        dummy_img = np.random.randn(1, 3, 1024, 1024).astype(np.float32)
        enc_out = enc_sess.run(None, {"input_image": dummy_img})
        embedding = enc_out[0]
        print(f"  Encoder output shape: {embedding.shape}")

        dec_sess = ort.InferenceSession(decoder_path)
        dec_inputs = {
            "image_embeddings": embedding,
            "point_coords": np.array([[[512.0, 512.0]]], dtype=np.float32),
            "point_labels": np.array([[1.0]], dtype=np.float32),
            "mask_input": np.zeros((1, 1, 256, 256), dtype=np.float32),
            "has_mask_input": np.array([0.0], dtype=np.float32),
            "orig_im_size": np.array([512.0, 512.0], dtype=np.float32),
        }
        dec_out = dec_sess.run(None, dec_inputs)
        print(f"  Decoder masks shape: {dec_out[0].shape}")
        print(f"  Decoder IoU scores: {dec_out[1]}")

        print(f"[INFO] Inference test passed!")

    except ImportError:
        print(f"[WARN] onnxruntime not found, skipping inference test")
    except Exception as e:
        print(f"[WARN] Inference test failed: {e}")


def main():
    parser = argparse.ArgumentParser(description="MobileSAM ONNX Export")
    parser.add_argument("--output", default=".", help="Output directory (default: current dir)")
    parser.add_argument("--checkpoint", default=None, help="Path to mobile_sam.pt (auto-download if not given)")
    parser.add_argument("--skip-verify", action="store_true", help="Skip ONNX verification")
    args = parser.parse_args()

    os.makedirs(args.output, exist_ok=True)

    # 1. Checkpoint
    if args.checkpoint and os.path.exists(args.checkpoint):
        checkpoint_path = args.checkpoint
        print(f"[INFO] Using checkpoint: {checkpoint_path}")
    else:
        checkpoint_path = download_checkpoint(args.output)

    # 2. Load MobileSAM
    print(f"[INFO] Loading MobileSAM model...")
    try:
        import torch

        try:
            from mobile_sam import sam_model_registry
            sam = sam_model_registry["vit_t"](checkpoint=checkpoint_path)
        except ImportError:
            print(f"[ERROR] mobile_sam package not found.")
            print(f"  Install: pip install git+https://github.com/ChaoningZhang/MobileSAM.git")
            sys.exit(1)

        sam.eval()
        print(f"[INFO] Model loaded successfully")

    except Exception as e:
        print(f"[ERROR] Failed to load model: {e}")
        sys.exit(1)

    # 3. ONNX Export
    encoder_path = os.path.join(args.output, "mobile_sam_encoder.onnx")
    decoder_path = os.path.join(args.output, "mobile_sam_decoder.onnx")

    export_encoder(sam, encoder_path)
    export_decoder(sam, decoder_path)

    # 4. Verify
    if not args.skip_verify:
        verify_onnx(encoder_path, decoder_path)
        verify_inference(encoder_path, decoder_path)

    # 5. Done
    print()
    print("=" * 60)
    print("  MobileSAM ONNX Export Complete!")
    print("=" * 60)
    print(f"  Encoder: {os.path.abspath(encoder_path)}")
    print(f"  Decoder: {os.path.abspath(decoder_path)}")
    print()
    print("  Usage in VMS DeepLearning:")
    print("    1. Create Segmentation dataset")
    print("    2. SAM Model > Set Encoder/Decoder paths")
    print("    3. Click Load SAM Model")
    print("=" * 60)


if __name__ == "__main__":
    main()
