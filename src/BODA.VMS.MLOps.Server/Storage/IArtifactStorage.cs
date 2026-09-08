namespace BODA.VMS.MLOps.Server.Storage;

/// <summary>
/// 아티팩트 스토리지 추상화 (개발 문서 §13-3: MVP 는 로컬 디스크, MinIO 전환 대비).
/// 키는 '/' 구분 상대 경로 — models/{sha[0:2]}/{sha}.onnx · jobs/{jobId}/{file} · datasets/{id}.zip · pretrained/{ref}/{file}
/// </summary>
public interface IArtifactStorage
{
    /// <summary>임시 파일 경로를 만든다 (같은 볼륨 → 원자적 이동 가능)</summary>
    string CreateTempPath(string? extension = null);

    /// <summary>임시 파일을 키 위치로 원자적으로 이동한다. 이미 있으면 덮어쓰지 않고 임시 파일을 지운다.</summary>
    Task<long> CommitTempAsync(string tempPath, string key, CancellationToken ct = default);

    Task<long> SaveAsync(string key, Stream content, CancellationToken ct = default);
    bool Exists(string key);
    long? Size(string key);
    Stream OpenRead(string key);
    Task DeleteAsync(string key, CancellationToken ct = default);
    /// <summary>로컬 구현의 실제 경로 (ONNX 검사 등 파일 기반 읽기용). 원격 구현은 null.</summary>
    string? LocalPath(string key);
}

public sealed class LocalDiskArtifactStorage : IArtifactStorage
{
    private readonly string _root;

    public LocalDiskArtifactStorage(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "tmp"));
    }

    public string Root => _root;

    public string CreateTempPath(string? extension = null) =>
        Path.Combine(_root, "tmp", Guid.NewGuid().ToString("N") + (extension ?? ".tmp"));

    public async Task<long> CommitTempAsync(string tempPath, string key, CancellationToken ct = default)
    {
        var dest = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var size = new FileInfo(tempPath).Length;
        if (File.Exists(dest))
        {
            // 내용 주소 지정(sha 경로)이라 같은 키면 같은 내용이다 — 기존 파일을 그대로 둔다
            File.Delete(tempPath);
            return new FileInfo(dest).Length;
        }
        File.Move(tempPath, dest, overwrite: false);
        await Task.CompletedTask;
        return size;
    }

    public async Task<long> SaveAsync(string key, Stream content, CancellationToken ct = default)
    {
        var temp = CreateTempPath();
        await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            await content.CopyToAsync(fs, ct);
        var dest = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Move(temp, dest, overwrite: true);
        return new FileInfo(dest).Length;
    }

    public bool Exists(string key) => File.Exists(Resolve(key));

    public long? Size(string key) => File.Exists(Resolve(key)) ? new FileInfo(Resolve(key)).Length : null;

    public Stream OpenRead(string key) =>
        new FileStream(Resolve(key), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var p = Resolve(key);
        if (File.Exists(p)) File.Delete(p);
        return Task.CompletedTask;
    }

    public string? LocalPath(string key) => Resolve(key);

    /// <summary>키 → 절대 경로. 루트 밖으로 나가는 키(..)는 거부 (경로 조작 방지, Phase 1 §8)</summary>
    private string Resolve(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains("..") || Path.IsPathRooted(key))
            throw new ArgumentException($"잘못된 스토리지 키: {key}");
        var full = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"스토리지 루트 밖의 키: {key}");
        return full;
    }
}

public static class StorageKeys
{
    public static string Model(string sha256) => $"models/{sha256[..2]}/{sha256}.onnx";
    public static string JobFile(Guid jobId, string fileName) => $"jobs/{jobId:N}/{SafeName(fileName)}";
    public static string Dataset(Guid id) => $"datasets/{id:N}.zip";
    public static string Pretrained(string @ref, string fileName) => $"pretrained/{SafeName(@ref)}/{SafeName(fileName)}";

    /// <summary>파일명 화이트리스트 — 경로 구분자·상위 이동 금지</summary>
    public static string SafeName(string name)
    {
        var n = Path.GetFileName(name.Trim());
        if (n.Length == 0 || n is "." or ".." || n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"잘못된 파일명: {name}");
        return n;
    }
}
