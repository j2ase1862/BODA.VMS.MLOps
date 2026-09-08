using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Data;
using BODA.VMS.MLOps.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>
/// 데이터셋 (개발 문서 §5.2). 이미지를 참조로만 담고, 분할을 정하고, 라벨 진행 상황을 센다.
/// 작업 유형과 클래스는 생성 후 바꾸지 않는다 — 이미 붙은 라벨의 뜻이 달라지기 때문이다.
/// </summary>
public sealed class DatasetService(MlopsDbContext db, AuditService audit, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<Dataset> CreateAsync(string name, TaskType taskType, string[] classes, string? description,
        CurrentUser user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw ApiException.BadRequest(ErrorCodes.Validation, "데이터셋 이름은 필수입니다.");

        var normalized = ModelRegistryService.NormalizeClasses(classes);
        if (normalized.Length == 0)
            throw ApiException.BadRequest(ErrorCodes.Validation, "클래스 목록은 1개 이상이어야 합니다.");
        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw ApiException.BadRequest(ErrorCodes.Validation, "클래스 이름이 중복됩니다.");
        if (taskType == TaskType.Anomaly && !normalized.Any(Core.Export.DatasetExportWriter.IsNormalClass))
            throw ApiException.BadRequest(ErrorCodes.Validation,
                "이상탐지 데이터셋에는 정상 클래스가 필요합니다 (good·normal·ok·정상 중 하나).");

        var dataset = new Dataset
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            TaskType = taskType,
            ClassesJson = Mapping.ToJson(normalized),
            Description = description,
            CreatedBy = user.Name,
            CreatedAt = Now,
        };
        db.Datasets.Add(dataset);
        audit.Record(AuditService.Dataset, "Created", user.Name, dataset.Id.ToString(), new { dataset.Name, taskType, classes = normalized });
        await db.SaveChangesAsync(ct);
        return dataset;
    }

    public async Task<Dataset> GetAsync(Guid id, CancellationToken ct) =>
        await db.Datasets.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw ApiException.NotFound("데이터셋");

    public async Task<List<Dataset>> ListAsync(TaskType? taskType, bool archived, CancellationToken ct)
    {
        var q = db.Datasets.AsNoTracking().Where(d => d.IsArchived == archived);
        if (taskType is not null) q = q.Where(d => d.TaskType == taskType);
        return await q.OrderBy(d => d.Name).ToListAsync(ct);
    }

    public async Task<Dataset> UpdateAsync(Guid id, string? name, string? description, bool? archived,
        CurrentUser user, CancellationToken ct)
    {
        var dataset = await db.Datasets.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw ApiException.NotFound("데이터셋");
        if (!string.IsNullOrWhiteSpace(name)) dataset.Name = name.Trim();
        if (description is not null) dataset.Description = description;
        if (archived is not null) dataset.IsArchived = archived.Value;
        audit.Record(AuditService.Dataset, "Updated", user.Name, id.ToString(), new { name, archived });
        await db.SaveChangesAsync(ct);
        return dataset;
    }

    /// <summary>진행 상황 요약 — 라벨링 화면과 스냅샷 버튼이 이 숫자를 보고 판단한다</summary>
    public sealed record DatasetStats(
        int ImageCount, int Labeled, int Reviewed, int Unlabeled, int AnnotationCount,
        int TrainCount, int ValCount, int TestCount, Dictionary<string, int> PerClass);

    public async Task<DatasetStats> StatsAsync(Guid datasetId, CancellationToken ct)
    {
        var members = await db.DatasetImages.AsNoTracking().Where(m => m.DatasetId == datasetId)
            .Select(m => new { m.ImageId, m.Split }).ToListAsync(ct);
        var states = await db.ImageLabelStates.AsNoTracking().Where(s => s.DatasetId == datasetId)
            .Select(s => new { s.ImageId, s.Status }).ToListAsync(ct);
        var perClass = await db.Annotations.AsNoTracking().Where(a => a.DatasetId == datasetId)
            .GroupBy(a => a.ClassName).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);

        var byImage = states.ToDictionary(s => s.ImageId, s => s.Status);
        int labeled = 0, reviewed = 0;
        foreach (var m in members)
        {
            var status = byImage.GetValueOrDefault(m.ImageId, LabelStatus.Unlabeled);
            if (status == LabelStatus.Reviewed) { reviewed++; labeled++; }
            else if (status == LabelStatus.Labeled) labeled++;
        }

        return new DatasetStats(
            members.Count, labeled, reviewed, members.Count - labeled,
            perClass.Sum(p => p.Count),
            members.Count(m => m.Split == DatasetSplit.Train),
            members.Count(m => m.Split == DatasetSplit.Val),
            members.Count(m => m.Split == DatasetSplit.Test),
            perClass.ToDictionary(p => p.Key, p => p.Count));
    }

    /// <summary>이미지를 데이터셋에 담는다. 이미 있는 것은 건너뛴다. 반환은 새로 담긴 수.</summary>
    public async Task<int> AddImagesAsync(Guid datasetId, Guid[] imageIds, DatasetSplit split,
        CurrentUser user, CancellationToken ct)
    {
        _ = await GetAsync(datasetId, ct);
        var existing = await db.DatasetImages.Where(m => m.DatasetId == datasetId && imageIds.Contains(m.ImageId))
            .Select(m => m.ImageId).ToListAsync(ct);
        var valid = await db.Images.Where(i => imageIds.Contains(i.Id)).Select(i => i.Id).ToListAsync(ct);

        var added = 0;
        foreach (var imageId in valid.Except(existing))
        {
            db.DatasetImages.Add(new DatasetImage
            {
                Id = Guid.NewGuid(), DatasetId = datasetId, ImageId = imageId,
                Split = split, AddedBy = user.Name, AddedAt = Now,
            });
            added++;
        }
        audit.Record(AuditService.Dataset, "ImagesAdded", user.Name, datasetId.ToString(), new { requested = imageIds.Length, added, split });
        await db.SaveChangesAsync(ct);
        return added;
    }

    public async Task<int> RemoveImagesAsync(Guid datasetId, Guid[] imageIds, CurrentUser user, CancellationToken ct)
    {
        var members = await db.DatasetImages.Where(m => m.DatasetId == datasetId && imageIds.Contains(m.ImageId)).ToListAsync(ct);
        db.DatasetImages.RemoveRange(members);
        // 이 데이터셋에서 뺐으니 라벨과 상태도 함께 지운다 (이미지 자체는 풀에 남는다)
        db.Annotations.RemoveRange(db.Annotations.Where(a => a.DatasetId == datasetId && imageIds.Contains(a.ImageId)));
        db.ImageLabelStates.RemoveRange(db.ImageLabelStates.Where(s => s.DatasetId == datasetId && imageIds.Contains(s.ImageId)));
        audit.Record(AuditService.Dataset, "ImagesRemoved", user.Name, datasetId.ToString(), new { count = members.Count });
        await db.SaveChangesAsync(ct);
        return members.Count;
    }

    public async Task<int> SetSplitAsync(Guid datasetId, Guid[] imageIds, DatasetSplit split, CurrentUser user, CancellationToken ct)
    {
        var members = await db.DatasetImages.Where(m => m.DatasetId == datasetId && imageIds.Contains(m.ImageId)).ToListAsync(ct);
        foreach (var m in members) m.Split = split;
        audit.Record(AuditService.Dataset, "SplitChanged", user.Name, datasetId.ToString(), new { count = members.Count, split });
        await db.SaveChangesAsync(ct);
        return members.Count;
    }

    /// <summary>
    /// 비율로 자동 분할한다. 라벨이 붙은 순서가 아니라 이미지 id 해시 순으로 나눠,
    /// 같은 데이터셋을 다시 나눠도 결과가 같게 한다 (재현성).
    /// </summary>
    public async Task<Dictionary<DatasetSplit, int>> AutoSplitAsync(
        Guid datasetId, double trainRatio, double valRatio, CurrentUser user, CancellationToken ct)
    {
        if (trainRatio < 0 || valRatio < 0 || trainRatio + valRatio > 1.0001)
            throw ApiException.BadRequest(ErrorCodes.Validation, "train + val 비율은 0~1 사이여야 합니다.");

        var members = await db.DatasetImages.Where(m => m.DatasetId == datasetId).ToListAsync(ct);
        var ordered = members.OrderBy(m => m.ImageId.ToString(), StringComparer.Ordinal).ToList();

        int trainEnd = (int)Math.Round(ordered.Count * trainRatio);
        int valEnd = trainEnd + (int)Math.Round(ordered.Count * valRatio);
        var counts = new Dictionary<DatasetSplit, int> { [DatasetSplit.Train] = 0, [DatasetSplit.Val] = 0, [DatasetSplit.Test] = 0 };

        for (int i = 0; i < ordered.Count; i++)
        {
            var split = i < trainEnd ? DatasetSplit.Train : i < valEnd ? DatasetSplit.Val : DatasetSplit.Test;
            ordered[i].Split = split;
            counts[split]++;
        }
        audit.Record(AuditService.Dataset, "AutoSplit", user.Name, datasetId.ToString(), new { trainRatio, valRatio, counts });
        await db.SaveChangesAsync(ct);
        return counts;
    }

    public async Task DeleteAsync(Guid datasetId, CurrentUser user, CancellationToken ct)
    {
        var dataset = await db.Datasets.FirstOrDefaultAsync(d => d.Id == datasetId, ct) ?? throw ApiException.NotFound("데이터셋");
        if (await db.DatasetVersions.AnyAsync(v => v.DatasetId == datasetId, ct))
            throw ApiException.Conflict(ErrorCodes.Validation, "버전이 있는 데이터셋은 지울 수 없습니다. 버전을 먼저 지우세요.");

        db.DatasetImages.RemoveRange(db.DatasetImages.Where(m => m.DatasetId == datasetId));
        db.Annotations.RemoveRange(db.Annotations.Where(a => a.DatasetId == datasetId));
        db.ImageLabelStates.RemoveRange(db.ImageLabelStates.Where(s => s.DatasetId == datasetId));
        db.Datasets.Remove(dataset);
        audit.Record(AuditService.Dataset, "Deleted", user.Name, datasetId.ToString(), new { dataset.Name });
        await db.SaveChangesAsync(ct);
    }

    public static string[] ClassesOf(Dataset dataset) => Mapping.Json(dataset.ClassesJson, Array.Empty<string>());
}
