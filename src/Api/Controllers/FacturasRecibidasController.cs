using Api.Data;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 18/11/2026 — "Facturas recibidas": las facturas que nos hacen los proveedores (las trae de ARCA
/// <see cref="ComprasAfipRobot"/> todos los días a las 7). Las que entraron después de la última vez
/// que el dueño abrió la lista son "nuevas" (tarjeta Compras del inicio y campanita). Solo admin.
/// Maqueta aprobada: scratchpad/Compras_e_Impuestos_inicio_PROPUESTA.png
/// </summary>
[ApiController]
[Route("api/compras/facturas-recibidas")]
[Authorize(Roles = "admin")]
public class FacturasRecibidasController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ComprasAfipRobot _robot;
    private readonly ProveedorCtaCteService _ctacte;

    public FacturasRecibidasController(AppDbContext db, ComprasAfipRobot robot, ProveedorCtaCteService ctacte)
    { _db = db; _robot = robot; _ctacte = ctacte; }

    public record FacturaDto(string Id, DateTime Llego, DateTime? Fecha, string Proveedor, string? Cuit, int? ProveedorId,
        string Tipo, string Numero, decimal Total, bool EsNotaCredito, bool TienePdf, bool Nueva, string? Pago);
    public record PorProveedorDto(string Nombre, int Cantidad, decimal Total);
    public record EstadoDto(DateTime? Ultima, string? Resultado, bool Corriendo, DateTime? VistasHasta);
    public record ResumenDto(int Nuevas, decimal TotalNuevas, List<PorProveedorDto> PorProveedor, EstadoDto Estado);

    /// <summary>"Vistas hasta": la primera vez arranca ahora (lo que ya había no es "nuevo").</summary>
    private async Task<DateTime> VistasHastaAsync()
    {
        var v = ComprasAfipRobot.ParseUtc(await ComprasAfipRobot.LeerAsync(_db, ComprasAfipRobot.KeyVistas));
        if (v.HasValue) return v.Value;
        var ahora = DateTime.UtcNow;
        await ComprasAfipRobot.GuardarAsync(_db, ComprasAfipRobot.KeyVistas, ahora.ToString("o"));
        return ahora;
    }

    private async Task<EstadoDto> EstadoAsync(DateTime vistas) => new(
        ComprasAfipRobot.ParseUtc(await ComprasAfipRobot.LeerAsync(_db, ComprasAfipRobot.KeyUltima)),
        await ComprasAfipRobot.LeerAsync(_db, ComprasAfipRobot.KeyResultado),
        ComprasAfipRobot.Corriendo, vistas);

    [HttpGet("resumen")]
    public async Task<IActionResult> Resumen()
    {
        var vistas = await VistasHastaAsync();
        var nuevas = await _db.ContadoraComprobantes.AsNoTracking()
            .Where(c => c.Naturaleza == "COMPRA" && c.PrimeraVezEn > vistas && !c.EsNotaCredito)
            .Select(c => new { c.ReceptorNombre, c.ReceptorDoc, c.Total })
            .ToListAsync();
        var porProv = nuevas.GroupBy(n => n.ReceptorDoc ?? n.ReceptorNombre ?? "?")
            .Select(g => new PorProveedorDto(Lindo(g.First().ReceptorNombre), g.Count(), g.Sum(x => x.Total)))
            .OrderByDescending(x => x.Total).ToList();
        return Ok(new ResumenDto(nuevas.Count, nuevas.Sum(n => n.Total), porProv, await EstadoAsync(vistas)));
    }

    [HttpGet("estado")]
    public async Task<IActionResult> Estado() => Ok(await EstadoAsync(await VistasHastaAsync()));

    /// <summary>Por defecto, lo que llegó en los últimos 60 días. mes=yyyy-MM filtra por fecha de la factura.</summary>
    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] string? mes, [FromQuery] string? q)
    {
        var vistas = await VistasHastaAsync();
        var qq = _db.ContadoraComprobantes.AsNoTracking().Where(c => c.Naturaleza == "COMPRA");
        if (!string.IsNullOrWhiteSpace(mes) && DateTime.TryParse(mes + "-01", out var ini))
        {
            var fin = ini.AddMonths(1);
            qq = qq.Where(c => c.FechaEmision >= ini && c.FechaEmision < fin);
        }
        else
        {
            var desde = DateTime.UtcNow.AddDays(-60);
            qq = qq.Where(c => c.PrimeraVezEn >= desde || c.FechaEmision >= desde);
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim();
            qq = qq.Where(c => (c.ReceptorNombre != null && c.ReceptorNombre.Contains(t)) || (c.ReceptorDoc != null && c.ReceptorDoc.Contains(t)));
        }
        var filas = await qq.OrderByDescending(c => c.PrimeraVezEn).ThenByDescending(c => c.FechaEmision).Take(500)
            .Select(c => new
            {
                c.IdComprobante, c.PrimeraVezEn, c.FechaEmision, c.ReceptorNombre, c.ReceptorDoc, c.TipoComprobante,
                c.PuntoVenta, c.NumeroComprobante, c.Total, c.EsNotaCredito, TienePdf = c.PdfPath != null
            })
            .ToListAsync();

        // Proveedor del sistema por CUIT y, si lleva cuenta corriente, cuánto le queda de cada factura.
        var cuits = filas.Where(f => f.ReceptorDoc != null).Select(f => f.ReceptorDoc!).Distinct().ToList();
        var provs = (await _db.CafeProveedores.AsNoTracking().Where(p => p.Cuit != null && cuits.Contains(p.Cuit))
                .Select(p => new { p.Id, p.Cuit, p.CuentaCorriente }).ToListAsync())
            .GroupBy(p => p.Cuit!).ToDictionary(g => g.Key, g => g.First());
        var conCta = provs.Values.Where(p => p.CuentaCorriente).Select(p => p.Id).ToList();
        var saldos = new Dictionary<string, decimal>();
        if (conCta.Count > 0)
            foreach (var c in (await _ctacte.CalcularAsync(conCta)).Values)
                foreach (var d in c.Docs.Where(d => d.AfipIdComprobante != null))
                    saldos[d.AfipIdComprobante!] = d.Saldo;

        var ar = System.Globalization.CultureInfo.GetCultureInfo("es-AR");
        var res = filas.Select(f =>
        {
            var p = f.ReceptorDoc != null && provs.TryGetValue(f.ReceptorDoc, out var x) ? x : null;
            string? pago = null;
            if (p?.CuentaCorriente == true && !f.EsNotaCredito)
                pago = saldos.TryGetValue(f.IdComprobante, out var s)
                    ? (s <= 0.01m ? "pagada" : s < f.Total - 0.01m ? "debe " + s.ToString("$#,0", ar) : "a pagar")
                    : "anterior a la cuenta corriente";
            return new FacturaDto(f.IdComprobante, f.PrimeraVezEn, f.FechaEmision, Lindo(f.ReceptorNombre), f.ReceptorDoc, p?.Id,
                string.IsNullOrWhiteSpace(f.TipoComprobante) ? "Factura" : f.TipoComprobante!,
                ProveedorCtaCteService.NumeroAfip(f.PuntoVenta, f.NumeroComprobante),
                f.EsNotaCredito ? -f.Total : f.Total, f.EsNotaCredito, f.TienePdf, f.PrimeraVezEn > vistas, pago);
        }).ToList();
        return Ok(res);
    }

    /// <summary>Abrió la lista: lo que hay deja de ser "nuevo".</summary>
    [HttpPost("marcar-vistas")]
    public async Task<IActionResult> MarcarVistas()
    {
        await ComprasAfipRobot.GuardarAsync(_db, ComprasAfipRobot.KeyVistas, DateTime.UtcNow.ToString("o"));
        return Ok(new { ok = true });
    }

    /// <summary>"↻ Traer ahora": entra a ARCA en segundo plano (1–3 minutos).</summary>
    [HttpPost("traer-ahora")]
    public IActionResult TraerAhora()
    {
        var op = Request.Headers["X-Operator-Name"].FirstOrDefault();
        var iniciado = _robot.Iniciar(string.IsNullOrWhiteSpace(op) ? "a mano" : $"a mano ({op.Trim()})");
        return Ok(new { iniciado, corriendo = true });
    }

    /// <summary>"GOOD FOOD SA" → "Good Food SA" (AFIP manda todo en mayúsculas).</summary>
    public static string Lindo(string? n)
    {
        if (string.IsNullOrWhiteSpace(n)) return "?";
        var siglas = new HashSet<string> { "SA", "SRL", "SAS", "SH", "SACIF", "SAIC", "SACI", "SAICF", "S.A.", "S.R.L.", "S", "A", "C", "I", "F" };
        return string.Join(' ', n.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => siglas.Contains(w.ToUpperInvariant()) ? w.ToUpperInvariant()
                : w.Length <= 1 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }
}
