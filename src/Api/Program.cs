using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Api.BackgroundJobs;
using Api.Controllers;
using Api.Data;
using Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Database
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// Authentication
var jwtSecret = builder.Configuration["Jwt:Secret"];
if (string.IsNullOrWhiteSpace(jwtSecret))
    throw new InvalidOperationException("JWT Secret no configurado. Definir JWT_SECRET en el archivo .env (minimo 32 caracteres).");
if (jwtSecret.Length < 32)
    throw new InvalidOperationException("JWT Secret demasiado corto. Debe tener al menos 32 caracteres.");

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("ConnectionString no configurada. Revisar SQL_SA_PASSWORD en .env.");

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
        ClockSkew = TimeSpan.Zero
    };

    // Permitir leer el JWT desde la cookie httpOnly ademas del header Authorization.
    // Esto evita que el frontend tenga que guardar el token en localStorage (riesgo XSS).
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            if (string.IsNullOrEmpty(context.Token)
                && context.Request.Cookies.TryGetValue(AuthController.AccessTokenCookieName, out var cookieToken)
                && !string.IsNullOrEmpty(cookieToken))
            {
                context.Token = cookieToken;
            }
            return Task.CompletedTask;
        }
    };
});

builder.Services.AddAuthorization();

// Forwarded headers: detras de Caddy/Nginx la IP real viene en X-Forwarded-For.
// Sin esto, el rate limiter ve la IP del proxy (siempre la misma) y limita a todos juntos.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Confiar en cualquier proxy interno (estamos siempre detras de uno en Docker).
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// Rate limiting para /api/auth/login: 5 intentos por minuto por IP.
// Bloquea brute-force sin afectar uso normal.
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("login", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// Services
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<RoleService>();
builder.Services.AddScoped<IntegrationService>();
builder.Services.AddHttpClient();
builder.Services.AddScoped<MeliAccountService>();
builder.Services.AddScoped<MeliOrderService>();
builder.Services.AddScoped<MeliItemService>();
builder.Services.AddScoped<ContabiliumStagingService>();
builder.Services.AddScoped<CafeKitService>();
builder.Services.AddScoped<ContabiliumCotejoService>();
builder.Services.AddScoped<AuditLogService>();
builder.Services.AddScoped<ScheduledProcessService>();
builder.Services.AddScoped<ProductService>();
builder.Services.AddScoped<PricingService>();
builder.Services.AddScoped<SupplierService>();
builder.Services.AddScoped<BrandService>();
builder.Services.AddScoped<ClientService>();
builder.Services.AddScoped<CustomerTierService>();
builder.Services.AddScoped<ComboService>();
builder.Services.AddScoped<StockBatchService>();
builder.Services.AddScoped<StockMovementService>();
builder.Services.AddScoped<SaleService>();
builder.Services.AddScoped<TreasuryService>();
builder.Services.AddScoped<EmployeeService>();
builder.Services.AddScoped<PayrollService>();
builder.Services.AddScoped<FiscalLookupService>();
builder.Services.AddScoped<QuotesService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<BulkImportService>();
builder.Services.AddScoped<AiService>();
builder.Services.AddSingleton<SyncProgressService>();
builder.Services.AddSingleton<WhatsAppService>();
builder.Services.AddScoped<FileStorageService>();
builder.Services.AddScoped<BackupService>();
builder.Services.AddScoped<VaultService>();
builder.Services.AddScoped<AssistantService>();

// Permitir subidas grandes para Archivos (2 GB)
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 2L * 1024 * 1024 * 1024;
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartHeadersLengthLimit = int.MaxValue;
});

// Background Jobs
builder.Services.AddSingleton<IScheduledJob, SyncMeliOrdersJob>();
builder.Services.AddSingleton<IScheduledJob, SyncMeliItemsJob>();
builder.Services.AddSingleton<IScheduledJob, ProcessOrderStockJob>();
builder.Services.AddSingleton<IScheduledJob, BackupDatabaseJob>();
builder.Services.AddHostedService<ProcessSchedulerService>();

