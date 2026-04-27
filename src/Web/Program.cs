using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.Components.Authorization;
using Web;
using Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// HttpClient - uses relative URLs, Nginx proxies /api/ to the backend
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Auth services
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<AuthenticationStateProvider, JwtAuthStateProvider>();
builder.Services.AddAuthorizationCore();

// App services
builder.Services.AddScoped<ApiClient>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<SyncProgressTracker>();
builder.Services.AddScoped<BrandSettingsService>();
builder.Services.AddSingleton<UploadProgressService>();

await builder.Build().RunAsync();
