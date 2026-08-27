using System.Globalization;
using Api.Data;
using Api.Models;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-08-27 — EXCEL EDITABLE de publicaciones (pedido viejo de Osmar).
///
/// Por qué existe: la pantalla sirve para trabajar de a pocas, pero hay 5.925 publicaciones.
/// Cuando investigamos cómo lo hacen los demás (MeLi, Real Trends, Integraly) los tres coincidían:
/// **de a una en pantalla, lo masivo por Excel**. Esto es la parte masiva.
///
/// El recorrido es SIEMPRE el mismo, y el del medio no se saltea nunca:
///   1. BAJAR   — sale lo que quedó filtrado en pantalla, con 4 columnas amarillas para escribir.
///   2. SUBIR   — se lee el archivo y se arma una VISTA PREVIA: qué cambiaría, fila por fila.
///   3. APLICAR — recién ahí se toca algo, y solo lo que el usuario dejó tildado.
///
/// Tres decisiones que importan:
///
/// • **Las columnas amarillas vienen PRECARGADAS con lo que hay hoy.** Así el que edita cambia
///   solo lo que quiere cambiar y no tiene que copiar nada. Lo que no tocó, no se toca.
///
/// • **La comparación NO es contra la base, es contra la hoja escondida `_original`** que viaja
///   dentro del archivo. Si entre que bajó el Excel y lo subió el motor nocturno movió un precio,
///   comparar contra la base haría que el archivo "deshaga" ese cambio sin querer. Comparando
///   contra el original, una fila que no se editó queda quieta aunque la base se haya movido —
///   y si se movió, la vista previa lo avisa.
///
/// • **El escalón de los $33.000 se avisa acá también.** Arriba de ese precio MeLi empuja el envío
///   gratis a cargo del vendedor: la silla Reina cruzó el escalón y pasó de dejar 55% a dejar 6,3%.
///   Un Excel con 300 filas es justo donde eso se puede colar sin que nadie lo mire.
/// </summary>
public class MeliPublicacionesExcelService
{
    private readonly AppDbContext _db;
    private readonly MeliPublicacionesV2Service _lista;
    private readonly MeliPrecioManualService _precioManual;
    private readonly ILogger<MeliPublicacionesExcelService> _logger;

    private const decimal IVA = 1.21m;
    private const decimal TOPE_SEGURO = 2_000_000m;

    /// <summary>El precio donde MeLi cambia de régimen (deja el cargo fijo y empuja el envío gratis).
    /// Medido el 26/08 sobre 3.790 activas: no hay ninguna por debajo de $33.000 sin cargo fijo.</summary>
    private const decimal ESCALON_ENVIO = 33_000m;

    /// <summary>Tope de filas que salen en el archivo. Con 5.925 publicaciones entra el catálogo entero.</summary>
    private const int MAX_EXPORT = 7000;

    /// <summary>Tope de precios por tanda al aplicar. Cada precio es una llamada a MeLi con freno de
    /// 250 ms: 300 son ~75 segundos. Más que eso conviene partirlo, si no la pantalla queda colgada.</summary>
    private const int MAX_PRECIOS_POR_TANDA = 300;

    /// <summary>Tope de filas del archivo que se leen. Arriba de esto es un archivo equivocado.</summary>
    private const int MAX_FILAS_LEIDAS = 10000;

    // Diferencias por debajo de esto no cuentan como edición: son el redondeo de Excel.
    private const decimal TOLERANCIA_PRECIO = 0.5m;
    private const decimal TOLERANCIA_PCT = 0.05m;

    private const string HOJA_DATOS = "Publicaciones";
    private const string HOJA_AYUDA = "Cómo se usa";
    private const string HOJA_ORIGINAL = "_original";

    // Posición de las columnas que se escriben (la hoja se arma abajo, en Exportar).
    private const int COL_MLA = 1;
    private const int COL_PRECIO_NUEVO = 15;
    private const int COL_OBJETIVO_NUEVO = 16;
    private const int COL_SINC_PRECIO = 17;
    private const int COL_SINC_STOCK = 18;
    private const int TOTAL_COLS = 18;

    public MeliPublicacionesExcelService(AppDbContext db, MeliPublicacionesV2Service lista,
        MeliPrecioManualService precioManual,
        ILogger<MeliPublicacionesExcelService> logger)
    {
        _db = db;
        _lista = lista;
        _precioManual = precioManual;
        _logger = logger;
    }

    // ─────────────────────────── 1) BAJAR ───────────────────────────

