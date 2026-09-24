using System.Text;
using System.Text.RegularExpressions;
using Api.Data;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Api.Services;

/// <summary>
/// 2026-09-24: etiqueta PROPIA para los envíos ME1 (los despachamos nosotros: MercadoLibre no da
/// etiqueta). Diseño aprobado por el dueño, 10 x 20 cm como los rollos de las térmicas:
///   - troquel arriba (venta, cantidad, producto, qué trae el combo, SKU) para armar y recortar;
///   - nuestros datos de contacto (sin domicilio): nombre, WhatsApp, mail, webs y "¿Alguna duda o
///     reclamo? Contactanos" — salen de Cafe_Settings, los mismos de los comprobantes;
///   - ME1 + fecha de despacho (la del día que se imprime);
///   - destinatario grande (nombre, teléfono), dirección, entre calles, CP grande, localidad y referencia;
///   - código de barras con el número de envío (se escanea en Depósito como las de MeLi).
/// Sale en ZPL (Térmica directa: Zebra / HPRT) y en PDF (Térmica PDF, A4 ×1, A4 ×3).
/// </summary>
public class MeliEtiquetaMe1Service
{
    private readonly AppDbContext _db;
    private readonly MeliShipmentService _shipments;

    public MeliEtiquetaMe1Service(AppDbContext db, MeliShipmentService shipments)
    {
        _db = db; _shipments = shipments;
    }

    static MeliEtiquetaMe1Service() { QuestPDF.Settings.License = LicenseType.Community; }

    public record Producto(int Cantidad, string Titulo, string? Sku, List<string> Contiene);
    public record Datos(long Envio, long Venta, List<Producto> Productos, string Nombre, string? Telefono,
        string Calle, string? Entre, string? CiudadBarrio, string? Cp, string? Provincia, string? Referencia, DateTime FechaAr);
    public record Contacto(string Nombre, string? Whatsapp, string? Email, string? Web1, string? Web2);

