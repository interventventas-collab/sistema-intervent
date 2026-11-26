using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Telefono dentro del sistema (central telefonica Asterisk, carpeta telefonia/).
/// 2026-11-26: un interno por persona. Cada compu se conecta con el interno de quien
/// esta usandola:
///   - usuario DEPOSITO            -> 210 (suenan todas las compus del deposito;
///                                    211-215 = "busca a ALEXIS/WALTER/..." en esas compus)
///   - operador OSMAR/GERMAN/GABRIEL (PIN) -> 201/202/203
///   - cualquier otro (ej. contadora) -> sin telefono
///   - repartidores desde el celu (link con token) -> 220, llaman a Oficina (200), Deposito (210) u Osmar (201)
/// 2026-11-26 (3a vuelta): registro de llamadas -> llamadas perdidas; mensajes programados.
/// </summary>
[ApiController]
[Route("api/telefono")]
[Authorize]
public class TelefonoController : ControllerBase
{
    public record InternoDto(string Nombre, string Numero, string Grupo);
    public record TelefonoConfigDto(bool Habilitado, string WsUrl, string Dominio, string Usuario, string Clave, string MiNombre, List<InternoDto> Internos);
    public record EstadoDto(List<string> Conectados);
    public record PerdidaDto(int Id, string De, string Nombre, string? Busca, DateTime FechaAr);
    public record MensajeDto(int Id, string Texto, string Destino, string Hora, string Dias, DateTime? Fecha, bool Activo, DateTime? UltimoEnvioAr, DateTime? UltimoEscuchadoAr);
    public record GuardarMensajeReq(string Texto, string Destino, string Hora, string Dias, DateTime? Fecha, bool Activo);

    // Orden fijo: asi aparece en la lista del telefono.
    public static readonly List<InternoDto> Guia = new()
    {
        new("Oficina (todos)", "200", "Oficina"),
        new("Teléfono fijo", "101", "Oficina"),
        new("Osmar", "201", "Oficina"),
        new("Germán", "202", "Oficina"),
        new("Gabriel", "203", "Oficina"),
        new("Depósito", "210", "Depósito"),
        new("Alexis", "211", "Depósito"),
        new("Walter", "212", "Depósito"),
        new("Rodrigo", "213", "Depósito"),
        new("Benjamín", "214", "Depósito"),
        new("Gonzalo", "215", "Depósito"),
    };

