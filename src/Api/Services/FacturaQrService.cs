using System.Globalization;
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

    /// <summary>Devuelve los datos del QR de AFIP del PDF, o null si no lo encuentra.
    /// Estrategia: renderiza a 300 DPI y prueba primero la hoja entera; si no lo encuentra, recorta
    /// las zonas donde AFIP suele poner el QR (abajo-izquierda / franja inferior), donde el QR queda
    /// grande y se detecta aunque en la hoja completa sea chico.</summary>
    public FacturaQrData? LeerQrDePdf(byte[] pdf)
    {
        try
        {
            foreach (var bmp in PDFtoImage.Conversion.ToImages(pdf, options: new(Dpi: 300)))
            {
                using (bmp)
                {
                    // 1) Hoja completa.
                    var url = DecodeAfipQr(bmp);
                    // 2) Recortes candidatos (el QR de AFIP va abajo, casi siempre a la izquierda).
                    if (url == null)
                    {
                        foreach (var (x0, y0, x1, y1) in Regiones)
                        {
                            using var crop = Recortar(bmp, x0, y0, x1, y1);
                            if (crop == null) continue;
                            url = DecodeAfipQr(crop);
                            if (url != null) break;
                        }
                    }
                    // 3) Último recurso: zbar (más potente que ZXing para QR densos como los de Contabilium).
                    if (url == null) url = DecodeConZbar(bmp);
                    if (url != null) return ParseAfipUrl(url);
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[FacturaQr] No pude leer el QR del PDF"); }
        return null;
    }

    // Zonas (en fracciones de la hoja) donde suele estar el QR de AFIP, de la más probable a la menos.
    private static readonly (double, double, double, double)[] Regiones =
    {
        (0.00, 0.60, 0.45, 1.00), // abajo-izquierda (lo más común)
        (0.00, 0.50, 0.55, 1.00), // abajo-izquierda más amplio
        (0.00, 0.70, 1.00, 1.00), // franja inferior completa
        (0.20, 0.55, 0.80, 1.00), // abajo-centro (algunos formatos)
    };

    /// <summary>Decodifica el QR con zbar (zbarimg), que es más robusto que ZXing con QR densos.
    /// Renderiza el bitmap a PNG en un archivo temporal y lo pasa a zbarimg. Devuelve la URL de AFIP o null.</summary>
    private string? DecodeConZbar(SKBitmap bmp)
    {
        string? tmp = null;
        try
        {
            tmp = Path.Combine(Path.GetTempPath(), "qr-" + Guid.NewGuid().ToString("N") + ".png");
            using (var img = SKImage.FromBitmap(bmp))
            using (var data = img.Encode(SKEncodedImageFormat.Png, 90))
            using (var fs = File.OpenWrite(tmp))
                data.SaveTo(fs);

            var psi = new System.Diagnostics.ProcessStartInfo("zbarimg", $"--quiet --raw \"{tmp}\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return null;
            var outp = p.StandardOutput.ReadToEnd();
            _ = p.StandardError.ReadToEnd(); // vaciar stderr (zbarimg escribe avisos de dbus) para no bloquear
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return null; }
            foreach (var line in outp.Split('\n'))
            {
                var t = line.Trim();
                if (EsQrFiscal(t)) return t;
            }
            return null;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[FacturaQr] Falló el lector zbar"); return null; }
        finally { if (tmp != null) { try { File.Delete(tmp); } catch { } } }
    }

    private static SKBitmap? Recortar(SKBitmap b, double fx0, double fy0, double fx1, double fy1)
    {
        int x = (int)(fx0 * b.Width), y = (int)(fy0 * b.Height);
        int w = (int)((fx1 - fx0) * b.Width), h = (int)((fy1 - fy0) * b.Height);
        if (w <= 8 || h <= 8) return null;
        var crop = new SKBitmap(w, h, b.ColorType, b.AlphaType);
        using var canvas = new SKCanvas(crop);
        canvas.DrawBitmap(b, new SKRect(x, y, x + w, y + h), new SKRect(0, 0, w, h));
        return crop;
    }

    private static string? DecodeAfipQr(SKBitmap bmp)
    {
        // Construyo un buffer RGB24 desde los píxeles reales (sin depender del stride ni del orden BGRA/RGBA,
        // que era lo que hacía fallar a ZXing en algunas facturas aunque el QR estuviera perfecto).
        var px = bmp.Pixels;
        var rgb = new byte[px.Length * 3];
        for (int i = 0; i < px.Length; i++)
        {
            var c = px[i];
            rgb[i * 3] = c.Red; rgb[i * 3 + 1] = c.Green; rgb[i * 3 + 2] = c.Blue;
        }
        var lum = new RGBLuminanceSource(rgb, bmp.Width, bmp.Height, RGBLuminanceSource.BitmapFormat.RGB24);
        var res = Reader.Decode(lum);
        var t = res?.Text;
        return (t != null && EsQrFiscal(t)) ? t : null;
    }

    /// <summary>True si el texto es el QR fiscal de una factura. Acepta el dominio viejo (afip.gob.ar)
    /// y el nuevo (arca.gob.ar) — AFIP pasó a llamarse ARCA y las facturas nuevas traen ese dominio.</summary>
    private static bool EsQrFiscal(string t) => t.Contains("/fe/qr", StringComparison.OrdinalIgnoreCase)
        && (t.Contains("afip.gob.ar", StringComparison.OrdinalIgnoreCase) || t.Contains("arca.gob.ar", StringComparison.OrdinalIgnoreCase));

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
            // Algunas facturas (ej. a consumidor final) generan JSON inválido con campos vacíos, tipo
            // "nroDocRec":,  → lo saneo rellenando esos vacíos con null antes de parsear.
            json = System.Text.RegularExpressions.Regex.Replace(json, @":\s*([,}])", ":null$1");
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            // Los campos del QR de AFIP a veces vienen como número y a veces como texto (según quién generó
            // la factura). Estos helpers aceptan las dos formas y nunca tiran excepción.
            long? L(string k)
            {
                if (!r.TryGetProperty(k, out var v)) return null;
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
                if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var m)) return m;
                return null;
            }
            int? I(string k)
            {
                if (!r.TryGetProperty(k, out var v)) return null;
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
                if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var m)) return m;
                return null;
            }
            decimal? D(string k)
            {
                if (!r.TryGetProperty(k, out var v)) return null;
                if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n)) return n;
                if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var m)) return m;
                return null;
            }
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