    /// <summary>Datos de cada envío ME1. Los que no se pueden armar (sin dirección) vuelven en errores.</summary>
    public async Task<(List<Datos> Datos, List<string> Errores)> ArmarAsync(IEnumerable<long> envios)
    {
        var res = new List<Datos>();
        var errores = new List<string>();
        var hoyAr = DateTime.UtcNow.AddHours(-3);

        foreach (var envio in envios.Distinct())
        {
            var ordenes = await _db.MeliOrders.AsNoTracking().Where(o => o.ShippingId == envio).ToListAsync();
            if (ordenes.Count == 0) { errores.Add($"Envío {envio}: no está en el sistema."); continue; }

            var sh = await _db.MeliShipments.AsNoTracking().FirstOrDefaultAsync(s => s.MeliShipmentId == envio);
            if (sh is null || string.IsNullOrWhiteSpace(sh.ReceiverName))
            {
                // Todavía no se bajó el envío: traerlo de MeLi (dirección, teléfono, referencia).
                try { await _shipments.SyncSingleShipmentAsync(envio); } catch { }
                sh = await _db.MeliShipments.AsNoTracking().FirstOrDefaultAsync(s => s.MeliShipmentId == envio);
            }
            if (sh is null) { errores.Add($"Envío {envio}: MercadoLibre no pasó la dirección del comprador."); continue; }

            var itemIds = ordenes.Select(o => o.ItemId).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
            var items = await _db.MeliItems.AsNoTracking().Where(i => itemIds.Contains(i.MeliItemId))
                .Select(i => new { i.MeliItemId, i.VariationId, i.Sku }).ToListAsync();
            var comps = await _db.MeliItemComponentes.AsNoTracking().Where(c => itemIds.Contains(c.MeliItemId))
                .Include(c => c.Producto).ToListAsync();

            var productos = ordenes.OrderBy(o => o.ItemTitle).Select(o =>
            {
                var sku = items.FirstOrDefault(i => i.MeliItemId == o.ItemId && i.VariationId == o.VariationId && !string.IsNullOrEmpty(i.Sku))?.Sku
                          ?? items.FirstOrDefault(i => i.MeliItemId == o.ItemId && !string.IsNullOrEmpty(i.Sku))?.Sku;
                var cs = comps.Where(c => c.MeliItemId == o.ItemId && (c.MeliVariationId == null || c.MeliVariationId == o.VariationId)).ToList();
                var esCombo = cs.Count > 1 || cs.Any(c => c.Cantidad > 1);
                var contiene = esCombo
                    ? cs.Select(c => $"×{Cant(c.Cantidad)} {c.Producto?.Nombre ?? "(producto)"}{(string.IsNullOrEmpty(c.Producto?.Sku) ? "" : $" ({c.Producto!.Sku})")}").ToList()
                    : new List<string>();
                return new Producto(o.Quantity, o.ItemTitle, sku, contiene);
            }).ToList();

            var (extra, referencia, entre) = PartirComentario(sh.Comment);
            var calle = !string.IsNullOrWhiteSpace(sh.StreetName)
                ? $"{sh.StreetName} {sh.StreetNumber}".Trim()
                : (sh.AddressLine ?? "");
            // Lo que viene antes de "Referencia:" (lote, piso, depto): corto va con la calle; largo, a la referencia.
            if (!string.IsNullOrWhiteSpace(extra))
            {
                if (extra!.Length <= 25) calle = $"{calle} · {extra}";
                else referencia = string.IsNullOrWhiteSpace(referencia) ? extra : $"{extra} · {referencia}";
            }
            var ciudad = string.Join(" · ", new[] { sh.City, sh.Neighborhood != sh.City ? sh.Neighborhood : null }
                .Where(x => !string.IsNullOrWhiteSpace(x)));

            var primera = ordenes.OrderBy(o => o.DateCreated).First();
            res.Add(new Datos(envio, primera.PackId ?? primera.MeliOrderId, productos,
                string.IsNullOrWhiteSpace(sh.ReceiverName) ? primera.BuyerNickname : sh.ReceiverName!,
                sh.ReceiverPhone, calle, entre, ciudad, sh.ZipCode, sh.State, referencia, hoyAr));
        }
        return (res, errores);
    }

    public async Task<Contacto> ContactoAsync()
    {
        var cfg = await _db.CafeSettings.AsNoTracking().FirstOrDefaultAsync(c => c.Id == 1);
        return new Contacto(
            cfg?.NegocioNombre ?? "INTERVENT",
            // El dueño pidió SOLO el de WhatsApp (15-2252-5458, cargado como teléfono 2).
            !string.IsNullOrWhiteSpace(cfg?.NegocioTelefono2) ? cfg!.NegocioTelefono2 : cfg?.NegocioTelefono,
            cfg?.NegocioEmail, cfg?.NegocioWeb, cfg?.NegocioWeb2);
    }

    /// <summary>MeLi manda "Lote 523A Referencia: portón negro Entre: Brown y Belgrano" en un solo texto.</summary>
    private static (string? Extra, string? Referencia, string? Entre) PartirComentario(string? c)
    {
        if (string.IsNullOrWhiteSpace(c)) return (null, null, null);
        // MeLi a veces manda la referencia en varios renglones: sin esto no se reconocía "Referencia:"
        // y todo iba pegado a la dirección (24/09, envío 48093236063).
        c = Regex.Replace(c, @"\s+", " ").Trim();
        string? entre = null;
        var mE = Regex.Match(c, @"\bEntre:\s*(.+)$", RegexOptions.IgnoreCase);
        if (mE.Success) { entre = mE.Groups[1].Value.Trim(); c = c[..mE.Index].Trim(); }
        string? extra = c, referencia = null;
        var mR = Regex.Match(c, @"\bReferencia:\s*(.*)$", RegexOptions.IgnoreCase);
        if (mR.Success) { referencia = mR.Groups[1].Value.Trim(); extra = c[..mR.Index].Trim(); }
        return (Nulo(extra), Nulo(referencia), Nulo(entre));
    }

