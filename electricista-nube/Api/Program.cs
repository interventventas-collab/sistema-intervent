using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// ============================================================
//  Sistema C.S.E (electricista) — backend chico y autonomo
//  .NET 8 minimal API + SQLite. Base de datos propia, separada
//  del sistema INTER VENT. Todo se sirve bajo /electricista-nube.
// ============================================================

var builder = WebApplication.CreateBuilder(args);
var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? "/data/electricista.db";
builder.Services.AddDbContext<Db>(o => o.UseSqlite($"Data Source={dbPath}"));
var app = builder.Build();

// --- Crear/semilla de la base al arrancar ---
string SECRET;
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<Db>();
    db.Database.EnsureCreated();
    if (!db.Usuarios.Any())
    {
        var (h, s) = Auth.HashPw("1234");
        db.Usuarios.Add(new Usuario { User = "admin", PassHash = h, Salt = s });
        db.SaveChanges();
    }
    var cfg = db.Configs.FirstOrDefault(c => c.Clave == "secret");
    if (cfg == null)
    {
        cfg = new Config { Clave = "secret", Valor = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
        db.Configs.Add(cfg); db.SaveChanges();
    }
    SECRET = cfg.Valor;
}

app.UseDefaultFiles();
app.UseStaticFiles();

var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

// --- helpers de sesion (cookie httpOnly firmada) ---
CookieOptions cookieOpts(int dias) => new()
{
    HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax,
    Path = "/electricista-nube", MaxAge = TimeSpan.FromDays(dias)
};
int? UidDe(HttpContext ctx) => Auth.LeerToken(ctx.Request.Cookies["electri_auth"], SECRET);
bool NoAuth(HttpContext ctx) => UidDe(ctx) == null;

// ==================== LOGIN ====================
app.MapPost("/api/login", async (HttpContext ctx, Db db) =>
{
    var body = await JsonSerializer.DeserializeAsync<LoginReq>(ctx.Request.Body, jsonOpts);
    if (body == null) return Results.BadRequest();
    var u = db.Usuarios.FirstOrDefault(x => x.User == (body.User ?? "").Trim());
    if (u == null || !Auth.VerifyPw(body.Pass ?? "", u.PassHash, u.Salt))
        return Results.Json(new { error = "Usuario o contraseña incorrectos" }, statusCode: 401);
    ctx.Response.Cookies.Append("electri_auth", Auth.HacerToken(u.Id, SECRET), cookieOpts(30));
    return Results.Ok(new { user = u.User });
});

app.MapPost("/api/logout", (HttpContext ctx) =>
{
    ctx.Response.Cookies.Delete("electri_auth", new CookieOptions { Path = "/electricista-nube" });
    return Results.Ok();
});

app.MapGet("/api/me", (HttpContext ctx, Db db) =>
{
    var uid = UidDe(ctx); if (uid == null) return Results.Unauthorized();
    var u = db.Usuarios.Find(uid.Value);
    return u == null ? Results.Unauthorized() : Results.Ok(new { user = u.User });
});

// ==================== CLIENTES ====================
app.MapGet("/api/clientes", (HttpContext ctx, Db db) =>
    NoAuth(ctx) ? Results.Unauthorized() : Results.Ok(db.Clientes.OrderBy(c => c.Nombre).ToList()));
app.MapPost("/api/clientes", async (HttpContext ctx, Db db) =>
{
    if (NoAuth(ctx)) return Results.Unauthorized();
    var c = await JsonSerializer.DeserializeAsync<Cliente>(ctx.Request.Body, jsonOpts); if (c == null) return Results.BadRequest();
    c.Id = 0; db.Clientes.Add(c); db.SaveChanges(); return Results.Ok(c);
});
app.MapPut("/api/clientes/{id}", async (int id, HttpContext ctx, Db db) =>
{
    if (NoAuth(ctx)) return Results.Unauthorized();
    var e = db.Clientes.Find(id); if (e == null) return Results.NotFound();
    var c = await JsonSerializer.DeserializeAsync<Cliente>(ctx.Request.Body, jsonOpts); if (c == null) return Results.BadRequest();
    e.Nombre = c.Nombre; e.Direccion = c.Direccion; e.Telefono = c.Telefono; e.Email = c.Email; e.Notas = c.Notas;
    db.SaveChanges(); return Results.Ok(e);
});
app.MapDelete("/api/clientes/{id}", (int id, HttpContext ctx, Db db) =>
{
    if (NoAuth(ctx)) return Results.Unauthorized();
    var e = db.Clientes.Find(id); if (e != null) { db.Clientes.Remove(e); db.SaveChanges(); } return Results.Ok();
});

