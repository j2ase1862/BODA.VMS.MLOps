namespace BODA.VMS.MLOps.Server.Services.Sam;

/// <summary>
/// appsettings "Sam" — SAM 보조 라벨링 (개발 문서 §5.4).
///
/// <para>
/// 경로가 비어 있거나 파일이 없으면 기능 자체가 꺼진다. 모델을 두지 않은 현장에서도
/// 서버가 그대로 뜨고, 라벨링 화면은 SAM 버튼만 숨긴 채 손으로 그리는 길을 계속 쓴다.
/// </para>
/// <para>
/// 모델은 MobileSAM(Apache 2.0, 허용 목록 §8). <c>scripts/export_mobile_sam.py</c> 로 만들고,
/// 인코더·디코더는 각각 <c>.onnx</c> 와 <c>.onnx.data</c> 두 파일이 한 세트다 (같은 폴더에 둬야 한다).
/// </para>
/// </summary>
public sealed class SamOptions
{
    public const string Section = "Sam";

    /// <summary>이 값을 false 로 두면 모델 파일이 있어도 쓰지 않는다.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>mobile_sam_encoder.onnx 경로. 상대 경로는 콘텐츠 루트 기준 (기본 models/sam).</summary>
    public string EncoderPath { get; set; } = "models/sam/mobile_sam_encoder.onnx";

    /// <summary>mobile_sam_decoder.onnx 경로. 상대 경로는 콘텐츠 루트 기준 (기본 models/sam).</summary>
    public string DecoderPath { get; set; } = "models/sam/mobile_sam_decoder.onnx";

    /// <summary>
    /// 메모리에 들고 있을 임베딩 개수. 한 장당 [1,256,64,64] float = 4MB 다.
    /// 라벨러는 한 번에 한 장을 보므로 몇 장이면 충분하고, 그 이상은 메모리만 먹는다.
    /// </summary>
    public int MaxCachedEmbeddings { get; set; } = 4;

    /// <summary>한 번에 받을 클릭 점 수 상한 (과도한 요청 방어)</summary>
    public int MaxPoints { get; set; } = 16;

    /// <summary>ORT intra-op 스레드 수. 0 이면 런타임 기본값.</summary>
    public int IntraOpThreads { get; set; }

    /// <summary>인코더 로드에 실패했을 때 다시 시도하기까지 기다리는 시간(초)</summary>
    public int RetryAfterFailureSec { get; set; } = 60;

    /// <summary>
    /// 상대 경로는 콘텐츠 루트 기준이다 — 개발에서는 프로젝트 폴더, 배포에서는 설치 폴더가 되므로
    /// 설정 한 줄이 두 환경에서 같은 뜻을 갖는다.
    /// </summary>
    public string? ResolvedEncoderPath(string basePath) => Resolve(EncoderPath, basePath);
    public string? ResolvedDecoderPath(string basePath) => Resolve(DecoderPath, basePath);

    private static string? Resolve(string path, string basePath)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(basePath, path));
    }
}
