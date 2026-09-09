using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BODA.VMS.MLOps.Contracts.Datasets;
using BODA.VMS.MLOps.Contracts.Lines;
using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Tests.TestAssets;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Server.Auth;
using FluentAssertions;
using SkiaSharp;
using static BODA.VMS.MLOps.Tests.Server.MlopsApiFactory;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// 라인 PC 서비스 계정 (개발 문서 §5.1 배포 · §5.2 수집).
///
/// <para>
/// 라인 PC 는 로그인 화면을 거칠 수 없어 토큰으로 붙는다. 이 토큰 하나로 모델이 라인에 내려가므로
/// 범위가 좁아야 한다 — 모델 참조 해석·아티팩트 내려받기·NG 사진 올리기까지다.
/// 학습 큐와 모델 등록·승격은 열리면 안 된다.
/// </para>
/// </summary>
public class LineClientTests : IClassFixture<MlopsApiFactory>
{
    private readonly MlopsApiFactory _f;
    public LineClientTests(MlopsApiFactory f) => _f = f;

    private async Task<(CreateLineClientResponse Issued, HttpClient Client)> IssueAsync(string lineId = "LINE-1")
    {
        var admin = await _f.AdminAsync();
        var res = await admin.PostAsJsonAsync("/api/line-clients",
            new CreateLineClientRequest($"line-pc-{Guid.NewGuid():N}"[..20], lineId), Json);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        var issued = (await res.Content.ReadFromJsonAsync<CreateLineClientResponse>(Json))!;

        var client = _f.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.Token);
        return (issued, client);
    }

    [Fact]
    public async Task 토큰은_발급_응답에서_한_번만_나온다()
    {
        var (issued, _) = await IssueAsync();

        issued.Token.Should().StartWith(LineTokenAuthenticationHandler.TokenPrefix,
            "접두사로 인증 스킴을 고르므로 접두사가 규약의 일부다");

        // 목록에는 토큰이 없어야 한다 — 서버는 해시만 들고 있다
        var admin = await _f.AdminAsync();
        var list = await admin.GetFromJsonAsync<List<LineClientDto>>("/api/line-clients", Json);
        list!.Should().Contain(c => c.Id == issued.Line.Id);
        var json = await (await admin.GetAsync("/api/line-clients")).Content.ReadAsStringAsync();
        json.Should().NotContain(issued.Token);
    }

    [Fact]
    public async Task 라인_토큰으로_자기_정보를_확인할_수_있다()
    {
        var (issued, line) = await IssueAsync("LINE-7");

        var res = await line.GetAsync("/api/line-clients/me");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("lineId").GetString().Should().Be("LINE-7");
        body.GetProperty("name").GetString().Should().Contain(issued.Line.Name);
    }

    [Fact]
    public async Task 라인_토큰으로_모델_참조를_풀고_아티팩트를_받는다()
    {
        // 이것이 이 토큰이 존재하는 이유다 — 승격된 모델이 라인으로 내려가는 길
        var eng = await _f.EngineerAsync();
        var model = await CreateModelAsync(eng, "line-resolve", TaskType.Detection, ["good", "defect"]);
        var onnx = OnnxStubs.Bytes(OnnxStubs.DeployWithMeta);
        var upload = await UploadVersionAsync(eng, model.Id, onnx,
            new VersionUploadMeta(Classes: ["good", "defect"]));
        upload.EnsureSuccessStatusCode();
        var version = (await upload.Content.ReadFromJsonAsync<ModelVersionDto>(Json))!;

        // 승격은 Candidate → Staging → Production 순서로만 간다
        var admin = await _f.AdminAsync();
        (await admin.PostAsJsonAsync($"/api/model-versions/{version.Id}/promote",
            new PromoteRequest(ModelStage.Staging), Json)).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync($"/api/model-versions/{version.Id}/promote",
            new PromoteRequest(ModelStage.Production), Json)).EnsureSuccessStatusCode();

        var (_, line) = await IssueAsync();

        var resolved = await line.GetFromJsonAsync<ResolveResponse>(
            $"/api/models/{model.Id}/resolve?stage=production", Json);
        resolved!.ModelVersionId.Should().Be(version.Id);
        resolved.Sha256.Should().NotBeNullOrWhiteSpace();

        var artifact = await line.GetAsync($"/api/model-versions/{version.Id}/artifact");
        artifact.StatusCode.Should().Be(HttpStatusCode.OK);
        var bytes = await artifact.Content.ReadAsByteArrayAsync();
        bytes.Should().Equal(onnx, "받은 파일이 올린 파일과 같아야 캐시에 넣을 수 있다");
    }

    [Fact]
    public async Task 라인_토큰으로_NG_사진을_올릴_수_있다()
    {
        var (_, line) = await IssueAsync("LINE-9");

        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(DataManagementApiTests.MakePng(64, 64, SKColors.DarkRed, 21));
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(part, "files", "ng.png");
        form.Add(new StringContent("LINE-9"), "lineId");

        var res = await line.PostAsync("/api/images/line-ng", form);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        (await res.Content.ReadFromJsonAsync<ImageUploadBatchDto>(Json))!.Results
            .Should().OnlyContain(r => r.Image.Source == ImageSource.LineNg);
    }

    [Fact]
    public async Task 라인_토큰의_범위는_좁다()
    {
        var (_, line) = await IssueAsync();

        // 학습 큐 — 워커의 몫
        (await line.GetAsync("/api/training-jobs/next?wait=0")).StatusCode
            .Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        // 모델 등록·데이터셋 관리 — 엔지니어의 몫
        (await line.PostAsJsonAsync("/api/models", new CreateModelRequest("x", TaskType.Detection, ["a"]), Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await line.PostAsJsonAsync("/api/datasets",
            new CreateDatasetRequest("x", TaskType.Detection, ["a"]), Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // 라인 계정 발급 — 관리자의 몫
        (await line.PostAsJsonAsync("/api/line-clients", new CreateLineClientRequest("x", "L"), Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 비활성한_계정은_인증_단계에서_막힌다()
    {
        // "이 라인을 끊었다" 는 조치가 실제로 성립해야 한다
        var (issued, line) = await IssueAsync();
        (await line.GetAsync("/api/line-clients/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        var admin = await _f.AdminAsync();
        (await admin.PostAsJsonAsync($"/api/line-clients/{issued.Line.Id}/disable",
            new DisableLineClientRequest("교체 예정"), Json)).EnsureSuccessStatusCode();

        (await line.GetAsync("/api/line-clients/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // 다시 켜면 같은 토큰이 살아난다
        (await admin.PostAsync($"/api/line-clients/{issued.Line.Id}/enable", null)).EnsureSuccessStatusCode();
        (await line.GetAsync("/api/line-clients/me")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task 토큰을_다시_발급하면_이전_토큰은_죽는다()
    {
        var (issued, old) = await IssueAsync();
        var admin = await _f.AdminAsync();

        var rotated = (await (await admin.PostAsync($"/api/line-clients/{issued.Line.Id}/rotate-token", null))
            .Content.ReadFromJsonAsync<CreateLineClientResponse>(Json))!;
        rotated.Token.Should().NotBe(issued.Token);

        (await old.GetAsync("/api/line-clients/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var fresh = _f.CreateClient();
        fresh.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rotated.Token);
        (await fresh.GetAsync("/api/line-clients/me")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task 모르는_토큰은_거부한다()
    {
        var client = _f.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "ln_아무거나없는토큰값");

        (await client.GetAsync("/api/line-clients/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task lineId_없이_발급할_수_없다()
    {
        var admin = await _f.AdminAsync();

        var res = await admin.PostAsJsonAsync("/api/line-clients",
            new CreateLineClientRequest("이름만", ""), Json);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
