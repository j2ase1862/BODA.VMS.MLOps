using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BODA.VMS.MLOps.Contracts.Training;
using BODA.VMS.MLOps.Contracts.Workers;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Server.Endpoints;
using BODA.VMS.MLOps.Tests.TestAssets;
using FluentAssertions;
using SkiaSharp;
using static BODA.VMS.MLOps.Tests.Server.MlopsApiFactory;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// 화면의 [다운로드] 는 링크(&lt;a href download&gt;)라 Authorization 헤더가 붙지 않는다. 그래서 받기 주소는
/// 다운로드 쿠키로도 인증한다 — 그 전에는 새 탭으로 열려 데이터셋 zip · 모델 ONNX · 사전학습 파일 · 학습 아티팩트가
/// 전부 401 이었다 (2026-10-22 점검). 아래 시험은 화면이 만드는 주소 그대로, 브라우저처럼 쿠키만 실어 부른다.
/// </summary>
public class DownloadCookieTests : IClassFixture<MlopsApiFactory>
{
    private readonly MlopsApiFactory _f;
    public DownloadCookieTests(MlopsApiFactory f) => _f = f;

    private sealed record Targets(string DatasetExport, string ModelArtifact, string PretrainedFile, string JobLog,
        IReadOnlyList<string> JobArtifacts, Guid JobId)
    {
        public IEnumerable<string> EngineerOnly => [DatasetExport, ModelArtifact, PretrainedFile];
        public IEnumerable<string> ForEveryone => JobArtifacts.Append(JobLog);
    }

