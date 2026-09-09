using System.Text;
using System.Text.Json.Serialization;
using BODA.VMS.MLOps.Server;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Data;
using BODA.VMS.MLOps.Server.Endpoints;
using BODA.VMS.MLOps.Server.Hubs;
using BODA.VMS.MLOps.Server.Services;
using BODA.VMS.MLOps.Server.Services.Sam;
using BODA.VMS.MLOps.Server.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Windows 서비스 지원 (콘솔 실행 시 영향 없음)
builder.Host.UseWindowsService();
builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "BODA.VMS.MLOps.Server")
    .WriteTo.Console());

// 대용량 업로드(ONNX 2GB·데이터셋 zip) — 상한은 파이프라인(TempFileWriter)이 검사한다
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = null);
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = long.MaxValue;
    o.ValueLengthLimit = int.MaxValue;
    o.MultipartHeadersLengthLimit = int.MaxValue;
});

builder.Services.Configure<MlopsOptions>(builder.Configuration.GetSection(MlopsOptions.Section));
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.Section));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
builder.Services.Configure<SamOptions>(builder.Configuration.GetSection(SamOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);

// 바인딩·본문 파싱 실패를 예외로 올려 ApiExceptionMiddleware 가 ApiError JSON 으로 변환하게 한다.
// 기본값은 개발 환경만 true 라, 이대로 두면 운영에서 본문 없는 400 이 나가 클라이언트가 원인을 알 수 없다.
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);

// JSON: camelCase + enum 문자열 (서버·워커·클라이언트 공통 규약)
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    // 한글을 이스케이프하지 않는다 — 응답이 읽을 수 있어야 하고, DB 에 저장된 값과도 어긋나지 않는다
    o.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

// SQLite (WAL) + EF Core
builder.Services.AddDbContext<MlopsDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=mlops.db"));

// 스토리지·서비스
builder.Services.AddSingleton<IArtifactStorage>(sp =>
    new LocalDiskArtifactStorage(sp.GetRequiredService<IOptions<MlopsOptions>>().Value.ResolvedStorageRoot()));
builder.Services.AddSingleton<ScriptManifestService>();
builder.Services.AddSingleton<IMlopsNotifier, SignalRNotifier>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<ModelRegistryService>();
builder.Services.AddScoped<BindingService>();
builder.Services.AddScoped<WorkerRegistryService>();
builder.Services.AddScoped<LineClientService>();
builder.Services.AddScoped<TrainingJobService>();
builder.Services.AddScoped<PretrainedMirrorService>();
builder.Services.AddScoped<DatasetVersionService>();
// 데이터 관리·라벨링 (개발 문서 §5.2·§5.4)
builder.Services.AddSingleton<ImageProcessor>();
builder.Services.AddScoped<ImagePoolService>();
builder.Services.AddScoped<DatasetService>();
builder.Services.AddScoped<DatasetQueryService>();
builder.Services.AddScoped<LabelingService>();
builder.Services.AddScoped<DatasetSnapshotService>();
// SAM 보조 (§5.4). 모델을 안 두면 스스로 꺼진 상태로 남는다 — 세션과 임베딩 캐시를 들고 있어 싱글턴이다.
builder.Services.AddSingleton<SamAssistService>();
builder.Services.AddHostedService<SamWarmupService>();
builder.Services.AddHostedService<JobSupervisor>();

builder.Services.AddSignalR().AddJsonProtocol(o =>
{
    o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
    o.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.PayloadSerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

// 인증: JWT(BODA.VMS.Web 와 같은 키) + 워커 토큰(wk_…) — Authorization 헤더 접두사로 자동 선택
var jwt = builder.Configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.Key) || jwt.Key.Length < 32)
    throw new InvalidOperationException(
        "Jwt:Key 가 설정되지 않았거나 32자 미만입니다. 개발: dotnet user-secrets set \"Jwt:Key\" \"<32자 이상>\" · 운영: 환경변수 Jwt__Key. " +
        "BODA.VMS.Web 와 같은 값을 쓰면 기존 로그인 토큰을 그대로 받습니다.");

