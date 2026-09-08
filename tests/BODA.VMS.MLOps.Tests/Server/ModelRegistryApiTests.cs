using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Tests.TestAssets;
using FluentAssertions;
using static BODA.VMS.MLOps.Tests.Server.MlopsApiFactory;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>Phase 1 §10 서버 단위: 업로드 검증·중복 멱등·클래스 불일치·yolo 라이선스·스테이지 전이·바인딩 롤백·참조 해석</summary>
public class ModelRegistryApiTests : IClassFixture<MlopsApiFactory>
{
    private readonly MlopsApiFactory _f;
    public ModelRegistryApiTests(MlopsApiFactory f) => _f = f;

    [Fact]
    public async Task Upload_creates_candidate_and_duplicate_returns_existing()
    {
        var eng = await _f.EngineerAsync();
        var model = await CreateModelAsync(eng, "m-upload");

        var res = await UploadVersionAsync(eng, model.Id, OnnxStubs.Bytes(OnnxStubs.DeployWithMeta));
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var v1 = (await res.Content.ReadFromJsonAsync<ModelVersionDto>(Json))!;
        v1.Number.Should().Be(1);
        v1.Stage.Should().Be(ModelStage.Candidate);
        v1.Format.Should().Be(ModelFormat.DFine);
        v1.Classes.Should().Equal("good", "defect");
        v1.InputSize.Should().Be(640);
        v1.Source.Should().Be(VersionSource.Upload);

        var dup = await UploadVersionAsync(eng, model.Id, OnnxStubs.Bytes(OnnxStubs.DeployWithMeta));
        dup.StatusCode.Should().Be(HttpStatusCode.OK, "같은 sha256 은 기존 버전 반환(멱등)");
        (await dup.Content.ReadFromJsonAsync<ModelVersionDto>(Json))!.Id.Should().Be(v1.Id);

        var detail = await eng.GetFromJsonAsync<ModelDto>($"/api/models/{model.Id}", Json);
        detail!.VersionCount.Should().Be(1);
        detail.LatestVersion!.Id.Should().Be(v1.Id);
    }

    [Fact]
    public async Task Upload_without_names_requires_meta_classes_and_rejects_mismatch()
    {
        var eng = await _f.EngineerAsync();
        var model = await CreateModelAsync(eng, "m-nometa");

        var missing = await UploadVersionAsync(eng, model.Id, OnnxStubs.Bytes(OnnxStubs.DeployNoMeta));
        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(missing))!.Code.Should().Be(ErrorCodes.MissingNames);

