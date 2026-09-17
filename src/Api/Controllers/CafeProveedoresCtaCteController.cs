using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 17/09/2026 — Cuenta corriente de proveedores (Clientes y proveedores → Cuenta corriente proveedores).
/// El cálculo vive en <see cref="ProveedorCtaCteService"/>; acá solo se arma lo que ve la pantalla,
/// el alta con saldo inicial, las cotizaciones y su foto.
/// </summary>
[ApiController]
[Route("api/cafe/proveedores-ctacte")]
[Authorize]
public class CafeProveedoresCtaCteController : ControllerBase
{
    private const string CarpetaArchivos = "proveedores/cotizaciones";

    private readonly AppDbContext _db;
    private readonly ProveedorCtaCteService _svc;
    private readonly FileStorageService _storage;

    public CafeProveedoresCtaCteController(AppDbContext db, ProveedorCtaCteService svc, FileStorageService storage)
    { _db = db; _svc = svc; _storage = storage; }

    public record ResumenDto(int ProveedorId, string Nombre, string? Cuit, DateTime? Desde,
        decimal Oficial, decimal NoOficial, decimal ACuenta, decimal Total, int Pendientes);
    public record DocDto(string Clave, string Tipo, string Numero, bool Oficial, bool EsNotaCredito, bool EsSaldoInicial,
        DateTime Fecha, decimal Total, decimal Pagado, decimal Saldo, int? DeudaId, string? AfipIdComprobante,
        bool TieneArchivo, string? Observaciones);
    public record MovDto(DateTime Fecha, string Que, decimal Suma, decimal Resta, decimal Saldo, string? Detalle, int? PagoId);
    public record CuentaDto(int ProveedorId, string Nombre, string? Cuit, bool CuentaCorriente, DateTime? Desde,
        decimal Oficial, decimal NoOficial, decimal ACuenta, decimal Total,
        decimal SaldoInicialOficial, decimal SaldoInicialNoOficial,
        List<DocDto> Pendientes, List<MovDto> Movimientos);
    public record CandidatoDto(int? ProveedorId, string Nombre, string? Cuit, bool YaLleva, int FacturasAfip, DateTime? UltimaFactura);

    public record ActivarRequest(int? ProveedorId, string? Cuit, string? Nombre, DateTime? Desde, decimal? SaldoOficial, decimal? SaldoNoOficial);
    public record SaldoInicialRequest(decimal Oficial, decimal NoOficial);
    public record CotizacionRequest(DateTime Fecha, string? Numero, decimal Importe, string? Observaciones);

    /// <summary>Todos los proveedores con cuenta corriente, los que más se les debe primero.</summary>
    [HttpGet]
    public async Task<IActionResult> Listado()
    {
        var cuentas = await _svc.CalcularAsync();
        return Ok(cuentas.Values
            .OrderByDescending(c => c.Total).ThenBy(c => c.Nombre)
            .Select(c => new ResumenDto(c.ProveedorId, c.Nombre, c.Cuit, c.Desde, c.Oficial, c.NoOficial, c.ACuenta, c.Total, c.Pendientes.Count))
            .ToList());
    }

    [HttpGet("{proveedorId:int}")]
    public async Task<IActionResult> Cuenta(int proveedorId)
    {
        var c = await _svc.GetCuentaAsync(proveedorId);
        if (c is null) return NotFound();
        return Ok(new CuentaDto(c.ProveedorId, c.Nombre, c.Cuit, c.CuentaCorriente, c.Desde,
            c.Oficial, c.NoOficial, c.ACuenta, c.Total,
            c.Docs.Where(d => d.EsSaldoInicial && d.Oficial).Sum(d => d.Total),
            c.Docs.Where(d => d.EsSaldoInicial && !d.Oficial).Sum(d => d.Total),
            c.Pendientes.Select(Map).ToList(),
            ProveedorCtaCteService.Movimientos(c)
                .Select(m => new MovDto(m.Fecha, m.Que, m.Suma, m.Resta, m.Saldo, m.Detalle, m.PagoId)).ToList()));
    }

    private static DocDto Map(ProveedorCtaCteService.Doc d) => new(d.Clave, d.Tipo, d.Numero, d.Oficial, d.EsNotaCredito,
        d.EsSaldoInicial, d.Fecha, d.Total, d.Pagado, d.Saldo, d.DeudaId, d.AfipIdComprobante, d.TieneArchivo, d.Observaciones);

