using System.Security.Claims;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Training;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Core.Training;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Services;
using Microsoft.Net.Http.Headers;

namespace BODA.VMS.MLOps.Server.Endpoints;

/// <summary>학습 작업 API — 사용자(생성·조회·취소·재실행·로그) + 워커 프로토콜(next/ack/progress/artifacts/finish) (Phase 3 §4)</summary>
public static class TrainingJobEndpoints
{
    public static RouteGroupBuilder MapTrainingJobEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/training-jobs").WithTags("TrainingJobs");

        // ── 사용자 ──
        g.MapPost("/", async (CreateTrainingJobRequest req, TrainingJobService svc, ClaimsPrincipal p, CancellationToken ct) =>
        {
            var dto = await svc.CreateAsync(req, CurrentUser.From(p), ct);
            return Results.Created($"/api/training-jobs/{dto.Id}", dto);
        }).RequireAuthorization(Policies.Engineer);

        g.MapGet("/", async (string? state, Guid? modelId, int? take, TrainingJobService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(EnumBinding.ParseOptional<TrainingJobState>(state, "state"), modelId, take ?? 100, ct)))
            .RequireAuthorization(Policies.Viewer);

        // 워커 long-poll — 반드시 /{id} 보다 먼저 등록되진 않아도 되지만 'next' 는 guid 가 아니므로 충돌 없음
        g.MapGet("/next", async (int? wait, TrainingJobService svc, ClaimsPrincipal p, CancellationToken ct) =>
        {
            var workerId = WorkerEndpoints.RequireWorkerId(p);
            var a = await svc.NextAsync(workerId, wait, ct);
            return a is null ? Results.NoContent() : Results.Ok(a);
        }).RequireAuthorization(Policies.Worker);

        g.MapGet("/{id:guid}", async (Guid id, TrainingJobService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetAsync(id, ct))).RequireAuthorization(Policies.Viewer);

        g.MapGet("/{id:guid}/logs", async (Guid id, long? from, int? take, TrainingJobService svc, CancellationToken ct) =>
            Results.Ok(await svc.LogsAsync(id, from ?? 0, take ?? 500, ct))).RequireAuthorization(Policies.Viewer);

        g.MapGet("/{id:guid}/artifacts", async (Guid id, TrainingJobService svc, CancellationToken ct) =>
            Results.Ok(await svc.ArtifactsAsync(id, ct))).RequireAuthorization(Policies.Viewer);

        g.MapGet("/{id:guid}/artifacts/{kind}", async (Guid id, string kind, TrainingJobService svc, CancellationToken ct) =>
        {
            var k = EnumBinding.Parse<JobArtifactKind>(kind, "kind");
            var (stream, a) = await svc.OpenArtifactAsync(id, k, ct);
            return Results.File(stream, "application/octet-stream", a.FileName, entityTag: new EntityTagHeaderValue($"\"{a.Sha256}\""));
        }).RequireAuthorization(Policies.Viewer);

        g.MapPost("/{id:guid}/cancel", async (Guid id, CancelJobRequest? req, TrainingJobService svc, ClaimsPrincipal p, CancellationToken ct) =>
            Results.Ok(await svc.CancelAsync(id, req ?? new CancelJobRequest(), CurrentUser.From(p), ct))).RequireAuthorization(Policies.Engineer);

        g.MapPost("/{id:guid}/retry", async (Guid id, TrainingJobService svc, ClaimsPrincipal p, CancellationToken ct) =>
        {
            var dto = await svc.RetryAsync(id, CurrentUser.From(p), ct);
            return Results.Created($"/api/training-jobs/{dto.Id}", dto);
        }).RequireAuthorization(Policies.Engineer);

        // ── 워커 프로토콜 ──
        g.MapPost("/{id:guid}/ack", async (Guid id, TrainingJobService svc, ClaimsPrincipal p, CancellationToken ct) =>
            Results.Ok(await svc.AckAsync(id, WorkerEndpoints.RequireWorkerId(p), ct))).RequireAuthorization(Policies.Worker);

        g.MapPatch("/{id:guid}/progress", async (Guid id, ProgressReport report, TrainingJobService svc, ClaimsPrincipal p, CancellationToken ct) =>
        {
            var (job, cancel) = await svc.ProgressAsync(id, WorkerEndpoints.RequireWorkerId(p), report, ct);
            return Results.Ok(new { job, cancelRequested = cancel });
        }).RequireAuthorization(Policies.Worker);

        // multipart 필드: onnx · metrics · curve · train_info · log
        g.MapPost("/{id:guid}/artifacts", async (Guid id, HttpRequest request, TrainingJobService svc, ClaimsPrincipal p, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                throw ApiException.BadRequest(ErrorCodes.Validation, "multipart/form-data 요청이어야 합니다.");
            var form = await request.ReadFormAsync(ct);
            return Results.Ok(await svc.UploadArtifactsAsync(id, WorkerEndpoints.RequireWorkerId(p), form, CurrentUser.From(p), ct));
        }).RequireAuthorization(Policies.Worker);

        g.MapPost("/{id:guid}/finish", async (Guid id, FinishJobRequest req, TrainingJobService svc, ClaimsPrincipal p, CancellationToken ct) =>
            Results.Ok(await svc.FinishAsync(id, WorkerEndpoints.RequireWorkerId(p), req, ct))).RequireAuthorization(Policies.Worker);

        // ── 화면용: 스크립트별 하이퍼파라미터 화이트리스트 ──
        api.MapGet("/training/hyperparams/{script}", (string script) =>
            Results.Ok(HyperparamWhitelist.For(EnumBinding.Parse<TrainingScript>(script, "script")).Select(s => new
            {
                s.Key, type = s.Type.ToString().ToLowerInvariant(), s.Min, s.Max, s.Choices, s.Default,
            }))).WithTags("TrainingJobs").RequireAuthorization(Policies.Viewer);

        return api;
    }
}
