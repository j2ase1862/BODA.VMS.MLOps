using System.Net;
using System.Net.Http.Json;
using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Core.Monitoring;
using FluentAssertions;
using static BODA.VMS.MLOps.Tests.Server.MlopsApiFactory;
using VMS.Core.Services;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// 모니터링 화면이 쓰는 곳.
///
/// <para>
/// 시험 서버에는 운영 웹 주소가 없다. 그때 <b>조용히 빈 화면을 주면 안 된다</b> —
/// 사람이 "모델이 다 괜찮구나" 로 읽는다. 왜 볼 수 없는지 말해야 한다.
/// </para>
/// </summary>
public class MonitoringApiTests : IClassFixture<MlopsApiFactory>
{
    private readonly MlopsApiFactory _f;
    public MonitoringApiTests(MlopsApiFactory f) => _f = f;

    [Fact]
    public async Task Says_why_it_cannot_look_when_production_web_is_not_configured()
    {
        var viewer = await _f.ViewerAsync();

        var report = await viewer.GetFromJsonAsync<ModelMonitorReportDto>("/api/monitoring/models", Json);

        report.Should().NotBeNull();
        report!.Available.Should().BeFalse();
        report.Message.Should().Contain("운영 웹 주소");
        report.Models.Should().BeEmpty();
    }

    [Fact]
    public async Task Requires_a_signed_in_user()
    {
        using var anonymous = _f.CreateClient();

        var res = await anonymous.GetAsync("/api/monitoring/models");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

/// <summary>
/// 모니터링이 기대는 모델 식별자 규약 — VMS 런타임이 싣는 <c>mv:{32자}</c> 를 MLOps 가 그대로 읽는지.
///
/// <para>
/// 규약은 <see cref="DlModelIdentity"/>(VMS.Core.Contracts) 한 곳에 있다. 예전에는 여기에 같은 규칙이 한 벌 더 있었다
/// (<c>ModelVersionTag</c>, VMS #572 로 정리). 그래도 이 시험은 남긴다 — 패키지를 올렸는데 형식이 바뀌어 있으면
/// 집계가 <b>조용히 빈다</b>. 오류도 안 나고 모델별 줄이 그냥 안 나타난다. VMS 쪽 <c>Wire_format_is_pinned</c> 와 같은 값을
/// 쓰는 쪽에서도 글자 그대로 못 박는다.
/// </para>
/// </summary>
public class DlModelIdentityContractTests
{
    /// <summary>VMS 가 실제로 만드는 문자열 모양. 이 값이 바뀌면 운영 웹에 쌓인 이력과 이어지지 않는다.</summary>
    [Fact]
    public void Format_is_pinned()
    {
        var id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

        DlModelIdentity.ForVersion(id).Should().Be("mv:0f8fad5bd9cb469fa16570867728950e");
        DlModelIdentity.VersionPrefix.Should().Be("mv:");
    }

    /// <summary>VMS 쪽 이력 열(DlModelVersion)이 50자라, 형식이 길어지면 라인의 업로드가 400 으로 막힌다</summary>
    [Fact]
    public void Fits_the_history_column()
    {
        DlModelIdentity.MaxLength.Should().Be(50);
        DlModelIdentity.ForVersion(Guid.NewGuid()).Length.Should().BeLessThanOrEqualTo(DlModelIdentity.MaxLength);
    }

    [Fact]
    public void Round_trips()
    {
        var id = Guid.NewGuid();

        DlModelIdentity.TryReadVersionId(DlModelIdentity.ForVersion(id)).Should().Be(id);
    }

    /// <summary>옛 VMS 가 싣던 형식과 빈 값은 이어지지 않는다 — 버리지 말고 세어야 한다.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("DetectionTool:best.onnx")]
    [InlineData("mv:이건GUID가아니다")]
    [InlineData("MV:0f8fad5bd9cb469fa16570867728950e")]   // 접두는 대소문자를 가린다
    public void Unknown_formats_read_as_null(string? identity)
        => DlModelIdentity.TryReadVersionId(identity).Should().BeNull();

    /// <summary>앞뒤 공백은 흔한 실수라 받아 준다.</summary>
    [Fact]
    public void Surrounding_whitespace_is_tolerated()
    {
        var id = Guid.NewGuid();

        DlModelIdentity.TryReadVersionId("  " + DlModelIdentity.ForVersion(id) + "  ").Should().Be(id);
    }
}
