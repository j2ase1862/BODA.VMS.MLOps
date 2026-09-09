using System.Text;
using BODA.VMS.MLOps.Server.Storage;
using FluentAssertions;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// 내용 주소 저장소가 같은 키의 동시 커밋을 견디는지.
///
/// <para>
/// 경로가 내용 해시라 <b>같은 사진을 두 라인이 같은 순간에 올리면 같은 자리를 노립니다</b>.
/// 있는지 보고 옮기는 사이에 남이 끼어들면 진 쪽이 <see cref="IOException"/> 으로 터지고
/// 그 요청은 500 이 됩니다. 사람에게는 "가끔 업로드가 실패한다" 로만 보이고,
/// 재현이 어려워 오래 남습니다 — 실제로 이 리포의 시험 묶음이 다섯 번에 한 번씩
/// 이 이유로 흔들렸습니다.
/// </para>
/// </summary>
public class ArtifactStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mlops-storage-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private LocalDiskArtifactStorage NewStorage() => new(_root);

    private static async Task<string> WriteTempAsync(LocalDiskArtifactStorage storage, byte[] bytes)
    {
        var path = storage.CreateTempPath();
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    [Fact]
    public async Task Concurrent_commits_of_the_same_key_all_succeed()
    {
        var storage = NewStorage();
        var bytes = Encoding.UTF8.GetBytes("같은 내용의 사진");
        const string key = "ab/abcdef0123456789";

        // 같은 키로 동시에 밀어 넣는다. 하나만 자리를 차지하고 나머지는 그것을 쓰면 된다.
        var temps = new List<string>();
        for (var i = 0; i < 8; i++) temps.Add(await WriteTempAsync(storage, bytes));

        var commits = temps.Select(t => Task.Run(() => storage.CommitTempAsync(t, key))).ToArray();
        var sizes = await Task.WhenAll(commits);

        sizes.Should().AllBeEquivalentTo((long)bytes.Length, "같은 키면 같은 내용이라 크기도 같다");
        storage.Exists(key).Should().BeTrue();

        // 진 쪽의 임시 파일이 쌓이면 안 된다 — 디스크가 조용히 찬다
        Directory.GetFiles(Path.Combine(_root, "tmp")).Should().BeEmpty();
    }

    /// <summary>
    /// 다른 키끼리는 서로를 건드리지 않는다 — 재시도가 엉뚱한 파일을 덮지 않는지.
    /// </summary>
    [Fact]
    public async Task Different_keys_keep_their_own_content()
    {
        var storage = NewStorage();

        var first = await WriteTempAsync(storage, Encoding.UTF8.GetBytes("첫째"));
        var second = await WriteTempAsync(storage, Encoding.UTF8.GetBytes("둘째 — 더 길다"));

        await storage.CommitTempAsync(first, "aa/first");
        await storage.CommitTempAsync(second, "bb/second");

        using var readFirst = new StreamReader(storage.OpenRead("aa/first"));
        using var readSecond = new StreamReader(storage.OpenRead("bb/second"));
        (await readFirst.ReadToEndAsync()).Should().Be("첫째");
        (await readSecond.ReadToEndAsync()).Should().Be("둘째 — 더 길다");
    }

    /// <summary>
    /// 이미 있는 자리에 커밋하면 있던 파일을 그대로 두고 크기를 돌려준다.
    /// 덮어쓰면 같은 내용이라도 그 파일을 읽고 있던 요청이 끊긴다.
    /// </summary>
    [Fact]
    public async Task Committing_over_an_existing_key_keeps_the_file_that_is_already_there()
    {
        var storage = NewStorage();
        var bytes = Encoding.UTF8.GetBytes("먼저 자리를 잡은 것");
        const string key = "cc/settled";

        await storage.CommitTempAsync(await WriteTempAsync(storage, bytes), key);
        var before = File.GetLastWriteTimeUtc(Path.Combine(_root, "cc", "settled"));

        var size = await storage.CommitTempAsync(await WriteTempAsync(storage, bytes), key);

        size.Should().Be(bytes.Length);
        File.GetLastWriteTimeUtc(Path.Combine(_root, "cc", "settled")).Should().Be(before, "있던 파일을 건드리지 않는다");
        Directory.GetFiles(Path.Combine(_root, "tmp")).Should().BeEmpty();
    }
}