// ==================== SERVICIOS ====================
app.MapGet("/api/servicios", (HttpContext ctx, Db db) =>
    NoAuth(ctx) ? Results.Unauthorized() : Results.Ok(db.Servicios.OrderBy(s => s.Nombre).ToList()));
app.MapPost("/api/servicios", async (HttpContext ctx, Db db) =>
{
    if (NoAuth(ctx)) return Results.Unauthorized();
    var s = await JsonSerializer.DeserializeAsync<Servicio>(ctx.Request.Body, jsonOpts); if (s == null) return Results.BadRequest();
    s.Id = 0; db.Servicios.Add(s); db.SaveChanges(); return Results.Ok(s);
});
app.MapPut("/api/servicios/{id}", async (int id, HttpContext ctx, Db db) =>
{
    if (NoAuth(ctx)) return Results.Unauthorized();
    var e = db.Servicios.Find(id); if (e == null) return Results.NotFound();
    var s = await JsonSerializer.DeserializeAsync<Servicio>(ctx.Request.Body, jsonOpts); if (s == null) return Results.BadRequest();
    e.Nombre = s.Nombre; e.Precio = s.Precio; e.Descripcion = s.Descripcion; db.SaveChanges(); return Results.Ok(e);
});
app.MapDelete("/api/servicios/{id}", (int id, HttpContext ctx, Db db) =>
{
    if (NoAuth(ctx)) return Results.Unauthorized();
    var e = db.Servicios.Find(id); if (e != null) { db.Servicios.Remove(e); db.SaveChanges(); } return Results.Ok();
});

// ==================== PRODUCTOS ====================
app.MapGet("/api/productos", (HttpContext ctx, Db db) =>
    NoAuth(ctx) ? Results.Unauthorized() : Results.Ok(db.Productos.OrderBy(p => p.Nombre).ToList()));
app.MapPost("/api/productos", async (HttpContext ctx, Db db) =>
{
    if (NoAuth(ctx)) return Results.Unauthorized();
    var p = await JsonSerializer.DeserializeAsync<Producto>(ctx.Request.Body, jsonOpts); if (p == null) return Results.BadRequest();
    p.Id = 0; db.Productos.Add(p); db.SaveChanges(); return Results.Ok(p);
});
app.MapPut("/api/productos/{id}", async (int id, HttpContext ctx, Db db) =>
{
    if (NoAuth(ctx)) return Results.Unauthorized();
    var e = db.Productos.Find(id); if (e == null) return Results.NotFound();
    var p = await JsonSerializer.DeserializeAsync<Producto>(ctx.Request.Body, jsonOpts); if (p == null) return Results.BadRequest();
    e.Nombre = p.Nombre; e.Codigo = p.Codigo; e.Precio = p.Precio; e.Stock = p.Stock; e.StockMin = p.StockMin;
    db.SaveChanges(); return Results.Ok(e);
});
app.MapDelete("/api/productos/{id}", (int id, HttpContext ctx, Db db) =>
{
    if (NoAuth(ctx)) return Results.Unauthorized();
    var e = db.Productos.Find(id); if (e != null) { db.Productos.Remove(e); db.SaveChanges(); } return Results.Ok();
});

// ==================== PRESUPUESTOS ====================
app.MapGet("/api/presupuestos", (HttpContext ctx, Db db) =>
{
    if (NoAuth(ctx)) return Results.Unauthorized();
    var list = db.Presupuestos.OrderBy(p => p.Numero).ToList()
        .Select(p => PresupuestoDto.De(p)).ToList();
    return Results.Ok(list);
});
app.MapPost("/api/presupuestos", async (HttpContext ctx, Db db) =>
{
    if (NoAuth(ctx)) return Results.Unauthorized();
    var el = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
    var p = new Presupuesto(); PresupuestoDto.Aplicar(el, p);
    db.Presupuestos.Add(p); db.SaveChanges(); return Results.Ok(PresupuestoDto.De(p));
});
app.MapPut("/api/presupuestos/{id}", async (int id, HttpContext ctx, Db db) =>
{
    if (NoAuth(ctx)) return Results.Unauthorized();
    var p = db.Presupuestos.Find(id); if (p == null) return Results.NotFound();
    var el = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
    PresupuestoDto.Aplicar(el, p); db.SaveChanges(); return Results.Ok(PresupuestoDto.De(p));
});
app.MapPut("/api/presupuestos/{id}/estado", async (int id, HttpContext ctx, Db db) =>
{
    if (NoAuth(ctx)) return Results.Unauthorized();
    var p = db.Presupuestos.Find(id); if (p == null) return Results.NotFound();
    var el = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
    if (el.TryGetProperty("estado", out var es)) p.Estado = es.GetString() ?? p.Estado;
    db.SaveChanges(); return Results.Ok();
});
app.MapDelete("/api/presupuestos/{id}", (int id, HttpContext ctx, Db db) =>
{
    if (NoAuth(ctx)) return Results.Unauthorized();
    var e = db.Presupuestos.Find(id); if (e != null) { db.Presupuestos.Remove(e); db.SaveChanges(); } return Results.Ok();
});

