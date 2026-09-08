namespace BODA.VMS.MLOps.Contracts.Datasets;

// SAM 보조 라벨링 (개발 문서 §5.4) — 이미지 위를 클릭하면 그 객체의 폴리곤을 돌려준다.

/// <summary>SAM 을 쓸 수 있는 상태인지. 화면은 이 값으로 SAM 버튼을 보이거나 숨긴다.</summary>
public sealed record SamStatusDto(
    /// <summary>설정에 모델 경로가 있고 파일이 있다</summary>
    bool Available,
    /// <summary>모델을 이미 올려 둬서 바로 응답할 수 있다</summary>
    bool Ready,
    /// <summary>쓸 수 없을 때 그 이유 (화면 안내에 그대로 쓴다)</summary>
    string? Message = null);

/// <summary>클릭 한 점. 좌표는 0~1 정규화. Foreground=false 면 "여기는 빼라"는 뜻이다.</summary>
public sealed record SamPointDto(double X, double Y, bool Foreground = true);

public sealed record SamPrepareRequest(Guid ImageId);

public sealed record SamPredictRequest(Guid ImageId, IReadOnlyList<SamPointDto> Points);

/// <summary>SAM 이 찾아낸 도형. 폴리곤과 그 외접 박스를 함께 준다 (검출은 박스로 붙일 수도 있어서).</summary>
public sealed record SamMaskDto(
    double[][] Points,
    double X, double Y, double W, double H,
    /// <summary>모델이 스스로 매긴 IoU 예측값 (0~1). 낮으면 클릭을 더 찍으라고 안내한다.</summary>
    double Score,
    /// <summary>차지한 마스크 픽셀 수</summary>
    int PixelArea);

/// <summary>못 찾았으면 Mask 가 null 이고 Message 에 이유가 담긴다 (오류가 아니라 흔한 결과다).</summary>
public sealed record SamPredictResponse(SamMaskDto? Mask, string? Message = null);
