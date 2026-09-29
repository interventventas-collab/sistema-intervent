using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-09-29 — "Avisos por WhatsApp" de la ficha del cliente: mandarle a Gabriel (o a quien se
/// elija de la libretita) lo que debe el cliente con cada comprobante sin pagar, ahora o
/// programado (una vez / semanal / mensual). La lógica vive en <see cref="ClienteAvisosWaService"/>.
/// </summary>
[ApiController]
[Route("api/cafe/cliente-avisos")]
[Authorize]
public class CafeClienteAvisosController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ClienteAvisosWaService _svc;

    public CafeClienteAvisosController(AppDbContext db, ClienteAvisosWaService svc)
    {
        _db = db; _svc = svc;
    }

    public record PersonaDto(int Id, string Nombre, bool TieneWhatsApp);

    public record ProgramadoDto(
        int Id, string Frecuencia, string Descripcion, List<int> PersonaIds, List<string> Personas,
        bool Activo, DateTime? ProximoEnvioAr, DateTime? UltimoEnvioAr, string? UltimoResultado);

    public record EnvioDto(DateTime FechaAr, string Destino, string Estado, string? Detalle);

    public record EstadoDto(
        List<PersonaDto> Personas, List<ProgramadoDto> Programados, List<EnvioDto> UltimosEnvios, string? Preview);

    public record EnviarAhoraRequest(List<int> PersonaIds);

    public record ProgramarRequest(
        string Frecuencia, string? Fecha, int? DiaSemana, int? DiaMes, string Hora, List<int> PersonaIds);

    public record ActivoRequest(bool Activo);

    private static DateTime? Ar(DateTime? utc) => utc is DateTime u ? DateTime.SpecifyKind(u.AddHours(-3), DateTimeKind.Unspecified) : null;

    [HttpGet("cliente/{clienteId:int}")]
    public async Task<ActionResult<EstadoDto>> Get(int clienteId)
    {
        if (!await _db.CafeClientes.AnyAsync(c => c.Id == clienteId))
            return NotFound(new { error = "No existe el cliente." });

        var personas = await _db.AutoPersonas.AsNoTracking().Where(p => p.Activo).OrderBy(p => p.Id)
            .Select(p => new PersonaDto(p.Id, p.Nombre, p.WhatsAppNumero != null && p.WhatsAppNumero != ""))
            .ToListAsync();
        var nombres = personas.ToDictionary(p => p.Id, p => p.Nombre);

        var avisos = await _db.CafeAvisosDeuda.AsNoTracking().Where(a => a.ClienteId == clienteId)
            .OrderByDescending(a => a.Activo).ThenBy(a => a.ProximoEnvio).ToListAsync();
        var programados = new List<ProgramadoDto>();
        foreach (var a in avisos)
        {
            var ids = await _svc.PersonasDeClaveAsync(CafeAvisoDeuda.Clave(a.Id));
            programados.Add(new ProgramadoDto(a.Id, a.Frecuencia, ClienteAvisosWaService.Describir(a), ids,
                ids.Select(i => nombres.GetValueOrDefault(i, "?")).ToList(),
                a.Activo, Ar(a.ProximoEnvio), Ar(a.UltimoEnvioAt), a.UltimoResultado));
        }

        // Lo último que salió (o está esperando) para este cliente: deuda y avisos de venta.
        var origenDeuda = $"deuda:{clienteId}";
        var origenMes = $"bonif-mes:{clienteId}";
        var ventasIds = await _db.CafeVentas.AsNoTracking().Where(v => v.ClienteId == clienteId)
            .OrderByDescending(v => v.Id).Take(40).Select(v => v.Id).ToListAsync();
        var origenesVenta = ventasIds.Select(ClienteAvisosWaService.OrigenVenta).ToList();
        var filas = await _db.WhatsAppMensajesProgramados.AsNoTracking()
            .Where(x => x.Origen == origenDeuda || x.Origen == origenMes || (x.Origen != null && origenesVenta.Contains(x.Origen)))
            .OrderByDescending(x => x.Id).Take(8).ToListAsync();
        var personasNum = await _db.AutoPersonas.AsNoTracking().Where(p => p.WhatsAppNumero != null).ToListAsync();
        string Quien(string numero)
        {
            var p = personasNum.FirstOrDefault(pp => MetaWhatsAppService.ToInboxWhatsApp(pp.WhatsAppNumero) == numero);
            return p?.Nombre ?? numero.Replace("whatsapp:", "");
        }
        var envios = filas.Select(f => new EnvioDto(
            (f.EnviadoAt ?? f.CreatedAt).AddHours(-3),
            Quien(f.Numero),
            f.Estado == WhatsAppMensajeProgramado.EstadoPendiente && f.Error != null ? "ESPERANDO" : f.Estado,
            // El "esperando" ya lo dice el estado; el motivo se muestra solo si no salió.
            (f.Origen == origenDeuda ? "Lo que debe" : f.Origen == origenMes ? "Lo que va del mes" : "Aviso de venta")
                + (f.Estado == WhatsAppMensajeProgramado.EstadoError && !string.IsNullOrWhiteSpace(f.Error) ? " · " + f.Error : "")))
            .ToList();

        return Ok(new EstadoDto(personas, programados, envios, await _svc.ArmarTextoDeudaAsync(clienteId)));
    }

    [HttpPost("cliente/{clienteId:int}/enviar-ahora")]
    public async Task<IActionResult> EnviarAhora(int clienteId, [FromBody] EnviarAhoraRequest req)
    {
        var (enviados, error) = await _svc.EncolarDeudaAsync(clienteId, req.PersonaIds ?? new(), User?.Identity?.Name);
        if (error != null) return BadRequest(new { error });
        return Ok(new { enviados });
    }

    [HttpPost("cliente/{clienteId:int}/programar")]
    public async Task<IActionResult> Programar(int clienteId, [FromBody] ProgramarRequest req)
    {
        if (!await _db.CafeClientes.AnyAsync(c => c.Id == clienteId))
            return NotFound(new { error = "No existe el cliente." });
        if ((req.PersonaIds ?? new()).Count == 0)
            return BadRequest(new { error = "Elegí al menos a quién mandárselo." });
        if (!TimeSpan.TryParseExact(req.Hora ?? "", @"hh\:mm", null, out var hora))
            return BadRequest(new { error = "Falta la hora (ej 10:00)." });

        var a = new CafeAvisoDeuda
        {
            ClienteId = clienteId,
            Frecuencia = req.Frecuencia,
            HoraMin = (int)hora.TotalMinutes,
            CreatedBy = User?.Identity?.Name,
        };
        switch (req.Frecuencia)
        {
            case CafeAvisoDeuda.FrecUnaVez:
                if (!DateTime.TryParseExact(req.Fecha ?? "", "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var f))
                    return BadRequest(new { error = "Falta el día." });
                a.Fecha = f.Date;
                break;
            case CafeAvisoDeuda.FrecSemanal:
                if (req.DiaSemana is not (>= 1 and <= 7)) return BadRequest(new { error = "Elegí el día de la semana." });
                a.DiaSemana = req.DiaSemana;
                break;
            case CafeAvisoDeuda.FrecMensual:
                if (req.DiaMes is not (>= 1 and <= 28)) return BadRequest(new { error = "El día del mes tiene que ser del 1 al 28 (así existe en todos los meses)." });
                a.DiaMes = req.DiaMes;
                break;
            default:
                return BadRequest(new { error = "Frecuencia inválida." });
        }
        a.ProximoEnvio = ClienteAvisosWaService.CalcularProximo(a, DateTime.UtcNow);
        if (a.ProximoEnvio == null) return BadRequest(new { error = "Ese día y hora ya pasaron." });

        _db.CafeAvisosDeuda.Add(a);
        await _db.SaveChangesAsync();
        await _svc.GuardarPersonasDeClaveAsync(CafeAvisoDeuda.Clave(a.Id), req.PersonaIds!);
        return Ok(new { a.Id, descripcion = ClienteAvisosWaService.Describir(a), proximoEnvioAr = Ar(a.ProximoEnvio) });
    }

    /// <summary>Manda YA el aviso de venta del plan de bonificación con la última venta del cliente,
    /// para ver cómo le llega a Gabriel sin tener que cargar una venta. Mismo camino que el real.</summary>
    /// <summary>Manda YA el resumen de lo que va del mes del plan de bonificación (sin venta).</summary>
    [HttpPost("cliente/{clienteId:int}/enviar-resumen-bonif")]
    public async Task<IActionResult> EnviarResumenBonif(int clienteId)
    {
        var (enviados, error) = await _svc.EncolarResumenBonifAsync(clienteId, User?.Identity?.Name);
        if (error != null) return BadRequest(new { error });
        return Ok(new { enviados });
    }

    [HttpPost("cliente/{clienteId:int}/probar-aviso-venta")]
    public async Task<IActionResult> ProbarAvisoVenta(int clienteId)
    {
        var plan = await _db.CafePlanesBonificacion.AsNoTracking().FirstOrDefaultAsync(p => p.ClienteId == clienteId);
        if (plan is not { Activo: true, AvisarEnCadaVenta: true })
            return BadRequest(new { error = "Primero prendé \"avisar en cada venta\" en el plan y guardalo." });
        var ventaId = await _db.CafeVentas.AsNoTracking()
            .Where(v => v.ClienteId == clienteId && v.Estado != "anulado" && v.TipoComprobante != "PRO" && v.OrigenVentaId == null)
            .OrderByDescending(v => v.Id).Select(v => (int?)v.Id).FirstOrDefaultAsync();
        if (ventaId == null) return BadRequest(new { error = "Este cliente todavía no tiene ventas." });
        await _svc.EncolarAvisoVentaAsync(ventaId.Value, User?.Identity?.Name, DateTime.UtcNow);
        return Ok(new { ok = true });
    }

    [HttpPut("{avisoId:int}/activo")]
    public async Task<IActionResult> CambiarActivo(int avisoId, [FromBody] ActivoRequest req)
    {
        var a = await _db.CafeAvisosDeuda.FindAsync(avisoId);
        if (a == null) return NotFound(new { error = "No existe ese aviso." });
        a.Activo = req.Activo;
        if (req.Activo)
        {
            a.ProximoEnvio = ClienteAvisosWaService.CalcularProximo(a, DateTime.UtcNow);
            if (a.ProximoEnvio == null) return BadRequest(new { error = "Ese día y hora ya pasaron: programalo de nuevo." });
        }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    [HttpDelete("{avisoId:int}")]
    public async Task<IActionResult> Borrar(int avisoId)
    {
        var a = await _db.CafeAvisosDeuda.FindAsync(avisoId);
        if (a == null) return NotFound(new { error = "No existe ese aviso." });
        _db.CafeAvisosDeuda.Remove(a);
        var dest = await _db.AutoDestinatarios.Where(d => d.AutoKey == CafeAvisoDeuda.Clave(avisoId)).ToListAsync();
        _db.AutoDestinatarios.RemoveRange(dest);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }
}
