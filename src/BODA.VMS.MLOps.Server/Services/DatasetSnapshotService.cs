using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Core.Export;
using BODA.VMS.MLOps.Core.Hashing;
using BODA.VMS.MLOps.Core.Labeling;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Data;
using BODA.VMS.MLOps.Server.Data.Entities;
using BODA.VMS.MLOps.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>
/// 데이터셋을 그 시점 그대로 굳혀 학습이 대상으로 삼을 버전을 만든다 (개발 문서 §5.2).
///
/// 매니페스트(이미지 목록·라벨·분할)만 먼저 굳히고 내보내기 zip 은 처음 요청될 때 만든다.
/// 스냅샷을 떠 놓기만 하고 학습은 안 돌리는 경우가 많아, 그때마다 수 GB 를 미리 만들 이유가 없다.
/// 만든 zip 은 저장해 두므로 워커가 여러 번 받아도 한 번만 만든다.
/// </summary>
public sealed class DatasetSnapshotService(
    MlopsDbContext db, IArtifactStorage storage, ImageProcessor processor, AuditService audit, TimeProvider clock,
    ILogger<DatasetSnapshotService> logger)
{
    private static readonly SemaphoreSlim BuildLock = new(1, 1);
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>스토리지에 굳는 매니페스트. 이미지 id 와 라벨이 여기 박혀 재현성을 만든다.</summary>
    private sealed record ManifestFile(
        string DatasetName, TaskType TaskType, string[] Classes, string ExportFormat,
        DateTime CreatedAt, ManifestEntry[] Images);

    private sealed record ManifestEntry(
        Guid ImageId, string Sha256, string FileName, string Split, int Width, int Height, ManifestLabel[] Labels);

    private sealed record ManifestLabel(
        string Shape, string ClassName, double[]? Box, double[][]? Points, string? Text);

    /// <summary>
    /// 지금의 데이터셋으로 버전을 만든다. 라벨이 붙은 이미지만 담는다
    /// (라벨 없는 이미지는 검출에서는 배경 샘플로 쓸 수 있지만, 실수로 섞이는 쪽이 더 흔하다).
    /// </summary>
    public async Task<DatasetVersion> CreateSnapshotAsync(
        Guid datasetId, string? name, bool includeUnlabeled, bool reviewedOnly, CurrentUser user, CancellationToken ct)
    {
        var dataset = await db.Datasets.AsNoTracking().FirstOrDefaultAsync(d => d.Id == datasetId, ct)
                      ?? throw ApiException.NotFound("데이터셋");
        var classes = DatasetService.ClassesOf(dataset);

        var members = await db.DatasetImages.AsNoTracking().Where(m => m.DatasetId == datasetId)
            .Select(m => new { m.ImageId, m.Split }).ToListAsync(ct);
        if (members.Count == 0)
            throw ApiException.BadRequest(ErrorCodes.Validation, "데이터셋에 이미지가 없습니다.");

        var imageIds = members.Select(m => m.ImageId).ToList();
        var images = await db.Images.AsNoTracking().Where(i => imageIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, ct);
        var annotations = (await db.Annotations.AsNoTracking().Where(a => a.DatasetId == datasetId).ToListAsync(ct))
            .GroupBy(a => a.ImageId).ToDictionary(g => g.Key, g => g.ToList());
        var states = await db.ImageLabelStates.AsNoTracking().Where(s => s.DatasetId == datasetId)
            .ToDictionaryAsync(s => s.ImageId, ct);

        var entries = new List<ManifestEntry>();
        foreach (var m in members.OrderBy(m => m.ImageId.ToString(), StringComparer.Ordinal))
        {
            if (!images.TryGetValue(m.ImageId, out var image)) continue;

            var status = states.GetValueOrDefault(m.ImageId)?.Status ?? LabelStatus.Unlabeled;
            if (reviewedOnly && status != LabelStatus.Reviewed) continue;

            var labels = annotations.GetValueOrDefault(m.ImageId, [])
                .Select(a => AnnotationPayload.FromJson(a.Shape, a.ClassName, a.PayloadJson))
                .Where(a => a is not null).Select(a => a!).ToList();
            if (labels.Count == 0 && !includeUnlabeled) continue;

            entries.Add(new ManifestEntry(
                image.Id, image.Sha256, image.FileName, DatasetExportWriter.SplitDir(m.Split),
                image.Width, image.Height,
                labels.Select(ToManifestLabel).ToArray()));
        }

        if (entries.Count == 0)
            throw ApiException.BadRequest(ErrorCodes.Validation,
                reviewedOnly ? "검토를 마친 이미지가 없습니다." : "라벨이 붙은 이미지가 없습니다.");

        var format = DatasetExportWriter.DefaultFormat(dataset.TaskType);
        var manifest = new ManifestFile(dataset.Name, dataset.TaskType, classes, format, Now, entries.ToArray());
        var json = JsonSerializer.Serialize(manifest, MlopsJson.Options);
        // 해시는 내용에서만 나온다 — 같은 이미지·라벨·분할이면 언제 떠도 같은 값
        var manifestHash = Sha256Util.HashString(json);

        var version = new DatasetVersion
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(name) ? $"{dataset.Name} {Now:yyyy-MM-dd HH:mm}" : name.Trim(),
            TaskType = dataset.TaskType,
            ExportFormat = format,
            ManifestHash = manifestHash,
            ImageCount = entries.Count,
            AnnotationCount = entries.Sum(e => e.Labels.Length),
            ClassesJson = Mapping.ToJson(classes),
            CreatedBy = user.Name,
            CreatedAt = Now,
            Source = DatasetVersionSource.Snapshot,
            DatasetId = datasetId,
            SplitCountsJson = Mapping.ToJson(entries.GroupBy(e => e.Split).ToDictionary(g => g.Key, g => g.Count())),
        };
        version.ManifestKey = StorageKeys.DatasetManifest(version.Id);
        await storage.SaveAsync(version.ManifestKey, new MemoryStream(Encoding.UTF8.GetBytes(json)), ct);

        db.DatasetVersions.Add(version);
        audit.Record(AuditService.Dataset, "VersionCreated", user.Name, version.Id.ToString(),
            new { datasetId, version.Name, version.ManifestHash, version.ImageCount, version.AnnotationCount, reviewedOnly });
        await db.SaveChangesAsync(ct);
        return version;
    }

    /// <summary>
    /// 내보내기 zip 을 연다. 아직 없으면 매니페스트를 보고 만든다.
    /// 동시에 두 워커가 같은 버전을 받아도 한 번만 만들도록 잠근다.
    /// </summary>
    public async Task<(Stream Stream, DatasetVersion Version)> OpenExportAsync(Guid versionId, string? format, CancellationToken ct)
    {
        var version = await db.DatasetVersions.FirstOrDefaultAsync(v => v.Id == versionId, ct)
                      ?? throw ApiException.NotFound("데이터셋 버전");
        if (!string.IsNullOrWhiteSpace(format) && !format.Equals(version.ExportFormat, StringComparison.OrdinalIgnoreCase))
            throw ApiException.BadRequest(ErrorCodes.Validation,
                $"이 버전은 {version.ExportFormat} 형식으로만 내보낼 수 있습니다 (요청: {format}).");

        if (version.StorageKey is { } key && storage.Exists(key))
            return (storage.OpenRead(key), version);

        if (version.Source == DatasetVersionSource.Upload)
            throw ApiException.NotFound("데이터셋 파일");

        await BuildLock.WaitAsync(ct);
        try
        {
            // 기다리는 사이에 다른 요청이 만들었을 수 있다
            await db.Entry(version).ReloadAsync(ct);
            if (version.StorageKey is { } ready && storage.Exists(ready))
                return (storage.OpenRead(ready), version);

            await BuildExportAsync(version, ct);
            await db.SaveChangesAsync(ct);
            return (storage.OpenRead(version.StorageKey!), version);
        }
        finally { BuildLock.Release(); }
    }

    private async Task BuildExportAsync(DatasetVersion version, CancellationToken ct)
    {
        if (version.ManifestKey is null || !storage.Exists(version.ManifestKey))
            throw ApiException.NotFound("데이터셋 매니페스트");

        string json;
        using (var reader = new StreamReader(storage.OpenRead(version.ManifestKey)))
            json = await reader.ReadToEndAsync(ct);
        var manifest = JsonSerializer.Deserialize<ManifestFile>(json, MlopsJson.Options)
                       ?? throw ApiException.NotFound("데이터셋 매니페스트");

        var imageIds = manifest.Images.Select(i => i.ImageId).ToList();
        var images = await db.Images.AsNoTracking().Where(i => imageIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);

        var exportImages = new List<ExportImage>();
        foreach (var entry in manifest.Images)
        {
            var labels = entry.Labels.Select(FromManifestLabel).ToList();
            // OCR 은 텍스트 영역을 잘라 쓰므로 파일명을 크롭 이름으로 바꾼다
            var fileName = manifest.ExportFormat == "ppocr"
                ? $"{Path.GetFileNameWithoutExtension(entry.FileName)}-{entry.ImageId:N}.jpg"
                : UniqueFileName(entry);
            exportImages.Add(new ExportImage(entry.ImageId, entry.Sha256, fileName,
                Enum.Parse<DatasetSplit>(entry.Split, ignoreCase: true), entry.Width, entry.Height, labels));
        }

        var temp = storage.CreateTempPath(".zip");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var sink = new ZipExportSink(archive, images, storage, processor, manifest, logger);
                DatasetExportWriter.Write(
                    new ExportManifest(manifest.DatasetName, manifest.TaskType, manifest.Classes, exportImages),
                    manifest.ExportFormat, sink);
            }

            var key = StorageKeys.Dataset(version.Id);
            await storage.DeleteAsync(key, ct);
            version.SizeBytes = await storage.CommitTempAsync(temp, key, ct);
            version.StorageKey = key;
            logger.LogInformation("데이터셋 버전 {Id} 내보내기 생성 ({Count}장, {Size} bytes)",
                version.Id, exportImages.Count, version.SizeBytes);
        }
        finally { TempFileWriter.TryDelete(temp); }
    }

    /// <summary>같은 파일명이 여러 장 있을 수 있어 id 조각을 붙여 겹치지 않게 한다</summary>
    private static string UniqueFileName(ManifestEntry entry)
    {
        var stem = Path.GetFileNameWithoutExtension(entry.FileName);
        var ext = Path.GetExtension(entry.FileName);
        return $"{stem}-{entry.Sha256[..8]}{(string.IsNullOrEmpty(ext) ? ".jpg" : ext)}";
    }

    private static ManifestLabel ToManifestLabel(LabelAnnotation a) => new(
        a.Shape.ToString(), a.ClassName,
        a.Box is { } b ? [b.X, b.Y, b.Width, b.Height] : null,
        a.Polygon?.Select(p => new[] { p.X, p.Y }).ToArray(),
        a.Text);

    private static LabelAnnotation FromManifestLabel(ManifestLabel l) => new()
    {
        Shape = Enum.Parse<AnnotationShape>(l.Shape, ignoreCase: true),
        ClassName = l.ClassName,
        Box = l.Box is { Length: 4 } b ? new NormBox(b[0], b[1], b[2], b[3]) : null,
        Polygon = l.Points?.Where(p => p.Length >= 2).Select(p => new NormPoint(p[0], p[1])).ToList(),
        Text = l.Text,
    };

    /// <summary>
    /// 내보내기 형식이 정한 경로대로 zip 에 담는다. 이미지는 재인코딩하지 않고 원본을 복사한다.
    /// OCR 만 예외로, 텍스트 영역을 잘라 넣는다 (PP-OCR 인식 학습이 크롭을 쓰기 때문).
    /// </summary>
    private sealed class ZipExportSink(
        ZipArchive archive, Dictionary<Guid, Image> images, IArtifactStorage storage,
        ImageProcessor processor, ManifestFile manifest, ILogger logger) : IExportSink
    {
        private readonly Dictionary<Guid, ManifestEntry> _entries = manifest.Images.ToDictionary(e => e.ImageId);

        public void WriteText(string path, string content)
        {
            var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        public void WriteImage(string path, Guid imageId)
        {
            if (!images.TryGetValue(imageId, out var image))
            {
                logger.LogWarning("내보내기: 이미지 레코드 없음 {Id}", imageId);
                return;
            }
            if (!storage.Exists(image.StorageKey))
            {
                logger.LogWarning("내보내기: 이미지 파일 없음 {Key}", image.StorageKey);
                return;
            }

            // 이미지는 이미 압축돼 있어 다시 압축해도 이득이 없다
            var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
            using var target = entry.Open();

            if (manifest.ExportFormat == "ppocr" && _entries.TryGetValue(imageId, out var e))
            {
                var box = e.Labels.FirstOrDefault(l => l.Box is { Length: 4 })?.Box;
                var local = storage.LocalPath(image.StorageKey);
                if (box is not null && local is not null && processor.Crop(local, box[0], box[1], box[2], box[3]) is { } crop)
                {
                    target.Write(crop, 0, crop.Length);
                    return;
                }
            }

            using var source = storage.OpenRead(image.StorageKey);
            source.CopyTo(target);
        }
    }
}
