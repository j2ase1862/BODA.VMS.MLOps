using BODA.VMS.MLOps.Core.Inference;
using BODA.VMS.MLOps.Core.Labeling;
using FluentAssertions;

namespace BODA.VMS.MLOps.Tests.Core;

/// <summary>
/// 레터박스 좌표 규약. 여기가 어긋나면 사전 라벨이 물체에서 조금씩 밀린 채 붙는데,
/// 화면으로 보면 "그럴듯" 해서 사람이 그대로 승인해 버린다.
/// </summary>
public class LetterBoxTests
{
    [Fact]
    public void 가로로_긴_이미지는_위아래에_여백이_생긴다()
    {
        var box = LetterBox.For(1280, 640, 640);

        box.ScaledWidth.Should().Be(640);
        box.ScaledHeight.Should().Be(320);
        box.PadX.Should().Be(0);
        box.PadY.Should().Be(160);
    }

    [Fact]
    public void 세로로_긴_이미지는_좌우에_여백이_생긴다()
    {
        var box = LetterBox.For(640, 1280, 640);

        box.ScaledWidth.Should().Be(320);
        box.ScaledHeight.Should().Be(640);
        box.PadX.Should().Be(160);
        box.PadY.Should().Be(0);
    }

    [Fact]
    public void 정사각형은_여백이_없다()
    {
        var box = LetterBox.For(1000, 1000, 640);
        box.PadX.Should().Be(0);
        box.PadY.Should().Be(0);
        box.ScaledWidth.Should().Be(640);
    }

