using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Core.Onnx;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Data;
using BODA.VMS.MLOps.Server.Data.Entities;
using BODA.VMS.MLOps.Server.Hubs;
using BODA.VMS.MLOps.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>모델 레지스트리 — Model/ModelVersion CRUD, 업로드 검증 파이프라인(Phase 1 §4), 스테이지 승격(§3 제약), 참조 해석</summary>
public sealed class ModelRegistryService(
    MlopsDbContext db, IArtifactStorage storage, AuditService audit, IMlopsNotifier notifier,
    IOptions<MlopsOptions> options, TimeProvider clock, ILogger<ModelRegistryService> logger)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ───────────── Model ─────────────

    public async Task<ModelDto> CreateModelAsync(CreateModelRequest req, CurrentUser user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw ApiException.BadRequest(ErrorCodes.Validation, "모델 이름은 필수입니다.");
        var classes = NormalizeClasses(req.Classes);
        if (classes.Length == 0)
            throw ApiException.BadRequest(ErrorCodes.Validation, "클래스 목록은 1개 이상이어야 합니다.");

        var model = new Model
        {
            Id = Guid.NewGuid(),
            Name = req.Name.Trim(),
            TaskType = req.TaskType,
            ClassesJson = Mapping.ToJson(classes),
            Description = req.Description,
            CreatedBy = user.Name,
            CreatedAt = Now,
        };
        db.Models.Add(model);
        audit.Record(AuditService.Model, "Created", user.Name, model.Id.ToString(), new { model.Name, model.TaskType, classes });
        await db.SaveChangesAsync(ct);
        return model.ToDto(0);
    }

    public async Task<List<ModelDto>> ListModelsAsync(TaskType? taskType, bool archived, CancellationToken ct)
    {
        var q = db.Models.Include(m => m.Versions).AsNoTracking().Where(m => m.IsArchived == archived);
        if (taskType is not null) q = q.Where(m => m.TaskType == taskType);
        var models = await q.OrderBy(m => m.Name).ToListAsync(ct);
        var ids = models.Select(m => m.Id).ToList();
        var bindingCounts = await db.ModelBindings.Where(b => b.IsActive && ids.Contains(b.ModelId))
            .GroupBy(b => b.ModelId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return models.Select(m => m.ToDto(bindingCounts.GetValueOrDefault(m.Id))).ToList();
    }

    public async Task<ModelDto> GetModelAsync(Guid id, CancellationToken ct)
    {
        var model = await db.Models.Include(m => m.Versions).AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct)
                    ?? throw ApiException.NotFound("모델");
        var count = await db.ModelBindings.CountAsync(b => b.IsActive && b.ModelId == id, ct);
        return model.ToDto(count);
    }

    public async Task<ModelDto> UpdateModelAsync(Guid id, UpdateModelRequest req, CurrentUser user, CancellationToken ct)
    {
        var model = await db.Models.Include(m => m.Versions).FirstOrDefaultAsync(m => m.Id == id, ct)
                    ?? throw ApiException.NotFound("모델");
        if (!string.IsNullOrWhiteSpace(req.Name)) model.Name = req.Name.Trim();
        if (req.Description is not null) model.Description = req.Description;
        if (req.IsArchived is not null) model.IsArchived = req.IsArchived.Value;
        audit.Record(AuditService.Model, "Updated", user.Name, id.ToString(), req);
        await db.SaveChangesAsync(ct);
        var count = await db.ModelBindings.CountAsync(b => b.IsActive && b.ModelId == id, ct);
        return model.ToDto(count);
    }

    // ───────────── Versions ─────────────

    public async Task<List<ModelVersionDto>> ListVersionsAsync(Guid modelId, CancellationToken ct)
    {
        if (!await db.Models.AnyAsync(m => m.Id == modelId, ct)) throw ApiException.NotFound("모델");
        var versions = await db.ModelVersions.AsNoTracking().Where(v => v.ModelId == modelId).OrderByDescending(v => v.Number).ToListAsync(ct);
        return versions.Select(v => v.ToDto()).ToList();
    }

    public async Task<ModelVersionDto> GetVersionAsync(Guid versionId, CancellationToken ct)
    {
        var v = await db.ModelVersions.Include(x => x.StageHistory).AsNoTracking().FirstOrDefaultAsync(x => x.Id == versionId, ct)
                ?? throw ApiException.NotFound("모델 버전");
        return v.ToDto(includeHistory: true);
    }

    /// <summary>
    /// 업로드 검증 파이프라인 (Phase 1 §4):
    /// 스트리밍 저장+SHA → 중복이면 기존 반환(멱등) → protobuf 구조 → 규약 판별 → names/imgsz 필수(폼 보충) →
    /// yolo 라이선스 게이트 → Model 의 TaskType/Classes 일치 → Candidate 생성 + 감사.
    /// </summary>
    public async Task<(ModelVersionDto Version, bool Created)> UploadVersionAsync(
        Guid modelId, Stream content, VersionUploadMeta? meta, VersionSource source, Guid? trainingJobId, CurrentUser user, CancellationToken ct)
    {
        var model = await db.Models.FirstOrDefaultAsync(m => m.Id == modelId, ct) ?? throw ApiException.NotFound("모델");

        var temp = await TempFileWriter.WriteAsync(storage, content, options.Value.MaxModelBytes, ".onnx", ct);
        try
        {
            // 멱등: 같은 계열에 같은 파일을 다시 올리면 기존 버전을 돌려준다 (다른 계열이면 새 버전).
            var existing = await db.ModelVersions.Include(v => v.StageHistory)
                .FirstOrDefaultAsync(v => v.ModelId == modelId && v.Sha256 == temp.Sha256, ct);
            if (existing is not null)
            {
                logger.LogInformation("중복 ONNX 업로드 — 기존 버전 반환 {Sha}", temp.Sha256);
                return (existing.ToDto(true), false);
            }

            var inspection = OnnxModelInspector.Inspect(temp.TempPath);
            if (!inspection.IsValidProtobuf)
                throw ApiException.BadRequest(ErrorCodes.UnsupportedFormat, "ONNX(protobuf) 구조를 읽을 수 없습니다.", inspection.Warnings);

            var classes = inspection.Classes ?? (meta?.Classes is { Length: > 0 } ? NormalizeClasses(meta.Classes) : null);
            if (classes is null || classes.Length == 0)
                throw ApiException.BadRequest(ErrorCodes.MissingNames, "ONNX metadata_props 'names' 가 없습니다. 클래스 목록(meta.classes)을 함께 보내세요.");

            var inputSize = inspection.InputSize ?? meta?.InputSize;
            if (inputSize is null && RequiresInputSize(inspection.Format))
                throw ApiException.BadRequest(ErrorCodes.MissingInputSize, "ONNX metadata_props 'imgsz' 가 없습니다. 입력 크기(meta.inputSize)를 함께 보내세요.");

            if (inspection.Format == ModelFormat.Yolo && string.IsNullOrWhiteSpace(meta?.License))
                throw ApiException.BadRequest(ErrorCodes.LicenseRequired,
                    "Ultralytics YOLO 파생 모델입니다 (AGPL-3.0). Enterprise License 보유 여부를 meta.license 에 기입해야 등록됩니다.");

            if (!FormatMatchesTaskType(inspection.Format, model.TaskType))
                throw ApiException.BadRequest(ErrorCodes.TaskTypeMismatch,
                    $"모델 규약 {inspection.Format} 은(는) 작업 유형 {model.TaskType} 과 맞지 않습니다.");

            var modelClasses = Mapping.Json(model.ClassesJson, Array.Empty<string>());
            if (!modelClasses.SequenceEqual(classes, StringComparer.Ordinal))
                throw ApiException.BadRequest(ErrorCodes.ClassMismatch,
                    "클래스 목록(수·이름·순서)이 모델 계열과 다릅니다.",
                    [$"model: [{string.Join(", ", modelClasses)}]", $"onnx: [{string.Join(", ", classes)}]"]);

            var key = StorageKeys.Model(temp.Sha256);
            await storage.CommitTempAsync(temp.TempPath, key, ct);

            var number = (await db.ModelVersions.Where(v => v.ModelId == modelId).MaxAsync(v => (int?)v.Number, ct) ?? 0) + 1;
            var version = new ModelVersion
            {
                Id = Guid.NewGuid(),
                ModelId = modelId,
                Number = number,
                Sha256 = temp.Sha256,
                ArtifactKey = key,
                SizeBytes = temp.SizeBytes,
                Format = inspection.Format,
                InputSize = inputSize,
                ClassesJson = Mapping.ToJson(classes),
                MetadataJson = Mapping.ToJson(inspection.Metadata),
                MetricsJson = Mapping.ToJson(meta?.Metrics ?? new Dictionary<string, double>()),
                Source = source,
                TrainingJobId = trainingJobId,
                License = meta?.License,
                Stage = ModelStage.Candidate,
                Notes = meta?.Notes,
                CreatedBy = user.Name,
                CreatedAt = Now,
                WarningsJson = Mapping.ToJson(inspection.Warnings),
            };
            db.ModelVersions.Add(version);
            audit.Record(AuditService.Model, "VersionCreated", user.Name, version.Id.ToString(),
                new { modelId, number, version.Sha256, version.Format, source, trainingJobId, version.License });
            await db.SaveChangesAsync(ct);

            var dto = version.ToDto(true);
            await notifier.VersionCreated(dto);
            return (dto, true);
        }
        finally
        {
            TempFileWriter.TryDelete(temp.TempPath);
        }
    }

    /// <summary>스테이지 승격/강등 (Phase 1 §3, §5, §8). Production·Retired 는 Admin, yolo→Production 은 라이선스 확인 필수.</summary>
    public async Task<ModelVersionDto> PromoteAsync(Guid versionId, PromoteRequest req, CurrentUser user, CancellationToken ct)
    {
        var version = await db.ModelVersions.Include(v => v.StageHistory).FirstOrDefaultAsync(v => v.Id == versionId, ct)
                      ?? throw ApiException.NotFound("모델 버전");
        var from = version.Stage;
        var to = req.Stage;

        if (!CanTransition(from, to))
            throw ApiException.Conflict(ErrorCodes.InvalidStageTransition, $"스테이지 전이 불가: {from} → {to}");

        bool needsAdmin = to is ModelStage.Production or ModelStage.Retired || from == ModelStage.Production;
        if (needsAdmin && !user.IsAdmin)
            throw ApiException.Forbidden($"{to} 전이는 Admin 권한이 필요합니다.");
        if (!user.IsEngineer)
            throw ApiException.Forbidden("Engineer 이상 권한이 필요합니다.");

        if (to == ModelStage.Production && version.Format == ModelFormat.Yolo && !req.ConfirmLicense)
            throw ApiException.BadRequest(ErrorCodes.LicenseRequired, "YOLO 파생 모델의 Production 승격은 라이선스 확인(confirmLicense=true)이 필요합니다.");

        var now = Now;
        if (to == ModelStage.Production)
        {
            // Model 당 Production 은 최대 1개 — 이전 Production 은 Staging 으로 강등 (롤백 편의)
            var previous = await db.ModelVersions
                .Where(v => v.ModelId == version.ModelId && v.Stage == ModelStage.Production && v.Id != version.Id).ToListAsync(ct);
            foreach (var p in previous)
            {
                p.Stage = ModelStage.Staging;
                p.StageChangedBy = user.Name;
                p.StageChangedAt = now;
                db.ModelStageHistories.Add(new ModelStageHistory
                {
                    Id = Guid.NewGuid(), ModelVersionId = p.Id, FromStage = ModelStage.Production, ToStage = ModelStage.Staging,
                    ChangedBy = user.Name, ChangedAt = now, Reason = $"v{version.Number} Production 승격으로 교체",
                });
                audit.Record(AuditService.Model, "Promoted", user.Name, p.Id.ToString(), new { from = ModelStage.Production, to = ModelStage.Staging, reason = "replaced" });
            }
        }

        version.Stage = to;
        version.StageChangedBy = user.Name;
        version.StageChangedAt = now;
        db.ModelStageHistories.Add(new ModelStageHistory
        {
            Id = Guid.NewGuid(), ModelVersionId = version.Id, FromStage = from, ToStage = to,
            ChangedBy = user.Name, ChangedAt = now, Reason = req.Reason,
        });
        audit.Record(AuditService.Model, "Promoted", user.Name, version.Id.ToString(), new { from, to, req.Reason, req.ConfirmLicense });
        await db.SaveChangesAsync(ct);

        await db.Entry(version).Collection(v => v.StageHistory).LoadAsync(ct);
        var dto = version.ToDto(true);
        await notifier.StagePromoted(dto);
        return dto;
    }

    /// <summary>라인 PC 의 model:// 참조 해석 — stage=production(기본) 또는 version=N</summary>
    public async Task<ResolveResponse> ResolveAsync(Guid modelId, string? stage, int? number, CancellationToken ct)
    {
        if (!await db.Models.AnyAsync(m => m.Id == modelId, ct)) throw ApiException.NotFound("모델");
        ModelVersion? v;
        if (number is not null)
            v = await db.ModelVersions.AsNoTracking().FirstOrDefaultAsync(x => x.ModelId == modelId && x.Number == number, ct);
        else
        {
            var target = ParseStage(stage ?? ModelReference.ProductionTag);
            v = await db.ModelVersions.AsNoTracking().Where(x => x.ModelId == modelId && x.Stage == target)
                .OrderByDescending(x => x.Number).FirstOrDefaultAsync(ct);
        }
        if (v is null) throw ApiException.NotFound(number is not null ? $"버전 {number}" : $"{stage ?? "production"} 스테이지 버전");
        return new ResolveResponse(modelId, v.Id, v.Number, v.Sha256, v.SizeBytes, v.Stage, Mapping.ArtifactUrl(v.Id));
    }

    public async Task<(Stream Stream, ModelVersion Version)> OpenArtifactAsync(Guid versionId, CancellationToken ct)
    {
        var v = await db.ModelVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == versionId, ct)
                ?? throw ApiException.NotFound("모델 버전");
        if (!storage.Exists(v.ArtifactKey)) throw ApiException.NotFound("아티팩트 파일");
        return (storage.OpenRead(v.ArtifactKey), v);
    }

    // ───────────── 규칙 ─────────────

    public static bool CanTransition(ModelStage from, ModelStage to) => (from, to) switch
    {
        (ModelStage.Candidate, ModelStage.Staging) => true,
        (ModelStage.Staging, ModelStage.Production) => true,
        (ModelStage.Production, ModelStage.Staging) => true,
        (ModelStage.Staging, ModelStage.Candidate) => true,
        (_, ModelStage.Retired) => from != ModelStage.Retired,
        _ => false,
    };

    public static bool RequiresInputSize(ModelFormat f) =>
        f is ModelFormat.DFine or ModelFormat.Yolo or ModelFormat.YoloSeg or ModelFormat.Classifier;

    public static bool FormatMatchesTaskType(ModelFormat f, TaskType t) => f switch
    {
        ModelFormat.Unknown => true,
        ModelFormat.DFine or ModelFormat.Yolo => t == TaskType.Detection,
        ModelFormat.YoloSeg => t == TaskType.Segmentation,
        ModelFormat.RfdetrSeg => t == TaskType.Segmentation,
        ModelFormat.Classifier => t == TaskType.Classification,
        ModelFormat.Anomaly => t == TaskType.Anomaly,
        ModelFormat.PpOcr => t == TaskType.Ocr,
        _ => false,
    };

    public static ModelStage ParseStage(string s) =>
        Enum.TryParse<ModelStage>(s, ignoreCase: true, out var st) ? st
            : throw ApiException.BadRequest(ErrorCodes.Validation, $"알 수 없는 스테이지: {s}");

    public static string[] NormalizeClasses(IEnumerable<string>? classes) =>
        (classes ?? []).Select(c => c.Trim()).Where(c => c.Length > 0).ToArray();
}