builder.Services.AddAuthentication(SmartAuthScheme.Name)
    .AddPolicyScheme(SmartAuthScheme.Name, "JWT or WorkerToken", o => o.ForwardDefaultSelector = SmartAuthScheme.Select)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ClockSkew = TimeSpan.FromMinutes(1),
        };
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                // SignalR 은 헤더를 붙일 수 없어 쿼리스트링으로 토큰을 받는다
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    ctx.Token = token;
                    return Task.CompletedTask;
                }
                // <img src> 도 헤더를 붙일 수 없다. 이미지 경로에서만 쿠키를 받아 준다.
                if (ctx.HttpContext.Request.Path.StartsWithSegments(ImageCookie.Path)
                    && string.IsNullOrEmpty(ctx.Request.Headers.Authorization)
                    && ctx.Request.Cookies.TryGetValue(ImageCookie.Name, out var cookie))
                    ctx.Token = cookie;
                return Task.CompletedTask;
            }
        };
    })
    .AddScheme<AuthenticationSchemeOptions, WorkerTokenAuthenticationHandler>(WorkerTokenAuthenticationHandler.SchemeName, null)
    .AddScheme<AuthenticationSchemeOptions, LineTokenAuthenticationHandler>(LineTokenAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization(o => o.AddMlopsPolicies());

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new() { Title = "BODA VMS MLOps API", Version = "v1" });
    o.AddSecurityDefinition("Bearer", new()
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http, Scheme = "bearer",
        Description = "사용자 JWT 또는 워커 토큰(wk_…)",
    });
    o.AddSecurityRequirement(new()
    {
        { new() { Reference = new() { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer" } }, Array.Empty<string>() }
    });
});

var app = builder.Build();

// DB·스토리지 초기화. 스키마는 마이그레이션으로만 바꾼다 (EnsureCreated 는 이후 변경을 반영하지 못한다).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MlopsDbContext>();
    var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
    if (pending.Count > 0)
    {
        app.Logger.LogInformation("마이그레이션 {Count}건 적용: {Names}", pending.Count, string.Join(", ", pending));
        await db.Database.MigrateAsync();
    }
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;");
    _ = scope.ServiceProvider.GetRequiredService<IArtifactStorage>();
    var scripts = scope.ServiceProvider.GetRequiredService<ScriptManifestService>();
    var (manifest, _) = scripts.GetManifest();
    app.Logger.LogInformation("스토리지 {Root} · 스크립트 {Count}개 ({ScriptsRoot})",
        scope.ServiceProvider.GetRequiredService<IOptions<MlopsOptions>>().Value.ResolvedStorageRoot(), manifest.Count, scripts.Root);

}

app.UseMiddleware<ApiExceptionMiddleware>();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseWebAssemblyDebugging();
}

// 관리 화면 (Blazor WASM). 인증은 API 가 하고, 정적 파일 자체는 익명으로 내보낸다.
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api");
api.MapAuthEndpoints();
api.MapModelEndpoints();
api.MapBindingEndpoints();
api.MapWorkerEndpoints();
api.MapTrainingJobEndpoints();
api.MapPretrainedEndpoints();
api.MapDatasetEndpoints();
api.MapImageEndpoints();
api.MapLineEndpoints();
api.MapSamEndpoints();

app.MapHub<ModelsHub>("/hubs/models");
app.MapHub<TrainingHub>("/hubs/training");
app.MapGet("/health", () => Results.Ok(new { status = "ok", version = typeof(ServerEntryPoint).Assembly.GetName().Version?.ToString() })).AllowAnonymous();

// 클라이언트 라우팅(/models/{id} 등)은 index.html 로 넘긴다. /api 와 /hubs 는 위에서 이미 처리됐다.
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>
/// WebApplicationFactory 진입점 표식. 워커 프로젝트도 최상위 문(암시적 Program)을 쓰므로,
/// 테스트가 두 어셈블리를 모두 참조할 때 이름이 충돌하지 않도록 별도 형식을 노출한다.
/// </summary>
public sealed class ServerEntryPoint;