    private static readonly Dictionary<string, string> InternoDeOperador = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OSMAR"] = "201", ["GERMAN"] = "202", ["GABRIEL"] = "203",
    };

    private readonly AppDbContext _db;
    private readonly TelefonoCentralService _central;
    public TelefonoController(AppDbContext db, TelefonoCentralService central) { _db = db; _central = central; }

    private static string WsUrl => Environment.GetEnvironmentVariable("TELEFONO_WS_URL") ?? "";
    private const string Dominio = "179.43.123.134";

    private string? MiInterno()
    {
        var usuario = (User.Identity?.Name ?? "").Trim();
        if (usuario.Equals("DEPOSITO", StringComparison.OrdinalIgnoreCase)) return "210";
        var operador = Request.Headers["X-Operator-Name"].ToString().Trim();
        return InternoDeOperador.TryGetValue(operador, out var n) ? n : null;
    }

    private static string NombreDe(string num, string? nombre)
    {
        var g = Guia.FirstOrDefault(x => x.Numero == num);
        if (g != null) return g.Nombre;
        if (num == "300") return "Mensaje programado";
        return string.IsNullOrWhiteSpace(nombre) ? num : nombre;
    }

    [HttpGet("config")]
    public ActionResult<TelefonoConfigDto> GetConfig()
    {
        var mio = MiInterno();
        if (mio == null || string.IsNullOrWhiteSpace(TelefonoCentralService.Secreto) || string.IsNullOrWhiteSpace(WsUrl))
            return new TelefonoConfigDto(false, "", "", "", "", "", Guia);
        return new TelefonoConfigDto(true, WsUrl, Dominio, mio, TelefonoCentralService.Clave(mio), NombreDe(mio, null), Guia);
    }

    // Quien esta conectado ahora (para el puntito verde).
    [HttpGet("estado")]
    public async Task<ActionResult<EstadoDto>> GetEstado()
    {
        var online = new List<string>(await _central.ConectadosAsync());
        // 211-215 suenan en las compus del deposito: estan "conectados" si el 210 lo esta
        if (online.Contains("210")) online.AddRange(new[] { "211", "212", "213", "214", "215" });
        // 200 = oficina (todos): conectado si hay alguien de la oficina
        if (online.Any(x => x is "101" or "201" or "202" or "203")) online.Add("200");
        return new EstadoDto(online);
    }

    // ---------------- registro de llamadas (lo llama la central al cortar) ----------------

    private bool EsLaCentral() =>
        !string.IsNullOrEmpty(TelefonoCentralService.Secreto) &&
        Request.Headers["X-Central-Key"].ToString() == TelefonoCentralService.Clave("central");

    [HttpPost("registro")]
    [AllowAnonymous]
    public async Task<IActionResult> Registro([FromForm] string? de, [FromForm] string? nombre, [FromForm] string? a,
        [FromForm] string? busca, [FromForm] string? estado, [FromForm] string? segundos)
    {
        if (!EsLaCentral()) return Unauthorized();
        if (string.IsNullOrWhiteSpace(a) || a == "600") return Ok();
        _db.TelefonoLlamadas.Add(new TelefonoLlamada
        {
            De = (de ?? "").Trim()[..Math.Min((de ?? "").Trim().Length, 10)],
            DeNombre = string.IsNullOrWhiteSpace(nombre) ? null : nombre.Trim()[..Math.Min(nombre.Trim().Length, 60)],
            A = a.Trim()[..Math.Min(a.Trim().Length, 10)],
            Busca = string.IsNullOrWhiteSpace(busca) ? null : busca.Trim()[..Math.Min(busca.Trim().Length, 30)],
            Estado = string.IsNullOrWhiteSpace(estado) ? "?" : estado.Trim()[..Math.Min(estado.Trim().Length, 20)],
            Segundos = int.TryParse(segundos, out var s) ? s : 0,
        });
        await _db.SaveChangesAsync();
        return Ok();
    }

    [HttpPost("mensaje-escuchado")]
    [AllowAnonymous]
    public async Task<IActionResult> MensajeEscuchado([FromForm] int id)
    {
        if (!EsLaCentral()) return Unauthorized();
        var m = await _db.TelefonoMensajes.FindAsync(id);
        if (m != null) { m.UltimoEscuchadoAr = PanoramaService.AhoraAr(); await _db.SaveChangesAsync(); }
        return Ok();
    }

    // ---------------- llamadas perdidas ----------------

    private static string[] MisNumeros(string mio) => mio == "210"
        ? new[] { "210", "211", "212", "213", "214", "215" }
        : mio is "201" or "202" or "203" ? new[] { mio, "200" } : new[] { mio };

    [HttpGet("perdidas")]
    public async Task<ActionResult<List<PerdidaDto>>> GetPerdidas()
    {
        var mio = MiInterno();
        if (mio == null) return new List<PerdidaDto>();
        var nums = MisNumeros(mio);
        var desde = DateTime.UtcNow.AddHours(-48);
        var filas = await _db.TelefonoLlamadas
            .Where(l => nums.Contains(l.A) && l.De != mio && !l.Vista && l.Estado != "ANSWER" && l.FechaUtc >= desde)
            .OrderByDescending(l => l.FechaUtc).Take(20).ToListAsync();
        // las del 200 (oficina todos): si alguien de la oficina la atendio, ya no es perdida
        return filas.Select(l => new PerdidaDto(l.Id, l.De, NombreDe(l.De, l.DeNombre), l.Busca, l.FechaUtc.AddHours(-3))).ToList();
    }

    [HttpPost("perdidas/vistas")]
    public async Task<IActionResult> MarcarVistas()
    {
        var mio = MiInterno();
        if (mio == null) return Ok();
        var nums = MisNumeros(mio);
        await _db.TelefonoLlamadas.Where(l => nums.Contains(l.A) && !l.Vista && l.Estado != "ANSWER")
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.Vista, true));
        return Ok();
    }

    // ---------------- mensajes programados (solo admin) ----------------

    private static MensajeDto Dto(TelefonoMensaje m) => new(m.Id, m.Texto, m.Destino, m.Hora, m.Dias, m.Fecha, m.Activo, m.UltimoEnvioAr, m.UltimoEscuchadoAr);

    [HttpGet("mensajes")]
    [Authorize(Roles = "admin")]
    public async Task<ActionResult<List<MensajeDto>>> GetMensajes() =>
        (await _db.TelefonoMensajes.OrderByDescending(m => m.Activo).ThenBy(m => m.Hora).ToListAsync()).Select(Dto).ToList();

    private static string? Validar(GuardarMensajeReq r)
    {
        if (string.IsNullOrWhiteSpace(r.Texto)) return "Escribí el mensaje";
        if (r.Texto.Length > 500) return "El mensaje es muy largo (máximo 500 letras)";
        if (!TimeSpan.TryParseExact(r.Hora, "hh\\:mm", null, out _)) return "Hora inválida";
        if (r.Destino != "todos" && Guia.All(g => g.Numero != r.Destino)) return "Elegí a quién";
        if (string.IsNullOrEmpty(r.Dias) && r.Fecha is null) return "Elegí los días o una fecha";
        return null;
    }

    private static string Dias(string? d) => new string((d ?? "").ToUpperInvariant().Where(c => "LMXJVSD".Contains(c)).Distinct().ToArray());

    [HttpPost("mensajes")]
    [Authorize(Roles = "admin")]
    public async Task<ActionResult<MensajeDto>> CrearMensaje([FromBody] GuardarMensajeReq r)
    {
        r = r with { Dias = Dias(r.Dias) };
        if (Validar(r) is { } err) return BadRequest(err);
        var m = new TelefonoMensaje { Texto = r.Texto.Trim(), Destino = r.Destino, Hora = r.Hora, Dias = r.Dias, Fecha = r.Fecha?.Date, Activo = r.Activo, CreadoPor = Request.Headers["X-Operator-Name"].ToString() };
        _db.TelefonoMensajes.Add(m);
        await _db.SaveChangesAsync();
        return Dto(m);
    }

    [HttpPut("mensajes/{id:int}")]
    [Authorize(Roles = "admin")]
    public async Task<ActionResult<MensajeDto>> EditarMensaje(int id, [FromBody] GuardarMensajeReq r)
    {
        var m = await _db.TelefonoMensajes.FindAsync(id);
        if (m == null) return NotFound();
        r = r with { Dias = Dias(r.Dias) };
        if (Validar(r) is { } err) return BadRequest(err);
        m.Texto = r.Texto.Trim(); m.Destino = r.Destino; m.Hora = r.Hora; m.Dias = r.Dias; m.Fecha = r.Fecha?.Date; m.Activo = r.Activo;
        await _db.SaveChangesAsync();
        return Dto(m);
    }

    [HttpDelete("mensajes/{id:int}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> BorrarMensaje(int id)
    {
        await _db.TelefonoMensajes.Where(m => m.Id == id).ExecuteDeleteAsync();
        return Ok();
    }

    // "Probar ahora": llama ya mismo con el texto (sin esperar la hora).
    [HttpPost("mensajes/probar")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> ProbarMensaje([FromBody] GuardarMensajeReq r)
    {
        if (string.IsNullOrWhiteSpace(r.Texto)) return BadRequest("Escribí el mensaje");
        var destinos = await _central.DestinosAsync(r.Destino);
        if (destinos.Count == 0) return BadRequest("No hay nadie conectado para recibirlo");
        var ok = 0;
        foreach (var d in destinos) if (await _central.LlamarYLeerAsync(d, r.Texto.Trim(), 0)) ok++;
        return ok > 0 ? Ok() : BadRequest("La central no pudo llamar");
    }

    // ---------------- repartidores (celu, link con token, sin login) ----------------

    [HttpGet("repartidor/{token}")]
    [AllowAnonymous]
    public async Task<ActionResult<TelefonoConfigDto>> ConfigRepartidor(string token)
    {
        var rep = await _db.CafeRepartidores.AsNoTracking().FirstOrDefaultAsync(x => x.PublicToken == token && x.IsActive);
        if (rep == null || string.IsNullOrWhiteSpace(TelefonoCentralService.Secreto) || string.IsNullOrWhiteSpace(WsUrl))
            return new TelefonoConfigDto(false, "", "", "", "", "", new());
        // 2026-11-26: en el celu aparecen en este orden: Oficina (todos), Deposito, Osmar
        var oficina = new List<InternoDto> { new("Oficina", "200", ""), new("Depósito", "210", ""), new("Osmar", "201", "") };
        return new TelefonoConfigDto(true, WsUrl, Dominio, "220", TelefonoCentralService.Clave("220"), rep.Nombre, oficina);
    }
}
