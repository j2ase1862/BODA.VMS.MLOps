using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BODA.VMS.MLOps.Contracts.Training;
using BODA.VMS.MLOps.Contracts.Workers;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Server.Data.Entities;
using BODA.VMS.MLOps.Server.Services;
using FluentAssertions;
using static BODA.VMS.MLOps.Tests.Server.MlopsApiFactory;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// 학습 로그: 실패·취소한 작업도 train.log 를 받고, 끝난 작업은 보관 폴더(Mlops:LogArchiveDir)에 한 벌 남기며,
/// 화면은 /log 로 같은 내용을 받는다. 워커가 train.log 를 못 올렸으면 서버 DB 의 로그 줄로 대신한다.
/// </summary>
public class JobLogArchiveTests : IClassFixture<MlopsApiFactory>
{
    private readonly MlopsApiFactory _f;
    public JobLogArchiveTests(MlopsApiFactory f) => _f = f;

    private async Task<(HttpClient Eng, TrainingJobDto Job, HttpClient Worker)> AssignedJobAsync(string name)
    {
        var eng = await _f.EngineerAsync();
        var admin = await _f.AdminAsync();
        var model = await CreateModelAsync(eng, name);
        var ds = await UploadDatasetAsync(eng, "ds-" + name, zip: MakeYoloDatasetZip(name.Length % 20));
        await EnsurePretrainedAsync(admin);
        var res = await eng.PostAsJsonAsync("/api/training-jobs", new CreateTrainingJobRequest(ds.Id, model.Id, TrainingScript.TrainDfine,
            PretrainedRef: SharedPretrainedRef, Hyperparams: JsonDocument.Parse("""{"epochs": 40, "patience": 15}""").RootElement, Priority: 50), Json);
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var job = (await res.Content.ReadFromJsonAsync<TrainingJobDto>(Json))!;

        var created = await _f.CreateWorkerAsync(admin, "w-" + name, [TaskType.Detection]);
        var w = _f.WorkerClient(created.Token);
        (await w.PostAsJsonAsync("/api/workers/register",
            new RegisterWorkerRequest("w-" + name, "GPU-PC", OkCapabilities(), [TaskType.Detection], "0.1.5"), Json)).EnsureSuccessStatusCode();
        var a = (await (await w.GetAsync("/api/training-jobs/next?wait=0")).Content.ReadFromJsonAsync<JobAssignment>(Json))!;
        a.Job.Id.Should().Be(job.Id);
        (await w.PostAsync($"/api/training-jobs/{job.Id}/ack", null)).EnsureSuccessStatusCode();
        return (eng, job, w);
    }

    private string ArchivedFile(Guid jobId, string result) =>
        Directory.GetFiles(_f.LogArchiveDir, $"*_{result}_{jobId.ToString("N")[..8]}.log").Should().ContainSingle().Subject;