    private static string? Nulo(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string Cant(decimal c) => c == Math.Floor(c) ? ((long)c).ToString() : c.ToString("0.##");
    private static string Unidades(int n) => n == 1 ? "Unidad" : "Unidades";

    // ─────────────────────────── ZPL (Zebra / HPRT, 203 dpi, 10 x 20 cm) ───────────────────────────

    public string Zpl(Datos d, Contacto k)
    {
        var z = new StringBuilder();
        z.Append("^XA^CI28^PW812^LL1624^LH0,0\n");

        // Troquel
        z.Append("^FO16,16^GB780,268,2^FS\n");
        T(z, 30, 28, 24, $"Venta: {d.Venta}", bold: true);
        T(z, 470, 26, 20, "Recortá esta parte antes de pegar el paquete", w: 310, lineas: 2);
        var totalU = d.Productos.Sum(p => p.Cantidad);
        T(z, 24, 80, 96, totalU.ToString(), w: 120, align: "C", bold: true);
        T(z, 24, 180, 22, Unidades(totalU), w: 120, align: "C");
        T(z, 160, 76, 24, TextoTroquel(d), w: 620, lineas: 7);
        z.Append("^FO16,292^A0N,22,22^FD- - - - - - - - - - - - - - -  recortar  - - - - - - - - - - - - - - -^FS\n");

        // Cuerpo
        z.Append("^FO16,322^GB780,1286,3^FS\n");
        T(z, 32, 334, 30, k.Nombre, bold: true);
        var y = 374;
        if (!string.IsNullOrWhiteSpace(k.Whatsapp))
        {
            z.Append($"^FO32,{y - 2}{IconoWhatsappZpl()}^FS\n");
            T(z, 72, y, 30, k.Whatsapp!, bold: true);
            y += 38;
        }
        var webs = string.Join(" · ", new[] { k.Web1, k.Web2 }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (!string.IsNullOrWhiteSpace(k.Email)) { T(z, 32, y, 24, k.Email!); y += 30; }
        if (!string.IsNullOrWhiteSpace(webs)) { T(z, 32, y, 24, webs); y += 30; }
        T(z, 32, y + 2, 26, "¿Alguna duda o reclamo? Contactanos.", bold: true);
        z.Append("^FO16,520^GB780,3,3^FS\n");

        T(z, 16, 540, 64, "ME1", w: 390, align: "C", bold: true);
        z.Append("^FO406,520^GB3,96,3^FS\n");
        T(z, 409, 532, 34, "DESPACHO", w: 387, align: "C");
        T(z, 409, 568, 40, d.FechaAr.ToString("dd/MM"), w: 387, align: "C", bold: true);
        z.Append("^FO16,616^GB780,3,3^FS\n");

        T(z, 32, 628, 22, "DESTINATARIO");
        T(z, 32, 656, 48, d.Nombre.ToUpperInvariant(), w: 750, lineas: 2, bold: true);
        if (!string.IsNullOrWhiteSpace(d.Telefono)) T(z, 32, 760, 34, "Tel: " + d.Telefono);
        z.Append("^FO16,806^GB780,3,3^FS\n");

        T(z, 32, 820, 38, d.Calle, w: 750, lineas: 2, bold: true);
        if (!string.IsNullOrWhiteSpace(d.Entre)) T(z, 32, 906, 28, "Entre: " + d.Entre, w: 750, lineas: 2);
        z.Append("^FO16,970^GB780,3,3^FS\n");

        T(z, 28, 1006, 26, "CP");
        T(z, 70, 982, 104, d.Cp ?? "-", w: 300, bold: true);
        z.Append("^FO380,970^GB3,130,3^FS\n");
        T(z, 396, 984, 38, (d.CiudadBarrio ?? "").ToUpperInvariant(), w: 390, lineas: 2, bold: true);
        if (!string.IsNullOrWhiteSpace(d.Provincia)) T(z, 396, 1062, 28, d.Provincia!, w: 390);
        z.Append("^FO16,1100^GB780,3,3^FS\n");

        if (!string.IsNullOrWhiteSpace(d.Referencia)) T(z, 32, 1116, 28, "Referencia: " + d.Referencia, w: 750, lineas: 9);

        z.Append("^FO16,1420^GB780,3,3^FS\n");
        z.Append($"^FO110,1444^BY3^BCN,110,N,N,N,A^FD{d.Envio}^FS\n");
        T(z, 16, 1566, 28, $"ENVÍO {d.Envio}", w: 780, align: "C");
        z.Append("^XZ\n");
        return z.ToString();
    }

    private static string TextoTroquel(Datos d)
    {
        var partes = new List<string>();
        foreach (var p in d.Productos.Take(4))
        {
            var s = (d.Productos.Count > 1 ? $"×{p.Cantidad} " : "") + p.Titulo;
            if (p.Contiene.Count > 0) s += " \\& Contiene: " + string.Join(", ", p.Contiene);
            if (!string.IsNullOrEmpty(p.Sku)) s += " \\& SKU: " + p.Sku;
            partes.Add(s);
        }
        if (d.Productos.Count > 4) partes.Add($"(+{d.Productos.Count - 4} productos más)");
        return string.Join(" \\& ", partes);
    }

    /// <summary>
    /// Un texto ZPL. El "negrita" es el mismo truco que usa MeLi: imprimirlo dos veces corrido 1 punto.
    /// Los textos de varios renglones los partimos NOSOTROS (no ^FB): si un texto no entra, la impresora
    /// escribe lo que sobra ENCIMA del último renglón (pasó el 24/09 con una dirección larga). Acá, si no
    /// entra, se achica la letra hasta 60% y, como último recurso, se corta con "…".
    /// </summary>
    private static void T(StringBuilder z, int x, int y, int alto, string texto, int w = 0, int lineas = 1, string align = "L", bool bold = false)
    {
        // ^ y ~ son comandos en ZPL: fuera. "\&" separa párrafos a propósito (troquel).
        var t = texto.Replace("^", " ").Replace("~", " ").Replace("\r", " ").Replace("\n", " ");
        if (w <= 0 || (lineas == 1 && align == "C"))
        {
            var fb = w > 0 ? $"^FB{w},1,0,{align},0" : "";
            z.Append($"^FO{x},{y}^A0N,{alto},{alto}{fb}^FD{t}^FS\n");
            if (bold) z.Append($"^FO{x + 1},{y}^A0N,{alto},{alto}{fb}^FD{t}^FS\n");
            return;
        }

        var h = alto;
        List<string> rs = Partir(t, w, h);
        while (rs.Count > lineas && h > alto * 0.6)
        {
            h = (int)(h * 0.9);
            rs = Partir(t, w, h);
        }
        if (rs.Count > lineas)
        {
            rs = rs.Take(lineas).ToList();
            rs[^1] = rs[^1].TrimEnd() + "…";
        }
        // mantener el alto total del bloque: si se achicó la letra, entran más renglones en el mismo lugar
        for (int i = 0; i < rs.Count; i++)
        {
            var yy = y + i * (h + 4);
            z.Append($"^FO{x},{yy}^A0N,{h},{h}^FB{w},1,0,{align},0^FD{rs[i]}^FS\n");
            if (bold) z.Append($"^FO{x + 1},{yy}^A0N,{h},{h}^FB{w},1,0,{align},0^FD{rs[i]}^FS\n");
        }
    }

    /// <summary>Parte en renglones por palabras. Ancho de letra estimado de la fuente 0 de Zebra: ~0,5 del alto (en mayúsculas, a lo sumo).</summary>
    private static List<string> Partir(string texto, int ancho, int alto)
    {
        int max = Math.Max(8, (int)(ancho / (alto * 0.5)));
        var res = new List<string>();
        foreach (var parrafo in texto.Split("\\&"))
        {
            var linea = new StringBuilder();
            foreach (var pal in parrafo.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = pal;
                while (p.Length > max) { if (linea.Length > 0) { res.Add(linea.ToString()); linea.Clear(); } res.Add(p[..max]); p = p[max..]; }
                if (linea.Length > 0 && linea.Length + 1 + p.Length > max) { res.Add(linea.ToString()); linea.Clear(); }
                if (linea.Length > 0) linea.Append(' ');
                linea.Append(p);
            }
            if (linea.Length > 0) res.Add(linea.ToString());
        }
        return res;
    }

    // Logo de WhatsApp (el mismo dibujo que usa el sistema en la web), pasado a puntos para la térmica.
    private const string WhatsappSvgPath = "M17.472 14.382c-.297-.149-1.758-.867-2.03-.967-.273-.099-.471-.148-.67.15-.197.297-.767.966-.94 1.164-.173.199-.347.223-.644.075-.297-.15-1.255-.463-2.39-1.475-.883-.788-1.48-1.761-1.653-2.059-.173-.297-.018-.458.13-.606.134-.133.298-.347.446-.52.149-.174.198-.298.298-.497.099-.198.05-.371-.025-.52-.075-.149-.669-1.612-.916-2.207-.242-.579-.487-.5-.669-.51-.173-.008-.371-.01-.57-.01-.198 0-.52.074-.792.372-.272.297-1.04 1.016-1.04 2.479 0 1.462 1.065 2.875 1.213 3.074.149.198 2.096 3.2 5.077 4.487.709.306 1.262.489 1.694.625.712.227 1.36.195 1.871.118.571-.085 1.758-.719 2.006-1.413.248-.694.248-1.289.173-1.413-.074-.124-.272-.198-.57-.347m-5.421 7.403h-.004a9.87 9.87 0 01-5.031-1.378l-.361-.214-3.741.982.998-3.648-.235-.374a9.86 9.86 0 01-1.51-5.26c.001-5.45 4.436-9.884 9.888-9.884 2.64 0 5.122 1.03 6.988 2.898a9.825 9.825 0 012.893 6.994c-.003 5.45-4.437 9.884-9.885 9.884m8.413-18.297A11.815 11.815 0 0012.05 0C5.495 0 .16 5.335.157 11.892c0 2.096.547 4.142 1.588 5.945L.057 24l6.305-1.654a11.882 11.882 0 005.683 1.448h.005c6.554 0 11.89-5.335 11.893-11.893a11.821 11.821 0 00-3.48-8.413Z";
    private static string? _iconoZpl;

    private static string IconoWhatsappZpl()
    {
        if (_iconoZpl is not null) return _iconoZpl;
        const int lado = 32;
        using var bmp = new SkiaSharp.SKBitmap(lado, lado);
        using (var canvas = new SkiaSharp.SKCanvas(bmp))
        {
            canvas.Clear(SkiaSharp.SKColors.White);
            // Svg.Skia entiende la notación abreviada de los arcos del path ("0 01-5.031..."); el
            // parser de SkiaSharp (SKPath.ParseSvgPathData) no, y devuelve null.
            using var svg = new Svg.Skia.SKSvg();
            svg.FromSvg($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><path fill=\"#000\" d=\"{WhatsappSvgPath}\"/></svg>");
            if (svg.Picture is not null)
            {
                var r = svg.Picture.CullRect;
                canvas.Scale(lado / Math.Max(r.Width, r.Height));
                canvas.DrawPicture(svg.Picture);
            }
        }
        int bytesFila = (lado + 7) / 8;
        var hex = new StringBuilder();
        for (int yy = 0; yy < lado; yy++)
            for (int bx = 0; bx < bytesFila; bx++)
            {
                int b = 0;
                for (int bit = 0; bit < 8; bit++)
                {
                    int xx = bx * 8 + bit;
                    if (xx < lado && bmp.GetPixel(xx, yy).Red < 128) b |= 0x80 >> bit;
                }
                hex.Append(b.ToString("X2"));
            }
        _iconoZpl = $"^GFA,{bytesFila * lado},{bytesFila * lado},{bytesFila},{hex}";
        return _iconoZpl;
    }

    // ─────────────────────────── PDF (10 x 20 cm, una página) ───────────────────────────

    public byte[] Pdf(Datos d, Contacto k)
    {
        var barras = new ZXing.BarcodeWriterSvg
        {
            Format = ZXing.BarcodeFormat.CODE_128,
            Options = new ZXing.Common.EncodingOptions { Width = 560, Height = 110, Margin = 0, PureBarcode = true }
        }.Write(d.Envio.ToString()).Content;
        var iconoWa = $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><path fill=\"#000\" d=\"{WhatsappSvgPath}\"/></svg>";
        var totalU = d.Productos.Sum(p => p.Cantidad);
        var webs = string.Join(" · ", new[] { k.Web1, k.Web2 }.Where(x => !string.IsNullOrWhiteSpace(x)));
        const float borde = 1.2f;

        return Document.Create(c => c.Page(page =>
        {
            page.Size(100, 200, Unit.Millimetre);
            page.Margin(2, Unit.Millimetre);
            page.DefaultTextStyle(t => t.FontSize(9).FontFamily("Helvetica").FontColor(Colors.Black));
            // ScaleToFit: si algún dato viene larguísimo, achica TODO en vez de pasarse a una 2ª hoja.
            page.Content().ScaleToFit().Column(col =>
            {
                // Troquel
                col.Item().Height(33, Unit.Millimetre).Border(0.8f).BorderColor(Colors.Grey.Darken2).Padding(4).Column(tq =>
                {
                    tq.Item().Row(r =>
                    {
                        r.RelativeItem().Text(t => { t.Span("Venta: ").FontSize(8); t.Span(d.Venta.ToString()).Bold().FontSize(10); });
                        r.ConstantItem(120).AlignRight().Text("Recortá esta parte antes de pegar el paquete").FontSize(6.5f).Italic();
                    });
                    tq.Item().PaddingTop(3).Row(r =>
                    {
                        r.ConstantItem(42).Column(cc =>
                        {
                            cc.Item().AlignCenter().Text(totalU.ToString()).FontSize(30).Bold();
                            cc.Item().AlignCenter().Text(Unidades(totalU)).FontSize(7);
                        });
                        r.RelativeItem().PaddingLeft(4).Column(pc =>
                        {
                            foreach (var p in d.Productos.Take(4))
                            {
                                pc.Item().Text((d.Productos.Count > 1 ? $"×{p.Cantidad} " : "") + p.Titulo).FontSize(8.5f);
                                if (p.Contiene.Count > 0) pc.Item().Text("Contiene: " + string.Join(", ", p.Contiene)).FontSize(7.5f);
                                if (!string.IsNullOrEmpty(p.Sku)) pc.Item().Text(t => { t.Span("SKU: ").FontSize(7.5f); t.Span(p.Sku).Bold().FontSize(8); });
                            }
                            if (d.Productos.Count > 4) pc.Item().Text($"(+{d.Productos.Count - 4} productos más)").FontSize(7.5f);
                        });
                    });
                });
                col.Item().Height(4, Unit.Millimetre).AlignMiddle().Text("- - - - - - - - - - - - -  recortar  - - - - - - - - - - - - -").FontSize(7).FontColor(Colors.Grey.Darken1);

                // Cuerpo
                col.Item().Border(borde).Column(b =>
                {
                    b.Item().BorderBottom(borde).Padding(4).Column(r =>
                    {
                        r.Item().Text(k.Nombre).Bold().FontSize(10);
                        if (!string.IsNullOrWhiteSpace(k.Whatsapp))
                            r.Item().PaddingTop(1).Row(w =>
                            {
                                w.ConstantItem(12).Height(12).Svg(iconoWa);
                                w.RelativeItem().PaddingLeft(3).Text(k.Whatsapp!).Bold().FontSize(10);
                            });
                        if (!string.IsNullOrWhiteSpace(k.Email)) r.Item().Text(k.Email!).FontSize(8);
                        if (!string.IsNullOrWhiteSpace(webs)) r.Item().Text(webs).FontSize(8);
                        r.Item().PaddingTop(2).Text("¿Alguna duda o reclamo? Contactanos.").Bold().FontSize(8.5f);
                    });
                    b.Item().BorderBottom(borde).Height(12, Unit.Millimetre).Row(r =>
                    {
                        r.RelativeItem().BorderRight(borde).AlignCenter().AlignMiddle().Text("ME1").FontSize(20).Bold();
                        r.RelativeItem().AlignCenter().AlignMiddle().Column(cc =>
                        {
                            cc.Item().AlignCenter().Text("DESPACHO").FontSize(10);
                            cc.Item().AlignCenter().Text(d.FechaAr.ToString("dd/MM")).FontSize(14).Bold();
                        });
                    });
                    b.Item().BorderBottom(borde).Padding(4).Column(r =>
                    {
                        r.Item().Text("DESTINATARIO").FontSize(7);
                        r.Item().Text(d.Nombre.ToUpperInvariant()).FontSize(d.Nombre.Length > 34 ? 12 : 15).Bold().ClampLines(2);
                        if (!string.IsNullOrWhiteSpace(d.Telefono)) r.Item().PaddingTop(1).Text("Tel: " + d.Telefono).FontSize(11);
                    });
                    b.Item().BorderBottom(borde).Padding(4).Column(r =>
                    {
                        r.Item().Text(d.Calle).FontSize(d.Calle.Length > 60 ? 10 : 12).Bold().ClampLines(3);
                        if (!string.IsNullOrWhiteSpace(d.Entre)) r.Item().Text("Entre: " + d.Entre).FontSize(9);
                    });
                    b.Item().BorderBottom(borde).Row(r =>
                    {
                        r.ConstantItem(105).BorderRight(borde).Padding(3).AlignMiddle().Text(t =>
                        {
                            t.Span("CP ").FontSize(9);
                            t.Span(d.Cp ?? "-").FontSize(30).Bold();
                        });
                        r.RelativeItem().Padding(4).AlignMiddle().Column(cc =>
                        {
                            cc.Item().Text((d.CiudadBarrio ?? "").ToUpperInvariant()).FontSize(12).Bold();
                            if (!string.IsNullOrWhiteSpace(d.Provincia)) cc.Item().Text(d.Provincia!).FontSize(9);
                        });
                    });
                    b.Item().MinHeight(52, Unit.Millimetre).Padding(4).Text(t =>
                    {
                        if (string.IsNullOrWhiteSpace(d.Referencia)) return;
                        var chica = d.Referencia!.Length > 380;
                        t.ClampLines(chica ? 16 : 12);
                        t.Span("Referencia: ").Bold().FontSize(chica ? 8 : 9);
                        t.Span(d.Referencia).FontSize(chica ? 8 : 9);
                    });
                    b.Item().BorderTop(borde).PaddingVertical(4).PaddingHorizontal(20).Column(cc =>
                    {
                        cc.Item().Height(13, Unit.Millimetre).Svg(barras);
                        cc.Item().AlignCenter().Text($"ENVÍO {d.Envio}").FontSize(9).LetterSpacing(0.05f);
                    });
                });
            });
        })).GeneratePdf();
    }
}
