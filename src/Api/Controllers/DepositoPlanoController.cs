using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-11-26: PLANO DEL DEPÓSITO (/deposito/plano). El admin dibuja el terreno, las zonas, las
/// puertas y los racks; el depósito, desde el celu, carga qué producto hay en cada estante
/// (rack-posición-nivel, ej. "B-3-2"). Cada estante lleva una etiqueta Zebra con un QR que abre
/// /deposito/estante/B-3-2.
///
/// Racks: con Rotacion 0, si Largo ≥ Ancho las posiciones corren sobre Y (posición 1 = la punta
/// más cerca de la calle, Y más chica); si no, corren sobre X (posición 1 = X más chica).
/// Nivel 1 = el piso.
///
/// Al guardar un producto en un estante también se le pone la ubicación de siempre
/// (UbicacionPlanta = planta del rack, UbicacionZona = "B-3-2") para que todas las pantallas
/// que ya muestran la ubicación sigan andando sin tocarlas. Esos códigos NO van a Cafe_UbicacionZonas.
/// </summary>
[ApiController]
[Route("api/deposito/plano")]
[Authorize]
public class DepositoPlanoController : ControllerBase
{
    private const string COLLATE_SIN_TILDES = "SQL_Latin1_General_CP1_CI_AI";
    private static readonly string[] PlantasValidas = { "PB", "P1" };

    private readonly AppDbContext _db;
    public DepositoPlanoController(AppDbContext db) { _db = db; }

    private string Usuario => User.Identity?.Name ?? "?";

    // ───────────────────────── DTOs ─────────────────────────

    public record ElementoDto(
        int Id, string Planta, string Tipo, string? Codigo, string? Nombre,
        decimal X, decimal Y, decimal Ancho, decimal Largo, decimal? Alto,
        int Rotacion, int? Posiciones, int? Niveles, string? Color, string? Puntos,
        string? Notas, int Orden, int Productos, string? Estilo);

    public class ElementoRequest
    {
        public string? Planta { get; set; }
        public string? Tipo { get; set; }
        public string? Codigo { get; set; }
        public string? Nombre { get; set; }
        public decimal X { get; set; }
        public decimal Y { get; set; }
        public decimal Ancho { get; set; }
        public decimal Largo { get; set; }
        public decimal? Alto { get; set; }
        public int Rotacion { get; set; }
        public int? Posiciones { get; set; }
        public int? Niveles { get; set; }
        public string? Color { get; set; }
        public string? Puntos { get; set; }
        public string? Notas { get; set; }
        public int Orden { get; set; }
        // 2026-11-26: cómo se ve (JSON chico): dónde va el nombre, de qué tamaño, etc.
        public string? Estilo { get; set; }
    }

    public record AsignacionDto(int Id, int ProductoId, string? Sku, string Nombre,
        string? Cantidad, string? Nota, DateTime At, string? Por);

    public record EstanteDto(int Posicion, int Nivel, string Codigo, List<AsignacionDto> Productos);

    public class AsignarRequest
    {
        public int ProductoId { get; set; }
        public string? Cantidad { get; set; }
        public string? Nota { get; set; }
    }

    private static ElementoDto ADto(DepositoPlanoElemento e, int productos) => new(
        e.Id, e.Planta, e.Tipo, e.Codigo, e.Nombre, e.X, e.Y, e.Ancho, e.Largo, e.Alto,
        e.Rotacion, e.Posiciones, e.Niveles, e.Color, e.Puntos, e.Notas, e.Orden, productos, e.Estilo);

    private static bool EsRack(string? tipo) => string.Equals(tipo, "rack", StringComparison.OrdinalIgnoreCase);

    private static string CodigoEstante(string rack, int pos, int nivel) => $"{rack}-{pos}-{nivel}";

    private static string? Recortar(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length > max ? s[..max] : s;
    }

    private async Task<List<AsignacionDto>> AsignacionesDe(int elementoId, int? pos = null, int? nivel = null)
    {
        var q = _db.DepositoEstanteProductos.AsNoTracking().Where(a => a.ElementoId == elementoId);
        if (pos is not null) q = q.Where(a => a.Posicion == pos);
        if (nivel is not null) q = q.Where(a => a.Nivel == nivel);
        var lista = await q
            .Join(_db.CafeProductos.AsNoTracking(), a => a.ProductoId, p => p.Id, (a, p) => new
            {
                a.Id, a.Posicion, a.Nivel, a.ProductoId, p.Sku, p.Nombre, a.Cantidad, a.Nota, a.At, a.Por
            })
            .OrderBy(x => x.Nombre)
            .ToListAsync();
        return lista.Select(x => new AsignacionDto(x.Id, x.ProductoId, x.Sku, x.Nombre, x.Cantidad, x.Nota, x.At, x.Por)).ToList();
    }

