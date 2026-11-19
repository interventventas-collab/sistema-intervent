using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 18/11/2026: "#gasto" por WhatsApp (ver Services/GastoWhatsAppService.cs). Listado con comentarios,
/// aplicar (tilde, atado al pago a proveedor) y rechazar. Nada de esto responde por WhatsApp.
/// Solo admin (igual que la tarjeta Compras con avisos y la campanita de compras).
/// </summary>
[ApiController]
[Route("api/compras/gastos-whatsapp")]
[Authorize(Roles = "admin")]
public class GastosWhatsAppController : ControllerBase
{
    private readonly AppDbContext _db;
    public GastosWhatsAppController(AppDbContext db) { _db = db; }

    private string Quien() => Request.Headers["X-Operator-Name"].FirstOrDefault() is { Length: > 0 } op ? op.Trim() : (User.Identity?.Name ?? "?");

    public record ComentarioDto(int Id, string Texto, string? Usuario, DateTime CreatedAt);
    public record GastoDto(int Id, string Numero, string? Quien, string Texto, string? MediaUrl, string? MediaFilename,
        decimal? Importe, string? Medio, string Estado, int? PagoId, string? PagoNumero, string? MotivoRechazo,
        string? ResueltoPor, DateTime? ResueltoAt, DateTime CreatedAt, List<ComentarioDto> Comentarios);

    private async Task<Dictionary<string, string>> NombresAsync()
    {
        // Nombre de la persona de Alertas con ese número (si no, el nombre de perfil de WhatsApp).
        var ps = await _db.AutoPersonas.AsNoTracking().Where(p => p.WhatsAppNumero != null && p.WhatsAppNumero != "")
            .Select(p => new { p.Nombre, p.WhatsAppNumero }).ToListAsync();
        var d = new Dictionary<string, string>();
        foreach (var p in ps) d.TryAdd(GastoWhatsAppService.Clave(p.WhatsAppNumero), p.Nombre);
        return d;
    }

    private async Task<List<GastoDto>> ArmarAsync(List<GastoWhatsApp> gs)
    {
        var ids = gs.Select(g => g.Id).ToList();
        var coms = await _db.GastosWhatsAppComentarios.AsNoTracking().Where(c => ids.Contains(c.GastoId))
            .OrderBy(c => c.Id).ToListAsync();
        var nombres = await NombresAsync();
        return gs.Select(g => new GastoDto(g.Id, g.Numero,
            nombres.TryGetValue(GastoWhatsAppService.Clave(g.Numero), out var n) ? n : g.NombrePerfil,
            g.Texto, g.MediaUrl, g.MediaFilename, g.Importe, g.Medio, g.Estado, g.PagoId, g.PagoNumero, g.MotivoRechazo,
            g.ResueltoPor, g.ResueltoAt, g.CreatedAt,
            coms.Where(c => c.GastoId == g.Id).Select(c => new ComentarioDto(c.Id, c.Texto, c.Usuario, c.CreatedAt)).ToList()))
            .ToList();
    }

    /// <summary>Listado. estado = PENDIENTE / APLICADO / RECHAZADO / (vacío = todos). Lo más nuevo arriba.</summary>
    [HttpGet]
    public async Task<IActionResult> Lista([FromQuery] string? estado = null, [FromQuery] int take = 300)
    {
        var q = _db.GastosWhatsApp.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(estado)) q = q.Where(g => g.Estado == estado);
        var gs = await q.OrderByDescending(g => g.Id).Take(Math.Clamp(take, 1, 1000)).ToListAsync();
        return Ok(await ArmarAsync(gs));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Uno(int id)
    {
        var g = await _db.GastosWhatsApp.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (g is null) return NotFound();
        return Ok((await ArmarAsync(new() { g }))[0]);
    }

