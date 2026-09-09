using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Core.Monitoring;
using BODA.VMS.MLOps.Server.Data;
using BODA.VMS.MLOps.Server.Data.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using static BODA.VMS.MLOps.Tests.Server.MlopsApiFactory;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// 운영 웹 자리에 가짜를 세워, 받은 줄이 우리 모델 버전과 어떻게 이어지는지 본다.
///
/// <para>
/// 이 경로는 두 시스템에 걸쳐 있어 눈으로만 확인하면 조용히 어긋난다 — 식별자 형식이
/// 달라지거나 라인이 여러 줄로 보내면 모델 줄이 그냥 <b>안 나타난다</b>. 오류도 나지 않는다.
/// </para>
/// </summary>
public class MonitoringReportTests : IClassFixture<MonitoringReportTests.StubbedWebFactory>
{
    private readonly StubbedWebFactory _f;
    public MonitoringReportTests(StubbedWebFactory f) => _f = f;

    /// <summary>
    /// 운영 웹 자리에 세우는 가짜. 기준 구간과 최근 구간을 요청 순서가 아니라
    /// <c>startDate</c> 로 갈라 답한다 — 서비스가 부르는 순서에 시험이 기대지 않게 한다.
    /// </summary>
    public sealed class StubbedWebFactory : MlopsApiFactory
    {
        /// <summary>기준 구간에서 만든 모델 버전 (mv: 로 이어진다).</summary>
        public Guid VersionId { get; } = Guid.NewGuid();
        public Guid ModelId { get; } = Guid.NewGuid();
        public Guid JobId { get; } = Guid.NewGuid();

        /// <summary>가짜가 받은 요청 — audience·역할을 확인할 수 있게 남긴다.</summary>
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Monitoring:ProductionWebUrl", "http://stub-web.invalid");
            builder.UseSetting("Monitoring:RecentWindowDays", "7");
            builder.UseSetting("Monitoring:BaselineWindowDays", "21");

