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

    /// <summary>
    /// 임시 파일을 제자리로 옮긴다.
    ///
    /// <para><b>같은 키가 동시에 들어올 수 있습니다.</b>
    /// 경로가 내용 해시라, 같은 사진을 두 라인이 같은 순간에 올리면 두 요청이 같은 자리를 노린다.
    /// 있는지 보고 옮기는 사이에 남이 끼어들면 한쪽은 <see cref="IOException"/> 으로 터지고
    /// 그 요청은 500 이 된다 — 사람에게는 "가끔 업로드가 실패한다" 로만 보인다.
    /// 같은 키면 내용이 같으므로 진 쪽은 이긴 파일을 그대로 쓰면 된다.
    /// </para>
    /// <para>
    /// 방금 닫은 파일을 백신이 잠깐 쥐고 있어 옮기지 못하는 경우도 같은 예외로 온다.
    /// 그래서 몇 번 짧게 다시 해 본다. 그래도 안 되면 진짜 문제이므로 그대로 올린다.
    /// </para>
    /// </summary>
    public async Task<long> CommitTempAsync(string tempPath, string key, CancellationToken ct = default)
    {
        var dest = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var size = new FileInfo(tempPath).Length;

        for (var attempt = 0; ; attempt++)
        {
            if (File.Exists(dest))
            {
                // 내용 주소 지정(sha 경로)이라 같은 키면 같은 내용이다 — 기존 파일을 그대로 둔다
                TryDelete(tempPath);
                return new FileInfo(dest).Length;
            }

            try
            {
                File.Move(tempPath, dest, overwrite: false);
                return size;
            }
            catch (IOException) when (attempt < MoveAttempts)
            {
                // 남이 먼저 옮겼거나(다음 회전의 Exists 가 잡는다) 잠깐 잡혀 있다
                await Task.Delay(BackoffMs * (attempt + 1), ct);
            }
            catch (IOException) when (attempt == MoveAttempts)
            {
                // 계속 막힌다. 잡고 있는 쪽(대개 백신 실시간 검사)은 읽기는 내주므로 복사로 넘어간다.
                // 임시 파일은 못 지워도 상관없다 — 이름이 Guid 라 다음 업로드와 부딪히지 않는다.
                await CopyIntoPlaceAsync(tempPath, dest, ct);
                TryDelete(tempPath);
                return size;
            }
        }
    }

    private static async Task CopyIntoPlaceAsync(string source, string dest, CancellationToken ct)
    {
        var staging = dest + ".copying-" + Guid.NewGuid().ToString("N")[..8];
        await using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true))
        await using (var to = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            await from.CopyToAsync(to, ct);

        try
        {
            File.Move(staging, dest, overwrite: false);
        }
        catch (IOException)
        {
            // 그 사이 남이 먼저 자리를 잡았다 — 같은 내용이므로 그것을 쓴다
            TryDelete(staging);
            if (!File.Exists(dest)) throw;
        }
    }

    public async Task<long> SaveAsync(string key, Stream content, CancellationToken ct = default)
    {
        var temp = CreateTempPath();
        await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            await content.CopyToAsync(fs, ct);
        var dest = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, dest, overwrite: true);
                return new FileInfo(dest).Length;
            }
            catch (IOException) when (attempt < MoveAttempts)
            {
                await Task.Delay(20 * (attempt + 1), ct);
            }
        }
    }

    /// <summary>옮기기를 몇 번까지 다시 해 보는가. 그 뒤에는 복사로 넘어간다.</summary>
    private const int MoveAttempts = 5;

    /// <summary>재시도 간격의 기준. 30·60·90·120·150ms 로 벌어진다.</summary>
    private const int BackoffMs = 30;

    /// <summary>진 쪽의 임시 파일을 치운다. 못 치워도 업로드를 실패시키지는 않는다.</summary>
    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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
    public static string DatasetManifest(Guid id) => $"datasets/{id:N}.manifest.json";
    public static string Pretrained(string @ref, string fileName) => $"pretrained/{SafeName(@ref)}/{SafeName(fileName)}";

    // 이미지도 내용 주소 지정 — 같은 사진을 여러 번 올려도 한 벌만 남는다
    public static string Image(string sha256, string extension) =>
        $"images/{sha256[..2]}/{sha256}{NormalizeExtension(extension)}";
    public static string Thumbnail(string sha256) => $"images/{sha256[..2]}/{sha256}-thumb.jpg";
    public static string View(string sha256) => $"images/{sha256[..2]}/{sha256}-view.jpg";

    private static string NormalizeExtension(string extension)
    {
        var e = extension.Trim().ToLowerInvariant();
        if (!e.StartsWith('.')) e = "." + e;
        return e.Length is > 1 and <= 6 && e.Skip(1).All(char.IsLetterOrDigit) ? e : ".bin";
    }

    /// <summary>파일명 화이트리스트 — 경로 구분자·상위 이동 금지</summary>
    public static string SafeName(string name)
    {
        var n = Path.GetFileName(name.Trim());
        if (n.Length == 0 || n is "." or ".." || n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"잘못된 파일명: {name}");
        return n;
    }
}
