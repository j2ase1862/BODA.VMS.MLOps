using System.IO.Compression;
using BODA.VMS.MLOps.Contracts.Training;
using BODA.VMS.MLOps.Core.Hashing;
using BODA.VMS.MLOps.TrainWorker.Agent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BODA.VMS.MLOps.TrainWorker.Jobs;

/// <summary>zip 안전 해제 — 경로 탈출(..)·절대 경로·심볼릭 링크 거부, 총 크기 상한 (Phase 3 §8)</summary>
public static class SafeZip
{
    public static void Extract(string zipPath, string destDir, long maxTotalBytes = 200L * 1024 * 1024 * 1024)
    {
        var root = Path.GetFullPath(destDir);
        Directory.CreateDirectory(root);
        long total = 0;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.Length == 0 || name.StartsWith('/') || name.Contains("../") || name.Contains("/..") || name == ".." || Path.IsPathRooted(name))
                throw new InvalidDataException($"zip 항목 경로가 허용되지 않습니다: {entry.FullName}");
            // 유닉스 심볼릭 링크(외부 속성 상위 16비트 S_IFLNK 0xA000) 거부
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException($"심볼릭 링크 항목은 허용되지 않습니다: {entry.FullName}");

            var dest = Path.GetFullPath(Path.Combine(root, name));
            if (!dest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && dest != root)
                throw new InvalidDataException($"zip 항목이 대상 폴더 밖을 가리킵니다: {entry.FullName}");

            if (name.EndsWith('/')) { Directory.CreateDirectory(dest); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            // entry.Length 는 zip 헤더가 신고한 값이라 압축 폭탄이 속일 수 있다. 실제 기록 바이트를 센다.
            total += CopyEntry(entry, dest, maxTotalBytes - total);
        }
    }

    /// <summary>남은 예산만큼만 쓰고, 넘으면 쓰다 만 파일을 지운 뒤 실패시킨다</summary>
    private static long CopyEntry(ZipArchiveEntry entry, string dest, long budget)
    {
        long written = 0;
        try
        {
            using var source = entry.Open();
            using var target = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            var buffer = new byte[1 << 16];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > budget) throw new InvalidDataException($"zip 해제 총 크기 상한 초과: {entry.FullName}");
                target.Write(buffer, 0, read);
            }
        }
        catch
        {
            try { File.Delete(dest); } catch { }
            throw;
        }
        return written;
    }
}

/// <summary>datasets\{manifestHash}\ 캐시 — 완료 마커(.complete)로 부분 해제 구분, LRU 정리 (Phase 3 §5.1, §5.4)</summary>
public sealed class DatasetCache(ServerClient server, IOptions<WorkerOptions> options, ILogger<DatasetCache> logger)
{
    private readonly WorkerOptions _o = options.Value;

    public string DirFor(string manifestHash) => Path.Combine(_o.DatasetsDir, Sanitize(manifestHash));

    public async Task<string> EnsureAsync(JobAssignment a, CancellationToken ct)
    {
        var dir = DirFor(a.DatasetManifestHash);
        var marker = Path.Combine(dir, ".complete");
        if (File.Exists(marker))
        {
            File.SetLastWriteTimeUtc(marker, DateTime.UtcNow); // LRU 갱신
            logger.LogInformation("데이터셋 캐시 적중 {Hash}", a.DatasetManifestHash[..12]);
            return dir;
        }

        Trim(a.DatasetSizeBytes);
        var zip = Path.Combine(_o.DatasetsDir, Sanitize(a.DatasetManifestHash) + ".zip");
        logger.LogInformation("데이터셋 다운로드 {Url} ({Size:N0} bytes)", a.DatasetExportUrl, a.DatasetSizeBytes);
        await server.DownloadAsync(a.DatasetExportUrl, zip, null, ct);

        var tmp = dir + ".extracting";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        try
        {
            SafeZip.Extract(zip, tmp);
            Directory.Move(tmp, dir);
            await File.WriteAllTextAsync(marker, a.DatasetManifestHash, ct);
        }
        finally
        {
            try { File.Delete(zip); } catch { }
            if (Directory.Exists(tmp)) { try { Directory.Delete(tmp, true); } catch { } }
        }
        ValidateLayout(dir, a.DatasetExportFormat);
        return dir;
    }

