using Api.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Globalization;

namespace Api.Services;

/// <summary>
/// Genera el "Recibo de Visita por Cobranza" — un PDF imprimible para que el repartidor
/// lleve cuando va a visitar a un cliente solo a cobrar un comprobante antiguo.
///
/// Contiene los datos del cliente y del comprobante a cobrar bien grandes para facilitar
/// la lectura, espacios en blanco para llenar a mano (fecha, importe, medio, firma), y un
/// QR para que el repartidor pueda cargar la cobranza digital desde su celular.
///
/// Pedido del usuario 2026-05-20.
/// </summary>
public class CafeReciboVisitaCobranzaPdfService
{
    private readonly FileStorageService _files;
    private readonly ILogger<CafeReciboVisitaCobranzaPdfService> _logger;
    private static readonly CultureInfo Es = new("es-AR");

    static CafeReciboVisitaCobranzaPdfService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public CafeReciboVisitaCobranzaPdfService(FileStorageService files, ILogger<CafeReciboVisitaCobranzaPdfService> logger)
    {
        _files = files;
        _logger = logger;
    }

    public byte[] GenerarPdf(CafeVenta v, decimal saldoPendiente, CafeSetting? cfg, byte[]? qrRepartidor)
    {
        cfg ??= new CafeSetting();
        var numero = $"VIS-{DateTime.Now.Year:0000}-{v.Id:0000}";
        var logoBytes = TryLoadLogoBytes(cfg.NegocioLogoUrl) ?? TryLoadLogoFallback(cfg.NegocioCuit);
        // Monto cobrable (con IVA si aplica) para mostrar el total real del comprobante
        var totalCobrable = (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m) ? v.ArcaImpTotal.Value : v.Total;

        var pdf = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(28);
                page.DefaultTextStyle(t => t.FontSize(10));

                // ── HEADER ──
                page.Header().Row(row =>
                {
                    row.RelativeItem(2).Row(r =>
                    {
                        if (logoBytes is not null)
                            r.ConstantItem(110).Height(65).AlignLeft().AlignMiddle().Image(logoBytes).FitArea();
                        r.RelativeItem().PaddingLeft(logoBytes is null ? 0 : 10).Column(c =>
                        {
                            var razon = string.IsNullOrWhiteSpace(cfg.NegocioRazonSocial)
                                ? (cfg.NegocioNombre ?? "Empresa") : cfg.NegocioRazonSocial!;
                            c.Item().Text(razon).FontSize(13).Bold();
                            if (!string.IsNullOrEmpty(cfg.NegocioDireccion))
                                c.Item().Text(cfg.NegocioDireccion!).FontSize(8);
                            if (!string.IsNullOrEmpty(cfg.NegocioTelefono))
                                c.Item().Text("Tel: " + cfg.NegocioTelefono).FontSize(8).FontColor(Colors.Grey.Darken1);
                        });
                    });
                    row.RelativeItem().AlignRight().Column(c =>
                    {
                        c.Item().AlignRight().Background(Colors.Amber.Lighten4).Border(1).BorderColor(Colors.Amber.Medium)
                            .Padding(6).Text("🚪 VISITA POR COBRANZA").FontSize(11).Bold().FontColor(Colors.Amber.Darken3);
                        c.Item().AlignRight().PaddingTop(4).Text($"N° {numero}").FontSize(10).Bold().FontFamily("Courier");
                        c.Item().AlignRight().Text($"Fecha: {DateTime.Now:dd/MM/yyyy}").FontSize(9);
                    });
                });

                // ── CONTENT ──
                page.Content().PaddingTop(15).Column(col =>
                {
                    // CLIENTE — bloque grande para que el repartidor lea la dirección bien
                    col.Item().Background(Colors.Blue.Lighten5).Border(1).BorderColor(Colors.Blue.Medium).Padding(12).Column(c =>
                    {
                        c.Item().Text("CLIENTE").FontSize(8).Bold().FontColor(Colors.Grey.Darken2).LetterSpacing(0.08f);
                        c.Item().PaddingTop(4).Text(v.ClienteNombreSnapshot ?? "(sin cliente)").FontSize(16).Bold();
                        if (!string.IsNullOrWhiteSpace(v.ClienteRazonSocialSnapshot))
                            c.Item().Text(v.ClienteRazonSocialSnapshot!).FontSize(10).Italic().FontColor(Colors.Grey.Darken1);
                        if (!string.IsNullOrWhiteSpace(v.ClienteCuitSnapshot))
                            c.Item().PaddingTop(2).Text($"CUIT/DNI: {v.ClienteCuitSnapshot}").FontSize(9);
                        // Dirección y zona BIEN GRANDES para que se lean en el celu del repartidor
                        if (!string.IsNullOrWhiteSpace(v.ClienteDireccionSnapshot))
                        {
                            c.Item().PaddingTop(6).Row(r =>
                            {
                                r.ConstantItem(22).Text("📍").FontSize(16);
                                r.RelativeItem().Text(v.ClienteDireccionSnapshot!).FontSize(13).Bold();
                            });
                        }
                        var locParts = new[] { v.ClienteLocalidadSnapshot, v.ClienteCiudadSnapshot, v.ClienteCpSnapshot }
                            .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                        if (locParts.Count > 0)
                            c.Item().PaddingLeft(22).Text(string.Join(" · ", locParts)).FontSize(11);
                        if (!string.IsNullOrWhiteSpace(v.ClienteTelefonoSnapshot))
                        {
                            c.Item().PaddingTop(4).Row(r =>
                            {
                                r.ConstantItem(22).Text("📞").FontSize(13);
                                r.RelativeItem().Text(v.ClienteTelefonoSnapshot!).FontSize(12).Bold();
                            });
                        }
                    });

                    // COMPROBANTE A COBRAR
                    col.Item().PaddingTop(10).Background(Colors.Red.Lighten5).Border(1).BorderColor(Colors.Red.Medium).Padding(12).Column(c =>
                    {
                        c.Item().Text("COMPROBANTE A COBRAR").FontSize(8).Bold().FontColor(Colors.Grey.Darken2).LetterSpacing(0.08f);
                        c.Item().PaddingTop(4).Row(r =>
                        {
                            r.RelativeItem().Column(cc =>
                            {
                                cc.Item().Text("Número").FontSize(8).FontColor(Colors.Grey.Darken1);
                                cc.Item().Text(v.Numero).FontSize(13).Bold().FontFamily("Courier");
                            });
                            r.RelativeItem().Column(cc =>
                            {
                                cc.Item().Text("Fecha emisión").FontSize(8).FontColor(Colors.Grey.Darken1);
                                cc.Item().Text(v.Fecha.ToString("dd/MM/yyyy")).FontSize(13).Bold();
                            });
                            r.RelativeItem().Column(cc =>
                            {
                                cc.Item().Text("Total comprobante").FontSize(8).FontColor(Colors.Grey.Darken1);
                                cc.Item().Text($"$ {totalCobrable.ToString("N2", Es)}").FontSize(13).Bold();
                            });
                        });
                        c.Item().PaddingTop(8).Background(Colors.Red.Lighten4).Padding(8).Row(r =>
                        {
                            r.RelativeItem().AlignCenter().Text("SALDO PENDIENTE A COBRAR").FontSize(9).Bold().FontColor(Colors.Red.Darken3);
                        });
                        c.Item().Background(Colors.Red.Lighten4).PaddingBottom(8).Row(r =>
                        {
                            r.RelativeItem().AlignCenter().Text($"$ {saldoPendiente.ToString("N2", Es)}").FontSize(24).Bold().FontColor(Colors.Red.Darken3);
                        });
                    });

                    // FORM PARA LLENAR A MANO
                    col.Item().PaddingTop(10).Border(1).BorderColor(Colors.Grey.Medium).Padding(12).Column(c =>
                    {
                        c.Item().Text("REGISTRO DE COBRANZA").FontSize(8).Bold().FontColor(Colors.Grey.Darken2).LetterSpacing(0.08f);

                        c.Item().PaddingTop(8).Row(r =>
                        {
                            r.RelativeItem().Column(cc =>
                            {
                                cc.Item().Text("Fecha de visita").FontSize(8).FontColor(Colors.Grey.Darken1);
                                cc.Item().PaddingTop(14).BorderBottom(0.8f).BorderColor(Colors.Black).Height(2);
                            });
                            r.ConstantItem(20);
                            r.RelativeItem().Column(cc =>
                            {
                                cc.Item().Text("Hora").FontSize(8).FontColor(Colors.Grey.Darken1);
                                cc.Item().PaddingTop(14).BorderBottom(0.8f).BorderColor(Colors.Black).Height(2);
                            });
                        });

                        c.Item().PaddingTop(12).Row(r =>
                        {
                            r.RelativeItem().Column(cc =>
                            {
                                cc.Item().Text("Importe cobrado").FontSize(8).FontColor(Colors.Grey.Darken1);
                                cc.Item().PaddingTop(14).BorderBottom(0.8f).BorderColor(Colors.Black).Height(2);
                            });
                        });

                        c.Item().PaddingTop(12).Text("Medio de pago").FontSize(8).FontColor(Colors.Grey.Darken1);
                        c.Item().PaddingTop(4).Row(r =>
                        {
                            r.RelativeItem().Text("[ ] Efectivo").FontSize(10);
                            r.RelativeItem().Text("[ ] Transferencia").FontSize(10);
                            r.RelativeItem().Text("[ ] Cheque").FontSize(10);
                            r.RelativeItem().Text("[ ] Otro: __________").FontSize(10);
                        });

                        c.Item().PaddingTop(10).Text("Observaciones").FontSize(8).FontColor(Colors.Grey.Darken1);
                        c.Item().PaddingTop(14).BorderBottom(0.8f).BorderColor(Colors.Black).Height(2);
                        c.Item().PaddingTop(14).BorderBottom(0.8f).BorderColor(Colors.Black).Height(2);
                    });

                    // QR del repartidor
                    if (qrRepartidor is not null)
                    {
                        col.Item().PaddingTop(10).Background(Colors.Grey.Lighten4).Border(1).BorderColor(Colors.Grey.Lighten1).Padding(10).Row(r =>
                        {
                            r.ConstantItem(90).Height(90).Image(qrRepartidor).FitArea();
                            r.RelativeItem().PaddingLeft(10).AlignMiddle().Column(c =>
                            {
                                c.Item().Text("📲 Cargá la cobranza desde el celu").FontSize(11).Bold();
                                c.Item().PaddingTop(3).Text("Escaneá este QR con tu celular para cargar la cobranza digital. Te pide tu PIN y listo.")
                                    .FontSize(8).FontColor(Colors.Grey.Darken2);
                            });
                        });
                    }
                });

                // ── FOOTER — firmas ──
                page.Footer().Column(col =>
                {
                    col.Item().PaddingTop(20).Row(r =>
                    {
                        r.RelativeItem().Column(c =>
                        {
                            c.Item().PaddingTop(25).BorderTop(0.8f).BorderColor(Colors.Black).PaddingTop(3).AlignCenter()
                                .Text("Firma del cliente").FontSize(9);
                            c.Item().AlignCenter().Text("Aclaración / DNI").FontSize(7).FontColor(Colors.Grey.Darken1);
                        });
                        r.ConstantItem(30);
                        r.RelativeItem().Column(c =>
                        {
                            c.Item().PaddingTop(25).BorderTop(0.8f).BorderColor(Colors.Black).PaddingTop(3).AlignCenter()
                                .Text("Firma del repartidor").FontSize(9);
                            c.Item().AlignCenter().Text(cfg.NegocioRazonSocial ?? cfg.NegocioNombre ?? "").FontSize(7).FontColor(Colors.Grey.Darken1);
                        });
                    });
                    col.Item().PaddingTop(8).AlignCenter().Text($"Documento generado el {DateTime.Now:dd/MM/yyyy HH:mm}")
                        .FontSize(6).FontColor(Colors.Grey.Medium);
                });
            });
        });

        return pdf.GeneratePdf();
    }

    private byte[]? TryLoadLogoBytes(string? logoUrl)
    {
        if (string.IsNullOrWhiteSpace(logoUrl)) return null;
        try
        {
            var idx = logoUrl.IndexOf("path=", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            var rel = Uri.UnescapeDataString(logoUrl[(idx + 5)..].Split('&')[0]);
            var abs = _files.ResolveSafe(rel);
            return File.Exists(abs) ? File.ReadAllBytes(abs) : null;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "logo principal no se pudo leer"); return null; }
    }

    private byte[]? TryLoadLogoFallback(string? cuitRaw)
    {
        if (string.IsNullOrWhiteSpace(cuitRaw)) return null;
        var cuit = new string(cuitRaw.Where(char.IsDigit).ToArray());
        if (cuit.Length != 11) return null;
        try
        {
            var ext = new[] { ".png", ".jpg", ".jpeg", ".webp", ".svg" };
            foreach (var e in ext)
            {
                var rel = $"Logos Empresa/{cuit}/logo{e}";
                var abs = _files.ResolveSafe(rel);
                if (File.Exists(abs)) return File.ReadAllBytes(abs);
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "logo fallback no se pudo leer"); }
        return null;
    }
}