    /// <summary>Arma el .xlsx con las publicaciones que matchean el filtro de la pantalla.</summary>
    public async Task<(byte[] Bytes, int Filas)> ExportarAsync(MeliPublicacionesV2Service.Filtros f,
        CancellationToken ct = default)
    {
        // La lista se pide por páginas de 500 (el tope del servicio) hasta juntar todo lo filtrado.
        // Se reusa el MISMO armador que la pantalla: los números del Excel y los de la pantalla
        // salen del mismo lugar, así no hay dos verdades.
        var filas = new List<MeliPublicacionesV2Service.FilaDto>();
        for (var pagina = 1; filas.Count < MAX_EXPORT; pagina++)
        {
            var page = await _lista.GetAsync(f with { Pagina = pagina, PorPagina = 500 }, ct);
            if (page.Items.Count == 0) break;
            filas.AddRange(page.Items);
            if (filas.Count >= page.Total) break;
            if (ct.IsCancellationRequested) break;
        }

        using var wb = new XLWorkbook();
        EscribirAyuda(wb);
        var ws = wb.Worksheets.Add(HOJA_DATOS);

        var headers = new[]
        {
            "N° publicación", "SKU", "Título", "Estado", "Tipo", "Cuotas", "Cómo se envía",
            "Stock", "Vendidas", "Costo", "Se lleva MeLi", "Precio hoy", "Te queda %", "Te queda $",
            "✏️ Precio nuevo", "✏️ Objetivo %", "✏️ Sincro precio", "✏️ Sincro stock"
        };

        for (int i = 0; i < headers.Length; i++)
        {
            var col = i + 1;
            var cell = ws.Cell(1, col);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            // Las editables van en ámbar para que se vea de una dónde se escribe. El resto es informativo.
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml(col >= COL_PRECIO_NUEVO ? "#b45309" : "#1f2937");
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Alignment.WrapText = true;
        }

        ws.Cell(1, COL_MLA).GetComment().AddText(
            "No cambies esta columna: es la que identifica la publicación. Si la borrás, la fila se ignora.");
        ws.Cell(1, COL_PRECIO_NUEVO).GetComment().AddText(
            "Escribí el precio que querés que valga. Si lo dejás como está, no se toca. " +
            "OJO: arriba de $33.000 MercadoLibre suele obligar al envío gratis y lo pagás vos.");
        ws.Cell(1, COL_OBJETIVO_NUEVO).GetComment().AddText(
            "El porcentaje de ganancia que querés sobre el costo. El sistema calcula el precio solo. " +
            "Para que lo mantenga, la columna de al lado tiene que decir SI.");
        ws.Cell(1, COL_SINC_PRECIO).GetComment().AddText(
            "SI = el sistema mantiene el precio según el objetivo. NO = el precio queda como vos lo pusiste.");
        ws.Cell(1, COL_SINC_STOCK).GetComment().AddText(
            "SI = el sistema le manda a MercadoLibre el stock que hay en el depósito.");

        int r = 2;
        foreach (var it in filas)
        {
            ws.Cell(r, 1).Value = it.MeliItemId;
            ws.Cell(r, 2).Value = it.Sku ?? "";
            ws.Cell(r, 3).Value = it.Titulo;
            ws.Cell(r, 4).Value = EstadoLindo(it.Estado);
            ws.Cell(r, 5).Value = TipoLindo(it.Tipo);
            ws.Cell(r, 6).Value = string.IsNullOrEmpty(it.Cuotas) ? "sin cuotas" : it.Cuotas;
            ws.Cell(r, 7).Value = EnvioLindo(it.Envio, it.EnvioGratis);
            ws.Cell(r, 8).Value = it.StockMeli;
            ws.Cell(r, 9).Value = it.Vendidas;

            if (it.Costo is > 0) ws.Cell(r, 10).Value = it.Costo.Value; else ws.Cell(r, 10).Value = "sin costo";
            if (it.ComisionMonto.HasValue) ws.Cell(r, 11).Value = it.ComisionMonto.Value; else ws.Cell(r, 11).Value = "falta el dato";
            ws.Cell(r, 12).Value = it.Precio;
            if (it.MargenPct.HasValue) ws.Cell(r, 13).Value = it.MargenPct.Value; else ws.Cell(r, 13).Value = "—";
            if (it.GananciaPesos.HasValue) ws.Cell(r, 14).Value = it.GananciaPesos.Value; else ws.Cell(r, 14).Value = "—";

            // ── Las 4 amarillas, precargadas con lo de hoy ──
            ws.Cell(r, COL_PRECIO_NUEVO).Value = it.Precio;
            if (it.ObjetivoPct.HasValue) ws.Cell(r, COL_OBJETIVO_NUEVO).Value = it.ObjetivoPct.Value;
            ws.Cell(r, COL_SINC_PRECIO).Value = it.SyncPrecio ? "SI" : "NO";
            ws.Cell(r, COL_SINC_STOCK).Value = it.SyncStock ? "SI" : "NO";

            foreach (var c in new[] { 8, 9 }) ws.Cell(r, c).Style.NumberFormat.Format = "#,##0";
            foreach (var c in new[] { 10, 11, 12, 14, COL_PRECIO_NUEVO }) ws.Cell(r, c).Style.NumberFormat.Format = "$ #,##0.00";
            foreach (var c in new[] { 13, COL_OBJETIVO_NUEVO }) ws.Cell(r, c).Style.NumberFormat.Format = "0.0";

            // Cebra en las informativas y fondo ámbar clarito en las editables, en TODAS las filas:
            // la idea es que el ojo encuentre la zona de escritura sin leer los títulos.
            if (r % 2 == 0)
                for (int c = 1; c < COL_PRECIO_NUEVO; c++) ws.Cell(r, c).Style.Fill.BackgroundColor = XLColor.FromHtml("#f9fafb");
            for (int c = COL_PRECIO_NUEVO; c <= TOTAL_COLS; c++)
            {
                ws.Cell(r, c).Style.Fill.BackgroundColor = XLColor.FromHtml("#fffbeb");
                ws.Cell(r, c).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                ws.Cell(r, c).Style.Border.OutsideBorderColor = XLColor.FromHtml("#fcd34d");
            }

            // Márgenes flojos en rojo: es la fila que hay que mirar primero.
            if (it.MargenPct is < 0) ws.Cell(r, 13).Style.Font.FontColor = XLColor.FromHtml("#b91c1c");
            else if (it.MargenPct is < 50) ws.Cell(r, 13).Style.Font.FontColor = XLColor.FromHtml("#b45309");

            if (it.ComisionVieja)
                ws.Cell(r, 11).GetComment().AddText("Este número es viejo: se capturó con otro precio. El margen que sale de acá no es confiable.");

            r++;
        }

        var ultima = Math.Max(r - 1, 2);

        // SI/NO por lista: evita que llegue "si", "s", "x" o un tilde y haya que adivinar qué quiso decir.
        foreach (var col in new[] { COL_SINC_PRECIO, COL_SINC_STOCK })
        {
            var dv = ws.Range(2, col, ultima, col).CreateDataValidation();
            dv.List("\"SI,NO\"", true);
            dv.ErrorTitle = "Poné SI o NO";
            dv.ErrorMessage = "Esta columna solo acepta SI o NO.";
        }

        ws.SheetView.FreezeRows(1);
        ws.Range(1, 1, ultima, TOTAL_COLS).SetAutoFilter();
        ws.Columns(1, TOTAL_COLS).AdjustToContents();
        ws.Column(3).Width = Math.Min(ws.Column(3).Width, 55);   // el título se come la pantalla
        foreach (var col in new[] { COL_PRECIO_NUEVO, COL_OBJETIVO_NUEVO, COL_SINC_PRECIO, COL_SINC_STOCK })
            ws.Column(col).Width = Math.Max(ws.Column(col).Width, 13);
        ws.Row(1).Height = 30;

        // ── Hoja escondida con lo que había al momento de bajar ──
        // Es lo que permite saber QUÉ editó el usuario, en vez de adivinar comparando contra la base.
        var wo = wb.Worksheets.Add(HOJA_ORIGINAL);
        wo.Cell(1, 1).Value = "MLA";
        wo.Cell(1, 2).Value = "Precio";
        wo.Cell(1, 3).Value = "Objetivo";
        wo.Cell(1, 4).Value = "SincPrecio";
        wo.Cell(1, 5).Value = "SincStock";
        wo.Cell(1, 6).Value = "BajadoEl";
        wo.Cell(2, 6).Value = ArNow().ToString("yyyy-MM-dd HH:mm");
        int ro = 2;
        foreach (var it in filas)
        {
            wo.Cell(ro, 1).Value = it.MeliItemId;
            wo.Cell(ro, 2).Value = it.Precio;
            wo.Cell(ro, 3).Value = it.ObjetivoPct ?? 0m;
            wo.Cell(ro, 4).Value = it.SyncPrecio ? 1 : 0;
            wo.Cell(ro, 5).Value = it.SyncStock ? 1 : 0;
            ro++;
        }
        wo.Hide();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        _logger.LogInformation("[PubExcel] Exportadas {N} publicaciones", filas.Count);
        return (ms.ToArray(), filas.Count);
    }