    // ───────────────────────── Plano ─────────────────────────

    /// <summary>Todos los elementos de una planta, en orden de dibujo.</summary>
    [HttpGet]
    public async Task<IActionResult> Plano([FromQuery] string? planta = "PB")
    {
        var pl = (planta ?? "PB").Trim().ToUpperInvariant();
        var elementos = await _db.DepositoPlanoElementos.AsNoTracking()
            .Where(e => e.Planta == pl)
            .OrderBy(e => e.Orden).ThenBy(e => e.Id)
            .ToListAsync();
        var ids = elementos.Select(e => e.Id).ToList();
        var conteos = await _db.DepositoEstanteProductos.AsNoTracking()
            .Where(a => ids.Contains(a.ElementoId))
            .GroupBy(a => a.ElementoId)
            .Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N);
        return Ok(new
        {
            planta = pl,
            elementos = elementos.Select(e => ADto(e, conteos.GetValueOrDefault(e.Id))).ToList()
        });
    }

    /// <summary>Valida y normaliza lo que manda el admin. Devuelve el texto del error o null.</summary>
    private static string? Validar(ElementoRequest r)
    {
        r.Planta = (r.Planta ?? "").Trim().ToUpperInvariant();
        if (!PlantasValidas.Contains(r.Planta)) return "La planta tiene que ser PB o P1.";
        r.Tipo = (r.Tipo ?? "").Trim().ToLowerInvariant();
        if (r.Tipo.Length == 0) return "Falta el tipo de elemento.";
        if (r.Tipo.Length > 20) return "El tipo es demasiado largo (máximo 20 letras).";
        if (r.Ancho <= 0 || r.Largo <= 0) return "El ancho y el largo tienen que ser mayores a cero.";
        if (r.X < -9999 || r.X > 9999 || r.Y < -9999 || r.Y > 9999 || r.Ancho > 9999 || r.Largo > 9999)
            return "Las medidas están fuera de rango.";
        if (r.Alto is not null && (r.Alto < 0 || r.Alto > 999)) return "El alto está fuera de rango.";

        var rot = ((r.Rotacion % 360) + 360) % 360;
        r.Rotacion = (int)(Math.Round(rot / 90.0) * 90) % 360;

        r.Nombre = Recortar(r.Nombre, 100);
        r.Notas = Recortar(r.Notas, 300);
        r.Color = Recortar(r.Color, 9);
        r.Estilo = string.IsNullOrWhiteSpace(r.Estilo) ? null : r.Estilo.Trim();
        if (r.Estilo is { Length: > 400 }) return "El estilo es demasiado largo.";
        r.Puntos = string.IsNullOrWhiteSpace(r.Puntos) ? null : r.Puntos.Trim();

        if (EsRack(r.Tipo))
        {
            r.Codigo = (r.Codigo ?? "").Trim().ToUpperInvariant();
            if (r.Codigo.Length == 0) return "El rack necesita una letra (código), por ejemplo A.";
            if (r.Codigo.Length > 10) return "El código del rack es demasiado largo (máximo 10).";
            if (!Regex.IsMatch(r.Codigo, "^[A-Z0-9]+$")) return "El código del rack solo puede tener letras y números (sin guiones ni espacios).";
            if (r.Posiciones is null || r.Posiciones < 1 || r.Posiciones > 30) return "El rack tiene que tener entre 1 y 30 posiciones.";
            if (r.Niveles is null || r.Niveles < 1 || r.Niveles > 15) return "El rack tiene que tener entre 1 y 15 niveles.";
        }
        else
        {
            r.Codigo = Recortar(r.Codigo, 10);
        }
        return null;
    }

    private static void Copiar(ElementoRequest r, DepositoPlanoElemento e)
    {
        e.Planta = r.Planta!; e.Tipo = r.Tipo!; e.Codigo = r.Codigo; e.Nombre = r.Nombre;
        e.X = r.X; e.Y = r.Y; e.Ancho = r.Ancho; e.Largo = r.Largo; e.Alto = r.Alto;
        e.Rotacion = r.Rotacion; e.Posiciones = r.Posiciones; e.Niveles = r.Niveles;
        e.Color = r.Color; e.Puntos = r.Puntos; e.Notas = r.Notas; e.Orden = r.Orden;
        e.Estilo = r.Estilo;
    }

    [HttpPost("elementos")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Crear([FromBody] ElementoRequest req)
    {
        var err = Validar(req);
        if (err is not null) return BadRequest(new { error = err });
        if (EsRack(req.Tipo) && await _db.DepositoPlanoElementos.AnyAsync(e => e.Tipo == "rack" && e.Codigo == req.Codigo))
            return Conflict(new { error = $"Ya hay un rack con la letra {req.Codigo}." });

        var e = new DepositoPlanoElemento { CreadoAt = DateTime.UtcNow, ModificadoAt = DateTime.UtcNow, ModificadoPor = Usuario };
        Copiar(req, e);
        _db.DepositoPlanoElementos.Add(e);
        await _db.SaveChangesAsync();
        return Ok(ADto(e, 0));
    }

    [HttpPut("elementos/{id:int}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Editar(int id, [FromBody] ElementoRequest req)
    {
        var e = await _db.DepositoPlanoElementos.FirstOrDefaultAsync(x => x.Id == id);
        if (e is null) return NotFound(new { error = "Ese elemento ya no existe." });
        var err = Validar(req);
        if (err is not null) return BadRequest(new { error = err });

        var asigs = await _db.DepositoEstanteProductos.Where(a => a.ElementoId == id).ToListAsync();

        if (EsRack(e.Tipo) && asigs.Count > 0)
        {
            if (!EsRack(req.Tipo))
                return Conflict(new { error = $"El rack {e.Codigo} tiene {asigs.Count} productos guardados. Sacalos antes de cambiarle el tipo." });
            var fuera = asigs
                .Where(a => a.Posicion > req.Posiciones || a.Nivel > req.Niveles)
                .Select(a => CodigoEstante(e.Codigo ?? "?", a.Posicion, a.Nivel))
                .Distinct().OrderBy(c => c).ToList();
            if (fuera.Count == 1)
                return Conflict(new { error = $"El estante {fuera[0]} tiene productos; sacalos antes de achicar el rack." });
            if (fuera.Count > 1)
                return Conflict(new { error = $"Los estantes {string.Join(", ", fuera)} tienen productos; sacalos antes de achicar el rack." });
        }
        if (EsRack(req.Tipo) && await _db.DepositoPlanoElementos.AnyAsync(x => x.Id != id && x.Tipo == "rack" && x.Codigo == req.Codigo))
            return Conflict(new { error = $"Ya hay un rack con la letra {req.Codigo}." });

        var codigoViejo = e.Codigo;
        var plantaVieja = e.Planta;
        Copiar(req, e);
        e.ModificadoAt = DateTime.UtcNow;
        e.ModificadoPor = Usuario;

        // Si el rack cambió de letra o de planta, la ubicación de los productos que apuntaban a
        // sus estantes se actualiza para que no quede el código viejo.
        if (EsRack(e.Tipo) && asigs.Count > 0 && (codigoViejo != e.Codigo || plantaVieja != e.Planta))
        {
            var prodIds = asigs.Select(a => a.ProductoId).Distinct().ToList();
            var prods = await _db.CafeProductos.Where(p => prodIds.Contains(p.Id)).ToListAsync();
            foreach (var a in asigs)
            {
                var viejo = CodigoEstante(codigoViejo ?? "?", a.Posicion, a.Nivel);
                var p = prods.FirstOrDefault(x => x.Id == a.ProductoId);
                if (p is not null && p.UbicacionZona == viejo)
                {
                    p.UbicacionZona = CodigoEstante(e.Codigo!, a.Posicion, a.Nivel);
                    p.UbicacionPlanta = e.Planta;
                }
            }
        }

        await _db.SaveChangesAsync();
        return Ok(ADto(e, asigs.Count));
    }

    [HttpDelete("elementos/{id:int}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Borrar(int id)
    {
        var e = await _db.DepositoPlanoElementos.FirstOrDefaultAsync(x => x.Id == id);
        if (e is null) return NotFound(new { error = "Ese elemento ya no existe." });
        var n = await _db.DepositoEstanteProductos.CountAsync(a => a.ElementoId == id);
        if (n > 0)
            return Conflict(new { error = $"El rack {e.Codigo} tiene {n} productos guardados. Sacalos antes de borrarlo." });
        _db.DepositoPlanoElementos.Remove(e);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ───────────────────────── Racks y estantes ─────────────────────────

    /// <summary>Un rack con TODOS sus estantes (posición × nivel), aunque estén vacíos.</summary>
    [HttpGet("racks/{id:int}")]
    public async Task<IActionResult> Rack(int id)
    {
        var e = await _db.DepositoPlanoElementos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.Tipo == "rack");
        if (e is null) return NotFound(new { error = "Ese rack no existe." });
        var asigs = await AsignacionesDe(id);
        var crudas = await _db.DepositoEstanteProductos.AsNoTracking()
            .Where(a => a.ElementoId == id)
            .Select(a => new { a.Id, a.Posicion, a.Nivel }).ToListAsync();
        var celdaDe = crudas.ToDictionary(a => a.Id, a => (a.Posicion, a.Nivel));

        var estantes = new List<EstanteDto>();
        for (int pos = 1; pos <= (e.Posiciones ?? 0); pos++)
            for (int niv = 1; niv <= (e.Niveles ?? 0); niv++)
                estantes.Add(new EstanteDto(pos, niv, CodigoEstante(e.Codigo ?? "?", pos, niv),
                    asigs.Where(a => celdaDe.TryGetValue(a.Id, out var c) && c.Posicion == pos && c.Nivel == niv).ToList()));

        return Ok(new { elemento = ADto(e, crudas.Count), estantes });
    }

    /// <summary>Parte "B-3-2" (o "b-3-2") en rack + posición + nivel y busca el rack.</summary>
    private async Task<(DepositoPlanoElemento? rack, int pos, int nivel, string? error)> ResolverEstante(string codigo, bool tracking = false)
    {
        var m = Regex.Match((codigo ?? "").Trim().ToUpperInvariant(), @"^([A-Z0-9]+)-(\d{1,3})-(\d{1,3})$");
        if (!m.Success) return (null, 0, 0, $"El código \"{codigo}\" no es de un estante (tiene que ser como B-3-2).");
        var letra = m.Groups[1].Value;
        var pos = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var niv = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        var q = _db.DepositoPlanoElementos.Where(x => x.Tipo == "rack" && x.Codigo == letra);
        var rack = tracking ? await q.FirstOrDefaultAsync() : await q.AsNoTracking().FirstOrDefaultAsync();
        if (rack is null) return (null, 0, 0, $"No hay ningún rack {letra} en el plano.");
        if (pos < 1 || pos > (rack.Posiciones ?? 0) || niv < 1 || niv > (rack.Niveles ?? 0))
            return (null, 0, 0, $"El rack {letra} no tiene el estante {letra}-{pos}-{niv} (tiene {rack.Posiciones ?? 0} posiciones y {rack.Niveles ?? 0} niveles).");
        return (rack, pos, niv, null);
    }

    [HttpGet("estante/{codigo}")]
    public async Task<IActionResult> Estante(string codigo)
    {
        var (rack, pos, niv, error) = await ResolverEstante(codigo);
        if (rack is null) return NotFound(new { error });
        var cod = CodigoEstante(rack.Codigo!, pos, niv);
        return Ok(new
        {
            rackId = rack.Id,
            planta = rack.Planta,
            rack = rack.Codigo,
            codigo = cod,
            posicion = pos,
            nivel = niv,
            productos = await AsignacionesDe(rack.Id, pos, niv)
        });
    }

    /// <summary>Guarda un producto en un estante (lo usa el depósito desde el celu). Si ya estaba
    /// en ese estante, solo actualiza cantidad y nota.</summary>
    [HttpPost("estante/{codigo}/productos")]
    public async Task<IActionResult> Asignar(string codigo, [FromBody] AsignarRequest req)
    {
        var (rack, pos, niv, error) = await ResolverEstante(codigo);
        if (rack is null) return NotFound(new { error });
        var prod = await _db.CafeProductos.FirstOrDefaultAsync(p => p.Id == req.ProductoId);
        if (prod is null) return NotFound(new { error = "Ese producto no existe." });

        var cod = CodigoEstante(rack.Codigo!, pos, niv);
        var ahora = DateTime.UtcNow;
        var a = await _db.DepositoEstanteProductos.FirstOrDefaultAsync(x =>
            x.ElementoId == rack.Id && x.Posicion == pos && x.Nivel == niv && x.ProductoId == prod.Id);
        if (a is null)
        {
            a = new DepositoEstanteProducto { ElementoId = rack.Id, Posicion = pos, Nivel = niv, ProductoId = prod.Id };
            _db.DepositoEstanteProductos.Add(a);
        }
        a.Cantidad = Recortar(req.Cantidad, 50);
        a.Nota = Recortar(req.Nota, 200);
        a.At = ahora;
        a.Por = Usuario;

        // La ubicación de siempre del producto, para que las otras pantallas lo muestren acá.
        prod.UbicacionPlanta = rack.Planta;
        prod.UbicacionZona = cod;
        prod.UbicacionAt = ahora;
        prod.UbicacionPor = Usuario;
        prod.UbicacionDudosaAt = null;
        prod.UbicacionDudosaPor = null;

        await _db.SaveChangesAsync();
        return Ok(new AsignacionDto(a.Id, prod.Id, prod.Sku, prod.Nombre, a.Cantidad, a.Nota, a.At, a.Por));
    }

    /// <summary>Saca un producto de un estante. Si la ubicación del producto apuntaba a ese estante,
    /// pasa a otro estante donde también esté (el más reciente) o queda solo la planta.</summary>
    [HttpDelete("asignaciones/{id:int}")]
    public async Task<IActionResult> Desasignar(int id)
    {
        var a = await _db.DepositoEstanteProductos.FirstOrDefaultAsync(x => x.Id == id);
        if (a is null) return NotFound(new { error = "Ese producto ya no estaba en el estante." });
        var rack = await _db.DepositoPlanoElementos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == a.ElementoId);
        var cod = CodigoEstante(rack?.Codigo ?? "?", a.Posicion, a.Nivel);

        _db.DepositoEstanteProductos.Remove(a);

        var prod = await _db.CafeProductos.FirstOrDefaultAsync(p => p.Id == a.ProductoId);
        if (prod is not null && prod.UbicacionZona == cod)
        {
            var otra = await _db.DepositoEstanteProductos.AsNoTracking()
                .Where(x => x.ProductoId == a.ProductoId && x.Id != a.Id)
                .Join(_db.DepositoPlanoElementos.AsNoTracking(), x => x.ElementoId, e => e.Id,
                    (x, e) => new { x.At, x.Id, x.Posicion, x.Nivel, e.Codigo, e.Planta })
                .OrderByDescending(x => x.At).ThenByDescending(x => x.Id)
                .FirstOrDefaultAsync();
            if (otra is not null)
            {
                prod.UbicacionZona = CodigoEstante(otra.Codigo ?? "?", otra.Posicion, otra.Nivel);
                prod.UbicacionPlanta = otra.Planta;
            }
            else
            {
                prod.UbicacionZona = null; // queda la planta
            }
            prod.UbicacionAt = DateTime.UtcNow;
            prod.UbicacionPor = Usuario;
        }

        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ───────────────────────── Buscar ─────────────────────────

    /// <summary>Busca productos activos por nombre (sin importar tildes), SKU o código de barras y
    /// dice en qué estantes están. Primero el que coincide exacto, después los que tienen lugar.</summary>
    [HttpGet("buscar")]
    public async Task<IActionResult> Buscar([FromQuery] string? q)
    {
        var texto = (q ?? "").Trim();
        if (texto.Length < 2) return Ok(Array.Empty<object>());

        var palabras = texto.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var query = _db.CafeProductos.AsNoTracking().Where(p => p.IsActive);
        var patTodo = "%" + CafePreventasController.EscaparLike(texto) + "%";
        // Coincide si TODAS las palabras están en el nombre (sin tildes), o si el texto entero está
        // en el SKU o el código de barras.
        var porNombre = _db.CafeProductos.AsNoTracking().Where(p => p.IsActive);
        foreach (var pal in palabras)
        {
            var pat = "%" + CafePreventasController.EscaparLike(pal) + "%";
            porNombre = porNombre.Where(p => EF.Functions.Like(EF.Functions.Collate(p.Nombre, COLLATE_SIN_TILDES), pat)
                || (p.Sku != null && EF.Functions.Like(p.Sku, pat)));
        }
        var idsNombre = porNombre.Select(p => p.Id);
        query = query.Where(p => idsNombre.Contains(p.Id)
            || (p.Sku != null && EF.Functions.Like(p.Sku, patTodo))
            || (p.Barcode != null && EF.Functions.Like(p.Barcode, patTodo)));

        var candidatos = await query
            .Select(p => new { p.Id, p.Sku, p.Nombre, p.Barcode })
            .OrderBy(p => p.Nombre)
            .Take(200)
            .ToListAsync();
        var ids = candidatos.Select(c => c.Id).ToList();

        var lugares = await _db.DepositoEstanteProductos.AsNoTracking()
            .Where(a => ids.Contains(a.ProductoId))
            .Join(_db.DepositoPlanoElementos.AsNoTracking(), a => a.ElementoId, e => e.Id,
                (a, e) => new { a.Id, a.ProductoId, a.Posicion, a.Nivel, a.Cantidad, a.At, RackId = e.Id, e.Codigo, e.Planta })
            .ToListAsync();
        var lugaresPor = lugares.GroupBy(l => l.ProductoId).ToDictionary(g => g.Key, g => g.OrderBy(l => l.Codigo).ThenBy(l => l.Posicion).ThenBy(l => l.Nivel).ToList());

        bool Exacto(string? s) => s != null && string.Equals(s.Trim(), texto, StringComparison.OrdinalIgnoreCase);

        var res = candidatos
            .OrderByDescending(c => Exacto(c.Barcode) || Exacto(c.Sku))
            .ThenByDescending(c => lugaresPor.ContainsKey(c.Id))
            .ThenBy(c => c.Nombre)
            .Take(30)
            .Select(c => new
            {
                productoId = c.Id,
                sku = c.Sku,
                nombre = c.Nombre,
                lugares = (lugaresPor.GetValueOrDefault(c.Id) ?? new()).Select(l => new
                {
                    asignacionId = l.Id,
                    codigo = CodigoEstante(l.Codigo ?? "?", l.Posicion, l.Nivel),
                    rackId = l.RackId,
                    planta = l.Planta,
                    cantidad = l.Cantidad
                }).ToList()
            })
            .ToList();
        return Ok(res);
    }

    // ───────────────────────── Etiquetas Zebra ─────────────────────────

    private static ContentResult ErrorZpl(int status, string texto) => new()
    {
        StatusCode = status,
        ContentType = "text/html; charset=utf-8",
        // zebra-print.js saca el texto del primer <p> cuando no vuelve ZPL
        Content = "<p>" + System.Net.WebUtility.HtmlEncode(texto) + "</p>"
    };

    /// <summary>Una etiqueta por estante (posición y después nivel) para la Zebra 4"×2"
    /// (812×406 puntos a 203 dpi): el código grande, un QR que abre la página del estante y una
    /// línea chica "Rack B · posición 3 · nivel 2". Se la pasa a window.zebraImprimir(url).</summary>
    [HttpGet("racks/{id:int}/etiquetas.zpl")]
    public async Task<IActionResult> Etiquetas(int id, [FromQuery] string? origen)
    {
        var o = (origen ?? "").Trim().TrimEnd('/');
        if (!o.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !o.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(o, UriKind.Absolute, out _) || o.Contains('^') || o.Contains('~'))
            return ErrorZpl(400, "Falta la dirección de la página (origen) para armar el QR.");

        var e = await _db.DepositoPlanoElementos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.Tipo == "rack");
        if (e is null) return ErrorZpl(404, "Ese rack no existe.");
        if ((e.Posiciones ?? 0) < 1 || (e.Niveles ?? 0) < 1) return ErrorZpl(400, "El rack no tiene posiciones ni niveles.");

        var sb = new StringBuilder();
        for (int pos = 1; pos <= e.Posiciones; pos++)
            for (int niv = 1; niv <= e.Niveles; niv++)
            {
                var cod = CodigoEstante(e.Codigo!, pos, niv);
                var url = $"{o}/deposito/estante/{cod}";
                sb.Append("^XA\n^CI28\n^PW812\n^LL406\n^LH0,0\n");
                // código grande a la izquierda (hasta ~520 puntos de ancho, centrado)
                sb.Append($"^FO20,70^A0N,170,140^FB520,1,0,C,0^FD{cod}^FS\n");
                // QR a la derecha (versión chica, aumento 6, entra hasta ~130 letras de dirección)
                sb.Append($"^FO560,50^BQN,2,6^FDLA,{url}^FS\n");
                // línea chica abajo
                sb.Append($"^FO20,320^A0N,38,38^FB760,1,0,L,0^FDRack {e.Codigo} · posición {pos} · nivel {niv}^FS\n");
                sb.Append("^XZ\n");
            }
        return Content(sb.ToString(), "text/plain; charset=utf-8");
    }
}
