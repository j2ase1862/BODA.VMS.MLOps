using System.Buffers;
using System.Security.Cryptography;
using BODA.VMS.MLOps.Contracts;

namespace BODA.VMS.MLOps.Server.Storage;

/// <summary>업로드 스트림 → 임시 파일 (스트리밍, SHA-256 동시 계산, 크기 상한 초과 시 413)</summary>
public static class TempFileWriter
{
    public sealed record Result(string TempPath, string Sha256, long SizeBytes);

    public static async Task<Result> WriteAsync(IArtifactStorage storage, Stream content, long maxBytes, string extension, CancellationToken ct)
    {
        var temp = storage.CreateTempPath(extension);
        long total = 0;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = ArrayPool<byte>.Shared.Rent(1 << 16);
        try
        {
            await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                int n;
                while ((n = await content.ReadAsync(buf, ct)) > 0)
                {
                    total += n;
                    if (total > maxBytes)
                        throw ApiException.TooLarge($"파일 크기 상한({maxBytes / (1024 * 1024)}MB)을 초과했습니다.");
                    sha.AppendData(buf, 0, n);
                    await fs.WriteAsync(buf.AsMemory(0, n), ct);
                }
            }
            if (total == 0)
                throw ApiException.BadRequest(ErrorCodes.Validation, "빈 파일은 등록할 수 없습니다.");
            return new Result(temp, Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(), total);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    public static void TryDelete(string? path)
    {
        try { if (path is not null && File.Exists(path)) File.Delete(path); } catch { /* 정리 실패는 무시 */ }
    }
}