    private static void EscribirAyuda(XLWorkbook wb)
    {
        var ws = wb.Worksheets.Add(HOJA_AYUDA);
        var lineas = new (string Texto, bool Titulo)[]
        {
            ("Cómo se usa este Excel", true),
            ("", false),
            ("1) Editá SOLO las cuatro columnas de la derecha, las que tienen fondo amarillo y un lapicito en el título.", false),
            ("2) Guardá el archivo y volvé a la pantalla de Publicaciones.", false),
            ("3) Tocá “⬆ Subir Excel”. NO se cambia nada todavía: primero te muestra una lista de qué cambiaría.", false),
            ("4) Revisá esa lista, destildá lo que no quieras, y recién ahí tocá “Aplicar”.", false),
            ("", false),
            ("Qué hace cada columna amarilla", true),
            ("Precio nuevo — el precio que querés que tenga la publicación en MercadoLibre.", false),
            ("Objetivo % — cuánto querés ganar sobre el costo. El sistema calcula el precio solo.", false),
            ("Sincro precio — SI: el sistema mantiene el precio según el objetivo. NO: el precio queda fijo como vos lo pusiste.", false),
            ("Sincro stock — SI: el sistema le manda a MercadoLibre el stock del depósito.", false),
            ("", false),
            ("Cosas que conviene saber", true),
            ("• Lo que no tocás, no se toca. Las columnas vienen con lo que hay hoy justamente para eso.", false),
            ("• Podés borrar filas que no te interesen: solo se mira lo que quedó en el archivo.", false),
            ("• NO cambies la columna “N° publicación”: es la que identifica cada fila.", false),
            ("• Si ponés precio nuevo Y objetivo en la misma fila, se guardan los dos, pero manda el precio que pusiste.", false),
            ("• OJO con los $33.000: arriba de ese precio MercadoLibre suele obligar al envío gratis y lo pagás vos.", false),
            ("   Pasó de verdad: una silla que a $32.999 dejaba 55% terminó dejando 6,3% al pasarse. La vista previa te avisa.", false),
            ("• Las columnas grises son informativas: se recalculan solas la próxima vez que bajes el archivo.", false),
        };

        for (int i = 0; i < lineas.Length; i++)
        {
            var c = ws.Cell(i + 1, 1);
            c.Value = lineas[i].Texto;
            if (lineas[i].Titulo)
            {
                c.Style.Font.Bold = true;
                c.Style.Font.FontSize = 13;
                c.Style.Font.FontColor = XLColor.FromHtml("#1d4f80");
            }
        }
        ws.Column(1).Width = 120;
    }

