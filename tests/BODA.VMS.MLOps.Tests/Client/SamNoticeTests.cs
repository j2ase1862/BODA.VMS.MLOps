using BODA.VMS.MLOps.Client.Shared;
using BODA.VMS.MLOps.Contracts.Datasets;
using BODA.VMS.MLOps.Core.Domain;
using FluentAssertions;

namespace BODA.VMS.MLOps.Tests.Client;

/// <summary>
/// [SAM 클릭] 이 보이지 않는 이유를 라벨링 화면이 알려 준다 (2026-11-22 매뉴얼 대조).
/// 설치 가이드는 <c>/api/sam/status</c> 를 열어 보라고 했지만 그 주소는 로그인 토큰이 있어야 열려
/// 주소창으로는 401 이다 — 이유를 볼 길이 화면에 있어야 한다.
/// </summary>
public class SamNoticeTests
{
    private static readonly SamStatusDto Off = new(false, false, "인코더 파일이 없습니다: models/sam/mobile_sam_encoder.onnx");

    [Theory]
    [InlineData(TaskType.Detection)]
    [InlineData(TaskType.Segmentation)]
    public void Unavailable_sam_gives_the_servers_reason_on_datasets_that_could_use_it(TaskType taskType)
    {
        var reason = SamNotice.UnavailableReason(Off, taskType.AllowedFor());

        reason.Should().StartWith("SAM 클릭을 쓸 수 없습니다");
        reason.Should().Contain("인코더 파일이 없습니다");
    }

    [Theory]
    [InlineData(TaskType.Classification)]
    [InlineData(TaskType.Anomaly)]
    [InlineData(TaskType.Ocr)]
    public void Datasets_that_never_offer_sam_get_no_notice(TaskType taskType)
    {
        SamNotice.UnavailableReason(Off, taskType.AllowedFor()).Should().BeNull();
    }

    [Fact]
    public void Available_sam_gives_no_notice()
    {
        SamNotice.UnavailableReason(new SamStatusDto(true, true), TaskType.Detection.AllowedFor()).Should().BeNull();
        // 파일은 있는데 아직 올리지 못한 상태도 버튼은 보인다 — 여기서 안내할 일이 아니다
        SamNotice.UnavailableReason(new SamStatusDto(true, false, "예열 실패"), TaskType.Detection.AllowedFor()).Should().BeNull();
    }

    [Fact]
    public void Before_the_status_arrives_no_notice_is_shown()
    {
        SamNotice.UnavailableReason(null, TaskType.Detection.AllowedFor()).Should().BeNull();
    }

    [Fact]
    public void Missing_message_still_says_what_to_check()
    {
        SamNotice.UnavailableReason(new SamStatusDto(false, false), TaskType.Detection.AllowedFor())
            .Should().Contain("SAM 모델 파일");
    }
}
