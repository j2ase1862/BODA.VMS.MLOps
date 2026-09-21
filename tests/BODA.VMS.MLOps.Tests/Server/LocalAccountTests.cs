using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BODA.VMS.MLOps.Contracts.Auth;
using BODA.VMS.MLOps.Contracts.Members;
using BODA.VMS.MLOps.Server.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// 자체 계정 로그인 (<c>Auth:Mode=Local</c>) — 운영 웹이 없는 설치에서 사람이 들어오는 문.
/// 설계 메모 <c>docs/로컬 계정 로그인 설계 메모.md</c> 의 규약을 그대로 시험한다.
/// </summary>
public class LocalAccountTests
{
    private const string LocalKey = "local-test-key-0123456789abcdef0123456789";

    /// <summary>로컬 모드 서버. 개발 토큰도 로컬 키로 나와야 하므로 그 경로까지 같이 확인된다.</summary>
    private class LocalFactory : MlopsApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Auth:Mode", "Local");
            builder.UseSetting("Auth:Local:Key", LocalKey);
            // 첫 관리자는 시험마다 따로 만든다 — 자동 생성이 끼면 "표가 비었을 때" 를 못 본다.
            builder.UseSetting("Auth:Local:BootstrapAdmin", "");
        }
    }

    /// <summary>계정을 하나 만들고 임시 비밀번호까지 받아 둔다.</summary>
    private static async Task<(MemberDto Member, string Password)> CreateAccountAsync(
        MlopsApiFactory f, string username, string role)
    {
        var admin = await f.AdminAsync();
        var member = await (await admin.PutAsJsonAsync("/api/members", new GrantMemberRequest(username, role)))
            .Content.ReadFromJsonAsync<MemberDto>(MlopsApiFactory.Json);
        var reset = await admin.PostAsync($"/api/members/{member!.Id}/reset-password", null);
        reset.StatusCode.Should().Be(HttpStatusCode.OK);
        var temp = await reset.Content.ReadFromJsonAsync<TemporaryPasswordResponse>(MlopsApiFactory.Json);
        return (member, temp!.TemporaryPassword);
    }

    private static async Task<HttpResponseMessage> LoginAsync(MlopsApiFactory f, string user, string password) =>
        await f.CreateClient().PostAsJsonAsync("/api/auth/login", new LocalLoginRequest(user, password));

    private static HttpClient WithToken(MlopsApiFactory f, string token)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    /// <summary>임시 비밀번호를 받은 계정이 바로 쓸 수 있는 상태(역할 있음)가 되도록 비밀번호를 바꿔 둔다.</summary>
    private static async Task<string> SettleAsync(MlopsApiFactory f, string user, string temporary, string next)
    {
        var login = await (await LoginAsync(f, user, temporary)).Content.ReadFromJsonAsync<LocalLoginResponse>(MlopsApiFactory.Json);
        var changed = await WithToken(f, login!.Token)
            .PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest(temporary, next));
        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await changed.Content.ReadFromJsonAsync<LocalLoginResponse>(MlopsApiFactory.Json);
        return after!.Token;
    }

    [Fact]
    public async Task 맞는_비밀번호로_들어오고_틀리면_401()
    {
        using var f = new LocalFactory();
        var (_, temp) = await CreateAccountAsync(f, "hong", Roles.Engineer);

        (await LoginAsync(f, "hong", temp)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await LoginAsync(f, "hong", "wrong-password")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // 없는 계정도 같은 401 이어야 한다 — 다르게 답하면 계정 이름 훑기를 도와주는 셈이다.
        (await LoginAsync(f, "nobody", temp)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 연속_실패_다섯_번이면_잠기고_시간이_지나면_풀린다()
    {
        using var f = new LocalFactory();
        var (_, temp) = await CreateAccountAsync(f, "hong", Roles.Viewer);

        for (var i = 0; i < 4; i++)
            (await LoginAsync(f, "hong", "wrong")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // 다섯 번째에서 잠근다. 이때부터는 맞는 비밀번호도 통하지 않아야 한다.
        (await LoginAsync(f, "hong", "wrong")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await LoginAsync(f, "hong", temp)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        f.Clock.Advance(TimeSpan.FromMinutes(11));
        (await LoginAsync(f, "hong", temp)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Web_모드에서는_자체_로그인이_열리지_않는다()
    {
        // 두 문을 함께 열면 같은 아이디가 두 출처에서 들어올 수 있다 — Members 는 계정 이름이 키다.
        using var f = new MlopsApiFactory();
        (await LoginAsync(f, "hong", "whatever")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var admin = await f.AdminAsync();
        var member = await (await admin.PutAsJsonAsync("/api/members", new GrantMemberRequest("hong", Roles.Viewer)))
            .Content.ReadFromJsonAsync<MemberDto>(MlopsApiFactory.Json);
        (await admin.PostAsync($"/api/members/{member!.Id}/reset-password", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task 로컬_토큰도_역할_표를_따른다()
    {
        // 토큰에 실린 역할이 아니라 표가 정본이어야 한다. 자체 발급 표시(mlops:self)를 붙이면
        // 역할 변환이 통째로 건너뛰어 강등이 토큰 수명 동안 듣지 않는다 — 그래서 붙이지 않는다.
        using var f = new LocalFactory();
        var (member, temp) = await CreateAccountAsync(f, "hong", Roles.Engineer);
        var token = await SettleAsync(f, "hong", temp, "new-password-1");
        var hong = WithToken(f, token);

        (await hong.PostAsJsonAsync("/api/models",
            new { name = "m1", taskType = "detection", classes = new[] { "a" } })).IsSuccessStatusCode.Should().BeTrue();

        // 같은 토큰 그대로인데 표에서 내리면 다음 요청부터 막힌다.
        var admin = await f.AdminAsync();
        (await admin.PutAsJsonAsync("/api/members", new GrantMemberRequest("hong", Roles.Viewer)))
            .EnsureSuccessStatusCode();

        (await hong.PostAsJsonAsync("/api/models",
            new { name = "m2", taskType = "detection", classes = new[] { "a" } }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await hong.GetAsync("/api/models")).StatusCode.Should().Be(HttpStatusCode.OK);
        member.Role.Should().Be(Roles.Engineer);
    }

    [Fact]
    public async Task 로컬_토큰은_운영_웹에_물어보지_않는다()
    {
        // 여기가 이 기능에서 가장 조용한 함정이다. 취소 검사는 Monitoring:ProductionWebUrl 만 있으면
        // 켜지는데, 운영 웹은 우리 로컬 계정을 모르니 무조건 401 을 준다 — 가르지 않으면
        // 로컬 모드에서 모니터링을 켜는 순간 로그인한 사람이 전원 쫓겨난다.
        using var f = new RevokingLocalFactory();
        var (_, temp) = await CreateAccountAsync(f, "hong", Roles.Engineer);
        var token = await SettleAsync(f, "hong", temp, "new-password-1");

        (await WithToken(f, token).GetAsync("/api/models")).StatusCode.Should().Be(HttpStatusCode.OK);
        f.Calls.Should().Be(0, "로컬 모드에서는 토큰을 운영 웹에 물어보지 않는다");
    }

    [Fact]
    public async Task 비활성_계정은_살아_있는_토큰으로도_막힌다()
    {
        using var f = new LocalFactory();
        var (member, temp) = await CreateAccountAsync(f, "hong", Roles.Engineer);
        var token = await SettleAsync(f, "hong", temp, "new-password-1");
        var hong = WithToken(f, token);
        (await hong.GetAsync("/api/models")).StatusCode.Should().Be(HttpStatusCode.OK);

        var admin = await f.AdminAsync();
        (await admin.PostAsJsonAsync($"/api/members/{member.Id}/disabled", new SetMemberDisabledRequest(true)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        // 토큰은 그대로 유효하지만 역할이 없어진다. 로그인도 막힌다.
        (await hong.GetAsync("/api/models")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await LoginAsync(f, "hong", "new-password-1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await admin.PostAsJsonAsync($"/api/members/{member.Id}/disabled", new SetMemberDisabledRequest(false)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await LoginAsync(f, "hong", "new-password-1")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task 임시_비밀번호_상태로는_비밀번호_변경_말고_아무것도_못_한다()
    {
        using var f = new LocalFactory();
        var (_, temp) = await CreateAccountAsync(f, "hong", Roles.Admin);

        var login = await (await LoginAsync(f, "hong", temp)).Content.ReadFromJsonAsync<LocalLoginResponse>(MlopsApiFactory.Json);
        login!.MustChangePassword.Should().BeTrue();

        var hong = WithToken(f, login.Token);
        (await hong.GetAsync("/api/models")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await hong.GetAsync("/api/members")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var changed = await hong.PostAsJsonAsync("/api/auth/change-password",
            new ChangePasswordRequest(temp, "new-password-1"));
        changed.StatusCode.Should().Be(HttpStatusCode.OK);

        // 바꾸고 받은 토큰에는 역할이 붙는다.
        var after = await changed.Content.ReadFromJsonAsync<LocalLoginResponse>(MlopsApiFactory.Json);
        after!.MustChangePassword.Should().BeFalse();
        (await WithToken(f, after.Token).GetAsync("/api/members")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task 비밀번호를_바꾸면_옛_비밀번호와_옛_토큰이_함께_죽는다()
    {
        using var f = new LocalFactory();
        var (_, temp) = await CreateAccountAsync(f, "hong", Roles.Engineer);
        var first = await SettleAsync(f, "hong", temp, "new-password-1");
        var firstClient = WithToken(f, first);
        (await firstClient.GetAsync("/api/models")).StatusCode.Should().Be(HttpStatusCode.OK);

        // 다른 자리에서 비밀번호를 한 번 더 바꾼다.
        var again = await WithToken(f, first)
            .PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest("new-password-1", "new-password-2"));
        again.StatusCode.Should().Be(HttpStatusCode.OK);

        (await LoginAsync(f, "hong", "new-password-1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await LoginAsync(f, "hong", "new-password-2")).StatusCode.Should().Be(HttpStatusCode.OK);

        // 옛 토큰은 서명·만료가 멀쩡해도 비밀번호 도장이 어긋나 역할을 잃는다.
        // (운영 웹이 토큰 세대 tv 로 하는 일을 로컬 모드에서는 이 도장이 한다.)
        (await firstClient.GetAsync("/api/models")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 관리자_초기화는_임시_비밀번호를_한_번만_내준다()
    {
        using var f = new LocalFactory();
        var (member, first) = await CreateAccountAsync(f, "hong", Roles.Labeler);
        await SettleAsync(f, "hong", first, "new-password-1");

        var admin = await f.AdminAsync();
        var second = await (await admin.PostAsync($"/api/members/{member.Id}/reset-password", null))
            .Content.ReadFromJsonAsync<TemporaryPasswordResponse>(MlopsApiFactory.Json);
        second!.TemporaryPassword.Should().NotBe(first);

        // 초기화하면 옛 비밀번호는 죽고, 새 임시 비밀번호는 다시 변경을 요구한다.
        (await LoginAsync(f, "hong", "new-password-1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var login = await (await LoginAsync(f, "hong", second.TemporaryPassword))
            .Content.ReadFromJsonAsync<LocalLoginResponse>(MlopsApiFactory.Json);
        login!.MustChangePassword.Should().BeTrue();

        // 목록에는 해시도 비밀번호도 실리지 않는다.
        var listed = (await admin.GetFromJsonAsync<List<MemberDto>>("/api/members", MlopsApiFactory.Json))!
            .Single(m => m.Username == "hong");
        listed.HasPassword.Should().BeTrue();
        listed.MustChangePassword.Should().BeTrue();
    }

    [Fact]
    public async Task 워커_토큰은_로컬_모드에서도_그대로_돈다()
    {
        // 사람의 문이 바뀌어도 서비스 계정의 경계는 그대로여야 한다 (WorkerScopeTests 와 같은 경계).
        using var f = new LocalFactory();
        var admin = await f.AdminAsync();
        var worker = await f.CreateWorkerAsync(admin);
        var client = f.WorkerClient(worker.Token);

        (await client.GetAsync("/api/workers/scripts")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/members")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 표가_비어_있으면_첫_관리자를_만든다()
    {
        // 운영 웹이 없으므로 "웹 관리자면 인정" 하는 통로도 없다. 이것이 없으면 아무도 못 들어온다.
        using var f = new BootstrappingFactory();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MLOps.Server.Data.MlopsDbContext>();

        var created = db.Members.Single();
        created.Username.Should().Be("admin");
        created.Role.Should().Be(Roles.Admin);
        created.MustChangePassword.Should().BeTrue("임시 비밀번호로 만들어진다");
        created.PasswordHash.Should().NotBeNullOrEmpty();
        File.Exists(Path.Combine(AppContext.BaseDirectory, "initial-admin-password.txt")).Should().BeTrue();
    }

    /// <summary>로컬 모드인데 취소 검사가 켜져 있는 서버 — 물어보면 401 을 주는 가짜 운영 웹을 세운다.</summary>
    private sealed class RevokingLocalFactory : LocalFactory
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Auth:WebBaseUrl", "http://stub-web.invalid");
            builder.UseSetting("Auth:RevocationCheckSeconds", "60");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient<WebTokenRevocationClient>()
                        .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(this));
            });
        }

        private sealed class StubHandler(RevokingLocalFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Interlocked.Increment(ref owner._calls);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }
        }
    }

    /// <summary>첫 관리자 자동 생성을 켠 서버.</summary>
    private sealed class BootstrappingFactory : MlopsApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Auth:Mode", "Local");
            builder.UseSetting("Auth:Local:Key", LocalKey);
            builder.UseSetting("Auth:Local:BootstrapAdmin", "admin");
        }
    }
}