        var mismatch = await UploadVersionAsync(eng, model.Id, OnnxStubs.Bytes(OnnxStubs.DeployNoMeta), new VersionUploadMeta(Classes: ["defect", "good"]));
        mismatch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(mismatch))!.Code.Should().Be(ErrorCodes.ClassMismatch);

        var ok = await UploadVersionAsync(eng, model.Id, OnnxStubs.Bytes(OnnxStubs.DeployNoMeta), new VersionUploadMeta(Classes: ["good", "defect"]));
        ok.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Yolo_requires_license_and_admin_confirmation_for_production()
    {
        var eng = await _f.EngineerAsync();
        var admin = await _f.AdminAsync();
        var model = await CreateModelAsync(eng, "m-yolo");

        var nolic = await UploadVersionAsync(eng, model.Id, OnnxStubs.Bytes(OnnxStubs.YoloStub));
        nolic.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(nolic))!.Code.Should().Be(ErrorCodes.LicenseRequired);

        var res = await UploadVersionAsync(eng, model.Id, OnnxStubs.Bytes(OnnxStubs.YoloStub), new VersionUploadMeta(License: "Ultralytics Enterprise #1234"));
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var v = (await res.Content.ReadFromJsonAsync<ModelVersionDto>(Json))!;
        v.Format.Should().Be(ModelFormat.Yolo);
        v.Warnings.Should().Contain(w => w.Contains("YOLO"));

        (await eng.PostAsJsonAsync($"/api/model-versions/{v.Id}/promote", new PromoteRequest(ModelStage.Staging), Json)).StatusCode.Should().Be(HttpStatusCode.OK);

        var noConfirm = await admin.PostAsJsonAsync($"/api/model-versions/{v.Id}/promote", new PromoteRequest(ModelStage.Production), Json);
        noConfirm.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(noConfirm))!.Code.Should().Be(ErrorCodes.LicenseRequired);

        var ok = await admin.PostAsJsonAsync($"/api/model-versions/{v.Id}/promote", new PromoteRequest(ModelStage.Production, ConfirmLicense: true), Json);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Task_type_mismatch_is_rejected()
    {
        var eng = await _f.EngineerAsync();
        var model = await CreateModelAsync(eng, "m-cls", TaskType.Classification);
        var res = await UploadVersionAsync(eng, model.Id, OnnxStubs.Bytes(OnnxStubs.DeployWithMeta));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(res))!.Code.Should().Be(ErrorCodes.TaskTypeMismatch);
    }

    [Fact]
    public async Task Garbage_file_is_unsupported()
    {
        var eng = await _f.EngineerAsync();
        var model = await CreateModelAsync(eng, "m-garbage");
        var res = await UploadVersionAsync(eng, model.Id, "definitely not an onnx protobuf file"u8.ToArray());
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(res))!.Code.Should().Be(ErrorCodes.UnsupportedFormat);
    }

    [Fact]
    public async Task Promotion_rules_single_production_admin_only_and_history()
    {
        var eng = await _f.EngineerAsync();
        var admin = await _f.AdminAsync();
        var model = await CreateModelAsync(eng, "m-promote");

        // 두 버전: 같은 그래프라도 메타 유무로 sha 가 다르다
        var v1 = (await (await UploadVersionAsync(eng, model.Id, OnnxStubs.Bytes(OnnxStubs.DeployWithMeta))).Content.ReadFromJsonAsync<ModelVersionDto>(Json))!;
        var v2 = (await (await UploadVersionAsync(eng, model.Id, OnnxStubs.Bytes(OnnxStubs.DeployNoMeta), new VersionUploadMeta(Classes: ["good", "defect"]))).Content.ReadFromJsonAsync<ModelVersionDto>(Json))!;
        v2.Number.Should().Be(2);

        // Candidate → Production 직행 불가 (409)
        var jump = await admin.PostAsJsonAsync($"/api/model-versions/{v1.Id}/promote", new PromoteRequest(ModelStage.Production), Json);
        jump.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorAsync(jump))!.Code.Should().Be(ErrorCodes.InvalidStageTransition);

        // Engineer 는 Staging 까지, Production 은 403
        (await eng.PostAsJsonAsync($"/api/model-versions/{v1.Id}/promote", new PromoteRequest(ModelStage.Staging, "test line"), Json)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await eng.PostAsJsonAsync($"/api/model-versions/{v1.Id}/promote", new PromoteRequest(ModelStage.Production), Json)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.PostAsJsonAsync($"/api/model-versions/{v1.Id}/promote", new PromoteRequest(ModelStage.Production), Json)).StatusCode.Should().Be(HttpStatusCode.OK);

        // v2 를 Production 으로 → v1 은 Staging 으로 강등 (Model 당 Production 1개)
        (await eng.PostAsJsonAsync($"/api/model-versions/{v2.Id}/promote", new PromoteRequest(ModelStage.Staging), Json)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.PostAsJsonAsync($"/api/model-versions/{v2.Id}/promote", new PromoteRequest(ModelStage.Production, "better mAP"), Json)).StatusCode.Should().Be(HttpStatusCode.OK);

        var v1After = await eng.GetFromJsonAsync<ModelVersionDto>($"/api/model-versions/{v1.Id}", Json);
        v1After!.Stage.Should().Be(ModelStage.Staging);
        v1After.StageHistory.Should().HaveCount(3); // C→S, S→P, P→S(교체)
        v1After.StageHistory![^1].Reason.Should().Contain("v2");

        var resolved = await (await _f.LineAsync()).GetFromJsonAsync<ResolveResponse>($"/api/models/{model.Id}/resolve?stage=production", Json);
        resolved!.ModelVersionId.Should().Be(v2.Id);
        resolved.Sha256.Should().Be(v2.Sha256);

        var byNumber = await (await _f.LineAsync()).GetFromJsonAsync<ResolveResponse>($"/api/models/{model.Id}/resolve?version=1", Json);
        byNumber!.ModelVersionId.Should().Be(v1.Id);
    }

    [Fact]
    public async Task Artifact_download_has_etag_and_304()
    {
        var eng = await _f.EngineerAsync();
        var line = await _f.LineAsync();
        var model = await CreateModelAsync(eng, "m-artifact");
        var v = (await (await UploadVersionAsync(eng, model.Id, OnnxStubs.Bytes(OnnxStubs.DeployWithMeta))).Content.ReadFromJsonAsync<ModelVersionDto>(Json))!;

        var res = await line.GetAsync($"/api/model-versions/{v.Id}/artifact");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Headers.ETag!.Tag.Should().Be($"\"{v.Sha256}\"");
        (await res.Content.ReadAsByteArrayAsync()).Should().Equal(OnnxStubs.Bytes(OnnxStubs.DeployWithMeta));

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/model-versions/{v.Id}/artifact");
        req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{v.Sha256}\""));
        (await line.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.NotModified);

        // Viewer 는 아티팩트 다운로드 불가 (Line 정책)
        (await (await _f.ViewerAsync()).GetAsync($"/api/model-versions/{v.Id}/artifact")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // 익명은 401
        (await _f.CreateClient().GetAsync($"/api/models")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Binding_set_follow_production_rollback_and_payload()
    {
        var eng = await _f.EngineerAsync();
        var admin = await _f.AdminAsync();
        var model = await CreateModelAsync(eng, "m-binding");
        var v1 = (await (await UploadVersionAsync(eng, model.Id, OnnxStubs.Bytes(OnnxStubs.DeployWithMeta))).Content.ReadFromJsonAsync<ModelVersionDto>(Json))!;
        var v2 = (await (await UploadVersionAsync(eng, model.Id, OnnxStubs.Bytes(OnnxStubs.DeployNoMeta), new VersionUploadMeta(Classes: ["good", "defect"]))).Content.ReadFromJsonAsync<ModelVersionDto>(Json))!;
        await eng.PostAsJsonAsync($"/api/model-versions/{v1.Id}/promote", new PromoteRequest(ModelStage.Staging), Json);
        await admin.PostAsJsonAsync($"/api/model-versions/{v1.Id}/promote", new PromoteRequest(ModelStage.Production), Json);

        const string recipe = "recipe-A", tool = "tool-7";
        var b1 = (await (await eng.PutAsJsonAsync($"/api/recipes/{recipe}/tools/{tool}/model", new SetBindingRequest(BindingMode.Pinned, v1.Id), Json))
            .Content.ReadFromJsonAsync<ModelBindingDto>(Json))!;
        b1.Reference.Should().Be($"model://{model.Id}@1");
        b1.ResolvedVersionId.Should().Be(v1.Id);
        b1.PreviousBindingId.Should().BeNull();

        var b2 = (await (await eng.PutAsJsonAsync($"/api/recipes/{recipe}/tools/{tool}/model", new SetBindingRequest(BindingMode.Pinned, v2.Id, Reason: "try v2"), Json))
            .Content.ReadFromJsonAsync<ModelBindingDto>(Json))!;
        b2.PreviousBindingId.Should().Be(b1.Id);

        var list = await eng.GetFromJsonAsync<List<ModelBindingDto>>($"/api/recipes/{recipe}/model-bindings", Json);
        list.Should().NotBeNull();
        list!.Count(b => b.IsActive).Should().Be(1);
        list!.First(b => b.IsActive).Id.Should().Be(b2.Id);

        // 롤백 → v1 로 새 바인딩
        var rolled = (await (await eng.PostAsJsonAsync($"/api/model-bindings/{b2.Id}/rollback", new RollbackRequest("NG rate up"), Json))
            .Content.ReadFromJsonAsync<ModelBindingDto>(Json))!;
        rolled.ModelVersionId.Should().Be(v1.Id);
        rolled.PreviousBindingId.Should().Be(b2.Id);
        rolled.IsActive.Should().BeTrue();

        // FollowProduction 은 현재 Production(v1) 으로 해석되고, 참조는 @production
        var follow = (await (await eng.PutAsJsonAsync($"/api/recipes/{recipe}/tools/tool-8/model", new SetBindingRequest(BindingMode.FollowProduction, ModelId: model.Id), Json))
            .Content.ReadFromJsonAsync<ModelBindingDto>(Json))!;
        follow.Reference.Should().Be($"model://{model.Id}@production");
        follow.ResolvedVersionId.Should().Be(v1.Id);

        var payload = await (await _f.LineAsync()).GetFromJsonAsync<RecipeModelBindingsPayload>($"/api/recipes/{recipe}/model-bindings/payload", Json);
        payload!.ModelBindings.Should().HaveCount(2);
        payload.ModelBindings.Should().OnlyContain(e => e.Sha256 == v1.Sha256);

        // Retired 버전은 바인딩 불가
        await eng.PostAsJsonAsync($"/api/model-versions/{v2.Id}/promote", new PromoteRequest(ModelStage.Staging), Json);
        (await admin.PostAsJsonAsync($"/api/model-versions/{v2.Id}/promote", new PromoteRequest(ModelStage.Retired), Json)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await eng.PutAsJsonAsync($"/api/recipes/{recipe}/tools/{tool}/model", new SetBindingRequest(BindingMode.Pinned, v2.Id), Json)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Viewer_cannot_create_model()
    {
        var viewer = await _f.ViewerAsync();
        var res = await viewer.PostAsJsonAsync("/api/models", new CreateModelRequest("x", TaskType.Detection, ["a"]), Json);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
