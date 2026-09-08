using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BODA.VMS.MLOps.Contracts.Datasets;
using BODA.VMS.MLOps.Server.Endpoints;
using FluentAssertions;
using SkiaSharp;
using static BODA.VMS.MLOps.Tests.Server.MlopsApiFactory;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// 브라우저의 &lt;img&gt; 는 Authorization 헤더를 붙일 수 없다. 그래서 이미지 경로만 쿠키로도 인증한다.
/// 이 규칙이 깨지면 썸네일과 라벨링 캔버스가 통째로 401 이 되는데, 화면은 그냥 빈 칸으로 보여
/// 알아채기 어렵다. 아래 시험이 그 경계를 지킨다.
/// </summary>
public class ImageAuthTests : IClassFixture<MlopsApiFactory>
{
    private readonly MlopsApiFactory _f;
    public ImageAuthTests(MlopsApiFactory f) => _f = f;

    private async Task<(string Token, ImageDto Image)> ArrangeAsync()
    {
        var token = await TokenAsync("img-user", "Engineer");
        var client = _f.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(DataManagementApiTests.MakePng(64, 48, SKColors.Teal, 7)), "files", "auth.png");
        var res = await client.PostAsync("/api/images", form);
        res.EnsureSuccessStatusCode();
        var batch = (await res.Content.ReadFromJsonAsync<ImageUploadBatchDto>(Json))!;
        return (token, batch.Results[0].Image);
    }

    private async Task<string> TokenAsync(string user, params string[] roles)
    {
        var anon = _f.CreateClient();
        var res = await anon.PostAsJsonAsync("/api/auth/dev-token", new { user, roles });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return body.GetProperty("token").GetString()!;
    }

    [Fact]
    public async Task Image_request_without_any_credential_is_rejected()
    {
        var (_, image) = await ArrangeAsync();
        var anon = _f.CreateClient();
        (await anon.GetAsync(image.ThumbnailUrl)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Cookie_alone_is_enough_for_image_paths()
    {
        var (token, image) = await ArrangeAsync();

        // 쿠키 발급에는 Bearer 가 필요하다
        var withBearer = _f.CreateClient();
        withBearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var issued = await withBearer.PostAsync("/api/auth/image-cookie", null);
        issued.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var setCookie = issued.Headers.GetValues("Set-Cookie").Single();
        setCookie.Should().Contain(ImageCookie.Name);
        setCookie.Should().Contain("httponly", "스크립트가 토큰을 읽지 못해야 한다");
        setCookie.Should().Contain($"path={ImageCookie.Path}", "이미지 경로 밖으로 새지 않아야 한다");
        setCookie.Should().Contain("samesite=strict");
        setCookie.Should().NotContain("expires=", "만료를 박아 두면 탭을 오래 연 사람의 이미지가 끊긴다");

        // 헤더 없이 쿠키만으로 세 변형을 모두 받을 수 있어야 한다 — img 태그가 이렇게 요청한다
        var cookieOnly = _f.CreateClient();
        foreach (var url in new[] { image.ThumbnailUrl, image.ViewUrl, image.OriginalUrl })
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"{ImageCookie.Name}={token}");
            var res = await cookieOnly.SendAsync(request);
            res.StatusCode.Should().Be(HttpStatusCode.OK, url);
            (await res.Content.ReadAsByteArrayAsync()).Length.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task Cookie_does_not_unlock_anything_but_images()
    {
        var (token, _) = await ArrangeAsync();
        var cookieOnly = _f.CreateClient();

        // 쿠키는 이미지 경로에서만 읽는다. 다른 API 는 여전히 Bearer 를 요구해야 한다.
        foreach (var path in new[] { "/api/models", "/api/datasets", "/api/workers", "/api/training-jobs" })
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("Cookie", $"{ImageCookie.Name}={token}");
            (await cookieOnly.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, path);
        }
    }

    [Fact]
    public async Task Bearer_still_works_and_wins_over_a_stale_cookie()
    {
        var (token, image) = await ArrangeAsync();
        var client = _f.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, image.ThumbnailUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Cookie", $"{ImageCookie.Name}=쓸모없는값");
        // 헤더가 있으면 쿠키는 보지 않는다 — 오래된 쿠키가 정상 요청을 막으면 안 된다
        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Garbage_cookie_is_rejected_rather_than_trusted()
    {
        var (_, image) = await ArrangeAsync();
        var client = _f.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, image.ThumbnailUrl);
        request.Headers.Add("Cookie", $"{ImageCookie.Name}=not.a.jwt");
        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Clearing_the_cookie_expires_it()
    {
        var res = await _f.CreateClient().PostAsync("/api/auth/image-cookie/clear", null);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var setCookie = res.Headers.GetValues("Set-Cookie").Single();
        setCookie.Should().Contain(ImageCookie.Name).And.Contain("expires=Thu, 01 Jan 1970");
    }
}
