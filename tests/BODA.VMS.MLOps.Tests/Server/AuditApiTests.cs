using System.Net;
using System.Net.Http.Json;
using BODA.VMS.MLOps.Contracts.Audit;
using BODA.VMS.MLOps.Contracts.Datasets;
using BODA.VMS.MLOps.Core.Domain;
using FluentAssertions;
using SkiaSharp;
using static BODA.VMS.MLOps.Tests.Server.MlopsApiFactory;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// 감사 로그 읽기 (ML-25). Admin 만 보고, 기간·종류·동작·사람으로 거른다. 쓰기·지우기 길은 없다.
/// </summary>
public class AuditApiTests
{
    [Fact]
    public async Task Admin_reads_the_audit_log_with_filters_and_others_cannot()
    {
        using var f = new MlopsApiFactory();
        var admin = await f.AdminAsync();
        var eng = await f.EngineerAsync();

        // 기록을 남긴다 — 데이터셋 만들기, 라벨 완료 사진 반려(ML-08: 어디서 되돌렸는지 남는다)
        var dataset = (await (await eng.PostAsJsonAsync("/api/datasets",
            new CreateDatasetRequest("감사 시험", TaskType.Detection, ["good"]), Json))
            .Content.ReadFromJsonAsync<DatasetDto>(Json))!;
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(DataManagementApiTests.MakePng(80, 60, SKColors.Orange, 77));
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(part, "files", "audit.png");
        form.Add(new StringContent("manual"), "source");
        var imageId = (await (await eng.PostAsync("/api/images", form)).Content.ReadFromJsonAsync<ImageUploadBatchDto>(Json))!
            .Results[0].Image.Id;
        await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/images", new AddImagesRequest([imageId]), Json);
        await eng.PutAsJsonAsync($"/api/datasets/{dataset.Id}/images/{imageId}/labels",
            new SaveLabelsRequest([new AnnotationDto(AnnotationShape.Box, "good", 0.1, 0.1, 0.2, 0.2)], true), Json);
        (await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/images/{imageId}/review", new ReviewRequest(false), Json))
            .EnsureSuccessStatusCode();

        (await eng.GetAsync("/api/audit")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "감사 로그는 Admin 만 본다");

        var all = (await admin.GetFromJsonAsync<AuditLogPageDto>("/api/audit", Json))!;
        all.Total.Should().BeGreaterThan(0);
        all.Categories.Should().Contain("Dataset");
        all.Items.Should().BeInDescendingOrder(i => i.At);

        var rejected = (await admin.GetFromJsonAsync<AuditLogPageDto>("/api/audit?category=Dataset&action=LabelRejected", Json))!;
        var row = rejected.Items.Should().ContainSingle().Subject;
        row.Actor.Should().Be("engineer");
        row.EntityId.Should().Be(imageId.ToString());
        row.Details.Should().Contain("\"from\":\"Labeled\"").And.Contain("\"to\":\"InProgress\"");
        rejected.Total.Should().Be(1);

        var byActor = (await admin.GetFromJsonAsync<AuditLogPageDto>("/api/audit?actor=nobody-here", Json))!;
        byActor.Total.Should().Be(0);

        // 기간 — 미래부터면 없고, 과거까지면 없다
        var future = Uri.EscapeDataString(DateTime.UtcNow.AddYears(5).ToString("O"));
        var past = Uri.EscapeDataString(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToString("O"));
        (await admin.GetFromJsonAsync<AuditLogPageDto>($"/api/audit?from={future}", Json))!.Total.Should().Be(0);
        (await admin.GetFromJsonAsync<AuditLogPageDto>($"/api/audit?to={past}", Json))!.Total.Should().Be(0);
        (await admin.GetAsync($"/api/audit?from={future}&to={past}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 쪽 나누기
        var page = (await admin.GetFromJsonAsync<AuditLogPageDto>("/api/audit?take=1", Json))!;
        page.Items.Should().HaveCount(1);
        page.Total.Should().Be(all.Total);
    }
}
