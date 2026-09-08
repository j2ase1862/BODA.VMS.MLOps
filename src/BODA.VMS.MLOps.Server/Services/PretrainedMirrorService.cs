using System.Text.RegularExpressions;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Pretrained;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Data;
using BODA.VMS.MLOps.Server.Data.Entities;
using BODA.VMS.MLOps.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>사전학습 가중치 미러 (Phase 3 §3 PretrainedAsset, §4, §9). 워커는 인터넷 대신 여기서만 받는다.</summary>
public sealed partial class PretrainedMirrorService(
    MlopsDbContext db, IArtifactStorage storage, AuditService audit, IOptions<MlopsOptions> options, TimeProvider clock)
{
    public sealed record FileEntry(string FileName, string Sha256, long SizeBytes);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$")]
    private static partial Regex RefPattern();

    public static string ValidateRef(string @ref) =>
        RefPattern().IsMatch(@ref ?? "") ? @ref! : throw ApiException.BadRequest(ErrorCodes.Validation, "ref 는 영숫자·'.'·'_'·'-' 100자 이내여야 합니다.");

    public async Task<List<PretrainedAssetDto>> ListAsync(CancellationToken ct) =>
        (await db.PretrainedAssets.AsNoTracking().OrderBy(a => a.Ref).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<PretrainedAssetDto> GetAsync(string @ref, CancellationToken ct) =>
        ToDto(await db.PretrainedAssets.AsNoTracking().FirstOrDefaultAsync(a => a.Ref == @ref, ct) ?? throw ApiException.NotFound("사전학습 자산"));

    public async Task<PretrainedAssetDto> CreateAsync(CreatePretrainedAssetRequest req, CurrentUser user, CancellationToken ct)
    {
        var r = ValidateRef(req.Ref);
        if (await db.PretrainedAssets.AnyAsync(a => a.Ref == r, ct))
            throw ApiException.Conflict(ErrorCodes.Validation, $"이미 존재하는 ref: {r}");
        var asset = new PretrainedAsset { Ref = r, License = req.License, Source = req.Source, AddedBy = user.Name, AddedAt = clock.GetUtcNow().UtcDateTime };
        db.PretrainedAssets.Add(asset);
        audit.Record(AuditService.Training, "PretrainedCreated", user.Name, r, req);
        await db.SaveChangesAsync(ct);
        return ToDto(asset);
    }

    public async Task<PretrainedAssetDto> AddFileAsync(string @ref, string fileName, Stream content, CurrentUser user, CancellationToken ct)
    {
        var asset = await db.PretrainedAssets.FirstOrDefaultAsync(a => a.Ref == @ref, ct) ?? throw ApiException.NotFound("사전학습 자산");
        var safe = StorageKeys.SafeName(fileName);
        var temp = await TempFileWriter.WriteAsync(storage, content, options.Value.MaxModelBytes, ".bin", ct);
        try
        {
            var key = StorageKeys.Pretrained(@ref, safe);
            await storage.DeleteAsync(key, ct);
            await storage.CommitTempAsync(temp.TempPath, key, ct);
        }
        finally { TempFileWriter.TryDelete(temp.TempPath); }

        var files = Mapping.Json(asset.FilesJson, new List<FileEntry>());
        files.RemoveAll(f => f.FileName == safe);
        files.Add(new FileEntry(safe, temp.Sha256, temp.SizeBytes));
        asset.FilesJson = Mapping.ToJson(files);
        audit.Record(AuditService.Training, "PretrainedFileAdded", user.Name, @ref, new { safe, temp.Sha256, temp.SizeBytes });
        await db.SaveChangesAsync(ct);
        return ToDto(asset);
    }

    public async Task DeleteAsync(string @ref, CurrentUser user, CancellationToken ct)
    {
        var asset = await db.PretrainedAssets.FirstOrDefaultAsync(a => a.Ref == @ref, ct) ?? throw ApiException.NotFound("사전학습 자산");
        foreach (var f in Mapping.Json(asset.FilesJson, new List<FileEntry>()))
            await storage.DeleteAsync(StorageKeys.Pretrained(@ref, f.FileName), ct);
        db.PretrainedAssets.Remove(asset);
        audit.Record(AuditService.Training, "PretrainedDeleted", user.Name, @ref);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<FileEntry>> FilesAsync(string @ref, CancellationToken ct)
    {
        var asset = await db.PretrainedAssets.AsNoTracking().FirstOrDefaultAsync(a => a.Ref == @ref, ct) ?? throw ApiException.NotFound("사전학습 자산");
        return Mapping.Json(asset.FilesJson, new List<FileEntry>());
    }

    public async Task<(Stream Stream, FileEntry Entry)> OpenFileAsync(string @ref, string fileName, CancellationToken ct)
    {
        var safe = StorageKeys.SafeName(fileName);
        var entry = (await FilesAsync(@ref, ct)).FirstOrDefault(f => f.FileName == safe) ?? throw ApiException.NotFound("사전학습 파일");
        var key = StorageKeys.Pretrained(@ref, safe);
        if (!storage.Exists(key)) throw ApiException.NotFound("사전학습 파일(스토리지)");
        return (storage.OpenRead(key), entry);
    }

    public static string FileUrl(string @ref, string fileName) => $"/api/pretrained/{Uri.EscapeDataString(@ref)}/{Uri.EscapeDataString(fileName)}";

    private static PretrainedAssetDto ToDto(PretrainedAsset a)
    {
        var files = Mapping.Json(a.FilesJson, new List<FileEntry>());
        return new PretrainedAssetDto(a.Ref,
            files.Select(f => new PretrainedFileDto(f.FileName, f.Sha256, f.SizeBytes, FileUrl(a.Ref, f.FileName))).ToList(),
            a.License, a.Source, a.AddedBy, a.AddedAt, files.Sum(f => f.SizeBytes));
    }
}
