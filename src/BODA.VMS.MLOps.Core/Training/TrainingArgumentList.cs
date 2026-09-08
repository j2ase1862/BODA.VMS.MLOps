using System.Globalization;
using BODA.VMS.MLOps.Core.Domain;

namespace BODA.VMS.MLOps.Core.Training;

/// <summary>
/// 워커가 train_*.py 를 실행할 때 쓰는 인자 목록. <c>ProcessStartInfo.ArgumentList</c> 에 그대로 넣는다
/// (문자열 연결·쿼팅 없음 → 인젝션 불가, Phase 3 §8). 화이트리스트 검증된 값만 받는다.
/// VMS.Core.Contracts 의 TrainingArgumentBuilder(문자열 반환, WPF 용)와 같은 스크립트 규약을 따른다.
/// </summary>
public static class TrainingArgumentList
{
    public sealed record Request(
        TrainingScript Script,
        string ScriptPath,
        string DatasetDir,
        string OutputDir,
        string? PretrainedPath,
        int Seed,
        IReadOnlyDictionary<string, string> Hyperparams,
        bool ExportOnnx = true,
        string? Device = null);

    public static List<string> Build(Request r)
    {
        var args = new List<string> { r.ScriptPath };
        var inv = CultureInfo.InvariantCulture;

        // ppocr 의 --target 은 위치 무관하지만 가독성을 위해 앞에
        if (r.Hyperparams.TryGetValue("target", out var target))
            args.AddRange(["--target", target]);

        args.AddRange(["--dataset", NormalizeDir(r.DatasetDir)]);
        args.AddRange(["--output", NormalizeDir(r.OutputDir)]);

        foreach (var spec in HyperparamWhitelist.For(r.Script))
        {
            if (spec.Key == "target") continue;
            if (!r.Hyperparams.TryGetValue(spec.Key, out var value)) continue;
            args.AddRange([spec.CliFlag, value]);
        }

        if (!string.IsNullOrEmpty(r.PretrainedPath))
            args.AddRange(["--pretrained", r.PretrainedPath]);

        if (SupportsSeed(r.Script))
            args.AddRange(["--seed", r.Seed.ToString(inv)]);

        if (!string.IsNullOrEmpty(r.Device) && SupportsDevice(r.Script))
            args.AddRange(["--device", r.Device]);

        if (r.ExportOnnx) args.Add("--export_onnx");
        return args;
    }

    /// <summary>--seed 를 받는 스크립트 (train_dfine.py). 나머지는 스크립트 갱신 시 추가.</summary>
    public static bool SupportsSeed(TrainingScript s) => s is TrainingScript.TrainDfine or TrainingScript.TrainRfdetrSeg;
    public static bool SupportsDevice(TrainingScript s) => s is TrainingScript.TrainDfine or TrainingScript.TrainRfdetrSeg;

    /// <summary>드라이브 루트(D:\) → "D:\." (VMS.Core.Contracts TrainingArgumentBuilder.NormalizeDir 와 동일 규칙)</summary>
    public static string NormalizeDir(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.Length == 3 && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
            return path[0] + ":\\.";
        return path.TrimEnd('\\', '/');
    }
}
