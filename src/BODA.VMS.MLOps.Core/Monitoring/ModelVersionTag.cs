namespace BODA.VMS.MLOps.Core.Monitoring;

/// <summary>
/// 검사 이력에 실린 모델 식별자를 읽는다.
///
/// <para>
/// VMS 가 <c>DlModelIdentity.ForVersion</c> 으로 만든 <c>mv:{32자 16진}</c> 를 되읽는다.
/// 두 리포에 같은 규약이 있으므로 <b>한쪽을 바꾸면 다른 쪽도 바꿔야 한다</b> —
/// VMS 쪽은 <c>VMS.Core/Services/DlModelIdentity.cs</c> 다.
/// </para>
/// <para>
/// 그 형식이 아니면 null 이다. 옛 VMS 는 <c>{도구타입}:{파일명}</c> 을 실었고 그것으로는
/// 어느 버전인지 알 수 없다. DL 을 안 쓴 검사는 아예 비어 있다. 둘 다 버리지 않고
/// "이어지지 않은 것" 으로 세어, 라인이 아직 옛 판을 쓰는지 알 수 있게 한다.
/// </para>
/// </summary>
public static class ModelVersionTag
{
    /// <summary>버전 id 로 만든 식별자임을 알리는 접두.</summary>
    public const string Prefix = "mv:";

    /// <summary>버전 id 로 식별자를 만든다 (VMS 쪽과 같은 규칙 — 시험이 두 구현을 맞춰 본다).</summary>
    public static string Write(Guid modelVersionId) => Prefix + modelVersionId.ToString("N");

    /// <summary>식별자에서 버전 id 를 읽는다. 그 형식이 아니면 null.</summary>
    public static Guid? TryRead(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return null;

        var value = identity.Trim();
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return null;

        return Guid.TryParseExact(value[Prefix.Length..], "N", out var id) ? id : null;
    }
}