// ==================== CONFIG (marca / numeracion) ====================
app.MapGet("/api/config", (HttpContext ctx, Db db) =>
{
    if (NoAuth(ctx)) return Results.Unauthorized();
    var d = db.Configs.ToDictionary(c => c.Clave, c => c.Valor);
    return Results.Ok(new
    {
        instagram = d.GetValueOrDefault("instagram", "Caldeira.servi.electrico"),
        whatsapp = d.GetValueOrDefault("whatsapp", "11 2505 2932"),
        mail = d.GetValueOrDefault("mail", "caldeira.servicioelectrico@gmail.com"),
        zona = d.GetValueOrDefault("zona", "P.B.A, Esteban Echeverría"),
        logo = d.GetValueOrDefault("logo", ""),
        proximoNumero = int.TryParse(d.GetValueOrDefault("proximoNumero", "1"), out var n) ? n : 1
    });
});
app.MapPut("/api/config", async (HttpContext ctx, Db db) =>
{
    if (NoAuth(ctx)) return Results.Unauthorized();
    var el = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
    void Set(string k, string v) { var c = db.Configs.FirstOrDefault(x => x.Clave == k); if (c == null) { c = new Config { Clave = k, Valor = v }; db.Configs.Add(c); } else c.Valor = v; }
    foreach (var k in new[] { "instagram", "whatsapp", "mail", "zona", "logo", "proximoNumero" })
        if (el.TryGetProperty(k, out var v)) Set(k, v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString());
    db.SaveChanges(); return Results.Ok();
});

// ==================== USUARIO (cambiar clave) ====================
app.MapPut("/api/usuario", async (HttpContext ctx, Db db) =>
{
    var uid = UidDe(ctx); if (uid == null) return Results.Unauthorized();
    var el = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
    var u = db.Usuarios.Find(uid.Value); if (u == null) return Results.Unauthorized();
    if (el.TryGetProperty("user", out var us) && !string.IsNullOrWhiteSpace(us.GetString())) u.User = us.GetString()!.Trim();
    if (el.TryGetProperty("pass", out var ps) && !string.IsNullOrWhiteSpace(ps.GetString()))
    { var (h, s) = Auth.HashPw(ps.GetString()!); u.PassHash = h; u.Salt = s; }
    db.SaveChanges(); return Results.Ok(new { user = u.User });
});

// ==================== ESTADISTICAS (tablero) ====================
app.MapGet("/api/stats", (HttpContext ctx, Db db) =>
{
    if (NoAuth(ctx)) return Results.Unauthorized();
    var ps = db.Presupuestos.ToList();
    double Total(Presupuesto p)
    {
        double mo = 0;
        try { foreach (var it in JsonSerializer.Deserialize<List<ItemDto>>(p.ItemsJson, jsonOpts) ?? new()) mo += it.Cant * it.Unit; } catch { }
        return mo + p.Materiales;
    }
    return Results.Ok(new
    {
        clientes = db.Clientes.Count(),
        servicios = db.Servicios.Count(),
        productos = db.Productos.Count(),
        presupuestos = ps.Count,
        aprobados = ps.Count(p => p.Estado == "aprobado"),
        pendientes = ps.Count(p => p.Estado == "pendiente"),
        rechazados = ps.Count(p => p.Estado == "rechazado"),
        facturado = ps.Where(p => p.Estado == "aprobado").Sum(Total)
    });
});

app.Run();


