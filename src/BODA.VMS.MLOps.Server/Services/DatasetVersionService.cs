using System.IO.Compression;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Datasets;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Data;
using BODA.VMS.MLOps.Server.Data.Entities;
using BODA.VMS.MLOps.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>
/// Phase 2 선행 최소 구현: 내보내기 zip 을 올리면 데이터셋 버전이 된다 (manifestHash = zip SHA-256).
/// Phase 2 에서 이미지 풀·라벨·스냅샷 생성이 이 위에 얹히며, 워커가 쓰는 export API 규약은 그대로 유지한다.
/// </summary>
public sealed class DatasetVersionService(
    MlopsDbContext db, IArtifactStorage storage, AuditService audit, IOptions<MlopsOptions> options, TimeProvider clock)
{
    public static readonly string[] Formats = ["yolo", "imagefolder", "mvtec", "ppocr", "coco"];

    public async Task<DatasetVersionDto> UploadAsync(DatasetVersionUploadMeta meta, Stream zip, CurrentUser user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(meta.Name))
            throw ApiException.BadRequest(ErrorCodes.Validation, "데이터셋 이름은 필수입니다.");
        var format = (meta.ExportFormat ?? "").Trim().ToLowerInvariant();
        if (!Formats.Contains(format))
            throw ApiException.BadRequest(ErrorCodes.Validation, $"exportFormat 은 {string.Join("|", Formats)} 중 하나여야 합니다.");

        var temp = await TempFileWriter.WriteAsync(storage, zip, options.Value.MaxDatasetBytes, ".zip", ct);
        try
        {
            ValidateZip(temp.TempPath, options.Value.MaxDatasetBytes);
            var id = Guid.NewGuid();
            var key = StorageKeys.Dataset(id);
            await storage.CommitTempAsync(temp.TempPath, key, ct);

            var dv = new DatasetVersion
            {
                Id = id, Name = meta.Name.Trim(), TaskType = meta.TaskType, ExportFormat = format,
                ManifestHash = temp.Sha256, StorageKey = key, SizeBytes = temp.SizeBytes, ImageCount = meta.ImageCount,
                ClassesJson = Mapping.ToJson(meta.Classes ?? []), CreatedBy = user.Name, CreatedAt = clock.GetUtcNow().UtcDateTime,
            };
            db.DatasetVersions.Add(dv);
            audit.Record(AuditService.Dataset, "VersionCreated", user.Name, id.ToString(), new { dv.Name, dv.TaskType, format, dv.ManifestHash, dv.SizeBytes });
            await db.SaveChangesAsync(ct);
            return dv.ToDto();
        }
        finally { TempFileWriter.TryDelete(temp.TempPath); }
    }

    public async Task<List<DatasetVersionDto>> ListAsync(CancellationToken ct) =>
        (await db.DatasetVersions.AsNoTracking().OrderByDescending(d => d.CreatedAt).ToListAsync(ct)).Select(d => d.ToDto()).ToList();

    public async Task<DatasetVersionDto> GetAsync(Guid id, CancellationToken ct) =>
        (await db.DatasetVersions.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw ApiException.NotFound("데이터셋 버전")).ToDto();

    public async Task<(Stream Stream, DatasetVersion Version)> OpenExportAsync(Guid id, string? format, CancellationToken ct)
    {
        var dv = await db.DatasetVersions.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw ApiException.NotFound("데이터셋 버전");
        if (!string.IsNullOrWhiteSpace(format) && !format.Equals(dv.ExportFormat, StringComparison.OrdinalIgnoreCase))
            throw ApiException.BadRequest(ErrorCodes.Validation, $"이 버전은 {dv.ExportFormat} 형식으로만 내보낼 수 있습니다 (요청: {format}).");
        if (!storage.Exists(dv.StorageKey)) throw ApiException.NotFound("데이터셋 파일");
        return (storage.OpenRead(dv.StorageKey), dv);
    }

    /// <summary>
    /// zip 구조·경로 탈출(..)·절대 경로 항목 거부 (Phase 3 §8) + 해제 후 총 크기 상한.
    /// 여기서 걸러야 워커가 압축 폭탄을 받아 디스크를 채우는 일이 없다 (워커도 해제하며 실제 바이트를 다시 센다).
    /// </summary>
    private static void ValidateZip(string path, long maxUncompressedBytes)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count == 0)
                throw ApiException.BadRequest(ErrorCodes.Validation, "빈 zip 입니다.");
            long total = 0;
            foreach (var e in archive.Entries)
            {
                var n = e.FullName.Replace('\\', '/');
                if (n.StartsWith('/') || n.Contains("../") || n.Contains("/..") || n == ".." || Path.IsPathRooted(n))
                    throw ApiException.BadRequest(ErrorCodes.Validation, $"zip 항목 경로가 허용되지 않습니다: {e.FullName}");
                total += e.Length;
                if (total > maxUncompressedBytes)
                    throw ApiException.TooLarge($"zip 해제 크기가 상한({maxUncompressedBytes / (1024 * 1024)}MB)을 넘습니다.");
            }
        }
        catch (InvalidDataException)
        {
            throw ApiException.BadRequest(ErrorCodes.UnsupportedFormat, "zip 파일이 아닙니다.");
        }
    }
}
