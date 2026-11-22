using BODA.VMS.MLOps.Client.Shared;
using BODA.VMS.MLOps.Core.Domain;
using FluentAssertions;

namespace BODA.VMS.MLOps.Tests.Client;

/// <summary>
/// 라벨링 캔버스 아래 안내 한 줄 (2026-11-18 매뉴얼 재캡처에서 분류 화면에 폴리곤 안내가 떠 있던 것).
/// 폴리곤 고치는 법은 폴리곤을 쓰는 작업 유형(검출·세그멘테이션)에서만 보인다.
/// </summary>
public class LabelingHintTests
{
    private const string PolygonEdit = "폴리곤은 꼭짓점을 끌어 고치고";

    [Theory]
    [InlineData(TaskType.Classification)]
    [InlineData(TaskType.Anomaly)]
    public void Classification_hint_has_no_polygon_guidance(TaskType taskType)
    {
        var hint = Display.LabelingHint(taskType, "classification");

        hint.Should().NotContain("폴리곤", "분류는 이미지 전체에 클래스를 붙일 뿐 도형을 그리지 않는다");
        hint.Should().NotContain("끌어서 그리기");
        hint.Should().Contain("이 사진의 클래스 붙이기");
    }

    [Theory]
    [InlineData(TaskType.Detection, "box")]
    [InlineData(TaskType.Detection, "polygon")]
    [InlineData(TaskType.Detection, "sam")]
    [InlineData(TaskType.Segmentation, "polygon")]
    [InlineData(TaskType.Segmentation, "sam")]
    public void Polygon_datasets_keep_the_polygon_edit_guidance(TaskType taskType, string mode)
    {
        var hint = Display.LabelingHint(taskType, mode);

        hint.Should().Contain(PolygonEdit);
        hint.Should().Contain("끌어서 그리기");
    }

    [Fact]
    public void Polygon_mode_adds_how_to_close()
    {
        Display.LabelingHint(TaskType.Segmentation, "polygon").Should().Contain("Enter 로 닫기");
        Display.LabelingHint(TaskType.Detection, "box").Should().NotContain("Enter 로 닫기");
    }

    [Fact]
    public void Sam_mode_adds_click_guidance()
    {
        Display.LabelingHint(TaskType.Detection, "sam").Should().Contain("객체를 클릭하면 경계를 만들어 줍니다");
        Display.LabelingHint(TaskType.Detection, "box").Should().NotContain("객체를 클릭하면");
    }

    [Fact]
    public void Ocr_has_no_polygon_guidance_because_it_cannot_draw_polygons()
    {
        Display.LabelingHint(TaskType.Ocr, "text").Should().NotContain("폴리곤");
    }

    [Fact]
    public void Before_the_dataset_loads_no_polygon_guidance_is_shown()
    {
        Display.LabelingHint(null, "box").Should().NotContain("폴리곤");
    }
}
