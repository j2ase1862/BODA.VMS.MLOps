namespace BODA.VMS.MLOps.Client.Shared;

/// <summary>
/// 사진 격자의 선택 상태. 수집 사진과 데이터셋 상세가 함께 쓴다 — 두 화면에서 고르는 법이
/// 다르면 사람이 매번 다시 배워야 한다.
///
/// <list type="table">
/// <item><term>클릭 · Ctrl+클릭</term><description>이것만 더하거나 뺀다 (나머지는 그대로)</description></item>
/// <item><term>Shift+클릭</term><description>기준부터 여기까지. 다시 하면 <b>그 범위만</b> 새로 잡는다</description></item>
/// <item><term>Ctrl+Shift+클릭</term><description>앞 범위를 둔 채 떨어진 범위를 하나 더</description></item>
/// <item><term>Ctrl+A · Esc</term><description>보이는 것 전부 · 선택 해제</description></item>
/// </list>
///
/// <para><b>그냥 클릭은 탐색기와 달리 쌓는다.</b> 이 격자는 사진을 골라 담는 곳이라
/// 하나씩 눌러 모으는 것이 기본 동작이다 — 탐색기처럼 "이것만 남기고 해제" 로 두면
/// 열 장 고르는 데 Ctrl 을 아홉 번 눌러야 한다. Ctrl+클릭도 같은 토글이라 탐색기 버릇도 통한다.</para>
/// </summary>
public sealed class GridSelection
{
    private readonly HashSet<Guid> _selected = [];

    /// <summary>Shift+클릭이 어디서부터 셀지의 기준. 마지막으로 그냥 클릭한 자리다.</summary>
    private Guid? _anchor;

    /// <summary>
    /// 직전 Shift+클릭이 집은 것들. 다시 Shift+클릭하면 <b>이것만</b> 걷어내고 새로 집는다 —
    /// 걷어내지 않으면 범위를 줄이려 해도 앞서 집은 것이 남아 줄지 않는다.
    /// 손으로 하나씩 고른 것은 이 집합 밖이라 그대로 남는다.
    /// </summary>
    private readonly HashSet<Guid> _lastRange = [];

    public IReadOnlySet<Guid> Selected => _selected;
    public int Count => _selected.Count;
    public bool Contains(Guid id) => _selected.Contains(id);
    public Guid[] ToArray() => _selected.ToArray();

    public void Clear()
    {
        _selected.Clear();
        _lastRange.Clear();
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
            // 직전에 Shift 로 집은 범위만 걷어내고 새로 집는다 — 손으로 고른 것은 건드리지 않는다.
            // Ctrl 을 함께 누르면 걷어내지 않으므로 떨어진 범위를 하나 더 얹을 수 있다.
            if (!ctrl) _selected.ExceptWith(_lastRange);
            _lastRange.Clear();
            SelectRange(anchor, id, ordered);
            // 기준은 그대로 둔다. 범위를 잡아 놓고 Shift 로 끝을 다시 집을 수 있어야 한다.
            return;
        }

        // 그냥 클릭도 Ctrl+클릭도 토글이다. 사진을 골라 담는 화면이라 하나씩 눌러 모으는 것이 기본이다.
        if (!_selected.Add(id)) _selected.Remove(id);
        _anchor = id;
        _lastRange.Clear();
    }

    /// <summary>두 사진 사이(양끝 포함)를 고른다. 이미 골라 둔 것은 그대로 둔다.</summary>
    private void SelectRange(Guid from, Guid to, IReadOnlyList<Guid> ordered)
    {
        int a = IndexOf(ordered, from), b = IndexOf(ordered, to);
        // 기준이 걸러져 화면에서 사라졌으면 범위를 셀 수 없다. 그때는 누른 것만 고른다.
        if (a < 0 || b < 0) { _selected.Add(to); _anchor = to; _lastRange.Clear(); return; }
        if (a > b) (a, b) = (b, a);

        for (int i = a; i <= b; i++)
        {
            _selected.Add(ordered[i]);
            _lastRange.Add(ordered[i]);
        }
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
        _lastRange.Clear();
        foreach (var id in ordered) _selected.Add(id);
        _anchor = ordered.Count > 0 ? ordered[^1] : null;
    }

    /// <summary>목록을 다시 읽었을 때 사라진 사진을 선택에서 뺀다.</summary>
    public void KeepOnly(IReadOnlyCollection<Guid> visible)
    {
        _selected.IntersectWith(visible);
        _lastRange.IntersectWith(visible);
        if (_anchor is { } anchor && !visible.Contains(anchor)) _anchor = null;
    }
}
