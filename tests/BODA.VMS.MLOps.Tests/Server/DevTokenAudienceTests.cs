using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BODA.VMS.MLOps.Server;
using BODA.VMS.MLOps.Server.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// 개발 토큰이 운영 웹에서 통하면 안 된다.
///
/// <para>
/// 두 서버는 서명 키·발급자를 공유하고, 운영 웹 로그인 토큰을 받으려고 우리 audience 도 운영 웹과
/// 맞춰 두었다. 그 상태로 개발 토큰을 우리 audience 로 발급하면, 개발 PC 에 운영 키를 넣고 서버를 띄운 동안
/// 익명 요청 하나로 <b>운영 웹이 받는 30일짜리 Admin 토큰</b>이 나온다 (2026-09-11 점검에서 발견).
/// 그래서 개발 토큰은 전용 audience(<see cref="ServiceTokenIssuer.DevAudience"/>)로만 나간다.
/// </para>
/// </summary>
public class DevTokenAudienceTests : IClassFixture<DevTokenAudienceTests.ProductionAudienceFactory>
{
    /// <summary>운영 웹 audience 는 이것이다 (BODA.VMS.Web 의 Jwt:Audience).</summary>
    private const string WebAudience = "BODA.VMS.Web.Client";

    /// <summary>개발 PC 와 같은 구성 — audience 를 운영 웹과 맞추고 개발 토큰을 켰다.</summary>
    public sealed class ProductionAudienceFactory : MlopsApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Jwt:Audience", WebAudience);
            builder.UseSetting("Auth:EnableDevTokens", "true");
        }
    }

    private readonly ProductionAudienceFactory _f;
    public DevTokenAudienceTests(ProductionAudienceFactory f) => _f = f;

    private JwtOptions Jwt => _f.Services.GetRequiredService<IOptions<JwtOptions>>().Value;

    private async Task<string> DevTokenAsync(string role)
    {
        var res = await _f.CreateClient().PostAsJsonAsync("/api/auth/dev-token", new { user = "anyone", roles = new[] { role }, hours = 720 });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    /// <summary>
    /// 운영 웹이 토큰을 검증하는 조건을 그대로 흉내 낸다 (BODA.VMS.Web Program.cs 의 TokenValidationParameters —
    /// 발급자·audience·수명·서명 키). 같은 키를 쓰므로 여기서 통하면 운영 웹에서도 통한다.
    /// </summary>
    private void ValidateAsTheProductionWebWould(string token)
    {
        new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = Jwt.Issuer,
            ValidAudience = WebAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Jwt.Key)),
        }, out _);
    }

    /// <summary>핵심 — Admin 개발 토큰을 운영 웹의 검증 조건에 넣으면 거부돼야 한다.</summary>
    [Fact]
    public async Task A_dev_admin_token_is_rejected_by_the_production_web()
    {
        var token = await DevTokenAsync(Roles.Admin);

        var validate = () => ValidateAsTheProductionWebWould(token);
        validate.Should().Throw<SecurityTokenInvalidAudienceException>(
            "운영 웹과 키를 공유하므로 audience 만이 개발 토큰을 막는다");
    }

    [Fact]
    public async Task Dev_tokens_carry_their_own_audience()
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(await DevTokenAsync(Roles.Viewer));

        jwt.Audiences.Should().ContainSingle().Which.Should().Be(ServiceTokenIssuer.DevAudience);
    }

    /// <summary>개발 토큰은 MLOps 에서는 그대로 쓸 수 있어야 한다 — 개발·시험이 이것으로 돈다.</summary>
    [Fact]
    public async Task Dev_tokens_still_work_on_mlops_while_enabled()
    {
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await DevTokenAsync(Roles.Admin));

        (await c.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>운영 웹이 발급한 로그인 토큰은 여전히 통해야 한다 — audience 를 바꾸다 이것을 깨면 로그인이 막힌다.</summary>
    [Fact]
    public async Task Web_login_tokens_are_still_accepted()
    {
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            Jwt.Issuer, WebAudience,
            [new Claim(ClaimTypes.Name, "web-admin"), new Claim(ClaimTypes.Role, "Admin")],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Jwt.Key)), SecurityAlgorithms.HmacSha256)));
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        (await c.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

/// <summary>
/// 개발 토큰이 꺼져 있으면(운영) 개발 audience 를 단 토큰도 받지 않는다.
/// 누군가 같은 키로 개발 audience 토큰을 만들어 와도 운영 MLOps 에서는 통하지 않아야 한다.
/// </summary>
public class DevAudienceWhenDisabledTests : IClassFixture<DevAudienceWhenDisabledTests.DevTokensOffFactory>
{
    public sealed class DevTokensOffFactory : MlopsApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Auth:EnableDevTokens", "false");
        }
    }

    private readonly DevTokensOffFactory _f;
    public DevAudienceWhenDisabledTests(DevTokensOffFactory f) => _f = f;

    [Fact]
    public async Task A_dev_audience_token_is_refused_when_dev_tokens_are_off()
    {
        var jwt = _f.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            jwt.Issuer, ServiceTokenIssuer.DevAudience,
            [new Claim(ClaimTypes.Name, "forged"), new Claim(ClaimTypes.Role, Roles.Admin)],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)), SecurityAlgorithms.HmacSha256)));
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        (await c.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_dev_token_endpoint_is_gone_when_disabled()
    {
        var res = await _f.CreateClient().PostAsJsonAsync("/api/auth/dev-token", new { user = "x", roles = new[] { "Admin" } });
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
