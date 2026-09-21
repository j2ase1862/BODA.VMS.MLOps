using System.Net;
using System.Net.Http.Json;
using BODA.VMS.MLOps.Contracts.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// 로그인 화면이 운영 웹을 찾아가는 길.
///
/// <para>
/// 로그인 자체는 <b>브라우저가</b> 운영 웹에 직접 붙어서 한다 — 서버가 대신 부르면 비밀번호가
/// 우리 서버를 지나가고, 운영 웹의 로그인 제한(IP 당 5회/분)을 공장 전체가 나눠 쓰게 된다.
/// 그래서 서버가 하는 일은 "어디로 가면 되는지" 알려 주는 것과, 실패했을 때 원인을 갈라 주는 것뿐이다.
/// </para>
/// </summary>
public class WebLoginEndpointTests
{
    [Fact]
    public async Task 로그인하기_전에_물어야_하므로_익명으로_열려_있다()
    {
        using var f = new MlopsApiFactory();
        var anon = f.CreateClient();

        var res = await anon.GetAsync("/api/auth/web-login");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task 운영_웹_주소가_없으면_빈_값을_준다()
    {
        // 기본 팩토리는 Monitoring:ProductionWebUrl 을 비워 둔다 — 시험이 실제 웹으로 나가지 않게.
        using var f = new MlopsApiFactory();
        var anon = f.CreateClient();

        var info = await anon.GetFromJsonAsync<WebLoginInfo>("/api/auth/web-login", MlopsApiFactory.Json);

        // 화면은 이 경우 아이디·비밀번호 칸을 내놓지 않고 토큰 붙여넣기만 보여 준다.
        info!.WebBaseUrl.Should().BeEmpty();
    }

    [Fact]
    public async Task 모니터링_주소를_그대로_쓴다()
    {
        // 보통 운영 웹은 하나다. 두 번 적게 하면 한쪽만 고쳐 놓고 헤맨다.
        using var f = new WebUrlFactory("http://web.example:5292", auth: "");
        var anon = f.CreateClient();

        var info = await anon.GetFromJsonAsync<WebLoginInfo>("/api/auth/web-login", MlopsApiFactory.Json);
        info!.WebBaseUrl.Should().Be("http://web.example:5292");
    }

    [Fact]
    public async Task 로그인용_주소를_따로_주면_그것이_이긴다()
    {
        using var f = new WebUrlFactory("http://internal.example:5292", auth: "http://login.example:5292");
        var anon = f.CreateClient();

        var info = await anon.GetFromJsonAsync<WebLoginInfo>("/api/auth/web-login", MlopsApiFactory.Json);
        info!.WebBaseUrl.Should().Be("http://login.example:5292");
    }

    [Fact]
    public async Task 주소가_없으면_도달_확인도_그렇게_답한다()
    {
        using var f = new MlopsApiFactory();
        var anon = f.CreateClient();

        var r = await anon.GetFromJsonAsync<WebReachability>("/api/auth/web-reachable", MlopsApiFactory.Json);
        r!.Reachable.Should().BeFalse();
        r.Message.Should().Contain("설정되지");
    }

    [Fact]
    public async Task 닿지_않는_주소면_서버도_못_닿는다고_답한다()
    {
        // 브라우저 로그인이 실패했을 때 화면이 이걸 물어 CORS 와 네트워크 단절을 가른다.
        // 여기서는 아무도 듣지 않는 포트라 "서버도 못 닿는다" 가 나와야 한다.
        using var f = new WebUrlFactory("http://127.0.0.1:1", auth: "");
        var anon = f.CreateClient();

        var r = await anon.GetFromJsonAsync<WebReachability>("/api/auth/web-reachable", MlopsApiFactory.Json);
        r!.Reachable.Should().BeFalse();
        r.Message.Should().Contain("닿지 못합니다");
    }

    /// <summary>운영 웹 주소를 준 서버. 실제로 그 주소에 붙지는 않는다.</summary>
    private sealed class WebUrlFactory(string monitoring, string auth) : MlopsApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Monitoring:ProductionWebUrl", monitoring);
            builder.UseSetting("Auth:WebBaseUrl", auth);
        }
    }
}
