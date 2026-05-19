using Api.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Globalization;

namespace Api.Services;

/// <summary>
/// Genera el comprobante PDF de una operacion de Comodato o Financiacion de maquina.
/// Para COMODATO: documento que el cliente firma reconociendo que la maquina sigue
/// siendo propiedad de la empresa, prestada en comodato.
/// Para FINANCIADA: comprobante con plan de pagos + pagos registrados + saldo.
/// Pedido del usuario 2026-05-19.
/// </summary>
public class CafeComodatoPdfService
{
    private readonly FileStorageService _files;
    private readonly ILogger<CafeComodatoPdfService> _logger;
    private static readonly CultureInfo Es = new("es-AR");

    static CafeComodatoPdfService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public CafeComodatoPdfService(FileStorageService files, ILogger<CafeComodatoPdfService> logger)
    {
        _files = files;
        _logger = logger;
    }

    public byte[] GenerarPdfBytes(CafeComodato c, CafeCliente? cliente, IEnumerable<CafeComodatoPago> pagos, CafeSetting? cfg)
    {
        cfg ??= new CafeSetting();
        var esFinanciada = c.Modalidad == "FINANCIADA";
        var prefijo = esFinanciada ? "FIN" : "COM";
        var numero = $"{prefijo}-{c.CreatedAt.Year:0000}-{c.Id:0000}";
        var sym = c.Moneda == "USD" ? "U$D" : "$";

        // Pagos ordenados por fecha (para mostrar cronologico)
        var pagosList = pagos.OrderBy(p => p.Fecha).ToList();
        var totalPagado = pagosList.Sum(p => p.Importe);
        var saldo = esFinanciada ? Math.Max(0m, (c.PrecioVenta ?? 0m) - totalPagado) : 0m;
        var porcentajePagado = (esFinanciada && (c.PrecioVenta ?? 0m) > 0m)
            ? Math.Round((totalPagado / c.PrecioVenta!.Value) * 100m, 1)
            : 0m;

        var logoBytes = TryLoadLogoBytes(cfg.NegocioLogoUrl)
                        ?? TryLoadLogoFallback(cfg.NegocioCuit);

        var pdf = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(25);
                page.DefaultTextStyle(t => t.FontSize(10));

                // ════════ HEADER ════════
                page.Header().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        row.RelativeItem(2).Row(r =>
                        {
                            if (logoBytes is not null)
                                r.ConstantItem(110).Height(65).AlignLeft().AlignMiddle().Image(logoBytes).FitArea();
                            r.RelativeItem().PaddingLeft(logoBytes is null ? 0 : 10).Column(c2 =>
                            {
                                var razon = string.IsNullOrWhiteSpace(cfg.NegocioRazonSocial)
                                    ? (cfg.NegocioNombre ?? "Empresa") : cfg.NegocioRazonSocial!;
                                c2.Item().Text(razon).FontSize(13).Bold();
                                if (!string.IsNullOrWhiteSpace(cfg.NegocioRazonSocial) && !string.IsNullOrEmpty(cfg.NegocioNombre))
                                    c2.Item().Text(cfg.NegocioNombre!).FontSize(8).Italic().FontColor(Colors.Grey.Darken1);
                                if (!string.IsNullOrEmpty(cfg.NegocioDireccion))
                                    c2.Item().Text(cfg.NegocioDireccion!).FontSize(8);
                                if (!string.IsNullOrEmpty(cfg.NegocioTelefono) || !string.IsNullOrEmpty(cfg.NegocioEmail))
                                {
                                    var contactParts = new List<string>();
                                    if (!string.IsNullOrEmpty(cfg.NegocioTelefono)) contactParts.Add("Tel: " + cfg.NegocioTelefono);
                                    if (!string.IsNullOrEmpty(cfg.NegocioEmail)) contactParts.Add(cfg.NegocioEmail!);
                                    c2.Item().Text(string.Join(" · ", contactParts)).FontSize(8).FontColor(Colors.Grey.Darken1);
                                }
                            });
                        });

                        row.RelativeItem().AlignRight().Column(c2 =>
                        {
                            c2.Item().AlignRight().Text(esFinanciada ? "💰 Comprobante de Financiación" : "🤝 Comodato de Máquina")
                                .FontSize(12).Bold().FontColor(esFinanciada ? Colors.Amber.Darken3 : Colors.Blue.Darken2);
                            c2.Item().AlignRight().PaddingTop(2).Text($"N° {numero}").FontSize(10).Bold().FontFamily("Courier");
                            c2.Item().AlignRight().Text($"Fecha emisión: {DateTime.Now:dd/MM/yyyy}").FontSize(9);
                            if (c.FechaEntrega.HasValue)
                                c2.Item().AlignRight().Text($"Fecha entrega: {c.FechaEntrega.Value:dd/MM/yyyy}").FontSize(9);
                        });
                    });
                });

                // ════════ CONTENT ════════
                page.Content().PaddingTop(15).Column(col =>
                {
                    // ── Cliente ──
                    col.Item().Background(Colors.Grey.Lighten4).Border(1).BorderColor(Colors.Grey.Lighten1).Padding(8).Column(c2 =>
                    {
                        c2.Item().Text("CLIENTE").FontSize(8).Bold().FontColor(Colors.Grey.Darken2).LetterSpacing(0.05f);
                        c2.Item().PaddingTop(2).Text(cliente?.Nombre ?? "(sin cliente)").FontSize(13).Bold();
                        if (!string.IsNullOrWhiteSpace(cliente?.RazonSocial))
                            c2.Item().Text(cliente.RazonSocial!).FontSize(9).Italic().FontColor(Colors.Grey.Darken1);
                        if (!string.IsNullOrWhiteSpace(cliente?.Cuit))
                            c2.Item().Text($"CUIT/DNI: {cliente.Cuit}").FontSize(9);
                        if (!string.IsNullOrWhiteSpace(cliente?.Direccion))
                            c2.Item().Text(cliente.Direccion!).FontSize(9);
                        var loc = string.Join(" - ", new[] { cliente?.Localidad, cliente?.Ciudad, cliente?.Cp }.Where(s => !string.IsNullOrEmpty(s)));
                        if (!string.IsNullOrEmpty(loc)) c2.Item().Text(loc).FontSize(9);
                        if (!string.IsNullOrWhiteSpace(cliente?.Telefono))
                            c2.Item().Text($"Tel: {cliente.Telefono}").FontSize(9);
                    });

                    // ── Maquina ──
                    col.Item().PaddingTop(10).Background(esFinanciada ? Colors.Amber.Lighten5 : Colors.Blue.Lighten5)
                        .Border(1).BorderColor(esFinanciada ? Colors.Amber.Medium : Colors.Blue.Medium).Padding(8).Column(c2 =>
                    {
                        c2.Item().Text("MÁQUINA").FontSize(8).Bold().FontColor(Colors.Grey.Darken2).LetterSpacing(0.05f);
                        c2.Item().PaddingTop(2).Row(r =>
                        {
                            r.RelativeItem().Column(cc =>
                            {
                                cc.Item().Text($"Marca: {c.Marca ?? "—"}").FontSize(11).Bold();
                                cc.Item().Text($"Modelo: {c.Modelo ?? "—"}").FontSize(10);
                                if (!string.IsNullOrEmpty(c.NumeroSerie))
                                    cc.Item().Text($"N° Serie: {c.NumeroSerie}").FontSize(9).FontFamily("Courier");
                            });
                            r.RelativeItem().AlignRight().Column(cc =>
                            {
                                var estadoLabel = c.Estado switch
                                {
                                    "EN_CLIENTE" => "🟢 En cliente",
                                    "EN_TALLER" => "🔧 En taller",
                                    "DEVUELTA" => "↩ Devuelta",
                                    "PAGADA" => "✅ Pagada",
                                    "BAJA" => "❌ Dada de baja",
                                    _ => c.Estado
                                };
                                cc.Item().AlignRight().Text($"Estado: {estadoLabel}").FontSize(10).Bold();
                                if (!esFinanciada && c.ValorEstimado.HasValue && c.ValorEstimado.Value > 0)
                                    cc.Item().AlignRight().Text($"Valor estimado: {sym} {c.ValorEstimado.Value.ToString("N2", Es)}").FontSize(10);
                            });
                        });
                    });

                    if (esFinanciada)
                    {
                        // ── Plan de pagos ──
                        col.Item().PaddingTop(10).Background(Colors.Amber.Lighten5).Border(1).BorderColor(Colors.Amber.Medium).Padding(8).Column(c2 =>
                        {
                            c2.Item().Text("PLAN DE PAGOS").FontSize(8).Bold().FontColor(Colors.Grey.Darken2).LetterSpacing(0.05f);
                            c2.Item().PaddingTop(4).Row(r =>
                            {
                                r.RelativeItem().Column(cc =>
                                {
                                    cc.Item().Text("Precio total").FontSize(9).FontColor(Colors.Grey.Darken1);
                                    cc.Item().Text($"{sym} {(c.PrecioVenta ?? 0m).ToString("N2", Es)}").FontSize(14).Bold().FontColor(Colors.Amber.Darken3);
                                });
                                r.RelativeItem().Column(cc =>
                                {
                                    cc.Item().Text("Cuotas").FontSize(9).FontColor(Colors.Grey.Darken1);
                                    var planTxt = c.CuotasTotales.HasValue && c.ValorCuota.HasValue
                                        ? $"{c.CuotasTotales} × {sym} {c.ValorCuota.Value.ToString("N2", Es)}"
                                        : "—";
                                    cc.Item().Text(planTxt).FontSize(11).Bold();
                                });
                                r.RelativeItem().Column(cc =>
                                {
                                    cc.Item().Text("Día de pago").FontSize(9).FontColor(Colors.Grey.Darken1);
                                    cc.Item().Text(c.DiaPagoMensual.HasValue ? $"día {c.DiaPagoMensual}" : "—").FontSize(11).Bold();
                                });
                            });
                        });

                        // ── Pagos registrados ──
                        col.Item().PaddingTop(10).Column(c2 =>
                        {
                            c2.Item().Text("📥 PAGOS REGISTRADOS").FontSize(10).Bold().FontColor(Colors.Green.Darken2);
                            if (pagosList.Count == 0)
                            {
                                c2.Item().PaddingTop(4).Text("Sin pagos registrados aún.").Italic().FontColor(Colors.Grey.Darken1);
                            }
                            else
                            {
                                c2.Item().PaddingTop(4).Table(table =>
                                {
                                    table.ColumnsDefinition(cd =>
                                    {
                                        cd.ConstantColumn(90);
                                        cd.RelativeColumn();
                                        cd.ConstantColumn(110);
                                    });
                                    table.Header(h =>
                                    {
                                        h.Cell().Background(Colors.Grey.Lighten3).Border(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(4).Text("Fecha").SemiBold().FontSize(9);
                                        h.Cell().Background(Colors.Grey.Lighten3).Border(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(4).Text("Medio / Notas").SemiBold().FontSize(9);
                                        h.Cell().Background(Colors.Grey.Lighten3).Border(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(4).AlignRight().Text("Importe").SemiBold().FontSize(9);
                                    });
                                    foreach (var p in pagosList)
                                    {
                                        table.Cell().Border(0.3f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(p.Fecha.ToString("dd/MM/yyyy")).FontSize(9);
                                        var detalle = (p.MedioPago ?? "—") + (string.IsNullOrEmpty(p.Notas) ? "" : $" — {p.Notas}");
                                        table.Cell().Border(0.3f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(detalle).FontSize(9);
                                        table.Cell().Border(0.3f).BorderColor(Colors.Grey.Lighten2).Padding(3).AlignRight().Text($"{sym} {p.Importe.ToString("N2", Es)}").FontSize(9).SemiBold().FontColor(Colors.Green.Darken3);
                                    }
                                });
                            }
                        });

                        // ── Resumen final ──
                        col.Item().PaddingTop(12).Background(Colors.Grey.Lighten4).Border(1).BorderColor(Colors.Grey.Medium).Padding(10).Column(c2 =>
                        {
                            c2.Item().Row(r =>
                            {
                                r.RelativeItem().Text("TOTAL PAGADO").FontSize(11).Bold();
                                r.AutoItem().AlignRight().Text($"{sym} {totalPagado.ToString("N2", Es)}").FontSize(13).Bold().FontColor(Colors.Green.Darken3);
                            });
                            c2.Item().Row(r =>
                            {
                                r.RelativeItem().Text($"   ({porcentajePagado.ToString("N1", Es)}% del precio total)").FontSize(9).FontColor(Colors.Grey.Darken1);
                                r.AutoItem();
                            });
                            c2.Item().PaddingTop(4).LineHorizontal(0.5f).LineColor(Colors.Grey.Medium);
                            c2.Item().PaddingTop(4).Row(r =>
                            {
                                r.RelativeItem().Text("SALDO PENDIENTE").FontSize(12).Bold().FontColor(saldo > 0 ? Colors.Red.Darken2 : Colors.Green.Darken2);
                                r.AutoItem().AlignRight().Text($"{sym} {saldo.ToString("N2", Es)}").FontSize(16).Bold()
                                    .FontColor(saldo > 0 ? Colors.Red.Darken2 : Colors.Green.Darken2);
                            });
                        });
                    }
                    else
                    {
                        // ── Comodato: nota legal ──
                        col.Item().PaddingTop(12).Background(Colors.Blue.Lighten5).Border(1).BorderColor(Colors.Blue.Medium).Padding(10).Column(c2 =>
                        {
                            c2.Item().Text("⚠ COMODATO — NO ES UNA VENTA").FontSize(10).Bold().FontColor(Colors.Blue.Darken3);
                            c2.Item().PaddingTop(4).Text(
                                "La máquina descripta arriba sigue siendo propiedad de " +
                                (cfg.NegocioRazonSocial ?? cfg.NegocioNombre ?? "la empresa") +
                                ". Se entrega al cliente en comodato (préstamo de uso) por tiempo indeterminado. " +
                                "El cliente firma este documento como conformidad de recepción y se compromete a " +
                                "mantener la máquina en buen estado y devolverla cuando finalice la relación comercial."
                            ).FontSize(9).FontColor(Colors.Grey.Darken3);
                        });
                    }

                    // ── Notas adicionales ──
                    if (!string.IsNullOrWhiteSpace(c.Notas))
                    {
                        col.Item().PaddingTop(10).BorderLeft(3).BorderColor(Colors.Orange.Darken1).Background(Colors.Orange.Lighten5).Padding(6).Text(t =>
                        {
                            t.Span("📝 Observaciones: ").SemiBold();
                            t.Span(c.Notas!).FontColor(Colors.Grey.Darken3);
                        });
                    }
                });

                // ════════ FOOTER — Firma del cliente ════════
                page.Footer().Column(col =>
                {
                    col.Item().PaddingTop(15).Row(row =>
                    {
                        row.RelativeItem().Column(c2 =>
                        {
                            c2.Item().PaddingTop(20).BorderTop(0.8f).BorderColor(Colors.Black).PaddingTop(3).AlignCenter()
                                .Text("Firma del cliente").FontSize(9);
                            c2.Item().AlignCenter().Text("Aclaración / DNI").FontSize(8).FontColor(Colors.Grey.Darken1);
                        });
                        row.ConstantItem(30);
                        row.RelativeItem().Column(c2 =>
                        {
                            c2.Item().PaddingTop(20).BorderTop(0.8f).BorderColor(Colors.Black).PaddingTop(3).AlignCenter()
                                .Text(cfg.NegocioRazonSocial ?? cfg.NegocioNombre ?? "Empresa").FontSize(9);
                            c2.Item().AlignCenter().Text("Por la empresa").FontSize(8).FontColor(Colors.Grey.Darken1);
                        });
                    });
                    col.Item().PaddingTop(10).AlignCenter().Text($"Documento generado el {DateTime.Now:dd/MM/yyyy HH:mm}")
                        .FontSize(7).FontColor(Colors.Grey.Medium);
                });
            });
        });

        return pdf.GeneratePdf();
    }

    /// <summary>Numero de comprobante derivado del Id + año de creacion. Estable y sin migracion DB.</summary>
    public static string GetNumeroComprobante(CafeComodato c)
    {
        var prefijo = c.Modalidad == "FINANCIADA" ? "FIN" : "COM";
        return $"{prefijo}-{c.CreatedAt.Year:0000}-{c.Id:0000}";
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
