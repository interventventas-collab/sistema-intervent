using System.Text.Json;
using SkiaSharp;
using ZXing;
using ZXing.Common;

namespace Api.Services;

/// <summary>Datos del QR de AFIP de una factura (los que sirven para matchearla con el Libro IVA).</summary>
public class FacturaQrData
{
    public string? Cuit { get; set; }      // CUIT del EMISOR de la factura
    public int? PtoVta { get; set; }
    public long? NroCmp { get; set; }
    public int? TipoCmp { get; set; }
    public decimal? Importe { get; set; }
    public string? Cae { get; set; }
}

/// <summary>Lee el QR de AFIP de un PDF de factura: renderiza la página con PDFium y decodifica con ZXing.</summary>
public class FacturaQrService
{
    private readonly ILogger<FacturaQrService> _logger;
    public FacturaQrService(ILogger<FacturaQrService> logger) => _logger = logger;

    private static readonly BarcodeReaderGeneric Reader = new()
    {
        AutoRotate = true,
        Options = new DecodingOptions { TryHarder = true, PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE } }
    };

    /// <summary>Devuelve los datos del QR de AFIP del PDF, o null si no lo encuentra.</summary>
    public FacturaQrData? LeerQrDePdf(byte[] pdf)
    {
        try
        {
            foreach (var bmp in PDFtoImage.Conversion.ToImages(pdf, options: new(Dpi: 200)))
            {
                using (bmp)
                {
                    var url = DecodeAfipQr(bmp);
                    if (url != null) return ParseAfipUrl(url);
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[FacturaQr] No pude leer el QR del PDF"); }
        return null;
    }

    private static string? DecodeAfipQr(SKBitmap bmp)
    {
        var fmt = bmp.ColorType == SKColorType.Rgba8888 ? RGBLuminanceSource.BitmapFormat.RGBA32 : RGBLuminanceSource.BitmapFormat.BGRA32;
        var lum = new RGBLuminanceSource(bmp.Bytes, bmp.Width, bmp.Height, fmt);
        var res = Reader.Decode(lum);
        var t = res?.Text;
        return (t != null && t.Contains("afip.gob.ar/fe/qr", StringComparison.OrdinalIgnoreCase)) ? t : null;
    }

    /// <summary>Parsea la URL del QR de AFIP (…/fe/qr/?p=BASE64) y saca los datos de la factura.</summary>
    public static FacturaQrData? ParseAfipUrl(string url)
    {
        var i = url.IndexOf("p=", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var b64 = url[(i + 2)..].Split('&')[0].Replace('-', '+').Replace('_', '/');
        switch (b64.Length % 4) { case 2: b64 += "=="; break; case 3: b64 += "="; break; }
        try
        {
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string? S(string k) => r.TryGetProperty(k, out var v) ? v.ToString() : null;
            long? L(string k) => r.TryGetProperty(k, out var v) && v.TryGetInt64(out var n) ? n : null;
            int? I(string k) => r.TryGetProperty(k, out var v) && v.TryGetInt32(out var n) ? n : null;
            decimal? D(string k) => r.TryGetProperty(k, out var v) && v.TryGetDecimal(out var n) ? n : null;
            return new FacturaQrData
            {
                Cuit = L("cuit")?.ToString(),
                PtoVta = I("ptoVta"),
                NroCmp = L("nroCmp"),
                TipoCmp = I("tipoCmp"),
                Importe = D("importe"),
                Cae = L("codAut")?.ToString()
            };
        }
        catch { return null; }
    }
}
