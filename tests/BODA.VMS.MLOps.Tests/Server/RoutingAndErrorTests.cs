using System.Net;
using System.Net.Http.Json;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Core.Domain;
using FluentAssertions;
using static BODA.VMS.MLOps.Tests.Server.MlopsApiFactory;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// 응답 JSON 의 camelCase 열거형을 그대로 URL 에 되돌려 보낼 수 있어야 한다.
/// 최소 API 기본 바인딩은 대소문자를 구분하고, 바인딩 실패를 500 으로 흘리던 회귀를 막는다.
/// </summary>
public class RoutingAndErrorTests : IClassFixture<MlopsApiFactory>
{
    private readonly MlopsApiFactory _f;
    public RoutingAndErrorTests(MlopsApiFactory f) => _f = f;

    [Theory]
    [InlineData("trainDfine")]   // DTO 가 내보내는 형태
    [InlineData("TrainDfine")]   // 열거형 이름 그대로
    [InlineData("traindfine")]
    public async Task Hyperparam_route_accepts_enum_casing(string script)
    {
        var eng = await _f.EngineerAsync();
        var res = await eng.GetAsync($"/api/training/hyperparams/{script}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>(Json);
        body!.Select(x => x["key"].ToString()).Should().Contain(["epochs", "lr", "batch_size", "hsv_v"]);
    }

    [Fact]
    public async Task Unknown_enum_value_is_400_not_500()
    {
        var eng = await _f.EngineerAsync();
        var res = await eng.GetAsync("/api/training/hyperparams/train_evil");
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var err = await ErrorAsync(res);
        err!.Code.Should().Be(ErrorCodes.Validation);
        err.Message.Should().Contain("trainDfine");
    }

    [Fact]
    public async Task Query_enum_filters_accept_camel_case()
    {
        var eng = await _f.EngineerAsync();
        await CreateModelAsync(eng, "routing-detect", TaskType.Detection);
        await CreateModelAsync(eng, "routing-classify", TaskType.Classification, ["ok", "ng"]);

        var detection = await eng.GetFromJsonAsync<List<ModelDto>>("/api/models?taskType=detection", Json);
        detection!.Should().Contain(m => m.Name == "routing-detect").And.NotContain(m => m.Name == "routing-classify");

        (await eng.GetAsync("/api/models?taskType=nope")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await eng.GetAsync("/api/training-jobs?state=queued")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Malformed_json_body_is_400_not_500()
    {
        var eng = await _f.EngineerAsync();
        var res = await eng.PostAsync("/api/models", new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(res))!.Code.Should().Be(ErrorCodes.Validation);
    }
}
