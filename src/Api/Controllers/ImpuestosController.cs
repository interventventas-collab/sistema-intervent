using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>17/11/2026 — Impuestos y VEP (ver <see cref="ImpuestosService"/>). Solo admin.</summary>
[ApiController]
[Route("api/impuestos")]
[Authorize(Roles = "admin")]
public class ImpuestosController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ImpuestosService _svc;
    public ImpuestosController(AppDbContext db, ImpuestosService svc) { _db = db; _svc = svc; }

    public record VepDto(int Id, string? NroVep, string Entidad, string Concepto, string? Detalle, string? Periodo,
        decimal Importe, DateTime? Vence, string Origen, string? MailAsunto, DateTime? MailFecha, string Estado,
        DateTime? PagadoFecha, string? PagadoComo, bool TienePdf, string? Nota, bool DelBanco);
    public record EditarReq(string? Concepto, string? Entidad, decimal? Importe, DateTime? Vence, string? Nota);
    public record PagadoReq(DateTime? Fecha, string? Como);
    public record SubirReq(string? Nombre, string? Base64, string? Texto);

    private static VepDto Map(ImpuestoVep v) => new(v.Id, v.NroVep, v.Entidad, v.Concepto, v.Detalle, v.Periodo, v.Importe,
        v.Vence, v.Origen, v.MailAsunto, v.MailFecha, v.Estado, v.PagadoFecha, v.PagadoComo, v.Pdf != null, v.Nota, v.ExtractoMovimientoId != null);

    [HttpGet]
    public async Task<IActionResult> Listar()
    {
        var l = await _db.ImpuestosVeps.AsNoTracking().Where(v => v.Estado != "DESCARTADO")
            .Select(v => new ImpuestoVep
            {
                Id = v.Id, NroVep = v.NroVep, Entidad = v.Entidad, Concepto = v.Concepto, Detalle = v.Detalle, Periodo = v.Periodo,
                Importe = v.Importe, Vence = v.Vence, Origen = v.Origen, MailAsunto = v.MailAsunto, MailFecha = v.MailFecha,
                Estado = v.Estado, PagadoFecha = v.PagadoFecha, PagadoComo = v.PagadoComo, Nota = v.Nota, CreatedAt = v.CreatedAt, ExtractoMovimientoId = v.ExtractoMovimientoId,
                Pdf = v.Pdf != null ? Array.Empty<byte>() : null,
            })
            .ToListAsync();
        return Ok(l.OrderByDescending(v => v.PagadoFecha ?? v.MailFecha ?? v.CreatedAt).Select(Map).ToList());
    }

    /// <summary>Lo que muestra la tarjeta del inicio.</summary>
    [HttpGet("avisos")]
    public async Task<IActionResult> Avisos()
    {
        var hoy = ImpuestosService.HoyAr;
        var iniMes = new DateTime(hoy.Year, hoy.Month, 1);
        var pend = await _db.ImpuestosVeps.AsNoTracking().Where(v => v.Estado == "PENDIENTE")
            .Select(v => new { v.Importe, v.Vence, v.MailFecha, v.Concepto, v.Entidad }).ToListAsync();
        DateTime VenceEf(DateTime? vence, DateTime? mail) => vence ?? (mail?.AddHours(-3).Date.AddDays(30) ?? hoy);
        var vigentes = pend.Where(p => VenceEf(p.Vence, p.MailFecha) >= hoy).ToList();
        var vencidos = pend.Where(p => VenceEf(p.Vence, p.MailFecha) < hoy && VenceEf(p.Vence, p.MailFecha) >= hoy.AddDays(-30)).ToList();
        var proximo = vigentes.Where(p => p.Vence.HasValue).OrderBy(p => p.Vence).FirstOrDefault();
        var pagadosMes = await _db.ImpuestosVeps.AsNoTracking()
            .Where(v => v.Estado == "PAGADO" && v.PagadoFecha >= iniMes).Select(v => v.Importe).ToListAsync();
        var hayDelMes = await _db.ImpuestosVeps.AnyAsync(v => v.Estado != "DESCARTADO" &&
            ((v.MailFecha != null && v.MailFecha >= iniMes.AddHours(3)) || (v.PagadoFecha != null && v.PagadoFecha >= iniMes)));
        return Ok(new
        {
            pendientes = vigentes.Count,
            pendientesImporte = vigentes.Sum(p => p.Importe),
            proximoVence = proximo?.Vence,
            proximoTexto = proximo is null ? null : $"{proximo.Concepto} {proximo.Entidad}",
            vencidosSinPago = vencidos.Count,
            pagadosMes = pagadosMes.Count,
            pagadosMesImporte = pagadosMes.Sum(),
            sinVepsEsteMes = hoy.Day >= 20 && !hayDelMes,
        });
    }

    [HttpGet("{id:int}/pdf")]
    public async Task<IActionResult> Pdf(int id)
    {
        var v = await _db.ImpuestosVeps.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Pdf, x.PdfNombre }).FirstOrDefaultAsync();
        if (v?.Pdf is null) return NotFound();
        Response.Headers["Content-Disposition"] = $"inline; filename=\"{(v.PdfNombre ?? "vep.pdf").Replace("\"", "")}\"";
        return File(v.Pdf, "application/pdf");
    }

    /// <summary>Leer ya los mails de la contadora y cruzar con el banco.</summary>
    [HttpPost("revisar")]
    public async Task<IActionResult> Revisar(CancellationToken ct) => Ok(await _svc.RevisarAsync(ct));

    /// <summary>Subir el PDF de un VEP (o pegar el texto de un WhatsApp de la contadora).</summary>
    [HttpPost("subir")]
    public async Task<IActionResult> Subir([FromBody] SubirReq req, CancellationToken ct)
    {
        var veps = new List<ImpuestosService.VepLeido>();
        string asunto;
        if (!string.IsNullOrWhiteSpace(req.Base64))
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(req.Base64); } catch { return BadRequest(new { error = "El archivo no se pudo leer" }); }
            if (bytes.Length > 5_000_000) return BadRequest(new { error = "El PDF es muy grande" });
            var v = await _svc.LeerPdfAsync(bytes, req.Nombre);
            if (v is null) return BadRequest(new { error = "No parece un VEP (no encontré número de VEP ni importe)" });
            veps.Add(v);
            asunto = "Subido a mano: " + (req.Nombre ?? "VEP");
        }
        else if (!string.IsNullOrWhiteSpace(req.Texto))
        {
            veps = ImpuestosService.LeerTexto(req.Texto, null);
            if (veps.Count == 0) return BadRequest(new { error = "No encontré montos con $ en el texto" });
            asunto = "Pegado a mano";
        }
        else return BadRequest(new { error = "Subí un PDF o pegá el texto" });

        var nuevos = await _svc.GuardarAsync(veps, "MANUAL", null, asunto, DateTime.UtcNow, ct);
        var (pagados, _) = await _svc.CruzarConBancoAsync(ct);
        return Ok(new { leidos = veps.Count, nuevos, pagados });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Editar(int id, [FromBody] EditarReq req)
    {
        var v = await _db.ImpuestosVeps.FindAsync(id);
        if (v is null) return NotFound();
        if (!string.IsNullOrWhiteSpace(req.Concepto)) v.Concepto = req.Concepto.Trim()[..Math.Min(80, req.Concepto.Trim().Length)];
        if (!string.IsNullOrWhiteSpace(req.Entidad)) v.Entidad = req.Entidad.Trim()[..Math.Min(80, req.Entidad.Trim().Length)];
        if (req.Importe is > 0 && v.ExtractoMovimientoId is null) v.Importe = Math.Round(req.Importe.Value, 2);
        v.Vence = req.Vence?.Date;
        v.Nota = string.IsNullOrWhiteSpace(req.Nota) ? null : req.Nota.Trim()[..Math.Min(500, req.Nota.Trim().Length)];
        v.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Marcar pagado a mano (QR de Mercado Pago, Rapipago, otra cuenta...).</summary>
    [HttpPost("{id:int}/pagado")]
    public async Task<IActionResult> Pagado(int id, [FromBody] PagadoReq req)
    {
        var v = await _db.ImpuestosVeps.FindAsync(id);
        if (v is null) return NotFound();
        v.Estado = "PAGADO";
        v.PagadoFecha = (req.Fecha ?? ImpuestosService.HoyAr).Date;
        v.PagadoComo = string.IsNullOrWhiteSpace(req.Como) ? "A mano" : req.Como.Trim()[..Math.Min(40, req.Como.Trim().Length)];
        v.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Deshacer un "pagado" puesto a mano (los que cruzó el banco no se tocan).</summary>
    [HttpPost("{id:int}/pendiente")]
    public async Task<IActionResult> Pendiente(int id)
    {
        var v = await _db.ImpuestosVeps.FindAsync(id);
        if (v is null) return NotFound();
        if (v.ExtractoMovimientoId is not null) return BadRequest(new { error = "Este pago salió del banco: no se puede desmarcar" });
        v.Estado = "PENDIENTE"; v.PagadoFecha = null; v.PagadoComo = null; v.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Sacar de la lista (no es un impuesto, está repetido...).</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Descartar(int id)
    {
        var v = await _db.ImpuestosVeps.FindAsync(id);
        if (v is null) return NotFound();
        v.Estado = "DESCARTADO"; v.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }
}
