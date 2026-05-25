using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Carga de stock via link publico, pensado para usar con el celular en el deposito.
/// Flujo: 1) el operador entra al link /stock/{token} (token unico de empresa), 2) elige quien
/// esta cargando (Osmar / Maxi / Ferman / ...), 3) busca por SKU/codigo (ej: C8733 → lista todas
/// las variantes C8733BL, C8733NEG, ...), 4) toca una variante y elige Sumar (default) / Restar
/// (descarte por roturas) / Setear (corregir por inventario), carga cantidad + comentario opcional.
/// El sistema actualiza StockUnidades y guarda un movimiento en Stock_Movimientos para auditoria.
/// </summary>
[ApiController]
[Route("api/stock")]
public class StockController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IServiceScopeFactory _scopeFactory;
    public StockController(AppDbContext db, IServiceScopeFactory scopeFactory) { _db = db; _scopeFactory = scopeFactory; }

    /// <summary>Fire-and-forget push de stock a MeLi tras editar stock desde el endpoint movil.
    /// Mismo patron que CafeProductosController.FireAndForgetPushMeli. Respeta kill switches.</summary>
    private void FireAndForgetPushMeli(int cafeProductoId)
    {
        var sf = _scopeFactory;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = sf.CreateScope();
                var pushSvc = scope.ServiceProvider.GetRequiredService<Api.Services.MeliStockPushService>();
                await pushSvc.PushStockForProductoAsync(cafeProductoId);
            }
            catch { /* error queda en StockChangedAt, lo retoma el background */ }
        });
    }

    // ============================================================
    // ENDPOINTS PUBLICOS (sin auth, por token)
    // ============================================================

    public record StockOperadorPubDto(int Id, string Nombre);
    public record StockProductoPubDto(int Id, string? Sku, string? Barcode, string Nombre,
        string Categoria, string? Marca, int StockActual);
    public record StockInitPubDto(string Token, List<StockOperadorPubDto> Operadores, string DepositoActual);
    public record StockMovimientoPubDto(int Id, DateTime Fecha, string ProductoNombre, string? Sku,
        string Operador, string TipoMov, int Cantidad, int StockAntes, int StockDespues, string? Comentario);

    private async Task<bool> TokenValidoAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var s = await _db.AppSettings.FirstOrDefaultAsync(x => x.Key == "stock.public_token");
        return s?.Value == token;
    }

    /// <summary>Datos iniciales para el celular: lista de operadores activos + nombre del depósito.</summary>
    [HttpGet("publica/{token}/init")]
    [AllowAnonymous]
    public async Task<IActionResult> GetInit(string token)
    {
        if (!await TokenValidoAsync(token)) return NotFound(new { error = "Token inválido" });
        var ops = await _db.StockOperadores
            .Where(o => o.IsActive)
            .OrderBy(o => o.Orden).ThenBy(o => o.Nombre)
            .Select(o => new StockOperadorPubDto(o.Id, o.Nombre))
            .ToListAsync();
        return Ok(new StockInitPubDto(token, ops, "9 de Abril"));
    }

    /// <summary>Busca productos por SKU, barcode o nombre. Devuelve hasta 25 resultados con stock actual.</summary>
    [HttpGet("publica/{token}/search")]
    [AllowAnonymous]
    public async Task<IActionResult> Search(string token, [FromQuery] string q = "")
    {
        if (!await TokenValidoAsync(token)) return NotFound(new { error = "Token inválido" });
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 1)
            return Ok(new List<StockProductoPubDto>());
        var term = q.Trim().ToUpperInvariant();

        // Si parece un código de barras (todo dígito) y matchea exact, devolvemos solo ese.
        if (term.All(char.IsDigit) && term.Length >= 8)
        {
            var exact = await _db.CafeProductos
                .Where(p => p.IsActive && p.Barcode == term)
                .Select(p => new StockProductoPubDto(p.Id, p.Sku, p.Barcode, p.Nombre,
                    p.Categoria, p.Marca, p.StockUnidades))
                .ToListAsync();
            if (exact.Count > 0) return Ok(exact);
        }

        // Sino: búsqueda por SKU (LIKE prefix) + Nombre (LIKE contains) + Barcode (LIKE)
        var q2 = _db.CafeProductos
            .Where(p => p.IsActive)
            .Where(p =>
                (p.Sku != null && p.Sku.ToUpper().Contains(term))
                || (p.Barcode != null && p.Barcode.Contains(term))
                || p.Nombre.ToUpper().Contains(term))
            .OrderBy(p => p.Sku)
            .Take(25)
            .Select(p => new StockProductoPubDto(p.Id, p.Sku, p.Barcode, p.Nombre,
                p.Categoria, p.Marca, p.StockUnidades));
        return Ok(await q2.ToListAsync());
    }

    public class CrearMovimientoRequest
    {
        public int OperadorId { get; set; }
        public int ProductoId { get; set; }
        /// <summary>SUMA | RESTA | SET.</summary>
        public string TipoMov { get; set; } = "SUMA";
        public int Cantidad { get; set; }
        public string? Comentario { get; set; }
    }

    /// <summary>El operador confirma un movimiento. Actualiza StockUnidades + guarda movimiento.</summary>
    [HttpPost("publica/{token}/movimiento")]
    [AllowAnonymous]
    public async Task<IActionResult> CrearMovimiento(string token, [FromBody] CrearMovimientoRequest req)
    {
        if (!await TokenValidoAsync(token)) return NotFound(new { error = "Token inválido" });
        if (req.OperadorId <= 0) return BadRequest(new { error = "Tenés que indicar quién carga" });
        if (req.ProductoId <= 0) return BadRequest(new { error = "Producto inválido" });
        if (req.Cantidad <= 0) return BadRequest(new { error = "Cantidad tiene que ser mayor a 0" });

        var tipo = (req.TipoMov ?? "SUMA").Trim().ToUpperInvariant();
        if (tipo != "SUMA" && tipo != "RESTA" && tipo != "SET")
            return BadRequest(new { error = "Tipo de movimiento inválido (SUMA / RESTA / SET)" });

        var op = await _db.StockOperadores.FindAsync(req.OperadorId);
        if (op is null || !op.IsActive) return BadRequest(new { error = "Operador no encontrado" });

        var prod = await _db.CafeProductos.FindAsync(req.ProductoId);
        if (prod is null || !prod.IsActive) return BadRequest(new { error = "Producto no encontrado" });

        var antes = prod.StockUnidades;
        int despues = tipo switch
        {
            "SUMA" => antes + req.Cantidad,
            "RESTA" => Math.Max(0, antes - req.Cantidad),
            "SET" => req.Cantidad,
            _ => antes
        };
        prod.StockUnidades = despues;
        prod.UpdatedAt = DateTime.UtcNow;
        // Marcar StockChangedAt para que el push event-driven + background lo capturen.
        // Sin esto, MeLi se queda con el stock viejo (bug observado 2026-05-24 con DELLA001).
        if (antes != despues) prod.StockChangedAt = DateTime.UtcNow;
        // Sincronizar Cafe_StockPorDeposito (parche 2026-05-25, ver CafeStockHelper).
        if (antes != despues) await Api.Services.CafeStockHelper.SyncStockPorDepositoAsync(_db, prod);

        var mov = new StockMovimiento
        {
            ProductoId = prod.Id,
            OperadorId = op.Id,
            OperadorNombreSnap = op.Nombre,
            DepositoId = null,
            DepositoNombreSnap = "9 de Abril",
            TipoMov = tipo,
            Cantidad = req.Cantidad,
            StockAntes = antes,
            StockDespues = despues,
            Comentario = string.IsNullOrWhiteSpace(req.Comentario) ? null : req.Comentario.Trim(),
            CreatedAt = DateTime.UtcNow
        };
        _db.StockMovimientos.Add(mov);
        await _db.SaveChangesAsync();

        // Push event-driven a MeLi (fire-and-forget). Si el master kill-switch esta off, no hace nada.
        if (antes != despues) FireAndForgetPushMeli(prod.Id);

        return Ok(new { ok = true, stockAntes = antes, stockDespues = despues, movId = mov.Id });
    }

    /// <summary>Últimos N movimientos cargados por un operador (para que el celular muestre el "feed" reciente).</summary>
    [HttpGet("publica/{token}/ultimos")]
    [AllowAnonymous]
    public async Task<IActionResult> GetUltimos(string token, [FromQuery] int? operadorId = null, [FromQuery] int limit = 20)
    {
        if (!await TokenValidoAsync(token)) return NotFound(new { error = "Token inválido" });
        limit = Math.Clamp(limit, 1, 100);
        var q = _db.StockMovimientos.Include(m => m.Producto).AsQueryable();
        if (operadorId.HasValue) q = q.Where(m => m.OperadorId == operadorId.Value);
        var movs = await q.OrderByDescending(m => m.CreatedAt).Take(limit).ToListAsync();
        var result = movs.Select(m => new StockMovimientoPubDto(
            m.Id, m.CreatedAt, m.Producto?.Nombre ?? "?", m.Producto?.Sku,
            m.OperadorNombreSnap ?? "?", m.TipoMov, m.Cantidad,
            m.StockAntes, m.StockDespues, m.Comentario)).ToList();
        return Ok(result);
    }

    // ============================================================
    // ENDPOINTS ADMIN (con auth)
    // ============================================================

    public record AdminOperadorDto(int Id, string Nombre, bool IsActive, int Orden, int TotalMovimientos);

    [HttpGet("admin/operadores")]
    [Authorize]
    public async Task<IActionResult> ListOperadores()
    {
        var ops = await _db.StockOperadores.OrderBy(o => o.Orden).ThenBy(o => o.Nombre).ToListAsync();
        var counts = await _db.StockMovimientos
            .GroupBy(m => m.OperadorId)
            .Select(g => new { OpId = g.Key, Cant = g.Count() })
            .ToListAsync();
        var dic = counts.ToDictionary(x => x.OpId ?? 0, x => x.Cant);
        return Ok(ops.Select(o => new AdminOperadorDto(
            o.Id, o.Nombre, o.IsActive, o.Orden,
            dic.TryGetValue(o.Id, out var c) ? c : 0)).ToList());
    }

    public class CreateOperadorRequest { public string Nombre { get; set; } = ""; public int Orden { get; set; } }

    [HttpPost("admin/operadores")]
    [Authorize]
    public async Task<IActionResult> CreateOperador([FromBody] CreateOperadorRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "Nombre obligatorio" });
        var op = new StockOperador { Nombre = req.Nombre.Trim(), Orden = req.Orden, IsActive = true, CreatedAt = DateTime.UtcNow };
        _db.StockOperadores.Add(op);
        await _db.SaveChangesAsync();
        return Ok(op);
    }

    public class UpdateOperadorRequest
    {
        public string? Nombre { get; set; }
        public bool? IsActive { get; set; }
        public int? Orden { get; set; }
    }

    [HttpPut("admin/operadores/{id:int}")]
    [Authorize]
    public async Task<IActionResult> UpdateOperador(int id, [FromBody] UpdateOperadorRequest req)
    {
        var op = await _db.StockOperadores.FindAsync(id);
        if (op is null) return NotFound();
        if (req.Nombre is not null && !string.IsNullOrWhiteSpace(req.Nombre)) op.Nombre = req.Nombre.Trim();
        if (req.IsActive.HasValue) op.IsActive = req.IsActive.Value;
        if (req.Orden.HasValue) op.Orden = req.Orden.Value;
        await _db.SaveChangesAsync();
        return Ok(op);
    }

    [HttpDelete("admin/operadores/{id:int}")]
    [Authorize]
    public async Task<IActionResult> DeleteOperador(int id)
    {
        var op = await _db.StockOperadores.FindAsync(id);
        if (op is null) return NotFound();
        // No borramos si tiene movimientos — desactivamos para preservar auditoria.
        var tieneMovs = await _db.StockMovimientos.AnyAsync(m => m.OperadorId == id);
        if (tieneMovs)
        {
            op.IsActive = false;
            await _db.SaveChangesAsync();
            return Ok(new { ok = true, soft = true, msg = "Tenía movimientos, lo desactivé en lugar de borrar." });
        }
        _db.StockOperadores.Remove(op);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    public record AdminMovDto(int Id, DateTime Fecha, int ProductoId, string? Sku, string ProductoNombre,
        string Operador, string TipoMov, int Cantidad, int StockAntes, int StockDespues, string? Comentario);

    [HttpGet("admin/movimientos")]
    [Authorize]
    public async Task<IActionResult> ListMovimientos([FromQuery] DateTime? desde = null,
        [FromQuery] DateTime? hasta = null, [FromQuery] int? operadorId = null,
        [FromQuery] int? productoId = null, [FromQuery] string? tipoMov = null,
        [FromQuery] string? texto = null, [FromQuery] int limit = 200)
    {
        limit = Math.Clamp(limit, 1, 2000);
        var q = _db.StockMovimientos.Include(m => m.Producto).AsQueryable();
        if (desde.HasValue) q = q.Where(m => m.CreatedAt >= desde.Value);
        if (hasta.HasValue) q = q.Where(m => m.CreatedAt <= hasta.Value.AddDays(1));
        if (operadorId.HasValue) q = q.Where(m => m.OperadorId == operadorId.Value);
        if (productoId.HasValue) q = q.Where(m => m.ProductoId == productoId.Value);
        if (!string.IsNullOrWhiteSpace(tipoMov)) q = q.Where(m => m.TipoMov == tipoMov);
        if (!string.IsNullOrWhiteSpace(texto))
        {
            var t = texto.Trim().ToLower();
            q = q.Where(m => (m.Producto != null && (m.Producto.Nombre.ToLower().Contains(t) || (m.Producto.Sku != null && m.Producto.Sku.ToLower().Contains(t))))
                || (m.Comentario != null && m.Comentario.ToLower().Contains(t)));
        }
        var movs = await q.OrderByDescending(m => m.CreatedAt).Take(limit).ToListAsync();
        return Ok(movs.Select(m => new AdminMovDto(m.Id, m.CreatedAt,
            m.ProductoId, m.Producto?.Sku, m.Producto?.Nombre ?? "?",
            m.OperadorNombreSnap ?? "?", m.TipoMov, m.Cantidad,
            m.StockAntes, m.StockDespues, m.Comentario)).ToList());
    }

    /// <summary>Devuelve estadísticas agregadas para el dashboard del historial:
    /// total de movimientos, distribución por tipo (VENTA_MELI vs VENTA_NUESTRA vs AJUSTE, etc.), última fecha.</summary>
    [HttpGet("admin/movimientos/stats")]
    [Authorize]
    public async Task<IActionResult> StatsMovimientos([FromQuery] DateTime? desde = null, [FromQuery] DateTime? hasta = null)
    {
        var q = _db.StockMovimientos.AsQueryable();
        if (desde.HasValue) q = q.Where(m => m.CreatedAt >= desde.Value);
        if (hasta.HasValue) q = q.Where(m => m.CreatedAt <= hasta.Value.AddDays(1));
        var total = await q.CountAsync();
        var porTipo = await q.GroupBy(m => m.TipoMov)
            .Select(g => new { TipoMov = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count).ToListAsync();
        var ultimo = await q.OrderByDescending(m => m.CreatedAt).Select(m => (DateTime?)m.CreatedAt).FirstOrDefaultAsync();
        return Ok(new { total, porTipo, ultimo });
    }

    /// <summary>Devuelve el token publico actual (admin lo necesita para copiar/mandar el link).</summary>
    [HttpGet("admin/token")]
    [Authorize]
    public async Task<IActionResult> GetToken()
    {
        var s = await _db.AppSettings.FirstOrDefaultAsync(x => x.Key == "stock.public_token");
        return Ok(new { token = s?.Value ?? "" });
    }

    /// <summary>Regenera el token publico (invalida el link anterior).</summary>
    [HttpPost("admin/token/regenerar")]
    [Authorize]
    public async Task<IActionResult> RegenerarToken()
    {
        var s = await _db.AppSettings.FirstOrDefaultAsync(x => x.Key == "stock.public_token");
        var nuevo = Guid.NewGuid().ToString("N");
        if (s is null) { _db.AppSettings.Add(new AppSetting { Key = "stock.public_token", Value = nuevo }); }
        else { s.Value = nuevo; }
        await _db.SaveChangesAsync();
        return Ok(new { token = nuevo });
    }
}