// ============================================================
//  MODELOS Y BASE DE DATOS
// ============================================================
class Db : DbContext
{
    public Db(DbContextOptions<Db> o) : base(o) { }
    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<Cliente> Clientes => Set<Cliente>();
    public DbSet<Servicio> Servicios => Set<Servicio>();
    public DbSet<Producto> Productos => Set<Producto>();
    public DbSet<Presupuesto> Presupuestos => Set<Presupuesto>();
    public DbSet<Config> Configs => Set<Config>();
}
class Usuario { public int Id { get; set; } public string User { get; set; } = ""; public string PassHash { get; set; } = ""; public string Salt { get; set; } = ""; }
class Cliente { public int Id { get; set; } public string Nombre { get; set; } = ""; public string? Direccion { get; set; } public string? Telefono { get; set; } public string? Email { get; set; } public string? Notas { get; set; } }
class Servicio { public int Id { get; set; } public string Nombre { get; set; } = ""; public double Precio { get; set; } public string? Descripcion { get; set; } }
class Producto { public int Id { get; set; } public string Nombre { get; set; } = ""; public string? Codigo { get; set; } public double Precio { get; set; } public double Stock { get; set; } public double StockMin { get; set; } }
class Presupuesto
{
    public int Id { get; set; }
    public int Numero { get; set; }
    public string Fecha { get; set; } = "";
    public int ClienteId { get; set; }
    public string Titulo { get; set; } = "";
    public double Materiales { get; set; }
    public bool MostrarIVA { get; set; } = true;
    public string? Cobros { get; set; }
    public string? Ejecucion { get; set; }
    public string? Adicionales { get; set; }
    public string Garantia { get; set; } = "5";
    public string Estado { get; set; } = "pendiente";
    public string ItemsJson { get; set; } = "[]";
}
class Config { public int Id { get; set; } public string Clave { get; set; } = ""; public string Valor { get; set; } = ""; }

class LoginReq { public string? User { get; set; } public string? Pass { get; set; } }
class ItemDto { public double Cant { get; set; } public string? Desc { get; set; } public double Unit { get; set; } public string? Detalle { get; set; } }

// DTO de presupuesto: convierte entre la fila (con ItemsJson) y el objeto que usa el navegador (con items[])
static class PresupuestoDto
{
    static readonly JsonSerializerOptions O = new() { PropertyNameCaseInsensitive = true };
    public static object De(Presupuesto p)
    {
        JsonElement items;
        try { items = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(p.ItemsJson) ? "[]" : p.ItemsJson); }
        catch { items = JsonSerializer.Deserialize<JsonElement>("[]"); }
        return new { id = p.Id, numero = p.Numero, fecha = p.Fecha, clienteId = p.ClienteId, titulo = p.Titulo, materiales = p.Materiales, mostrarIVA = p.MostrarIVA, cobros = p.Cobros, ejecucion = p.Ejecucion, adicionales = p.Adicionales, garantia = p.Garantia, estado = p.Estado, items };
    }
    public static void Aplicar(JsonElement el, Presupuesto p)
    {
        if (el.TryGetProperty("numero", out var v) && v.TryGetInt32(out var n)) p.Numero = n;
        if (el.TryGetProperty("fecha", out v)) p.Fecha = v.GetString() ?? p.Fecha;
        if (el.TryGetProperty("clienteId", out v) && v.TryGetInt32(out var ci)) p.ClienteId = ci;
        if (el.TryGetProperty("titulo", out v)) p.Titulo = v.GetString() ?? "";
        if (el.TryGetProperty("materiales", out v) && v.TryGetDouble(out var m)) p.Materiales = m;
        if (el.TryGetProperty("mostrarIVA", out v)) p.MostrarIVA = v.ValueKind == JsonValueKind.True;
        if (el.TryGetProperty("cobros", out v)) p.Cobros = v.GetString();
        if (el.TryGetProperty("ejecucion", out v)) p.Ejecucion = v.GetString();
        if (el.TryGetProperty("adicionales", out v)) p.Adicionales = v.GetString();
        if (el.TryGetProperty("garantia", out v)) p.Garantia = v.GetString() ?? "5";
        if (el.TryGetProperty("estado", out v)) p.Estado = v.GetString() ?? "pendiente";
        if (el.TryGetProperty("items", out v)) p.ItemsJson = v.GetRawText();
    }
}

// ============================================================
//  AUTENTICACION (hash de clave + token firmado)
// ============================================================
static class Auth
{
    public static (string hash, string salt) HashPw(string pw)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pw, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }
    public static bool VerifyPw(string pw, string hash, string salt)
    {
        try
        {
            var h = Rfc2898DeriveBytes.Pbkdf2(pw, Convert.FromBase64String(salt), 100_000, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(h, Convert.FromBase64String(hash));
        }
        catch { return false; }
    }
    static string Firma(string data, string secret)
    {
        using var h = new HMACSHA256(Convert.FromBase64String(secret));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(data)));
    }
    public static string HacerToken(int uid, string secret)
    {
        long exp = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        string payload = $"{uid}.{exp}";
        return $"{payload}.{Firma(payload, secret)}";
    }
    public static int? LeerToken(string? token, string secret)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        var payload = $"{parts[0]}.{parts[1]}";
        var esperado = Firma(payload, secret);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(esperado), Encoding.UTF8.GetBytes(parts[2]))) return null;
        if (!long.TryParse(parts[1], out var exp) || exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
        return int.TryParse(parts[0], out var uid) ? uid : null;
    }
}
