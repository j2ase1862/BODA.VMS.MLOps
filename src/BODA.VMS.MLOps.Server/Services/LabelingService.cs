using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Core.Labeling;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Data;
using BODA.VMS.MLOps.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>
/// 라벨 저장과 이미지 단위 잠금 (개발 문서 §5.4).
///
/// 잠금은 만료 시각으로 관리한다. 브라우저가 그냥 닫히거나 PC 가 꺼져도
/// 정해진 시간이 지나면 다른 사람이 이어받을 수 있어야 하기 때문이다.
/// 저장할 때마다 잠금이 연장되므로, 작업 중인 이미지를 남이 가져가지는 않는다.
/// </summary>
public sealed class LabelingService(
    MlopsDbContext db, AuditService audit, IOptions<MlopsOptions> options, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private TimeSpan LockDuration => TimeSpan.FromMinutes(Math.Max(1, options.Value.LabelLockMinutes));

    public sealed record ImageLabels(
        Guid DatasetId, Guid ImageId, IReadOnlyList<LabelAnnotation> Annotations,
        LabelStatus Status, string? LockedBy, DateTime? LockExpiresAt, bool LockedByMe,
        string? LabeledBy, DateTime? LabeledAt, string? ReviewedBy, DateTime? ReviewedAt);

    public async Task<ImageLabels> GetAsync(Guid datasetId, Guid imageId, CurrentUser user, CancellationToken ct)
    {
        await EnsureMemberAsync(datasetId, imageId, ct);
        var annotations = await db.Annotations.AsNoTracking()
            .Where(a => a.DatasetId == datasetId && a.ImageId == imageId)
            .OrderBy(a => a.CreatedAt).ToListAsync(ct);
        var state = await db.ImageLabelStates.AsNoTracking()
            .FirstOrDefaultAsync(s => s.DatasetId == datasetId && s.ImageId == imageId, ct);
        return ToLabels(datasetId, imageId, annotations, state, user);
    }

    /// <summary>
    /// 라벨을 통째로 갈아 끼운다. 부분 수정 대신 전체 저장을 쓰는 이유는
    /// 캔버스가 항상 이미지 한 장의 라벨 전부를 들고 있고, 그 편이 충돌을 판단하기 쉽기 때문이다.
    /// </summary>
    public async Task<ImageLabels> SaveAsync(Guid datasetId, Guid imageId, IReadOnlyList<LabelAnnotation> annotations,
        bool markLabeled, CurrentUser user, CancellationToken ct)
    {
        var dataset = await db.Datasets.AsNoTracking().FirstOrDefaultAsync(d => d.Id == datasetId, ct)
                      ?? throw ApiException.NotFound("데이터셋");
        await EnsureMemberAsync(datasetId, imageId, ct);

        var classes = DatasetService.ClassesOf(dataset);
        var allowedShapes = dataset.TaskType.AllowedFor();
        var errors = new List<string>();

        for (int i = 0; i < annotations.Count; i++)
        {
            var a = annotations[i];
            if (!a.IsValid(out var error)) errors.Add($"[{i}] {error}");
            else if (!allowedShapes.Contains(a.Shape))
                errors.Add($"[{i}] {dataset.TaskType} 데이터셋에는 {a.Shape} 도형을 쓸 수 없습니다 (허용: {string.Join(", ", allowedShapes)}).");
            else if (!classes.Contains(a.ClassName, StringComparer.Ordinal))
                errors.Add($"[{i}] 데이터셋에 없는 클래스입니다: {a.ClassName}");
        }
        // 분류·이상탐지는 이미지 한 장에 클래스 하나다
        if (allowedShapes.Contains(AnnotationShape.Classification) && annotations.Count > 1)
            errors.Add("이 작업 유형은 이미지당 클래스 하나만 붙일 수 있습니다.");

        if (errors.Count > 0)
            throw ApiException.BadRequest(ErrorCodes.Validation, "라벨을 저장할 수 없습니다.", errors);

        var state = await LoadOrCreateStateAsync(datasetId, imageId, ct);
        EnsureNotLockedByOthers(state, user);

        var existing = await db.Annotations.Where(a => a.DatasetId == datasetId && a.ImageId == imageId).ToListAsync(ct);
        db.Annotations.RemoveRange(existing);
        foreach (var a in annotations)
        {
            db.Annotations.Add(new Annotation
            {
                Id = Guid.NewGuid(), DatasetId = datasetId, ImageId = imageId,
                Shape = a.Shape, ClassName = a.ClassName, PayloadJson = AnnotationPayload.ToJson(a),
                CreatedBy = user.Name, CreatedAt = Now, UpdatedBy = user.Name, UpdatedAt = Now,
            });
        }

        state.AnnotationCount = annotations.Count;
        // 검토까지 끝난 이미지를 다시 고치면 검토 상태는 풀린다 — 바뀐 라벨은 다시 봐야 한다
        if (state.Status == LabelStatus.Reviewed) { state.ReviewedBy = null; state.ReviewedAt = null; }
        state.Status = markLabeled ? LabelStatus.Labeled : annotations.Count > 0 ? LabelStatus.InProgress : LabelStatus.Unlabeled;
        if (markLabeled) { state.LabeledBy = user.Name; state.LabeledAt = Now; }
        // 저장은 곧 작업 중이라는 뜻이므로 잠금을 연장한다
        state.LockedBy = user.Name;
        state.LockedAt = Now;
        state.LockExpiresAt = Now + LockDuration;

        await SaveStateAsync(ct);
        audit.Record(AuditService.Dataset, "LabelsSaved", user.Name, imageId.ToString(),
            new { datasetId, count = annotations.Count, markLabeled });
        await db.SaveChangesAsync(ct);

        return ToLabels(datasetId, imageId,
            await db.Annotations.AsNoTracking().Where(a => a.DatasetId == datasetId && a.ImageId == imageId).ToListAsync(ct),
            state, user);
    }

    /// <summary>잠금을 잡거나 연장한다. 남이 잡고 있으면 409.</summary>
    public async Task<ImageLabels> LockAsync(Guid datasetId, Guid imageId, CurrentUser user, CancellationToken ct)
    {
        await EnsureMemberAsync(datasetId, imageId, ct);
        var state = await LoadOrCreateStateAsync(datasetId, imageId, ct);
        EnsureNotLockedByOthers(state, user);

        state.LockedBy = user.Name;
        state.LockedAt = Now;
        state.LockExpiresAt = Now + LockDuration;
        await SaveStateAsync(ct);
        await db.SaveChangesAsync(ct);

        var annotations = await db.Annotations.AsNoTracking()
            .Where(a => a.DatasetId == datasetId && a.ImageId == imageId).ToListAsync(ct);
        return ToLabels(datasetId, imageId, annotations, state, user);
    }

    public async Task UnlockAsync(Guid datasetId, Guid imageId, CurrentUser user, CancellationToken ct)
    {
        var state = await db.ImageLabelStates.FirstOrDefaultAsync(s => s.DatasetId == datasetId && s.ImageId == imageId, ct);
        if (state is null) return;
        // 남의 잠금은 Admin 만 풀 수 있다
        if (IsLockedByOther(state, user) && !user.IsAdmin)
            throw ApiException.Conflict(ErrorCodes.Validation, $"{state.LockedBy} 가 작업 중입니다.");
        state.LockedBy = null;
        state.LockedAt = null;
        state.LockExpiresAt = null;
        await SaveStateAsync(ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ImageLabels> ReviewAsync(Guid datasetId, Guid imageId, bool approved, CurrentUser user, CancellationToken ct)
    {
        if (!user.IsEngineer)
            throw ApiException.Forbidden("검토는 Engineer 이상 권한이 필요합니다.");
        await EnsureMemberAsync(datasetId, imageId, ct);

        var state = await LoadOrCreateStateAsync(datasetId, imageId, ct);
        if (approved)
        {
            if (state.Status is LabelStatus.Unlabeled)
                throw ApiException.Conflict(ErrorCodes.Validation, "라벨이 없는 이미지는 검토할 수 없습니다.");
            state.Status = LabelStatus.Reviewed;
            state.ReviewedBy = user.Name;
            state.ReviewedAt = Now;
        }
        else
        {
            // 반려하면 다시 라벨링 대상으로 돌린다
            state.Status = state.AnnotationCount > 0 ? LabelStatus.InProgress : LabelStatus.Unlabeled;
            state.ReviewedBy = null;
            state.ReviewedAt = null;
        }
        await SaveStateAsync(ct);
        audit.Record(AuditService.Dataset, approved ? "LabelApproved" : "LabelRejected", user.Name, imageId.ToString(), new { datasetId });
        await db.SaveChangesAsync(ct);

        var annotations = await db.Annotations.AsNoTracking()
            .Where(a => a.DatasetId == datasetId && a.ImageId == imageId).ToListAsync(ct);
        return ToLabels(datasetId, imageId, annotations, state, user);
    }

    /// <summary>
    /// 다음에 라벨링할 이미지. 불확실도가 매겨져 있으면 큰 것부터 (Active Learning),
    /// 없으면 담긴 순서대로. 남이 잠근 것은 건너뛴다.
    /// </summary>
    public async Task<Guid?> NextToLabelAsync(Guid datasetId, Guid? afterImageId, CurrentUser user, CancellationToken ct)
    {
        var members = await db.DatasetImages.AsNoTracking().Where(m => m.DatasetId == datasetId)
            .Select(m => m.ImageId).ToListAsync(ct);
        if (members.Count == 0) return null;

        var states = await db.ImageLabelStates.AsNoTracking().Where(s => s.DatasetId == datasetId).ToListAsync(ct);
        var byImage = states.ToDictionary(s => s.ImageId);
        var now = Now;

        var candidates = members
            .Where(id => id != afterImageId)
            .Select(id => (Id: id, State: byImage.GetValueOrDefault(id)))
            .Where(x => x.State is null || x.State.Status is LabelStatus.Unlabeled or LabelStatus.InProgress)
            .Where(x => x.State is null || x.State.LockedBy is null || x.State.LockedBy == user.Name
                        || x.State.LockExpiresAt is null || x.State.LockExpiresAt <= now)
            .OrderByDescending(x => x.State?.Uncertainty ?? -1)
            .ThenBy(x => x.Id.ToString(), StringComparer.Ordinal)
            .ToList();

        return candidates.Count > 0 ? candidates[0].Id : null;
    }

    /// <summary>후보 모델의 추론 결과를 초기 라벨로 채워 넣는다 (개발 문서 §5.4 Active Learning)</summary>
    public async Task<int> PrefillAsync(Guid datasetId, IReadOnlyDictionary<Guid, IReadOnlyList<LabelAnnotation>> predictions,
        IReadOnlyDictionary<Guid, double>? uncertainty, CurrentUser user, CancellationToken ct)
    {
        var dataset = await db.Datasets.AsNoTracking().FirstOrDefaultAsync(d => d.Id == datasetId, ct)
                      ?? throw ApiException.NotFound("데이터셋");
        var classes = DatasetService.ClassesOf(dataset);
        var members = await db.DatasetImages.AsNoTracking().Where(m => m.DatasetId == datasetId)
            .Select(m => m.ImageId).ToListAsync(ct);

        int filled = 0;
        foreach (var (imageId, annotations) in predictions)
        {
            if (!members.Contains(imageId)) continue;
            var state = await LoadOrCreateStateAsync(datasetId, imageId, ct);
            // 사람이 이미 손댄 이미지는 건드리지 않는다
            if (state.Status is LabelStatus.Labeled or LabelStatus.Reviewed) continue;
            if (state.AnnotationCount > 0) continue;

            var valid = annotations.Where(a => a.IsValid(out _) && classes.Contains(a.ClassName, StringComparer.Ordinal)).ToList();
            foreach (var a in valid)
            {
                db.Annotations.Add(new Annotation
                {
                    Id = Guid.NewGuid(), DatasetId = datasetId, ImageId = imageId,
                    Shape = a.Shape, ClassName = a.ClassName, PayloadJson = AnnotationPayload.ToJson(a),
                    CreatedBy = $"model:{user.Name}", CreatedAt = Now,
                });
            }
            state.AnnotationCount = valid.Count;
            if (valid.Count > 0) state.Status = LabelStatus.InProgress;
            if (uncertainty?.TryGetValue(imageId, out var u) == true) state.Uncertainty = u;
            filled++;
        }

        audit.Record(AuditService.Dataset, "LabelsPrefilled", user.Name, datasetId.ToString(), new { filled });
        await db.SaveChangesAsync(ct);
        return filled;
    }

    // ───────────── 하부 ─────────────

    private async Task EnsureMemberAsync(Guid datasetId, Guid imageId, CancellationToken ct)
    {
        if (!await db.DatasetImages.AnyAsync(m => m.DatasetId == datasetId && m.ImageId == imageId, ct))
            throw ApiException.NotFound("데이터셋에 담긴 이미지");
    }

    private async Task<ImageLabelState> LoadOrCreateStateAsync(Guid datasetId, Guid imageId, CancellationToken ct)
    {
        var state = await db.ImageLabelStates.FirstOrDefaultAsync(s => s.DatasetId == datasetId && s.ImageId == imageId, ct);
        if (state is not null) return state;

        state = new ImageLabelState
        {
            Id = Guid.NewGuid(), DatasetId = datasetId, ImageId = imageId, Status = LabelStatus.Unlabeled,
        };
        db.ImageLabelStates.Add(state);
        return state;
    }

    /// <summary>동시 저장 충돌은 409 로 알린다 — 두 사람이 같은 이미지를 저장하는 상황</summary>
    private async Task SaveStateAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw ApiException.Conflict(ErrorCodes.Validation, "다른 사람이 방금 이 이미지를 저장했습니다. 새로 고친 뒤 다시 시도하세요.");
        }
    }

    private bool IsLockedByOther(ImageLabelState state, CurrentUser user) =>
        state.LockedBy is not null && state.LockedBy != user.Name
        && state.LockExpiresAt is { } expires && expires > Now;

    private void EnsureNotLockedByOthers(ImageLabelState state, CurrentUser user)
    {
        if (IsLockedByOther(state, user))
            throw ApiException.Conflict(ErrorCodes.Validation,
                $"{state.LockedBy} 가 작업 중입니다 ({state.LockExpiresAt:HH:mm} 까지).");
    }

    private ImageLabels ToLabels(Guid datasetId, Guid imageId, List<Annotation> annotations, ImageLabelState? state, CurrentUser user)
    {
        var parsed = annotations
            .Select(a => AnnotationPayload.FromJson(a.Shape, a.ClassName, a.PayloadJson))
            .Where(a => a is not null).Select(a => a!).ToList();

        bool lockActive = state?.LockExpiresAt is { } expires && expires > Now;
        return new ImageLabels(datasetId, imageId, parsed,
            state?.Status ?? LabelStatus.Unlabeled,
            lockActive ? state!.LockedBy : null,
            lockActive ? state!.LockExpiresAt : null,
            lockActive && state!.LockedBy == user.Name,
            state?.LabeledBy, state?.LabeledAt, state?.ReviewedBy, state?.ReviewedAt);
    }
}
