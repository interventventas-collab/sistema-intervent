using System.Globalization;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-11-27: "🧭 Recorrer estantes" del celu del depósito (/depo/recorrer). Sin usuario: clave del
/// día (ClaveDiaCeluService) y eligen quién es (operador de stock, como Cargar stock). Se recorre
/// calle → rack → altura (de abajo hacia arriba); en cada estante se ubica lo que hay y se CUENTA
/// (siempre): si lo contado no coincide con el sistema, se corrige el stock con un movimiento SET del
/// operador (igual que la carga de stock del celu). Si el producto está en varios estantes, el stock
/// es la SUMA de lo contado en cada uno (y se corrige recién cuando están todos contados).
/// "📦 Todo el stock acá" ubica sin contar: toma el stock del sistema y no lo toca.
/// Un estante queda "revisado" con una revisión posterior a "deposito.recorrido.desde" (AppSettings).
/// </summary>
[ApiController]
[Route("api/public/depo-recorrido")]
[AllowAnonymous]
public class DepoRecorridoController : ControllerBase
{
    private const string CLAVE_DESDE = "deposito.recorrido.desde";
    private const string COLLATE_SIN_TILDES = "SQL_Latin1_General_CP1_CI_AI";
    private readonly AppDbContext _db;
    private readonly ClaveDiaCeluService _clave;
    private readonly IServiceScopeFactory _scopeFactory;

    public DepoRecorridoController(AppDbContext db, ClaveDiaCeluService clave, IServiceScopeFactory scopeFactory)
    {
        _db = db;
        _clave = clave;
        _scopeFactory = scopeFactory;
    }

    private ObjectResult PaseVencido() => StatusCode(403, new { error = "La clave cambió. Pedí la de hoy." });

    private async Task<DateTime> DesdeAsync()
    {
        var v = await _db.AppSettings.AsNoTracking().Where(x => x.Key == CLAVE_DESDE).Select(x => x.Value).FirstOrDefaultAsync();
        return DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : DateTime.MinValue;
    }

    // Un lado de un rack que da a una calle. Orden dentro de la calle: desde la entrada (Y) y por fila.
    private record Lado(DepositoPlanoElemento Rack, int Cara, int Calle, int Orden);

    private async Task<List<Lado>> LadosAsync()
    {
        var racks = await _db.DepositoPlanoElementos.AsNoTracking()
            .Where(e => e.Tipo == "rack" && (e.Calle1 != null || e.Calle2 != null || e.Forma == "estanteria")).ToListAsync();
        var l = new List<(DepositoPlanoElemento R, int Cara, int Calle)>();
        foreach (var r in racks)
        {
            // 2026-11-27: las estanterías sueltas (tostadero, escritorio) van juntas como "calle 0"
            if (DepositoPlanoController.EsEstanteria(r)) { l.Add((r, 1, 0)); continue; }
            if (r.Calle1 is int c1) l.Add((r, 1, c1));
            if (r.Calle2 is int c2) l.Add((r, 2, c2));
        }
        return l.GroupBy(x => x.Calle).OrderBy(g => g.Key)
            .SelectMany(g => g.OrderBy(x => x.R.Y).ThenBy(x => x.R.Fila).ThenBy(x => x.R.NroFila)
                .Select((x, i) => new Lado(x.R, x.Cara, x.Calle, i + 1)))
            .ToList();
    }

    /// <summary>(rack, cara, pos, nivel) de los estantes revisados en este recorrido.</summary>
    private async Task<HashSet<(int, int, int, int)>> RevisadosAsync(DateTime desde)
        => (await _db.DepositoEstanteRevisiones.AsNoTracking().Where(r => r.At >= desde)
                .Select(r => new { r.ElementoId, r.Cara, r.Posicion, r.Nivel }).Distinct().ToListAsync())
            .Select(r => (r.ElementoId, r.Cara, r.Posicion, r.Nivel)).ToHashSet();

    // ───────────────────────── Inicio: quién sos + calles ─────────────────────────