    public record ComentarRequest(string Texto);
    [HttpPost("{id:int}/comentarios")]
    public async Task<IActionResult> Comentar(int id, [FromBody] ComentarRequest r)
    {
        var t = (r.Texto ?? "").Trim();
        if (t.Length == 0) return BadRequest(new { message = "Escribí el comentario" });
        if (!await _db.GastosWhatsApp.AnyAsync(g => g.Id == id)) return NotFound();
        _db.GastosWhatsAppComentarios.Add(new GastoWhatsAppComentario
        {
            GastoId = id, Texto = t.Length > 1000 ? t[..1000] : t, Usuario = Quien(), CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        return Ok();
    }

    public record AplicarRequest(int PagoId);
    /// <summary>Lo llama "Nuevo pago" al guardar un pago abierto desde un #gasto: queda con tilde.</summary>
    [HttpPost("{id:int}/aplicar")]
    public async Task<IActionResult> Aplicar(int id, [FromBody] AplicarRequest r)
    {
        var g = await _db.GastosWhatsApp.FirstOrDefaultAsync(x => x.Id == id);
        if (g is null) return NotFound();
        var pago = await _db.CafePagosProveedor.AsNoTracking().FirstOrDefaultAsync(p => p.Id == r.PagoId);
        if (pago is null) return BadRequest(new { message = "No encontré el pago" });
        g.Estado = "APLICADO"; g.PagoId = pago.Id; g.PagoNumero = pago.Numero; g.MotivoRechazo = null;
        g.ResueltoPor = Quien(); g.ResueltoAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok();
    }

    public record RechazarRequest(string? Motivo);
    [HttpPost("{id:int}/rechazar")]
    public async Task<IActionResult> Rechazar(int id, [FromBody] RechazarRequest r)
    {
        var g = await _db.GastosWhatsApp.FirstOrDefaultAsync(x => x.Id == id);
        if (g is null) return NotFound();
        if (g.Estado == "APLICADO") return BadRequest(new { message = "Ya está aplicado a un pago: anulá el pago primero" });
        g.Estado = "RECHAZADO"; g.MotivoRechazo = string.IsNullOrWhiteSpace(r.Motivo) ? null : r.Motivo.Trim()[..Math.Min(300, r.Motivo.Trim().Length)];
        g.ResueltoPor = Quien(); g.ResueltoAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok();
    }

    /// <summary>19/11/2026: "Ignorar" de un toque, sin motivo (reemplaza a Rechazar en la pantalla).</summary>
    [HttpPost("{id:int}/ignorar")]
    public async Task<IActionResult> Ignorar(int id)
    {
        var g = await _db.GastosWhatsApp.FirstOrDefaultAsync(x => x.Id == id);
        if (g is null) return NotFound();
        if (g.Estado == "APLICADO") return BadRequest(new { message = "Ya está aplicado a un pago: anulá el pago primero" });
        g.Estado = "IGNORADO"; g.ResueltoPor = Quien(); g.ResueltoAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok();
    }

    /// <summary>Vuelve a pendiente (deshacer un rechazo, o un aplicado si el pago se anuló).</summary>
    [HttpPost("{id:int}/reabrir")]
    public async Task<IActionResult> Reabrir(int id)
    {
        var g = await _db.GastosWhatsApp.FirstOrDefaultAsync(x => x.Id == id);
        if (g is null) return NotFound();
        g.Estado = "PENDIENTE"; g.PagoId = null; g.PagoNumero = null; g.MotivoRechazo = null;
        g.ResueltoPor = null; g.ResueltoAt = null;
        await _db.SaveChangesAsync();
        return Ok();
    }

    // ---------- Quién puede mandar #gasto ----------
    public record PersonaNumeroDto(string Nombre, string Numero);
    public record NumerosDto(List<string> Numeros, bool UsaAlertas, List<PersonaNumeroDto> DeAlertas);

    [HttpGet("numeros")]
    public async Task<IActionResult> Numeros()
    {
        var conf = await GastoWhatsAppService.NumerosConfiguradosAsync(_db);
        var ps = await _db.AutoPersonas.AsNoTracking()
            .Where(p => p.Activo && p.WhatsAppNumero != null && p.WhatsAppNumero != "")
            .OrderBy(p => p.Nombre).Select(p => new PersonaNumeroDto(p.Nombre, p.WhatsAppNumero!)).ToListAsync();
        return Ok(new NumerosDto(conf, conf.Count == 0, ps));
    }

    public record NumerosRequest(List<string> Numeros);
    [HttpPut("numeros")]
    public async Task<IActionResult> GuardarNumeros([FromBody] NumerosRequest r)
    {
        var lista = (r.Numeros ?? new()).Select(x => (x ?? "").Trim())
            .Where(x => GastoWhatsAppService.Clave(x).Length >= 8).Distinct().ToList();
        var s = await _db.AppSettings.FirstOrDefaultAsync(x => x.Key == GastoWhatsAppService.KeyNumeros);
        if (s is null) _db.AppSettings.Add(s = new AppSetting { Key = GastoWhatsAppService.KeyNumeros });
        s.Value = string.Join(",", lista);
        s.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok();
    }
}
