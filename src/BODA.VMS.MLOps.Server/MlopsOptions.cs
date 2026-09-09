namespace BODA.VMS.MLOps.Server;

/// <summary>appsettings "Mlops" 섹션</summary>
public sealed class MlopsOptions
{
    public const string Section = "Mlops";

    /// <summary>아티팩트 스토리지 루트. 기본 %ProgramData%\BODA VMS MLOps\storage (Phase 1 §3)</summary>
    public string StorageRoot { get; set; } = "";

    /// <summary>워커에 배포하는 학습 스크립트 폴더. 기본 {BaseDirectory}\scripts</summary>
    public string ScriptsRoot { get; set; } = "";

    /// <summary>ONNX 파일 크기 상한 (기본 2GB)</summary>
    public long MaxModelBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>데이터셋 zip 크기 상한 (기본 50GB)</summary>
    public long MaxDatasetBytes { get; set; } = 50L * 1024 * 1024 * 1024;

    /// <summary>이미지 한 장의 크기 상한. 2448×2048 무압축 BMP 가 15MB 라 넉넉히 잡는다 (개발 문서 §5.2).</summary>
    public long MaxImageBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>라벨링 잠금이 유지되는 시간. 브라우저가 그냥 닫혀도 이 시간이 지나면 다른 사람이 이어받는다.</summary>
    public int LabelLockMinutes { get; set; } = 10;

    /// <summary>사전 라벨링(Active Learning)을 한 번에 받을 이미지 수 상한. 그보다 많으면 나눠 보낸다.</summary>
    public int MaxPrefillImages { get; set; } = 2000;

    public int HeartbeatSec { get; set; } = 30;
    /// <summary>하트비트 3회 소실 → Offline + WorkerLost (Phase 3 §3)</summary>
    public int HeartbeatLostSec { get; set; } = 90;
    /// <summary>Assigned 후 ack 없으면 재큐</summary>
    public int AckTimeoutSec { get; set; } = 60;
    /// <summary>워커 long-poll 최대 대기</summary>
    public int PollIntervalSec { get; set; } = 25;
    public int SupervisorIntervalSec { get; set; } = 10;
    public int MaxAttempts { get; set; } = 2;

    public string ResolvedStorageRoot() =>
        string.IsNullOrWhiteSpace(StorageRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BODA VMS MLOps", "storage")
            : StorageRoot;

    public string ResolvedScriptsRoot() =>
        string.IsNullOrWhiteSpace(ScriptsRoot) ? Path.Combine(AppContext.BaseDirectory, "scripts") : ScriptsRoot;
}

/// <summary>
/// appsettings "Monitoring" — 라인에 나간 모델을 지켜보는 설정 (Phase 5).
///
/// <para>
/// <see cref="ProductionWebUrl"/> 이 비어 있으면 이 기능은 꺼진 채로 남는다. 서버는 그대로 뜬다 —
/// 모니터링은 있으면 좋은 것이지 없으면 못 도는 것이 아니다.
/// </para>
/// </summary>
public sealed class MonitoringOptions
{
    public const string Section = "Monitoring";

    /// <summary>
    /// 운영 웹(BODA.VMS.Web) 주소. 비어 있으면 모니터링을 하지 않는다.
    /// 그쪽에 <c>/api/history/model-outcomes</c> 가 있어야 한다 (v1.9.0 이상).
    /// </summary>
    public string ProductionWebUrl { get; set; } = "";

    /// <summary>지금 상태를 보는 구간. 짧으면 흔들리고 길면 늦게 안다.</summary>
    public int RecentWindowDays { get; set; } = 7;

    /// <summary>견줄 기준 구간. 최근 구간 바로 앞의 이 기간을 쓴다.</summary>
    public int BaselineWindowDays { get; set; } = 21;

    /// <summary>한 라인만 볼 때. null 이면 전체.</summary>
    public int? ClientId { get; set; }

    /// <summary>
    /// 운영 웹이 검증하는 JWT audience.
    ///
    /// <para>
    /// 우리 것과 다르다. BODA.VMS.Web 은 발급은 <c>BODA.VMS.Web</c> 이름으로 하고
    /// 검증은 <c>BODA.VMS.Web.Client</c> 로 한다. 이 값을 우리 것으로 두면 요청이 401 로 돌아온다 —
    /// 서명 키가 같아도 그렇다.
    /// </para>
    /// </summary>
    public string Audience { get; set; } = "BODA.VMS.Web.Client";
}

/// <summary>appsettings "Jwt" — BODA.VMS.Web 와 같은 키·발급자를 쓰면 기존 로그인 토큰을 그대로 받는다</summary>
public sealed class JwtOptions
{
    public const string Section = "Jwt";
    public string Key { get; set; } = "";
    public string Issuer { get; set; } = "BODA.VMS.Web";
    public string Audience { get; set; } = "BODA.VMS.Web";
}

/// <summary>appsettings "Auth"</summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";
    /// <summary>개발/테스트용 토큰 발급 엔드포인트 (/api/auth/dev-token). 운영은 반드시 false.</summary>
    public bool EnableDevTokens { get; set; }
}