    [HttpGet("inicio")]
    public async Task<IActionResult> Inicio()
    {
        if (!_clave.PaseValido(Request)) return PaseVencido();
        var operadores = await _db.StockOperadores.AsNoTracking().Where(o => o.IsActive)
            .OrderBy(o => o.Orden).ThenBy(o => o.Nombre).Select(o => new { id = o.Id, nombre = o.Nombre }).ToListAsync();
        var desde = await DesdeAsync();
        var revisados = await RevisadosAsync(desde);
        var lados = await LadosAsync();
        var conteos = await _db.DepositoEstanteProductos.AsNoTracking()
            .GroupBy(a => new { a.ElementoId, a.Cara }).Select(g => new { g.Key.ElementoId, g.Key.Cara, N = g.Count() }).ToListAsync();
        var calles = lados.GroupBy(x => x.Calle).Select(g =>
        {
            int total = 0, rev = 0;
            foreach (var x in g)
                for (int p = 1; p <= (x.Rack.Posiciones ?? 1); p++)
                    for (int n = 1; n <= (x.Rack.Niveles ?? 1); n++)
                    {
                        total++;
                        if (revisados.Contains((x.Rack.Id, x.Cara, p, n))) rev++;
                    }
            // donde seguir: el primer estante sin revisar de la calle
            string? seguir = null;
            foreach (var x in g.OrderBy(y => y.Orden))
            {
                for (int p = 1; p <= (x.Rack.Posiciones ?? 1) && seguir is null; p++)
                    for (int n = 1; n <= (x.Rack.Niveles ?? 1) && seguir is null; n++)
                        if (!revisados.Contains((x.Rack.Id, x.Cara, p, n)))
                            seguir = DepositoPlanoController.CodigoEstante(x.Rack, x.Cara, p, n);
                if (seguir is not null) break;
            }
            var primero = g.OrderBy(y => y.Orden).First();
            return new
            {
                calle = g.Key,
                racks = g.Count(),
                estantes = total,
                revisados = rev,
                productos = g.Sum(x => conteos.Where(c => c.ElementoId == x.Rack.Id && c.Cara == x.Cara).Sum(c => c.N)),
                planta = primero.Rack.Planta,
                seguir = seguir ?? DepositoPlanoController.CodigoEstante(primero.Rack, primero.Cara, 1, 1),
            };
        }).ToList();
        return Ok(new { operadores, calles, desde = desde == DateTime.MinValue ? (DateTime?)null : desde });
    }

    // ───────────────────────── Un estante ─────────────────────────

    [HttpGet("estante/{codigo}")]
    public async Task<IActionResult> Estante(string codigo)
    {
        if (!_clave.PaseValido(Request)) return PaseVencido();
        // 2026-11-28: etiqueta única del rack (sin altura): devuelve las alturas para elegir
        var (rackE, caraE, errorE) = await DepositoPlanoController.ResolverRackAsync(_db, codigo);
        if (errorE is not null) return NotFound(new { error = errorE });
        if (rackE is not null)
            return Ok(await DepositoPlanoController.ElegirAlturaAsync(_db, rackE, caraE, await RevisadosAsync(await DesdeAsync())));
        var (rack, cara, pos, niv, error) = await DepositoPlanoController.ResolverEstanteAsync(_db, codigo);
        if (rack is null) return NotFound(new { error });
        var desde = await DesdeAsync();
        var revisados = await RevisadosAsync(desde);
        var cod = DepositoPlanoController.CodigoEstante(rack, cara, pos, niv);
        var (calle, nro) = DepositoPlanoController.LadoDe(rack, cara);

        var aca = await _db.DepositoEstanteProductos.AsNoTracking()
            .Where(a => a.ElementoId == rack.Id && a.Posicion == pos && a.Nivel == niv)
            .ToListAsync();
        aca = aca.Where(a => DepositoPlanoController.NormCara(rack, a.Cara) == cara).ToList();
        var prodIds = aca.Select(a => a.ProductoId).ToList();
        var prods = await _db.CafeProductos.AsNoTracking().Where(p => prodIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Sku, p.Nombre, p.StockUnidades }).ToListAsync();
        var otras = await OtrosLugaresAsync(prodIds, aca.Select(a => a.Id).ToList(), desde);

