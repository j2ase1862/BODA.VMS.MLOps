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

    /// <summary>
    /// 이 서버에서 쓰지 않는 학습 스크립트 — 제출 자체를 막고 화면에서도 감춘다.
    ///
    /// <para><b>기본값이 train_yolo 인 이유.</b> Ultralytics YOLO 는 AGPL-3.0 이라 상용 배포에 제약이
    /// 있어 검출 백본을 D-FINE(Apache 2.0) 으로 옮겼고, 그래서 <c>ultralytics</c> 는 워커 패키지 허용
    /// 목록(<c>scripts/requirements-allowlist.txt</c>)에도 <b>일부러 없다</b>. 그런데 제출은 막히지
    /// 않아서, 고르면 라이선스 확인 문구까지 받아 놓고 워커에서 "ultralytics가 설치되지 않았습니다" 로
    /// 실패했다 — 몇 분 기다린 끝에야 알게 되는 길이다 (2026-09-16 확인).</para>
    ///
    /// <para>Enterprise License 를 갖춘 현장이라면 이 목록을 비우고 워커 허용 목록에 ultralytics 를
    /// 추가하면 된다. 코드를 고칠 필요는 없다.</para>
    /// </summary>
    /// <para><b>배열이 아니라 쉼표 구분 문자열인 이유.</b> ConfigurationBinder 는 배열을 덮어쓰지 않고
    /// <b>기존 값 뒤에 이어 붙인다</b> — 배열로 두면 설정으로 목록을 비울 수가 없다(시험에서 드러났다).
    /// 문자열은 그대로 대체되므로 빈 값으로 두면 전부 열린다.</para>
    public string DisabledScripts { get; set; } = nameof(Core.Domain.TrainingScript.TrainYolo);

    /// <summary>스크립트가 이 서버에서 쓸 수 있는지. 이름 비교는 대소문자를 가리지 않는다.</summary>
    public bool IsScriptEnabled(Core.Domain.TrainingScript script) =>
        !DisabledScripts
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(d => string.Equals(d, script.ToString(), StringComparison.OrdinalIgnoreCase));

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
    /// 운영 웹의 기계용 API 키(<c>X-API-Key</c>). 라인 PC 가 쓰는 것과 같은 키다.
    ///
    /// <para><b>왜 JWT 가 아닌가.</b> 예전에는 두 서버가 같은 서명 키를 쓴다는 점을 이용해 MLOps 가
    /// 스스로 토큰을 만들어 붙었다. 그런데 운영 웹이 토큰 세대 검사(AccessTokenVersionGuard)를 넣으면서
    /// <b>모든 토큰에 "Web 사용자 번호"를 요구</b>하게 됐고, 사람 계정이 아닌 서비스 토큰은 그 클레임이
    /// 없어 서명·발급자·수신자가 다 맞아도 401 이 났다. 모니터링은 실패해도 경고 한 줄만 남기고 나머지가
    /// 그대로 돌아, 한동안 아무도 몰랐다 (2026-09-16 확인).</para>
    ///
    /// <para>운영 웹은 이 호출을 <b>기계용 endpoint</b> 로 바꿔 X-API-Key 로 지킨다 — 라인 PC 와 같은 방식이다.
    /// 비워 두면 헤더를 붙이지 않는다. 운영 웹이 호환 모드(<c>ClientApiKey:Required=false</c>)면 그래도 통하지만,
    /// 키 강제 모드로 넘어가는 순간 막히므로 현장 배포에는 채워 두는 것이 맞다.</para>
    /// </summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// 운영 웹이 검증하는 JWT audience.
    ///
    /// <para>
    /// 우리 것과 다르다. BODA.VMS.Web 은 발급은 <c>BODA.VMS.Web</c> 이름으로 하고
    /// 검증은 <c>BODA.VMS.Web.Client</c> 로 한다. 이 값을 우리 것으로 두면 요청이 401 로 돌아온다 —
    /// 서명 키가 같아도 그렇다.
    /// </para>
    /// <para>지금은 기계용 endpoint 라 쓰이지 않지만, 구버전 운영 웹(이 변경 이전)에 붙을 때를 위해 남긴다.</para>
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

    /// <summary>
    /// 역할 표(<c>Members</c>)에 없는 계정이 받는 역할. 운영 웹으로 로그인은 되지만 우리가 아직
    /// 역할을 주지 않은 사람이다. 기본 <c>Viewer</c> — 보기만 하고 아무것도 바꾸지 못한다.
    ///
    /// <para>
    /// 비워 두면 역할이 없어 모든 화면이 막힌다. 표에 올린 사람만 들어오게 하려면 그렇게 둔다.
    /// </para>
    /// </summary>
    public string DefaultRole { get; set; } = Auth.Roles.Viewer;

    /// <summary>
    /// 이 역할을 달고 온 토큰은 표에 없어도 MLOps <c>Admin</c> 으로 인정한다. 운영 웹의 역할 이름이다.
    ///
    /// <para>
    /// <b>없으면 아무도 첫 역할을 줄 수 없다.</b> 표가 비어 있는 새 서버에서 관리자가 들어와
    /// 사람들에게 역할을 붙이려면 이 통로가 필요하다. 표에 그 계정의 줄이 있으면 그쪽이 이긴다 —
    /// 운영 웹 관리자를 MLOps 에서는 Viewer 로 낮출 수 있다.
    /// </para>
    /// <para>
    /// 사람을 다 올린 뒤 비워 두면 운영 웹 관리자라는 이유만으로는 못 들어온다.
    /// </para>
    /// </summary>
    public string BootstrapWebRole { get; set; } = "Admin";
}
