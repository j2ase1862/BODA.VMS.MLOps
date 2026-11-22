using System.Text.RegularExpressions;
using FluentAssertions;

namespace BODA.VMS.MLOps.Tests.Client;

/// <summary>
/// 화면 소스 규칙 (2026-11-22 매뉴얼 대조).
/// 역할로 막힌 화면으로 가는 메뉴·버튼은 같은 역할의 <c>AuthorizeView</c> 안에 둔다 — 누구에게나 보이는데
/// 누르면 "이 화면에 필요한 권한이 없습니다" 만 나오는 길을 만들지 않는다. 권한 정책 자체는 화면(@attribute)과 서버가 정한다.
/// </summary>
public class ClientRoleGateSourceTests
{
    private static string ClientDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "src", "BODA.VMS.MLOps.Client");
            if (Directory.Exists(Path.Combine(c, "Pages"))) return c;
        }
        throw new DirectoryNotFoundException("src/BODA.VMS.MLOps.Client 를 찾지 못했습니다");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([ClientDir(), .. parts]));

    /// <summary>화면 파일의 <c>@attribute [Authorize(Roles = "…")]</c> 가 받는 역할</summary>
    private static string[] PageRoles(string pageFile)
    {
        var m = Regex.Match(Read("Pages", pageFile), """@attribute \[Authorize\(Roles = "(?<r>[^"]+)"\)\]""");
        m.Success.Should().BeTrue($"{pageFile} 는 역할로 막힌 화면이어야 이 시험이 뜻이 있다");
        return m.Groups["r"].Value.Split(',', StringSplitOptions.TrimEntries);
    }

    /// <summary><paramref name="marker"/> 를 감싸고 있는 가장 안쪽 <c>AuthorizeView Roles</c> 의 역할. 감싸지 않았으면 null.</summary>
    private static string[]? EnclosingRoles(string text, string marker)
    {
        var at = text.IndexOf(marker, StringComparison.Ordinal);
        at.Should().BeGreaterThan(0, $"소스에 {marker} 가 있어야 한다");

        var stack = new Stack<string?>();
        foreach (Match m in Regex.Matches(text[..at], """<AuthorizeView\b(?<attrs>[^>]*)>|</AuthorizeView>"""))
        {
            if (m.Value.StartsWith("</", StringComparison.Ordinal)) { if (stack.Count > 0) stack.Pop(); continue; }
            if (m.Value.EndsWith("/>", StringComparison.Ordinal)) continue;
            var roles = Regex.Match(m.Groups["attrs"].Value, """Roles="(?<r>[^"]+)"\s*""");
            stack.Push(roles.Success ? roles.Groups["r"].Value : null);
        }
        return stack.FirstOrDefault(r => r is not null)?.Split(',', StringSplitOptions.TrimEntries);
    }

    [Fact]
    public void Line_pc_menu_is_shown_only_to_roles_that_can_open_the_screen()
    {
        var shown = EnclosingRoles(Read("Shared", "MainLayout.razor"), "Href=\"/line-clients\"");

        shown.Should().NotBeNull("[라인 PC] 화면은 Engineer 이상만 열린다 — 메뉴도 그 역할에게만 보여야 한다");
        shown.Should().BeSubsetOf(PageRoles("LineClients.razor"));
    }

    [Fact]
    public void Admin_only_menus_stay_admin_only()
    {
        var layout = Read("Shared", "MainLayout.razor");

        EnclosingRoles(layout, "Href=\"/members\"").Should().BeSubsetOf(PageRoles("Members.razor"));
        EnclosingRoles(layout, "Href=\"/audit\"").Should().BeSubsetOf(PageRoles("AuditLog.razor"));
    }

    [Fact]
    public void Dataset_grid_opens_the_labeling_screen_only_for_roles_that_can_label()
    {
        var shown = EnclosingRoles(Read("Pages", "DatasetDetail.razor"), "/label/{image.Id}");

        shown.Should().NotBeNull("라벨링 화면은 Labeler 이상만 열린다 — Viewer 에게 격자 카드의 [라벨 보기] 를 보이지 않는다");
        shown.Should().BeSubsetOf(PageRoles("Labeling.razor"));
    }
}