    [Fact]
    public void 입력_좌표를_원본_비율로_되돌린다()
    {
        // 1280×640 → 640×320 이 위아래 160 여백 안에 들어간다
        var box = LetterBox.For(1280, 640, 640);

        box.ToNormalized(0, 160).Should().Be((0d, 0d));           // 그림의 왼쪽 위
        box.ToNormalized(640, 480).Should().Be((1d, 1d));         // 그림의 오른쪽 아래
        var (x, y) = box.ToNormalized(320, 320);
        x.Should().BeApproximately(0.5, 1e-9);
        y.Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void 여백_바깥을_가리키면_가장자리로_잡는다()
    {
        var box = LetterBox.For(1280, 640, 640);
        box.ToNormalized(320, 10).Should().Be((0.5, 0d));
    }

    [Fact]
    public void 사각형은_좌우가_뒤집혀_들어와도_정상으로_나온다()
    {
        var box = LetterBox.For(640, 640, 640);

        var normalized = box.BoxToNormalized(400, 300, 200, 100);

        normalized.X.Should().BeApproximately(200.0 / 640, 1e-9);
        normalized.Y.Should().BeApproximately(100.0 / 640, 1e-9);
        normalized.Width.Should().BeApproximately(200.0 / 640, 1e-9);
    }

    [Fact]
    public void 크기가_0이면_거부한다()
    {
        var act = () => LetterBox.For(0, 100, 640);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

/// <summary>YOLO·D-FINE 출력 후처리와 불확실도.</summary>
public class DetectionPostProcessTests
{
    /// <summary>[channels × anchors] 행 우선 버퍼를 만든다 (YOLO output0 배치와 같다).</summary>
    private static float[] YoloBuffer(int classCount, params (float Cx, float Cy, float W, float H, int Cls, float Score)[] items)
    {
        int channels = 4 + classCount;
        int anchors = Math.Max(1, items.Length);
        var buffer = new float[channels * anchors];
        for (int a = 0; a < items.Length; a++)
        {
            var (cx, cy, w, h, cls, score) = items[a];
            buffer[0 * anchors + a] = cx;
            buffer[1 * anchors + a] = cy;
            buffer[2 * anchors + a] = w;
            buffer[3 * anchors + a] = h;
            buffer[(4 + cls) * anchors + a] = score;
        }
        return buffer;
    }

    [Fact]
    public void YOLO_상자를_원본_정규화로_돌려준다()
    {
        var box = LetterBox.For(640, 640, 640);   // 여백 없음
        var buffer = YoloBuffer(2, (320, 320, 160, 160, 1, 0.9f));

        var found = DetectionPostProcess.Yolo(buffer, channels: 6, anchors: 1, box);

        found.Should().HaveCount(1);
        found[0].ClassIndex.Should().Be(1);
        found[0].Score.Should().BeApproximately(0.9, 1e-6);
        found[0].Box.X.Should().BeApproximately(0.375, 1e-6);
        found[0].Box.Width.Should().BeApproximately(0.25, 1e-6);
    }

    [Fact]
    public void 문턱보다_낮은_점수는_버린다()
    {
        var box = LetterBox.For(640, 640, 640);
        var buffer = YoloBuffer(2, (320, 320, 100, 100, 0, 0.10f));

        DetectionPostProcess.Yolo(buffer, 6, 1, box, confidence: 0.25).Should().BeEmpty();
    }

    [Fact]
    public void 겹치는_상자는_점수가_높은_것만_남는다()
    {
        var box = LetterBox.For(640, 640, 640);
        var buffer = YoloBuffer(2,
            (320, 320, 200, 200, 0, 0.9f),
            (325, 325, 200, 200, 0, 0.6f));   // 거의 같은 자리

        var found = DetectionPostProcess.Yolo(buffer, 6, 2, box);

        found.Should().HaveCount(1);
        found[0].Score.Should().BeApproximately(0.9, 1e-6);
    }

    [Fact]
    public void 클래스가_다르면_겹쳐도_남는다()
    {
        // 같은 자리에 서로 다른 것을 봤다는 뜻이라 사람이 판단할 몫이다
        var box = LetterBox.For(640, 640, 640);
        var buffer = YoloBuffer(2,
            (320, 320, 200, 200, 0, 0.9f),
            (320, 320, 200, 200, 1, 0.8f));

        DetectionPostProcess.Yolo(buffer, 6, 2, box).Should().HaveCount(2);
    }

    [Fact]
    public void 여백이_있는_이미지도_제자리에_온다()
    {
        // 1280×640 → 위아래 160 여백. 그림 한가운데 상자는 정규화 (0.5, 0.5) 여야 한다.
        var box = LetterBox.For(1280, 640, 640);
        var buffer = YoloBuffer(1, (320, 320, 64, 32, 0, 0.8f));

        var found = DetectionPostProcess.Yolo(buffer, 5, 1, box);

        found.Should().HaveCount(1);
        var b = found[0].Box;
        (b.X + b.Width / 2).Should().BeApproximately(0.5, 1e-6);
        (b.Y + b.Height / 2).Should().BeApproximately(0.5, 1e-6);
        // 세로로 눌린 만큼 정규화 높이가 커진다 (원본 기준이므로)
        b.Height.Should().BeApproximately(32.0 / 320, 1e-6);
    }

    [Fact]
    public void 출력이_모자라면_빈_목록이다()
    {
        var box = LetterBox.For(640, 640, 640);
        DetectionPostProcess.Yolo(new float[10], channels: 6, anchors: 8400, box).Should().BeEmpty();
    }

    [Fact]
    public void DFine_상자는_원본_픽셀이라_나누기만_한다()
    {
        long[] labels = [1, 0];
        float[] boxes = [100, 50, 300, 250, 0, 0, 10, 10];
        float[] scores = [0.8f, 0.05f];

        var found = DetectionPostProcess.DFine(labels, boxes, scores, count: 2,
            originalWidth: 1000, originalHeight: 500);

        found.Should().HaveCount(1, "점수가 낮은 뒤쪽은 자리 채움이다");
        found[0].ClassIndex.Should().Be(1);
        found[0].Box.X.Should().BeApproximately(0.1, 1e-6);
        found[0].Box.Y.Should().BeApproximately(0.1, 1e-6);
        found[0].Box.Width.Should().BeApproximately(0.2, 1e-6);
        found[0].Box.Height.Should().BeApproximately(0.4, 1e-6);
    }

    [Fact]
    public void DFine_상자가_이미지_밖으로_나가면_잘라_준다()
    {
        long[] labels = [0];
        float[] boxes = [-20, -10, 1200, 700];
        float[] scores = [0.9f];

        var found = DetectionPostProcess.DFine(labels, boxes, scores, 1, 1000, 500);

        found[0].Box.X.Should().Be(0);
        found[0].Box.Y.Should().Be(0);
        (found[0].Box.X + found[0].Box.Width).Should().BeLessThanOrEqualTo(1);
        (found[0].Box.Y + found[0].Box.Height).Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public void IoU_는_완전히_겹치면_1_이고_안_겹치면_0_이다()
    {
        var a = new NormBox(0.1, 0.1, 0.2, 0.2);
        DetectionPostProcess.Iou(a, a).Should().BeApproximately(1, 1e-9);
        DetectionPostProcess.Iou(a, new NormBox(0.7, 0.7, 0.2, 0.2)).Should().Be(0);
    }

    [Fact]
    public void 아무것도_못_찾으면_가장_애매하다()
    {
        // 모델이 모르는 그림이라는 뜻이라 사람이 가장 먼저 봐야 한다
        UncertaintyScore.ForDetections([]).Should().Be(1.0);
    }

    [Fact]
    public void 가장_확신_없는_상자가_그_이미지를_대표한다()
    {
        var box = new NormBox(0.1, 0.1, 0.2, 0.2);
        var detections = new List<Detection>
        {
            new(box, 0, 0.99),
            new(box, 1, 0.30),
        };

        // 0.99 짜리가 있어도 0.3 짜리가 문제다
        UncertaintyScore.ForDetections(detections).Should().BeApproximately(0.70, 1e-9);
    }

    [Fact]
    public void 모두_확신하면_애매하지_않다()
    {
        var box = new NormBox(0.1, 0.1, 0.2, 0.2);
        // 가장 낮은 0.97 이 기준이다 (가장 높은 0.98 이 아니라)
        UncertaintyScore.ForDetections([new(box, 0, 0.98), new(box, 1, 0.97)])
            .Should().BeApproximately(0.03, 1e-9);
    }
}
