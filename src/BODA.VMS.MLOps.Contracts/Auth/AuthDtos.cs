namespace BODA.VMS.MLOps.Contracts.Auth;

/// <summary>
/// 로그인 화면에 알려 주는 운영 웹 주소. 비어 있으면 아이디·비밀번호 로그인을 쓸 수 없고
/// 토큰 붙여넣기만 남는다.
/// </summary>
public sealed record WebLoginInfo(string WebBaseUrl);

/// <summary>
/// 서버에서 본 운영 웹 도달 여부. 브라우저 로그인이 실패했을 때만 묻는다 —
/// 브라우저는 CORS 거부와 네트워크 단절을 같은 오류로 보여 주기 때문이다.
/// </summary>
public sealed record WebReachability(bool Reachable, string Message);

/// <summary>
/// 이 서버가 사람을 어떻게 들이는지. 로그인 화면이 무엇을 그릴지 정하는 데 쓴다 (로그인 전이라 익명).
/// </summary>
/// <param name="Mode">"Web" 이면 운영 웹이 인증하고, "Local" 이면 이 서버의 계정으로 로그인한다.</param>
/// <param name="WebBaseUrl">Web 모드에서 브라우저가 로그인할 주소. Local 모드에서는 비어 있다.</param>
public sealed record AuthModeInfo(string Mode, string WebBaseUrl);

/// <summary>자체 계정 로그인. Local 모드에서만 열려 있다 (Web 모드에서는 404).</summary>
public sealed record LocalLoginRequest(string Username, string Password);

/// <summary>
/// 로그인 결과.
/// </summary>
/// <param name="Token">우리가 발급한 JWT. 수명은 <c>Auth:Local:TokenHours</c>(기본 8시간)이고 갱신은 없다.</param>
/// <param name="MustChangePassword">
/// 임시 비밀번호 상태. 이 토큰으로는 비밀번호 변경 말고 아무것도 못 한다 — 화면은 곧장 변경으로 보낸다.
/// </param>
public sealed record LocalLoginResponse(string Token, string Name, string? DisplayName, string Role, bool MustChangePassword);

/// <summary>비밀번호 바꾸기. 성공하면 새 토큰이 나오고, 다른 자리에 남아 있던 세션은 그 즉시 끊긴다.</summary>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>관리자가 임시 비밀번호를 새로 내준 결과. <b>이 응답에서 한 번만 나간다</b> — 서버에는 해시만 남는다.</summary>
public sealed record TemporaryPasswordResponse(string Username, string TemporaryPassword);
