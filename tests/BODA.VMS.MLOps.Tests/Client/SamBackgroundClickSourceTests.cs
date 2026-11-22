using System.Text.RegularExpressions;
using FluentAssertions;

namespace BODA.VMS.MLOps.Tests.Client;

/// <summary>
/// SAM 의 빼는 점(배경 점)을 찍는 키 (2026-11-22 매뉴얼 대조).
/// 예전 매뉴얼은 "빼고 싶은 부분을 Ctrl+클릭" 이라 했는데 캔버스는 Shift+클릭 · 오른쪽 클릭만 받아,
/// 그대로 하면 빼려던 곳이 물체 점으로 찍혀 오히려 더 들어갔다. 세 가지를 모두 같은 뜻으로 받는다.
/// </summary>
public class SamBackgroundClickSourceTests
{
    private static string CanvasScript()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "src", "BODA.VMS.MLOps.Client", "wwwroot", "js", "label-canvas.js");
            if (File.Exists(f)) return File.ReadAllText(f);
        }
        throw new FileNotFoundException("label-canvas.js 를 찾지 못했습니다");
    }

    [Fact]
    public void Background_point_accepts_right_click_shift_and_ctrl()
    {
        var fn = Regex.Match(CanvasScript(), """export function isSamBackgroundClick\(e\) \{(?<body>[^}]*)\}""");

        fn.Success.Should().BeTrue("빼는 점 판정은 isSamBackgroundClick 한 곳에서 한다");
        fn.Groups["body"].Value.Should()
            .Contain("e.button === 2").And.Contain("e.shiftKey").And.Contain("e.ctrlKey").And.Contain("e.metaKey");
    }

    [Fact]
    public void Sam_points_are_added_through_that_one_rule()
    {
        var js = CanvasScript();

        // 예전처럼 Shift·오른쪽 클릭만 직접 보면 Ctrl+클릭이 다시 물체 점이 된다
        js.Should().Contain("this.addSamPoint(image, !isSamBackgroundClick(e))");
        Regex.Matches(js, """this\.addSamPoint\(""").Count.Should().Be(1, "SAM 점을 찍는 길은 하나여야 판정이 갈리지 않는다");
    }

    [Fact]
    public void Ctrl_click_on_a_vertex_still_removes_the_vertex_first()
    {
        var js = CanvasScript();

        // SAM 모드에서도 꼭짓점 편집이 SAM 점보다 먼저다 — Ctrl+클릭이 꼭짓점 위면 꼭짓점을 뺀다
        var sam = js.IndexOf("if (this.mode === 'sam') {", StringComparison.Ordinal);
        var edit = js.IndexOf("this.beginVertexEdit(pos, image, e)", sam, StringComparison.Ordinal);
        var add = js.IndexOf("this.addSamPoint(image", sam, StringComparison.Ordinal);
        sam.Should().BeGreaterThan(0);
        edit.Should().BeGreaterThan(sam).And.BeLessThan(add);
    }
}