        // alturas del lado (de abajo hacia arriba) y si están revisadas
        var porNivel = await _db.DepositoEstanteProductos.AsNoTracking().Where(a => a.ElementoId == rack.Id)
            .Select(a => new { a.Cara, a.Posicion, a.Nivel }).ToListAsync();
        var notas = await _db.DepositoEstanteNotas.AsNoTracking().Where(n => n.ElementoId == rack.Id).ToListAsync();
        var nota = notas.FirstOrDefault(n => n.Cara == cara && n.Posicion == pos && n.Nivel == niv);
        var alturas = new List<object>();
        for (int n = 1; n <= (rack.Niveles ?? 1); n++)
            alturas.Add(new
            {
                nivel = n,
                codigo = DepositoPlanoController.CodigoEstante(rack, cara, pos, n),
                revisado = revisados.Contains((rack.Id, cara, pos, n)),
                productos = porNivel.Count(a => DepositoPlanoController.NormCara(rack, a.Cara) == cara && a.Posicion == pos && a.Nivel == n),
                nota = notas.Any(x => x.Cara == cara && x.Posicion == pos && x.Nivel == n),
            });

        // el que sigue: la altura de arriba; si es la última, el próximo rack de la calle
        string? siguiente = null; bool cambiaRack = false;
        if (niv < (rack.Niveles ?? 1)) siguiente = DepositoPlanoController.CodigoEstante(rack, cara, pos, niv + 1);
        else if (pos < (rack.Posiciones ?? 1)) siguiente = DepositoPlanoController.CodigoEstante(rack, cara, pos + 1, 1);
        else if (calle is int c)
        {
            var lados = await LadosAsync();
            var yo = lados.FirstOrDefault(x => x.Calle == c && x.Rack.Id == rack.Id && x.Cara == cara);
            var prox = yo is null ? null : lados.Where(x => x.Calle == c && x.Orden > yo.Orden).OrderBy(x => x.Orden).FirstOrDefault();
            if (prox is not null) { siguiente = DepositoPlanoController.CodigoEstante(prox.Rack, prox.Cara, 1, 1); cambiaRack = true; }
        }

