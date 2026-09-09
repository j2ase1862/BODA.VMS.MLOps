using System.Diagnostics.CodeAnalysis;

namespace BODA.VMS.MLOps.Core.Domain;

/// <summary>
/// 레시피 파라미터에 저장되는 모델 참조 <c>model://{modelId}@{version|stage}</c> (Phase 1 §2, §6.1).
/// VMS 의 <c>ModelReferenceResolver</c> 와 서버가 같은 문법을 쓴다.
///
/// <para>
/// 가리킬 수 있는 것은 둘이다 — 버전 번호를 못 박거나(<c>@7</c>, <c>@v7</c>),
/// 단계를 따라가거나(<c>@production</c>, <c>@staging</c>, <c>@candidate</c>).
/// 단계를 따라가면 승격·롤백이 그대로 반영된다.
/// </para>
/// <para>
/// <see cref="ModelStage.Retired"/> 는 일부러 받지 않는다. "이제 쓰지 말라" 는 뜻이라
/// 레시피가 그것을 가리키는 것 자체가 사고다. 여기서 막으면 저장할 때 걸리고,
/// 통과시키면 라인에서 검사가 시작될 때야 드러난다.
/// </para>
/// </summary>
public sealed record ModelReference(Guid ModelId, int? Version, string? Stage = null)
{
    public const string Scheme = "model://";
    public const string ProductionTag = "production";

    /// <summary>레시피가 따라갈 수 있는 단계. 소문자로만 쓴다 (응답 JSON 과 같은 표기).</summary>
    public static readonly string[] FollowableStages = [ProductionTag, "staging", "candidate"];

    /// <summary>version 이 없고 단계도 지정하지 않았거나 production 이면 운영 버전을 따른다</summary>
    public bool FollowsProduction =>
        Version is null && (Stage is null || Stage.Equals(ProductionTag, StringComparison.Ordinal));

    /// <summary>단계를 따라가는 참조인가 (버전 고정이 아니라)</summary>
    public bool FollowsStage => Version is null;

    /// <summary>따라갈 단계 이름. 버전 고정 참조면 null.</summary>
    public string? EffectiveStage => Version is null ? Stage ?? ProductionTag : null;

    public static bool IsReference(string? value) =>
        value is not null && value.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(string? value, [NotNullWhen(true)] out ModelReference? reference)
    {
        reference = null;
        if (!IsReference(value)) return false;

        var body = value![Scheme.Length..].Trim();
        var at = body.IndexOf('@');
        if (at <= 0) return false;

        if (!Guid.TryParse(body[..at], out var id) || id == Guid.Empty) return false;
        var tag = body[(at + 1)..].Trim();
        if (tag.Length == 0) return false;

        var stage = NormalizeStage(tag);
        if (stage is not null)
        {
            // production 은 예전부터 Stage 없이 표현해 왔다. 그 모양을 유지해 기존 비교가 그대로 맞는다.
            reference = new ModelReference(id, null, stage == ProductionTag ? null : stage);
            return true;
        }

        if (tag.StartsWith('v') || tag.StartsWith('V')) tag = tag[1..];
        if (!int.TryParse(tag, out var ver) || ver <= 0) return false;
        reference = new ModelReference(id, ver);
        return true;
    }

    /// <summary>레시피가 따라갈 수 있는 단계면 소문자 이름, 아니면 null.</summary>
    private static string? NormalizeStage(string tag)
    {
        foreach (var stage in FollowableStages)
            if (tag.Equals(stage, StringComparison.OrdinalIgnoreCase)) return stage;
        return null;
    }

    public static ModelReference Parse(string value) =>
        TryParse(value, out var r) ? r : throw new FormatException($"모델 참조 형식이 아닙니다: {value}");

    public override string ToString() =>
        $"{Scheme}{ModelId:D}@{(Version is not null ? Version.Value.ToString() : Stage ?? ProductionTag)}";
}
