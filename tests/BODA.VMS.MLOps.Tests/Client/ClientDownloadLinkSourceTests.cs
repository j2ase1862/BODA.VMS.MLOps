using System.Text.RegularExpressions;
using FluentAssertions;

namespace BODA.VMS.MLOps.Tests.Client;

/// <summary>
/// 화면 소스의 다운로드 링크 규칙 (2026-10-22 점검에서 화면의 [다운로드] 가 전부 401·403 이던 것).
/// <list type="bullet">
/// <item>새 탭(<c>Target="_blank"</c>)으로 API 를 열지 않는다 — Blazor 가 가로채지 않는 <c>download</c> 링크로 받아야
///   서버가 다운로드 쿠키로 인증한다(DownloadCookieTests).</item>
/// <item>엔지니어 이상만 받을 수 있는 파일(데이터셋 zip · 모델 ONNX · 사전학습 파일)의 버튼은
///   <c>AuthorizeView Roles="Engineer,Admin"</c> 안에 둔다 — 보이는데 누르면 403 인 버튼을 만들지 않는다.</item>
/// </list>
/// </summary>
public class ClientDownloadLinkSourceTests
{
    private static readonly Regex LinkTag = new(
        """<(?<tag>MudButton|MudIconButton|MudLink|MudListItem|MudMenuItem|MudNavLink|a)\b(?<attrs>(?:[^>"]|"[^"]*")*)>""",
        RegexOptions.Singleline);

    private static readonly Regex HrefAttr = new("""\b[Hh]ref="(?<v>(?:[^"]|"[^"]*")*?)"(?=\s|/?$|>)""", RegexOptions.Singleline);

    private static string ClientDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "src", "BODA.VMS.MLOps.Client");
            if (Directory.Exists(Path.Combine(c, "Pages"))) return c;
        }
        throw new DirectoryNotFoundException("src/BODA.VMS.MLOps.Client 를 찾지 못했습니다");
    }

    private static IEnumerable<(string File, string Text)> RazorFiles() =>
        Directory.GetFiles(ClientDir(), "*.razor", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f)));

    /// <summary>API 파일을 가리키는 링크 — 화면 경로(/models/…)는 아니고, 서버가 준 *Url 이나 /api/ 주소</summary>
    private static bool PointsToApi(string href) => href.Contains("/api/") || Regex.IsMatch(href, @"Url\b");

    [Fact]
    public void Api_links_are_download_links_not_new_tabs()
    {
        var problems = new List<string>();
        foreach (var (file, text) in RazorFiles())
        {
            foreach (Match m in LinkTag.Matches(text))
            {
                var attrs = m.Groups["attrs"].Value;
                var href = HrefAttr.Match(attrs);
                if (!href.Success || !PointsToApi(href.Groups["v"].Value)) continue;
                var line = text[..m.Index].Count(c => c == '\n') + 1;
                if (attrs.Contains("Target=\"_blank\"") || attrs.Contains("target=\"_blank\""))
                    problems.Add($"{file}:{line} 새 탭으로 API 를 연다 — 토큰이 실리지 않아 401");
                if (!Regex.IsMatch(attrs, @"\bdownload\b"))
                    problems.Add($"{file}:{line} download 속성이 없다 — Blazor 라우터가 가로채거나 쿠키 인증을 못 받는다");
            }
        }
        problems.Should().BeEmpty();
    }

    [Theory]
    [InlineData("DatasetVersions.razor")]
    [InlineData("ModelDetail.razor")]
    [InlineData("Pretrained.razor")]
    public void Engineer_only_downloads_are_hidden_from_other_roles(string page)
    {
        var text = RazorFiles().Single(f => f.File == page).Text;
        var downloads = LinkTag.Matches(text).Where(m => Regex.IsMatch(m.Groups["attrs"].Value, @"\bdownload\b")).ToList();
        downloads.Should().NotBeEmpty($"{page} 에 다운로드 버튼이 있어야 한다");

        foreach (var m in downloads)
        {
            var before = text[..m.Index];
            var open = before.LastIndexOf("<AuthorizeView Roles=\"Engineer,Admin\"", StringComparison.Ordinal);
            var close = before.LastIndexOf("</AuthorizeView>", StringComparison.Ordinal);
            var line = before.Count(c => c == '\n') + 1;
            (open > close).Should().BeTrue($"{page}:{line} 의 다운로드는 서버가 엔지니어 이상에게만 내주므로 AuthorizeView Roles=\"Engineer,Admin\" 안에 있어야 한다");
        }
    }
}
