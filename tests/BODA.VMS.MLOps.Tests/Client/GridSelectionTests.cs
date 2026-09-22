using BODA.VMS.MLOps.Client.Shared;
using FluentAssertions;

namespace BODA.VMS.MLOps.Tests.Client;

/// <summary>
/// 사진 격자에서 고르는 법. 수집 사진과 데이터셋 상세가 같은 것을 쓰므로 여기가 두 화면의 계약이다.
///
/// <para>Shift+클릭·Ctrl+A·Esc 는 탐색기와 같지만 <b>그냥 클릭은 쌓는다</b> —
/// 사진을 골라 담는 화면이라 하나씩 눌러 모으는 것이 기본 동작이다.</para>
/// </summary>
public class GridSelectionTests
{
    // 화면에 보이는 순서. Shift+클릭의 "A 부터 H 까지" 는 이 순서를 뜻한다.
    private static readonly Guid[] Grid = Enumerable.Range(0, 8)
        .Select(i => new Guid($"{i:D8}-0000-0000-0000-000000000000")).ToArray();

    private static GridSelection New() => new();

    // 읽기 쉬우라고 감싼다 — 아래 시험에서 어느 수식키인지가 이름으로 드러난다.
    private static void Click(GridSelection s, Guid id) => s.Click(id, shift: false, ctrl: false, Grid);
    private static void CtrlClick(GridSelection s, Guid id) => s.Click(id, shift: false, ctrl: true, Grid);
    private static void ShiftClick(GridSelection s, Guid id) => s.Click(id, shift: true, ctrl: false, Grid);
    private static void CtrlShiftClick(GridSelection s, Guid id) => s.Click(id, shift: true, ctrl: true, Grid);

    [Fact]
    public void Plain_click_accumulates_and_keeps_the_others()
    {
        var s = New();
        Click(s, Grid[0]);
        Click(s, Grid[3]);

        s.Selected.Should().BeEquivalentTo([Grid[0], Grid[3]], "골라 담는 격자라 그냥 클릭은 쌓인다");
    }

    [Fact]
    public void Clicking_the_selected_one_again_removes_it()
    {
        var s = New();
        Click(s, Grid[2]);
        Click(s, Grid[2]);

        s.Selected.Should().BeEmpty();
    }

    /// <summary>Ctrl+클릭도 같은 토글이다 — 탐색기 버릇으로 눌러도 기대대로 된다.</summary>
    [Fact]
    public void Ctrl_click_behaves_the_same_as_a_plain_click()
    {
        var s = New();
        Click(s, Grid[0]);
        CtrlClick(s, Grid[3]);
        CtrlClick(s, Grid[5]);
        s.Selected.Should().BeEquivalentTo([Grid[0], Grid[3], Grid[5]]);

        CtrlClick(s, Grid[3]);
        s.Selected.Should().BeEquivalentTo([Grid[0], Grid[5]]);
    }

    [Fact]
    public void Shift_click_selects_the_range_from_the_last_click()
    {
        var s = New();
        Click(s, Grid[0]);      // A
        ShiftClick(s, Grid[7]); // H

        s.Selected.Should().BeEquivalentTo(Grid, "A 를 고르고 H 를 Shift+클릭하면 A~H 가 다 들어온다");
    }

    [Fact]
    public void Shift_click_works_backwards_too()
    {
        var s = New();
        Click(s, Grid[5]);
        ShiftClick(s, Grid[2]);

        s.Selected.Should().BeEquivalentTo([Grid[2], Grid[3], Grid[4], Grid[5]]);
    }

    /// <summary>
    /// 범위를 잡아 놓고 끝을 다시 집을 수 있어야 한다 — 기준(anchor)은 Shift+클릭으로 움직이지 않는다.
    /// 넓혔다가 줄이면 실제로 줄어야 한다.
    /// </summary>
    [Fact]
    public void Shift_click_again_re_takes_the_range_so_it_can_shrink()
    {
        var s = New();
        Click(s, Grid[1]);
        ShiftClick(s, Grid[6]);
        s.Count.Should().Be(6);

        ShiftClick(s, Grid[3]);
        s.Selected.Should().BeEquivalentTo([Grid[1], Grid[2], Grid[3]],
            "범위를 줄이면 앞서 집은 것이 남지 않고 실제로 줄어야 한다");
    }

    /// <summary>
    /// 범위를 다시 집을 때 걷어내는 것은 <b>직전 범위뿐</b>이다. 손으로 하나씩 골라 둔 것까지
    /// 쓸어 가면 "쌓아 두고 범위를 한 번 더" 가 안 된다.
    /// </summary>
    [Fact]
    public void Re_taking_a_range_keeps_what_was_picked_by_hand()
    {
        var s = New();
        Click(s, Grid[7]);          // 손으로 하나
        Click(s, Grid[1]);          // 기준
        ShiftClick(s, Grid[5]);     // 1~5
        ShiftClick(s, Grid[3]);     // 1~3 으로 줄인다

        s.Selected.Should().BeEquivalentTo([Grid[1], Grid[2], Grid[3], Grid[7]]);
    }

    /// <summary>Ctrl+Shift+클릭은 앞 범위를 걷어내지 않는다 — 떨어진 두 덩어리를 고를 때 쓴다.</summary>
    [Fact]
    public void Ctrl_shift_click_adds_a_second_range()
    {
        var s = New();
        Click(s, Grid[0]);
        ShiftClick(s, Grid[1]);      // 0~1

        CtrlClick(s, Grid[5]);       // 기준을 5 로 옮긴다
        CtrlShiftClick(s, Grid[7]);  // 5~7 을 얹는다 (0~1 은 그대로)

        s.Selected.Should().BeEquivalentTo([Grid[0], Grid[1], Grid[5], Grid[6], Grid[7]]);
    }

    /// <summary>기준 없이 Shift+클릭하면(화면에 막 들어온 상태) 그냥 하나만 고른 것과 같다.</summary>
    [Fact]
    public void Shift_click_without_an_anchor_selects_only_that_one()
    {
        var s = New();
        ShiftClick(s, Grid[4]);
        s.Selected.Should().BeEquivalentTo([Grid[4]]);
    }

    [Fact]
    public void Select_all_takes_what_is_on_screen()
    {
        var s = New();
        s.SelectAll(Grid);
        s.Count.Should().Be(8);

        Click(s, Grid[0]);
        s.Selected.Should().NotContain(Grid[0], "전부 고른 뒤 다시 누르면 그 한 장이 빠진다");
        s.Count.Should().Be(7);
    }

    [Fact]
    public void Clear_forgets_the_anchor_too()
    {
        var s = New();
        Click(s, Grid[0]);
        s.Clear();
        ShiftClick(s, Grid[5]);

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
        s.KeepOnly([Grid[1], Grid[2]]);

        s.Selected.Should().BeEquivalentTo([Grid[1], Grid[2]]);
    }

    /// <summary>기준이 걸러져 사라졌으면 범위를 셀 수 없다. 그때는 누른 것만 고른다.</summary>
    [Fact]
    public void A_range_whose_anchor_vanished_falls_back_to_one()
    {
        var s = New();
        Click(s, Grid[0]);
        s.KeepOnly([Grid[4], Grid[5]]);

        s.Click(Grid[5], shift: true, ctrl: false, [Grid[4], Grid[5]]);
        s.Selected.Should().BeEquivalentTo([Grid[5]]);
    }
}