    // ─────────────────────────── 2) SUBIR (vista previa) ───────────────────────────

    public record CambioDto(
        string Mla, string? Sku, string Titulo,
        decimal? PrecioNuevo, decimal? PrecioHoy,
        decimal? ObjetivoNuevo, decimal? ObjetivoHoy,
        bool? SincPrecioNuevo, bool SincPrecioHoy,
        bool? SincStockNuevo, bool SincStockHoy,
        string Resumen, List<string> Avisos, bool Bloqueada, decimal? MargenEstimadoPct);

    public record PreviewDto(
        int FilasLeidas, int SinCambios, int ConCambios, int Bloqueadas,
        int CambianPrecio, int CambianObjetivo, int CambianSincro,
        List<CambioDto> Cambios, List<string> Problemas, string? BajadoEl);

    /// <summary>Lee el archivo y arma la vista previa. NO cambia nada.</summary>
    public async Task<PreviewDto> LeerAsync(Stream archivo, CancellationToken ct = default)
    {
        var problemas = new List<string>();
        XLWorkbook wb;
        try { wb = new XLWorkbook(archivo); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PubExcel] Archivo ilegible");
            return new PreviewDto(0, 0, 0, 0, 0, 0, 0, new(), new()
            { "No se pudo abrir el archivo. Tiene que ser el .xlsx que bajaste de esta pantalla." }, null);
        }

