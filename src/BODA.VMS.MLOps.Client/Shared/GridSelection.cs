namespace BODA.VMS.MLOps.Client.Shared;

/// <summary>
/// 사진 격자의 선택 상태. 수집 사진과 데이터셋 상세가 함께 쓴다 — 두 화면에서 고르는 법이
/// 다르면 사람이 매번 다시 배워야 한다.
///
/// <para><b>파일 탐색기와 같은 규칙이다.</b></para>
/// <list type="table">
/// <item><term>클릭</term><description>이것 하나만 남기고 나머지는 해제</description></item>
/// <item><term>Ctrl+클릭</term><description>이것만 더하거나 뺀다 (나머지는 그대로)</description></item>
/// <item><term>Shift+클릭</term><description>기준부터 여기까지로 <b>갈아 끼운다</b></description></item>
/// <item><term>Ctrl+Shift+클릭</term><description>기준부터 여기까지를 지금 고른 것에 <b>더한다</b></description></item>
/// <item><term>Ctrl+A · Esc</term><description>보이는 것 전부 · 선택 해제</description></item>
/// </list>
///
/// <para>고른 것을 클릭으로 풀 수는 없다 — 탐색기가 그렇다. Ctrl+클릭이나 Esc 를 쓴다.</para>
/// </summary>
public sealed class GridSelection
{
    private readonly HashSet<Guid> _selected = [];

    /// <summary>Shift+클릭이 어디서부터 셀지의 기준. 마지막으로 그냥 클릭한 자리다.</summary>
    private Guid? _anchor;

    public IReadOnlySet<Guid> Selected => _selected;
    public int Count => _selected.Count;
    public bool Contains(Guid id) => _selected.Contains(id);
    public Guid[] ToArray() => _selected.ToArray();

    public void Clear()
    {
        _selected.Clear();
        _anchor = null;
    }

    /// <summary>
    /// 격자에서 사진 하나를 눌렀을 때. <paramref name="ordered"/> 는 <b>화면에 보이는 순서</b>여야
    /// 한다 — Shift+클릭의 "A 부터 H 까지" 는 사람이 보고 있는 그 줄 순서를 뜻하지, 담긴 순서가 아니다.
    /// </summary>
    public void Click(Guid id, bool shift, bool ctrl, IReadOnlyList<Guid> ordered)
    {
        if (shift && _anchor is { } anchor && !anchor.Equals(id))
        {
            // Shift 만이면 범위로 갈아 끼우고, Ctrl 을 함께 누르면 지금 고른 것에 더한다.
            // 갈아 끼우지 않으면 범위를 줄이려 해도 앞서 고른 것이 남아 줄지 않는다.
            if (!ctrl) _selected.Clear();
            SelectRange(anchor, id, ordered);
            // 기준은 그대로 둔다. 범위를 잡아 놓고 Shift 로 끝을 다시 집을 수 있어야 한다.
            return;
        }

        if (ctrl)
        {
            if (!_selected.Add(id)) _selected.Remove(id);
        }
        else
        {
            // 탐색기와 같다 — 그냥 클릭은 이것 하나만 남긴다.
            // 고른 것을 다시 눌러도 풀리지 않는다. 푸는 것은 Ctrl+클릭이나 Esc 다.
            _selected.Clear();
            _selected.Add(id);
        }
        _anchor = id;
    }

    /// <summary>두 사진 사이(양끝 포함)를 고른다. 이미 골라 둔 것은 그대로 둔다.</summary>
    private void SelectRange(Guid from, Guid to, IReadOnlyList<Guid> ordered)
    {
        int a = IndexOf(ordered, from), b = IndexOf(ordered, to);
        // 기준이 걸러져 화면에서 사라졌으면 범위를 셀 수 없다. 그때는 누른 것만 고른다.
        if (a < 0 || b < 0) { _selected.Add(to); _anchor = to; return; }
        if (a > b) (a, b) = (b, a);

        for (int i = a; i <= b; i++) _selected.Add(ordered[i]);
    }

    private static int IndexOf(IReadOnlyList<Guid> ordered, Guid id)
    {
        for (int i = 0; i < ordered.Count; i++)
            if (ordered[i].Equals(id)) return i;
        return -1;
    }

    /// <summary>보이는 것 전부. 더 불러올 것이 남아 있어도 <b>불러온 것까지</b>만 고른다.</summary>
    public void SelectAll(IReadOnlyList<Guid> ordered)
    {
        foreach (var id in ordered) _selected.Add(id);
        _anchor = ordered.Count > 0 ? ordered[^1] : null;
    }

    /// <summary>목록을 다시 읽었을 때 사라진 사진을 선택에서 뺀다.</summary>
    public void KeepOnly(IReadOnlyCollection<Guid> visible)
    {
        _selected.IntersectWith(visible);
        if (_anchor is { } anchor && !visible.Contains(anchor)) _anchor = null;
    }
}