        return Ok(new
        {
            codigo = cod,
            desglose = DepositoPlanoController.DesgloseEstante(rack, cara, pos, niv),
            planta = rack.Planta,
            rackId = rack.Id,
            rack = rack.Codigo,
            calle, nro, fila = rack.Fila, nombreRack = DepositoPlanoController.NombreRack(rack), cara, posicion = pos, nivel = niv, niveles = rack.Niveles ?? 1,
            revisado = revisados.Contains((rack.Id, cara, pos, niv)),
            nota = nota?.Nota,
            notaPor = nota?.Por,
            notaAt = nota?.At,
            alturas,
            siguiente,
            cambiaRack,
            productos = aca.Select(a =>
            {
                var p = prods.First(x => x.Id == a.ProductoId);
                var o = otras.GetValueOrDefault(a.ProductoId) ?? new();
                return new
                {
                    productoId = p.Id,
                    sku = p.Sku,
                    nombre = p.Nombre,
                    stock = p.StockUnidades,
                    contado = a.ContadoAt >= desde ? a.CantidadNum : null,
                    contadoPor = a.ContadoAt >= desde ? a.ContadoPor : null,
                    cantidadTexto = a.Cantidad,
                    otros = o,
                };
            }).OrderBy(x => x.contado is not null).ThenBy(x => x.nombre).ToList(),
        });
    }

    public record OtroLugar(string Codigo, int? Contado);

    /// <summary>Otros estantes donde está cada producto (sin contar los de acá), con lo contado en este recorrido.</summary>
    private async Task<Dictionary<int, List<OtroLugar>>> OtrosLugaresAsync(List<int> prodIds, List<int> excluirAsigs, DateTime desde)
    {
        var l = await _db.DepositoEstanteProductos.AsNoTracking()
            .Where(a => prodIds.Contains(a.ProductoId) && !excluirAsigs.Contains(a.Id))
            .Join(_db.DepositoPlanoElementos.AsNoTracking(), a => a.ElementoId, e => e.Id, (a, e) => new { a, e })
            .ToListAsync();
        return l.GroupBy(x => x.a.ProductoId).ToDictionary(g => g.Key, g => g.Select(x =>
            new OtroLugar(DepositoPlanoController.CodigoEstante(x.e, x.a.Cara, x.a.Posicion, x.a.Nivel),
                x.a.ContadoAt >= desde ? x.a.CantidadNum : null)).ToList());
    }

    // ───────────────────────── Buscar producto ─────────────────────────

    [HttpGet("buscar")]
    public async Task<IActionResult> Buscar([FromQuery] string? q, [FromQuery] string? marca = null)
    {
        if (!_clave.PaseValido(Request)) return PaseVencido();
        // 2026-11-27: "Traer todos los de la marca X"
        if (!string.IsNullOrWhiteSpace(marca))
        {
            var m = marca.Trim();
            var deMarca = await _db.CafeProductos.AsNoTracking()
                .Where(p => p.IsActive && ((p.MarcaNav != null && p.MarcaNav.Nombre == m) || (p.MarcaNav == null && p.Marca == m)))
                .OrderBy(p => p.Nombre).Select(p => new { p.Id, p.Sku, p.Nombre, p.StockUnidades }).Take(300).ToListAsync();
            return Ok(await ConLugaresAsync(deMarca.Select(p => (p.Id, p.Sku, p.Nombre, p.StockUnidades)).ToList()));
        }
        var texto = (q ?? "").Trim();
        if (texto.Length < 2) return Ok(Array.Empty<object>());
        // código de barras o SKU exacto primero (lo que manda el escáner)
        var exactos = await _db.CafeProductos.AsNoTracking()
            .Where(p => p.IsActive && (p.Barcode == texto || p.Sku == texto))
            .Select(p => new { p.Id, p.Sku, p.Nombre, p.StockUnidades }).Take(5).ToListAsync();
        var lista = exactos;
        if (lista.Count == 0)
        {
            var qq = _db.CafeProductos.AsNoTracking().Where(p => p.IsActive);
            foreach (var pal in texto.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var pat = "%" + CafePreventasController.EscaparLike(pal) + "%";
                qq = qq.Where(p => EF.Functions.Like(EF.Functions.Collate(p.Nombre, COLLATE_SIN_TILDES), pat)
                    || (p.Sku != null && EF.Functions.Like(p.Sku, pat)) || (p.Barcode != null && EF.Functions.Like(p.Barcode, pat))
                    || (p.MarcaNav != null && EF.Functions.Like(EF.Functions.Collate(p.MarcaNav.Nombre, COLLATE_SIN_TILDES), pat))
                    || (p.Marca != null && EF.Functions.Like(EF.Functions.Collate(p.Marca, COLLATE_SIN_TILDES), pat)));
            }
            lista = await qq.OrderBy(p => p.Nombre).Select(p => new { p.Id, p.Sku, p.Nombre, p.StockUnidades }).Take(40).ToListAsync();
        }
        return Ok(await ConLugaresAsync(lista.Select(p => (p.Id, p.Sku, p.Nombre, p.StockUnidades)).ToList()));
    }

    private async Task<List<object>> ConLugaresAsync(List<(int Id, string? Sku, string Nombre, int Stock)> lista)
    {
        var ids = lista.Select(x => x.Id).ToList();
        var lugares = await OtrosLugaresAsync(ids, new(), await DesdeAsync());
        return lista.Select(p => (object)new
        {
            productoId = p.Id, sku = p.Sku, nombre = p.Nombre, stock = p.Stock,
            lugares = lugares.GetValueOrDefault(p.Id) ?? new(),
        }).ToList();
    }

    /// <summary>2026-11-27: marcas que coinciden con lo que escriben, con cuántos productos tienen
    /// (para el botón "Traer todos los de X").</summary>
    [HttpGet("marcas")]
    public async Task<IActionResult> Marcas([FromQuery] string? q)
    {
        if (!_clave.PaseValido(Request)) return PaseVencido();
        var texto = (q ?? "").Trim();
        if (texto.Length < 3) return Ok(Array.Empty<object>());
        var pat = "%" + CafePreventasController.EscaparLike(texto) + "%";
        var marcas = await _db.CafeProductos.AsNoTracking().Where(p => p.IsActive)
            .Select(p => p.MarcaNav != null ? p.MarcaNav.Nombre : p.Marca)
            .Where(m => m != null && m != "" && EF.Functions.Like(EF.Functions.Collate(m, COLLATE_SIN_TILDES), pat))
            .GroupBy(m => m).Select(g => new { marca = g.Key, productos = g.Count() })
            .OrderByDescending(x => x.productos).Take(3).ToListAsync();
        return Ok(marcas);
    }

    public class BarcodeRequest
    {
        public int OperadorId { get; set; }
        public string? Barcode { get; set; }
    }

    /// <summary>2026-11-27: escanearon un código que el sistema no conocía y eligieron el producto
    /// buscando por nombre: se le guarda el código de barras (solo si no tenía uno y ningún otro
    /// producto activo lo usa), así la próxima vez lo encuentra solo.</summary>
    [HttpPost("producto/{id:int}/barcode")]
    public async Task<IActionResult> GuardarBarcode(int id, [FromBody] BarcodeRequest req)
    {
        if (!_clave.PaseValido(Request)) return PaseVencido();
        var op = await _db.StockOperadores.AsNoTracking().FirstOrDefaultAsync(o => o.Id == req.OperadorId && o.IsActive);
        if (op is null) return BadRequest(new { error = "Elegí quién sos." });
        var bc = (req.Barcode ?? "").Trim();
        if (bc.Length < 6 || bc.Length > 50 || bc.Contains('/')) return BadRequest(new { error = "Ese código no parece de barras." });
        var prod = await _db.CafeProductos.FirstOrDefaultAsync(p => p.Id == id);
        if (prod is null) return NotFound(new { error = "Ese producto no existe." });
        if (!string.IsNullOrWhiteSpace(prod.Barcode)) return Ok(new { ok = false, motivo = "ya tenía" });
        if (await _db.CafeProductos.AnyAsync(p => p.IsActive && p.Id != id && p.Barcode == bc))
            return Ok(new { ok = false, motivo = "lo usa otro" });
        prod.Barcode = bc;
        prod.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ───────────────────────── Contar / ubicar ─────────────────────────

    public class ContarRequest
    {
        public int OperadorId { get; set; }
        public int ProductoId { get; set; }
        /// <summary>Lo contado en este estante. Con Todo=true se ignora (se toma el stock del sistema).</summary>
        public int? Cantidad { get; set; }
        /// <summary>"📦 Todo el stock acá": ubica sin contar, no toca el stock.</summary>
        public bool Todo { get; set; }
        /// <summary>Sacarlo de los otros estantes donde estaba (queda solo acá).</summary>
        public bool Mover { get; set; }
    }

    [HttpPost("estante/{codigo}/contar")]
    public async Task<IActionResult> Contar(string codigo, [FromBody] ContarRequest req)
    {
        if (!_clave.PaseValido(Request)) return PaseVencido();
        var op = await _db.StockOperadores.FirstOrDefaultAsync(o => o.Id == req.OperadorId && o.IsActive);
        if (op is null) return BadRequest(new { error = "Elegí quién sos." });
        var (rack, cara, pos, niv, error) = await DepositoPlanoController.ResolverEstanteAsync(_db, codigo);
        if (rack is null) return NotFound(new { error });
        var prod = await _db.CafeProductos.FirstOrDefaultAsync(p => p.Id == req.ProductoId && p.IsActive);
        if (prod is null) return NotFound(new { error = "Ese producto no existe." });
        if (!req.Todo && (req.Cantidad is null || req.Cantidad < 0 || req.Cantidad > 1_000_000))
            return BadRequest(new { error = "Poné cuántos hay (0 o más)." });

        var cod = DepositoPlanoController.CodigoEstante(rack, cara, pos, niv);
        var ahora = DateTime.UtcNow;
        var desde = await DesdeAsync();
        var todas = await _db.DepositoEstanteProductos.Where(a => a.ProductoId == prod.Id).ToListAsync();
        var a = todas.FirstOrDefault(x => x.ElementoId == rack.Id && DepositoPlanoController.NormCara(rack, x.Cara) == cara && x.Posicion == pos && x.Nivel == niv);
        var nuevo = a is null;
        if (a is null)
        {
            a = new DepositoEstanteProducto { ElementoId = rack.Id, Cara = cara, Posicion = pos, Nivel = niv, ProductoId = prod.Id, At = ahora, Por = op.Nombre };
            _db.DepositoEstanteProductos.Add(a);
        }
        var otras = todas.Where(x => x != a).ToList();
        if (req.Mover && otras.Count > 0) { _db.DepositoEstanteProductos.RemoveRange(otras); otras.Clear(); }

        var antes = prod.StockUnidades;
        var contado = req.Todo ? Math.Max(0, antes) : req.Cantidad!.Value;
        a.CantidadNum = contado;
        a.ContadoAt = ahora;
        a.ContadoPor = op.Nombre;

        // la ubicación de siempre del producto: este estante
        prod.UbicacionPlanta = rack.Planta;
        prod.UbicacionZona = cod;
        prod.UbicacionAt = ahora;
        prod.UbicacionPor = op.Nombre;
        prod.UbicacionDudosaAt = null;
        prod.UbicacionDudosaPor = null;

        // stock: suma de lo contado en todos sus estantes (si falta contar alguno, se espera)
        string? aviso = null;
        var ajustado = false;
        var despues = antes;
        if (!req.Todo)
        {
            var sinContar = otras.Where(x => x.ContadoAt is null || x.ContadoAt < desde || x.CantidadNum is null).ToList();
            if (sinContar.Count > 0)
            {
                var rks = await _db.DepositoPlanoElementos.AsNoTracking().Where(e => sinContar.Select(s => s.ElementoId).Contains(e.Id)).ToListAsync();
                var cods = sinContar.Select(s => rks.FirstOrDefault(r => r.Id == s.ElementoId) is { } r ? DepositoPlanoController.CodigoEstante(r, s.Cara, s.Posicion, s.Nivel) : "?").Distinct();
                aviso = $"También está en {string.Join(", ", cods)}: el stock se corrige cuando lo cuentes ahí.";
            }
            else
            {
                despues = contado + otras.Sum(x => x.CantidadNum ?? 0);
                if (despues != antes)
                {
                    prod.StockUnidades = despues;
                    prod.UpdatedAt = ahora;
                    prod.StockChangedAt = ahora;
                    await CafeStockHelper.SyncStockPorDepositoAsync(_db, prod);
                    _db.StockMovimientos.Add(new StockMovimiento
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
                        Comentario = $"Recorrer estantes · {cod}",
                        CreatedAt = ahora,
                    });
                    ajustado = true;
                }
            }
        }

        await _db.SaveChangesAsync();
        if (ajustado) PushMeli(prod.Id);
        return Ok(new { ok = true, nuevo, codigo = cod, stockAntes = antes, stockDespues = despues, ajustado, aviso });
    }

    /// <summary>"No está acá": lo saca de este estante (el stock no se toca).</summary>
    [HttpDelete("estante/{codigo}/producto/{productoId:int}")]
    public async Task<IActionResult> Sacar(string codigo, int productoId)
    {
        if (!_clave.PaseValido(Request)) return PaseVencido();
        var (rack, cara, pos, niv, error) = await DepositoPlanoController.ResolverEstanteAsync(_db, codigo);
        if (rack is null) return NotFound(new { error });
        var todas = await _db.DepositoEstanteProductos.Where(a => a.ProductoId == productoId).ToListAsync();
        var a = todas.FirstOrDefault(x => x.ElementoId == rack.Id && DepositoPlanoController.NormCara(rack, x.Cara) == cara && x.Posicion == pos && x.Nivel == niv);
        if (a is null) return NotFound(new { error = "Ese producto ya no estaba en este estante." });
        _db.DepositoEstanteProductos.Remove(a);

        var cod = DepositoPlanoController.CodigoEstante(rack, cara, pos, niv);
        var prod = await _db.CafeProductos.FirstOrDefaultAsync(p => p.Id == productoId);
        if (prod is not null && prod.UbicacionZona == cod)
        {
            var otra = todas.Where(x => x != a).OrderByDescending(x => x.At).FirstOrDefault();
            if (otra is not null)
            {
                var r2 = await _db.DepositoPlanoElementos.AsNoTracking().FirstAsync(e => e.Id == otra.ElementoId);
                prod.UbicacionZona = DepositoPlanoController.CodigoEstante(r2, otra.Cara, otra.Posicion, otra.Nivel);
                prod.UbicacionPlanta = r2.Planta;
            }
            else prod.UbicacionZona = null;
            prod.UbicacionAt = DateTime.UtcNow;
            prod.UbicacionPor = "Celu depósito";
        }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    public class RevisadoRequest
    {
        public int OperadorId { get; set; }
        public int Ajustes { get; set; }
    }

    public class NotaRequest
    {
        public int OperadorId { get; set; }
        public string? Nota { get; set; }
    }

    /// <summary>2026-11-27: comentario libre del estante (vacío lo borra).</summary>
    [HttpPost("estante/{codigo}/nota")]
    public async Task<IActionResult> Nota(string codigo, [FromBody] NotaRequest req)
    {
        if (!_clave.PaseValido(Request)) return PaseVencido();
        var op = await _db.StockOperadores.AsNoTracking().FirstOrDefaultAsync(o => o.Id == req.OperadorId && o.IsActive);
        if (op is null) return BadRequest(new { error = "Elegí quién sos." });
        var (rack, cara, pos, niv, error) = await DepositoPlanoController.ResolverEstanteAsync(_db, codigo);
        if (rack is null) return NotFound(new { error });
        var n = await DepositoPlanoController.GuardarNotaAsync(_db, rack.Id, cara, pos, niv, req.Nota, op.Nombre);
        return Ok(new { ok = true, nota = n?.Nota, notaPor = n?.Por, notaAt = n?.At });
    }

    /// <summary>"Listo": el estante queda revisado en este recorrido.</summary>
    [HttpPost("estante/{codigo}/revisado")]
    public async Task<IActionResult> Revisado(string codigo, [FromBody] RevisadoRequest req)
    {
        if (!_clave.PaseValido(Request)) return PaseVencido();
        var op = await _db.StockOperadores.AsNoTracking().FirstOrDefaultAsync(o => o.Id == req.OperadorId && o.IsActive);
        if (op is null) return BadRequest(new { error = "Elegí quién sos." });
        var (rack, cara, pos, niv, error) = await DepositoPlanoController.ResolverEstanteAsync(_db, codigo);
        if (rack is null) return NotFound(new { error });
        var n = (await _db.DepositoEstanteProductos.AsNoTracking()
                .Where(a => a.ElementoId == rack.Id && a.Posicion == pos && a.Nivel == niv).Select(a => a.Cara).ToListAsync())
            .Count(c => DepositoPlanoController.NormCara(rack, c) == cara);
        _db.DepositoEstanteRevisiones.Add(new DepositoEstanteRevision
        {
            ElementoId = rack.Id, Cara = cara, Posicion = pos, Nivel = niv, At = DateTime.UtcNow,
            OperadorId = op.Id, Por = op.Nombre, Productos = n, Ajustes = Math.Max(0, req.Ajustes),
        });
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    private void PushMeli(int cafeProductoId)
    {
        var sf = _scopeFactory;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = sf.CreateScope();
                await scope.ServiceProvider.GetRequiredService<MeliStockPushService>().PushStockForProductoAsync(cafeProductoId);
            }
            catch { /* queda en StockChangedAt y lo retoma el proceso de fondo */ }
        });
    }
}
