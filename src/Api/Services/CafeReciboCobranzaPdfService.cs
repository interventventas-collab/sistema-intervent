using Api.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Globalization;

namespace Api.Services;

/// <summary>
/// Genera el PDF del recibo de cobranza (Cafe → Tesorería → Cobranza → Imprimir).
/// Documento simple: cabecera con datos del negocio + tabla de comprobantes cobrados
/// + tabla de formas de cobro + retenciones + total + observaciones.
/// </summary>
public class CafeReciboCobranzaPdfService
{
    private readonly ILogger<CafeReciboCobranzaPdfService> _logger;
    private static readonly CultureInfo Es = new("es-AR");

    static CafeReciboCobranzaPdfService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public CafeReciboCobranzaPdfService(ILogger<CafeReciboCobranzaPdfService> logger)
    {
        _logger = logger;
    }

    public byte[] GenerarPdfBytes(
        CafeCobranza cobranza,
        CafeCliente cliente,
        List<(string numero, decimal importe, bool aCuenta)> comprobantes,
        List<(string cajaNombre, decimal importe, string? referencia, string? chequeInfo)> medios,
        CafeSetting? cfg)
    {
        cfg ??= new CafeSetting();

        var pdf = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(20);
                page.DefaultTextStyle(t => t.FontSize(9));

                // ─── HEADER ───
                page.Header().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text(cfg.NegocioNombre ?? "Mi Empresa").FontSize(14).SemiBold();
                            if (!string.IsNullOrWhiteSpace(cfg.NegocioRazonSocial) && cfg.NegocioRazonSocial != cfg.NegocioNombre)
                                c.Item().Text(cfg.NegocioRazonSocial).FontSize(8);
                            if (!string.IsNullOrWhiteSpace(cfg.NegocioCuit))
                                c.Item().Text($"CUIT: {cfg.NegocioCuit}").FontSize(8);
                            if (!string.IsNullOrWhiteSpace(cfg.NegocioDireccion))
                                c.Item().Text(cfg.NegocioDireccion).FontSize(8);
                            if (!string.IsNullOrWhiteSpace(cfg.NegocioTelefono))
                                c.Item().Text($"Tel: {cfg.NegocioTelefono}").FontSize(8);
                        });

                        row.ConstantItem(170).Column(c =>
                        {
                            c.Item().AlignRight().Background("#1d4ed8").Padding(8).Column(cc =>
                            {
                                cc.Item().AlignCenter().Text("RECIBO DE COBRANZA").FontColor(Colors.White).SemiBold().FontSize(11);
                                cc.Item().AlignCenter().Text($"N° {cobranza.Numero}").FontColor(Colors.White).SemiBold().FontSize(10);
                            });
                            c.Item().PaddingTop(4).AlignRight().Text($"Fecha: {cobranza.Fecha.ToLocalTime():dd/MM/yyyy}").FontSize(9);
                        });
                    });

                    col.Item().PaddingTop(8).BorderTop(0.5f).PaddingTop(6).Column(c =>
                    {
                        c.Item().Text(t =>
                        {
                            t.Span("Recibido de: ").SemiBold();
                            t.Span(cliente.Nombre).SemiBold();
                        });
                        if (!string.IsNullOrWhiteSpace(cliente.RazonSocial) && cliente.RazonSocial != cliente.Nombre)
                            c.Item().Text(t => { t.Span("Razón social: ").SemiBold(); t.Span(cliente.RazonSocial); });
                        if (!string.IsNullOrWhiteSpace(cliente.Cuit))
                            c.Item().Text(t => { t.Span("CUIT/DNI: ").SemiBold(); t.Span(cliente.Cuit); });
                        if (!string.IsNullOrWhiteSpace(cliente.Direccion))
                            c.Item().Text(cliente.Direccion).FontSize(8);
                    });
                });

                // ─── BODY ───
                page.Content().PaddingVertical(10).Column(col =>
                {
                    col.Spacing(8);

                    // Comprobantes
                    col.Item().Text("Aplicado a:").SemiBold().FontSize(10);
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c => { c.RelativeColumn(2); c.RelativeColumn(); });
                        t.Header(h =>
                        {
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(4).Text("Comprobante").SemiBold().FontSize(9);
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(4).AlignRight().Text("Importe").SemiBold().FontSize(9);
                        });
                        foreach (var c in comprobantes)
                        {
                            t.Cell().Padding(3).Text(c.aCuenta ? "A CUENTA (sin imputar a comprobante)" : c.numero).FontSize(9);
                            t.Cell().Padding(3).AlignRight().Text($"$ {Fmt(c.importe)}").FontSize(9);
                        }
                    });

                    // Formas de cobro
                    col.Item().PaddingTop(6).Text("Forma de cobro:").SemiBold().FontSize(10);
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c => { c.RelativeColumn(2); c.RelativeColumn(2); c.RelativeColumn(); });
                        t.Header(h =>
                        {
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(4).Text("Forma").SemiBold().FontSize(9);
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(4).Text("Detalle").SemiBold().FontSize(9);
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(4).AlignRight().Text("Importe").SemiBold().FontSize(9);
                        });
                        foreach (var m in medios)
                        {
                            t.Cell().Padding(3).Text(m.cajaNombre).FontSize(9);
                            t.Cell().Padding(3).Text((m.referencia ?? "") + (string.IsNullOrEmpty(m.chequeInfo) ? "" : " · " + m.chequeInfo)).FontSize(8);
                            t.Cell().Padding(3).AlignRight().Text($"$ {Fmt(m.importe)}").FontSize(9);
                        }
                    });

                    // Totales
                    col.Item().PaddingTop(8).AlignRight().Column(c =>
                    {
                        c.Item().Width(220).Row(r =>
                        {
                            r.RelativeItem().Text("Subtotal medios de cobro:").FontSize(9);
                            r.AutoItem().Text($"$ {Fmt(cobranza.Total)}").SemiBold().FontSize(9);
                        });
                        if (cobranza.Retenciones > 0)
                        {
                            c.Item().Width(220).Row(r =>
                            {
                                r.RelativeItem().Text("+ Retenciones sufridas:").FontSize(9);
                                r.AutoItem().Text($"$ {Fmt(cobranza.Retenciones)}").SemiBold().FontSize(9);
                            });
                        }
                        c.Item().Width(220).PaddingTop(2).Background("#1d4ed8").Padding(4).Row(r =>
                        {
                            r.RelativeItem().Text("TOTAL CANCELADO").FontColor(Colors.White).SemiBold().FontSize(10);
                            r.AutoItem().Text($"$ {Fmt(cobranza.Total + cobranza.Retenciones)}").FontColor(Colors.White).SemiBold().FontSize(11);
                        });
                    });

                    if (!string.IsNullOrWhiteSpace(cobranza.Observaciones))
                    {
                        col.Item().PaddingTop(8).BorderTop(0.3f).PaddingTop(4).Column(c =>
                        {
                            c.Item().Text("Observaciones:").SemiBold().FontSize(9);
                            c.Item().Text(cobranza.Observaciones).FontSize(8);
                        });
                    }

                    // Firmas
                    col.Item().PaddingTop(30).Row(r =>
                    {
                        r.RelativeItem().Column(c =>
                        {
                            c.Item().BorderTop(0.5f).PaddingTop(2).AlignCenter().Text("Firma del cliente").FontSize(8);
                        });
                        r.ConstantItem(40);
                        r.RelativeItem().Column(c =>
                        {
                            c.Item().BorderTop(0.5f).PaddingTop(2).AlignCenter().Text("Firma del recibidor").FontSize(8);
                        });
                    });
                });

                // ─── FOOTER ───
                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("Recibo emitido el ").FontSize(7).FontColor("#6b7280");
                    t.Span(DateTime.Now.ToString("dd/MM/yyyy HH:mm")).FontSize(7).FontColor("#6b7280");
                    if (!string.IsNullOrWhiteSpace(cobranza.Operador))
                        t.Span($" · Operador: {cobranza.Operador}").FontSize(7).FontColor("#6b7280");
                });
            });
        }).GeneratePdf();

        return pdf;
    }

    private static string Fmt(decimal v) => v.ToString("N2", Es);
}
