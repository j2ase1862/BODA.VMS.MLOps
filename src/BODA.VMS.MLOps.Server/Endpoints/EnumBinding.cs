using BODA.VMS.MLOps.Contracts;

namespace BODA.VMS.MLOps.Server.Endpoints;

/// <summary>
/// 라우트·쿼리의 열거형 값 파싱. 최소 API 의 기본 열거형 바인딩은 대소문자를 구분하지만
/// 이 API 의 JSON 은 camelCase 열거형("detection", "trainDfine")을 내보낸다.
/// 응답에서 받은 값을 그대로 URL 에 넣을 수 있도록 대소문자를 무시하고 파싱한다.
/// </summary>
public static class EnumBinding
{
    public static T Parse<T>(string value, string paramName) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var result) && Enum.IsDefined(result)
            ? result
            : throw ApiException.BadRequest(ErrorCodes.Validation,
                $"{paramName}: 허용값 {string.Join("|", Enum.GetNames<T>().Select(Camel))} (받은 값: {value})");

    public static T? ParseOptional<T>(string? value, string paramName) where T : struct, Enum =>
        string.IsNullOrWhiteSpace(value) ? null : Parse<T>(value, paramName);

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}
