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
                Sharpness = processed.Quality.Sharpness,
                MeanLuma = processed.Quality.MeanLuma,
                ClippedDarkRatio = processed.Quality.ClippedDarkRatio,
                ClippedBrightRatio = processed.Quality.ClippedBrightRatio,
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

    public sealed record CropOutcome(Guid SourceImageId, string SourceFileName, Image? Image, bool Created, string? Error);

    /// <summary>
    /// 고른 사진들을 같은 자리(ROI)로 잘라 새 사진으로 담는다.
    ///
    /// <para><b>원본은 그대로 둔다.</b> 자른 것이 원본을 대신하는 것이 아니라 나란히 쌓인다 —
    /// ROI 를 잘못 잡았을 때 되돌릴 데가 있어야 하고, 무엇을 지울지는 사람이 정한다.</para>
    ///
    /// <para>출처·라인·검사 이력·태그는 <b>원본에서 물려받는다</b>. 라인 NG 사진을 잘랐다면
    /// 그것은 여전히 그 라인의 그 검사에서 나온 사진이다 — 끊으면 나중에 사고를 되짚을 때
    /// 자른 사진만 출처 없이 떠 있게 된다.</para>
    ///
    /// <para>한 장씩 독립적으로 처리하고 실패한 것만 이유를 돌려준다. 열 장 중 한 장이
    /// 깨졌다고 나머지 아홉 장을 버릴 이유가 없다 (업로드와 같은 규칙).</para>
    /// </summary>
    public async Task<List<CropOutcome>> CropAsync(
        Guid[] imageIds, double x, double y, double w, double h,
        CurrentUser user, CancellationToken ct)
    {
        if (imageIds.Length == 0)
            throw ApiException.BadRequest(ErrorCodes.Validation, "자를 사진을 고르세요.");
        if (imageIds.Length > options.Value.MaxCropImages)
            throw ApiException.BadRequest(ErrorCodes.Validation,
                $"한 번에 {options.Value.MaxCropImages}장까지 자를 수 있습니다. 나눠서 하세요.");

        // 좌표 규약은 어디서나 0~1 정규화다 (README "라벨 좌표"). 여기만 픽셀로 받으면
        // 해상도가 다른 사진에 같은 ROI 를 못 쓴다 — 이 기능의 핵심이 그것이다.
        if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(w) || double.IsNaN(h))
            throw ApiException.BadRequest(ErrorCodes.Validation, "자를 영역이 올바르지 않습니다.");
        if (w <= 0 || h <= 0 || x < 0 || y < 0 || x + w > 1.0001 || y + h > 1.0001)
            throw ApiException.BadRequest(ErrorCodes.Validation,
                "자를 영역은 사진 안의 0~1 범위여야 합니다.");

        var images = await db.Images.AsNoTracking().Where(i => imageIds.Contains(i.Id)).ToListAsync(ct);
        var byId = images.ToDictionary(i => i.Id);
        var roiJson = Mapping.ToJson(new[] { x, y, w, h });
        var results = new List<CropOutcome>(imageIds.Length);

        foreach (var id in imageIds)
        {
            if (!byId.TryGetValue(id, out var source))
            {
                results.Add(new CropOutcome(id, "", null, false, "사진을 찾을 수 없습니다."));
                continue;
            }
            try
            {
                results.Add(await CropOneAsync(source, x, y, w, h, roiJson, user, ct));
            }
            catch (ApiException ex)
            {
                results.Add(new CropOutcome(id, source.FileName, null, false, ex.Message));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "ROI 자르기 실패 {ImageId}", id);
                results.Add(new CropOutcome(id, source.FileName, null, false, "자르지 못했습니다."));
            }
        }

        audit.Record(AuditService.Dataset, "ImagesCropped", user.Name, null,
            new { requested = imageIds.Length, created = results.Count(r => r.Created), roi = new[] { x, y, w, h } });
        await db.SaveChangesAsync(ct);
        return results;
    }

    private async Task<CropOutcome> CropOneAsync(Image source, double x, double y, double w, double h,
        string roiJson, CurrentUser user, CancellationToken ct)
    {
        // 잘라낸 변이 한 화소도 안 되면 만들 것이 없다. 사진마다 해상도가 달라
        // 같은 ROI 라도 어떤 사진에서는 너무 작을 수 있으므로 장마다 본다.
        if ((int)Math.Round(w * source.Width) < 1 || (int)Math.Round(h * source.Height) < 1)
            return new CropOutcome(source.Id, source.FileName, null, false,
                $"이 사진({source.Width}×{source.Height})에서는 자를 영역이 1화소보다 작습니다.");

        var (localPath, scratch) = await LocalCopyAsync(source.StorageKey, ct);
        try
        {
            if (processor.Crop(localPath, x, y, w, h, lossless: true) is not { } bytes)
                return new CropOutcome(source.Id, source.FileName, null, false, "사진을 읽지 못했습니다.");

            var name = $"{Path.GetFileNameWithoutExtension(source.FileName)}_roi.png";
            using var stream = new MemoryStream(bytes);
            var result = await UploadAsync(stream, name, source.Source, source.LineId, source.InspectionId,
                Mapping.Json(source.TagsJson, Array.Empty<string>()), source.CapturedAt, user, ct);

            // 같은 ROI 로 같은 사진을 두 번 자르면 내용이 같아 한 벌로 합쳐진다 (내용 주소 저장).
            // 그때 온 것은 먼저 만들어진 행이므로 출처를 덮어쓰지 않는다.
            if (result.Created)
            {
                var tracked = await db.Images.FirstAsync(i => i.Id == result.Image.Id, ct);
                tracked.SourceImageId = source.Id;
                tracked.RoiJson = roiJson;
                await db.SaveChangesAsync(ct);
            }
            return new CropOutcome(source.Id, source.FileName, result.Image, result.Created, null);
        }
        finally
        {
            if (scratch) TempFileWriter.TryDelete(localPath);
        }
    }

    /// <summary>
    /// 저장소가 로컬이면 그 파일을 그대로 쓰고, 아니면 임시 파일로 받아 온다.
    /// SkiaSharp 에 경로를 넘겨야 해서(스트림 디코드는 큰 사진에서 메모리를 두 배로 쓴다) 필요한 단계다.
    /// </summary>
    private async Task<(string Path, bool Scratch)> LocalCopyAsync(string key, CancellationToken ct)
    {
        if (storage.LocalPath(key) is { } local && File.Exists(local)) return (local, false);

        var temp = storage.CreateTempPath(".bin");
        await using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var reader = storage.OpenRead(key))
            await reader.CopyToAsync(target, ct);
        return (temp, true);
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
        string? Search, LabelStatus? LabelStatus, int Skip, int Take,
        /// <summary>이 값보다 흐린 것만. 지표가 없는 옛 이미지는 빠진다 (흐린지 알 수 없어서다).</summary>
        double? MaxSharpness = null,
        /// <summary>날아간 화소가 이 비율을 넘는 것만. 0~1.</summary>
        double? MinClippedRatio = null,
        /// <summary>흐린 것부터 본다. 사람이 눈으로 확인할 후보를 앞으로 끌어오는 용도.</summary>
        bool BlurriestFirst = false);

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

        // 품질로 좁힌다. 지표가 없는 이미지(이 값이 생기기 전에 올라온 것)는 빼는데,
        // 넣으면 "흐린 것 보기" 에 멀쩡한 옛 사진이 섞여 사람이 그것부터 지우게 된다.
        if (q.MaxSharpness is { } maxSharp)
            query = query.Where(i => i.Sharpness != null && i.Sharpness <= maxSharp);
        if (q.MinClippedRatio is { } minClipped)
            query = query.Where(i => i.ClippedDarkRatio != null && i.ClippedBrightRatio != null
                                     && i.ClippedDarkRatio + i.ClippedBrightRatio >= minClipped);

        var total = await query.CountAsync(ct);

        // 흐린 것부터 볼 때도 지표가 없는 것은 뒤로 보낸다 — null 이 0 으로 정렬되면 맨 앞을 차지한다.
        // 한 번에 올린 사진들은 CreatedAt 이 같으므로 Id 로 한 번 더 가른다. 갈라 두지 않으면
        // 쪽을 넘길 때 순서가 흔들려 같은 사진이 두 번 나오거나 빠지고, 라벨링 화면의 자리표와도 어긋난다.
        var ordered = q.BlurriestFirst
            ? query.OrderBy(i => i.Sharpness == null).ThenBy(i => i.Sharpness)
                   .ThenByDescending(i => i.CreatedAt).ThenBy(i => i.Id)
            : query.OrderByDescending(i => i.CreatedAt).ThenBy(i => i.Id);

        var items = await ordered
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
        // 자른 사진은 원본을 붙잡지 않는다 — 그 자체로 온전한 학습 재료다.
        // 대신 끊어진 연결을 남기지 않게 지우기 전에 풀어 준다.
        foreach (var child in await db.Images.Where(i => i.SourceImageId != null
                                                        && imageIds.Contains(i.SourceImageId.Value)).ToListAsync(ct))
            child.SourceImageId = null;

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
