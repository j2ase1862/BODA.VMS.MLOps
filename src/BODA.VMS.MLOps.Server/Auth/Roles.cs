using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace BODA.VMS.MLOps.Server.Auth;

/// <summary>
/// 역할 (개발 문서 §6, Phase 1 §7): 사용자 Viewer ⊂ Labeler ⊂ Engineer ⊂ Admin, 서비스 계정 Worker(학습 워커) · Line(라인 PC).
/// 정책 이름은 "최소 요구 역할" 이다.
/// </summary>
public static class Roles
{
    public const string Viewer = "Viewer";
    public const string Labeler = "Labeler";
    public const string Engineer = "Engineer";
    public const string Admin = "Admin";
    public const string Worker = "Worker";
    public const string Line = "Line";

    public static readonly string[] All = [Viewer, Labeler, Engineer, Admin, Worker, Line];
}

/// <summary>
/// 정책. 워커 서비스 계정의 범위는 Phase 3 §8 대로 워커 API·데이터셋 export·사전학습 미러·아티팩트 업로드로 한정한다.
/// 그래서 Worker 역할은 <see cref="Viewer"/>·<see cref="Line"/> 에 들어가지 않는다.
/// 넣으면 GPU PC 한 대의 토큰이 새는 순간 레시피↔모델 매핑, 모든 Production ONNX, 남의 학습 로그까지 읽힌다.
/// 워커에 필요한 읽기는 <see cref="WorkerOrEngineer"/> 로 해당 엔드포인트에만 명시적으로 연다.
/// </summary>
public static class Policies
{
    /// <summary>사람 사용자 조회 + 라인 PC. 워커는 포함하지 않는다.</summary>
    public const string Viewer = "Viewer";
    public const string Labeler = "Labeler";
    public const string Engineer = "Engineer";
    public const string Admin = "Admin";
    /// <summary>학습 워커 서비스 계정만</summary>
    public const string Worker = "Worker";
    /// <summary>라인 PC(모델 pull·resolve·바인딩 페이로드). 워커는 포함하지 않는다.</summary>
    public const string Line = "Line";
    /// <summary>워커가 실행에 필요한 것 (데이터셋 export·사전학습 파일·스크립트) 또는 Engineer 이상</summary>
    public const string WorkerOrEngineer = "WorkerOrEngineer";

    public static void AddMlopsPolicies(this AuthorizationOptions o)
    {
        o.AddPolicy(Viewer, p => p.RequireAuthenticatedUser().RequireRole(Roles.Viewer, Roles.Labeler, Roles.Engineer, Roles.Admin, Roles.Line));
        o.AddPolicy(Labeler, p => p.RequireAuthenticatedUser().RequireRole(Roles.Labeler, Roles.Engineer, Roles.Admin));
        o.AddPolicy(Engineer, p => p.RequireAuthenticatedUser().RequireRole(Roles.Engineer, Roles.Admin));
        o.AddPolicy(Admin, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin));
        o.AddPolicy(Worker, p => p.RequireAuthenticatedUser().RequireRole(Roles.Worker));
        o.AddPolicy(Line, p => p.RequireAuthenticatedUser().RequireRole(Roles.Line, Roles.Engineer, Roles.Admin));
        o.AddPolicy(WorkerOrEngineer, p => p.RequireAuthenticatedUser().RequireRole(Roles.Worker, Roles.Engineer, Roles.Admin));
    }
}

/// <summary>요청 주체 — 사용자 이름·역할·(워커면) 워커 ID·(라인 PC 면) 라인 ID</summary>
public sealed record CurrentUser(string Name, IReadOnlySet<string> RolesSet, Guid? WorkerId, string? LineId = null)
{
    public const string WorkerIdClaim = "mlops:workerId";
    public const string LineIdClaim = "mlops:lineId";

    public bool IsAdmin => RolesSet.Contains(Roles.Admin);
    public bool IsEngineer => IsAdmin || RolesSet.Contains(Roles.Engineer);
    public bool IsWorker => RolesSet.Contains(Roles.Worker);

    public static CurrentUser From(ClaimsPrincipal principal)
    {
        var name = principal.Identity?.Name
                   ?? principal.FindFirstValue(ClaimTypes.Name)
                   ?? principal.FindFirstValue("name")
                   ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
                   ?? principal.FindFirstValue("sub")
                   ?? "anonymous";
        var roles = principal.FindAll(ClaimTypes.Role).Select(c => c.Value)
            .Concat(principal.FindAll("role").Select(c => c.Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Guid? workerId = Guid.TryParse(principal.FindFirstValue(WorkerIdClaim), out var g) ? g : null;
        var lineId = principal.FindFirstValue(LineIdClaim);
        return new CurrentUser(name, roles, workerId, string.IsNullOrWhiteSpace(lineId) ? null : lineId);
    }
}
