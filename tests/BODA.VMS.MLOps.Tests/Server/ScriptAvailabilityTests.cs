using System.Net;
using System.Net.Http.Json;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Contracts.Training;
using BODA.VMS.MLOps.Core.Domain;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using static BODA.VMS.MLOps.Tests.Server.MlopsApiFactory;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>운영 기본값 — train_yolo 는 이 서버에서 쓰지 않는다.</summary>
public class YoloDisabledFactory : MlopsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Mlops:DisabledScripts", nameof(TrainingScript.TrainYolo));
    }
}

/// <summary>
/// 워커에 프레임워크가 없는 스크립트는 <b>고르지도 제출하지도</b> 못해야 한다.
///
/// <para>Ultralytics YOLO 는 AGPL-3.0 이라 검출 백본을 D-FINE(Apache 2.0)으로 옮겼고,
/// <c>ultralytics</c> 는 워커 패키지 허용 목록에도 일부러 없다. 그런데 제출은 막히지 않아서,
/// 고르면 라이선스 확인 문구까지 받아 놓고 <b>워커가 받아 돌리는 순간</b>
/// "ultralytics가 설치되지 않았습니다" 로 실패했다 — 몇 분 기다린 끝에야 알게 되는 길이다
/// (2026-09-16 확인). 제출 시점에 끊고, 목록에서도 뺀다.</para>
/// </summary>
public class ScriptAvailabilityTests : IClassFixture<YoloDisabledFactory>
{
    private readonly YoloDisabledFactory _f;
    public ScriptAvailabilityTests(YoloDisabledFactory f) => _f = f;

    [Fact]
    public async Task 쓰지_않는_스크립트는_목록에서_빠지고_제출도_거부된다()
    {
        var eng = await _f.EngineerAsync();

        // ① 화면이 받는 목록에서 빠진다 — 고를 수 없으면 헤맬 일이 없다
        var scripts = await eng.GetFromJsonAsync<List<string>>("/api/training/scripts", Json);
        scripts!.Should().NotContain(nameof(TrainingScript.TrainYolo));
        scripts.Should().Contain(nameof(TrainingScript.TrainDfine), "쓸 수 있는 것은 그대로 남아야 한다");

        // ② 목록을 우회해 직접 제출해도 거부된다 (라이선스 문구를 채워도 마찬가지)
        var model = await CreateModelAsync(eng, "m-yolo-off");
        var ds = await UploadDatasetAsync(eng, "ds-yolo-off");
        var res = await eng.PostAsJsonAsync("/api/training-jobs",
            new CreateTrainingJobRequest(ds.Id, model.Id, TrainingScript.TrainYolo, License: "Ultralytics Enterprise #1"), Json);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var err = await ErrorAsync(res);
        err!.Code.Should().Be(ErrorCodes.ScriptDisabled);
        err.Message.Should().Contain("train_yolo.py").And.Contain("이 서버에서 쓰지 않습니다");

        // ③ 막는 것은 그 스크립트뿐이다 — 나머지는 종전대로 돈다
        await EnsurePretrainedAsync(await _f.AdminAsync());
        (await eng.PostAsJsonAsync("/api/training-jobs",
            new CreateTrainingJobRequest(ds.Id, model.Id, TrainingScript.TrainDfine, PretrainedRef: SharedPretrainedRef), Json))
            .StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
