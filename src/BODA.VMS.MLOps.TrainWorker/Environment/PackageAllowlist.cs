namespace BODA.VMS.MLOps.TrainWorker.Environment;

/// <summary>
/// 파이썬 패키지 허용 목록 (개발 문서 §8, Phase 3 §6). 워커는 여기에 없는 패키지를 설치하지 않는다.
/// 새 패키지는 이 목록 + scripts/requirements-allowlist.txt + gs_distribution_policy.md §2.6 을 함께 갱신한다.
/// </summary>
public static class PackageAllowlist
{
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // 프레임워크 (BSD / Apache / MIT)
        "torch", "torchvision", "torchaudio", "transformers", "safetensors", "tokenizers", "huggingface-hub", "huggingface_hub",
        "onnx", "onnxruntime", "onnxruntime-gpu", "onnxscript", "onnxslim",
        // 학습 유틸
        "numpy", "pillow", "pyyaml", "tqdm", "scipy", "matplotlib", "opencv-python-headless", "opencv-python",
        "torchmetrics", "pycocotools", "faster-coco-eval", "timm", "einops", "psutil", "packaging", "filelock", "fsspec", "requests",
        // 이상탐지 (Apache 2.0)
        "anomalib", "lightning", "pytorch-lightning", "omegaconf", "jsonargparse", "rich", "scikit-learn", "scikit-image", "kornia", "freia",
        // OCR (Apache 2.0)
        "paddlepaddle", "paddlepaddle-gpu", "paddleocr", "paddle2onnx", "rapidfuzz", "lmdb", "imgaug", "shapely", "pyclipper",
        // 검출·세그멘테이션 (Apache 2.0). rfdetr[train] 이 끌고 오는 것까지 함께 연다 —
        // 하나라도 빠지면 워커가 설치를 거부해 세그멘테이션 학습이 시작되지 않는다.
        "rfdetr", "supervision", "peft", "torch-hungarian", "torch_hungarian", "roboflow",
        // 라벨링 보조 (Apache 2.0)
        "mobile-sam", "mobile_sam", "segment-anything",
        // SSL 검사 백신 환경 대응
        "truststore", "certifi", "pip", "setuptools", "wheel",
    };

    /// <summary>"torch==2.5.1+cu124", "transformers>=4.52" → "torch", "transformers"</summary>
    public static string NormalizeName(string requirementLine)
    {
        var s = requirementLine.Trim();
        var cut = s.IndexOfAny(['=', '>', '<', '~', '!', ';', '[', ' ', '@']);
        return (cut >= 0 ? s[..cut] : s).Trim().ToLowerInvariant();
    }

    public static bool IsAllowed(string requirementLine)
    {
        var name = NormalizeName(requirementLine);
        return name.Length > 0 && (Allowed.Contains(name) || Allowed.Contains(name.Replace('_', '-')) || Allowed.Contains(name.Replace('-', '_')));
    }

    /// <summary>
    /// requirements 파일에 허용하는 pip 옵션. 다른 파일을 끌어오거나(-r, -c) 임의 소스를 빌드하는(-e) 옵션은 막는다.
    /// 이것들을 무조건 통과시키면 허용 목록 검사가 통째로 우회된다.
    /// </summary>
    private static readonly string[] AllowedOptions = ["--extra-index-url", "--index-url", "--find-links", "--only-binary", "--prefer-binary"];

    /// <summary>requirements 파일에서 허용 목록 밖 항목·옵션을 찾는다 (주석·빈 줄 제외)</summary>
    public static List<string> FindDisallowed(IEnumerable<string> lines) =>
        lines.Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Where(l => l.StartsWith('-') ? !IsAllowedOption(l) : !IsAllowed(l))
            .ToList();

    public static bool IsAllowedOption(string line)
    {
        var name = line.Split([' ', '='], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return AllowedOptions.Contains(name, StringComparer.OrdinalIgnoreCase);
    }
}
