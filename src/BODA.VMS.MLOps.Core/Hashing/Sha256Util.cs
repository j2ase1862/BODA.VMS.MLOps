using System.Security.Cryptography;
using System.Text;

namespace BODA.VMS.MLOps.Core.Hashing;

public static class Sha256Util
{
    public static string HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 스트림 전체를 읽어 해시한다. 저장소가 파일 경로를 내주지 않는 경우(원격 저장소 등)를 위한 것이다.
    /// 부르는 쪽이 스트림을 처음부터 준다고 본다.
    /// </summary>
    public static async Task<string> HashStreamAsync(Stream stream, CancellationToken ct = default)
    {
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string HashString(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static string HashBytes(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static bool IsSha256(string? s) =>
        s is { Length: 64 } && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
}