    [Fact]
    public async Task Failed_job_accepts_log_only_upload_while_preparing_and_is_archived_with_a_header()
    {
        var (eng, job, w) = await AssignedJobAsync("log-prep-fail");

        // 준비 단계에서는 결과물(ONNX 등)은 여전히 못 올린다 — 로그만 받는다
        using (var onnx = new MultipartFormDataContent())
        {
            onnx.Add(new StringContent("{}", Encoding.UTF8, "application/json"), "metrics", "metrics.json");
            (await w.PostAsync($"/api/training-jobs/{job.Id}/artifacts", onnx)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        using (var log = new MultipartFormDataContent())
        {
            log.Add(new StringContent("[worker] 데이터셋 준비 중\n[worker] Failed (PrepareError): 디스크 여유 부족\n"), "log", "train.log");
            var up = await w.PostAsync($"/api/training-jobs/{job.Id}/artifacts", log);
            up.StatusCode.Should().Be(HttpStatusCode.OK, await up.Content.ReadAsStringAsync());
        }
        // 로그만 올린 것으로 상태가 Uploading 으로 넘어가지 않는다
        (await eng.GetFromJsonAsync<TrainingJobDto>($"/api/training-jobs/{job.Id}", Json))!.State.Should().Be(TrainingJobState.Preparing);

        (await w.PostAsJsonAsync($"/api/training-jobs/{job.Id}/finish",
            new FinishJobRequest(TrainingJobState.Failed, "디스크 여유 부족", FailureKinds.PrepareError), Json)).EnsureSuccessStatusCode();

        var path = ArchivedFile(job.Id, "실패");
        Path.GetFileName(path).Should().Contain("_log-prep-fail_");
        var text = await File.ReadAllTextAsync(path);
        text.Should().StartWith("# BODA VMS MLOps 학습 로그")
            .And.Contain($"# 작업       : {job.Id}")
            .And.Contain("# 결과       : 실패 (PrepareError)")
            .And.Contain("# 오류       : 디스크 여유 부족")
            .And.Contain("# 모델 계열  : log-prep-fail")
            .And.Contain("# 하이퍼파라미터 : epochs=40 patience=15")
            .And.Contain("# 본문       : 워커가 올린 train.log")
            .And.Contain("[worker] Failed (PrepareError): 디스크 여유 부족");

        // 화면의 "로그 다운로드" 는 보관 파일과 같은 이름·내용이다 (Viewer 도 받는다)
        var res = await (await _f.ViewerAsync()).GetAsync($"/api/training-jobs/{job.Id}/log");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        var cd = res.Content.Headers.ContentDisposition!;
        (cd.FileNameStar ?? cd.FileName!.Trim('"')).Should().Be(Path.GetFileName(path));
        (await res.Content.ReadAsStringAsync()).Should().Be(text);
    }

    [Fact]
    public async Task Without_a_worker_log_the_server_log_lines_are_used_running_and_after_cancel()
    {
        var (eng, job, w) = await AssignedJobAsync("log-db-fallback");
        (await w.PatchAsJsonAsync($"/api/training-jobs/{job.Id}/progress",
            new ProgressReport(TrainingJobState.Running, 2.5, 1, 40, 108.2, 0.0009,
                [new LogChunkDto(JobLogLevel.Stdout, "[EPOCH] 1/40", DateTime.UtcNow), new LogChunkDto(JobLogLevel.Info, "val_loss=76.9", DateTime.UtcNow)]), Json))
            .EnsureSuccessStatusCode();

        // 도는 중에도 받을 수 있다 — 지금까지의 줄
        var running = await (await eng.GetAsync($"/api/training-jobs/{job.Id}/log")).Content.ReadAsStringAsync();
        running.Should().Contain("# 결과       : 진행중")
            .And.Contain("서버에 남은 로그 줄")
            .And.Contain("[EPOCH] 1/40")
            .And.Contain("val_loss=76.9");

        // 0.1.5 전 워커처럼 train.log 없이 취소로 끝내도 보관은 된다
        (await w.PostAsJsonAsync($"/api/training-jobs/{job.Id}/finish", new FinishJobRequest(TrainingJobState.Cancelled, "취소됨"), Json))
            .EnsureSuccessStatusCode();
        var text = await File.ReadAllTextAsync(ArchivedFile(job.Id, "취소"));
        text.Should().Contain("# 결과       : 취소").And.Contain("[EPOCH] 1/40");
    }

    [Fact]
    public async Task Job_cancelled_while_queued_is_archived_too()
    {
        var eng = await _f.EngineerAsync();
        var model = await CreateModelAsync(eng, "log-queued-cancel");
        var ds = await UploadDatasetAsync(eng, "ds-log-queued-cancel", zip: MakeYoloDatasetZip(7));
        await EnsurePretrainedAsync(await _f.AdminAsync());
        var job = (await (await eng.PostAsJsonAsync("/api/training-jobs",
                new CreateTrainingJobRequest(ds.Id, model.Id, TrainingScript.TrainDfine, PretrainedRef: SharedPretrainedRef, Priority: -50), Json))
            .Content.ReadFromJsonAsync<TrainingJobDto>(Json))!;

        (await eng.PostAsJsonAsync($"/api/training-jobs/{job.Id}/cancel", new CancelJobRequest("잘못 냄"), Json)).EnsureSuccessStatusCode();

        var text = await File.ReadAllTextAsync(ArchivedFile(job.Id, "취소"));
        text.Should().Contain("# 하이퍼파라미터 : (스크립트 기본값)").And.Contain("# 워커       : —");
    }

    [Fact]
    public void Archive_file_name_sorts_by_time_and_is_safe_for_windows()
    {
        var job = new TrainingJob
        {
            Id = Guid.Parse("0e7ede3c-9d54-4549-bded-30e20e3a27fd"),
            State = TrainingJobState.Succeeded,
            FinishedAt = new DateTime(2026, 10, 22, 3, 33, 54, DateTimeKind.Utc),
        };
        var name = JobLogArchiveService.FileName(job, " GS-A 검출/v2:new ");
        var local = new DateTime(2026, 10, 22, 3, 33, 54, DateTimeKind.Utc).ToLocalTime();
        name.Should().Be($"{local:yyyy-MM-dd_HHmmss}_GS-A_검출_v2_new_성공_0e7ede3c.log");
        name.IndexOfAny(Path.GetInvalidFileNameChars()).Should().Be(-1);
    }
}
