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
// 2026-08-06: presencia en vivo de la bandeja de WhatsApp (SignalR)
builder.Services.AddScoped<Web.Services.PresenceService>();
// 2026-08-15: tema (claro/oscuro) por línea de WhatsApp + letra elegida para la pantalla
builder.Services.AddScoped<Web.Services.WaAparienciaService>();
builder.Services.AddScoped<SyncProgressTracker>();
builder.Services.AddScoped<BrandSettingsService>();
builder.Services.AddScoped<OperatorService>();
builder.Services.AddScoped<NuevaVentaSignal>();
builder.Services.AddScoped<CurrentCompanyService>();
builder.Services.AddSingleton<UploadProgressService>();
builder.Services.AddScoped<CpService>();

// ── Componentes móviles (rediseño 2026-05-26): índice de catálogo en memoria + prefs ──
builder.Services.AddSingleton<Web.Services.Mobile.KeyboardPrefs>();
builder.Services.AddSingleton<Web.Services.Mobile.CatalogIndex>();
// 2026-05-28: cache de visibilidad del sidebar por rol (admin edita desde el sidebar)
builder.Services.AddSingleton<Web.Services.MenuVisibilityService>();

await builder.Build().RunAsync();
