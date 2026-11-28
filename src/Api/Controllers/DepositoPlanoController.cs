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
        int? Calle1, int? Nro1, int? Calle2, int? Nro2, int? Fila, int? NroFila,
        string? Forma = null, decimal? Elevacion = null, int? CallePunta1 = null, int? CallePunta2 = null,
        List<DibujoDto>? Dibujos = null, string? AlturasCm = null);

    /// <summary>2026-11-28: lo que se dibuja en un estante del rack (ver DepositoEstanteNota.Dibujo).</summary>
    public record DibujoDto(int Cara, int Posicion, int Nivel, string? Dibujo, int? Cant, string? Texto, string? Foto);

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
        // 2026-11-27: "piso" (lugar en el piso de una calle) / "estanteria" (estantería suelta) / null = rack
        public string? Forma { get; set; }
        public decimal? Elevacion { get; set; }
        // 2026-11-28: calles de las puntas (caras 3 y 4)
        public int? CallePunta1 { get; set; }
        public int? CallePunta2 { get; set; }
        // 2026-11-28: alto de cada altura en cm, de abajo hacia arriba ("136,100,60")
        public string? AlturasCm { get; set; }
    }

    public record AsignacionDto(int Id, int ProductoId, string? Sku, string Nombre,
        string? Cantidad, string? Nota, DateTime At, string? Por, int? Stock = null);

    public record EstanteDto(int Posicion, int Nivel, string Codigo, List<AsignacionDto> Productos,
        int Cara = 1, string? Desglose = null, string? Nota = null, string? NotaPor = null, DateTime? NotaAt = null,
        string? Dibujo = null, int? DibujoCant = null, string? DibujoTexto = null, string? Foto = null);

    // ───────────── 2026-11-28: dibujo y foto del estante ─────────────
    public static readonly string[] Dibujos = { "bolsas", "cajas", "bidones", "sillas", "pallet", "rollos", "bultos", "foto" };
    public const string FotosEstanteDir = "/data/files/estante-fotos";
    public static string? UrlFoto(string? archivo) => string.IsNullOrWhiteSpace(archivo) ? null : $"api/deposito/plano/foto/{archivo}";
    public static string? NotaTexto(DepositoEstanteNota? n) => string.IsNullOrWhiteSpace(n?.Nota) ? null : n!.Nota;

    private static async Task<DepositoEstanteNota> FilaNotaAsync(AppDbContext db, int rackId, int cara, int pos, int niv)
    {
        var n = await db.DepositoEstanteNotas.FirstOrDefaultAsync(x => x.ElementoId == rackId && x.Cara == cara && x.Posicion == pos && x.Nivel == niv);
        if (n is null)
        {
            n = new DepositoEstanteNota { ElementoId = rackId, Cara = cara, Posicion = pos, Nivel = niv, Nota = "" };
            db.DepositoEstanteNotas.Add(n);
        }
        return n;
    }

    /// <summary>La fila se borra cuando no le queda ni comentario, ni dibujo, ni foto.</summary>
    private static void SacarSiVacia(AppDbContext db, DepositoEstanteNota n)
    {
        if (string.IsNullOrWhiteSpace(n.Nota) && n.Dibujo is null && n.Foto is null) db.DepositoEstanteNotas.Remove(n);
    }

    /// <summary>Guarda (o cambia) la foto del estante. La usan el plano y Recorrer estantes (celu).</summary>
    public static async Task<(string? Url, string? Error)> GuardarFotoEstanteAsync(AppDbContext db, int rackId, int cara, int pos, int niv, IFormFile? file, string? por)
    {
        if (file is null || file.Length == 0) return (null, "No llegó ninguna foto.");
        if (file.Length > 15 * 1024 * 1024) return (null, "La foto es muy grande (máximo 15 MB).");
        if (!(file.ContentType ?? "").StartsWith("image/")) return (null, "El archivo tiene que ser una foto.");
        var ext = Path.GetExtension(file.FileName ?? "").ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp" or ".gif")) ext = ".jpg";
        Directory.CreateDirectory(FotosEstanteDir);
        var nombre = $"est-{rackId}-{cara}-{pos}-{niv}-{Guid.NewGuid():N}{ext}";
        await using (var fs = System.IO.File.Create(Path.Combine(FotosEstanteDir, nombre))) await file.CopyToAsync(fs);
        var n = await FilaNotaAsync(db, rackId, cara, pos, niv);
        var viejo = n.Foto;
        n.Foto = nombre; n.At = DateTime.UtcNow; n.Por = por;
        await db.SaveChangesAsync();
        BorrarArchivo(viejo);
        return (UrlFoto(nombre), null);
    }

    private static void BorrarArchivo(string? nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre)) return;
        try { System.IO.File.Delete(Path.Combine(FotosEstanteDir, Path.GetFileName(nombre))); } catch { }
    }

    public class DibujoRequest
    {
        public string? Dibujo { get; set; }
        public int? Cantidad { get; set; }
        public string? Texto { get; set; }
    }

    public class NotaRequest
    {
        public string? Nota { get; set; }
    }

    /// <summary>2026-11-27: guarda (o borra, si viene vacío) el comentario de un estante.</summary>
    public static async Task<DepositoEstanteNota?> GuardarNotaAsync(AppDbContext db, int rackId, int cara, int pos, int niv, string? texto, string? por)
    {
        var t = (texto ?? "").Trim();
        if (t.Length > 500) t = t[..500];
        var n = await db.DepositoEstanteNotas.FirstOrDefaultAsync(x => x.ElementoId == rackId && x.Cara == cara && x.Posicion == pos && x.Nivel == niv);
        if (t.Length == 0)
        {
            // 2026-11-28: si tiene dibujo o foto, la fila queda (solo sin comentario)
            if (n is not null) { n.Nota = ""; SacarSiVacia(db, n); await db.SaveChangesAsync(); }
            return null;
        }
        if (n is null)
        {
            n = new DepositoEstanteNota { ElementoId = rackId, Cara = cara, Posicion = pos, Nivel = niv };
            db.DepositoEstanteNotas.Add(n);
        }
        n.Nota = t; n.At = DateTime.UtcNow; n.Por = por;
        await db.SaveChangesAsync();
        return n;
    }

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
        e.Calle1, e.Nro1, e.Calle2, e.Nro2, e.Fila, e.NroFila, e.Forma, e.Elevacion, e.CallePunta1, e.CallePunta2, AlturasCm: e.AlturasCm);

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
        var posTxt = PosicionesDe(e, cara) > 1 ? $"-P{pos}" : "";
        if (EsPiso(e)) return CodigoPiso(e) + posTxt;
        if (EsEstanteria(e)) return $"{e.Codigo}-A{nivel}" + posTxt;
        if (e.Fila is int f && e.NroFila is int r)
            return (calle is int c ? $"C{c}-" : "") + $"F{f}-R{r}-A{nivel}" + posTxt;
        return CodigoEstante(e.Codigo ?? "?", e.Posiciones, pos, nivel);
    }

    /// <summary>"Calle 3 · Fila 4 · Rack 2 · Altura 3" — para leerlo escrito.</summary>
    public static string DesgloseEstante(DepositoPlanoElemento e, int cara, int pos, int nivel)
    {
        var calle = CalleDe(e, cara);
        var posTxt = PosicionesDe(e, cara) > 1 ? $" · Posición {pos}" : "";
        if (NormCara(e, cara) >= 3 && e.Fila is int fp && e.NroFila is int rp)
            return (calle is int cpu ? $"Calle {cpu} · " : "") + $"Fila {fp} · Rack {rp} (punta) · Altura {nivel}";
        if (EsPiso(e)) return (calle is int cp ? $"Calle {cp} · " : "") + $"Piso · Lugar {e.NroFila ?? 1}{posTxt}";
        if (EsEstanteria(e)) return $"{TituloEstanteria(e)} · Altura {nivel}{posTxt}";
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
        // 2026-11-28: las puntas que dan a una calle (3 = lado entrada / pared izq., 4 = la otra)
        if (!EsPiso(e) && !EsEstanteria(e))
        {
            if (e.CallePunta1 is not null) l.Add(3);
            if (e.CallePunta2 is not null) l.Add(4);
        }
        return l;
    }

    /// <summary>2026-11-28: cuántas posiciones tiene ese lado (las puntas son angostas: una sola).</summary>
    public static int PosicionesDe(DepositoPlanoElemento e, int cara) => NormCara(e, cara) >= 3 ? 1 : (e.Posiciones ?? 1);

    /// <summary>El lado pedido si existe; si no, el único que tiene (racks de pared).</summary>
    public static int NormCara(DepositoPlanoElemento e, int cara)
    {
        var cs = Caras(e);
        return cs.Contains(cara) ? cara : cs[0];
    }

    public static int? CalleDe(DepositoPlanoElemento e, int cara)
        => NormCara(e, cara) switch { 2 => e.Calle2, 3 => e.CallePunta1, 4 => e.CallePunta2, _ => e.Calle1 };

    /// <summary>2026-11-28: qué lado o punta del rack da a esa calle (0 = ninguno).</summary>
    public static int CaraDeCalle(DepositoPlanoElemento e, int calle)
        => e.Calle1 == calle ? 1 : e.Calle2 == calle ? 2 : e.CallePunta1 == calle ? 3 : e.CallePunta2 == calle ? 4 : 0;

    /// <summary>(calle de ese lado, número de rack en la fila).</summary>
    public static (int? Calle, int? Nro) LadoDe(DepositoPlanoElemento e, int cara) => (CalleDe(e, cara), e.NroFila);

    /// <summary>"F4-R2" (o el código de siempre si no tiene fila).</summary>
    public static string NombreRack(DepositoPlanoElemento e)
        => EsPiso(e) ? CodigoPiso(e)
         : e.Fila is int f && e.NroFila is int r ? $"F{f}-R{r}" : (e.Codigo ?? "?");

    /// <summary>2026-11-27: lugar en el piso de una calle (no es un rack).</summary>
    public static bool EsPiso(DepositoPlanoElemento e) => e.Forma == "piso";
    /// <summary>2026-11-27: estantería suelta con nombre corto (tostadero, arriba del escritorio).</summary>
    public static bool EsEstanteria(DepositoPlanoElemento e) => e.Forma == "estanteria";
    /// <summary>"C3-PISO1" (sin calle: "PISO1").</summary>
    public static string CodigoPiso(DepositoPlanoElemento e)
        => (e.Calle1 is int c ? $"C{c}-" : "") + $"PISO{e.NroFila ?? 1}";
    /// <summary>"Estantería TOST" o el nombre que le pusieron.</summary>
    public static string TituloEstanteria(DepositoPlanoElemento e)
        => string.IsNullOrWhiteSpace(e.Nombre) ? $"Estantería {e.Codigo}" : e.Nombre!;

    private static List<CaraDto> CarasDto(DepositoPlanoElemento e) => Caras(e).Select(c =>
    {
        var calle = CalleDe(e, c);
        if (EsPiso(e)) return new CaraDto(c, calle, e.NroFila, (calle is int cp ? $"Calle {cp} · " : "") + $"Piso · Lugar {e.NroFila ?? 1}");
        if (EsEstanteria(e)) return new CaraDto(c, null, null, TituloEstanteria(e));
        return new CaraDto(c, calle, e.NroFila, (calle is int cc ? $"Calle {cc} · " : "") + (e.Fila is int f && e.NroFila is int r ? $"Fila {f} · Rack {r}" : $"Rack {e.Codigo}") + (c >= 3 ? " (punta)" : ""));
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
                a.Id, a.Posicion, a.Nivel, a.ProductoId, p.Sku, p.Nombre, a.Cantidad, a.Nota, a.At, a.Por, p.StockUnidades
            })
            .OrderBy(x => x.Nombre)
            .ToListAsync();
        return lista.Select(x => new AsignacionDto(x.Id, x.ProductoId, x.Sku, x.Nombre, x.Cantidad, x.Nota, x.At, x.Por, x.StockUnidades)).ToList();
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
        // 2026-11-28: lo que se dibuja en cada estante (para el 3D)
        var dibs = (await _db.DepositoEstanteNotas.AsNoTracking()
                .Where(n => ids.Contains(n.ElementoId) && (n.Dibujo != null || n.Foto != null)).ToListAsync())
            .GroupBy(n => n.ElementoId)
            .ToDictionary(g => g.Key, g => g.Select(n => new DibujoDto(n.Cara, n.Posicion, n.Nivel, n.Dibujo, n.DibujoCant, n.DibujoTexto, UrlFoto(n.Foto))).ToList());
        return Ok(new
        {
            planta = pl,
            elementos = elementos.Select(e => ADto(e, conteos.GetValueOrDefault(e.Id)) with { Dibujos = dibs.GetValueOrDefault(e.Id) }).ToList()
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
            r.Forma = string.IsNullOrWhiteSpace(r.Forma) ? null : r.Forma.Trim().ToLowerInvariant();
            if (r.Forma != "piso" && (r.Fila is null) != (r.NroFila is null)) return "Poné la fila y el número de rack en la fila (los dos).";
            foreach (var v in new[] { r.Calle1, r.Calle2, r.Fila, r.NroFila, r.CallePunta1, r.CallePunta2 })
                if (v is not null && (v < 0 || v > 99)) return "La calle, la fila y el rack van de 0 a 99.";
            if (r.Calle1 is not null && r.Calle1 == r.Calle2) return "Los dos lados del rack no pueden dar a la misma calle.";
            // 2026-11-28: cada lado y cada punta, una calle distinta (el código del estante sale de la calle)
            var calles = new[] { r.Calle1, r.Calle2, r.CallePunta1, r.CallePunta2 }.Where(c => c is not null).ToList();
            if (calles.Count != calles.Distinct().Count()) return "Cada lado y cada punta del rack tienen que dar a una calle distinta.";
            // 2026-11-27: lugares que no son racks
            if (r.Forma is not null && r.Forma != "piso" && r.Forma != "estanteria") return "Clase de lugar desconocida.";
            if (r.Forma == "piso")
            {
                // un lugar en el piso: una calle, un número de lugar, un solo "estante"
                if (r.Calle1 is null) return "Poné en qué calle está el lugar en el piso.";
                if (r.NroFila is null or < 1) return "Poné el número del lugar en el piso (1, 2, 3…).";
                r.Calle2 = null; r.Nro1 = r.Nro2 = null; r.Fila = null; r.CallePunta1 = r.CallePunta2 = null;
                r.Niveles = 1; r.Posiciones = 1; r.Elevacion = null;
            }
            else if (r.Forma == "estanteria")
            {
                // estantería suelta: se llama por su código corto (TOST-A3), sin calle ni fila
                if (Regex.IsMatch(r.Codigo, "^(C?\\d|F\\d|PISO)")) return "El nombre corto de la estantería tiene que empezar con letras (por ejemplo TOST o ESC).";
                r.Calle1 = r.Calle2 = r.Nro1 = r.Nro2 = r.Fila = r.NroFila = r.CallePunta1 = r.CallePunta2 = null;
                if (r.Elevacion is not null && (r.Elevacion < 0 || r.Elevacion > 10)) return "La altura desde el piso va de 0 a 10 metros.";
            }
            else r.Elevacion = null;
            // 2026-11-28: alto de cada altura (cm, de abajo hacia arriba); tiene que haber uno por altura
            if (string.IsNullOrWhiteSpace(r.AlturasCm) || r.Forma == "piso") r.AlturasCm = null;
            else
            {
                var partes = r.AlturasCm.Split(new[] { ',', ';', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
                var cms = new List<int>();
                foreach (var t in partes)
                {
                    if (!int.TryParse(t.Trim(), out var cm) || cm < 5 || cm > 500) return "El alto de cada altura va en centímetros, por ejemplo 136, 100, 60.";
                    cms.Add(cm);
                }
                if (cms.Count != r.Niveles) return $"Poné un alto por cada altura: el rack tiene {r.Niveles} y pusiste {cms.Count}.";
                r.AlturasCm = string.Join(",", cms);
            }
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
        e.Forma = rack ? r.Forma : null; e.Elevacion = rack ? r.Elevacion : null;
        e.CallePunta1 = rack ? r.CallePunta1 : null; e.CallePunta2 = rack ? r.CallePunta2 : null;
        e.AlturasCm = rack ? r.AlturasCm : null;
    }

    /// <summary>2026-11-27: ¿algún otro rack ya es esa fila + número? Devuelve el error o null.</summary>
    private async Task<string?> CalleRepetida(ElementoRequest r, int? idPropio)
    {
        if (EsRack(r.Tipo) && r.Forma == "piso")
        {
            var hay = await _db.DepositoPlanoElementos.AsNoTracking()
                .AnyAsync(x => x.Tipo == "rack" && x.Id != (idPropio ?? 0) && x.Forma == "piso" && x.Calle1 == r.Calle1 && x.NroFila == r.NroFila);
            return hay ? $"La calle {r.Calle1} ya tiene un lugar en el piso {r.NroFila}." : null;
        }
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
                .Where(a => (a.Cara <= 2 && a.Posicion > req.Posiciones) || a.Nivel > req.Niveles)
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
            Forma = e.Forma, Nombre = e.Nombre, CallePunta1 = e.CallePunta1, CallePunta2 = e.CallePunta2,
        };
        Copiar(req, e);
        e.ModificadoAt = DateTime.UtcNow;
        e.ModificadoPor = Usuario;

        if (EsRack(e.Tipo) && asigs.Count > 0)
        {
            // 2026-11-27: si un lado con productos se queda sin calle y el otro sí tiene, no se puede
            // (los productos quedarían "del otro lado").
            var carasNuevas = Caras(e);
            foreach (var perdida in Caras(antes).Where(c => !carasNuevas.Contains(c)))
            {
                // 2026-11-28: lados y puntas: si se queda sin calle uno que tiene productos, no se puede
                if (perdida <= 2 && Caras(antes).Count(c => c <= 2) < 2) continue;
                var n = asigs.Count(a => a.Cara == perdida);
                if (n > 0)
                    return Conflict(new { error = $"{CarasDto(antes).First(x => x.Cara == perdida).Titulo} tiene {n} productos guardados. Sacalos antes de sacarle la calle." });
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

        var notas = await _db.DepositoEstanteNotas.AsNoTracking().Where(n => n.ElementoId == id).ToListAsync();
        // 2026-11-27: un juego de estantes por cada lado que da a una calle
        var estantes = new List<EstanteDto>();
        foreach (var cara in Caras(e))
            for (int pos = 1; pos <= PosicionesDe(e, cara); pos++)
                for (int niv = 1; niv <= (e.Niveles ?? 0); niv++)
                {
                    var nt = notas.FirstOrDefault(n => NormCara(e, n.Cara) == cara && n.Posicion == pos && n.Nivel == niv);
                    estantes.Add(new EstanteDto(pos, niv, CodigoEstante(e, cara, pos, niv),
                        asigs.Where(a => celdaDe.TryGetValue(a.Id, out var c) && c.Cara == cara && c.Posicion == pos && c.Nivel == niv).ToList(),
                        cara, DesgloseEstante(e, cara, pos, niv), NotaTexto(nt), nt?.Por, nt?.At,
                        nt?.Dibujo, nt?.DibujoCant, nt?.DibujoTexto, UrlFoto(nt?.Foto)));
                }

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
            var caraF = calleF is null ? Caras(rf)[0] : CaraDeCalle(rf, calleF.Value);
            if (caraF == 0) return (null, 0, 0, 0, $"El rack {nroF} de la fila {fila} no da a la calle {calleF}.");
            if (pos < 1 || pos > PosicionesDe(rf, caraF) || niv < 1 || niv > (rf.Niveles ?? 0))
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
        // 2026-11-27: lugar en el piso de una calle: C3-PISO1 (-P2 si tiene varias posiciones)
        var mPiso = Regex.Match(c, @"^(?:C(\d{1,2})-)?PISO(\d{1,2})(?:-P(\d{1,2}))?$");
        if (mPiso.Success)
        {
            int? calleP = mPiso.Groups[1].Success ? int.Parse(mPiso.Groups[1].Value, CultureInfo.InvariantCulture) : null;
            var nroP = int.Parse(mPiso.Groups[2].Value, CultureInfo.InvariantCulture);
            pos = mPiso.Groups[3].Success ? int.Parse(mPiso.Groups[3].Value, CultureInfo.InvariantCulture) : 1;
            var qp = _db.DepositoPlanoElementos.Where(x => x.Tipo == "rack" && x.Forma == "piso" && x.NroFila == nroP && (calleP == null || x.Calle1 == calleP));
            var rp = tracking ? await qp.FirstOrDefaultAsync() : await qp.AsNoTracking().FirstOrDefaultAsync();
            if (rp is null) return (null, 0, 0, 0, calleP is null ? $"No hay ningún lugar en el piso {nroP}." : $"La calle {calleP} no tiene un lugar en el piso {nroP}.");
            if (pos < 1 || pos > (rp.Posiciones ?? 1)) return (null, 0, 0, 0, $"El lugar {CodigoPiso(rp)} no tiene posición {pos}.");
            return (rp, 1, pos, 1, null);
        }
        // 2026-11-27: estantería suelta: TOST-A3 (nombre corto + altura)
        var mEst = Regex.Match(c, @"^([A-Z][A-Z0-9]*)-A(\d{1,2})(?:-P(\d{1,2}))?$");
        if (mEst.Success)
        {
            var nom = mEst.Groups[1].Value;
            niv = int.Parse(mEst.Groups[2].Value, CultureInfo.InvariantCulture);
            pos = mEst.Groups[3].Success ? int.Parse(mEst.Groups[3].Value, CultureInfo.InvariantCulture) : 1;
            var qe = _db.DepositoPlanoElementos.Where(x => x.Tipo == "rack" && x.Forma == "estanteria" && x.Codigo == nom);
            var re = tracking ? await qe.FirstOrDefaultAsync() : await qe.AsNoTracking().FirstOrDefaultAsync();
            if (re is null) return (null, 0, 0, 0, $"No hay ninguna estantería {nom} en el plano.");
            if (pos < 1 || pos > (re.Posiciones ?? 0) || niv < 1 || niv > (re.Niveles ?? 0))
                return (null, 0, 0, 0, $"La estantería {nom} no tiene altura {niv}{(pos > 1 ? $" posición {pos}" : "")} (tiene {re.Niveles ?? 0} alturas).");
            return (re, 1, pos, niv, null);
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

    // ───────────── 2026-11-28: UNA etiqueta por rack (por lado): el QR trae el rack sin altura ─────────────

    /// <summary>Código del rack entero (sin altura), el de la etiqueta única: "C3-F4-R2" (sin calle "F4-R2"),
    /// estantería "TOST", lugar en el piso "C3-PISO1" (ese ya es un solo estante), rack viejo sin fila: su código.</summary>
    public static string CodigoRack(DepositoPlanoElemento e, int cara)
    {
        if (EsPiso(e)) return CodigoPiso(e);
        if (EsEstanteria(e)) return e.Codigo ?? "?";
        var calle = CalleDe(e, cara);
        if (e.Fila is int f && e.NroFila is int r) return (calle is int c ? $"C{c}-" : "") + $"F{f}-R{r}";
        return e.Codigo ?? "?";
    }

    /// <summary>"Calle 3 · Fila 4 · Rack 2" (el lado del rack, sin altura).</summary>
    public static string DesgloseRack(DepositoPlanoElemento e, int cara)
        => CarasDto(e).FirstOrDefault(x => x.Cara == NormCara(e, cara))?.Titulo ?? NombreRack(e);

    /// <summary>Si el código es de un rack ENTERO (C3-F4-R2, F4-R2, TOST, código viejo de rack) lo busca.
    /// rack null y error null = no es un código de rack (probar como estante).</summary>
    public static async Task<(DepositoPlanoElemento? rack, int cara, string? error)> ResolverRackAsync(AppDbContext _db, string codigo)
    {
        var c = (codigo ?? "").Trim().ToUpperInvariant();
        var m = Regex.Match(c, @"^(?:C(\d{1,2})-)?F(\d{1,2})-R(\d{1,2})$");
        if (m.Success)
        {
            int? calle = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
            var fila = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var nro = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            var r = await _db.DepositoPlanoElementos.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Tipo == "rack" && x.Fila == fila && x.NroFila == nro);
            if (r is null) return (null, 0, $"No hay ningún rack {nro} en la fila {fila}.");
            var cara = calle is null ? Caras(r)[0] : CaraDeCalle(r, calle.Value);
            if (cara == 0) return (null, 0, $"El rack {nro} de la fila {fila} no da a la calle {calle}.");
            return (r, cara, null);
        }
        // nombre solo: estantería (TOST) o rack viejo sin fila (D2)
        if (Regex.IsMatch(c, @"^[A-Z][A-Z0-9]{0,11}$") && !Regex.IsMatch(c, @"^(?:C?\d|PISO\d)"))
        {
            var l = await _db.DepositoPlanoElementos.AsNoTracking()
                .Where(x => x.Tipo == "rack" && x.Codigo == c && x.Forma != "piso").ToListAsync();
            var r = l.FirstOrDefault(EsEstanteria) ?? l.FirstOrDefault(x => x.Fila is null);
            if (r is not null) return (r, Caras(r)[0], null);
        }
        return (null, 0, null);
    }

    /// <summary>Los estantes de ese lado del rack, de ARRIBA hacia abajo (como se ve el rack), para elegir la altura.</summary>
    public static async Task<object> ElegirAlturaAsync(AppDbContext _db, DepositoPlanoElemento rack, int cara, HashSet<(int, int, int, int)>? revisados = null)
    {
        var asigs = await _db.DepositoEstanteProductos.AsNoTracking().Where(a => a.ElementoId == rack.Id)
            .Select(a => new { a.Cara, a.Posicion, a.Nivel }).ToListAsync();
        var notas = await _db.DepositoEstanteNotas.AsNoTracking().Where(n => n.ElementoId == rack.Id && n.Nota != "")
            .Select(n => new { n.Cara, n.Posicion, n.Nivel }).ToListAsync();
        var alturas = new List<object>();
        for (int n = rack.Niveles ?? 1; n >= 1; n--)
            for (int p = 1; p <= PosicionesDe(rack, cara); p++)
                alturas.Add(new
                {
                    nivel = n,
                    posicion = p,
                    codigo = CodigoEstante(rack, cara, p, n),
                    productos = asigs.Count(a => NormCara(rack, a.Cara) == cara && a.Posicion == p && a.Nivel == n),
                    nota = notas.Any(x => NormCara(rack, x.Cara) == cara && x.Posicion == p && x.Nivel == n),
                    revisado = revisados?.Contains((rack.Id, cara, p, n)) ?? false,
                });
        var (calle, nro) = LadoDe(rack, cara);
        return new
        {
            elegir = true,
            codigo = CodigoRack(rack, cara),
            desglose = DesgloseRack(rack, cara),
            planta = rack.Planta,
            rackId = rack.Id,
            rack = rack.Codigo,
            forma = rack.Forma,
            nombreRack = NombreRack(rack),
            cara, calle, nro, fila = rack.Fila,
            niveles = rack.Niveles ?? 1,
            posiciones = PosicionesDe(rack, cara),
            alturas,
        };
    }

    [HttpGet("estante/{codigo}")]
    public async Task<IActionResult> Estante(string codigo)
    {
        // 2026-11-28: el QR de la etiqueta única del rack trae el rack sin altura: se elige en el celu
        var (rackE, caraE, errorE) = await ResolverRackAsync(_db, codigo);
        if (errorE is not null) return NotFound(new { error = errorE });
        if (rackE is not null) return Ok(await ElegirAlturaAsync(_db, rackE, caraE));

        var (rack, cara, pos, niv, error) = await ResolverEstante(codigo);
        if (rack is null) return NotFound(new { error });
        var (calle, nro) = LadoDe(rack, cara);
        var nota = await _db.DepositoEstanteNotas.AsNoTracking()
            .FirstOrDefaultAsync(n => n.ElementoId == rack.Id && n.Cara == cara && n.Posicion == pos && n.Nivel == niv);
        return Ok(new
        {
            nota = NotaTexto(nota),
            notaPor = nota?.Por,
            notaAt = nota?.At,
            dibujo = nota?.Dibujo,
            dibujoCant = nota?.DibujoCant,
            dibujoTexto = nota?.DibujoTexto,
            foto = UrlFoto(nota?.Foto),
            rackId = rack.Id,
            planta = rack.Planta,
            rack = rack.Codigo,
            codigo = CodigoEstante(rack, cara, pos, niv),
            desglose = DesgloseEstante(rack, cara, pos, niv),
            forma = rack.Forma,
            cara,
            calle,
            nro,
            fila = rack.Fila,
            posicion = pos,
            nivel = niv,
            posiciones = PosicionesDe(rack, cara),
            productos = await AsignacionesDe(rack.Id, pos, niv, cara)
        });
    }

    /// <summary>2026-11-28: qué se ve en el estante aunque no tenga stock (dibujo null = nada).</summary>
    [HttpPut("estante/{codigo}/dibujo")]
    public async Task<IActionResult> Dibujo(string codigo, [FromBody] DibujoRequest req)
    {
        var (rack, cara, pos, niv, error) = await ResolverEstante(codigo);
        if (rack is null) return NotFound(new { error });
        var d = string.IsNullOrWhiteSpace(req.Dibujo) ? null : req.Dibujo.Trim().ToLowerInvariant();
        if (d is not null && !Dibujos.Contains(d)) return BadRequest(new { error = "Ese dibujo no existe." });
        if (req.Cantidad is < 0 or > 9999) return BadRequest(new { error = "La cantidad va de 0 a 9999." });
        var n = await FilaNotaAsync(_db, rack.Id, cara, pos, niv);
        n.Dibujo = d;
        n.DibujoCant = d is null ? null : req.Cantidad;
        n.DibujoTexto = d is null ? null : Recortar(req.Texto, 100);
        n.At = DateTime.UtcNow; n.Por = Usuario;
        SacarSiVacia(_db, n);
        await _db.SaveChangesAsync();
        return Ok(new { dibujo = n.Dibujo, dibujoCant = n.DibujoCant, dibujoTexto = n.DibujoTexto });
    }

    /// <summary>2026-11-28: foto del estante (la cambia si ya tenía).</summary>
    [HttpPost("estante/{codigo}/foto")]
    [RequestSizeLimit(16 * 1024 * 1024)]
    public async Task<IActionResult> SubirFoto(string codigo, IFormFile? file)
    {
        var (rack, cara, pos, niv, error) = await ResolverEstante(codigo);
        if (rack is null) return NotFound(new { error });
        var (url, err) = await GuardarFotoEstanteAsync(_db, rack.Id, cara, pos, niv, file, Usuario);
        return err is null ? Ok(new { foto = url }) : BadRequest(new { error = err });
    }

    [HttpDelete("estante/{codigo}/foto")]
    public async Task<IActionResult> SacarFoto(string codigo)
    {
        var (rack, cara, pos, niv, error) = await ResolverEstante(codigo);
        if (rack is null) return NotFound(new { error });
        var n = await _db.DepositoEstanteNotas.FirstOrDefaultAsync(x => x.ElementoId == rack.Id && x.Cara == cara && x.Posicion == pos && x.Nivel == niv);
        if (n?.Foto is null) return NoContent();
        var viejo = n.Foto;
        n.Foto = null;
        SacarSiVacia(_db, n);
        await _db.SaveChangesAsync();
        BorrarArchivo(viejo);
        return NoContent();
    }

    /// <summary>2026-11-28: la foto del estante (nombre con GUID: no se puede adivinar). Sin login porque
    /// también la ve el celu del depósito en Recorrer estantes (que entra con la clave del día).</summary>
    [HttpGet("foto/{nombre}")]
    [AllowAnonymous]
    public IActionResult Foto(string nombre)
    {
        if (!Regex.IsMatch(nombre ?? "", @"^est-[\d-]+-[0-9a-f]{32}\.(jpg|jpeg|png|webp|gif)$")) return NotFound();
        var ruta = Path.Combine(FotosEstanteDir, nombre!);
        if (!System.IO.File.Exists(ruta)) return NotFound();
        var tipo = Path.GetExtension(ruta) switch { ".png" => "image/png", ".webp" => "image/webp", ".gif" => "image/gif", _ => "image/jpeg" };
        Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return PhysicalFile(ruta, tipo);
    }

    /// <summary>2026-11-27: comentario libre del estante (vacío lo borra).</summary>
    [HttpPut("estante/{codigo}/nota")]
    public async Task<IActionResult> Nota(string codigo, [FromBody] NotaRequest req)
    {
        var (rack, cara, pos, niv, error) = await ResolverEstante(codigo);
        if (rack is null) return NotFound(new { error });
        var n = await GuardarNotaAsync(_db, rack.Id, cara, pos, niv, req.Nota, Usuario);
        return Ok(new { nota = n?.Nota, notaPor = n?.Por, notaAt = n?.At });
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
        return Ok(new AsignacionDto(a.Id, prod.Id, prod.Sku, prod.Nombre, a.Cantidad, a.Nota, a.At, a.Por, prod.StockUnidades));
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
            .Select(p => new { p.Id, p.Sku, p.Nombre, p.Barcode, p.StockUnidades })
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
                stock = c.StockUnidades,
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
    public async Task<IActionResult> Etiquetas(int id, [FromQuery] string? origen, [FromQuery] string? estante = null, [FromQuery] bool porRack = false)
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
        // 2026-11-28: ?porRack=true → UNA etiqueta por lado del rack; el QR abre el rack y en el celu se elige
        // la altura. Los lugares en el piso ya son un solo estante: siguen con la suya de siempre.
        if (porRack && !EsPiso(e))
        {
            foreach (var cara in Caras(e))
            {
                var cod = CodigoRack(e, cara);
                var url = $"{o}/deposito/estante/{cod}";
                var desg = DesgloseRack(e, cara);
                var alturas = (e.Niveles ?? 1) == 1 ? "1 altura" : $"alturas A1 a A{e.Niveles}";
                sb.Append("^XA\n^CI28\n^PW812\n^LL1624\n^LH0,0\n");
                // troquel: el código del rack en negro, por si quieren pegar otro en el otro parante
                sb.Append("^FO16,16^GB780,268,2^FS\n");
                sb.Append("^FO28,26^GB510,248,248^FS\n");
                sb.Append($"^FO28,60^A0N,{(cod.Length <= 6 ? 150 : 110)},{(cod.Length <= 6 ? 90 : cod.Length <= 8 ? 62 : 50)}^FR^FB510,1,0,C,0^FD{cod}^FS\n");
                sb.Append($"^FO28,200^A0N,30,26^FR^FB510,1,0,C,0^FDElegí la altura en el celu^FS\n");
                sb.Append($"^FO552,14^BQN,2,7^FDLA,{url}^FS\n");
                const int T0 = 300;
                string Acostado(int u, int v, int alto, int ancho, string s)
                    => $"^FO{812 - (v + alto)},{T0 + u}^A0R,{alto},{ancho}^FB860,1,0,C,0^FD{s}^FS\n";
                var grande = cod.Length <= 5 ? (300, 200) : cod.Length <= 8 ? (230, 125) : (200, 105);
                sb.Append(Acostado(20, 80, grande.Item1, grande.Item2, cod));
                sb.Append(Acostado(20, 420, 66, 56, desg));
                sb.Append(Acostado(20, 505, 42, 38, $"Escaneá y elegí la altura ({alturas})"));
                sb.Append(Acostado(20, 600, 40, 36, "Una sola etiqueta para todo el rack"));
                sb.Append($"^FO226,{T0 + 920}^BQN,2,11^FDLA,{url}^FS\n");
                sb.Append("^XZ\n");
            }
            return Content(sb.ToString(), "text/plain; charset=utf-8");
        }
        // 2026-11-27: una etiqueta por estante de CADA lado que da a una calle (los racks del medio, dos juegos)
        foreach (var cara in Caras(e))
        for (int pos = 1; pos <= PosicionesDe(e, cara); pos++)
            for (int niv = 1; niv <= e.Niveles; niv++)
            {
                var cod = CodigoEstante(e, cara, pos, niv);
                if (solo is not null && !string.Equals(cod, solo, StringComparison.OrdinalIgnoreCase)) continue;
                var desglose = DesgloseEstante(e, cara, pos, niv);
                var url = $"{o}/deposito/estante/{cod}";
                // 2026-11-27: mismo rollo que las etiquetas de envío de MeLi (10 x 20 cm, troquel arriba).
                sb.Append("^XA\n^CI28\n^PW812\n^LL1624\n^LH0,0\n");
                // troquel (talón de arriba, como en MeLi). 2026-11-27: es el cartel de ALTURA que se pega en
                // el estante donde va la mercadería (el QR grande de abajo se pega a una altura cómoda para
                // escanear): A3 grande en negro, el código, y el mismo QR chico (aumento 7, se lee bien).
                sb.Append("^FO16,16^GB780,268,2^FS\n");
                sb.Append("^FO28,26^GB230,248,248^FS\n");
                if (EsPiso(e))
                {
                    // 2026-11-27: lugar en el piso de una calle: "P1" grande (lugar 1) en vez de la altura
                    sb.Append($"^FO28,40^A0N,220,{((e.NroFila ?? 1) < 10 ? 110 : 80)}^FR^FB230,1,0,C,0^FDP{e.NroFila ?? 1}^FS\n");
                    sb.Append($"^FO282,36^A0N,34,30^FB262,1,0,L,0^FDPISO · LUGAR {e.NroFila ?? 1}^FS\n");
                    sb.Append($"^FO282,76^A0N,24,22^FB262,1,0,L,0^FD{(e.Calle1 is int cl ? $"en la calle {cl}" : "en el piso")}^FS\n");
                }
                else
                {
                    sb.Append($"^FO28,40^A0N,220,{(niv < 10 ? 130 : 88)}^FR^FB230,1,0,C,0^FDA{niv}^FS\n");
                    sb.Append($"^FO282,36^A0N,34,30^FB262,1,0,L,0^FDALTURA {niv}^FS\n");
                    sb.Append($"^FO282,76^A0N,24,22^FB262,1,0,L,0^FD{(EsEstanteria(e) ? "contando desde abajo" : "contando desde el piso")}^FS\n");
                }
                sb.Append($"^FO282,130^A0N,46,{(cod.Length <= 11 ? 32 : 24)}^FB262,1,0,L,0^FD{cod}^FS\n");
                sb.Append("^FO282,190^A0N,24,22^FB262,3,0,L,0^FDPegalo en el estante donde va la mercadería^FS\n");
                sb.Append($"^FO552,14^BQN,2,7^FDLA,{url}^FS\n");
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
                sb.Append(Acostado(20, 505, 42, 38, EsPiso(e) ? "Mercadería apoyada en el piso de la calle"
                    : EsEstanteria(e) ? $"Altura {niv} contando desde abajo"
                    : niv == 1 ? "Altura 1 = el piso" : $"Altura {niv} contando desde el piso"));
                sb.Append(Acostado(20, 600, 40, 36, "Escaneá con el celu para ver o guardar productos"));
                // QR (aumento 11 ≈ 360 puntos con la dirección larga de C-F-R-A; se lee en cualquier sentido)
                sb.Append($"^FO226,{T0 + 920}^BQN,2,11^FDLA,{url}^FS\n");
                sb.Append("^XZ\n");
            }
        if (sb.Length == 0) return ErrorZpl(404, "Ese estante no está en este rack.");
        return Content(sb.ToString(), "text/plain; charset=utf-8");
    }
}
