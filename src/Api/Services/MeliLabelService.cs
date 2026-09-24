using System.Net.Http.Headers;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;
using PdfSharpCore;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace Api.Services;

/// <summary>
/// 2026-08-13: Trae la etiqueta de envio OFICIAL de MercadoLibre (la misma que se imprime desde
/// la web de MeLi) y la devuelve lista para imprimir. Al pedirla, MeLi marca el envio como
/// "impresa" del lado de ellos (no genera una etiqueta distinta ni duplicada).
///
/// Tres formatos:
///   - "termica": el .txt para impresora Zebra (ZPL), el mismo archivo que baja MeLi.
///   - "termica-pdf": PDF con una etiqueta por pagina de 10 x 20 cm (termicas no Zebra).
///   - "a4-1"   : una etiqueta por hoja A4.
///   - "a4-3"   : A4 acostada con 3 etiquetas lado a lado, con troquel (igual que MeLi).
///
/// La autenticacion reusa el token por-cuenta de <see cref="MeliAccountService"/> (con refresh
/// automatico ante 401/403), igual que el resto de las llamadas a MeLi.
/// </summary>
public class MeliLabelService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MeliAccountService _accountService;
    private readonly MeliEtiquetaMe1Service _me1;

    public MeliLabelService(AppDbContext db, IHttpClientFactory httpFactory, MeliAccountService accountService,
        MeliEtiquetaMe1Service me1)
    {
        _db = db; _httpFactory = httpFactory; _accountService = accountService; _me1 = me1;
    }

    /// <summary>Pdf = el archivo (PDF, o .txt ZPL si EsZpl).</summary>
    public record LabelResult(bool Ok, byte[]? Pdf, string? Error, bool EsZpl = false);

    /// <summary>Devuelve un PDF con las etiquetas de los envios indicados, en el formato pedido.</summary>
    public async Task<LabelResult> GetLabelsPdfAsync(long[] shipmentIds, string formato)
    {
        shipmentIds = shipmentIds.Where(x => x > 0).Distinct().ToArray();
        if (shipmentIds.Length == 0)
            return new LabelResult(false, null, "No se indico ningun envio para imprimir.");

        // Mapear cada envio (ShippingId) a su cuenta MeLi, para saber con que token pedirlo.
        var mapa = await _db.MeliOrders
            .Where(o => o.ShippingId != null && shipmentIds.Contains(o.ShippingId.Value))
            .Select(o => new { ShipId = o.ShippingId!.Value, o.MeliAccountId, EsMe1 = o.ShippingMode == "me1" })
            .Distinct()
            .ToListAsync();

        if (mapa.Count == 0)
            return new LabelResult(false, null,
                "No se encontraron los envios en el sistema. Proba sincronizar las ordenes primero.");

        bool esTermica = string.Equals(formato, "termica", StringComparison.OrdinalIgnoreCase);
        var errores = new List<string>();
        // Termica: el .txt para impresora Zebra (ZPL) tal cual lo baja MeLi, un bloque ^XA..^XZ por etiqueta.
        var zpl = new System.Text.StringBuilder();
        // Cada etiqueta suelta: de que PDF sale, que pagina y que rectangulo de esa pagina.
        var piezas = new List<Pieza>();

        // 2026-09-24: los ME1 los despachamos nosotros y MeLi no da etiqueta: se arma la NUESTRA
        // (MeliEtiquetaMe1Service), en ZPL para la térmica directa o en PDF para el resto.
        var me1 = mapa.Where(x => x.EsMe1).Select(x => x.ShipId).Distinct().ToList();
        if (me1.Count > 0)
        {
            var (datos, errMe1) = await _me1.ArmarAsync(me1);
            errores.AddRange(errMe1);
            var contacto = await _me1.ContactoAsync();
            foreach (var d in datos)
            {
                if (esTermica) zpl.Append(_me1.Zpl(d, contacto));
                else piezas.Add(new Pieza(_me1.Pdf(d, contacto), 1, 0, 0, TermicaAncho, TermicaAlto));
            }
            await AnotarImpresionAsync(datos.Select(d => d.Envio).ToArray());
        }

        foreach (var grupo in mapa.Where(x => !x.EsMe1).GroupBy(x => x.MeliAccountId))
        {
            var account = await _db.MeliAccounts.FindAsync(grupo.Key);
            if (account is null) { errores.Add($"Cuenta {grupo.Key} no encontrada."); continue; }

            var ids = grupo.Select(x => x.ShipId).Distinct().ToArray();
            var (bytes, err) = await FetchLabelFromMeliAsync(account, ids, esTermica ? "zpl2" : "pdf");
            if (bytes is null) { errores.Add($"{account.Nickname}: {err}"); continue; }

            // 2026-09-24: MeLi ya la marco impresa; anotamos la hora de la PRIMERA impresion.
            await AnotarImpresionAsync(ids);

            if (esTermica) { zpl.Append(System.Text.Encoding.UTF8.GetString(bytes)).Append('\n'); continue; }

            try
            {
                piezas.AddRange(Recortar(bytes, ids.Length));
            }
            catch (Exception ex)
            {
                errores.Add($"{account.Nickname}: no se pudo leer el PDF de MeLi ({ex.Message}).");
            }
        }

        if (esTermica && zpl.Length > 0)
            return new LabelResult(true, System.Text.Encoding.UTF8.GetBytes(zpl.ToString()),
                errores.Count > 0 ? string.Join(" ", errores) : null, EsZpl: true);

        if (piezas.Count == 0)
            return new LabelResult(false, null, errores.Count > 0
                ? string.Join(" ", errores)
                : "MercadoLibre no devolvio ninguna etiqueta. Puede que el envio todavia no tenga la etiqueta lista para imprimir.");

        var outBytes = (formato ?? "").ToLowerInvariant() switch
        {
            "termica-pdf" => ComponerTermicaPdf(piezas),
            "a4-1" => ComponerA4Una(piezas),
            _ => ComponerA4Tres(piezas),
        };

        // errores puede traer avisos parciales (algunas cuentas fallaron) aunque haya PDF.
        return new LabelResult(true, outBytes, errores.Count > 0 ? string.Join(" ", errores) : null);
    }

    private async Task AnotarImpresionAsync(long[] shipIds)
    {
        try
        {
            var ahora = DateTime.UtcNow;
            await _db.MeliOrders
                .Where(o => o.ShippingId != null && shipIds.Contains(o.ShippingId.Value) && o.EtiquetaImpresaAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.EtiquetaImpresaAt, ahora));
        }
        catch { /* no frenar la impresion por esto */ }
    }

    /// <summary>
    /// Pide a MeLi las etiquetas de una cuenta (con refresh de token ante 401/403).
    /// tipo "pdf" devuelve el PDF; "zpl2" devuelve el texto ZPL para impresora Zebra (MeLi lo manda
    /// dentro de un ZIP; se saca el .txt de adentro).
    /// </summary>
    private async Task<(byte[]? bytes, string? error)> FetchLabelFromMeliAsync(MeliAccount account, long[] shipmentIds, string tipo)
    {
        var token = await _accountService.GetValidTokenAsync(account);
        if (string.IsNullOrEmpty(token)) return (null, "sin token valido de MeLi.");

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        var csv = string.Join(",", shipmentIds);
        var url = $"https://api.mercadolibre.com/shipment_labels?shipment_ids={csv}&response_type={tipo}";

        async Task<HttpResponseMessage> Do(string tok)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tok);
            return await http.GetAsync(url);
        }

        var resp = await Do(token);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
            resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            var fresh = await _accountService.GetValidTokenAsync(account, forceRefresh: true);
            if (!string.IsNullOrEmpty(fresh)) resp = await Do(fresh);
        }

        if (!resp.IsSuccessStatusCode)
        {
            var hint = ((int)resp.StatusCode) switch
            {
                404 => "el envio no tiene etiqueta disponible.",
                400 => "MeLi rechazo el pedido (puede que el envio no sea de un tipo con etiqueta imprimible).",
                _ => $"MeLi respondio {(int)resp.StatusCode}."
            };
            return (null, hint);
        }

        var bytes = await resp.Content.ReadAsByteArrayAsync();
        if (tipo == "zpl2") return LeerZpl(bytes);
        // Validar que realmente sea un PDF (empieza con "%PDF"). Si MeLi devolvio otra cosa, avisar.
        if (bytes.Length < 5 || !(bytes[0] == (byte)'%' && bytes[1] == (byte)'P' && bytes[2] == (byte)'D' && bytes[3] == (byte)'F'))
            return (null, "MeLi no devolvio un PDF de etiqueta (formato inesperado).");
        return (bytes, null);
    }

    /// <summary>Saca el ZPL de la respuesta de MeLi: viene en un ZIP (.txt adentro) o, a veces, como texto directo.</summary>
    private static (byte[]? bytes, string? error) LeerZpl(byte[] bytes)
    {
        if (bytes.Length > 2 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K')
        {
            using var ms = new MemoryStream(bytes);
            using var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);
            var sb = new System.Text.StringBuilder();
            foreach (var e in zip.Entries.Where(e => e.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
            {
                using var r = new StreamReader(e.Open());
                sb.Append(r.ReadToEnd()).Append('\n');
            }
            return sb.ToString().Contains("^XA")
                ? (System.Text.Encoding.UTF8.GetBytes(sb.ToString()), null)
                : (null, "MeLi no devolvio la etiqueta para impresora termica (el ZIP no trae el .txt).");
        }
        var txt = System.Text.Encoding.UTF8.GetString(bytes);
        return txt.Contains("^XA") ? (bytes, null) : (null, "MeLi no devolvio la etiqueta para impresora termica.");
    }

    // ── Armado de hojas ─────────────────────────────────────────────────────────────────────
    //
    // 2026-09-24: MeLi entrega la etiqueta de dos maneras segun la preferencia de la cuenta
    // (vendedores.mercadolibre.com.ar -> Preferencias de venta -> Formato de etiqueta):
    //   - "PDF A4": hoja A4 ACOSTADA (842x595 pt) con hasta 3 etiquetas una al lado de la otra,
    //     cada una con su troquel arriba (talon con tijerita). Medido sobre un PDF real de MeLi:
    //     columnas en x = 31 / 296 / 560, de 256 pt de ancho, y de y = 28 a 561.
    //   - termica: una etiqueta por pagina (si la preferencia de la cuenta vuelve a termica).
    // Por eso primero se recorta todo en etiquetas sueltas ("piezas") y despues se arma el
    // formato pedido. El "A4 x3" copia exactamente la grilla de MeLi.

    private const double A4Largo = 841.89, A4Corto = 595.28;
    private static readonly double[] ColX = { 29, 294, 558 };
    private const double ColY = 26, ColAncho = 260, ColAlto = 538;

    private record Pieza(byte[] Pdf, int Pagina, double X, double Y, double W, double H);

    /// <summary>Parte el PDF de MeLi en etiquetas sueltas. "esperadas" = cuantos envios se pidieron.</summary>
    private static List<Pieza> Recortar(byte[] pdf, int esperadas)
    {
        var res = new List<Pieza>();
        using var ms = new MemoryStream(pdf);
        var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
        int quedan = esperadas;
        for (int i = 0; i < doc.PageCount; i++)
        {
            var pg = doc.Pages[i];
            double w = pg.Width.Point, h = pg.Height.Point;
            bool esA4Acostada = Math.Abs(w - A4Largo) < 25 && Math.Abs(h - A4Corto) < 25;
            if (esA4Acostada)
            {
                // Hasta 3 por hoja; la ultima hoja puede venir con 1 o 2 (el resto en blanco).
                int enEsta = Math.Clamp(quedan, 1, 3);
                for (int c = 0; c < enEsta; c++)
                    res.Add(new Pieza(pdf, i + 1, ColX[c], ColY, ColAncho, ColAlto));
                quedan -= enEsta;
            }
            else
            {
                res.Add(new Pieza(pdf, i + 1, 0, 0, w, h));
                quedan--;
            }
        }
        return res;
    }

    /// <summary>Dibuja una pieza encajada (sin deformar) dentro del rectangulo destino, arriba y centrada.</summary>
    private static void Dibujar(XGraphics gfx, Pieza p, double dx, double dy, double dw, double dh,
        List<MemoryStream> keepAlive)
    {
        var formMs = new MemoryStream(p.Pdf);
        keepAlive.Add(formMs);
        var form = XPdfForm.FromStream(formMs);
        form.PageNumber = p.Pagina;

        double escala = Math.Min(dw / p.W, dh / p.H);
        double w = p.W * escala, h = p.H * escala;
        double x = dx + (dw - w) / 2, y = dy;

        var estado = gfx.Save();
        gfx.IntersectClip(new XRect(x, y, w, h));
        // La pagina entera se corre para que el rectangulo de la pieza caiga justo en (x, y).
        gfx.DrawImage(form, x - p.X * escala, y - p.Y * escala, form.PointWidth * escala, form.PointHeight * escala);
        gfx.Restore(estado);
    }

    private static byte[] Armar(IEnumerable<(XSize tam, Action<XGraphics, List<MemoryStream>> dibujo)> hojas)
    {
        var outDoc = new PdfDocument();
        // Los XPdfForm leen del stream de forma diferida: hay que mantenerlos vivos hasta el Save.
        var keepAlive = new List<MemoryStream>();
        try
        {
            foreach (var (tam, dibujo) in hojas)
            {
                var page = outDoc.AddPage();
                page.Width = XUnit.FromPoint(tam.Width);
                page.Height = XUnit.FromPoint(tam.Height);
                using var gfx = XGraphics.FromPdfPage(page);
                dibujo(gfx, keepAlive);
            }
            using var outMs = new MemoryStream();
            outDoc.Save(outMs, false);
            return outMs.ToArray();
        }
        finally
        {
            foreach (var ms in keepAlive) ms.Dispose();
        }
    }

    /// <summary>A4 acostada, 3 etiquetas una al lado de la otra, igual que MeLi.</summary>
    private static byte[] ComponerA4Tres(List<Pieza> piezas) =>
        Armar(piezas.Chunk(3).Select(grupo => (new XSize(A4Largo, A4Corto),
            (Action<XGraphics, List<MemoryStream>>)((gfx, ka) =>
            {
                for (int c = 0; c < grupo.Length; c++)
                    Dibujar(gfx, grupo[c], ColX[c], ColY, ColAncho, ColAlto, ka);
            }))));

    /// <summary>Una etiqueta por hoja A4 parada, al tamano de MeLi (no se agranda: el QR queda igual).</summary>
    private static byte[] ComponerA4Una(List<Pieza> piezas) =>
        Armar(piezas.Select(p => (new XSize(A4Corto, A4Largo),
            (Action<XGraphics, List<MemoryStream>>)((gfx, ka) =>
                Dibujar(gfx, p, 28, 28, Math.Min(p.W, A4Corto - 56), Math.Min(p.H, A4Largo - 56), ka)))));

    // 2026-09-24: pagina de 10 x 20 cm, la medida que usa el .txt de MeLi para termica (810 puntos de
    // ancho a 203 dpi, troquel + etiqueta ~19,5 cm). Antes la pagina tenia la medida del recorte
    // (9,2 x 19 cm) y la impresora la achicaba y la giraba: salia chiquita y acostada.
    private const double TermicaAncho = 283.46, TermicaAlto = 566.93;

    /// <summary>Una etiqueta por pagina de 10 x 20 cm, llenandola (termicas que no son Zebra).</summary>
    private static byte[] ComponerTermicaPdf(List<Pieza> piezas) =>
        Armar(piezas.Select(p => (new XSize(TermicaAncho, TermicaAlto),
            (Action<XGraphics, List<MemoryStream>>)((gfx, ka) => Dibujar(gfx, p, 0, 0, TermicaAncho, TermicaAlto, ka)))));
}
