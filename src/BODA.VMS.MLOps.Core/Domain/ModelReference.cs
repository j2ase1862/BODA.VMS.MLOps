using System.Diagnostics.CodeAnalysis;

namespace BODA.VMS.MLOps.Core.Domain;

/// <summary>
/// 레시피 파라미터에 저장되는 모델 참조 <c>model://{modelId}@{version|production}</c> (Phase 1 §2, §6.1).
/// VMS 의 ModelReferenceResolver 와 서버가 같은 문법을 쓴다.
/// </summary>
public sealed record ModelReference(Guid ModelId, int? Version)
{
    public const string Scheme = "model://";
    public const string ProductionTag = "production";

    /// <summary>version 이 null 이면 Production 추종</summary>
    public bool FollowsProduction => Version is null;

    public static bool IsReference(string? value) =>
        value is not null && value.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(string? value, [NotNullWhen(true)] out ModelReference? reference)
    {
        reference = null;
        if (!IsReference(value)) return false;

        var body = value![Scheme.Length..].Trim();
        var at = body.IndexOf('@');
        if (at <= 0) return false;

        if (!Guid.TryParse(body[..at], out var id)) return false;
        var tag = body[(at + 1)..].Trim();
        if (tag.Length == 0) return false;

        if (tag.Equals(ProductionTag, StringComparison.OrdinalIgnoreCase))
        {
            reference = new ModelReference(id, null);
            return true;
        }
        if (tag.StartsWith('v') || tag.StartsWith('V')) tag = tag[1..];
        if (!int.TryParse(tag, out var ver) || ver <= 0) return false;
        reference = new ModelReference(id, ver);
        return true;
    }

    public static ModelReference Parse(string value) =>
        TryParse(value, out var r) ? r : throw new FormatException($"모델 참조 형식이 아닙니다: {value}");

    public override string ToString() =>
        $"{Scheme}{ModelId:D}@{(Version is null ? ProductionTag : Version.Value.ToString())}";
}
