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