    /// <summary>받을 것이 하나씩 다 있는 상태 — 데이터셋 버전, 끝난 학습 작업(아티팩트 5종 + 결과 모델 버전), 사전학습 파일</summary>
    private async Task<Targets> ArrangeAsync(string name, int seed)
    {
        var eng = await _f.EngineerAsync();
        var admin = await _f.AdminAsync();
        var dv = await UploadDatasetAsync(eng, "ds-" + name, zip: MakeYoloDatasetZip(seed));
        var model = await CreateModelAsync(eng, name);

        var pre = await EnsurePretrainedAsync(admin);
        using (var pf = new MultipartFormDataContent())
        {
            pf.Add(new ByteArrayContent("{\"model_type\":\"d_fine\"}"u8.ToArray()), "file", "config.json");
            (await admin.PostAsync($"/api/pretrained/{pre}/files", pf)).EnsureSuccessStatusCode();
        }
        var preDto = await eng.GetFromJsonAsync<JsonElement>($"/api/pretrained/{pre}", Json);

        var job = (await (await eng.PostAsJsonAsync("/api/training-jobs", new CreateTrainingJobRequest(dv.Id, model.Id, TrainingScript.TrainDfine,
            PretrainedRef: pre, Priority: 99), Json)).Content.ReadFromJsonAsync<TrainingJobDto>(Json))!;
        var wk = await _f.CreateWorkerAsync(admin, "w-" + name, [TaskType.Detection]);
        var w = _f.WorkerClient(wk.Token);
        (await w.PostAsJsonAsync("/api/workers/register", new RegisterWorkerRequest("w-" + name, "PC", OkCapabilities(), [TaskType.Detection], "0.1.5"), Json)).EnsureSuccessStatusCode();
        (await w.GetAsync("/api/training-jobs/next?wait=0")).EnsureSuccessStatusCode();
        (await w.PostAsync($"/api/training-jobs/{job.Id}/ack", null)).EnsureSuccessStatusCode();
        (await w.PatchAsJsonAsync($"/api/training-jobs/{job.Id}/progress", new ProgressReport(TrainingJobState.Running, 50, 1, 2), Json)).EnsureSuccessStatusCode();
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent(OnnxStubs.Bytes(OnnxStubs.DeployNoMeta)), "onnx", "best.onnx");
            form.Add(new StringContent("""{"map50": 0.9}""", Encoding.UTF8, "application/json"), "metrics", "metrics.json");
            form.Add(new ByteArrayContent(DataManagementApiTests.MakePng(64, 64, SKColors.White, seed)), "curve", "curves.png");
            form.Add(new StringContent("""{"names": ["good", "defect"], "imgsz": 640}""", Encoding.UTF8, "application/json"), "train_info", "vms_train_info.json");
            form.Add(new StringContent("[EPOCH] 1/2\n[DONE]\n"), "log", "train.log");
            (await w.PostAsync($"/api/training-jobs/{job.Id}/artifacts", form)).EnsureSuccessStatusCode();
        }
        var done = (await (await w.PostAsJsonAsync($"/api/training-jobs/{job.Id}/finish", new FinishJobRequest(TrainingJobState.Succeeded), Json))
            .Content.ReadFromJsonAsync<TrainingJobDto>(Json))!;
        var artifacts = (await eng.GetFromJsonAsync<List<JobArtifactDto>>($"/api/training-jobs/{job.Id}/artifacts", Json))!;
        artifacts.Should().HaveCount(5);

        return new Targets(
            dv.ExportUrl,
            $"/api/model-versions/{done.ResultModelVersionId}/artifact",              // MlopsApi.ArtifactUrl 과 같은 모양
            preDto.GetProperty("files")[0].GetProperty("url").GetString()!,
            $"/api/training-jobs/{job.Id}/log",                                          // MlopsApi.JobLogUrl 과 같은 모양
            artifacts.Select(a => a.Url).ToList(),
            job.Id);
    }

    /// <summary>로그인한 화면처럼 쿠키를 받아 둔 "브라우저" — 이후 요청에는 Authorization 헤더가 없다</summary>
    private async Task<HttpClient> BrowserAsync(string user, string role)
    {
        var token = (await _f.ClientAsAsync(user, role)).DefaultRequestHeaders.Authorization!.Parameter!;
        var browser = _f.CreateClient();   // 쿠키 저장소가 있어 Set-Cookie 를 경로 규칙대로 다시 보낸다
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/image-cookie");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await browser.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        browser.DefaultRequestHeaders.Authorization.Should().BeNull();
        return browser;
    }

    [Fact]
    public async Task Engineer_browser_downloads_every_file_with_the_cookie_alone()
    {
        var t = await ArrangeAsync("dl-eng", 11);
        var browser = await BrowserAsync("dl-engineer", "Engineer");

        foreach (var url in t.EngineerOnly.Concat(t.ForEveryone))
        {
            using var res = await browser.GetAsync(url);
            res.StatusCode.Should().Be(HttpStatusCode.OK, url);
            res.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment", url);
            (await res.Content.ReadAsByteArrayAsync()).Should().NotBeEmpty(url);
        }
    }

    [Fact]
    public async Task Big_files_resume_with_range_through_the_cookie()
    {
        var t = await ArrangeAsync("dl-range", 12);
        var browser = await BrowserAsync("dl-range-eng", "Engineer");

        // 데이터셋 zip · 모델 ONNX · 사전학습 파일은 커서, 끊기면 이어받을 수 있어야 한다
        foreach (var url in t.EngineerOnly)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(4, null);
            using var res = await browser.SendAsync(req);
            res.StatusCode.Should().Be(HttpStatusCode.PartialContent, url);
        }
    }

    [Fact]
    public async Task Viewer_browser_gets_training_files_but_not_engineer_only_downloads()
    {
        var t = await ArrangeAsync("dl-viewer", 13);
        var browser = await BrowserAsync("dl-viewer", "Viewer");

        foreach (var url in t.ForEveryone)
            (await browser.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.OK, url);
        // 화면은 이 세 버튼을 엔지니어 이상에게만 보인다 (ClientDownloadLinkSourceTests)
        foreach (var url in t.EngineerOnly)
            (await browser.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Forbidden, url);
    }

    [Fact]
    public async Task Download_cookie_unlocks_nothing_but_marked_downloads()
    {
        var t = await ArrangeAsync("dl-scope", 14);
        var browser = await BrowserAsync("dl-scope-admin", "Admin");

        // 목록·상세 같은 다른 GET 은 쿠키만으로 안 된다
        foreach (var path in new[] { "/api/training-jobs", $"/api/training-jobs/{t.JobId}", $"/api/training-jobs/{t.JobId}/logs",
                     $"/api/training-jobs/{t.JobId}/artifacts", "/api/models", "/api/pretrained", "/api/workers" })
            (await browser.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, path);

        // 바꾸는 요청은 더더욱 — 쿠키를 실은 요청을 남이 대신 보내게 만드는 공격(CSRF)의 문을 열지 않는다
        (await browser.PostAsync($"/api/training-jobs/{t.JobId}/retry", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await browser.DeleteAsync($"/api/training-jobs/{t.JobId}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // 쿠키가 없으면 받기 주소도 401 이다
        (await _f.CreateClient().GetAsync(t.JobLog)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Bearer_wins_over_a_stale_download_cookie_and_garbage_is_rejected()
    {
        var t = await ArrangeAsync("dl-stale", 15);
        var eng = await _f.EngineerAsync();

        using (var req = new HttpRequestMessage(HttpMethod.Get, t.JobLog))
        {
            req.Headers.Authorization = eng.DefaultRequestHeaders.Authorization;
            req.Headers.Add("Cookie", $"{DownloadCookie.Name}=쓸모없는값");
            (await _f.CreateClient().SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        using (var req = new HttpRequestMessage(HttpMethod.Get, t.JobLog))
        {
            req.Headers.Add("Cookie", $"{DownloadCookie.Name}=not.a.jwt");
            (await _f.CreateClient().SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
    }

    [Fact]
    public async Task Cookie_is_http_only_strict_under_api_and_cleared_with_the_image_cookie()
    {
        var eng = await _f.EngineerAsync();
        var issued = await eng.PostAsync("/api/auth/image-cookie", null);
        issued.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var set = issued.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(DownloadCookie.Name + "="));
        set.Should().Contain("httponly", "스크립트가 토큰을 읽지 못해야 한다")
            .And.Contain($"path={DownloadCookie.Path}")
            .And.Contain("samesite=strict", "다른 사이트에서 부르는 요청에는 붙지 않아야 한다")
            .And.NotContain("expires=");

        var cleared = await _f.CreateClient().PostAsync("/api/auth/image-cookie/clear", null);
        cleared.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(DownloadCookie.Name + "="))
            .Should().Contain("expires=Thu, 01 Jan 1970");
    }
}