    /// <summary>Para agregar un proveedor a la cuenta corriente: busca en los proveedores cargados y
    /// también en los que mandan facturas por AFIP aunque no estén cargados como proveedor.</summary>
    [HttpGet("buscar")]
    public async Task<IActionResult> Buscar([FromQuery] string? q)
    {
        var t = (q ?? "").Trim();
        if (t.Length < 2) return Ok(new List<CandidatoDto>());
        var dig = ProveedorCtaCteService.NormCuit(t);
        var porCuit = dig.Length >= 3;

        var provs = await _db.CafeProveedores.AsNoTracking()
            .Where(p => p.IsActive && (p.Nombre.Contains(t) || (porCuit && p.Cuit != null && p.Cuit.Contains(dig))))
            .OrderBy(p => p.Nombre).Take(15)
            .Select(p => new { p.Id, p.Nombre, p.Cuit, p.CuentaCorriente })
            .ToListAsync();

        var afip = await _db.ContadoraComprobantes.AsNoTracking()
            .Where(c => c.Naturaleza == "COMPRA" && c.ReceptorDoc != null
                && ((c.ReceptorNombre != null && c.ReceptorNombre.Contains(t)) || (porCuit && c.ReceptorDoc.Contains(dig))))
            .GroupBy(c => c.ReceptorDoc!)
            .Select(g => new { Cuit = g.Key, Nombre = g.Max(x => x.ReceptorNombre), N = g.Count(), Ult = g.Max(x => x.FechaEmision) })
            .OrderByDescending(x => x.Ult).Take(15)
            .ToListAsync();

        // Estadística de AFIP para los proveedores encontrados por nombre.
        var cuitsProv = provs.Where(p => p.Cuit != null).Select(p => p.Cuit!).ToList();
        var statsProv = cuitsProv.Count == 0 ? new() : await _db.ContadoraComprobantes.AsNoTracking()
            .Where(c => c.Naturaleza == "COMPRA" && c.ReceptorDoc != null && cuitsProv.Contains(c.ReceptorDoc))
            .GroupBy(c => c.ReceptorDoc!)
            .Select(g => new { Cuit = g.Key, N = g.Count(), Ult = g.Max(x => x.FechaEmision) })
            .ToListAsync();

        // Los de AFIP que ya existen como proveedor (aunque no hayan salido por nombre).
        var cuitsAfip = afip.Select(a => a.Cuit).ToList();
        var provDeAfip = cuitsAfip.Count == 0 ? new() : await _db.CafeProveedores.AsNoTracking()
            .Where(p => p.Cuit != null && cuitsAfip.Contains(p.Cuit))
            .Select(p => new { p.Id, p.Nombre, p.Cuit, p.CuentaCorriente })
            .ToListAsync();

        var res = new List<CandidatoDto>();
        foreach (var p in provs)
        {
            var st = statsProv.FirstOrDefault(s => s.Cuit == p.Cuit);
            res.Add(new CandidatoDto(p.Id, p.Nombre, p.Cuit, p.CuentaCorriente, st?.N ?? 0, st?.Ult));
        }
        foreach (var a in afip)
        {
            var p = provDeAfip.FirstOrDefault(x => x.Cuit == a.Cuit);
            if (p is not null && res.Any(r => r.ProveedorId == p.Id)) continue;
            res.Add(new CandidatoDto(p?.Id, p?.Nombre ?? a.Nombre ?? a.Cuit, a.Cuit, p?.CuentaCorriente ?? false, a.N, a.Ult));
        }
        return Ok(res);
    }

