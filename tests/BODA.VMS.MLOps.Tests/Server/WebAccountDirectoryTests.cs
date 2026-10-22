using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using BODA.VMS.MLOps.Contracts.Members;
using BODA.VMS.MLOps.Server.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using static BODA.VMS.MLOps.Tests.Server.MlopsApiFactory;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// [사용자 역할] 의 [역할 주기] 는 운영 웹 계정에서 고른다. 서버는 부른 사람의 토큰 그대로 운영 웹의
/// <c>/api/admin/users</c> 에 묻고, 못 받으면 이유를 돌려준다(화면은 직접 입력으로 돌아간다).
/// </summary>
public class WebAccountDirectoryTests
{
    /// <summary>운영 웹이 내주는 것과 같은 모양의 토큰</summary>
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

    private static async Task<WebAccountListDto> ListAsync(HttpClient c)
    {
        var res = await c.GetAsync("/api/members/web-accounts");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<WebAccountListDto>(Json))!;
    }

    /// <summary>운영 웹 UserDto 를 System.Text.Json 기본(camelCase)으로 낸 모양</summary>
    private const string WebUsersJson = """
        [
          {"id": 3, "username": "kim", "displayName": "김라벨", "role": "User", "isApproved": true, "createdAt": "2026-09-01T00:00:00Z", "approvedAt": null},
          {"id": 1, "username": "Admin", "displayName": "관리자", "role": "Admin", "isApproved": true, "createdAt": "2026-08-01T00:00:00Z", "approvedAt": null},
          {"id": 7, "username": "newbie", "displayName": "", "role": "User", "isApproved": false, "createdAt": "2026-10-20T00:00:00Z", "approvedAt": null}
        ]
        """;

    [Fact]
    public async Task Web_admin_gets_the_web_accounts_with_approval_and_the_call_carries_their_own_token()
    {
        using var f = new StubWebFactory(HttpStatusCode.OK, WebUsersJson);
        var token = WebToken("admin", "Admin");   // 운영 웹 Admin → BootstrapWebRole 로 MLOps 관리자

        var list = await ListAsync(As(f, token));

        list.Available.Should().BeTrue();
        list.Accounts.Select(a => a.Username).Should().Equal("Admin", "kim", "newbie");
        list.Accounts.Single(a => a.Username == "kim").Should().Be(new WebAccountDto("kim", "김라벨", "User", true));
        list.Accounts.Single(a => a.Username == "newbie").Should().Be(new WebAccountDto("newbie", null, "User", false));

        f.LastPath.Should().Be("/api/admin/users");
        f.LastAuthorization.Should().Be($"Bearer {token}", "운영 웹은 부른 사람의 권한으로 판단해야 한다");
    }

    [Fact]
    public async Task Not_a_web_admin_gets_a_reason_instead_of_an_error()
    {
        using var f = new StubWebFactory(HttpStatusCode.Forbidden);
        // MLOps 에서는 Admin 이지만(역할 표) 운영 웹에서는 User 인 사람
        var bootstrap = As(f, WebToken("boss", "Admin"));
        (await bootstrap.PutAsJsonAsync("/api/members", new GrantMemberRequest("lee", "Admin"), Json)).EnsureSuccessStatusCode();

        var list = await ListAsync(As(f, WebToken("lee", "User")));

        list.Available.Should().BeFalse();
        list.Reason.Should().Be(WebAccountUnavailable.NotWebAdmin);
        list.Message.Should().Contain("운영 웹에서 Admin");
        list.Accounts.Should().BeEmpty();
    }

    [Fact]
    public async Task Unreachable_web_gives_a_reason()
    {
        using var f = new StubWebFactory(null);
        var list = await ListAsync(As(f, WebToken("admin", "Admin")));
        list.Available.Should().BeFalse();
        list.Reason.Should().Be(WebAccountUnavailable.Unreachable);
    }

    [Fact]
    public async Task Dev_token_is_not_sent_to_the_web()
    {
        using var f = new StubWebFactory(HttpStatusCode.OK, WebUsersJson);
        var list = await ListAsync(await f.AdminAsync());
        list.Reason.Should().Be(WebAccountUnavailable.NotWebToken);
        f.Calls.Should().Be(0, "우리가 만든 토큰을 운영 웹에 들려 보내지 않는다");
    }

    [Fact]
    public async Task Only_mlops_admins_may_ask()
    {
        using var f = new StubWebFactory(HttpStatusCode.OK, WebUsersJson);
        // 표에 없는 운영 웹 User → 기본 역할(Viewer)
        (await As(f, WebToken("viewer-kim", "User")).GetAsync("/api/members/web-accounts")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        f.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Without_a_web_address_it_says_so()
    {
        using var f = new MlopsApiFactory();   // 운영 웹 주소 없음
        var list = await ListAsync(await f.AdminAsync());
        list.Reason.Should().Be(WebAccountUnavailable.NotConfigured);
    }

    [Fact]
    public async Task Local_mode_has_no_web_accounts()
    {
        using var f = new LocalModeFactory();
        var list = await ListAsync(await f.AdminAsync());
        list.Reason.Should().Be(WebAccountUnavailable.LocalMode);
    }

    /// <summary>운영 웹 자리에 세우는 가짜 — 계정 목록에 정해진 상태·본문으로 답한다. null 이면 연결 실패.</summary>
    private sealed class StubWebFactory(HttpStatusCode? status, string body = "[]") : MlopsApiFactory
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string? LastPath { get; private set; }
        public string? LastAuthorization { get; private set; }
        private HttpStatusCode? Status { get; } = status;
        private string Body { get; } = body;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Auth:WebBaseUrl", "http://stub-web.invalid");
            builder.UseSetting("Auth:RevocationCheckSeconds", "0");   // 토큰 확인은 TokenRevocationTests 가 맡는다
            builder.ConfigureServices(services =>
                services.AddHttpClient<WebAccountDirectoryClient>().ConfigurePrimaryHttpMessageHandler(() => new StubHandler(this)));
        }

        private sealed class StubHandler(StubWebFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Interlocked.Increment(ref owner._calls);
                owner.LastPath = request.RequestUri!.AbsolutePath;
                owner.LastAuthorization = request.Headers.Authorization?.ToString();
                if (owner.Status is null) throw new HttpRequestException("운영 웹에 닿지 못했습니다");
                return Task.FromResult(new HttpResponseMessage(owner.Status.Value)
                {
                    Content = new StringContent(owner.Body, Encoding.UTF8, "application/json"),
                });
            }
        }
    }

    private sealed class LocalModeFactory : MlopsApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Auth:Mode", "Local");
            builder.UseSetting("Auth:Local:Key", "local-key-0123456789abcdef0123456789abcdef-zz");
            builder.UseSetting("Auth:Local:BootstrapAdmin", "");
        }
    }
}
