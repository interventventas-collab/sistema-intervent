using Api.Data;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/contabilium")]
[Authorize]
public class ContabiliumController : ControllerBase
{
    private readonly ContabiliumService _svc;
    private readonly ContabiliumImportService _import;
    private readonly AppDbContext _db;

    public ContabiliumController(ContabiliumService svc, ContabiliumImportService import, AppDbContext db)
    {
        _svc = svc;
        _import = import;
        _db = db;
    }

    public record StatusDto(bool Connected, string? Email, DateTime? LastSyncAt, int? LastSyncCount, string? LastSyncError);

    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var acc = await _svc.GetAccountAsync();
        if (acc is null) return Ok(new StatusDto(false, null, null, null, null));
        return Ok(new StatusDto(true, acc.Email, acc.LastSyncAt, acc.LastSyncCount, acc.LastSyncError));
    }

    public class ConnectRequest
    {
        public string Email { get; set; } = "";
        public string ApiKey { get; set; } = "";
    }

    /// <summary>Guarda email + apikey y valida la conexion contra Contabilium.</summary>
    [HttpPost("connect")]
    public async Task<IActionResult> Connect([FromBody] ConnectRequest req)
    {
        var (ok, err) = await _svc.ConnectAsync(req.Email, req.ApiKey);
        if (!ok) return BadRequest(new { error = err });
        return Ok(new { ok = true });
    }

    /// <summary>Test simple: pide los primeros 2 conceptos para confirmar que sigue conectado.</summary>
    [HttpGet("ping")]
    public async Task<IActionResult> Ping()
    {
        var page = await _svc.ListConceptosAsync(page: 1, pageSize: 2);
        if (page is null) return BadRequest(new { error = "No se pudo conectar." });
        return Ok(new { ok = true, total = page.TotalItems, sample = page.Items.Take(2) });
    }

    /// <summary>Corre el import completo: trae productos+combos+stock desde Contabilium y
    /// pobla CafeProductos + MeliItemComponentes. Tarda ~5-10 min porque pagina y baja detalle de combos.</summary>
    [HttpPost("import")]
    public async Task<IActionResult> RunImport(CancellationToken ct)
    {
        try
        {
            var r = await _import.RunFullImportAsync(ct);
            return Ok(new {
                ok = true,
                creados = r.ProductosCreados,
                actualizados = r.ProductosActualizados,
                componentes = r.ComponentesLinkeados,
                itemsConCombo = r.ItemsConCombo,
                itemsDirectos = r.ItemsDirectos,
                itemsSinMatch = r.ItemsSinMatch,
                warnings = r.Warnings
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    public record StockComparadoRow(string Sku, string? Nombre, decimal StockSistema, decimal? StockContabilium, decimal? Diferencia, DateTime? FechaSnapshot);

    /// <summary>Pantalla comparador: para cada producto importado (no café), muestra el stock acá
    /// y el último snapshot de Contabilium para detectar diferencias.</summary>
    [HttpGet("stock-comparado")]
    public async Task<IActionResult> StockComparado([FromQuery] string? filtro = null, [FromQuery] bool soloDiferencias = false)
    {
        // Productos OTROS (no café) en sistema
        var prodsQ = _db.CafeProductos.Where(p => p.Categoria == "OTROS" && p.Sku != null && p.IsActive);
        if (!string.IsNullOrWhiteSpace(filtro))
        {
            var f = filtro.Trim().ToUpper();
            prodsQ = prodsQ.Where(p => p.Sku!.Contains(f) || p.Nombre.Contains(f));
        }
        var prods = await prodsQ.OrderBy(p => p.Sku).Take(1000).ToListAsync();
        var skus = prods.Select(p => p.Sku!.ToUpper()).ToList();

        // Último snapshot por SKU
        var snaps = await _db.StockSnapshots
            .Where(s => skus.Contains(s.Sku))
            .GroupBy(s => s.Sku)
            .Select(g => g.OrderByDescending(s => s.Fecha).First())
            .ToListAsync();
        var snapsBySku = snaps.ToDictionary(s => s.Sku, StringComparer.OrdinalIgnoreCase);

        var rows = new List<StockComparadoRow>();
        foreach (var p in prods)
        {
            var sku = p.Sku!.ToUpper();
            var stockSis = (decimal)p.StockUnidades;
            decimal? stockCont = null;
            DateTime? fecha = null;
            if (snapsBySku.TryGetValue(sku, out var snap))
            {
                stockCont = snap.StockContabilium;
                fecha = snap.Fecha;
            }
            var diff = stockCont.HasValue ? (decimal?)(stockSis - stockCont.Value) : null;
            if (soloDiferencias && (!diff.HasValue || diff.Value == 0)) continue;
            rows.Add(new StockComparadoRow(p.Sku!, p.Nombre, stockSis, stockCont, diff, fecha));
        }
        return Ok(new { rows, total = rows.Count, lastSnapshotAt = snaps.Count > 0 ? snaps.Max(s => s.Fecha) : (DateTime?)null });
    }
}
