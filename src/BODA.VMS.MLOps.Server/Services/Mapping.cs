using System.Text.Json;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Datasets;
using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Contracts.Training;
using BODA.VMS.MLOps.Contracts.Workers;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Server.Data.Entities;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>엔티티 ↔ DTO. JSON 컬럼 역직렬화 실패는 빈 값으로 흡수한다.</summary>
public static class Mapping
{
    public static T Json<T>(string? json, T fallback)
    {
        if (string.IsNullOrWhiteSpace(json)) return fallback;
        try { return JsonSerializer.Deserialize<T>(json, MlopsJson.Options) ?? fallback; }
        catch (JsonException) { return fallback; }
    }

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, MlopsJson.Options);

    public static ModelVersionSummary ToSummary(this ModelVersion v) =>
        new(v.Id, v.Number, v.Stage, v.Format, v.Sha256, v.CreatedAt);

    public static ModelDto ToDto(this Model m, int activeBindingCount)
    {
        var versions = m.Versions.OrderByDescending(v => v.Number).ToList();
        return new ModelDto(m.Id, m.Name, m.TaskType, Json(m.ClassesJson, Array.Empty<string>()), m.Description,
            m.CreatedBy, m.CreatedAt, m.IsArchived,
            versions.FirstOrDefault()?.ToSummary(),
            versions.FirstOrDefault(v => v.Stage == ModelStage.Production)?.ToSummary(),
            versions.Count, activeBindingCount);
    }

    public static ModelVersionDto ToDto(this ModelVersion v, bool includeHistory = false) =>
        new(v.Id, v.ModelId, v.Number, v.Sha256, v.SizeBytes, v.Format, v.InputSize,
            Json(v.ClassesJson, Array.Empty<string>()),
            Json(v.MetadataJson, new Dictionary<string, string>()),
            Json(v.MetricsJson, new Dictionary<string, double>()),
            v.Source, v.TrainingJobId, v.License, v.Stage, v.StageChangedBy, v.StageChangedAt, v.Notes,
            v.CreatedBy, v.CreatedAt, v.ValidationStatus, Json(v.WarningsJson, Array.Empty<string>()),
            includeHistory
                ? v.StageHistory.OrderBy(h => h.ChangedAt).Select(h => new StageHistoryDto(h.FromStage, h.ToStage, h.ChangedBy, h.ChangedAt, h.Reason)).ToList()
                : null);

    public static WorkerDto ToDto(this Worker w) =>
        new(w.Id, w.Name, w.MachineName, w.Status, w.DisabledReason, w.LastHeartbeatAt,
            Json<WorkerCapabilities?>(w.CapabilitiesJson, null),
            Json(w.TaskTypesJson, Array.Empty<TaskType>()), w.MaxConcurrent, w.CurrentJobId, w.CreatedAt, w.WorkerVersion);

    public static TrainingJobDto ToDto(this TrainingJob j) =>
        new(j.Id, j.ProjectId, j.DatasetVersionId, j.ModelId, j.TaskType, j.Script, j.Backbone, j.PretrainedRef,
            Json(j.HyperparamsJson, new Dictionary<string, string>()), j.Seed, j.State, j.Priority, j.WorkerId,
            j.AssignedAt, j.StartedAt, j.FinishedAt, j.Attempt, j.MaxAttempts, j.Progress, j.CurrentEpoch, j.TotalEpochs,
            j.LastLoss, j.LastMetric, j.Error, j.FailureKind, j.ResultModelVersionId,
            Json<ReproducibilityRecord?>(j.ReproducibilityJson, null), j.CreatedBy, j.CreatedAt, j.CancelRequested);

    public static DatasetVersionDto ToDto(this DatasetVersion d) =>
        new(d.Id, d.Name, d.TaskType, d.ExportFormat, d.ManifestHash, d.SizeBytes, d.ImageCount,
            Json(d.ClassesJson, Array.Empty<string>()), d.CreatedBy, d.CreatedAt,
            $"/api/dataset-versions/{d.Id}/export?format={d.ExportFormat}",
            d.Source, d.DatasetId, d.AnnotationCount,
            Json(d.SplitCountsJson, new Dictionary<string, int>()),
            // 스냅샷은 처음 내보낼 때 zip 이 만들어지므로, 아직 없으면 "만들어야 함" 으로 알린다
            d.StorageKey is not null);

    public static JobLogLineDto ToDto(this JobLogChunk c) => new(c.Seq, c.Level, c.Text, c.At);

    public static string ArtifactUrl(Guid versionId) => $"/api/model-versions/{versionId}/artifact";
}