            builder.ConfigureServices(services =>
            {
                services.AddHttpClient<MLOps.Server.Services.Monitoring.ProductionOutcomeClient>()
                        .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(this));
            });
        }

        private sealed class StubHandler(StubbedWebFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                owner.Requests.Add(request);

                // 최근 구간의 startDate 는 "지금 - 7일" 근처다. 그보다 이른 것이 기준 구간.
                var start = ParseStart(request.RequestUri!.Query);
                bool recent = start > owner.Clock.GetUtcNow().UtcDateTime.AddDays(-14);

                var tag = ModelVersionTag.Write(owner.VersionId);
                var rows = recent
                    ? new object[]
                    {
                        // 라인 둘이 같은 모델을 돌린다 — 합쳐서 700건 · 불량 70건(10%)이 되어야 한다
                        Row(tag, 400, 40, 0.90, 400, 120, 44),
                        Row(tag, 300, 30, 0.90, 300, 121, 44),
                        // 옛 VMS — 이어지지 않지만 세어야 한다
                        Row("DetectionTool:best.onnx", 50, 3, 0.80, 50, 120, 44),
                    }
                    : new object[]
                    {
                        Row(tag, 2000, 40, 0.92, 2000, 120, 45),
                    };

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(rows), Encoding.UTF8, "application/json"),
                });
            }

            private static object Row(string? version, int total, int ng, double confidence,
                int confidenceSamples, double brightness, double focus) => new
                {
                    modelVersion = version,
                    totalCount = total,
                    passCount = total - ng,
                    ngCount = ng,
                    avgConfidence = confidence,
                    minConfidence = confidence - 0.2,
                    confidenceSampleCount = confidenceSamples,
                    avgBrightness = brightness,
                    avgFocusScore = focus,
                    avgCycleTimeMs = 250.0,
                };

            private static DateTime ParseStart(string query)
            {
                var value = System.Web.HttpUtility.ParseQueryString(query)["startDate"];
                return DateTime.Parse(value!, null, System.Globalization.DateTimeStyles.RoundtripKind);
            }
        }
    }

    /// <summary>모델 계열과 버전을 넣어 둔다 — 이것이 있어야 mv: 가 이름을 얻는다.</summary>
    private async Task SeedAsync()
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MlopsDbContext>();
        if (await db.Models.FindAsync(_f.ModelId) is not null) return;

        db.Models.Add(new Model
        {
            Id = _f.ModelId, Name = "라인A 스크래치 검출", TaskType = TaskType.Detection,
            CreatedBy = "test", CreatedAt = DateTime.UtcNow,
        });
        db.ModelVersions.Add(new ModelVersion
        {
            Id = _f.VersionId, ModelId = _f.ModelId, Number = 3, Stage = ModelStage.Production,
            Sha256 = new string('a', 64), ArtifactKey = "k", SizeBytes = 1, Format = ModelFormat.DFine,
            Source = VersionSource.TrainingJob, TrainingJobId = _f.JobId,
            CreatedBy = "test", CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Merges_lines_matches_our_version_and_counts_what_did_not_match()
    {
        await SeedAsync();
        var viewer = await _f.ViewerAsync();

        var report = await viewer.GetFromJsonAsync<ModelMonitorReportDto>("/api/monitoring/models", Json);

        report!.Available.Should().BeTrue(report.Message);
        var model = report.Models.Should().ContainSingle().Subject;

        model.ModelName.Should().Be("라인A 스크래치 검출");
        model.Number.Should().Be(3);
        // 라인 두 줄이 합쳐져야 한다. 합치지 않으면 각 줄이 100건 문턱을 못 넘어 "판단 보류" 가 된다.
        model.RecentTotal.Should().Be(700);
        model.RecentNgRate.Should().BeApproximately(0.10, 1e-9);
        model.BaselineTotal.Should().Be(2000);
        model.BaselineNgRate.Should().BeApproximately(0.02, 1e-9);
        model.Signal.Should().Be(nameof(ModelHealthSignal.NgRateRose));
        model.SuggestRetrain.Should().BeTrue();

        // 옛 형식은 버리지 않고 센다 — 이 수가 크면 라인이 아직 옛 VMS 를 쓰고 있다는 뜻이다
        report.UnmatchedTotal.Should().Be(50);
    }

    /// <summary>재학습 제안을 실제로 누르려면 화면이 모델 계열과 원본 작업을 알아야 한다.</summary>
    [Fact]
    public async Task Carries_the_ids_the_retrain_button_needs()
    {
        await SeedAsync();
        var viewer = await _f.ViewerAsync();

        var report = await viewer.GetFromJsonAsync<ModelMonitorReportDto>("/api/monitoring/models", Json);

        var model = report!.Models.Should().ContainSingle().Subject;
        model.ModelVersionId.Should().Be(_f.VersionId);
        model.ModelId.Should().Be(_f.ModelId);
        model.SourceJobId.Should().Be(_f.JobId);
    }

    /// <summary>
    /// 남의 서버로 나가는 토큰은 그쪽이 검증하는 audience 여야 하고, 역할은 읽기 하나여야 한다.
    /// 서명 키가 같아 통과하더라도 Admin 을 실어 보내면 그 토큰이 새는 순간 운영이 열린다.
    /// </summary>
    [Fact]
    public async Task Sends_a_read_only_token_for_the_other_servers_audience()
    {
        await SeedAsync();
        var viewer = await _f.ViewerAsync();
        _f.Requests.Clear();

        await viewer.GetFromJsonAsync<ModelMonitorReportDto>("/api/monitoring/models", Json);

        var sent = _f.Requests.Should().NotBeEmpty().And.Subject.First();
        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
            .ReadJwtToken(sent.Headers.Authorization!.Parameter);

        jwt.Audiences.Should().ContainSingle().Which.Should().Be("BODA.VMS.Web.Client");
        jwt.Claims.Where(c => c.Type.EndsWith("/role") || c.Type == "role")
           .Select(c => c.Value).Should().BeEquivalentTo([MLOps.Server.Auth.Roles.Viewer]);
    }
}
