using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using BODA.VMS.MLOps.Server.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// 운영 웹에서 끊긴 토큰은 여기서도 막힌다.
///
/// <para>
/// 운영 웹은 토큰에 세대(<c>tv</c>)를 싣고 요청마다 DB 와 대조해 로그아웃·비밀번호 변경·계정 삭제
/// 뒤의 토큰을 거부한다. 우리는 서명과 만료만 보므로, 물어보지 않으면 잘린 계정의 토큰이
/// 최대 8시간(운영 웹 기본 수명) 더 살아 있다. 그 판단은 그쪽 DB 가 쥐고 있어 흉내 내지 않고
/// <c>/api/auth/me</c> 에 들려 보내 401 인지 본다.
/// </para>
/// </summary>
public class TokenRevocationTests
{
    /// <summary>운영 웹이 내주는 것과 같은 모양의 토큰.</summary>
    private static string WebToken(string username = "hong")
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("test-key-0123456789abcdef0123456789abcdef"));
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken("BODA.VMS.Web", "BODA.VMS.Web",
            [new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, "User"), new Claim("tv", "3")],
            now, now.AddHours(1), new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static HttpClient WithToken(MlopsApiFactory f, string token)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [Fact]
    public async Task 운영_웹이_끊은_토큰은_막힌다()
    {
        using var f = new RevocationFactory(HttpStatusCode.Unauthorized);
        var user = WithToken(f, WebToken());

        // 서명도 만료도 멀쩡하다. 그쪽에서 세대가 올라갔을 뿐인데, 예전에는 이게 통했다.
        (await user.GetAsync("/api/datasets")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 살아_있는_토큰은_통한다()
    {
        using var f = new RevocationFactory(HttpStatusCode.OK);
        var user = WithToken(f, WebToken());

        (await user.GetAsync("/api/datasets")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task 한_번_물어본_뒤에는_기억한다()
    {
        using var f = new RevocationFactory(HttpStatusCode.OK);
        var user = WithToken(f, WebToken());

        for (var i = 0; i < 5; i++) await user.GetAsync("/api/datasets");

        // 요청마다 운영 웹을 두드리면 목록 화면 한 번에 수십 번이 된다.
        f.Calls.Should().Be(1);
    }

    [Fact]
    public async Task 운영_웹이_죽으면_통과시킨다()
    {
        using var f = new RevocationFactory(null);   // 연결 자체가 실패한다
        var user = WithToken(f, WebToken());

        // 이 플랫폼은 운영 웹이 멈춰도 라벨링과 학습이 돌아야 한다. 못 물어봤다는 이유로
        // 전원을 쫓아내면, 그쪽 장애가 이쪽 장애가 된다.
        (await user.GetAsync("/api/datasets")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task 확인을_끄면_묻지_않는다()
    {
        using var f = new RevocationFactory(HttpStatusCode.Unauthorized, seconds: 0);
        var user = WithToken(f, WebToken());

        (await user.GetAsync("/api/datasets")).StatusCode.Should().Be(HttpStatusCode.OK);
        f.Calls.Should().Be(0);
    }

    [Fact]
    public async Task 우리가_발급한_토큰은_묻지_않는다()
    {
        // 개발 토큰은 운영 웹이 모른다. 물어보면 401 이라 시험 묶음 전체가 무너진다.
        using var f = new RevocationFactory(HttpStatusCode.Unauthorized);
        var admin = await f.AdminAsync();

        (await admin.GetAsync("/api/datasets")).StatusCode.Should().Be(HttpStatusCode.OK);
        f.Calls.Should().Be(0);
    }

    [Fact]
    public async Task 워커_토큰은_묻지_않는다()
    {
        using var f = new RevocationFactory(HttpStatusCode.Unauthorized);
        var admin = await f.AdminAsync();
        var worker = await f.CreateWorkerAsync(admin);

        var wc = f.WorkerClient(worker.Token);
        (await wc.GetAsync("/api/workers/scripts")).StatusCode.Should().Be(HttpStatusCode.OK);

        // 워커·라인 토큰은 JWT 가 아니라 전용 스킴이라 이 경로를 아예 타지 않는다.
        f.Calls.Should().Be(0);
    }

    /// <summary>운영 웹 자리에 세우는 가짜 — 정해진 상태 코드로만 답한다.</summary>
    private sealed class RevocationFactory(HttpStatusCode? status, int seconds = 60) : MlopsApiFactory
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        /// <summary>가짜가 낼 상태 코드. null 이면 연결 실패를 흉내 낸다.</summary>
        private HttpStatusCode? Status { get; } = status;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Auth:WebBaseUrl", "http://stub-web.invalid");
            builder.UseSetting("Auth:RevocationCheckSeconds", seconds.ToString());
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient<WebTokenRevocationClient>()
                        .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(this));
            });
        }

        private sealed class StubHandler(RevocationFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Interlocked.Increment(ref owner._calls);
                if (owner.Status is null) throw new HttpRequestException("운영 웹에 닿지 못했습니다");
                return Task.FromResult(new HttpResponseMessage(owner.Status.Value));
            }
        }
    }
}