    /// <summary>내보내기 형식별 최소 구조 확인 (data.yaml / ImageFolder / MVTec / PP-OCR)</summary>
    public static void ValidateLayout(string dir, string format)
    {
        bool ok = format.ToLowerInvariant() switch
        {
            "yolo" => File.Exists(Path.Combine(dir, "data.yaml")) || Directory.GetFiles(dir, "data.yaml", SearchOption.AllDirectories).Length > 0,
            "imagefolder" => Directory.Exists(Path.Combine(dir, "train")) || Directory.GetDirectories(dir).Length > 0,
            "mvtec" => Directory.GetDirectories(dir, "train", SearchOption.AllDirectories).Length > 0,
            "ppocr" => Directory.GetFiles(dir, "*.txt", SearchOption.AllDirectories).Length > 0,
            "coco" => Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories).Length > 0,
            _ => true,
        };
        if (!ok) throw new InvalidDataException($"데이터셋 구조가 {format} 형식이 아닙니다: {dir}");
    }

    /// <summary>캐시 상한을 넘으면 오래된 것부터 지운다</summary>
    public void Trim(long incomingBytes)
    {
        if (!Directory.Exists(_o.DatasetsDir)) return;
        long limit = (long)_o.DatasetCacheLimitGB * 1024 * 1024 * 1024;
        var entries = Directory.GetDirectories(_o.DatasetsDir)
            .Select(d => new { Dir = d, Marker = Path.Combine(d, ".complete") })
            .Where(x => File.Exists(x.Marker))
            .Select(x => new { x.Dir, Used = File.GetLastWriteTimeUtc(x.Marker), Size = DirSize(x.Dir) })
            .OrderBy(x => x.Used).ToList();
        long total = entries.Sum(e => e.Size) + incomingBytes * 2;
        foreach (var e in entries)
        {
            if (total <= limit) break;
            try { Directory.Delete(e.Dir, true); total -= e.Size; logger.LogInformation("데이터셋 캐시 정리 {Dir}", e.Dir); }
            catch (Exception ex) { logger.LogWarning(ex, "캐시 삭제 실패 {Dir}", e.Dir); }
        }
    }

    private static long DirSize(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch { return 0; }
    }

    private static string Sanitize(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());
}

/// <summary>pretrained\{ref}\ 캐시 — 파일별 sha256 확인, 없거나 다르면 서버 미러에서 받는다 (Phase 3 §5.1)</summary>
public sealed class PretrainedCache(ServerClient server, IOptions<WorkerOptions> options, ILogger<PretrainedCache> logger)
{
    private readonly WorkerOptions _o = options.Value;

    /// <summary>반환: 사전학습 폴더 경로 (파일이 없으면 null)</summary>
    public async Task<string?> EnsureAsync(IReadOnlyList<PretrainedFileRef> files, CancellationToken ct)
    {
        if (files.Count == 0) return null;
        var @ref = files[0].Ref;
        var dir = Path.Combine(_o.PretrainedDir, @ref);
        Directory.CreateDirectory(dir);
        foreach (var f in files)
        {
            var path = Path.Combine(dir, Path.GetFileName(f.FileName));
            if (File.Exists(path) && new FileInfo(path).Length == f.SizeBytes)
            {
                var sha = await Sha256Util.HashFileAsync(path, ct);
                if (sha.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                logger.LogWarning("사전학습 파일 해시 불일치 → 재다운로드 {File}", f.FileName);
            }
            logger.LogInformation("사전학습 다운로드 {Ref}/{File}", @ref, f.FileName);
            await server.DownloadAsync(f.Url, path, f.Sha256, ct);
        }
        return dir;
    }
}

/// <summary>scripts\{name}.py — 서버 매니페스트 해시와 다르면 서버 버전으로 교체 (Phase 3 §5.1, §8)</summary>
public sealed class ScriptStore(ServerClient server, IOptions<WorkerOptions> options, ILogger<ScriptStore> logger)
{
    private readonly WorkerOptions _o = options.Value;

    public async Task<string> EnsureAsync(string scriptName, string expectedSha, string scriptUrl, CancellationToken ct)
    {
        var path = Path.Combine(_o.ScriptsDir, Path.GetFileName(scriptName));
        if (File.Exists(path) && (await Sha256Util.HashFileAsync(path, ct)).Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
            return path;

        // 설치 동봉본이 서버와 같으면 복사, 아니면 서버에서 받는다
        var bundled = Path.Combine(WorkerOptions.BundledScriptsDir, Path.GetFileName(scriptName));
        if (File.Exists(bundled) && (await Sha256Util.HashFileAsync(bundled, ct)).Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(bundled, path, overwrite: true);
            return path;
        }

        logger.LogInformation("스크립트 갱신 {Name} ← 서버 ({Sha})", scriptName, expectedSha[..12]);
        await server.DownloadAsync(scriptUrl, path, expectedSha, ct);
        return path;
    }
}
