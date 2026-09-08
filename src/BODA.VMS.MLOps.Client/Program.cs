using System.Text.Json.Serialization;
using Blazored.LocalStorage;
using BODA.VMS.MLOps.Client;
using BODA.VMS.MLOps.Client.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddBlazoredLocalStorage();
builder.Services.AddMudServices(o =>
{
    o.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
    o.SnackbarConfiguration.VisibleStateDuration = 5000;
    o.SnackbarConfiguration.PreventDuplicates = false;
});

builder.Services.AddScoped<TokenStore>();
builder.Services.AddScoped<JwtAuthenticationStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<JwtAuthenticationStateProvider>());
builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<AuthTokenHandler>();

// API 는 앱과 같은 오리진 (서버가 이 WASM 을 호스팅한다)
builder.Services.AddHttpClient<MlopsApi>(c =>
{
    c.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress);
    c.Timeout = TimeSpan.FromMinutes(30); // 대용량 ONNX·데이터셋 업로드
}).AddHttpMessageHandler<AuthTokenHandler>();

builder.Services.AddScoped<TrainingHubClient>();

await builder.Build().RunAsync();
