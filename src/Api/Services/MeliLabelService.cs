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
///   - "termica": el PDF tal cual lo entrega MeLi (una etiqueta ~10x15 por pagina, ideal Zebra).
///   - "a4-1"   : una etiqueta por hoja A4.
///   - "a4-3"   : tres etiquetas por hoja A4 (como la opcion "3 por hoja" de MeLi).
///
/// La autenticacion reusa el token por-cuenta de <see cref="MeliAccountService"/> (con refresh
/// automatico ante 401/403), igual que el resto de las llamadas a MeLi.
/// </summary>
public class MeliLabelService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MeliAccountService _accountService;

    public MeliLabelService(AppDbContext db, IHttpClientFactory httpFactory, MeliAccountService accountService)
    {
        _db = db; _httpFactory = httpFactory; _accountService = accountService;
    }

    public record LabelResult(bool Ok, byte[]? Pdf, string? Error);

    /// <summary>Devuelve un PDF con las etiquetas de los envios indicados, en el formato pedido.</summary>
    public async Task<LabelResult> GetLabelsPdfAsync(long[] shipmentIds, string formato)
    {
        shipmentIds = shipmentIds.Where(x => x > 0).Distinct().ToArray();
        if (shipmentIds.Length == 0)
            return new LabelResult(false, null, "No se indico ningun envio para imprimir.");

        // Mapear cada envio (ShippingId) a su cuenta MeLi, para saber con que token pedirlo.
        var mapa = await _db.MeliOrders
            .Where(o => o.ShippingId != null && shipmentIds.Contains(o.ShippingId.Value))
            .Select(o => new { ShipId = o.ShippingId!.Value, o.MeliAccountId })
            .Distinct()
            .ToListAsync();

        if (mapa.Count == 0)
            return new LabelResult(false, null,
                "No se encontraron los envios en el sistema. Proba sincronizar las ordenes primero.");

        var errores = new List<string>();
        // Documento con las etiquetas nativas de MeLi (una etiqueta por pagina).
        var nativas = new PdfDocument();

        foreach (var grupo in mapa.GroupBy(x => x.MeliAccountId))
        {
            var account = await _db.MeliAccounts.FindAsync(grupo.Key);
            if (account is null) { errores.Add($"Cuenta {grupo.Key} no encontrada."); continue; }

            var ids = grupo.Select(x => x.ShipId).Distinct().ToArray();
            var (bytes, err) = await FetchLabelPdfFromMeliAsync(account, ids);
            if (bytes is null) { errores.Add($"{account.Nickname}: {err}"); continue; }

            try
            {
                using var ms = new MemoryStream(bytes);
                var src = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
                for (int i = 0; i < src.PageCount; i++) nativas.AddPage(src.Pages[i]);
            }
            catch (Exception ex)
            {
                errores.Add($"{account.Nickname}: no se pudo leer el PDF de MeLi ({ex.Message}).");
            }
        }

        if (nativas.PageCount == 0)
            return new LabelResult(false, null, errores.Count > 0
                ? string.Join(" ", errores)
                : "MercadoLibre no devolvio ninguna etiqueta. Puede que el envio todavia no tenga la etiqueta lista para imprimir.");

        byte[] outBytes;
        if (string.Equals(formato, "termica", StringComparison.OrdinalIgnoreCase))
        {
            using var outMs = new MemoryStream();
            nativas.Save(outMs, false);
            outBytes = outMs.ToArray();
        }
        else
        {
            int porHoja = string.Equals(formato, "a4-3", StringComparison.OrdinalIgnoreCase) ? 3 : 1;
            outBytes = ComposeA4(nativas, porHoja);
        }

        // errores puede traer avisos parciales (algunas cuentas fallaron) aunque haya PDF.
        return new LabelResult(true, outBytes, errores.Count > 0 ? string.Join(" ", errores) : null);
    }

    /// <summary>Pide a MeLi el PDF de etiquetas de una cuenta (con refresh de token ante 401/403).</summary>
    private async Task<(byte[]? bytes, string? error)> FetchLabelPdfFromMeliAsync(MeliAccount account, long[] shipmentIds)
    {
        var token = await _accountService.GetValidTokenAsync(account);
        if (string.IsNullOrEmpty(token)) return (null, "sin token valido de MeLi.");

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        var csv = string.Join(",", shipmentIds);
        var url = $"https://api.mercadolibre.com/shipment_labels?shipment_ids={csv}&response_type=pdf";

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
        // Validar que realmente sea un PDF (empieza con "%PDF"). Si MeLi devolvio otra cosa, avisar.
        if (bytes.Length < 5 || !(bytes[0] == (byte)'%' && bytes[1] == (byte)'P' && bytes[2] == (byte)'D' && bytes[3] == (byte)'F'))
            return (null, "MeLi no devolvio un PDF de etiqueta (formato inesperado).");
        return (bytes, null);
    }

    /// <summary>
    /// Compone las etiquetas nativas de MeLi (una por pagina) en hojas A4, "porHoja" etiquetas por hoja
    /// (1 o 3), apiladas verticalmente. Preserva la proporcion de cada etiqueta (asi el codigo de barras
    /// sigue siendo escaneable) y usa PdfSharpCore (vectorial, no rasteriza).
    /// </summary>
    private static byte[] ComposeA4(PdfDocument labels, int porHoja)
    {
        if (porHoja < 1) porHoja = 1;

        using var srcMs = new MemoryStream();
        labels.Save(srcMs, false);
        var srcBytes = srcMs.ToArray();
        int total = labels.PageCount;

        var outDoc = new PdfDocument();
        const double margen = 14; // ~0.5 cm en puntos
        // Los XPdfForm leen del stream de forma diferida: hay que mantenerlos vivos hasta el Save.
        var keepAlive = new List<MemoryStream>();
        try
        {
            int idx = 0;
            while (idx < total)
            {
                var page = outDoc.AddPage();
                page.Size = PageSize.A4;
                using var gfx = XGraphics.FromPdfPage(page);
                double pw = page.Width.Point, ph = page.Height.Point;
                double slotH = (ph - 2 * margen) / porHoja;
                double slotW = pw - 2 * margen;

                for (int s = 0; s < porHoja && idx < total; s++, idx++)
                {
                    var formMs = new MemoryStream(srcBytes);
                    keepAlive.Add(formMs);
                    var form = XPdfForm.FromStream(formMs);
                    form.PageNumber = idx + 1; // 1-based

                    double lw = form.PointWidth, lh = form.PointHeight;
                    if (lw <= 0 || lh <= 0) { lw = 283; lh = 425; } // fallback ~10x15 cm

                    double scale = Math.Min(slotW / lw, slotH / lh);
                    double w = lw * scale, h = lh * scale;
                    double x = margen + (slotW - w) / 2;
                    double y = margen + s * slotH + (slotH - h) / 2;
                    gfx.DrawImage(form, x, y, w, h);
                }
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
}
