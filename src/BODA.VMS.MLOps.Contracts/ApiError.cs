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
    public const string Validation = "Validation";
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
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }

    /// <summary>워커 ↔ 서버 프로토콜 버전 헤더 (Phase 3 §10)</summary>
    public const string ProtocolHeader = "X-Worker-Protocol";
    public const int ProtocolVersion = 1;
}
