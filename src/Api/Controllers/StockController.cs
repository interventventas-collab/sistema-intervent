using Api.Data;
using Api.Models;
using Api.Services;
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
        string Categoria, string? Marca, int StockActual,
        DateTime? UltimaModifAt, string? UltimaModifOperador, bool YaEnAuditoriaActiva,
        int Reservado = 0);
    public record StockInitPubDto(string Token, List<StockOperadorPubDto> Operadores, string DepositoActual,
        StockAuditoriaPubDto? AuditoriaActiva);
    public record StockMovimientoPubDto(int Id, DateTime Fecha, string ProductoNombre, string? Sku,
        string Operador, string TipoMov, int Cantidad, int StockAntes, int StockDespues, string? Comentario);
    public record StockAuditoriaPubDto(int Id, DateTime IniciadaAt, string? IniciadaPorNombre, int ItemsContados);

    private async Task<bool> TokenValidoAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var s = await _db.AppSettings.FirstOrDefaultAsync(x => x.Key == "stock.public_token");
        return s?.Value == token;
    }

    /// <summary>Datos iniciales para el celular: lista de operadores activos + nombre del depósito + auditoría activa.</summary>
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

        var audAct = await _db.StockAuditorias
            .Where(a => a.CerradaAt == null)
            .OrderByDescending(a => a.IniciadaAt)
            .FirstOrDefaultAsync();
        StockAuditoriaPubDto? audDto = null;
        if (audAct is not null)
        {
            var items = await _db.StockMovimientos
                .Where(m => m.AuditoriaId == audAct.Id && !m.Reverted)
                .Select(m => m.ProductoId).Distinct().CountAsync();
            audDto = new StockAuditoriaPubDto(audAct.Id, audAct.IniciadaAt, audAct.IniciadaPorNombre, items);
        }

        return Ok(new StockInitPubDto(token, ops, "9 de Abril", audDto));
    }

    /// <summary>
    /// 2026-05-28 (Prompt 2 cargador stock móvil): catálogo público para la pantalla móvil V2.
    /// Mismo formato que /api/catalogo-buscador/all pero validando el token de stock público
    /// en lugar de JWT. Devuelve solo productos puros (los combos NO se cargan manualmente).
    /// READ-ONLY: este endpoint no toca stock ni movimientos.
    /// </summary>
    public record CatalogoPubItemDto(
        string Sku, string Nombre, string Tipo, int Id, string? Categoria,
        decimal Stock, bool EsCafe, DateTime? StockChangedAt, string? Marca);

    [HttpGet("publica/{token}/catalogo")]
    [AllowAnonymous]
    public async Task<IActionResult> GetCatalogo(string token, [FromQuery] bool onlyProductos = true)
    {
        if (!await TokenValidoAsync(token)) return NotFound(new { error = "Token inválido" });

        var productos = await _db.CafeProductos
            .AsNoTracking()
            .Where(p => p.IsActive && p.Sku != null && p.Sku != "")
            .Select(p => new CatalogoPubItemDto(
                p.Sku!,
                p.Nombre,
                "producto",
                p.Id,
                p.Categoria,
                p.Categoria == "CAFE" ? p.StockGramos : p.StockUnidades,
                p.Categoria == "CAFE",
                p.StockChangedAt,
                p.Marca))
            .ToListAsync();

        if (onlyProductos)
        {
            return Ok(new { count = productos.Count, items = productos });
        }

        // Por completitud incluimos combos también si alguien pide onlyProductos=false (no es el caso de uso esperado).
        var combos = await _db.CafeCombos
            .AsNoTracking()
            .Where(c => c.IsActive && c.Sku != null)
            .Select(c => new CatalogoPubItemDto(c.Sku!, c.Nombre, "combo", c.Id, c.Categoria, 0m, false, c.UpdatedAt, null))
            .ToListAsync();
        var all = productos.Concat(combos).ToList();
        return Ok(new { count = all.Count, items = all });
    }

    /// <summary>Busca productos. Devuelve hasta 25 con stock actual + última modificación + flag auditoría.</summary>
    [HttpGet("publica/{token}/search")]
    [AllowAnonymous]
    public async Task<IActionResult> Search(string token, [FromQuery] string q = "")
    {
        if (!await TokenValidoAsync(token)) return NotFound(new { error = "Token inválido" });
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 1)
            return Ok(new List<StockProductoPubDto>());

        // Auditoría activa (si hay), para marcar productos ya contados.
        var audActId = await _db.StockAuditorias
            .Where(a => a.CerradaAt == null)
            .OrderByDescending(a => a.IniciadaAt)
            .Select(a => (int?)a.Id).FirstOrDefaultAsync();

        var term = q.Trim().ToUpperInvariant();
        IQueryable<Models.CafeProducto> baseQ;

        if (term.All(char.IsDigit) && term.Length >= 8)
        {
            baseQ = _db.CafeProductos.Where(p => p.IsActive && p.Barcode == term);
            if (!await baseQ.AnyAsync())
                baseQ = _db.CafeProductos.Where(p => p.IsActive && (
                    (p.Sku != null && p.Sku.ToUpper().Contains(term))
                    || (p.Barcode != null && p.Barcode.Contains(term))
                    || p.Nombre.ToUpper().Contains(term)));
        }
        else
        {
            baseQ = _db.CafeProductos.Where(p => p.IsActive && (
                (p.Sku != null && p.Sku.ToUpper().Contains(term))
                || (p.Barcode != null && p.Barcode.Contains(term))
                || p.Nombre.ToUpper().Contains(term)));
        }

        var prods = await baseQ.OrderBy(p => p.Sku).Take(25).ToListAsync();
        var prodIds = prods.Select(p => p.Id).ToList();

        var ultimasModifs = await _db.StockMovimientos
            .Where(m => prodIds.Contains(m.ProductoId) && !m.Reverted)
            .GroupBy(m => m.ProductoId)
            .Select(g => new {
                ProductoId = g.Key,
                Ultima = g.OrderByDescending(m => m.CreatedAt).Select(m => new {
                    m.CreatedAt, m.OperadorNombreSnap
                }).FirstOrDefault()
            })
            .ToListAsync();
        var ultMap = ultimasModifs.ToDictionary(x => x.ProductoId, x => x.Ultima);

        HashSet<int> enAuditoria = new();
        if (audActId.HasValue)
        {
            enAuditoria = (await _db.StockMovimientos
                .Where(m => m.AuditoriaId == audActId.Value && !m.Reverted && prodIds.Contains(m.ProductoId))
                .Select(m => m.ProductoId).Distinct().ToListAsync()).ToHashSet();
        }

        var result = prods.Select(p => new StockProductoPubDto(
            p.Id, p.Sku, p.Barcode, p.Nombre, p.Categoria, p.Marca, p.StockUnidades,
            ultMap.TryGetValue(p.Id, out var um) ? um?.CreatedAt : null,
            ultMap.TryGetValue(p.Id, out var um2) ? um2?.OperadorNombreSnap : null,
            enAuditoria.Contains(p.Id)
        )).ToList();
        return Ok(result);
    }

    /// <summary>2026-06-15: Devuelve el SIGUIENTE producto a auditar ordenado por más vendido
    /// (últimos 30 días, sumando todos los canales: ventas oficina + MeLi + MeLi Full),
    /// saltando los que ya fueron contados en la auditoría activa.
    /// Si auditoriaId viene null, no filtra por "ya contados".
    /// 2026-06-15 v2: parámetro opcional ?marca=... para filtrar por marca (case-insensitive).
    /// "(sin marca)" filtra los que tienen Marca NULL/vacío.</summary>
    [HttpGet("publica/{token}/top-vendidos-siguiente")]
    [AllowAnonymous]
    public async Task<IActionResult> SiguienteTopVendido(string token,
        [FromQuery] int? auditoriaId = null,
        [FromQuery] int dias = 30,
        [FromQuery] string? marca = null)
    {
        if (!await TokenValidoAsync(token)) return NotFound(new { error = "Token inválido" });

        var desde = DateTime.UtcNow.AddDays(-Math.Max(1, dias));

        // Top productos por unidades vendidas en el período (de cualquier canal).
        // Suma absoluta de Cantidad — el sistema guarda valores positivos en VENTA_*.
        // 2026-06-15: EXCLUIR categoría CAFE porque el café se mide en gramos (1 kg = 1000 "unidades")
        // y eso ensucia el orden — los cafés siempre aparecerían arriba. Además no se cuentan físicamente.
        // 2026-06-15 v2: si viene marca, también pre-filtramos ids por marca.
        var idsCafe = await _db.CafeProductos.Where(p => p.Categoria == "CAFE").Select(p => p.Id).ToListAsync();

        HashSet<int>? idsMarca = null;
        var marcaNorm = (marca ?? "").Trim();
        if (!string.IsNullOrEmpty(marcaNorm))
        {
            if (marcaNorm.Equals("(sin marca)", StringComparison.OrdinalIgnoreCase))
            {
                idsMarca = (await _db.CafeProductos
                    .Where(p => p.Marca == null || p.Marca == "")
                    .Select(p => p.Id).ToListAsync()).ToHashSet();
            }
            else
            {
                idsMarca = (await _db.CafeProductos
                    .Where(p => p.Marca != null && p.Marca.ToLower() == marcaNorm.ToLower())
                    .Select(p => p.Id).ToListAsync()).ToHashSet();
            }
        }

        var topVendidos = await _db.StockMovimientos
            .Where(m => !m.Reverted
                     && (m.TipoMov == "VENTA_NUESTRA" || m.TipoMov == "VENTA_MELI" || m.TipoMov == "VENTA_MELI_FULL")
                     && m.CreatedAt >= desde
                     && !idsCafe.Contains(m.ProductoId)
                     && (idsMarca == null || idsMarca.Contains(m.ProductoId)))
            .GroupBy(m => m.ProductoId)
            .Select(g => new { ProductoId = g.Key, Unidades = g.Sum(m => m.Cantidad) })
            .OrderByDescending(x => x.Unidades)
            .Take(500) // tope alto, igual lo va a recorrer en orden y rara vez auditan 500 en un día
            .ToListAsync();

        // IDs ya contados en la auditoría activa (si la hay).
        HashSet<int> yaContados = new();
        if (auditoriaId.HasValue)
        {
            yaContados = (await _db.StockMovimientos
                .Where(m => m.AuditoriaId == auditoriaId.Value && !m.Reverted)
                .Select(m => m.ProductoId).Distinct().ToListAsync()).ToHashSet();
        }

        // Recorrer en orden de más vendido, encontrar el primero NO contado todavía y que siga activo.
        foreach (var t in topVendidos)
        {
            if (yaContados.Contains(t.ProductoId)) continue;
            var p = await _db.CafeProductos.FirstOrDefaultAsync(x => x.Id == t.ProductoId && x.IsActive);
            if (p is null) continue;

            // Buscar última modificación de stock (para mostrar quién y cuándo).
            var ult = await _db.StockMovimientos
                .Where(m => m.ProductoId == p.Id && !m.Reverted)
                .OrderByDescending(m => m.CreatedAt)
                .Select(m => new { m.CreatedAt, m.OperadorNombreSnap })
                .FirstOrDefaultAsync();

            return Ok(new SiguienteTopDto(
                Posicion: topVendidos.IndexOf(t) + 1,
                TotalTop: topVendidos.Count,
                ContadosEnAuditoria: yaContados.Count,
                Producto: new StockProductoPubDto(
                    p.Id, p.Sku, p.Barcode, p.Nombre, p.Categoria, p.Marca, p.StockUnidades,
                    ult?.CreatedAt, ult?.OperadorNombreSnap, false),
                UnidadesVendidasPeriodo: (int)t.Unidades,
                DiasPeriodo: dias));
        }

        // Si llegó hasta acá, terminaron todos los del top — devolvemos 200 con Producto=null.
        return Ok(new SiguienteTopDto(0, topVendidos.Count, yaContados.Count, null, 0, dias));
    }

    public record SiguienteTopDto(int Posicion, int TotalTop, int ContadosEnAuditoria,
        StockProductoPubDto? Producto, int UnidadesVendidasPeriodo, int DiasPeriodo);

    /// <summary>2026-06-15: Lista las marcas con ventas (excluyendo CAFE) en los últimos N días,
    /// para poblar el dropdown del modo Top vendidos. Incluye conteo de unidades vendidas y productos distintos.</summary>
    [HttpGet("publica/{token}/top-vendidos-marcas")]
    [AllowAnonymous]
    public async Task<IActionResult> TopVendidosMarcas(string token, [FromQuery] int dias = 30)
    {
        if (!await TokenValidoAsync(token)) return NotFound(new { error = "Token inválido" });

        var desde = DateTime.UtcNow.AddDays(-Math.Max(1, dias));

        // Productos no-café con ventas en el período
        var ventas = await (
            from m in _db.StockMovimientos
            join p in _db.CafeProductos on m.ProductoId equals p.Id
            where !m.Reverted
               && (m.TipoMov == "VENTA_NUESTRA" || m.TipoMov == "VENTA_MELI" || m.TipoMov == "VENTA_MELI_FULL")
               && m.CreatedAt >= desde
               && (p.Categoria != "CAFE" || p.Categoria == null)
            select new { m.ProductoId, m.Cantidad, Marca = p.Marca }
        ).ToListAsync();

        var grupos = ventas
            .GroupBy(v => string.IsNullOrWhiteSpace(v.Marca) ? "(sin marca)" : v.Marca!.Trim())
            .Select(g => new TopVendidosMarcaDto(
                Marca: g.Key,
                UnidadesVendidas: (int)g.Sum(x => x.Cantidad),
                Productos: g.Select(x => x.ProductoId).Distinct().Count()))
            .OrderByDescending(x => x.UnidadesVendidas)
            .ToList();

        return Ok(grupos);
    }

    public record TopVendidosMarcaDto(string Marca, int UnidadesVendidas, int Productos);

    /// <summary>2026-06-15: devuelve la reserva por etiqueta no impresa de un producto.
    /// Reservado = unidades de ventas MeLi (excluyendo Full) cuyo stock ya se descontó pero
    /// el paquete sigue físicamente en el depósito (etiqueta no impresa en Flex, no retirado en ME1).</summary>
    [HttpGet("publica/{token}/reserva/{productoId:int}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetReservaProducto(string token, int productoId,
        [FromServices] StockReservaService reservaService)
    {
        if (!await TokenValidoAsync(token)) return NotFound(new { error = "Token inválido" });
        var unidades = await reservaService.GetReservaAsync(productoId);
        return Ok(new { productoId, reservado = unidades });
    }


    /// <summary>Lista TODOS los productos activos paginados (para modo auditoría que recorre el depósito).</summary>
    [HttpGet("publica/{token}/all")]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll(string token, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, [FromQuery] string? filter = null)
    {
        if (!await TokenValidoAsync(token)) return NotFound(new { error = "Token inválido" });
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 200);

        var audActId = await _db.StockAuditorias
            .Where(a => a.CerradaAt == null)
            .OrderByDescending(a => a.IniciadaAt)
            .Select(a => (int?)a.Id).FirstOrDefaultAsync();

        IQueryable<Models.CafeProducto> baseQ = _db.CafeProductos.Where(p => p.IsActive);

        // Filtro: "pendientes" (solo los NO modificados en la auditoría activa) / "hechos" (los SÍ modificados) / null = todos
        if (audActId.HasValue && (filter == "pendientes" || filter == "hechos"))
        {
            var enAud = _db.StockMovimientos
                .Where(m => m.AuditoriaId == audActId.Value && !m.Reverted)
                .Select(m => m.ProductoId).Distinct();
            if (filter == "pendientes")
                baseQ = baseQ.Where(p => !enAud.Contains(p.Id));
            else
                baseQ = baseQ.Where(p => enAud.Contains(p.Id));
        }

        var total = await baseQ.CountAsync();
        var prods = await baseQ
            .OrderBy(p => p.Sku)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync();
        var prodIds = prods.Select(p => p.Id).ToList();

        var ultimasModifs = await _db.StockMovimientos
            .Where(m => prodIds.Contains(m.ProductoId) && !m.Reverted)
            .GroupBy(m => m.ProductoId)
            .Select(g => new {
                ProductoId = g.Key,
                Ultima = g.OrderByDescending(m => m.CreatedAt).Select(m => new {
                    m.CreatedAt, m.OperadorNombreSnap
                }).FirstOrDefault()
            })
            .ToListAsync();
        var ultMap = ultimasModifs.ToDictionary(x => x.ProductoId, x => x.Ultima);

        HashSet<int> enAuditoria = new();
        if (audActId.HasValue)
        {
            enAuditoria = (await _db.StockMovimientos
                .Where(m => m.AuditoriaId == audActId.Value && !m.Reverted && prodIds.Contains(m.ProductoId))
                .Select(m => m.ProductoId).Distinct().ToListAsync()).ToHashSet();
        }

        var items = prods.Select(p => new StockProductoPubDto(
            p.Id, p.Sku, p.Barcode, p.Nombre, p.Categoria, p.Marca, p.StockUnidades,
            ultMap.TryGetValue(p.Id, out var um) ? um?.CreatedAt : null,
            ultMap.TryGetValue(p.Id, out var um2) ? um2?.OperadorNombreSnap : null,
            enAuditoria.Contains(p.Id)
        )).ToList();
        return Ok(new { items, total, page, pageSize });
    }

    public class CrearMovimientoRequest
    {
        public int OperadorId { get; set; }
        public int ProductoId { get; set; }
        /// <summary>SUMA | RESTA | SET.</summary>
        public string TipoMov { get; set; } = "SUMA";
        public int Cantidad { get; set; }
        public string? Comentario { get; set; }
        /// <summary>Si viene seteado, el movimiento se asocia a esa sesión de auditoría
        /// (se usa para marcar productos "ya stockeados en esta auditoría"). Null = movimiento normal.</summary>
        public int? AuditoriaId { get; set; }
    }

    /// <summary>El operador confirma un movimiento. Actualiza StockUnidades + guarda movimiento.</summary>
    [HttpPost("publica/{token}/movimiento")]
    [AllowAnonymous]
    public async Task<IActionResult> CrearMovimiento(string token, [FromBody] CrearMovimientoRequest req)
    {
        if (!await TokenValidoAsync(token)) return NotFound(new { error = "Token inválido" });
        if (req.OperadorId <= 0) return BadRequest(new { error = "Tenés que indicar quién carga" });
        if (req.ProductoId <= 0) return BadRequest(new { error = "Producto inválido" });
        if (req.Cantidad < 0) return BadRequest(new { error = "Cantidad inválida" });

        var tipo = (req.TipoMov ?? "SUMA").Trim().ToUpperInvariant();
        if (tipo != "SUMA" && tipo != "RESTA" && tipo != "SET")
            return BadRequest(new { error = "Tipo de movimiento inválido (SUMA / RESTA / SET)" });
        // 2026-06-15: cantidad 0 solo permitida en SET (confirmar stock=0). En SUMA/RESTA no tiene sentido.
        if (req.Cantidad == 0 && tipo != "SET")
            return BadRequest(new { error = "Cantidad tiene que ser mayor a 0" });

        var op = await _db.StockOperadores.FindAsync(req.OperadorId);
        if (op is null || !op.IsActive) return BadRequest(new { error = "Operador no encontrado" });

        var prod = await _db.CafeProductos.FindAsync(req.ProductoId);
        if (prod is null || !prod.IsActive) return BadRequest(new { error = "Producto no encontrado" });

        // Validar AuditoriaId si vino: tiene que existir y estar abierta.
        int? auditoriaIdValida = null;
        if (req.AuditoriaId.HasValue && req.AuditoriaId.Value > 0)
        {
            var aud = await _db.StockAuditorias.FindAsync(req.AuditoriaId.Value);
            if (aud is null) return BadRequest(new { error = "Auditoría no encontrada" });
            if (aud.CerradaAt.HasValue) return BadRequest(new { error = "La auditoría ya fue cerrada" });
            auditoriaIdValida = aud.Id;
        }

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
            CreatedAt = DateTime.UtcNow,
            AuditoriaId = auditoriaIdValida
        };
        _db.StockMovimientos.Add(mov);
        await _db.SaveChangesAsync();

        // Push event-driven a MeLi (fire-and-forget). Si el master kill-switch esta off, no hace nada.
        if (antes != despues) FireAndForgetPushMeli(prod.Id);

        return Ok(new { ok = true, stockAntes = antes, stockDespues = despues, movId = mov.Id });
    }

    // ============================================================
    // AUDITORIA (sesion de conteo masivo desde el celu)
    // ============================================================

    public class IniciarAuditoriaRequest
    {
        public int OperadorId { get; set; }
        public string? Notas { get; set; }
    }

    /// <summary>Inicia una sesión de auditoría. Si ya hay una abierta, devuelve esa (no duplica).</summary>
    [HttpPost("publica/{token}/auditoria/iniciar")]
    [AllowAnonymous]
    public async Task<IActionResult> IniciarAuditoria(string token, [FromBody] IniciarAuditoriaRequest req)
    {
        if (!await TokenValidoAsync(token)) return NotFound(new { error = "Token inválido" });
        if (req.OperadorId <= 0) return BadRequest(new { error = "Falta indicar el operador" });

        var op = await _db.StockOperadores.FindAsync(req.OperadorId);
        if (op is null || !op.IsActive) return BadRequest(new { error = "Operador no encontrado" });

        // Si ya hay una abierta, devolverla (lock simple — no permitimos dos en simultáneo).
        var existente = await _db.StockAuditorias
            .Where(a => a.CerradaAt == null)
            .OrderByDescending(a => a.IniciadaAt)
            .FirstOrDefaultAsync();
        if (existente is not null)
        {
            var itemsExist = await _db.StockMovimientos
                .Where(m => m.AuditoriaId == existente.Id && !m.Reverted)
                .Select(m => m.ProductoId).Distinct().CountAsync();
            return Ok(new StockAuditoriaPubDto(existente.Id, existente.IniciadaAt, existente.IniciadaPorNombre, itemsExist));
        }

        var aud = new StockAuditoria
        {
            IniciadaAt = DateTime.UtcNow,
            IniciadaPorOperadorId = op.Id,
            IniciadaPorNombre = op.Nombre,
            Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim()
        };
        _db.StockAuditorias.Add(aud);
        await _db.SaveChangesAsync();
        return Ok(new StockAuditoriaPubDto(aud.Id, aud.IniciadaAt, aud.IniciadaPorNombre, 0));
    }

    public class CerrarAuditoriaRequest
    {
        public int? OperadorId { get; set; }
        public string? Notas { get; set; }
    }

    /// <summary>Cierra la auditoría indicada (setea CerradaAt). Idempotente: si ya está cerrada, devuelve OK igual.</summary>
    [HttpPost("publica/{token}/auditoria/{id:int}/cerrar")]
    [AllowAnonymous]
    public async Task<IActionResult> CerrarAuditoria(string token, int id, [FromBody] CerrarAuditoriaRequest req)
    {
        if (!await TokenValidoAsync(token)) return NotFound(new { error = "Token inválido" });
        var aud = await _db.StockAuditorias.FindAsync(id);
        if (aud is null) return NotFound(new { error = "Auditoría no encontrada" });
        if (aud.CerradaAt.HasValue) return Ok(new { ok = true, alreadyClosed = true });

        string? nombreCerro = null;
        if (req?.OperadorId.HasValue == true && req.OperadorId.Value > 0)
        {
            var op = await _db.StockOperadores.FindAsync(req.OperadorId.Value);
            nombreCerro = op?.Nombre;
        }
        aud.CerradaAt = DateTime.UtcNow;
        aud.CerradaPorNombre = nombreCerro;
        if (req != null && !string.IsNullOrWhiteSpace(req.Notas))
            aud.Notas = (aud.Notas is null ? "" : aud.Notas + " | ") + req.Notas.Trim();
        await _db.SaveChangesAsync();

        var items = await _db.StockMovimientos
            .Where(m => m.AuditoriaId == aud.Id && !m.Reverted)
            .Select(m => m.ProductoId).Distinct().CountAsync();
        return Ok(new { ok = true, id = aud.Id, items, cerradaAt = aud.CerradaAt });
    }

    /// <summary>2026-06-15: Endpoint admin (con auth) para cerrar la auditoría actualmente abierta
    /// desde el dashboard. El operario ya no tiene botón cerrar en el celular — solo admin desde acá.</summary>
    [HttpPost("auditoria-actual/cerrar")]
    [Authorize]
    public async Task<IActionResult> CerrarAuditoriaActual()
    {
        var aud = await _db.StockAuditorias
            .Where(a => a.CerradaAt == null)
            .OrderByDescending(a => a.IniciadaAt)
            .FirstOrDefaultAsync();
        if (aud is null) return Ok(new { ok = true, message = "No hay auditoría abierta" });

        var nombre = User?.Identity?.Name ?? "Admin";
        aud.CerradaAt = DateTime.UtcNow;
        aud.CerradaPorNombre = nombre;
        await _db.SaveChangesAsync();

        var items = await _db.StockMovimientos
            .Where(m => m.AuditoriaId == aud.Id && !m.Reverted)
            .Select(m => m.ProductoId).Distinct().CountAsync();
        return Ok(new { ok = true, id = aud.Id, items, cerradaAt = aud.CerradaAt });
    }

    public class BatchMovimientoItem
    {
        public int ProductoId { get; set; }
        public string TipoMov { get; set; } = "SET";
        public int Cantidad { get; set; }
        public string? Comentario { get; set; }
    }

    public class BatchMovimientoRequest
    {
        public int OperadorId { get; set; }
        public int? AuditoriaId { get; set; }
        public List<BatchMovimientoItem> Items { get; set; } = new();
    }

    /// <summary>Aplica varios movimientos juntos (modo "confirmar varios items que conté en el pasillo").
    /// Recorre uno a uno y devuelve un resumen con éxitos / errores por item — NO hace rollback global.</summary>
    [HttpPost("publica/{token}/movimientos-batch")]
    [AllowAnonymous]
    public async Task<IActionResult> CrearMovimientosBatch(string token, [FromBody] BatchMovimientoRequest req)
    {
        if (!await TokenValidoAsync(token)) return NotFound(new { error = "Token inválido" });
        if (req.OperadorId <= 0) return BadRequest(new { error = "Tenés que indicar quién carga" });
        if (req.Items is null || req.Items.Count == 0) return BadRequest(new { error = "No mandaste items" });
        if (req.Items.Count > 200) return BadRequest(new { error = "Demasiados items en un batch (máx 200)" });

        var op = await _db.StockOperadores.FindAsync(req.OperadorId);
        if (op is null || !op.IsActive) return BadRequest(new { error = "Operador no encontrado" });

        int? auditoriaIdValida = null;
        if (req.AuditoriaId.HasValue && req.AuditoriaId.Value > 0)
        {
            var aud = await _db.StockAuditorias.FindAsync(req.AuditoriaId.Value);
            if (aud is null) return BadRequest(new { error = "Auditoría no encontrada" });
            if (aud.CerradaAt.HasValue) return BadRequest(new { error = "La auditoría ya fue cerrada" });
            auditoriaIdValida = aud.Id;
        }

        var prodIds = req.Items.Select(i => i.ProductoId).Distinct().ToList();
        var prods = await _db.CafeProductos.Where(p => prodIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

        var okList = new List<object>();
        var errList = new List<object>();
        var pushIds = new HashSet<int>();

        foreach (var it in req.Items)
        {
            try
            {
                var tipo = (it.TipoMov ?? "SET").Trim().ToUpperInvariant();
                if (tipo != "SUMA" && tipo != "RESTA" && tipo != "SET")
                {
                    errList.Add(new { it.ProductoId, error = "Tipo inválido" });
                    continue;
                }
                if (it.Cantidad <= 0 && tipo != "SET")
                {
                    errList.Add(new { it.ProductoId, error = "Cantidad debe ser > 0" });
                    continue;
                }
                if (it.Cantidad < 0)
                {
                    errList.Add(new { it.ProductoId, error = "Cantidad no puede ser negativa" });
                    continue;
                }
                if (!prods.TryGetValue(it.ProductoId, out var prod) || !prod.IsActive)
                {
                    errList.Add(new { it.ProductoId, error = "Producto no encontrado" });
                    continue;
                }

                var antes = prod.StockUnidades;
                int despues = tipo switch
                {
                    "SUMA" => antes + it.Cantidad,
                    "RESTA" => Math.Max(0, antes - it.Cantidad),
                    "SET" => it.Cantidad,
                    _ => antes
                };
                prod.StockUnidades = despues;
                prod.UpdatedAt = DateTime.UtcNow;
                if (antes != despues)
                {
                    prod.StockChangedAt = DateTime.UtcNow;
                    await Api.Services.CafeStockHelper.SyncStockPorDepositoAsync(_db, prod);
                    pushIds.Add(prod.Id);
                }

                var mov = new StockMovimiento
                {
                    ProductoId = prod.Id,
                    OperadorId = op.Id,
                    OperadorNombreSnap = op.Nombre,
                    DepositoId = null,
                    DepositoNombreSnap = "9 de Abril",
                    TipoMov = tipo,
                    Cantidad = it.Cantidad,
                    StockAntes = antes,
                    StockDespues = despues,
                    Comentario = string.IsNullOrWhiteSpace(it.Comentario) ? null : it.Comentario.Trim(),
                    CreatedAt = DateTime.UtcNow,
                    AuditoriaId = auditoriaIdValida
                };
                _db.StockMovimientos.Add(mov);
                okList.Add(new { it.ProductoId, stockAntes = antes, stockDespues = despues });
            }
            catch (Exception ex)
            {
                errList.Add(new { it.ProductoId, error = ex.Message });
            }
        }

        await _db.SaveChangesAsync();
        // Push a MeLi tras guardar todo, una vez por producto.
        foreach (var pid in pushIds) FireAndForgetPushMeli(pid);

        return Ok(new {
            ok = errList.Count == 0,
            aplicados = okList.Count,
            errores = errList.Count,
            detalleOk = okList,
            detalleErr = errList
        });
    }

    public class DeshacerUltimoRequest
    {
        public int OperadorId { get; set; }
        /// <summary>Si viene, solo deshace el último DE ESA auditoría (no toca movimientos sueltos previos).</summary>
        public int? AuditoriaId { get; set; }
    }

    /// <summary>Deshace el último movimiento NO revertido (del operador, opcionalmente filtrado por auditoría).
    /// Genera un movimiento inverso + marca el original como Reverted=true. NO borra historial.</summary>
    [HttpPost("publica/{token}/deshacer-ultimo")]
    [AllowAnonymous]
    public async Task<IActionResult> DeshacerUltimo(string token, [FromBody] DeshacerUltimoRequest req)
    {
        if (!await TokenValidoAsync(token)) return NotFound(new { error = "Token inválido" });
        if (req.OperadorId <= 0) return BadRequest(new { error = "Falta operador" });

        var op = await _db.StockOperadores.FindAsync(req.OperadorId);
        if (op is null || !op.IsActive) return BadRequest(new { error = "Operador no encontrado" });

        var q = _db.StockMovimientos.Where(m => !m.Reverted && m.OperadorId == op.Id);
        if (req.AuditoriaId.HasValue) q = q.Where(m => m.AuditoriaId == req.AuditoriaId.Value);

        var ult = await q.OrderByDescending(m => m.CreatedAt).FirstOrDefaultAsync();
        if (ult is null) return BadRequest(new { error = "No hay nada para deshacer" });

        var prod = await _db.CafeProductos.FindAsync(ult.ProductoId);
        if (prod is null) return BadRequest(new { error = "Producto del último movimiento no existe" });

        // Restaurar al StockAntes original (no recalculamos: usamos el snapshot que ya está guardado).
        var antes = prod.StockUnidades;
        var despues = ult.StockAntes;
        prod.StockUnidades = despues;
        prod.UpdatedAt = DateTime.UtcNow;
        if (antes != despues)
        {
            prod.StockChangedAt = DateTime.UtcNow;
            await Api.Services.CafeStockHelper.SyncStockPorDepositoAsync(_db, prod);
        }

        // Marcar el original como revertido para que no cuente en "ya stockeados en auditoría".
        ult.Reverted = true;

        // Crear un movimiento inverso para que quede el historial visible.
        var inverso = new StockMovimiento
        {
            ProductoId = prod.Id,
            OperadorId = op.Id,
            OperadorNombreSnap = op.Nombre,
            DepositoId = null,
            DepositoNombreSnap = "9 de Abril",
            TipoMov = "SET",
            Cantidad = despues,
            StockAntes = antes,
            StockDespues = despues,
            Comentario = $"Deshacer último (mov #{ult.Id})",
            CreatedAt = DateTime.UtcNow,
            AuditoriaId = ult.AuditoriaId,
            // Tambien marcamos el inverso como Reverted para que NO cuente como "stockeado en auditoría".
            Reverted = true
        };
        _db.StockMovimientos.Add(inverso);
        await _db.SaveChangesAsync();

        if (antes != despues) FireAndForgetPushMeli(prod.Id);

        return Ok(new { ok = true, movRevertedId = ult.Id, productoId = prod.Id, stockAntes = antes, stockDespues = despues });
    }

    /// <summary>Lista las últimas N auditorías (cerradas + abierta si la hay) para mostrar historial.</summary>
    [HttpGet("publica/{token}/auditorias")]
    [AllowAnonymous]
    public async Task<IActionResult> ListAuditoriasPub(string token, [FromQuery] int limit = 20)
    {
        if (!await TokenValidoAsync(token)) return NotFound(new { error = "Token inválido" });
        limit = Math.Clamp(limit, 1, 100);
        var auds = await _db.StockAuditorias
            .OrderByDescending(a => a.IniciadaAt)
            .Take(limit).ToListAsync();
        var ids = auds.Select(a => a.Id).ToList();
        var counts = await _db.StockMovimientos
            .Where(m => m.AuditoriaId != null && ids.Contains(m.AuditoriaId.Value) && !m.Reverted)
            .GroupBy(m => m.AuditoriaId!.Value)
            .Select(g => new { Id = g.Key, Items = g.Select(x => x.ProductoId).Distinct().Count() })
            .ToListAsync();
        var dic = counts.ToDictionary(x => x.Id, x => x.Items);
        var result = auds.Select(a => new {
            a.Id,
            a.IniciadaAt,
            a.IniciadaPorNombre,
            a.CerradaAt,
            a.CerradaPorNombre,
            a.Notas,
            Items = dic.TryGetValue(a.Id, out var c) ? c : 0
        }).ToList();
        return Ok(result);
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

    public record AdminAuditoriaDto(int Id, DateTime IniciadaAt, string? IniciadaPorNombre,
        DateTime? CerradaAt, string? CerradaPorNombre, string? Notas, int ItemsContados,
        int MovimientosTotales);

    /// <summary>Lista de auditorías (admin) con conteo de movimientos y productos únicos contados.</summary>
    [HttpGet("admin/auditorias")]
    [Authorize]
    public async Task<IActionResult> ListAuditoriasAdmin([FromQuery] int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 500);
        var auds = await _db.StockAuditorias
            .OrderByDescending(a => a.IniciadaAt)
            .Take(limit).ToListAsync();
        var ids = auds.Select(a => a.Id).ToList();
        var movs = await _db.StockMovimientos
            .Where(m => m.AuditoriaId != null && ids.Contains(m.AuditoriaId.Value))
            .Select(m => new { m.AuditoriaId, m.ProductoId, m.Reverted })
            .ToListAsync();
        var byAud = movs.GroupBy(m => m.AuditoriaId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var result = auds.Select(a =>
        {
            var list = byAud.TryGetValue(a.Id, out var l) ? l : new();
            var items = list.Where(x => !x.Reverted).Select(x => x.ProductoId).Distinct().Count();
            var totalMovs = list.Count(x => !x.Reverted);
            return new AdminAuditoriaDto(a.Id, a.IniciadaAt, a.IniciadaPorNombre,
                a.CerradaAt, a.CerradaPorNombre, a.Notas, items, totalMovs);
        }).ToList();
        return Ok(result);
    }

    /// <summary>Detalle de una auditoría (admin): lista de movimientos con producto + operador.</summary>
    [HttpGet("admin/auditorias/{id:int}")]
    [Authorize]
    public async Task<IActionResult> GetAuditoriaAdmin(int id)
    {
        var aud = await _db.StockAuditorias.FindAsync(id);
        if (aud is null) return NotFound();
        var movs = await _db.StockMovimientos
            .Include(m => m.Producto)
            .Where(m => m.AuditoriaId == id)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();
        return Ok(new {
            aud.Id, aud.IniciadaAt, aud.IniciadaPorNombre, aud.CerradaAt, aud.CerradaPorNombre, aud.Notas,
            movimientos = movs.Select(m => new AdminMovDto(m.Id, m.CreatedAt, m.ProductoId, m.Producto?.Sku,
                m.Producto?.Nombre ?? "?", m.OperadorNombreSnap ?? "?", m.TipoMov, m.Cantidad,
                m.StockAntes, m.StockDespues, m.Comentario)).ToList()
        });
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
