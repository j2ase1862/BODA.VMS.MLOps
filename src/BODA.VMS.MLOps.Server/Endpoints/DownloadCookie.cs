namespace BODA.VMS.MLOps.Server.Endpoints;

/// <summary>
/// 파일 받기 링크(<c>&lt;a href download&gt;</c>)에만 쓰이는 쿠키. 브라우저는 링크에 Authorization 헤더를 붙일 수 없어,
/// 화면의 [다운로드] 가 새 탭으로 열리면 401 이었다 — 데이터셋 zip · 모델 ONNX · 사전학습 파일 · 학습 아티팩트·로그 전부.
///
/// <para>받는 주소가 여러 갈래라(/api/dataset-versions · model-versions · pretrained · training-jobs) 경로로는 좁힐 수 없다.
/// 그래서 쿠키는 /api 아래로 나가되, 서버는 <see cref="DownloadCookieEndpoint"/> 표시가 붙은 GET 에서만,
/// Authorization 헤더가 없을 때만 이 쿠키를 받는다. 그 밖의 API(목록·생성·삭제…)에는 쿠키만으로 들어올 수 없다.</para>
///
/// <para>파일을 화면 메모리로 받지 않고 브라우저가 디스크로 바로 받으므로 큰 파일(데이터셋 zip 최대 50GB)도 되고,
/// 서버가 Range 를 지원하는 주소는 이어받기도 된다. 권한은 쿠키 속 토큰의 역할로 그대로 검사한다.</para>
/// </summary>
public static class DownloadCookie
{
    public const string Name = "mlops.dl";
    public const string Path = "/api";
}

/// <summary>엔드포인트 표시 — 이 GET 은 다운로드 쿠키로도 인증한다</summary>
public sealed class DownloadCookieEndpoint
{
    public static readonly DownloadCookieEndpoint Instance = new();
    private DownloadCookieEndpoint() { }
}

public static class DownloadCookieEndpointExtensions
{
    /// <summary>링크로 받는 파일 주소에 붙인다. 쿠키는 GET·HEAD 에서만 받는다.</summary>
    public static TBuilder AllowDownloadCookie<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(DownloadCookieEndpoint.Instance);
}
