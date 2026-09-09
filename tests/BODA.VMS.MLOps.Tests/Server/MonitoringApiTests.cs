using System.Net;
using System.Net.Http.Json;
using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Core.Monitoring;
using FluentAssertions;
using static BODA.VMS.MLOps.Tests.Server.MlopsApiFactory;

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
/// VMS 가 싣는 식별자와 MLOps 가 읽는 식별자가 같은 규약인지.
///
/// <para>
/// 두 리포에 같은 규칙이 두 벌 있다 (VMS 의 <c>DlModelIdentity</c>, 여기의
/// <see cref="ModelVersionTag"/>). 한쪽만 바뀌면 집계가 <b>조용히 빈다</b> — 오류도 안 나고
/// 모델별 줄이 그냥 안 나타난다. 그래서 형식을 글자 그대로 못 박는다.
/// </para>
/// </summary>
public class ModelVersionTagTests
{
    /// <summary>VMS 가 실제로 만드는 문자열 모양. 이 값이 바뀌면 두 리포를 함께 고쳐야 한다.</summary>
    [Fact]
    public void Format_matches_what_vms_writes()
    {
        var id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

        ModelVersionTag.Write(id).Should().Be("mv:0f8fad5bd9cb469fa16570867728950e");
    }

    [Fact]
    public void Round_trips()
    {
        var id = Guid.NewGuid();

        ModelVersionTag.TryRead(ModelVersionTag.Write(id)).Should().Be(id);
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
        => ModelVersionTag.TryRead(identity).Should().BeNull();

    /// <summary>앞뒤 공백은 흔한 실수라 받아 준다.</summary>
    [Fact]
    public void Surrounding_whitespace_is_tolerated()
    {
        var id = Guid.NewGuid();

        ModelVersionTag.TryRead("  " + ModelVersionTag.Write(id) + "  ").Should().Be(id);
    }
}
