using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Datasets;
using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Contracts.Pretrained;
using BODA.VMS.MLOps.Contracts.Training;
using BODA.VMS.MLOps.Contracts.Workers;
using BODA.VMS.MLOps.Core.Domain;

namespace BODA.VMS.MLOps.Client.Services;

/// <summary>서버가 돌려준 ApiError 를 그대로 들고 있는 예외 — 화면이 code 별로 안내를 다르게 낸다</summary>
public sealed class MlopsApiException(HttpStatusCode status, ApiError error) : Exception(error.Message)
{
    public HttpStatusCode Status { get; } = status;
    public ApiError Error { get; } = error;
    public string Code => Error.Code;

    /// <summary>details 까지 붙인 사용자용 한 줄</summary>
    public string Describe() =>
        Error.Details is { Count: > 0 } d ? $"{Error.Message} ({string.Join("; ", d)})" : Error.Message;
}

/// <summary>MLOps 서버 API 클라이언트. 같은 오리진의 /api 를 호출한다.</summary>
public sealed class MlopsApi(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = MlopsJson.Options;

    // ───────────── 모델 ─────────────

    public Task<List<ModelDto>> ModelsAsync(TaskType? taskType = null, bool archived = false)
    {
        var q = $"/api/models?archived={archived.ToString().ToLowerInvariant()}";
        if (taskType is not null) q += $"&taskType={Camel(taskType.Value)}";
        return GetAsync<List<ModelDto>>(q);
    }

    public Task<ModelDto> ModelAsync(Guid id) => GetAsync<ModelDto>($"/api/models/{id}");
    public Task<ModelDto> CreateModelAsync(CreateModelRequest req) => PostAsync<CreateModelRequest, ModelDto>("/api/models", req);
    public Task<ModelDto> UpdateModelAsync(Guid id, UpdateModelRequest req) => SendJsonAsync<UpdateModelRequest, ModelDto>(HttpMethod.Patch, $"/api/models/{id}", req);
    public Task<List<ModelVersionDto>> VersionsAsync(Guid modelId) => GetAsync<List<ModelVersionDto>>($"/api/models/{modelId}/versions");
    public Task<ModelVersionDto> VersionAsync(Guid id) => GetAsync<ModelVersionDto>($"/api/model-versions/{id}");
    public Task<ModelVersionDto> PromoteAsync(Guid versionId, PromoteRequest req) => PostAsync<PromoteRequest, ModelVersionDto>($"/api/model-versions/{versionId}/promote", req);
    public static string ArtifactUrl(Guid versionId) => $"/api/model-versions/{versionId}/artifact";

    /// <summary>ONNX 업로드 — file 파트 + meta(json) 파트</summary>
    public async Task<ModelVersionDto> UploadVersionAsync(Guid modelId, string fileName, Stream content, VersionUploadMeta meta, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        form.Add(new StringContent(JsonSerializer.Serialize(meta, Json), Encoding.UTF8, "application/json"), "meta");
        using var res = await http.PostAsync($"/api/models/{modelId}/versions", form, ct);
        return await ReadAsync<ModelVersionDto>(res);
    }

    // ───────────── 바인딩 ─────────────

    public Task<List<ModelBindingDto>> BindingsAsync(string recipeId) => GetAsync<List<ModelBindingDto>>($"/api/recipes/{Uri.EscapeDataString(recipeId)}/model-bindings");
    public Task<ModelBindingDto> SetBindingAsync(string recipeId, string toolId, SetBindingRequest req) =>
        SendJsonAsync<SetBindingRequest, ModelBindingDto>(HttpMethod.Put, $"/api/recipes/{Uri.EscapeDataString(recipeId)}/tools/{Uri.EscapeDataString(toolId)}/model", req);
    public Task<ModelBindingDto> RollbackBindingAsync(Guid bindingId, RollbackRequest req) =>
        PostAsync<RollbackRequest, ModelBindingDto>($"/api/model-bindings/{bindingId}/rollback", req);

    // ───────────── 워커 ─────────────

    public Task<List<WorkerDto>> WorkersAsync() => GetAsync<List<WorkerDto>>("/api/workers");
    public Task<CreateWorkerResponse> CreateWorkerAsync(CreateWorkerRequest req) => PostAsync<CreateWorkerRequest, CreateWorkerResponse>("/api/workers", req);
    public Task<CreateWorkerResponse> RotateWorkerTokenAsync(Guid id) => PostAsync<object?, CreateWorkerResponse>($"/api/workers/{id}/rotate-token", null);
    public Task<WorkerDto> DisableWorkerAsync(Guid id, string? reason) => PostAsync<object, WorkerDto>($"/api/workers/{id}/disable", new { reason });
    public Task<WorkerDto> EnableWorkerAsync(Guid id) => PostAsync<object?, WorkerDto>($"/api/workers/{id}/enable", null);
    public Task<ScriptsManifest> ScriptsAsync() => GetAsync<ScriptsManifest>("/api/workers/scripts");

    // ───────────── 학습 작업 ─────────────

    public Task<List<TrainingJobDto>> JobsAsync(TrainingJobState? state = null, Guid? modelId = null, int take = 100)
    {
        var q = $"/api/training-jobs?take={take}";
        if (state is not null) q += $"&state={Camel(state.Value)}";
        if (modelId is not null) q += $"&modelId={modelId}";
        return GetAsync<List<TrainingJobDto>>(q);
    }

    public Task<TrainingJobDto> JobAsync(Guid id) => GetAsync<TrainingJobDto>($"/api/training-jobs/{id}");
    public Task<TrainingJobDto> CreateJobAsync(CreateTrainingJobRequest req) => PostAsync<CreateTrainingJobRequest, TrainingJobDto>("/api/training-jobs", req);
    public Task<TrainingJobDto> CancelJobAsync(Guid id, string? reason) => PostAsync<CancelJobRequest, TrainingJobDto>($"/api/training-jobs/{id}/cancel", new CancelJobRequest(reason));
    public Task<TrainingJobDto> RetryJobAsync(Guid id) => PostAsync<object?, TrainingJobDto>($"/api/training-jobs/{id}/retry", null);
    public Task<JobLogPage> JobLogsAsync(Guid id, long from = 0, int take = 500) => GetAsync<JobLogPage>($"/api/training-jobs/{id}/logs?from={from}&take={take}");
    public Task<List<JobArtifactDto>> JobArtifactsAsync(Guid id) => GetAsync<List<JobArtifactDto>>($"/api/training-jobs/{id}/artifacts");

    public sealed record HyperparamSpecDto(string Key, string Type, double? Min, double? Max, string[]? Choices, string? Default);
    public Task<List<HyperparamSpecDto>> HyperparamsAsync(TrainingScript script) => GetAsync<List<HyperparamSpecDto>>($"/api/training/hyperparams/{Camel(script)}");

    // ───────────── 데이터셋·사전학습 ─────────────

    public Task<List<DatasetVersionDto>> DatasetsAsync() => GetAsync<List<DatasetVersionDto>>("/api/dataset-versions");

    public async Task<DatasetVersionDto> UploadDatasetAsync(string fileName, Stream zip, DatasetVersionUploadMeta meta, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(zip);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(file, "file", fileName);
        form.Add(new StringContent(JsonSerializer.Serialize(meta, Json), Encoding.UTF8, "application/json"), "meta");
        using var res = await http.PostAsync("/api/dataset-versions", form, ct);
        return await ReadAsync<DatasetVersionDto>(res);
    }

    public Task<List<PretrainedAssetDto>> PretrainedAsync() => GetAsync<List<PretrainedAssetDto>>("/api/pretrained");
    public Task<PretrainedAssetDto> CreatePretrainedAsync(CreatePretrainedAssetRequest req) => PostAsync<CreatePretrainedAssetRequest, PretrainedAssetDto>("/api/pretrained", req);
    public Task DeletePretrainedAsync(string @ref) => SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/pretrained/{Uri.EscapeDataString(@ref)}"));

    public async Task<PretrainedAssetDto> UploadPretrainedFileAsync(string @ref, string fileName, Stream content, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        using var res = await http.PostAsync($"/api/pretrained/{Uri.EscapeDataString(@ref)}/files", form, ct);
        return await ReadAsync<PretrainedAssetDto>(res);
    }

    // ───────────── 인증 ─────────────

    public sealed record MeResponse(string Name, string[] Roles, Guid? WorkerId);
    public Task<MeResponse> MeAsync() => GetAsync<MeResponse>("/api/auth/me");

    public sealed record DevTokenResponse(string Token, string[] Roles);

    /// <summary>개발 서버에서만 열려 있다 (Auth:EnableDevTokens). 닫혀 있으면 404 라 화면이 조용히 숨긴다.</summary>
    public async Task<DevTokenResponse?> DevTokenAsync(string user, string[] roles)
    {
        using var res = await http.PostAsJsonAsync("/api/auth/dev-token", new { user, roles }, Json);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        return await ReadAsync<DevTokenResponse>(res);
    }

    // ───────────── 하부 ─────────────

    private async Task<T> GetAsync<T>(string url)
    {
        using var res = await http.GetAsync(url);
        return await ReadAsync<T>(res);
    }

    private async Task<TRes> PostAsync<TReq, TRes>(string url, TReq? body)
    {
        using var res = body is null
            ? await http.PostAsync(url, new StringContent("{}", Encoding.UTF8, "application/json"))
            : await http.PostAsJsonAsync(url, body, Json);
        return await ReadAsync<TRes>(res);
    }

    private async Task<TRes> SendJsonAsync<TReq, TRes>(HttpMethod method, string url, TReq body)
    {
        using var req = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body, options: Json) };
        using var res = await http.SendAsync(req);
        return await ReadAsync<TRes>(res);
    }

    private async Task SendAsync(HttpRequestMessage request)
    {
        using var res = await http.SendAsync(request);
        if (!res.IsSuccessStatusCode) throw await ToExceptionAsync(res);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage res)
    {
        if (!res.IsSuccessStatusCode) throw await ToExceptionAsync(res);
        var value = await res.Content.ReadFromJsonAsync<T>(Json);
        return value ?? throw new MlopsApiException(res.StatusCode, new ApiError("EmptyResponse", "서버가 빈 응답을 보냈습니다."));
    }

    private static async Task<MlopsApiException> ToExceptionAsync(HttpResponseMessage res)
    {
        ApiError? error = null;
        try
        {
            var text = await res.Content.ReadAsStringAsync();
            if (!string.IsNullOrWhiteSpace(text)) error = JsonSerializer.Deserialize<ApiError>(text, Json);
        }
        catch (JsonException) { /* JSON 이 아닌 오류 본문 */ }

        error ??= res.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new ApiError("Unauthorized", "로그인이 필요합니다."),
            HttpStatusCode.Forbidden => new ApiError(ErrorCodes.Forbidden, "권한이 없습니다."),
            HttpStatusCode.NotFound => new ApiError(ErrorCodes.NotFound, "대상을 찾을 수 없습니다."),
            _ => new ApiError("HttpError", $"요청 실패 ({(int)res.StatusCode})"),
        };
        return new MlopsApiException(res.StatusCode, error);
    }

    /// <summary>열거형을 API 가 쓰는 camelCase 로 (라우트·쿼리는 대소문자를 무시하지만 응답과 형태를 맞춘다)</summary>
    private static string Camel<T>(T value) where T : struct, Enum
    {
        var s = value.ToString()!;
        return char.ToLowerInvariant(s[0]) + s[1..];
    }
}
