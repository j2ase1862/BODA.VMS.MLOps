using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using BODA.VMS.MLOps.Contracts.Members;
using BODA.VMS.MLOps.Server.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// 운영 웹 계정 ↔ MLOps 역할 매핑.
///
/// <para>
/// 여기서 쓰는 토큰은 <b>개발 토큰이 아니라</b> 운영 웹이 내주는 것과 같은 모양이다 —
/// 같은 키로 서명하고 역할은 운영 웹의 것(<c>Admin</c>·<c>User</c>)을 싣는다.
/// 개발 토큰에는 <see cref="ServiceTokenIssuer.SelfIssuedClaim"/> 이 붙어 매핑을 건너뛰므로
/// 그것으로는 이 동작을 확인할 수 없다.
/// </para>
/// </summary>
public class MemberRoleTests
{
    /// <summary>운영 웹이 발급한 것과 같은 토큰. 역할 이름이 우리 것과 다른 게 요점이다.</summary>
    private static string WebToken(string username, string webRole, string? displayName = null)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("test-key-0123456789abcdef0123456789abcdef"));
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, "42"),
            new(ClaimTypes.Name, username),
            new(ClaimTypes.Role, webRole),
        };
        if (displayName is not null) claims.Add(new Claim("DisplayName", displayName));

        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken("BODA.VMS.Web", "BODA.VMS.Web", claims, now, now.AddHours(1),
            new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static HttpClient WithToken(MlopsApiFactory f, string token)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [Fact]
    public async Task 표에_없는_웹_사용자는_기본_역할로_읽기만_한다()
    {
        using var f = new MlopsApiFactory();
        var user = WithToken(f, WebToken("hong", "User"));

        // 예전에는 여기서 403 이었다 — 운영 웹의 "User" 는 우리 정책 어디에도 없는 역할이라
        // 로그인은 되는데 모든 화면이 막혔다.
        (await user.GetAsync("/api/datasets")).StatusCode.Should().Be(HttpStatusCode.OK);

        var create = await user.PostAsJsonAsync("/api/datasets",
            new { name = "x", taskType = "detection", classes = new[] { "a" } }, MlopsApiFactory.Json);
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 표에_올리면_그_역할을_얻는다()
    {
        using var f = new MlopsApiFactory();
        var admin = await f.AdminAsync();

        var granted = await admin.PutAsJsonAsync("/api/members",
            new GrantMemberRequest("Hong", Roles.Engineer, "홍길동"), MlopsApiFactory.Json);
        granted.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await granted.Content.ReadFromJsonAsync<MemberDto>(MlopsApiFactory.Json))!;
        dto.Username.Should().Be("hong");     // 대소문자를 섞어 넣어도 한 줄로 모인다

        var user = WithToken(f, WebToken("hong", "User"));
        var create = await user.PostAsJsonAsync("/api/datasets",
            new { name = "엔지니어가 만든 것", taskType = "detection", classes = new[] { "a" } },
            MlopsApiFactory.Json);
        create.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task 역할을_거두면_다음_요청부터_바로_듣는다()
    {
        using var f = new MlopsApiFactory();
        var admin = await f.AdminAsync();
        await admin.PutAsJsonAsync("/api/members",
            new GrantMemberRequest("hong", Roles.Engineer), MlopsApiFactory.Json);

        var user = WithToken(f, WebToken("hong", "User"));
        var before = await user.PostAsJsonAsync("/api/datasets",
            new { name = "하나", taskType = "detection", classes = new[] { "a" } }, MlopsApiFactory.Json);
        before.StatusCode.Should().Be(HttpStatusCode.Created);

        // 낮춘다. 캐시가 살아 있으면 이 조치가 몇 분 뒤에야 들어 — 권한 회수는 즉시여야 한다.
        await admin.PutAsJsonAsync("/api/members",
            new GrantMemberRequest("hong", Roles.Viewer), MlopsApiFactory.Json);

        var after = await user.PostAsJsonAsync("/api/datasets",
            new { name = "둘", taskType = "detection", classes = new[] { "a" } }, MlopsApiFactory.Json);
        after.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 표에서_지워도_바로_듣는다()
    {
        using var f = new MlopsApiFactory();
        var admin = await f.AdminAsync();
        var granted = await admin.PutAsJsonAsync("/api/members",
            new GrantMemberRequest("hong", Roles.Engineer), MlopsApiFactory.Json);
        var dto = (await granted.Content.ReadFromJsonAsync<MemberDto>(MlopsApiFactory.Json))!;

        var user = WithToken(f, WebToken("hong", "User"));
        (await user.PostAsJsonAsync("/api/datasets",
            new { name = "하나", taskType = "detection", classes = new[] { "a" } }, MlopsApiFactory.Json))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        (await admin.DeleteAsync($"/api/members/{dto.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await user.PostAsJsonAsync("/api/datasets",
            new { name = "둘", taskType = "detection", classes = new[] { "a" } }, MlopsApiFactory.Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 운영_웹_관리자는_표가_비어도_들어온다()
    {
        using var f = new MlopsApiFactory();
        var boss = WithToken(f, WebToken("boss", "Admin"));

        // 표가 비어 있어도 첫 역할을 줄 사람은 들어와야 한다. 아니면 아무도 시작할 수 없다.
        (await boss.GetAsync("/api/members")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task 표가_부트스트랩을_이긴다()
    {
        using var f = new MlopsApiFactory();
        var admin = await f.AdminAsync();
        await admin.PutAsJsonAsync("/api/members",
            new GrantMemberRequest("boss", Roles.Viewer), MlopsApiFactory.Json);

        var boss = WithToken(f, WebToken("boss", "Admin"));

        // 운영 웹 관리자라도 우리 쪽에서는 낮출 수 있어야 한다.
        (await boss.GetAsync("/api/members")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await boss.GetAsync("/api/datasets")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task 개발_토큰은_매핑을_거치지_않는다()
    {
        using var f = new MlopsApiFactory();
        var admin = await f.AdminAsync();

        // 우리가 발급한 토큰은 실린 역할이 곧 의도다. 표에 없다고 Viewer 로 떨어지면
        // 시험 묶음 전체가 권한 부족으로 무너진다.
        (await admin.GetAsync("/api/members")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task 로그인하면_마지막_접속과_표시_이름이_남는다()
    {
        using var f = new MlopsApiFactory();
        var admin = await f.AdminAsync();
        await admin.PutAsJsonAsync("/api/members",
            new GrantMemberRequest("hong", Roles.Labeler), MlopsApiFactory.Json);

        var user = WithToken(f, WebToken("hong", "User", "홍길동"));
        (await user.GetAsync("/api/auth/me")).EnsureSuccessStatusCode();

        var list = (await admin.GetFromJsonAsync<List<MemberDto>>("/api/members", MlopsApiFactory.Json))!;
        var row = list.Single(m => m.Username == "hong");
        row.LastSeenAt.Should().NotBeNull();
        row.DisplayName.Should().Be("홍길동");
    }

    [Fact]
    public async Task 사람에게_서비스_계정_역할은_주지_못한다()
    {
        using var f = new MlopsApiFactory();
        var admin = await f.AdminAsync();

        // Worker·Line 은 토큰으로만 서는 서비스 계정이다. 사람 계정에 붙이면
        // 워커 범위(WorkerScopeTests 가 지키는 경계)가 사람 손으로 새어 나간다.
        var res = await admin.PutAsJsonAsync("/api/members",
            new GrantMemberRequest("hong", Roles.Worker), MlopsApiFactory.Json);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task 기본_역할을_비우면_표에_없는_사람은_아무것도_못_한다()
    {
        using var f = new NoDefaultRoleFactory();
        var user = WithToken(f, WebToken("hong", "User"));

        (await user.GetAsync("/api/datasets")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>표에 올린 사람만 들어오게 한 서버 — Auth:DefaultRole 을 비운 구성.</summary>
    private sealed class NoDefaultRoleFactory : MlopsApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Auth:DefaultRole", "");
        }
    }
}
