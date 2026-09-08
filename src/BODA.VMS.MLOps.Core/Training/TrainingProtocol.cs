using VMS.Core.Models.Annotation;
using VMS.Core.Services;

namespace BODA.VMS.MLOps.Core.Training;

/// <summary>워커가 stdout 한 줄에서 얻는 이벤트. VMS.Core.Contracts 의 TrainingOutputParser 를 감싼다 (부록 A 프로토콜).</summary>
public sealed record TrainingLineEvent(TrainingOutputKind Kind, string Line,
    int CurrentEpoch, int TotalEpochs, double Progress, double Loss, double Metric, string? OnnxPath, string? Error);

/// <summary>프로토콜 파서 상태 보관 + 한 줄 해석. 스레드 안전하지 않다(프로세스당 1개).</summary>
public sealed class TrainingProtocolTracker
{
    private readonly TrainingStatus _status = new();

    public int CurrentEpoch => _status.CurrentEpoch;
    public int TotalEpochs => _status.TotalEpochs;
    public double Progress => _status.Progress;
    public double Loss => _status.Loss;
    public double Metric => _status.Accuracy;
    public string? OnnxPath => string.IsNullOrEmpty(_status.OnnxOutputPath) ? null : _status.OnnxOutputPath;
    public string? LastError { get; private set; }
    public bool SawDone { get; private set; }

    public TrainingLineEvent Apply(string line)
    {
        var kind = TrainingOutputParser.Apply(line, _status);
        if (kind == TrainingOutputKind.Error) LastError = _status.Message;
        if (kind == TrainingOutputKind.Done) SawDone = true;
        return new TrainingLineEvent(kind, line, _status.CurrentEpoch, _status.TotalEpochs, _status.Progress,
            _status.Loss, _status.Accuracy, OnnxPath, kind == TrainingOutputKind.Error ? LastError : null);
    }

    /// <summary>GPU 메모리 부족 — 로그에서 감지하면 batch 반감 재시도 1회 (Phase 3 §5.2)</summary>
    public static bool IsCudaOom(string line) =>
        line.Contains("CUDA out of memory", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("CUDA error: out of memory", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("OutOfMemoryError", StringComparison.OrdinalIgnoreCase);
}