// CORS
// En produccion solo se aceptan los origenes definidos en CORS_ALLOWED_ORIGINS
// (lista separada por coma). En desarrollo, si la variable esta vacia, se cae a
// localhost para que la experiencia de dev no requiera configuracion extra.
// Importante: AllowCredentials() es necesario para que las cookies httpOnly
// (donde vive el JWT) se envien con cada request cross-origin.
var corsAllowedOrigins = (builder.Configuration["Cors:AllowedOrigins"]
                         ?? Environment.GetEnvironmentVariable("CORS_ALLOWED_ORIGINS")
                         ?? string.Empty)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

if (corsAllowedOrigins.Length == 0 && builder.Environment.IsDevelopment())
{
    corsAllowedOrigins = new[]
    {
        "http://localhost:3000",
        "http://127.0.0.1:3000"
    };
}

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (corsAllowedOrigins.Length > 0)
        {
            policy.WithOrigins(corsAllowedOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials();
        }
        // Si no hay origenes configurados (ej. produccion sin la env definida),
        // CORS queda cerrado a proposito: la app sigue funcionando para requests
        // del mismo origen (sirve mediante el reverse proxy).
    });
});

// Controllers + JSON
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Template API",
        Version = "v1"
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter your JWT token"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// Increase request body size for base64 photo uploads
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 2L * 1024 * 1024 * 1024);

var app = builder.Build();

// Seed del admin:
// - Si el hash es el placeholder del init.sql (primer arranque) o un hash invalido,
//   setear la clave desde DEFAULT_ADMIN_PASSWORD.
// - Si el admin ya tiene un hash BCrypt valido (o sea, la clave se cambio o ya se
//   seteo antes), NO se toca aunque DEFAULT_ADMIN_PASSWORD siga definida en .env.
// - Si se necesita forzar un reseteo (ej. olvido de clave), setear FORCE_RESET_ADMIN_PASSWORD=true.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var admin = await db.Users.FirstOrDefaultAsync(u => u.Username == "admin");
    if (admin is not null)
    {
        var defaultAdminPassword = Environment.GetEnvironmentVariable("DEFAULT_ADMIN_PASSWORD");
        var forceReset = string.Equals(Environment.GetEnvironmentVariable("FORCE_RESET_ADMIN_PASSWORD"), "true", StringComparison.OrdinalIgnoreCase);

        var hashIsInvalid = string.IsNullOrWhiteSpace(admin.PasswordHash)
                            || admin.PasswordHash == "placeholder"
                            || !admin.PasswordHash.StartsWith("$2", StringComparison.Ordinal);

        if (hashIsInvalid || forceReset)
        {
            if (string.IsNullOrWhiteSpace(defaultAdminPassword))
            {
                logger.LogWarning("El admin no tiene clave valida y DEFAULT_ADMIN_PASSWORD no esta definida. El login no va a funcionar hasta configurarla.");
            }
            else
            {
                admin.PasswordHash = BCrypt.Net.BCrypt.HashPassword(defaultAdminPassword);
                await db.SaveChangesAsync();
                logger.LogInformation("Clave del admin {Action} desde DEFAULT_ADMIN_PASSWORD.", forceReset ? "reseteada" : "inicializada");
            }
        }
    }
}

// Inicializar la bóveda de contraseñas con la maestra por defecto si todavía no existe.
// La password viene de VAULT_DEFAULT_PASSWORD (en .env). Una vez creada, podés vaciar esa variable —
// la bóveda solo la usa para el primer arranque.
{
    using var scope = app.Services.CreateScope();
    var vault = scope.ServiceProvider.GetRequiredService<VaultService>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var defaultVaultPwd = Environment.GetEnvironmentVariable("VAULT_DEFAULT_PASSWORD");
    if (!string.IsNullOrWhiteSpace(defaultVaultPwd))
    {
        try { await vault.InitializeIfMissingAsync(defaultVaultPwd); }
        catch (Exception ex) { logger.LogWarning(ex, "No se pudo inicializar la bóveda con la maestra por defecto."); }
    }
}

// Sync admin permissions with MenuDefinition
{
    using var scope = app.Services.CreateScope();
    var roleService = scope.ServiceProvider.GetRequiredService<RoleService>();
    await roleService.SyncAdminPermissionsAsync();
}

// Middleware pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// ForwardedHeaders debe ir antes que cualquier middleware que mire RemoteIpAddress
// (CORS, rate limit, auth, etc.).
app.UseForwardedHeaders();

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
