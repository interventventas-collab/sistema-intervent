using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 17/11/2026 — Pagos a proveedores PROGRAMADOS (ver <see cref="CafePagoProgramado"/>).
/// Pedido del dueño: "elegir un proveedor y poder cargar el pago pendiente con fecha", como el
/// "Programar pago" del equipo. No mueve plata: el pago real se hace con el Nuevo pago de siempre.
/// </summary>
[ApiController]
[Route("api/cafe/pagos-programados")]
[Authorize]
public class CafePagosProgramadosController : ControllerBase
{
    private static readonly string[] Medios = { "TRANSFERENCIA", "ECHEQ", "EFECTIVO" };
    private readonly AppDbContext _db;
    private readonly PagosProgramadosService _svc;

    public CafePagosProgramadosController(AppDbContext db, PagosProgramadosService svc) { _db = db; _svc = svc; }

    public record ProgramadoDto(int Id, int ProveedorId, string ProveedorNombre, DateTime Fecha, decimal Importe,
        string Medio, List<string> Claves, string? Nota, int Parte, int Partes, string Estado, int? PagoId, string? Operador,
        // 17/11/2026: todos los meses + datos para transferir + lo que vino en el banco
        int? DiaMes = null, bool ImporteUltimaFactura = false, DateTime? TransferidoAt = null,
        string? Cuit = null, string? Alias = null, string? Cbu = null, string? FacturaTexto = null,
        int? BancoMovId = null, DateTime? BancoFecha = null, decimal? BancoImporte = null,
        DateTime? PagadoFecha = null, bool PagadoPorBanco = false);
    public record ParteReq(DateTime Fecha, decimal Importe);
    public record CrearReq(int ProveedorId, string? Medio, List<string>? Claves, string? Nota, List<ParteReq> Partes,
        int? DiaMes = null, bool ImporteUltimaFactura = false, string? Alias = null, string? Cbu = null);
    public record EditarReq(DateTime Fecha, decimal Importe, string? Medio, string? Nota);
    public record PagadoReq(int PagoId);
    public record TomarBancoReq(int MovId);
    public record DatosBancoReq(string? Alias, string? Cbu);

