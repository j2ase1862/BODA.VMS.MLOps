using BODA.VMS.MLOps.Contracts.Datasets;
using BODA.VMS.MLOps.Core.Domain;

namespace BODA.VMS.MLOps.Client.Shared;

/// <summary>
/// 라벨링 화면에서 [SAM 클릭] 이 보이지 않을 때 그 이유.
/// 서버는 <c>/api/sam/status</c> 의 <c>message</c> 로 이유를 주지만 그 주소는 로그인 토큰이 있어야 열려
/// 브라우저 주소창으로는 볼 수 없다 — 화면이 보여 주지 않으면 "버튼이 왜 없는지" 를 알 길이 없다.
/// </summary>
public static class SamNotice
{
    /// <summary>
    /// 안내할 이유. SAM 이 켜져 있거나, 상태를 아직 못 받았거나, 클릭 분할로 얻을 도형(사각형·폴리곤)이 없는
    /// 데이터셋(분류·이상탐지·OCR)이면 null — 그런 화면에 "꺼짐" 을 띄우면 없는 기능을 찾게 만든다.
    /// </summary>
    public static string? UnavailableReason(SamStatusDto? status, IReadOnlyCollection<AnnotationShape> shapes)
    {
        if (status is null || status.Available) return null;
        if (!shapes.Contains(AnnotationShape.Box) && !shapes.Contains(AnnotationShape.Polygon)) return null;
        return string.IsNullOrWhiteSpace(status.Message)
            ? "SAM 클릭을 쓸 수 없습니다. 서버의 SAM 모델 파일을 확인하세요."
            : $"SAM 클릭을 쓸 수 없습니다 — {status.Message}";
    }
}
