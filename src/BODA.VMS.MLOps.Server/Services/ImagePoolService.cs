using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Core.Hashing;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Data;
using BODA.VMS.MLOps.Server.Data.Entities;
using BODA.VMS.MLOps.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>
/// 이미지 풀 (개발 문서 §5.2). 업로드·검색·중복 제거를 맡는다.
/// 같은 파일(SHA-256)은 한 벌만 저장하고, 거의 같은 사진은 dHash 로 찾아 알려 준다.
/// 데이터셋에 담는 것은 <see cref="DatasetService"/> 가 참조로만 건다.
/// </summary>
public sealed class ImagePoolService(
    MlopsDbContext db, IArtifactStorage storage, ImageProcessor processor, AuditService audit,
    IOptions<MlopsOptions> options, TimeProvider clock, ILogger<ImagePoolService> logger)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public sealed record UploadResult(Image Image, bool Created, string? NearDuplicateOf);

    /// <summary>
    /// 이미지 한 장을 풀에 넣는다. 이미 있는 파일이면 그 레코드를 그대로 돌려준다(멱등).
    /// 거의 같은 사진이 이미 있으면 등록은 하되 어느 것과 닮았는지 알려 준다 — 지울지는 사람이 정한다.
    /// </summary>
    public async Task<UploadResult> UploadAsync(
        Stream content, string fileName, ImageSource source, string? lineId, string? inspectionId,
        string[]? tags, DateTime? capturedAt, CurrentUser user, CancellationToken ct)
    {
        if (!ImageProcessor.IsSupportedExtension(fileName))
            throw ApiException.BadRequest(ErrorCodes.UnsupportedFormat,
                $"지원하지 않는 확장자입니다: {Path.GetExtension(fileName)} (jpg·png·bmp·webp·gif)");

        var temp = await TempFileWriter.WriteAsync(storage, content, options.Value.MaxImageBytes,
            Path.GetExtension(fileName), ct);
        try
        {
            var existing = await db.Images.FirstOrDefaultAsync(i => i.Sha256 == temp.Sha256, ct);
            if (existing is not null)
            {
                // 같은 사진을 다시 올린 것 — 태그만 합쳐 준다
                if (tags is { Length: > 0 }) MergeTags(existing, tags);
                await db.SaveChangesAsync(ct);
                return new UploadResult(existing, false, null);
            }

            ProcessedImage processed;
            try
            {
                processed = processor.Process(temp.TempPath);
            }
            catch (ImageProcessor.UnsupportedImageException ex)
            {
                throw ApiException.BadRequest(ErrorCodes.UnsupportedFormat, ex.Message);
            }

            var originalKey = StorageKeys.Image(temp.Sha256, Path.GetExtension(fileName));
            var thumbKey = StorageKeys.Thumbnail(temp.Sha256);
            var viewKey = processed.View is null ? originalKey : StorageKeys.View(temp.Sha256);

            await storage.CommitTempAsync(temp.TempPath, originalKey, ct);
            await storage.SaveAsync(thumbKey, new MemoryStream(processed.Thumbnail), ct);
            if (processed.View is not null)
                await storage.SaveAsync(viewKey, new MemoryStream(processed.View), ct);

            var image = new Image
            {
                Id = Guid.NewGuid(),
                Sha256 = temp.Sha256,
                StorageKey = originalKey,
                ThumbnailKey = thumbKey,
                ViewKey = viewKey,
                FileName = StorageKeys.SafeName(fileName),
                ContentType = ImageProcessor.ContentTypeFor(fileName),
                Width = processed.Width,
                Height = processed.Height,
                SizeBytes = temp.SizeBytes,
                Source = source,
                LineId = lineId,
                InspectionId = inspectionId,
                TagsJson = Mapping.ToJson(NormalizeTags(tags)),
                PerceptualHash = processed.PerceptualHash,
                CapturedAt = capturedAt,
                CreatedBy = user.Name,
                CreatedAt = Now,
            };
            db.Images.Add(image);
            await db.SaveChangesAsync(ct);

            var near = await FindNearDuplicateAsync(image, ct);
            return new UploadResult(image, true, near);
        }
        finally
        {
            TempFileWriter.TryDelete(temp.TempPath);
        }
    }

    /// <summary>
    /// 거의 같은 사진을 찾는다. dHash 는 앞자리가 같아도 뒷자리가 다를 수 있어 인덱스로 좁힐 수 없으므로,
    /// 최근 것부터 정해진 개수만 비교한다. 전수 비교는 풀이 커지면 감당이 안 된다.
    /// </summary>
    private async Task<string?> FindNearDuplicateAsync(Image image, CancellationToken ct)
    {
        if (image.PerceptualHash is null) return null;
        var mine = PerceptualHash.FromHex(image.PerceptualHash);

        var candidates = await db.Images.AsNoTracking()
            .Where(i => i.Id != image.Id && i.PerceptualHash != null)
            .OrderByDescending(i => i.CreatedAt)
            .Take(2000)
            .Select(i => new { i.Id, i.PerceptualHash, i.FileName })
            .ToListAsync(ct);

        foreach (var c in candidates)
            if (PerceptualHash.IsNearDuplicate(mine, PerceptualHash.FromHex(c.PerceptualHash)))
                return c.FileName;
        return null;
    }

    /// <summary>풀 안에서 거의 같은 사진끼리 묶는다 (개발 문서 §5.2 중복 제거)</summary>
    public async Task<List<List<Guid>>> FindDuplicateGroupsAsync(int take, CancellationToken ct)
    {
        var images = await db.Images.AsNoTracking()
            .Where(i => i.PerceptualHash != null)
            .OrderByDescending(i => i.CreatedAt)
            .Take(Math.Clamp(take, 1, 5000))
            .Select(i => new { i.Id, i.PerceptualHash })
            .ToListAsync(ct);

        var hashes = images.Select(i => (i.Id, Hash: PerceptualHash.FromHex(i.PerceptualHash))).ToList();
        var groups = new List<List<Guid>>();
        var seen = new HashSet<Guid>();

        for (int i = 0; i < hashes.Count; i++)
        {
            if (!seen.Add(hashes[i].Id)) continue;
            var group = new List<Guid> { hashes[i].Id };
            for (int j = i + 1; j < hashes.Count; j++)
            {
                if (seen.Contains(hashes[j].Id)) continue;
                if (!PerceptualHash.IsNearDuplicate(hashes[i].Hash, hashes[j].Hash)) continue;
                group.Add(hashes[j].Id);
                seen.Add(hashes[j].Id);
            }
            if (group.Count > 1) groups.Add(group);
        }
        return groups;
    }

    public sealed record ImageQuery(
        Guid? DatasetId, bool? InDataset, ImageSource? Source, string? LineId, string? Tag,
        string? Search, LabelStatus? LabelStatus, int Skip, int Take);

    public async Task<(List<Image> Items, int Total)> ListAsync(ImageQuery q, CancellationToken ct)
    {
        var query = db.Images.AsNoTracking().AsQueryable();

        if (q.Source is not null) query = query.Where(i => i.Source == q.Source);
        if (!string.IsNullOrWhiteSpace(q.LineId)) query = query.Where(i => i.LineId == q.LineId);
        if (!string.IsNullOrWhiteSpace(q.Search)) query = query.Where(i => i.FileName.Contains(q.Search));
        // 태그는 JSON 문자열이라 정확 일치가 어려워 부분 일치로 좁히고 뒤에서 걸러낸다
        if (!string.IsNullOrWhiteSpace(q.Tag)) query = query.Where(i => i.TagsJson.Contains(q.Tag));

        if (q.DatasetId is { } datasetId)
        {
            var member = db.DatasetImages.Where(m => m.DatasetId == datasetId).Select(m => m.ImageId);
            query = q.InDataset == false ? query.Where(i => !member.Contains(i.Id)) : query.Where(i => member.Contains(i.Id));

            if (q.LabelStatus is { } status)
            {
                var withStatus = db.ImageLabelStates
                    .Where(s => s.DatasetId == datasetId && s.Status == status).Select(s => s.ImageId);
                query = status == Core.Domain.LabelStatus.Unlabeled
                    // 상태 레코드가 아예 없는 것도 미라벨로 본다
                    ? query.Where(i => withStatus.Contains(i.Id) ||
                        !db.ImageLabelStates.Any(s => s.DatasetId == datasetId && s.ImageId == i.Id))
                    : query.Where(i => withStatus.Contains(i.Id));
            }
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(i => i.CreatedAt)
            .Skip(Math.Max(0, q.Skip)).Take(Math.Clamp(q.Take, 1, 500)).ToListAsync(ct);

        if (!string.IsNullOrWhiteSpace(q.Tag))
            items = items.Where(i => Mapping.Json(i.TagsJson, Array.Empty<string>())
                .Contains(q.Tag, StringComparer.OrdinalIgnoreCase)).ToList();

        return (items, total);
    }

    public async Task<Image> GetAsync(Guid id, CancellationToken ct) =>
        await db.Images.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw ApiException.NotFound("이미지");

    public async Task<(Stream Stream, string ContentType, string FileName, string ETag)> OpenAsync(
        Guid id, string variant, CancellationToken ct)
    {
        var image = await GetAsync(id, ct);
        var (key, type, suffix) = variant.ToLowerInvariant() switch
        {
            "thumb" or "thumbnail" => (image.ThumbnailKey, "image/jpeg", "-thumb.jpg"),
            "view" => (image.ViewKey, image.ViewKey == image.StorageKey ? image.ContentType : "image/jpeg", "-view.jpg"),
            _ => (image.StorageKey, image.ContentType, Path.GetExtension(image.FileName)),
        };
        if (!storage.Exists(key)) throw ApiException.NotFound("이미지 파일");
        // 내용 주소 지정이라 파일이 바뀌지 않는다 — 브라우저가 마음껏 캐시해도 된다
        return (storage.OpenRead(key), type, Path.GetFileNameWithoutExtension(image.FileName) + suffix, $"\"{image.Sha256}-{variant}\"");
    }

    public async Task SetTagsAsync(Guid[] imageIds, string[] add, string[] remove, CurrentUser user, CancellationToken ct)
    {
        var images = await db.Images.Where(i => imageIds.Contains(i.Id)).ToListAsync(ct);
        foreach (var image in images)
        {
            var tags = Mapping.Json(image.TagsJson, Array.Empty<string>()).ToList();
            tags.RemoveAll(t => remove.Contains(t, StringComparer.OrdinalIgnoreCase));
            foreach (var t in NormalizeTags(add))
                if (!tags.Contains(t, StringComparer.OrdinalIgnoreCase)) tags.Add(t);
            image.TagsJson = Mapping.ToJson(tags);
        }
        audit.Record(AuditService.Dataset, "ImagesTagged", user.Name, null, new { count = images.Count, add, remove });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 풀에서 지운다. 데이터셋 버전에 굳어 있는 이미지는 막는다 — 스냅샷이 재현되지 않기 때문이다.
    /// 저장 파일은 같은 해시를 쓰는 다른 레코드가 없을 때만 지운다.
    /// </summary>
    public async Task<int> DeleteAsync(Guid[] imageIds, CurrentUser user, CancellationToken ct)
    {
        var images = await db.Images.Where(i => imageIds.Contains(i.Id)).ToListAsync(ct);
        if (images.Count == 0) return 0;

        var frozen = await db.DatasetVersions.AnyAsync(v => v.Source == DatasetVersionSource.Snapshot, ct);
        if (frozen)
        {
            var used = await UsedInSnapshotsAsync(images.Select(i => i.Id).ToList(), ct);
            if (used.Count > 0)
                throw ApiException.Conflict(ErrorCodes.Validation,
                    $"데이터셋 버전에 포함된 이미지는 지울 수 없습니다 ({used.Count}장). 버전을 먼저 지우세요.");
        }

        var ids = images.Select(i => i.Id).ToList();
        db.DatasetImages.RemoveRange(db.DatasetImages.Where(m => ids.Contains(m.ImageId)));
        db.Annotations.RemoveRange(db.Annotations.Where(a => ids.Contains(a.ImageId)));
        db.ImageLabelStates.RemoveRange(db.ImageLabelStates.Where(s => ids.Contains(s.ImageId)));
        db.Images.RemoveRange(images);
        audit.Record(AuditService.Dataset, "ImagesDeleted", user.Name, null, new { count = images.Count });
        await db.SaveChangesAsync(ct);

        foreach (var image in images)
        {
            try
            {
                await storage.DeleteAsync(image.StorageKey, ct);
                await storage.DeleteAsync(image.ThumbnailKey, ct);
                if (image.ViewKey != image.StorageKey) await storage.DeleteAsync(image.ViewKey, ct);
            }
            catch (Exception ex) { logger.LogWarning(ex, "이미지 파일 삭제 실패 {Key}", image.StorageKey); }
        }
        return images.Count;
    }

    /// <summary>스냅샷 매니페스트에 들어 있는 이미지 id 를 걸러낸다</summary>
    private async Task<List<Guid>> UsedInSnapshotsAsync(List<Guid> imageIds, CancellationToken ct)
    {
        var manifestKeys = await db.DatasetVersions.AsNoTracking()
            .Where(v => v.Source == DatasetVersionSource.Snapshot && v.ManifestKey != null)
            .Select(v => v.ManifestKey!).ToListAsync(ct);

        var used = new List<Guid>();
        foreach (var key in manifestKeys)
        {
            if (!storage.Exists(key)) continue;
            string text;
            using (var reader = new StreamReader(storage.OpenRead(key))) text = await reader.ReadToEndAsync(ct);
            foreach (var id in imageIds)
                if (text.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase) && !used.Contains(id)) used.Add(id);
        }
        return used;
    }

    private static void MergeTags(Image image, string[] tags)
    {
        var current = Mapping.Json(image.TagsJson, Array.Empty<string>()).ToList();
        foreach (var t in NormalizeTags(tags))
            if (!current.Contains(t, StringComparer.OrdinalIgnoreCase)) current.Add(t);
        image.TagsJson = Mapping.ToJson(current);
    }

    public static string[] NormalizeTags(string[]? tags) =>
        (tags ?? []).Select(t => t.Trim()).Where(t => t.Length is > 0 and <= 60).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