        using (wb)
        {
            var ws = wb.Worksheets.FirstOrDefault(w => w.Name == HOJA_DATOS) ?? wb.Worksheets.FirstOrDefault();
            if (ws is null)
                return new PreviewDto(0, 0, 0, 0, 0, 0, 0, new(), new() { "El archivo no tiene ninguna hoja." }, null);

            // Lo que había cuando se bajó el archivo. Sin esto no se puede saber qué editó el usuario
            // (ver el comentario de arriba): se compara contra la base y se avisa.
            var original = new Dictionary<string, (decimal Precio, decimal Objetivo, bool SincP, bool SincS)>();
            string? bajadoEl = null;
            var wo = wb.Worksheets.FirstOrDefault(w => w.Name == HOJA_ORIGINAL);
            if (wo is not null)
            {
                bajadoEl = wo.Cell(2, 6).GetString();
                if (string.IsNullOrWhiteSpace(bajadoEl)) bajadoEl = null;
                foreach (var row in wo.RowsUsed().Skip(1))
                {
                    var mla = row.Cell(1).GetString().Trim();
                    if (string.IsNullOrWhiteSpace(mla)) continue;
                    original[mla] = (LeerDecimal(row.Cell(2)) ?? 0m, LeerDecimal(row.Cell(3)) ?? 0m,
                                     (LeerDecimal(row.Cell(4)) ?? 0m) == 1m, (LeerDecimal(row.Cell(5)) ?? 0m) == 1m);
                }
            }
            else
            {
                problemas.Add("El archivo no trae la hoja de control interna (pasa si lo rehiciste a mano). " +
                              "Se va a comparar contra lo que hay hoy en el sistema.");
            }

            // ── Leer las filas ──
            var leidas = new List<(string Mla, decimal? Precio, decimal? Objetivo, bool? SincP, bool? SincS)>();
            var filasArchivo = 0;
            foreach (var row in ws.RowsUsed().Skip(1))
            {
                if (++filasArchivo > MAX_FILAS_LEIDAS)
                {
                    problemas.Add($"El archivo tiene más de {MAX_FILAS_LEIDAS:N0} filas: se leyeron las primeras.");
                    break;
                }
                var mla = row.Cell(COL_MLA).GetString().Trim().TrimStart('#').Trim();
                if (string.IsNullOrWhiteSpace(mla)) continue;
                if (!mla.StartsWith("MLA", StringComparison.OrdinalIgnoreCase))
                {
                    problemas.Add($"Fila {row.RowNumber()}: “{mla}” no parece un número de publicación. Se saltea.");
                    continue;
                }

                var sp = LeerSiNo(row.Cell(COL_SINC_PRECIO));
                var ss = LeerSiNo(row.Cell(COL_SINC_STOCK));
                if (sp is null && !string.IsNullOrWhiteSpace(row.Cell(COL_SINC_PRECIO).GetString()))
                    problemas.Add($"Fila {row.RowNumber()}: en “Sincro precio” dice “{row.Cell(COL_SINC_PRECIO).GetString()}”. Solo se entiende SI o NO — se deja como está.");
                if (ss is null && !string.IsNullOrWhiteSpace(row.Cell(COL_SINC_STOCK).GetString()))
                    problemas.Add($"Fila {row.RowNumber()}: en “Sincro stock” dice “{row.Cell(COL_SINC_STOCK).GetString()}”. Solo se entiende SI o NO — se deja como está.");

                leidas.Add((mla.ToUpperInvariant(), LeerDecimal(row.Cell(COL_PRECIO_NUEVO)),
                            LeerDecimal(row.Cell(COL_OBJETIVO_NUEVO)), sp, ss));
            }

            if (leidas.Count == 0)
                return new PreviewDto(0, 0, 0, 0, 0, 0, 0, new(), problemas.Count > 0 ? problemas
                    : new() { "No se encontró ninguna publicación en el archivo." }, bajadoEl);

            // ── Traer de la base solo lo que hace falta para decidir ──
            var mlas = leidas.Select(x => x.Mla).Distinct().ToList();
            var items = await _db.MeliItems.AsNoTracking()
                .Where(m => m.VariationId == null && mlas.Contains(m.MeliItemId))
                .Select(m => new
                {
                    m.MeliItemId, m.Sku, m.Title, m.Status, m.Price, m.FreeShipping,
                    m.SaleFeeAmount, m.SaleFeeShippingCost
                })
                .ToDictionaryAsync(m => m.MeliItemId, ct);

            var cfgs = await _db.MeliItemSyncConfigs.AsNoTracking()
                .Where(c => mlas.Contains(c.MeliItemId))
                .ToDictionaryAsync(c => c.MeliItemId, ct);

            // Costo por publicación, para poder decir "este precio te deja tanto".
            var costos = await (
                from c in _db.MeliItemComponentes.AsNoTracking()
                join p in _db.CafeProductos.AsNoTracking() on c.CafeProductoId equals p.Id
                where mlas.Contains(c.MeliItemId)
                group new { p.Costo, c.Cantidad } by c.MeliItemId into g
                select new { MeliItemId = g.Key, Costo = g.Sum(x => x.Costo * x.Cantidad) }
            ).ToDictionaryAsync(x => x.MeliItemId, x => x.Costo, ct);

            var cambios = new List<CambioDto>();
            int sinCambios = 0, bloqueadas = 0, nPrecio = 0, nObjetivo = 0, nSincro = 0;

            foreach (var f in leidas)
            {
                if (!items.TryGetValue(f.Mla, out var it))
                {
                    cambios.Add(new CambioDto(f.Mla, null, "(no está en el sistema)", null, null, null, null,
                        null, false, null, false, "No se encontró esta publicación",
                        new() { "Este número de publicación no existe en el sistema. Puede que se haya borrado o que esté mal escrito." },
                        true, null));
                    bloqueadas++;
                    continue;
                }

                cfgs.TryGetValue(f.Mla, out var cfg);
                var objHoy = cfg?.GananciaObjetivoPct;
                var spHoy = cfg?.SyncPrecio ?? false;
                var ssHoy = cfg?.SyncStock ?? false;

                // Base de comparación: lo que decía el archivo cuando se bajó.
                var tieneOrig = original.TryGetValue(f.Mla, out var org);
                var precioBase = tieneOrig ? org.Precio : it.Price;
                var objBase = tieneOrig ? (org.Objetivo == 0 ? (decimal?)null : org.Objetivo) : objHoy;
                var spBase = tieneOrig ? org.SincP : spHoy;
                var ssBase = tieneOrig ? org.SincS : ssHoy;

                var avisos = new List<string>();
                var partes = new List<string>();
                bool bloqueada = false;

                // ── Precio ──
                decimal? precioNuevo = null;
                if (f.Precio.HasValue && Math.Abs(f.Precio.Value - precioBase) > TOLERANCIA_PRECIO)
                {
                    if (f.Precio.Value <= 0)
                    {
                        avisos.Add("El precio tiene que ser mayor que cero.");
                        bloqueada = true;
                    }
                    else if (f.Precio.Value > TOPE_SEGURO)
                    {
                        avisos.Add($"${f.Precio.Value:N0} pasa el tope de seguridad de ${TOPE_SEGURO:N0}. Frenado por las dudas.");
                        bloqueada = true;
                    }
                    else
                    {
                        precioNuevo = Math.Round(f.Precio.Value, 2);
                        partes.Add($"precio ${precioBase:N0} → ${precioNuevo.Value:N0}");
                        nPrecio++;

                        // El escalón: lo más caro que puede pasar en un Excel de 300 filas.
                        if (!it.FreeShipping && it.Price < ESCALON_ENVIO && precioNuevo.Value >= ESCALON_ENVIO)
                            avisos.Add($"⚠ Pasa los ${ESCALON_ENVIO:N0}. Arriba de ese precio MercadoLibre suele obligar al " +
                                       "envío gratis y lo pagás vos: puede quedarte MENOS ganancia que ahora, aunque el precio suba.");
                    }
                }

                // ── Objetivo ──
                decimal? objetivoNuevo = null;
                if (f.Objetivo.HasValue && (objBase is null || Math.Abs(f.Objetivo.Value - objBase.Value) > TOLERANCIA_PCT))
                {
                    if (f.Objetivo.Value is <= 0 or > 500)
                    {
                        avisos.Add("El objetivo tiene que estar entre 1% y 500%.");
                        bloqueada = true;
                    }
                    else
                    {
                        objetivoNuevo = Math.Round(f.Objetivo.Value, 2);
                        partes.Add(objBase is null
                            ? $"objetivo → {objetivoNuevo.Value:0.#}%"
                            : $"objetivo {objBase.Value:0.#}% → {objetivoNuevo.Value:0.#}%");
                        nObjetivo++;
                    }
                }

                // ── Sincros ──
                bool? spNuevo = f.SincP.HasValue && f.SincP.Value != spBase ? f.SincP : null;
                bool? ssNuevo = f.SincS.HasValue && f.SincS.Value != ssBase ? f.SincS : null;
                if (spNuevo.HasValue) { partes.Add(spNuevo.Value ? "prende sincro de precio" : "apaga sincro de precio"); nSincro++; }
                if (ssNuevo.HasValue) { partes.Add(ssNuevo.Value ? "prende sincro de stock" : "apaga sincro de stock"); nSincro++; }

                if (partes.Count == 0) { sinCambios++; continue; }

                // ── Avisos que no bloquean pero hay que ver ──
                if (it.Status is "closed" or "deleted")
                {
                    avisos.Add($"La publicación está {EstadoLindo(it.Status).ToLowerInvariant()}: no se le puede cambiar nada.");
                    bloqueada = true;
                }

                if (tieneOrig && Math.Abs(it.Price - org.Precio) > TOLERANCIA_PRECIO)
                    avisos.Add($"Ojo: desde que bajaste el Excel el precio cambió solo (${org.Precio:N0} → ${it.Price:N0}). " +
                               "Si aplicás, vuelve a lo que dice el archivo.");

                // Qué dejaría el precio nuevo. Es una ESTIMACIÓN: usa la comisión que hay guardada,
                // que escala con el precio pero no es exacta. El número fino lo da MeLi al aplicar.
                decimal? margenEst = null;
                if (precioNuevo.HasValue && costos.TryGetValue(f.Mla, out var costo) && costo > 0
                    && it.SaleFeeAmount is > 0 && it.Price > 0)
                {
                    var comPct = it.SaleFeeAmount!.Value / it.Price;
                    var envio = it.FreeShipping ? (it.SaleFeeShippingCost ?? 0m) : 0m;
                    var neto = (precioNuevo.Value - precioNuevo.Value * comPct - envio) / IVA;
                    margenEst = Math.Round((neto - costo) / costo * 100m, 1);
                    if (margenEst < 0)
                        avisos.Add($"A ese precio quedaría a PÉRDIDA (aprox. {margenEst:0.#}% sobre el costo).");
                    else if (margenEst < 50)
                        avisos.Add($"A ese precio te quedaría aprox. {margenEst:0.#}% sobre el costo — abajo del 50%.");
                }
                else if (precioNuevo.HasValue && (!costos.TryGetValue(f.Mla, out var c2) || c2 <= 0))
                    avisos.Add("Sin costo cargado no se puede saber qué te deja este precio.");

                if (objetivoNuevo.HasValue && !(spNuevo ?? spBase))
                    avisos.Add("El objetivo se guarda, pero el sincro de precio está apagado: nadie lo va a mantener. " +
                               "Poné SI en “Sincro precio” si querés que el sistema lo aplique.");

                if (bloqueada) bloqueadas++;

                cambios.Add(new CambioDto(f.Mla, it.Sku, it.Title,
                    precioNuevo, precioBase, objetivoNuevo, objBase,
                    spNuevo, spBase, ssNuevo, ssBase,
                    string.Join(" · ", partes), avisos, bloqueada, margenEst));
            }

            // Primero lo que tiene avisos: es lo que hay que mirar antes de aplicar.
            cambios = cambios
                .OrderByDescending(c => c.Bloqueada)
                .ThenByDescending(c => c.Avisos.Count)
                .ThenBy(c => c.Titulo)
                .ToList();

            if (nPrecio > MAX_PRECIOS_POR_TANDA)
                problemas.Add($"Hay {nPrecio:N0} cambios de precio y por tanda se aplican {MAX_PRECIOS_POR_TANDA}. " +
                              "Aplicá, y después volvé a subir el mismo archivo para seguir con el resto.");

            _logger.LogInformation("[PubExcel] Vista previa: {Leidas} filas, {Cambios} con cambios, {Bloq} bloqueadas",
                leidas.Count, cambios.Count, bloqueadas);

            return new PreviewDto(leidas.Count, sinCambios, cambios.Count(c => !c.Bloqueada), bloqueadas,
                nPrecio, nObjetivo, nSincro, cambios, problemas, bajadoEl);
        }
    }

    // ─────────────────────────── 3) APLICAR ───────────────────────────

    public record AplicarItem(string Mla, decimal? PrecioNuevo, decimal? ObjetivoNuevo,
        bool? SincPrecioNuevo, bool? SincStockNuevo);
    public record AplicarRequest(List<AplicarItem> Items);
    public record FilaResultado(string Mla, bool Ok, string Mensaje);
    public record AplicarResultado(int Pedidos, int Ok, int Errores, int Salteados, List<FilaResultado> Detalle);

    /// <summary>TOCA MELI en las filas que traen precio. Las demás solo guardan configuración.</summary>
    public async Task<AplicarResultado> AplicarAsync(List<AplicarItem> items, CancellationToken ct = default)
    {
        var detalle = new List<FilaResultado>();
        int ok = 0, err = 0, salteados = 0, preciosHechos = 0;

        var pedidos = items.Where(i => !string.IsNullOrWhiteSpace(i.Mla))
            .GroupBy(i => i.Mla.Trim().ToUpperInvariant()).Select(g => g.First()).ToList();

        // Primero las que solo tocan configuración: son instantáneas y no dependen de MeLi.
        // Así, si más adelante algo de MeLi falla o se corta, la parte segura ya quedó guardada.
        foreach (var i in pedidos.Where(x => x.PrecioNuevo is null))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var (hizo, msg) = await GuardarConfigAsync(i, ct);
                if (hizo) { ok++; detalle.Add(new(i.Mla, true, msg)); }
                else { salteados++; detalle.Add(new(i.Mla, true, "sin cambios")); }
            }
            catch (Exception ex) { err++; detalle.Add(new(i.Mla, false, ex.Message)); }
        }
        await _db.SaveChangesAsync(ct);

        // Y ahora las que cambian el precio: una por una contra MeLi, con freno.
        foreach (var i in pedidos.Where(x => x.PrecioNuevo is not null))
        {
            if (ct.IsCancellationRequested) break;
            if (preciosHechos >= MAX_PRECIOS_POR_TANDA)
            {
                salteados++;
                detalle.Add(new(i.Mla, true, $"salteada: se aplican {MAX_PRECIOS_POR_TANDA} precios por tanda"));
                continue;
            }

            try
            {
                // El objetivo y el sincro de stock se guardan igual, aunque después mande el precio.
                await GuardarConfigAsync(i with { SincPrecioNuevo = null }, ct);
                await _db.SaveChangesAsync(ct);

                // Quién manda de acá en adelante: si el Excel dice "Sincro precio = NO", el precio
                // queda fijo. Si no dijo nada, se respeta lo que ya estaba configurado.
                var cfg = await _db.MeliItemSyncConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.MeliItemId == i.Mla, ct);
                var sincroFinal = i.SincPrecioNuevo ?? cfg?.SyncPrecio ?? false;
                var quedaFijo = !sincroFinal;

                var r = await _precioManual.PublicarAsync(i.Mla, i.PrecioNuevo!.Value, quedaFijo, ct);
                preciosHechos++;
                if (r.Ok)
                {
                    // PublicarAsync solo apaga el sincro cuando queda fijo; si el Excel pidió
                    // prenderlo, hay que hacerlo acá.
                    if (i.SincPrecioNuevo == true) await PrenderSincroPrecioAsync(i.Mla, ct);
                    ok++;
                    detalle.Add(new(i.Mla, true, r.Mensaje));
                }
                else { err++; detalle.Add(new(i.Mla, false, r.Mensaje)); }
            }
            catch (Exception ex) { err++; detalle.Add(new(i.Mla, false, ex.Message)); }

            try { await Task.Delay(250, ct); } catch (OperationCanceledException) { }
        }

        _logger.LogWarning("[PubExcel] Aplicado: {Ok} ok, {Err} error, {Skip} salteados (de {N})",
            ok, err, salteados, pedidos.Count);
        return new AplicarResultado(pedidos.Count, ok, err, salteados, detalle);
    }

    /// <summary>Guarda objetivo y sincros. Devuelve si hubo algo que guardar.</summary>
    private async Task<(bool Hizo, string Mensaje)> GuardarConfigAsync(AplicarItem i, CancellationToken ct)
    {
        if (i.ObjetivoNuevo is null && i.SincPrecioNuevo is null && i.SincStockNuevo is null)
            return (false, "sin cambios");

        if (i.ObjetivoNuevo is not null && i.ObjetivoNuevo is <= 0 or > 500)
            throw new InvalidOperationException("El objetivo tiene que estar entre 1% y 500%");

        var cfg = await _db.MeliItemSyncConfigs.FirstOrDefaultAsync(c => c.MeliItemId == i.Mla, ct);
        if (cfg is null)
        {
            cfg = new MeliItemSyncConfig { MeliItemId = i.Mla, CreatedAt = DateTime.UtcNow };
            _db.MeliItemSyncConfigs.Add(cfg);
        }

        var partes = new List<string>();
        if (i.ObjetivoNuevo is not null)
        {
            cfg.GananciaObjetivoPct = i.ObjetivoNuevo;
            cfg.GananciaObjetivoAt = DateTime.UtcNow;
            partes.Add($"objetivo {i.ObjetivoNuevo.Value:0.#}%");
        }
        if (i.SincPrecioNuevo is not null)
        {
            cfg.SyncPrecio = i.SincPrecioNuevo.Value;
            partes.Add(i.SincPrecioNuevo.Value ? "sincro de precio prendido" : "sincro de precio apagado");
        }
        if (i.SincStockNuevo is not null)
        {
            cfg.SyncStock = i.SincStockNuevo.Value;
            partes.Add(i.SincStockNuevo.Value ? "sincro de stock prendido" : "sincro de stock apagado");
        }
        cfg.UpdatedAt = DateTime.UtcNow;
        return (true, string.Join(" · ", partes));
    }

    private async Task PrenderSincroPrecioAsync(string mla, CancellationToken ct)
    {
        var cfg = await _db.MeliItemSyncConfigs.FirstOrDefaultAsync(c => c.MeliItemId == mla, ct);
        if (cfg is null)
        {
            cfg = new MeliItemSyncConfig { MeliItemId = mla, CreatedAt = DateTime.UtcNow };
            _db.MeliItemSyncConfigs.Add(cfg);
        }
        cfg.SyncPrecio = true;
        cfg.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    // ─────────────────────────── auxiliares ───────────────────────────

    /// <summary>Lee un número de una celda sin importar si Excel lo guardó como número o como texto.
    /// El texto puede venir "$ 32.999,00" (es-AR) o "32999.00" (invariante) según quién lo escribió.</summary>
    private static decimal? LeerDecimal(IXLCell cell)
    {
        if (cell.IsEmpty()) return null;
        if (cell.DataType == XLDataType.Number) return (decimal)cell.GetDouble();

        var s = cell.GetString().Trim();
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Replace("$", "").Replace("%", "").Replace(" ", "").Replace(" ", "");
        if (s.Length == 0) return null;

        // Con coma decimal (es-AR): el punto es separador de miles.
        if (s.Contains(','))
        {
            var limpio = s.Replace(".", "").Replace(',', '.');
            return decimal.TryParse(limpio, NumberStyles.Any, CultureInfo.InvariantCulture, out var v1) ? v1 : null;
        }
        // Sin coma: un punto puede ser decimal (32999.50) o de miles (32.999). Se decide por los
        // dígitos que hay después del último punto: 3 exactos y más de un grupo = miles.
        var partes = s.Split('.');
        if (partes.Length > 2 || (partes.Length == 2 && partes[1].Length == 3 && partes[0].Length <= 3
                                  && !s.StartsWith("0.")))
            s = s.Replace(".", "");
        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static bool? LeerSiNo(IXLCell cell)
    {
        var s = cell.GetString().Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Replace("Í", "I");
        return s switch
        {
            "SI" or "S" or "SÍ" or "X" or "1" or "TRUE" or "VERDADERO" => true,
            "NO" or "N" or "0" or "FALSE" or "FALSO" => false,
            _ => null
        };
    }

    private static string EstadoLindo(string? estado) => estado switch
    {
        "active" => "Activa",
        "paused" => "Pausada",
        "closed" => "Cerrada",
        "under_review" => "En revisión",
        "deleted" => "Borrada",
        null or "" => "—",
        _ => estado
    };

    private static string TipoLindo(string? tipo) => tipo switch
    {
        "gold_pro" => "Premium",
        "gold_special" => "Clásica",
        "free" => "Gratuita",
        null or "" => "—",
        _ => tipo
    };

    private static string EnvioLindo(string? logistic, bool gratis)
    {
        var baseTxt = logistic switch
        {
            "fulfillment" => "Full",
            "cross_docking" => "Colecta",
            "self_service" => "Flex",
            "drop_off" => "Correo",
            "xd_drop_off" => "Correo",
            "me1" or "custom" => "A acordar",
            null or "" => "sin dato",
            _ => logistic
        };
        return gratis ? baseTxt + " (lo pagás vos)" : baseTxt;
    }

    /// <summary>La hora de Argentina. Regla del proyecto: nunca DateTime.Now pelado.</summary>
    private static DateTime ArNow()
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        }
        catch { return DateTime.UtcNow.AddHours(-3); }
    }
}
