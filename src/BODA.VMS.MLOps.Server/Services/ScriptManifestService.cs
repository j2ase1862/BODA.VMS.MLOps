using BODA.VMS.MLOps.Core.Hashing;
using Microsoft.Extensions.Options;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>
/// 서버가 배포하는 학습 스크립트(scripts\train_*.py)의 SHA-256 매니페스트 (Phase 3 §2, §4, §8).
/// 워커는 매니페스트와 일치하는 스크립트만 실행하고, 다르면 서버 버전으로 교체한다. 파일 변경 시각을 보고 다시 계산한다.
/// </summary>
public sealed class ScriptManifestService(IOptions<MlopsOptions> options, ILogger<ScriptManifestService> logger)
{
    private readonly object _lock = new();
    private Dictionary<string, string> _hashes = new(StringComparer.Ordinal);
    private Dictionary<string, DateTime> _stamps = new(StringComparer.Ordinal);
    private DateTime _generatedAt;

    public string Root => options.Value.ResolvedScriptsRoot();

    public (Dictionary<string, string> Scripts, DateTime GeneratedAt) GetManifest()
    {
        lock (_lock)
        {
            RefreshIfChanged();
            return (new Dictionary<string, string>(_hashes, StringComparer.Ordinal), _generatedAt);
        }
    }

    public string? GetHash(string scriptName)
    {
        var (m, _) = GetManifest();
        return m.TryGetValue(scriptName, out var h) ? h : null;
    }

    /// <summary>매니페스트에 있는 이름만 허용 — 임의 파일 읽기 방지</summary>
    public string? GetPath(string scriptName)
    {
        var (m, _) = GetManifest();
        if (!m.ContainsKey(scriptName)) return null;
        return Path.Combine(Root, scriptName);
    }

    private void RefreshIfChanged()
    {
        if (!Directory.Exists(Root))
        {
            if (_hashes.Count > 0 || _generatedAt == default)
            {
                logger.LogWarning("스크립트 폴더가 없습니다: {Root}", Root);
                _hashes = new(StringComparer.Ordinal);
                _stamps = new(StringComparer.Ordinal);
                _generatedAt = DateTime.UtcNow;
            }
            return;
        }

        var files = Directory.GetFiles(Root, "train_*.py");
        var current = files.ToDictionary(f => Path.GetFileName(f), f => File.GetLastWriteTimeUtc(f), StringComparer.Ordinal);
        bool changed = current.Count != _stamps.Count || current.Any(kv => !_stamps.TryGetValue(kv.Key, out var t) || t != kv.Value);
        if (!changed && _generatedAt != default) return;

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in files)
            hashes[Path.GetFileName(f)] = Sha256Util.HashFile(f);
        _hashes = hashes;
        _stamps = current;
        _generatedAt = DateTime.UtcNow;
        logger.LogInformation("스크립트 매니페스트 갱신: {Count}개 ({Root})", hashes.Count, Root);
    }
}