    /// <summary>Prende la cuenta corriente (y si hace falta crea el proveedor con el nombre y CUIT de
    /// AFIP). De paso deja cargado el saldo inicial.</summary>
    [HttpPost("activar")]
    public async Task<IActionResult> Activar([FromBody] ActivarRequest req)
    {
        int proveedorId;
        if (req.ProveedorId is > 0) proveedorId = req.ProveedorId.Value;
        else
        {
            var cuit = ProveedorCtaCteService.NormCuit(req.Cuit);
            if (cuit.Length == 0) return BadRequest(new { error = "Falta el proveedor." });
            var existente = await _db.CafeProveedores.FirstOrDefaultAsync(p => p.Cuit == cuit);
            if (existente is null)
            {
                if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "Falta el nombre del proveedor." });
                existente = new CafeProveedor { Nombre = req.Nombre.Trim(), Cuit = cuit, IsActive = true, CreatedAt = DateTime.UtcNow };
                _db.CafeProveedores.Add(existente);
                await _db.SaveChangesAsync();
            }
            proveedorId = existente.Id;
        }

        var err = await _svc.ActivarAsync(proveedorId, req.Desde);
        if (err is not null) return BadRequest(new { error = err });
        if (req.SaldoOficial.HasValue)
        {
            err = await _svc.SetSaldoInicialAsync(proveedorId, true, req.SaldoOficial.Value, QuienCarga());
            if (err is not null) return BadRequest(new { error = err });
        }
        if (req.SaldoNoOficial.HasValue)
        {
            err = await _svc.SetSaldoInicialAsync(proveedorId, false, req.SaldoNoOficial.Value, QuienCarga());
            if (err is not null) return BadRequest(new { error = err });
        }
        return Ok(new { proveedorId });
    }

    [HttpPost("{proveedorId:int}/desactivar")]
    public async Task<IActionResult> Desactivar(int proveedorId)
    {
        var err = await _svc.DesactivarAsync(proveedorId);
        return err is null ? Ok(new { ok = true }) : BadRequest(new { error = err });
    }

    [HttpPost("{proveedorId:int}/saldo-inicial")]
    public async Task<IActionResult> SaldoInicial(int proveedorId, [FromBody] SaldoInicialRequest req)
    {
        var err = await _svc.SetSaldoInicialAsync(proveedorId, true, req.Oficial, QuienCarga())
                  ?? await _svc.SetSaldoInicialAsync(proveedorId, false, req.NoOficial, QuienCarga());
        return err is null ? Ok(new { ok = true }) : BadRequest(new { error = err });
    }

    [HttpPost("{proveedorId:int}/cotizaciones")]
    public async Task<IActionResult> CrearCotizacion(int proveedorId, [FromBody] CotizacionRequest req)
    {
        var (err, id) = await _svc.CrearCotizacionAsync(proveedorId, req.Fecha, req.Numero, req.Importe, req.Observaciones, QuienCarga());
        return err is null ? Ok(new { id }) : BadRequest(new { error = err });
    }

    [HttpPost("deudas/{id:int}/anular")]
    public async Task<IActionResult> AnularDeuda(int id)
    {
        var err = await _svc.AnularDeudaAsync(id);
        return err is null ? Ok(new { ok = true }) : BadRequest(new { error = err });
    }

    /// <summary>Foto o PDF de la cotización. Reemplaza el que hubiera.</summary>
    [HttpPost("deudas/{id:int}/archivo")]
    [RequestSizeLimit(25_000_000)]
    public async Task<IActionResult> SubirArchivo(int id, IFormFile archivo)
    {
        var d = await _db.CafeProveedorDeudas.FindAsync(id);
        if (d is null) return NotFound(new { error = "No encontré esa cotización." });
        if (archivo is null || archivo.Length == 0) return BadRequest(new { error = "No llegó ningún archivo." });

        string nombre;
        try { nombre = FileStorageService.SanitizeName(Path.GetFileName(archivo.FileName)); }
        catch { nombre = "archivo"; }
        var rel = $"{CarpetaArchivos}/{d.Id}_{nombre}";
        var full = _storage.ResolveSafe(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using (var fs = System.IO.File.Create(full))
            await archivo.CopyToAsync(fs);

        if (d.ArchivoPath != null && d.ArchivoPath != rel)
        {
            try { System.IO.File.Delete(_storage.ResolveSafe(d.ArchivoPath)); } catch { /* si no está, no importa */ }
        }
        d.ArchivoPath = rel;
        d.ArchivoNombre = nombre.Length > 200 ? nombre[..200] : nombre;
        d.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    [HttpGet("deudas/{id:int}/archivo")]
    public async Task<IActionResult> VerArchivo(int id)
    {
        var d = await _db.CafeProveedorDeudas.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (d?.ArchivoPath is null) return NotFound();
        string full;
        try { full = _storage.ResolveSafe(d.ArchivoPath); } catch { return NotFound(); }
        if (!System.IO.File.Exists(full)) return NotFound();
        var ext = Path.GetExtension(full).ToLowerInvariant();
        var tipo = ext switch
        {
            ".pdf" => "application/pdf",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".heic" => "image/heic",
            _ => "application/octet-stream"
        };
        // Sin nombre de descarga: que el navegador lo muestre (foto o PDF) en la pestaña.
        return PhysicalFile(full, tipo);
    }

    /// <summary>Quién está cargando: el operador elegido en la pantalla (el login es "admin" para todos).</summary>
    private string? QuienCarga()
    {
        var op = Request?.Headers["X-Operator-Name"].FirstOrDefault();
        return string.IsNullOrWhiteSpace(op) ? User?.Identity?.Name : op.Trim();
    }
}
