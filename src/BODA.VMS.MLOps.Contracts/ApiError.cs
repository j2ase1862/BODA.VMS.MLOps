using System.Text.Json;
using System.Text.Json.Serialization;

namespace BODA.VMS.MLOps.Contracts;

/// <summary>오류 규약 (Phase 1 §5): 400 검증 실패 code, 409 상태 전이 불가, 413 크기 초과</summary>
public sealed record ApiError(string Code, string Message, IReadOnlyList<string>? Details = null);

/// <summary>검증 실패 코드 (Phase 1 §5 + Phase 3)</summary>
public static class ErrorCodes
{
    public const string ClassMismatch = "ClassMismatch";
    public const string MissingNames = "MissingNames";
    public const string MissingInputSize = "MissingInputSize";
    public const string UnsupportedFormat = "UnsupportedFormat";
    public const string TooLarge = "TooLarge";
    public const string LicenseRequired = "LicenseRequired";
    public const string InvalidStageTransition = "InvalidStageTransition";
    public const string InvalidJobTransition = "InvalidJobTransition";
    public const string InvalidHyperparams = "InvalidHyperparams";
    public const string TaskTypeMismatch = "TaskTypeMismatch";
    public const string NotFound = "NotFound";
    public const string Forbidden = "Forbidden";
    public const string DuplicateJob = "DuplicateJob";
    public const string WorkerDisabled = "WorkerDisabled";
    public const string ProtocolMismatch = "ProtocolMismatch";
    public const string ScriptDisabled = "ScriptDisabled";
    public const string PretrainedRequired = "PretrainedRequired";
    public const string PretrainedNotSupported = "PretrainedNotSupported";
    public const string Validation = "Validation";

    // ── 자체 계정 로그인 (Auth:Mode=Local) ──
    /// <summary>아이디나 비밀번호가 맞지 않는다. 둘 중 무엇이 틀렸는지는 알리지 않는다 — 계정 이름 훑기를 돕는 셈이다.</summary>
    public const string InvalidCredentials = "InvalidCredentials";
    /// <summary>연속 실패로 계정이 잠겼다. 언제 풀리는지는 메시지에 있다.</summary>
    public const string AccountLocked = "AccountLocked";
    /// <summary>임시 비밀번호 상태. 바꾸기 전에는 다른 화면에 들어가지 못한다.</summary>
    public const string PasswordChangeRequired = "PasswordChangeRequired";
    /// <summary>한 IP 에서 너무 자주 시도했다.</summary>
    public const string TooManyAttempts = "TooManyAttempts";
}

/// <summary>서버·워커·클라이언트 공통 JSON 규약 — camelCase, enum 문자열</summary>
public static class MlopsJson
{
    public static readonly JsonSerializerOptions Options = Create();

    public static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // 한글을 \uXXXX 로 이스케이프하지 않는다. 기본 인코더를 쓰면 DB 에 저장된 JSON 문자열과
            // 사용자가 입력한 문자열이 달라져 태그·클래스 검색이 어긋나고, 오류 메시지도 읽을 수 없게 나간다.
            // JSON 을 HTML 에 그대로 박지 않으므로 이 인코더로 인한 위험은 없다.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }

    /// <summary>워커 ↔ 서버 프로토콜 버전 헤더 (Phase 3 §10)</summary>
    public const string ProtocolHeader = "X-Worker-Protocol";
    public const int ProtocolVersion = 1;
}