    /// <summary>Por defecto: todos los PENDIENTES (atrasados incluidos). Con desde/hasta: los de ese rango.</summary>
    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] DateTime? desde, [FromQuery] DateTime? hasta,
        [FromQuery] string? estado = "PENDIENTE", [FromQuery] int? proveedorId = null)
    {
        // 17/11/2026: antes de mostrar, armar el mes siguiente de los "todos los meses" y cerrar los que vinieron en el banco.
        try { await _svc.MantenerAsync(); await _svc.CruzarConBancoAsync(); } catch { /* no impide ver la lista */ }
        var q = _db.CafePagosProgramados.AsNoTracking().Include(p => p.Proveedor).AsQueryable();
        if (!string.IsNullOrWhiteSpace(estado) && estado != "TODOS") q = q.Where(p => p.Estado == estado);
        if (desde.HasValue) q = q.Where(p => p.Fecha >= desde.Value.Date);
        if (hasta.HasValue) q = q.Where(p => p.Fecha <= hasta.Value.Date);
        if (proveedorId.HasValue) q = q.Where(p => p.ProveedorId == proveedorId.Value);
        var lista = await q.OrderBy(p => p.Fecha).ThenBy(p => p.Id).ToListAsync();
        return Ok(await MapListaAsync(lista));
    }

    /// <summary>17/11/2026: los pagados en los últimos días (para mostrar "✓ pagado (vino en el banco)").</summary>
    [HttpGet("recien-pagados")]
    public async Task<IActionResult> RecienPagados([FromQuery] int dias = 7)
    {
        var desde = DateTime.UtcNow.AddDays(-Math.Clamp(dias, 1, 60));
        var lista = await _db.CafePagosProgramados.AsNoTracking().Include(p => p.Proveedor)
            .Where(p => p.Estado == "PAGADO" && p.UpdatedAt >= desde)
            .OrderByDescending(p => p.UpdatedAt).Take(30).ToListAsync();
        return Ok(await MapListaAsync(lista));
    }

    private async Task<List<ProgramadoDto>> MapListaAsync(List<CafePagoProgramado> lista)
    {
        var cand = await _svc.CandidatosAsync(lista);
        var pagoIds = lista.Where(p => p.PagoId != null).Select(p => p.PagoId!.Value).ToList();
        var pagos = pagoIds.Count == 0 ? new() : await _db.CafePagosProveedor.AsNoTracking()
            .Where(x => pagoIds.Contains(x.Id)).Select(x => new { x.Id, x.Fecha, x.ExtractoMovId })
            .ToDictionaryAsync(x => x.Id);
        var res = new List<ProgramadoDto>();
        foreach (var p in lista)
        {
            var dto = Map(p);
            string? fac = null;
            if (p.Estado == "PENDIENTE" && p.ImporteUltimaFactura)
            {
                var uf = await _svc.UltimaFacturaAsync(p.Proveedor?.Cuit, p.Fecha);
                if (uf is not null) fac = $"{uf.Texto} · {uf.Fecha:dd/MM}";
            }
            // Sugerencia del banco: solo si NO coincide el importe (las exactas se cierran solas).
            var mov = cand.TryGetValue(p.Id, out var l) ? l.FirstOrDefault() : null;
            var pg = p.PagoId.HasValue && pagos.TryGetValue(p.PagoId.Value, out var x) ? x : null;
            res.Add(dto with
            {
                FacturaTexto = fac,
                BancoMovId = mov?.Id, BancoFecha = mov?.Fecha, BancoImporte = mov?.Importe,
                PagadoFecha = pg?.Fecha, PagadoPorBanco = pg?.ExtractoMovId != null,
            });
        }
        return res;
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var p = await _db.CafePagosProgramados.AsNoTracking().Include(x => x.Proveedor).FirstOrDefaultAsync(x => x.Id == id);
        return p is null ? NotFound() : Ok((await MapListaAsync(new() { p }))[0]);
    }

    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] CrearReq req)
    {
        if (!await _db.CafeProveedores.AnyAsync(p => p.Id == req.ProveedorId)) return BadRequest(new { error = "Elegí un proveedor" });
        var partes = (req.Partes ?? new()).Where(p => p.Importe > 0).ToList();
        if (partes.Count == 0) return BadRequest(new { error = "Poné el importe" });
        if (partes.Count > 24) return BadRequest(new { error = "Máximo 24 partes" });
        var medio = NormMedio(req.Medio);
        var claves = string.Join("|", (req.Claves ?? new()).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct());
        var nota = Recortar(req.Nota, 500);
        var op = QuienCarga();
        var prov = await _db.CafeProveedores.FirstAsync(p => p.Id == req.ProveedorId);
        if (req.Alias != null || req.Cbu != null)
        {
            prov.Alias = Recortar(req.Alias, 100);
            prov.Cbu = SoloDigitos(req.Cbu, 30);
            prov.UpdatedAt = DateTime.UtcNow;
        }

        // 17/11/2026 — "Todos los meses": un solo programado (el próximo); los siguientes los arma el sistema.
        if (req.DiaMes is >= 1 and <= 31)
        {
            var primera = partes.OrderBy(x => x.Fecha).First();
            var fecha = primera.Fecha.Date >= PagosProgramadosService.HoyAr && primera.Fecha.Day == req.DiaMes
                ? PagosProgramadosService.FechaDelMes(primera.Fecha.Year, primera.Fecha.Month, req.DiaMes.Value)
                : PagosProgramadosService.PrimeraFecha(req.DiaMes.Value, PagosProgramadosService.HoyAr);
            var pm = new CafePagoProgramado
            {
                ProveedorId = req.ProveedorId, Fecha = fecha, Importe = Math.Round(primera.Importe, 2), Medio = medio,
                Claves = string.IsNullOrEmpty(claves) ? null : claves, Nota = nota, Operador = op,
                DiaMes = req.DiaMes, ImporteUltimaFactura = req.ImporteUltimaFactura,
            };
            _db.CafePagosProgramados.Add(pm);
            await _db.SaveChangesAsync();
            pm.SerieId = pm.Id;
            await _db.SaveChangesAsync();
            return Ok(new { cantidad = 1, fecha });
        }
        await _db.SaveChangesAsync();   // alias/CBU
        var n = 0;
        foreach (var pt in partes.OrderBy(p => p.Fecha))
        {
            n++;
            _db.CafePagosProgramados.Add(new CafePagoProgramado
            {
                ProveedorId = req.ProveedorId, Fecha = pt.Fecha.Date, Importe = Math.Round(pt.Importe, 2), Medio = medio,
                Claves = string.IsNullOrEmpty(claves) ? null : claves, Nota = nota,
                Parte = n, Partes = partes.Count, Operador = op,
            });
        }
        await _db.SaveChangesAsync();
        return Ok(new { cantidad = n });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Editar(int id, [FromBody] EditarReq req)
    {
        var p = await _db.CafePagosProgramados.FindAsync(id);
        if (p is null) return NotFound();
        if (p.Estado != "PENDIENTE") return BadRequest(new { error = "Ya no está pendiente" });
        if (req.Importe <= 0) return BadRequest(new { error = "Poné el importe" });
        p.Fecha = req.Fecha.Date;
        p.Importe = Math.Round(req.Importe, 2);
        p.Medio = NormMedio(req.Medio);
        p.Nota = Recortar(req.Nota, 500);
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Sacarlo de la lista (no se pagó así). Queda ANULADO, no se borra.</summary>
    /// <summary>17/11/2026: si es de "todos los meses", <paramref name="todos"/>=true deja de repetirse;
    /// si no, solo se saltea este mes y aparece el del mes siguiente.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Anular(int id, [FromQuery] bool todos = false)
    {
        var p = await _db.CafePagosProgramados.FindAsync(id);
        if (p is null) return NotFound();
        if (p.Estado != "PENDIENTE") return BadRequest(new { error = "Ya no está pendiente" });
        p.Estado = "ANULADO";
        if (todos) p.DiaMes = null;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _svc.MantenerAsync();
        return Ok(new { ok = true });
    }

    /// <summary>17/11/2026: "✓ Ya la hice" — queda esperando que aparezca en el banco. deshacer=true lo vuelve atrás.</summary>
    [HttpPost("{id:int}/transferido")]
    public async Task<IActionResult> Transferido(int id, [FromQuery] bool deshacer = false)
    {
        var p = await _db.CafePagosProgramados.FindAsync(id);
        if (p is null) return NotFound();
        if (p.Estado != "PENDIENTE") return BadRequest(new { error = "Ya no está pendiente" });
        p.TransferidoAt = deshacer ? null : DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>17/11/2026: tomar como pago una transferencia del banco que no coincidía clavada en importe.</summary>
    [HttpPost("{id:int}/tomar-banco")]
    public async Task<IActionResult> TomarBanco(int id, [FromBody] TomarBancoReq req)
    {
        var p = await _db.CafePagosProgramados.Include(x => x.Proveedor).FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        if (p.Estado != "PENDIENTE") return BadRequest(new { error = "Ya no está pendiente" });
        var (error, pagoId) = await _svc.PagarConMovAsync(p, req.MovId, QuienCarga());
        if (error is not null) return BadRequest(new { error });
        await _svc.MantenerAsync();
        return Ok(new { ok = true, pagoId });
    }

    /// <summary>17/11/2026: alias y CBU del proveedor (para la ventana "Transferir a ...").</summary>
    [HttpPut("proveedor/{proveedorId:int}/banco")]
    public async Task<IActionResult> DatosBanco(int proveedorId, [FromBody] DatosBancoReq req)
    {
        var prov = await _db.CafeProveedores.FindAsync(proveedorId);
        if (prov is null) return NotFound();
        prov.Alias = Recortar(req.Alias, 100);
        prov.Cbu = SoloDigitos(req.Cbu, 30);
        prov.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    public record ProveedorBancoDto(int Id, string Nombre, string? Cuit, string? Alias, string? Cbu, decimal? UltimaFacturaImporte, string? UltimaFacturaTexto);

    [HttpGet("proveedor/{proveedorId:int}")]
    public async Task<IActionResult> ProveedorBanco(int proveedorId)
    {
        var prov = await _db.CafeProveedores.AsNoTracking().FirstOrDefaultAsync(x => x.Id == proveedorId);
        if (prov is null) return NotFound();
        var uf = await _svc.UltimaFacturaAsync(prov.Cuit);
        return Ok(new ProveedorBancoDto(prov.Id, prov.Nombre, prov.Cuit, prov.Alias, prov.Cbu, uf?.Total,
            uf is null ? null : $"{uf.Texto} · {uf.Fecha:dd/MM}"));
    }

    /// <summary>Se guardó el pago de verdad (Nuevo pago abierto desde "Pagar"): queda PAGADO.</summary>
    [HttpPost("{id:int}/pagado")]
    public async Task<IActionResult> Pagado(int id, [FromBody] PagadoReq req)
    {
        var p = await _db.CafePagosProgramados.FindAsync(id);
        if (p is null) return NotFound();
        if (!await _db.CafePagosProveedor.AnyAsync(x => x.Id == req.PagoId && x.ProveedorId == p.ProveedorId))
            return BadRequest(new { error = "Ese pago no es de este proveedor" });
        p.Estado = "PAGADO";
        p.PagoId = req.PagoId;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _svc.MantenerAsync();
        return Ok(new { ok = true });
    }

    private static ProgramadoDto Map(CafePagoProgramado p) => new(p.Id, p.ProveedorId, p.Proveedor?.Nombre ?? "?", p.Fecha.Date,
        p.Importe, p.Medio, (p.Claves ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).ToList(), p.Nota,
        p.Parte, p.Partes, p.Estado, p.PagoId, p.Operador,
        p.DiaMes, p.ImporteUltimaFactura, p.TransferidoAt, p.Proveedor?.Cuit, p.Proveedor?.Alias, p.Proveedor?.Cbu);

    private static string? SoloDigitos(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var d = new string(s.Where(char.IsDigit).ToArray());
        return d.Length == 0 ? null : d.Length > max ? d[..max] : d;
    }

    private static string NormMedio(string? m)
    {
        var u = (m ?? "").Trim().ToUpperInvariant();
        return Medios.Contains(u) ? u : "TRANSFERENCIA";
    }

    private static string? Recortar(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length > max ? s[..max] : s;
    }

    private string? QuienCarga()
    {
        var op = Request?.Headers["X-Operator-Name"].FirstOrDefault();
        return string.IsNullOrWhiteSpace(op) ? User?.Identity?.Name : op.Trim();
    }
}
