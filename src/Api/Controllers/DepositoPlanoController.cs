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
/// (rack-nivel, ej. "D2-3"; ver CodigoEstante). Cada estante lleva una etiqueta Zebra con un QR que abre
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
        string? Notas, int Orden, int Productos, string? Estilo,
        int? Calle1, int? Nro1, int? Calle2, int? Nro2, int? Fila, int? NroFila);

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
        // 2026-11-27: calles (ver DepositoPlanoElemento.Calle1)
        public int? Calle1 { get; set; }
        public int? Nro1 { get; set; }
        public int? Calle2 { get; set; }
        public int? Nro2 { get; set; }
        public int? Fila { get; set; }
        public int? NroFila { get; set; }
    }

    public record AsignacionDto(int Id, int ProductoId, string? Sku, string Nombre,
        string? Cantidad, string? Nota, DateTime At, string? Por);

    public record EstanteDto(int Posicion, int Nivel, string Codigo, List<AsignacionDto> Productos,
        int Cara = 1, string? Desglose = null);

    /// <summary>2026-11-27: un lado del rack que da a una calle.</summary>
    public record CaraDto(int Cara, int? Calle, int? Nro, string Titulo);

    public class AsignarRequest
    {
        public int ProductoId { get; set; }
        public string? Cantidad { get; set; }
        public string? Nota { get; set; }
    }

    private static ElementoDto ADto(DepositoPlanoElemento e, int productos) => new(
        e.Id, e.Planta, e.Tipo, e.Codigo, e.Nombre, e.X, e.Y, e.Ancho, e.Largo, e.Alto,
        e.Rotacion, e.Posiciones, e.Niveles, e.Color, e.Puntos, e.Notas, e.Orden, productos, e.Estilo,
        e.Calle1, e.Nro1, e.Calle2, e.Nro2, e.Fila, e.NroFila);

    private static bool EsRack(string? tipo) => string.Equals(tipo, "rack", StringComparison.OrdinalIgnoreCase);

    /// <summary>2026-11-27: código del estante SIN calle (rack sin calle cargada, ej. planta alta):
    /// rack-nivel "D2-3", y con varias posiciones la posición como letra "D2-3A".</summary>
    public static string CodigoEstante(string rack, int? posiciones, int pos, int nivel)
        => (posiciones ?? 1) <= 1 ? $"{rack}-{nivel}" : $"{rack}-{nivel}{LetraPosicion(pos)}";

    /// <summary>2026-11-27: CALLE-FILA-RACK-ALTURA. Código del estante de un lado del rack:
    /// C{calle}-F{fila}-R{rack de la fila}-A{altura} (+ -P{posición} si el rack tiene varias), ej. "C3-F4-R2-A3".
    /// Sin calle en ese lado: F4-R2-A3; sin fila: el de siempre (rack-nivel). ResolverEstante entiende también
    /// los anteriores (C3-R4-A3, D2-3, D2-1-3), así las etiquetas QR ya impresas siguen abriendo el estante.</summary>
    public static string CodigoEstante(DepositoPlanoElemento e, int cara, int pos, int nivel)
    {
        var calle = CalleDe(e, cara);
        var posTxt = (e.Posiciones ?? 1) > 1 ? $"-P{pos}" : "";
        if (e.Fila is int f && e.NroFila is int r)
            return (calle is int c ? $"C{c}-" : "") + $"F{f}-R{r}-A{nivel}" + posTxt;
        return CodigoEstante(e.Codigo ?? "?", e.Posiciones, pos, nivel);
    }

    /// <summary>"Calle 3 · Fila 4 · Rack 2 · Altura 3" — para leerlo escrito.</summary>
    public static string DesgloseEstante(DepositoPlanoElemento e, int cara, int pos, int nivel)
    {
        var calle = CalleDe(e, cara);
        var posTxt = (e.Posiciones ?? 1) > 1 ? $" · Posición {pos}" : "";
        if (e.Fila is int f && e.NroFila is int r)
            return (calle is int c ? $"Calle {c} · " : "") + $"Fila {f} · Rack {r} · Altura {nivel}{posTxt}";
        return $"Rack {e.Codigo} · Altura {nivel}{posTxt}";
    }

    /// <summary>Los lados del rack que dan a una calle (1 y/o 2). Sin calles cargadas = un solo lado (1).</summary>
    public static List<int> Caras(DepositoPlanoElemento e)
    {
        var l = new List<int>();
        if (e.Calle1 is not null) l.Add(1);
        if (e.Calle2 is not null) l.Add(2);
        if (l.Count == 0) l.Add(1);
        return l;
    }

    /// <summary>El lado pedido si existe; si no, el único que tiene (racks de pared).</summary>
    public static int NormCara(DepositoPlanoElemento e, int cara)
    {
        var cs = Caras(e);
        return cs.Contains(cara) ? cara : cs[0];
    }

    public static int? CalleDe(DepositoPlanoElemento e, int cara)
        => NormCara(e, cara) == 2 ? e.Calle2 : e.Calle1;

    /// <summary>(calle de ese lado, número de rack en la fila).</summary>
    public static (int? Calle, int? Nro) LadoDe(DepositoPlanoElemento e, int cara) => (CalleDe(e, cara), e.NroFila);

    /// <summary>"F4-R2" (o el código de siempre si no tiene fila).</summary>
    public static string NombreRack(DepositoPlanoElemento e)
        => e.Fila is int f && e.NroFila is int r ? $"F{f}-R{r}" : (e.Codigo ?? "?");

    private static List<CaraDto> CarasDto(DepositoPlanoElemento e) => Caras(e).Select(c =>
    {
        var calle = CalleDe(e, c);
        return new CaraDto(c, calle, e.NroFila, (calle is int cc ? $"Calle {cc} · " : "") + (e.Fila is int f && e.NroFila is int r ? $"Fila {f} · Rack {r}" : $"Rack {e.Codigo}"));
    }).ToList();

    public static string LetraPosicion(int pos) => pos >= 1 && pos <= 26 ? ((char)('A' + pos - 1)).ToString() : pos.ToString(CultureInfo.InvariantCulture);

    private static string? Recortar(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length > max ? s[..max] : s;
    }

    private async Task<List<AsignacionDto>> AsignacionesDe(int elementoId, int? pos = null, int? nivel = null, int? cara = null)
    {
        var q = _db.DepositoEstanteProductos.AsNoTracking().Where(a => a.ElementoId == elementoId);
        if (cara is not null) q = q.Where(a => a.Cara == cara);
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
            if (r.Codigo.Length == 0) return "El rack necesita un código (fila + número), por ejemplo A1.";
            if (r.Codigo.Length > 10) return "El código del rack es demasiado largo (máximo 10).";
            if (!Regex.IsMatch(r.Codigo, "^[A-Z0-9]+$")) return "El código del rack solo puede tener letras y números (sin guiones ni espacios).";
            if (r.Posiciones is null || r.Posiciones < 1 || r.Posiciones > 30) return "El rack tiene que tener entre 1 y 30 posiciones.";
            if (r.Niveles is null || r.Niveles < 1 || r.Niveles > 15) return "El rack tiene que tener entre 1 y 15 niveles.";
            // 2026-11-27: fila + rack de la fila (juntos o ninguno) y la calle de cada lado
            if ((r.Fila is null) != (r.NroFila is null)) return "Poné la fila y el número de rack en la fila (los dos).";
            foreach (var v in new[] { r.Calle1, r.Calle2, r.Fila, r.NroFila })
                if (v is not null && (v < 0 || v > 99)) return "La calle, la fila y el rack van de 0 a 99.";
            if (r.Calle1 is not null && r.Calle1 == r.Calle2) return "Los dos lados del rack no pueden dar a la misma calle.";
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
        var rack = EsRack(r.Tipo);
        e.Calle1 = rack ? r.Calle1 : null; e.Nro1 = rack ? r.Nro1 : null;
        e.Calle2 = rack ? r.Calle2 : null; e.Nro2 = rack ? r.Nro2 : null;
        e.Fila = rack ? r.Fila : null; e.NroFila = rack ? r.NroFila : null;
    }

    /// <summary>2026-11-27: ¿algún otro rack ya es esa fila + número? Devuelve el error o null.</summary>
    private async Task<string?> CalleRepetida(ElementoRequest r, int? idPropio)
    {
        if (!EsRack(r.Tipo) || r.Fila is null || r.NroFila is null) return null;
        var otro = await _db.DepositoPlanoElementos.AsNoTracking()
            .Where(x => x.Tipo == "rack" && x.Id != (idPropio ?? 0) && x.Fila == r.Fila && x.NroFila == r.NroFila)
            .Select(x => x.Codigo).FirstOrDefaultAsync();
        return otro is null ? null : $"La fila {r.Fila} ya tiene un rack {r.NroFila}.";
    }

    [HttpPost("elementos")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Crear([FromBody] ElementoRequest req)
    {
        var err = Validar(req);
        if (err is not null) return BadRequest(new { error = err });
        if (EsRack(req.Tipo) && await _db.DepositoPlanoElementos.AnyAsync(e => e.Tipo == "rack" && e.Codigo == req.Codigo))
            return Conflict(new { error = $"Ya hay un rack con el código {req.Codigo}." });
        if (await CalleRepetida(req, null) is string rep) return Conflict(new { error = rep });

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
                .Select(a => CodigoEstante(e, a.Cara, a.Posicion, a.Nivel))
                .Distinct().OrderBy(c => c).ToList();
            if (fuera.Count == 1)
                return Conflict(new { error = $"El estante {fuera[0]} tiene productos; sacalos antes de achicar el rack." });
            if (fuera.Count > 1)
                return Conflict(new { error = $"Los estantes {string.Join(", ", fuera)} tienen productos; sacalos antes de achicar el rack." });
        }
        if (EsRack(req.Tipo) && await _db.DepositoPlanoElementos.AnyAsync(x => x.Id != id && x.Tipo == "rack" && x.Codigo == req.Codigo))
            return Conflict(new { error = $"Ya hay un rack con el código {req.Codigo}." });
        if (await CalleRepetida(req, id) is string repetida) return Conflict(new { error = repetida });

        // Cómo era antes (para saber qué códigos tenían los productos guardados)
        var antes = new DepositoPlanoElemento
        {
            Codigo = e.Codigo, Planta = e.Planta, Posiciones = e.Posiciones,
            Calle1 = e.Calle1, Nro1 = e.Nro1, Calle2 = e.Calle2, Nro2 = e.Nro2, Fila = e.Fila, NroFila = e.NroFila,
        };
        Copiar(req, e);
        e.ModificadoAt = DateTime.UtcNow;
        e.ModificadoPor = Usuario;

        if (EsRack(e.Tipo) && asigs.Count > 0)
        {
            // 2026-11-27: si un lado con productos se queda sin calle y el otro sí tiene, no se puede
            // (los productos quedarían "del otro lado").
            var carasNuevas = Caras(e);
            if (Caras(antes).Count == 2 && carasNuevas.Count == 1)
            {
                var perdida = Caras(antes).First(c => !carasNuevas.Contains(c));
                var n = asigs.Count(a => a.Cara == perdida);
                if (n > 0)
                    return Conflict(new { error = $"El lado {CarasDto(antes).First(x => x.Cara == perdida).Titulo} tiene {n} productos guardados. Sacalos antes de sacarle la calle." });
            }

            // Si cambió algo del código (rack, planta, posiciones o calles), la ubicación de los
            // productos que apuntaban a sus estantes se actualiza para que no quede el código viejo.
            var prodIds = asigs.Select(a => a.ProductoId).Distinct().ToList();
            var prods = await _db.CafeProductos.Where(p => prodIds.Contains(p.Id)).ToListAsync();
            foreach (var a in asigs)
            {
                var viejo = CodigoEstante(antes, a.Cara, a.Posicion, a.Nivel);
                a.Cara = NormCara(e, a.Cara);
                var nuevo = CodigoEstante(e, a.Cara, a.Posicion, a.Nivel);
                var p = prods.FirstOrDefault(x => x.Id == a.ProductoId);
                if (p is not null && p.UbicacionZona == viejo && (viejo != nuevo || p.UbicacionPlanta != e.Planta))
                {
                    p.UbicacionZona = nuevo;
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
            return Conflict(new { error = $"El rack {NombreRack(e)} tiene {n} productos guardados. Sacalos antes de borrarlo." });
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
            .Select(a => new { a.Id, a.Cara, a.Posicion, a.Nivel }).ToListAsync();
        var celdaDe = crudas.ToDictionary(a => a.Id, a => (Cara: NormCara(e, a.Cara), a.Posicion, a.Nivel));

        // 2026-11-27: un juego de estantes por cada lado que da a una calle
        var estantes = new List<EstanteDto>();
        foreach (var cara in Caras(e))
            for (int pos = 1; pos <= (e.Posiciones ?? 0); pos++)
                for (int niv = 1; niv <= (e.Niveles ?? 0); niv++)
                    estantes.Add(new EstanteDto(pos, niv, CodigoEstante(e, cara, pos, niv),
                        asigs.Where(a => celdaDe.TryGetValue(a.Id, out var c) && c.Cara == cara && c.Posicion == pos && c.Nivel == niv).ToList(),
                        cara, DesgloseEstante(e, cara, pos, niv)));

        return Ok(new { elemento = ADto(e, crudas.Count), estantes, caras = CarasDto(e) });
    }

    /// <summary>Parte el código del estante y busca el rack. Entiende "D2-3" (rack-nivel), "D2-3B"
    /// (nivel 3, posición B) y el formato viejo "D2-1-3" (rack-posición-nivel, de las etiquetas QR
    /// impresas antes del 27/11/2026).</summary>
    private Task<(DepositoPlanoElemento? rack, int cara, int pos, int nivel, string? error)> ResolverEstante(string codigo, bool tracking = false)
        => ResolverEstanteAsync(_db, codigo, tracking);

    /// <summary>2026-11-27: lo mismo, para otros controllers (Recorrer estantes del celu).</summary>
    public static async Task<(DepositoPlanoElemento? rack, int cara, int pos, int nivel, string? error)> ResolverEstanteAsync(AppDbContext _db, string codigo, bool tracking = false)
    {
        var c = (codigo ?? "").Trim().ToUpperInvariant();
        string letra; int pos, niv;
        // 2026-11-27: C3-F4-R2-A3 (calle, fila, rack de la fila, altura) y -P2 si hay posiciones; sin calle F4-R2-A3
        var mFila = Regex.Match(c, @"^(?:C(\d{1,2})-)?F(\d{1,2})-R(\d{1,2})-A(\d{1,2})(?:-P(\d{1,2}))?$");
        if (mFila.Success)
        {
            int? calleF = mFila.Groups[1].Success ? int.Parse(mFila.Groups[1].Value, CultureInfo.InvariantCulture) : null;
            var fila = int.Parse(mFila.Groups[2].Value, CultureInfo.InvariantCulture);
            var nroF = int.Parse(mFila.Groups[3].Value, CultureInfo.InvariantCulture);
            niv = int.Parse(mFila.Groups[4].Value, CultureInfo.InvariantCulture);
            pos = mFila.Groups[5].Success ? int.Parse(mFila.Groups[5].Value, CultureInfo.InvariantCulture) : 1;
            var qf = _db.DepositoPlanoElementos.Where(x => x.Tipo == "rack" && x.Fila == fila && x.NroFila == nroF);
            var rf = tracking ? await qf.FirstOrDefaultAsync() : await qf.AsNoTracking().FirstOrDefaultAsync();
            if (rf is null) return (null, 0, 0, 0, $"No hay ningún rack {nroF} en la fila {fila}.");
            var caraF = calleF is null ? Caras(rf)[0] : rf.Calle1 == calleF ? 1 : rf.Calle2 == calleF ? 2 : 0;
            if (caraF == 0) return (null, 0, 0, 0, $"El rack {nroF} de la fila {fila} no da a la calle {calleF}.");
            if (pos < 1 || pos > (rf.Posiciones ?? 0) || niv < 1 || niv > (rf.Niveles ?? 0))
                return (null, 0, 0, 0, $"El rack {nroF} de la fila {fila} no tiene altura {niv}{(pos > 1 ? $" posición {pos}" : "")} (tiene {rf.Niveles ?? 0} alturas).");
            return (rf, caraF, pos, niv, null);
        }
        // código anterior (27/11, etiquetas ya impresas): C3-R4-A3 = calle + número de rack en la calle
        var mCalle = Regex.Match(c, @"^C(\d{1,2})-R(\d{1,2})-A(\d{1,2})(?:-P(\d{1,2}))?$");
        if (mCalle.Success)
        {
            var calle = int.Parse(mCalle.Groups[1].Value, CultureInfo.InvariantCulture);
            var nro = int.Parse(mCalle.Groups[2].Value, CultureInfo.InvariantCulture);
            niv = int.Parse(mCalle.Groups[3].Value, CultureInfo.InvariantCulture);
            pos = mCalle.Groups[4].Success ? int.Parse(mCalle.Groups[4].Value, CultureInfo.InvariantCulture) : 1;
            var qc = _db.DepositoPlanoElementos.Where(x => x.Tipo == "rack" && ((x.Calle1 == calle && x.Nro1 == nro) || (x.Calle2 == calle && x.Nro2 == nro)));
            var rk = tracking ? await qc.FirstOrDefaultAsync() : await qc.AsNoTracking().FirstOrDefaultAsync();
            if (rk is null) return (null, 0, 0, 0, $"No hay ningún rack {nro} en la calle {calle}.");
            var cara = rk.Calle1 == calle && rk.Nro1 == nro ? 1 : 2;
            if (pos < 1 || pos > (rk.Posiciones ?? 0) || niv < 1 || niv > (rk.Niveles ?? 0))
                return (null, 0, 0, 0, $"La calle {calle}, rack {nro} no tiene altura {niv}{(pos > 1 ? $" posición {pos}" : "")} (tiene {rk.Niveles ?? 0} alturas{((rk.Posiciones ?? 1) > 1 ? $" y {rk.Posiciones} posiciones" : "")}).");
            return (rk, cara, pos, niv, null);
        }
        var mNuevo = Regex.Match(c, @"^([A-Z0-9]+)-(\d{1,3})([A-Z])?$");
        var mViejo = Regex.Match(c, @"^([A-Z0-9]+)-(\d{1,3})-(\d{1,3})$");
        if (mNuevo.Success)
        {
            letra = mNuevo.Groups[1].Value;
            niv = int.Parse(mNuevo.Groups[2].Value, CultureInfo.InvariantCulture);
            pos = mNuevo.Groups[3].Success ? mNuevo.Groups[3].Value[0] - 'A' + 1 : 1;
        }
        else if (mViejo.Success)
        {
            letra = mViejo.Groups[1].Value;
            pos = int.Parse(mViejo.Groups[2].Value, CultureInfo.InvariantCulture);
            niv = int.Parse(mViejo.Groups[3].Value, CultureInfo.InvariantCulture);
        }
        else return (null, 0, 0, 0, $"El código \"{codigo}\" no es de un estante (tiene que ser como C3-R4-A3: calle, rack y altura).");
        var q = _db.DepositoPlanoElementos.Where(x => x.Tipo == "rack" && x.Codigo == letra);
        var rack = tracking ? await q.FirstOrDefaultAsync() : await q.AsNoTracking().FirstOrDefaultAsync();
        if (rack is null) return (null, 0, 0, 0, $"No hay ningún rack {letra} en el plano.");
        if (pos < 1 || pos > (rack.Posiciones ?? 0) || niv < 1 || niv > (rack.Niveles ?? 0))
            return (null, 0, 0, 0, $"El rack {letra} no tiene el estante {CodigoEstante(letra, Math.Max(rack.Posiciones ?? 1, pos), pos, niv)} (tiene {rack.Niveles ?? 0} niveles{((rack.Posiciones ?? 1) > 1 ? $" y {rack.Posiciones} posiciones" : "")}).");
        // código viejo (sin calle): el primer lado del rack
        return (rack, Caras(rack)[0], pos, niv, null);
    }

    [HttpGet("estante/{codigo}")]
    public async Task<IActionResult> Estante(string codigo)
    {
        var (rack, cara, pos, niv, error) = await ResolverEstante(codigo);
        if (rack is null) return NotFound(new { error });
        var (calle, nro) = LadoDe(rack, cara);
        return Ok(new
        {
            rackId = rack.Id,
            planta = rack.Planta,
            rack = rack.Codigo,
            codigo = CodigoEstante(rack, cara, pos, niv),
            desglose = DesgloseEstante(rack, cara, pos, niv),
            cara,
            calle,
            nro,
            fila = rack.Fila,
            posicion = pos,
            nivel = niv,
            posiciones = rack.Posiciones,
            productos = await AsignacionesDe(rack.Id, pos, niv, cara)
        });
    }

    /// <summary>Guarda un producto en un estante (lo usa el depósito desde el celu). Si ya estaba
    /// en ese estante, solo actualiza cantidad y nota.</summary>
    [HttpPost("estante/{codigo}/productos")]
    public async Task<IActionResult> Asignar(string codigo, [FromBody] AsignarRequest req)
    {
        var (rack, cara, pos, niv, error) = await ResolverEstante(codigo);
        if (rack is null) return NotFound(new { error });
        var prod = await _db.CafeProductos.FirstOrDefaultAsync(p => p.Id == req.ProductoId);
        if (prod is null) return NotFound(new { error = "Ese producto no existe." });

        var cod = CodigoEstante(rack, cara, pos, niv);
        var ahora = DateTime.UtcNow;
        var a = await _db.DepositoEstanteProductos.FirstOrDefaultAsync(x =>
            x.ElementoId == rack.Id && x.Cara == cara && x.Posicion == pos && x.Nivel == niv && x.ProductoId == prod.Id);
        if (a is null)
        {
            a = new DepositoEstanteProducto { ElementoId = rack.Id, Cara = cara, Posicion = pos, Nivel = niv, ProductoId = prod.Id };
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
        var cod = rack is null ? "?" : CodigoEstante(rack, a.Cara, a.Posicion, a.Nivel);

        _db.DepositoEstanteProductos.Remove(a);

        var prod = await _db.CafeProductos.FirstOrDefaultAsync(p => p.Id == a.ProductoId);
        if (prod is not null && prod.UbicacionZona == cod)
        {
            var otra = await _db.DepositoEstanteProductos.AsNoTracking()
                .Where(x => x.ProductoId == a.ProductoId && x.Id != a.Id)
                .Join(_db.DepositoPlanoElementos.AsNoTracking(), x => x.ElementoId, e => e.Id,
                    (x, e) => new { x.At, x.Id, x.Cara, x.Posicion, x.Nivel, Rack = e })
                .OrderByDescending(x => x.At).ThenByDescending(x => x.Id)
                .FirstOrDefaultAsync();
            if (otra is not null)
            {
                prod.UbicacionZona = CodigoEstante(otra.Rack, otra.Cara, otra.Posicion, otra.Nivel);
                prod.UbicacionPlanta = otra.Rack.Planta;
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

    // ───────────────────────── Para acomodar ─────────────────────────

    /// <summary>2026-11-26: productos activos que tienen la ubicación vieja (planta y a veces zona,
    /// ej. "PB · TOST") pero no están guardados en ningún estante del plano. El plano los lista
    /// para irlos acomodando de a uno. Sin planta = todas. Ordenados por zona y nombre, máx. 500.</summary>
    [HttpGet("sin-estante")]
    public async Task<IActionResult> SinEstante([FromQuery] string? planta = null)
    {
        var q = _db.CafeProductos.AsNoTracking().Where(p => p.IsActive && p.UbicacionPlanta != null);
        if (!string.IsNullOrWhiteSpace(planta))
        {
            var pl = planta.Trim().ToUpperInvariant();
            q = q.Where(p => p.UbicacionPlanta == pl);
        }
        var lista = await q
            .Where(p => !_db.DepositoEstanteProductos.Any(a => a.ProductoId == p.Id))
            .OrderBy(p => p.UbicacionZona == null).ThenBy(p => p.UbicacionZona).ThenBy(p => p.Nombre)
            .Take(500)
            .Select(p => new
            {
                productoId = p.Id,
                sku = p.Sku,
                nombre = p.Nombre,
                planta = p.UbicacionPlanta,
                zona = p.UbicacionZona,
                stock = p.StockUnidades
            })
            .ToListAsync();
        return Ok(lista);
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
                (a, e) => new { a.Id, a.ProductoId, a.Cara, a.Posicion, a.Nivel, a.Cantidad, a.At, RackId = e.Id, e.Codigo, e.Planta, Rack = e })
            .ToListAsync();
        var lugaresPor = lugares.GroupBy(l => l.ProductoId).ToDictionary(g => g.Key, g => g.OrderBy(l => l.Codigo).ThenBy(l => l.Cara).ThenBy(l => l.Posicion).ThenBy(l => l.Nivel).ToList());

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
                    codigo = CodigoEstante(l.Rack, l.Cara, l.Posicion, l.Nivel),
                    desglose = DesgloseEstante(l.Rack, l.Cara, l.Posicion, l.Nivel),
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

    /// <summary>Una etiqueta por estante (por lado, posición y nivel) para la Zebra, rollo de MeLi 10×20 cm
    /// (812×1624 puntos a 203 dpi, troquel arriba): el código grande (C3-R4-A3), escrito "Calle 3 · Rack 4 ·
    /// Altura 3" y un QR que abre la página del estante. Se la pasa a window.zebraImprimir(url).</summary>
    [HttpGet("racks/{id:int}/etiquetas.zpl")]
    public async Task<IActionResult> Etiquetas(int id, [FromQuery] string? origen, [FromQuery] string? estante = null)
    {
        // 2026-11-27: ?estante=C3-F4-R1-A3 imprime solo la etiqueta de ese estante
        var solo = string.IsNullOrWhiteSpace(estante) ? null : estante.Trim();
        var o = (origen ?? "").Trim().TrimEnd('/');
        if (!o.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !o.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(o, UriKind.Absolute, out _) || o.Contains('^') || o.Contains('~'))
            return ErrorZpl(400, "Falta la dirección de la página (origen) para armar el QR.");

        var e = await _db.DepositoPlanoElementos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.Tipo == "rack");
        if (e is null) return ErrorZpl(404, "Ese rack no existe.");
        if ((e.Posiciones ?? 0) < 1 || (e.Niveles ?? 0) < 1) return ErrorZpl(400, "El rack no tiene posiciones ni niveles.");

        var sb = new StringBuilder();
        // 2026-11-27: una etiqueta por estante de CADA lado que da a una calle (los racks del medio, dos juegos)
        foreach (var cara in Caras(e))
        for (int pos = 1; pos <= e.Posiciones; pos++)
            for (int niv = 1; niv <= e.Niveles; niv++)
            {
                var cod = CodigoEstante(e, cara, pos, niv);
                if (solo is not null && !string.Equals(cod, solo, StringComparison.OrdinalIgnoreCase)) continue;
                var desglose = DesgloseEstante(e, cara, pos, niv);
                var url = $"{o}/deposito/estante/{cod}";
                // 2026-11-27: mismo rollo que las etiquetas de envío de MeLi (10 x 20 cm, troquel arriba).
                sb.Append("^XA\n^CI28\n^PW812\n^LL1624\n^LH0,0\n");
                // troquel (talón de arriba, como en MeLi)
                sb.Append("^FO16,16^GB780,268,2^FS\n");
                sb.Append($"^FO30,36^A0N,96,80^FB750,1,0,L,0^FD{cod}^FS\n");
                sb.Append($"^FO30,150^A0N,40,36^FB750,1,0,L,0^FD{desglose}^FS\n");
                sb.Append("^FO30,215^A0N,28,26^FB750,1,0,L,0^FDRecortá esta parte antes de pegar en el estante^FS\n");
                // La parte grande de abajo va ACOSTADA (se pega a lo largo del fierro del rack, que es
                // angosto): campos girados (R), se lee girando la etiqueta. A la izquierda el código
                // grande y abajo ESCRITO (calle · rack · altura), a la derecha el QR.
                // (u, v) = posición en la etiqueta acostada; u a lo largo (0..1324), v a lo alto (0..812).
                const int T0 = 300;
                string Acostado(int u, int v, int alto, int ancho, string s)
                    => $"^FO{812 - (v + alto)},{T0 + u}^A0R,{alto},{ancho}^FB860,1,0,C,0^FD{s}^FS\n";
                var grande = cod.Length <= 5 ? (300, 200) : cod.Length <= 8 ? (230, 125) : (200, 105);
                sb.Append(Acostado(20, 80, grande.Item1, grande.Item2, cod));
                sb.Append(Acostado(20, 420, 66, 56, desglose));
                sb.Append(Acostado(20, 505, 42, 38, niv == 1 ? "Altura 1 = el piso" : $"Altura {niv} contando desde el piso"));
                sb.Append(Acostado(20, 600, 40, 36, "Escaneá con el celu para ver o guardar productos"));
                // QR (aumento 11 ≈ 360 puntos con la dirección larga de C-F-R-A; se lee en cualquier sentido)
                sb.Append($"^FO226,{T0 + 920}^BQN,2,11^FDLA,{url}^FS\n");
                sb.Append("^XZ\n");
            }
        if (sb.Length == 0) return ErrorZpl(404, "Ese estante no está en este rack.");
        return Content(sb.ToString(), "text/plain; charset=utf-8");
    }
}
