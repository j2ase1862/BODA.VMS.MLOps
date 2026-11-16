using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Server.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using static BODA.VMS.MLOps.Tests.Server.MlopsApiFactory;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// [레시피 바인딩] 의 레시피 고르기 (ML-02). 서버는 부른 사람의 토큰 그대로 운영 웹의 <b>읽기</b> API
/// (<c>/api/clients</c> · <c>/api/clients/{id}/recipes</c>)를 당겨 오고, 못 받으면 이유만 돌려준다.
/// </summary>
public class WebRecipeDirectoryTests
{
    private static string WebToken(string username, string webRole)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("test-key-0123456789abcdef0123456789abcdef"));
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken("BODA.VMS.Web", "BODA.VMS.Web",
            [new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, webRole), new Claim("tv", "1")],
            now, now.AddHours(1), new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static HttpClient As(MlopsApiFactory f, string token)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static async Task<WebRecipeListDto> ListAsync(HttpClient c)
    {
        var res = await c.GetAsync("/api/recipes");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<WebRecipeListDto>(Json))!;
    }

    [Fact]
    public async Task Web_user_gets_recipes_of_every_line_with_their_own_token_and_only_reads()
    {
        using var f = new StubWebFactory(path => path switch
        {
            "/api/clients" => (HttpStatusCode.OK, """[{"id": 2, "name": "2호기", "clientIndex": 2}, {"id": 1, "name": "1호기", "clientIndex": 1}]"""),
            "/api/clients/1/recipes" => (HttpStatusCode.OK, """[{"id": 11, "name": "PUT-10"}, {"id": 12, "name": "PUT-20"}]"""),
            "/api/clients/2/recipes" => (HttpStatusCode.OK, """[{"id": 21, "name": "BOLT"}]"""),
            _ => (HttpStatusCode.NotFound, ""),
        });
        var token = WebToken("kim", "User");   // 표에 없는 Web User → 기본 Viewer 도 볼 수 있다

        var list = await ListAsync(As(f, token));

        list.Available.Should().BeTrue();
        list.Recipes.Should().Equal(
            new WebRecipeDto("11", "PUT-10", 1, "1호기"),
            new WebRecipeDto("12", "PUT-20", 1, "1호기"),
            new WebRecipeDto("21", "BOLT", 2, "2호기"));
        f.Authorizations.Should().OnlyContain(a => a == $"Bearer {token}", "운영 웹은 부른 사람의 권한으로 판단해야 한다");
        f.Methods.Should().OnlyContain(m => m == "GET", "운영 웹에는 읽기만 한다");
    }

    [Fact]
    public async Task Refused_or_unreachable_web_gives_a_reason_not_an_error()
    {
        using var refused = new StubWebFactory(_ => (HttpStatusCode.Forbidden, ""));
        var a = await ListAsync(As(refused, WebToken("kim", "User")));
        a.Available.Should().BeFalse();
        a.Message.Should().Contain("직접 넣으세요");

        using var down = new StubWebFactory(_ => null);
        var b = await ListAsync(As(down, WebToken("kim", "User")));
        b.Available.Should().BeFalse();
        b.Message.Should().Contain("연결하지 못해");
    }

    [Fact]
    public async Task Dev_token_is_not_sent_to_the_web()
    {
        using var f = new StubWebFactory(_ => (HttpStatusCode.OK, "[]"));
        var list = await ListAsync(await f.AdminAsync());
        list.Available.Should().BeFalse();
        f.Authorizations.Should().BeEmpty("우리가 만든 토큰을 운영 웹에 들려 보내지 않는다");
    }

    [Fact]
    public async Task Without_a_web_address_it_says_so()
    {
        using var f = new MlopsApiFactory();
        var list = await ListAsync(await f.AdminAsync());
        list.Available.Should().BeFalse();
        list.Message.Should().Contain("운영 웹 주소");
    }

    /// <summary>운영 웹 자리에 세우는 가짜. 경로마다 상태·본문을 정하고, null 이면 연결 실패.</summary>
    private sealed class StubWebFactory(Func<string, (HttpStatusCode Status, string Body)?> respond) : MlopsApiFactory
    {
        private readonly object _gate = new();
        private readonly List<string> _authorizations = [];
        private readonly List<string> _methods = [];
        public IReadOnlyList<string> Authorizations { get { lock (_gate) return _authorizations.ToList(); } }
        public IReadOnlyList<string> Methods { get { lock (_gate) return _methods.ToList(); } }
        private Func<string, (HttpStatusCode Status, string Body)?> Respond { get; } = respond;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Auth:WebBaseUrl", "http://stub-web.invalid");
            builder.UseSetting("Auth:RevocationCheckSeconds", "0");
            builder.ConfigureServices(services =>
                services.AddHttpClient<WebRecipeDirectoryClient>().ConfigurePrimaryHttpMessageHandler(() => new StubHandler(this)));
        }

        private sealed class StubHandler(StubWebFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                lock (owner._gate)
                {
                    owner._authorizations.Add(request.Headers.Authorization?.ToString() ?? "");
                    owner._methods.Add(request.Method.Method);
                }
                var answer = owner.Respond(request.RequestUri!.AbsolutePath)
                             ?? throw new HttpRequestException("운영 웹에 닿지 못했습니다");
                return Task.FromResult(new HttpResponseMessage(answer.Status)
                {
                    Content = new StringContent(answer.Body, Encoding.UTF8, "application/json"),
                });
            }
        }
    }
}
