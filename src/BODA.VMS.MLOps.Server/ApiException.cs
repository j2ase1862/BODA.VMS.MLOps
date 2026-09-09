using System.Text.Json;
using BODA.VMS.MLOps.Contracts;

namespace BODA.VMS.MLOps.Server;

/// <summary>서비스 계층이 던지는 오류 → 미들웨어가 ApiError JSON 으로 변환 (400/403/404/409/413)</summary>
public sealed class ApiException(int status, string code, string message, IReadOnlyList<string>? details = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public IReadOnlyList<string>? Details { get; } = details;

    public static ApiException NotFound(string what) => new(404, ErrorCodes.NotFound, $"{what} 을(를) 찾을 수 없습니다.");
    public static ApiException BadRequest(string code, string message, IReadOnlyList<string>? details = null) => new(400, code, message, details);
    public static ApiException Conflict(string code, string message) => new(409, code, message);
    public static ApiException Forbidden(string message) => new(403, ErrorCodes.Forbidden, message);
    public static ApiException TooLarge(string message) => new(413, ErrorCodes.TooLarge, message);
}

/// <summary>
/// 서비스가 던진 오류를 <see cref="ApiError"/> JSON 으로 바꾼다.
///
/// <para><b>운영이 아니면 500 에 예외를 실어 보냅니다.</b>
/// 가려 두면 개발·시험에서 500 이 났을 때 원인을 볼 길이 없다 — 로그는 콘솔로만 나가고
/// 시험 실행기는 그것을 잡지 않아, "가끔 실패한다" 를 쫓는 데 며칠이 든다.
/// 운영에서는 스택을 밖으로 내보내지 않는다.
/// </para>
/// </summary>
public sealed class ApiExceptionMiddleware(
    RequestDelegate next, ILogger<ApiExceptionMiddleware> logger, IHostEnvironment environment)
{
    public async Task Invoke(HttpContext ctx)
    {
        try
        {
            await next(ctx);
        }
        catch (ApiException ex) when (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = ex.Status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new ApiError(ex.Code, ex.Message, ex.Details), MlopsJson.Options));
        }
        // 모델 바인딩·본문 파싱 실패는 자체 상태 코드(주로 400, 크기 초과 413)를 갖는다. 500 으로 승격하지 않는다.
        catch (BadHttpRequestException ex) when (!ctx.Response.HasStarted)
        {
            var (code, message) = ex.StatusCode == 413
                ? (ErrorCodes.TooLarge, "요청 본문이 크기 상한을 초과했습니다.")
                : (ErrorCodes.Validation, ex.Message);
            logger.LogWarning("잘못된 요청 {Status} {Path}: {Message}", ex.StatusCode, ctx.Request.Path, ex.Message);
            ctx.Response.StatusCode = ex.StatusCode;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new ApiError(code, message), MlopsJson.Options));
        }
        catch (Exception ex) when (!ctx.Response.HasStarted && ex is not OperationCanceledException)
        {
            logger.LogError(ex, "처리되지 않은 예외 {Path}", ctx.Request.Path);
            ctx.Response.StatusCode = 500;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            // 운영에서는 스택을 내보내지 않는다. 그 밖에서는 이것이 없으면 원인을 볼 길이 없다.
            var details = environment.IsProduction() ? null : new[] { ex.ToString() };
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(
                new ApiError("InternalError", "서버 내부 오류", details), MlopsJson.Options));
        }
    }
}
