using BODA.VMS.MLOps.Client.Shared;
using FluentAssertions;

namespace BODA.VMS.MLOps.Tests.Client;

/// <summary>
/// 사진 격자에서 고르는 법 (파일 탐색기 규칙). 수집 사진과 데이터셋 상세가 같은 것을 쓰므로
/// 여기가 두 화면의 계약이다.
/// </summary>
public class GridSelectionTests
{
    // 화면에 보이는 순서. Shift+클릭의 "A 부터 H 까지" 는 이 순서를 뜻한다.
    private static readonly Guid[] Grid = Enumerable.Range(0, 8)
        .Select(i => new Guid($"{i:D8}-0000-0000-0000-000000000000")).ToArray();

    private static GridSelection New() => new();

    [Fact]
    public void Plain_click_toggles_and_keeps_the_others()
    {
        var s = New();
        s.Click(Grid[0], shift: false, Grid);
        s.Click(Grid[3], shift: false, Grid);
        s.Selected.Should().BeEquivalentTo([Grid[0], Grid[3]], "골라 담는 격자라 그냥 클릭은 쌓인다");

        s.Click(Grid[0], shift: false, Grid);
        s.Selected.Should().BeEquivalentTo([Grid[3]], "같은 것을 다시 누르면 빠진다");
    }

    [Fact]
    public void Shift_click_selects_the_range_from_the_last_click()
    {
        var s = New();
        s.Click(Grid[0], shift: false, Grid);   // A
        s.Click(Grid[7], shift: true, Grid);    // H

        s.Selected.Should().BeEquivalentTo(Grid, "A 를 고르고 H 를 Shift+클릭하면 A~H 가 다 들어온다");
    }

    [Fact]
    public void Shift_click_works_backwards_too()
    {
        var s = New();
        s.Click(Grid[5], shift: false, Grid);
        s.Click(Grid[2], shift: true, Grid);

        s.Selected.Should().BeEquivalentTo([Grid[2], Grid[3], Grid[4], Grid[5]]);
    }

    /// <summary>
    /// 범위를 잡아 놓고 끝을 다시 집을 수 있어야 한다 — 기준(anchor)은 Shift+클릭으로 움직이지 않는다.
    /// 탐색기가 그렇게 동작하고, 그렇지 않으면 한 칸 더 넓히려다 범위가 통째로 어긋난다.
    /// </summary>
    [Fact]
    public void Shift_click_can_be_redone_to_a_wider_range()
    {
        var s = New();
        s.Click(Grid[1], shift: false, Grid);
        s.Click(Grid[3], shift: true, Grid);
        s.Click(Grid[6], shift: true, Grid);

        s.Selected.Should().Contain([Grid[1], Grid[2], Grid[3], Grid[4], Grid[5], Grid[6]]);
        s.Selected.Should().NotContain(Grid[0]);
        s.Selected.Should().NotContain(Grid[7]);
    }

    /// <summary>기준 없이 Shift+클릭하면(화면에 막 들어온 상태) 그냥 하나만 골린 것과 같다.</summary>
    [Fact]
    public void Shift_click_without_an_anchor_selects_only_that_one()
    {
        var s = New();
        s.Click(Grid[4], shift: true, Grid);
        s.Selected.Should().BeEquivalentTo([Grid[4]]);
    }

    /// <summary>이미 고른 것 위로 범위가 지나가도 빠지지 않는다 — 범위는 더하기다.</summary>
    [Fact]
    public void Shift_click_adds_and_never_removes()
    {
        var s = New();
        s.Click(Grid[6], shift: false, Grid);
        s.Click(Grid[0], shift: false, Grid);
        s.Click(Grid[2], shift: true, Grid);

        s.Selected.Should().BeEquivalentTo([Grid[0], Grid[1], Grid[2], Grid[6]]);
    }

    [Fact]
    public void Select_all_takes_what_is_on_screen()
    {
        var s = New();
        s.SelectAll(Grid);
        s.Count.Should().Be(8);

        // 전부 고른 뒤 Shift+클릭의 기준은 마지막 장이다.
        s.Click(Grid[0], shift: false, Grid);
        s.Selected.Should().NotContain(Grid[0], "고른 것을 다시 누르면 빠진다");
    }

    [Fact]
    public void Clear_forgets_the_anchor_too()
    {
        var s = New();
        s.Click(Grid[0], shift: false, Grid);
        s.Clear();
        s.Click(Grid[5], shift: true, Grid);

        s.Selected.Should().BeEquivalentTo([Grid[5]], "해제한 뒤에는 기준이 남아 있으면 안 된다");
    }

    /// <summary>
    /// 더 불러오거나 거르개를 바꾸면 화면에서 사라지는 사진이 생긴다. 안 보이는 것을 고른 채로
    /// 두면 다음에 누르는 [담기]·[삭제] 가 화면에 없는 사진에 걸린다.
    /// </summary>
    [Fact]
    public void Reloading_drops_what_is_no_longer_on_screen()
    {
        var s = New();
        s.SelectAll(Grid);

        var visible = new HashSet<Guid> { Grid[1], Grid[2] };
        s.KeepOnly(visible);

        s.Selected.Should().BeEquivalentTo([Grid[1], Grid[2]]);
    }

    /// <summary>기준이 걸러져 사라졌으면 범위를 셀 수 없다. 그때는 누른 것만 고른다.</summary>
    [Fact]
    public void A_range_whose_anchor_vanished_falls_back_to_one()
    {
        var s = New();
        s.Click(Grid[0], shift: false, Grid);
        s.KeepOnly([Grid[4], Grid[5]]);

        s.Click(Grid[5], shift: true, [Grid[4], Grid[5]]);
        s.Selected.Should().BeEquivalentTo([Grid[5]]);
    }
}
