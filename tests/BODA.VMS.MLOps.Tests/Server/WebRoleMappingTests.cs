using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BODA.VMS.MLOps.Client.Services;
using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Contracts.Workers;
using BODA.VMS.MLOps.Core.Auth;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Server;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using static BODA.VMS.MLOps.Tests.Server.MlopsApiFactory;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// 운영 웹(BODA.VMS.Web)이 발급한 토큰으로 들어온 사람이 MLOps 에서 무엇을 할 수 있는가.
///
/// <para>
/// 운영 웹 역할은 Admin · User · Guest 이고 MLOps 는 Viewer ⊂ Labeler ⊂ Engineer ⊂ Admin 을 봅니다.
/// 옮기는 규칙은 <see cref="WebRoleMapping"/> 한 곳이고, 서버와 화면이 같은 규칙을 써야 합니다 —
/// 한쪽만 옮기면 권한은 있는데 버튼이 안 보이거나, 버튼은 보이는데 누르면 403 입니다.
/// </para>
/// </summary>
public class WebRoleMappingTests : IClassFixture<MlopsApiFactory>
{
    private readonly MlopsApiFactory _f;
    public WebRoleMappingTests(MlopsApiFactory f) => _f = f;

    /// <summary>
    /// 운영 웹의 <c>JwtTokenService.GenerateAccessToken</c> 과 같은 모양으로 만든다 —
    /// NameIdentifier · Name · DisplayName · Role 하나. 우리 발급기(ServiceTokenIssuer)는
    /// 모르는 역할을 걸러 내므로 여기서는 쓸 수 없다.
    /// </summary>
    private string WebToken(string role, string user = "web-user")
    {
        var jwt = _f.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: jwt.Issuer,
            audience: jwt.Audience,
            claims:
            [
                new Claim(ClaimTypes.NameIdentifier, "7"),
                new Claim(ClaimTypes.Name, user),
                new Claim("DisplayName", user),
                new Claim(ClaimTypes.Role, role),
            ],
            expires: DateTime.UtcNow.AddHours(8),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private HttpClient As(string webRole)
    {
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", WebToken(webRole, $"web-{webRole.ToLowerInvariant()}"));
        return c;
    }

    private static async Task<string[]> RolesAsync(HttpClient client)
    {
        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me", Json);
        return me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).ToArray();
    }

    private static Task<HttpResponseMessage> CreateModelAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/models",
            new CreateModelRequest($"매핑 시험 {Guid.NewGuid():N}", TaskType.Detection, ["ok"]), Json);

    private static Task<HttpResponseMessage> CreateWorkerAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/workers", new CreateWorkerRequest($"gpu-{Guid.NewGuid():N}"[..20]), Json);

    // ───────────── 서버 ─────────────

    /// <summary>
    /// 운영 웹 User 는 엔지니어급이다 (VMS SSO 에서도 Engineer). 모델·데이터셋을 만들고 학습을 걸 수 있어야 한다.
    /// 전에는 여기서 로그인부터 403 이었다.
    /// </summary>
    [Fact]
    public async Task A_web_user_works_as_an_engineer()
    {
        var user = As("User");

        (await RolesAsync(user)).Should().Contain(["User", "Engineer"], "원래 역할도 남겨 어디서 왔는지 보이게 한다");
        (await CreateModelAsync(user)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    /// <summary>User 가 Engineer 가 됐다고 관리자가 되는 것은 아니다 — 워커 발급·Production 승격은 Admin 만.</summary>
    [Fact]
    public async Task A_web_user_is_not_an_admin()
    {
        (await CreateWorkerAsync(As("User"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>운영 웹 Guest 는 조회만 하는 계정이다. MLOps 에서도 보기만 한다.</summary>
    [Fact]
    public async Task A_web_guest_can_look_but_not_change()
    {
        var guest = As("Guest");

        (await RolesAsync(guest)).Should().Contain("Viewer");
        (await guest.GetAsync("/api/models")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CreateModelAsync(guest)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_web_admin_is_an_admin()
    {
        (await CreateWorkerAsync(As("Admin"))).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    /// <summary>모르는 역할은 옮기지 않는다 — 조용히 권한이 생기면 안 된다.</summary>
    [Fact]
    public async Task An_unknown_web_role_gets_nothing()
    {
        (await As("Operator").GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ───────────── 화면 ─────────────

    /// <summary>
    /// 관리 화면은 토큰을 브라우저에서 직접 읽어 버튼을 보일지 정한다. 서버와 같은 규칙이어야
    /// Engineer 버튼(재학습·모델 등록 등)이 운영 웹 User 에게 보인다.
    /// </summary>
    [Theory]
    [InlineData("User", "Engineer", true)]
    [InlineData("User", "Admin", false)]
    [InlineData("Guest", "Viewer", true)]
    [InlineData("Guest", "Engineer", false)]
    [InlineData("Admin", "Admin", true)]
    public void The_admin_screen_sees_the_same_roles_as_the_server(string webRole, string mlopsRole, bool expected)
    {
        var principal = JwtAuthenticationStateProvider.Parse(WebToken(webRole));

        principal.Should().NotBeNull();
        principal!.IsInRole(mlopsRole).Should().Be(expected);
    }
}

/// <summary>규칙 자체 — 서버와 화면이 같이 쓰는 곳.</summary>
public class WebRoleMappingRuleTests
{
    [Theory]
    [InlineData("User", "Engineer")]
    [InlineData("Guest", "Viewer")]
    [InlineData("user", "Engineer")]   // 대소문자는 가리지 않는다 — 옮긴 이름은 우리 표기로 낸다
    public void Web_roles_gain_the_mapped_mlops_role(string web, string mlops) =>
        WebRoleMapping.Expand([web]).Should().BeEquivalentTo([web, mlops]);

    [Theory]
    [InlineData("Admin")]
    [InlineData("Engineer")]   // 개발 토큰·우리 발급기가 싣는 MLOps 역할은 그대로
    [InlineData("Worker")]
    [InlineData("Line")]
    [InlineData("Operator")]   // 모르는 것은 옮기지 않는다
    public void Other_roles_pass_through_untouched(string role) =>
        WebRoleMapping.Expand([role]).Should().BeEquivalentTo([role]);

    /// <summary>인증 처리는 한 요청에서도 여러 번 돌 수 있다. 몇 번을 불러도 같아야 한다.</summary>
    [Fact]
    public void Expanding_twice_changes_nothing()
    {
        var once = WebRoleMapping.Expand(["User"]);
        WebRoleMapping.Expand(once).Should().Equal(once);
    }
}
