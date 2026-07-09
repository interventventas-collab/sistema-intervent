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
        // 1) Camino rápido: PDFium + ZXing (hoja entera y recortes), sin procesos externos.
        try
        {
            foreach (var bmp in PDFtoImage.Conversion.ToImages(pdf, options: new(Dpi: 300)))
            {
                using (bmp)
                {
                    var url = DecodeAfipQr(bmp);
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
                    if (url != null) return ParseAfipUrl(url);
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[FacturaQr] Falló el camino ZXing"); }

        // 2) Respaldo robusto: poppler (pdftoppm) + zbar sobre el PDF original. Lee muchos QR que
        //    el renderizador propio (PDFium) no logra leer bien.
        try { var u = DecodeConPopplerZbar(pdf); if (u != null) return ParseAfipUrl(u); }
        catch (Exception ex) { _logger.LogWarning(ex, "[FacturaQr] Falló el respaldo poppler+zbar"); }

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

    /// <summary>Respaldo robusto: renderiza el PDF con poppler (pdftoppm) a PNG y decodifica el QR con zbar
    /// (zbarimg). Es la combinación que lee bien casi todos los QR (incluidos los densos que PDFium no logra).</summary>
    private string? DecodeConPopplerZbar(byte[] pdf)
    {
        var dir = Path.Combine(Path.GetTempPath(), "qr-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var pdfPath = Path.Combine(dir, "f.pdf");
            File.WriteAllBytes(pdfPath, pdf);

            // pdftoppm genera un PNG por página: pag-1.png, pag-2.png, …
            if (!Correr("pdftoppm", $"-png -r 300 \"{pdfPath}\" \"{Path.Combine(dir, "pag")}\"", out _)) return null;

            foreach (var png in Directory.EnumerateFiles(dir, "pag*.png").OrderBy(f => f))
            {
                if (!Correr("zbarimg", $"--quiet --raw \"{png}\"", out var outp)) continue;
                foreach (var line in outp.Split('\n'))
                {
                    var t = line.Trim();
                    if (EsQrFiscal(t)) return t;
                }
            }
            return null;
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>Corre un proceso y devuelve su salida estándar. true si terminó bien a tiempo.</summary>
    private static bool Correr(string cmd, string args, out string stdout)
    {
        stdout = "";
        var psi = new System.Diagnostics.ProcessStartInfo(cmd, args)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = System.Diagnostics.Process.Start(psi);
        if (p == null) return false;
        stdout = p.StandardOutput.ReadToEnd();
        _ = p.StandardError.ReadToEnd(); // vaciar stderr (avisos de dbus de zbarimg) para no bloquear
        if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } return false; }
        return true;
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

    /// <summary>True si el texto es el QR fiscal de una factura (una URL de AFIP/ARCA con el payload ?p=BASE64).
    /// Hay varios formatos de URL según quién generó la factura, todos con el mismo payload:
    ///   • https://www.afip.gob.ar/fe/qr/?p=...              (estándar)
    ///   • https://www.arca.gob.ar/fe/qr/?p=...              (AFIP pasó a llamarse ARCA)
    ///   • https://serviciosweb.afip.gob.ar/genericos/comprobantes/cae.aspx?p=...   (formato alternativo)
    /// Por eso alcanza con: es una URL de afip/arca.gob.ar y trae "p=".</summary>
    private static bool EsQrFiscal(string t) => t.Contains("p=", StringComparison.OrdinalIgnoreCase)
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
