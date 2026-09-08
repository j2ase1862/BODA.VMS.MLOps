using BODA.VMS.MLOps.Contracts.Datasets;
using BODA.VMS.MLOps.Server.Data;
using BODA.VMS.MLOps.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>
/// 이미지 목록에 데이터셋 안에서의 상태(분할·라벨 상태·잠금)를 붙인다.
/// 한 장씩 조회하면 목록 한 페이지에 수십 번의 왕복이 되므로 한 번에 모아 읽는다.
/// </summary>
public sealed class DatasetQueryService(MlopsDbContext db, TimeProvider clock)
{
    public async Task<List<ImageDto>> DecorateAsync(List<Image> images, Guid? datasetId, CancellationToken ct)
    {
        if (images.Count == 0) return [];
        if (datasetId is not { } id) return images.Select(i => i.ToDto()).ToList();

        var imageIds = images.Select(i => i.Id).ToList();
        var splits = await db.DatasetImages.AsNoTracking()
            .Where(m => m.DatasetId == id && imageIds.Contains(m.ImageId))
            .ToDictionaryAsync(m => m.ImageId, m => m.Split, ct);
        var states = await db.ImageLabelStates.AsNoTracking()
            .Where(s => s.DatasetId == id && imageIds.Contains(s.ImageId))
            .ToDictionaryAsync(s => s.ImageId, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        return images.Select(i =>
        {
            var state = states.GetValueOrDefault(i.Id);
            bool locked = state?.LockExpiresAt is { } expires && expires > now;
            return i.ToDto(
                state?.Status ?? Core.Domain.LabelStatus.Unlabeled,
                splits.TryGetValue(i.Id, out var split) ? split : null,
                state?.AnnotationCount ?? 0,
                locked ? state!.LockedBy : null);
        }).ToList();
    }

    public async Task<List<ImageDto>> ByIdsAsync(IReadOnlyList<Guid> imageIds, CancellationToken ct)
    {
        var images = await db.Images.AsNoTracking().Where(i => imageIds.Contains(i.Id)).ToListAsync(ct);
        // 요청한 순서를 지킨다 (중복 묶음은 순서가 뜻을 갖는다)
        var byId = images.ToDictionary(i => i.Id);
        return imageIds.Where(byId.ContainsKey).Select(id => byId[id].ToDto()).ToList();
    }
}
