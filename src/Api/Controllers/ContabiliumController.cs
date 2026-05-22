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

    private static int _importRunning = 0;
    private static string? _importLastError;
    private static DateTime? _importStartedAt;
    private static DateTime? _importFinishedAt;
    private static object? _importLastResult;

    /// <summary>Dispara el import EN BACKGROUND y devuelve inmediato. El job corre desacoplado del
    /// HTTP request (asi no se aborta cuando el browser hace timeout a los 100s). Para ver el estado
    /// usar GET /api/contabilium/import/status.</summary>
    [HttpPost("import")]
    public IActionResult RunImport([FromServices] IServiceScopeFactory scopeFactory)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _importRunning, 1, 0) != 0)
            return Ok(new { ok = true, started = false, message = "Ya hay un import corriendo. Esperá a que termine." });

        _importLastError = null;
        _importStartedAt = DateTime.UtcNow;
        _importFinishedAt = null;
        _importLastResult = null;

        // Lanzamos el job sin await — corre desacoplado del HTTP request.
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var import = scope.ServiceProvider.GetRequiredService<ContabiliumImportService>();
                var r = await import.RunFullImportAsync(CancellationToken.None);
                _importLastResult = new {
                    creados = r.ProductosCreados,
                    actualizados = r.ProductosActualizados,
                    componentes = r.ComponentesLinkeados,
                    itemsConCombo = r.ItemsConCombo,
                    itemsDirectos = r.ItemsDirectos,
                    itemsSinMatch = r.ItemsSinMatch,
                    warningsCount = r.Warnings.Count
                };
            }
            catch (Exception ex)
            {
                _importLastError = ex.Message;
            }
            finally
            {
                _importFinishedAt = DateTime.UtcNow;
                System.Threading.Interlocked.Exchange(ref _importRunning, 0);
            }
        });

        return Ok(new { ok = true, started = true, message = "Import iniciado en background. Podes cerrar la pestaña. Toca 'Ver estado' o entra a /cafe/stock-comparado cuando termine." });
    }

    [HttpGet("import/status")]
    public IActionResult ImportStatus()
    {
        var running = _importRunning != 0;
        return Ok(new {
            running,
            startedAt = _importStartedAt,
            finishedAt = _importFinishedAt,
            lastError = _importLastError,
            result = _importLastResult
        });
    }

    // ============================================================
    // Clone Contabilium (2026-05-22)
    // Importa productos + combos completos + relinkea MeliItemComponentes con variation_id.
    // ============================================================

    private static int _cloneRunning = 0;
    private static string? _cloneLastError;
    private static DateTime? _cloneStartedAt;
    private static DateTime? _cloneFinishedAt;
    private static object? _cloneLastResult;

    /// <summary>Dispara el clone EN BACKGROUND y devuelve inmediato. Para ver el estado:
    /// GET /api/contabilium/clone/status.</summary>
    [HttpPost("clone")]
    public IActionResult RunClone([FromServices] IServiceScopeFactory scopeFactory)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _cloneRunning, 1, 0) != 0)
            return Ok(new { ok = true, started = false, message = "Ya hay un clone corriendo. Espera a que termine." });

        _cloneLastError = null;
        _cloneStartedAt = DateTime.UtcNow;
        _cloneFinishedAt = null;
        _cloneLastResult = null;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var clone = scope.ServiceProvider.GetRequiredService<CloneContabiliumService>();
                var r = await clone.RunCloneAsync(CancellationToken.None);
                _cloneLastResult = new {
                    productosCreados = r.ProductosCreados,
                    productosActualizados = r.ProductosActualizados,
                    combosCreados = r.CombosCreados,
                    combosActualizados = r.CombosActualizados,
                    mappingsCreados = r.MappingsCreados,
                    itemsConVariante = r.ItemsConVariante,
                    itemsSinMatch = r.ItemsSinMatch,
                    warningsCount = r.Warnings.Count,
                    warningsSample = r.Warnings.Take(20).ToList()
                };
            }
            catch (Exception ex)
            {
                _cloneLastError = ex.Message;
            }
            finally
            {
                _cloneFinishedAt = DateTime.UtcNow;
                System.Threading.Interlocked.Exchange(ref _cloneRunning, 0);
            }
        });

        return Ok(new { ok = true, started = true, message = "Clone iniciado en background." });
    }

    [HttpGet("clone/status")]
    public IActionResult CloneStatus()
    {
        var running = _cloneRunning != 0;
        return Ok(new {
            running,
            startedAt = _cloneStartedAt,
            finishedAt = _cloneFinishedAt,
            lastError = _cloneLastError,
            result = _cloneLastResult
        });
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
