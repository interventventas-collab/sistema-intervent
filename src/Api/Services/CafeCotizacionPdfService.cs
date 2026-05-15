using Api.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Globalization;

namespace Api.Services;

/// <summary>
/// Generador puro del PDF de cotizaciones / proformas / remitos del módulo Café.
/// NO toca ARCA, NO toca el ArcaInvoicePdfService. Solo recibe la venta + los
/// datos del negocio (CafeSetting) y devuelve byte[] del PDF.
/// </summary>
public class CafeCotizacionPdfService
{
    private readonly FileStorageService _files;
    private readonly ILogger<CafeCotizacionPdfService> _logger;
    private static readonly CultureInfo Es = new("es-AR");

    static CafeCotizacionPdfService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public CafeCotizacionPdfService(FileStorageService files, ILogger<CafeCotizacionPdfService> logger)
    {
        _files = files;
        _logger = logger;
    }

    public byte[] GenerarPdfBytes(CafeVenta v, CafeSetting? cfg)
    {
        cfg ??= new CafeSetting();
        var tipoLetra = TipoComprobanteCorto(v.TipoComprobante);
        var tipoNombre = TipoComprobanteLargo(v.TipoComprobante);
        var esProforma = v.TipoComprobante == "PRO";

        decimal netoSinIva = v.Total;
        decimal iva21 = Math.Round(netoSinIva * 0.21m, 2, MidpointRounding.AwayFromZero);
        decimal totalConIva = netoSinIva + iva21;

        // Carga del logo con fallback. Intenta:
        //   1) NegocioLogoUrl del CafeSetting (formato: /api/files/download?path=...)
        //   2) Si no está, "Logos Empresa/{CUIT}/logo.<ext>" del FileStorage (el logo subido en la ficha
        //      ARCA Emisor) — así no hay que cargar el mismo logo en dos lugares.
        var logoBytes = TryLoadLogoBytes(cfg.NegocioLogoUrl)
                        ?? TryLoadLogoFallback(cfg.NegocioCuit);
        var diasActivos = (v.WeekDays ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().ToUpperInvariant()).ToHashSet();
        var allDays = new[] { "LUN", "MAR", "MIE", "JUE", "VIE", "SAB", "DOM" };

        var pdf = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(20);
                page.DefaultTextStyle(t => t.FontSize(9));

                // ─── Marca de agua diagonal "PROFORMA" para evitar que se confunda con factura ───
                // Solo aplica a tipo PRO. El tipo X tiene su propia leyenda y los tipos FA/FB/FC
                // van por otro PDF (ArcaInvoicePdfService) y no llegan acá.
                if (esProforma)
                {
                    page.Background().AlignCenter().AlignMiddle().Element(e =>
                        e.Rotate(-30).Text("PROFORMA").FontSize(120).Bold().FontColor(Colors.Grey.Lighten2));
                }

                page.Header().Column(col =>
                {
                    if (v.Estado == "anulado")
                    {
                        col.Item().Background(Colors.Red.Lighten4).Border(2).BorderColor(Colors.Red.Darken2)
                            .Padding(6).AlignCenter().Text("⚠ COMPROBANTE ANULADO").FontSize(11).Bold().FontColor(Colors.Red.Darken3);
                    }

                    // Banner claro de "DOCUMENTO SIN VALOR FISCAL" para Proforma (y para tipo X).
                    // Es lo primero que ve el cliente — ningún malentendido posible.
                    if (esProforma || v.TipoComprobante == "X")
                    {
                        var bannerText = esProforma
                            ? "📋 DOCUMENTO SIN VALOR FISCAL — PROFORMA NO APTA PARA ARCA"
                            : "📋 DOCUMENTO INTERNO — NO VÁLIDO COMO FACTURA";
                        col.Item().PaddingBottom(4).Background(Colors.Orange.Lighten4)
                            .Border(1).BorderColor(Colors.Orange.Medium)
                            .Padding(5).AlignCenter()
                            .Text(bannerText).FontSize(9).Bold().FontColor(Colors.Orange.Darken3);
                    }

                    // ─── Bloque emisor + tipo de comprobante ───
                    col.Item().Border(1).BorderColor(Colors.Grey.Lighten1).Padding(8).Row(row =>
                    {
                        row.RelativeItem(2).Row(r =>
                        {
                            if (logoBytes is not null)
                                r.ConstantItem(120).Height(70).AlignLeft().AlignMiddle().Image(logoBytes).FitArea();

                            r.RelativeItem().PaddingLeft(logoBytes is null ? 0 : 8).Column(c =>
                            {
                                var razon = string.IsNullOrWhiteSpace(cfg.NegocioRazonSocial)
                                    ? (cfg.NegocioNombre ?? "Café e insumos")
                                    : cfg.NegocioRazonSocial!;
                                c.Item().Text(razon).FontSize(13).Bold();
                                if (!string.IsNullOrWhiteSpace(cfg.NegocioRazonSocial) && !string.IsNullOrEmpty(cfg.NegocioNombre))
                                    c.Item().Text(cfg.NegocioNombre!).FontSize(8).Italic().FontColor(Colors.Grey.Darken1);

                                if (!string.IsNullOrEmpty(cfg.NegocioDireccion))
                                {
                                    var locCp = string.Join(" - ", new[] { cfg.NegocioLocalidad, cfg.NegocioCp }
                                        .Where(x => !string.IsNullOrWhiteSpace(x)));
                                    var line = cfg.NegocioDireccion + (string.IsNullOrEmpty(locCp) ? "" : " · " + locCp);
                                    c.Item().Text(line).FontSize(8);
                                }
                                if (!string.IsNullOrEmpty(cfg.NegocioCuit) || !string.IsNullOrEmpty(cfg.NegocioCondicionIva))
                                {
                                    var parts = new List<string>();
                                    if (!string.IsNullOrEmpty(cfg.NegocioCuit)) parts.Add($"CUIT: {cfg.NegocioCuit}");
                                    if (!string.IsNullOrEmpty(cfg.NegocioCondicionIva)) parts.Add($"Cond. IVA: {CondicionIvaLabel(cfg.NegocioCondicionIva!)}");
                                    c.Item().Text(string.Join(" · ", parts)).FontSize(8);
                                }
                                if (!string.IsNullOrEmpty(cfg.NegocioIngresosBrutos) || cfg.NegocioInicioActividad.HasValue)
                                {
                                    var parts = new List<string>();
                                    if (!string.IsNullOrEmpty(cfg.NegocioIngresosBrutos)) parts.Add($"IIBB: {cfg.NegocioIngresosBrutos}");
                                    if (cfg.NegocioInicioActividad.HasValue) parts.Add($"Inicio actividad: {cfg.NegocioInicioActividad.Value:dd/MM/yyyy}");
                                    c.Item().Text(string.Join(" · ", parts)).FontSize(8);
                                }
                                if (!string.IsNullOrEmpty(cfg.NegocioTelefono) || !string.IsNullOrEmpty(cfg.NegocioEmail) || !string.IsNullOrEmpty(cfg.NegocioWeb))
                                {
                                    var parts = new List<string>();
                                    if (!string.IsNullOrEmpty(cfg.NegocioTelefono)) parts.Add($"Tel: {cfg.NegocioTelefono}");
                                    if (!string.IsNullOrEmpty(cfg.NegocioEmail)) parts.Add(cfg.NegocioEmail);
                                    if (!string.IsNullOrEmpty(cfg.NegocioWeb)) parts.Add(cfg.NegocioWeb);
                                    c.Item().Text(string.Join(" · ", parts)).FontSize(8).FontColor(Colors.Grey.Darken1);
                                }
                            });
                        });

                        row.RelativeItem().Column(c =>
                        {
                            c.Item().AlignRight().Row(r =>
                            {
                                r.AutoItem().Border(2).BorderColor(Colors.Grey.Medium).Padding(4)
                                    .AlignCenter().AlignMiddle().Text(tipoLetra).FontSize(18).Bold();
                                r.AutoItem().PaddingLeft(6).AlignMiddle()
                                    .Text(tipoNombre).FontSize(11).Bold().FontColor(Colors.Blue.Darken2);
                            });
                            c.Item().PaddingTop(2).AlignRight().Text($"N° {v.Numero}").FontSize(10).Bold().FontFamily("Courier");
                            c.Item().AlignRight().Text($"Fecha: {v.Fecha:dd/MM/yyyy}").FontSize(9);
                            if (v.TipoComprobante == "X")
                                c.Item().PaddingTop(2).AlignRight().Text("Documento no válido como factura")
                                    .FontSize(7).Italic().FontColor(Colors.Grey.Darken1);
                            if (v.IsPaid && v.Estado != "anulado")
                            {
                                c.Item().PaddingTop(3).AlignRight().Row(rr =>
                                {
                                    rr.AutoItem().Background(Colors.Red.Lighten4).Border(1).BorderColor(Colors.Red.Medium)
                                        .Padding(3).Text("PAGADO").FontSize(11).Bold().FontColor(Colors.Red.Darken2);
                                });
                            }
                        });
                    });

                    // ─── Cliente (receptor) ───
                    col.Item().BorderLeft(1).BorderRight(1).BorderBottom(1).BorderColor(Colors.Grey.Lighten1).Padding(6).Row(row =>
                    {
                        row.RelativeItem(2).Column(c =>
                        {
                            var razonCli = v.ClienteRazonSocialSnapshot;
                            var nombreCli = v.ClienteNombreSnapshot ?? "Consumidor final";
                            if (!string.IsNullOrWhiteSpace(razonCli))
                            {
                                c.Item().Text(razonCli).FontSize(10).Bold();
                                c.Item().Text(nombreCli).FontSize(8).Italic().FontColor(Colors.Grey.Darken1);
                            }
                            else
                            {
                                c.Item().Text(nombreCli).FontSize(10).Bold();
                            }
                            var lineas = new List<string>();
                            if (!string.IsNullOrWhiteSpace(v.ClienteCuitSnapshot)) lineas.Add("CUIT/DNI: " + v.ClienteCuitSnapshot);
                            if (!string.IsNullOrWhiteSpace(v.ClienteDireccionSnapshot)) lineas.Add(v.ClienteDireccionSnapshot!);
                            var locCli = string.Join(" - ",
                                new[] { v.ClienteLocalidadSnapshot, v.ClienteCiudadSnapshot, v.ClienteCpSnapshot }
                                    .Where(x => !string.IsNullOrWhiteSpace(x)));
                            if (!string.IsNullOrEmpty(locCli)) lineas.Add(locCli);
                            foreach (var l in lineas) c.Item().Text(l).FontSize(8).FontColor(Colors.Grey.Darken2);
                            if (!string.IsNullOrWhiteSpace(v.ClienteTelefonoSnapshot))
                                c.Item().Text($"Tel: {v.ClienteTelefonoSnapshot}").FontSize(8);
                        });

                        row.RelativeItem().Column(c =>
                        {
                            var esBar = string.Equals(v.ClienteTipoSnapshot, "BAR", StringComparison.OrdinalIgnoreCase);
                            var tagBg = esBar ? Colors.Blue.Lighten4 : Colors.Green.Lighten4;
                            var tagFg = esBar ? Colors.Blue.Darken3 : Colors.Green.Darken3;
                            var tagText = esBar ? "🍺 BAR" : "🏢 Comercial";
                            c.Item().AlignRight().Row(rr =>
                            {
                                rr.AutoItem().Background(tagBg).Padding(3).Text(tagText)
                                    .FontSize(8).Bold().FontColor(tagFg);
                            });
                            c.Item().PaddingTop(2).AlignRight()
                                .Text($"Cond. IVA: {CondicionIvaLabel(v.CondicionIva)}").FontSize(8);
                        });
                    });
                });

                // ───── CONTENIDO ─────
                page.Content().PaddingTop(8).Column(col =>
                {
                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(45);
                            c.RelativeColumn(5);
                            c.ConstantColumn(60);
                            c.ConstantColumn(80);
                            c.ConstantColumn(50);
                            c.ConstantColumn(95);
                        });
                        table.Header(h =>
                        {
                            h.Cell().Background(Colors.Grey.Lighten3).Border(0.3f).BorderColor(Colors.Grey.Lighten1).Padding(3).AlignCenter().Text("Cant.").SemiBold().FontSize(8);
                            h.Cell().Background(Colors.Grey.Lighten3).Border(0.3f).BorderColor(Colors.Grey.Lighten1).Padding(3).Text("Producto").SemiBold().FontSize(8);
                            h.Cell().Background(Colors.Grey.Lighten3).Border(0.3f).BorderColor(Colors.Grey.Lighten1).Padding(3).AlignCenter().Text("Formato").SemiBold().FontSize(8);
                            h.Cell().Background(Colors.Grey.Lighten3).Border(0.3f).BorderColor(Colors.Grey.Lighten1).Padding(3).AlignRight().Text("P. Unitario").SemiBold().FontSize(8);
                            h.Cell().Background(Colors.Grey.Lighten3).Border(0.3f).BorderColor(Colors.Grey.Lighten1).Padding(3).AlignRight().Text("Desc.").SemiBold().FontSize(8);
                            h.Cell().Background(Colors.Grey.Lighten3).Border(0.3f).BorderColor(Colors.Grey.Lighten1).Padding(3).AlignRight().Text("Subtotal").SemiBold().FontSize(8);
                        });
                        foreach (var i in v.Items)
                        {
                            // Si es BULTO, mostramos cantidad real (cant×UxB), formato "Bulto × N",
                            // y precio efectivo (precio/UxB). Asi el deposito ve unidades totales.
                            int uxbItem = (i.Formato == "BULTO" && i.ProductoNav?.UxB is int u && u > 1) ? u : 1;
                            int cantPrint = i.Cantidad * uxbItem;
                            string fmtPrint = uxbItem > 1 ? $"Bulto × {uxbItem}" : FormatoLabel(i.Formato);
                            decimal precioPrint = uxbItem > 1
                                ? Math.Round(i.PrecioUnitario / uxbItem, 2, MidpointRounding.AwayFromZero)
                                : i.PrecioUnitario;
                            // Cant
                            table.Cell().Border(0.3f).BorderColor(Colors.Grey.Lighten1).Padding(3).AlignCenter().Text(cantPrint.ToString()).SemiBold();
                            // Producto
                            table.Cell().Border(0.3f).BorderColor(Colors.Grey.Lighten1).Padding(3).Text(t =>
                            {
                                if (!string.IsNullOrEmpty(i.ProductoNav?.Sku))
                                    t.Span($"{i.ProductoNav.Sku}  ").Bold().FontColor(Colors.Blue.Darken3).FontSize(8);
                                t.Span(i.ProductoNombreSnapshot).SemiBold();
                                if (i.EsDoyPack) t.Span("  d.p.").Bold().FontColor(Colors.Blue.Darken3);
                                else if (i.EsEnvasePlateado) t.Span("  env. plat.").Bold().FontColor(Colors.Grey.Darken2);
                                if (!string.IsNullOrEmpty(i.Molienda)) t.Span($"  — {i.Molienda}").FontColor(Colors.Grey.Darken1).FontSize(8);
                            });
                            // Formato — italic gris para que NO se confunda con la cantidad
                            table.Cell().Border(0.3f).BorderColor(Colors.Grey.Lighten1).Padding(3).AlignCenter()
                                .Text(fmtPrint).Italic().FontColor(Colors.Grey.Darken1).FontFamily("Times New Roman");
                            // P. Unitario (efectivo si es bulto)
                            table.Cell().Border(0.3f).BorderColor(Colors.Grey.Lighten1).Padding(3).AlignRight().Text(t =>
                            {
                                var pu = "$ " + precioPrint.ToString("N2", Es);
                                if (i.DescuentoPct > 0)
                                    t.Span(pu).Strikethrough().FontColor(Colors.Grey.Medium);
                                else
                                    t.Span(pu);
                            });
                            // Desc.
                            table.Cell().Border(0.3f).BorderColor(Colors.Grey.Lighten1).Padding(3).AlignRight().Text(t =>
                            {
                                if (i.DescuentoPct > 0)
                                    t.Span($"-{i.DescuentoPct.ToString("0.##", Es)}%").FontColor(Colors.Red.Darken1).Bold();
                                else
                                    t.Span("—").FontColor(Colors.Grey.Medium);
                            });
                            // Subtotal
                            table.Cell().Border(0.3f).BorderColor(Colors.Grey.Lighten1).Padding(3).AlignRight()
                                .Text("$ " + i.Subtotal.ToString("N2", Es)).SemiBold();
                        }
                    });

                    // ─── Totales (alineados a la derecha via Row con filler) ───
                    col.Item().PaddingTop(8).Row(row =>
                    {
                        row.RelativeItem();
                        row.ConstantItem(280).Border(1).BorderColor(Colors.Grey.Lighten1).Padding(8).Column(c =>
                        {
                            c.Item().Row(r =>
                            {
                                r.RelativeItem().Text(esProforma ? "Subtotal (neto sin IVA):" : "Subtotal:").FontSize(9);
                                r.AutoItem().Text("$ " + v.Subtotal.ToString("N2", Es)).SemiBold().FontSize(9);
                            });
                            if (v.Descuento > 0)
                            {
                                c.Item().Row(r =>
                                {
                                    r.RelativeItem().Text("Descuento:").FontSize(9).FontColor(Colors.Red.Darken1);
                                    r.AutoItem().Text("− $ " + v.Descuento.ToString("N2", Es)).FontColor(Colors.Red.Darken1).FontSize(9);
                                });
                            }
                            if (esProforma)
                            {
                                c.Item().Row(r =>
                                {
                                    r.RelativeItem().Text("Neto gravado (sin IVA):").FontSize(9);
                                    r.AutoItem().Text("$ " + netoSinIva.ToString("N2", Es)).SemiBold().FontSize(9);
                                });
                                c.Item().Row(r =>
                                {
                                    r.RelativeItem().Text("IVA 21% (estimado):").FontSize(9).FontColor(Colors.Blue.Darken2);
                                    r.AutoItem().Text("$ " + iva21.ToString("N2", Es)).FontColor(Colors.Blue.Darken2).FontSize(9);
                                });
                                c.Item().PaddingTop(2).LineHorizontal(1.2f).LineColor(Colors.Grey.Darken3);
                                c.Item().Row(r =>
                                {
                                    r.RelativeItem().Text("TOTAL c/IVA:").FontSize(13).Bold().FontColor(Colors.Green.Darken2);
                                    r.AutoItem().Text("$ " + totalConIva.ToString("N2", Es)).Bold().FontSize(13).FontColor(Colors.Green.Darken2);
                                });
                            }
                            else
                            {
                                c.Item().PaddingTop(2).LineHorizontal(1.2f).LineColor(Colors.Grey.Darken3);
                                c.Item().Row(r =>
                                {
                                    r.RelativeItem().Text("TOTAL:").FontSize(13).Bold().FontColor(Colors.Green.Darken2);
                                    r.AutoItem().Text("$ " + v.Total.ToString("N2", Es)).Bold().FontSize(13).FontColor(Colors.Green.Darken2);
                                });
                            }
                            c.Item().PaddingTop(4).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);
                            // Forma de pago destacada: cuadro gris claro con título arriba + estado a la derecha
                            c.Item().PaddingTop(3).Background(Colors.Grey.Lighten4).Border(0.5f).BorderColor(Colors.Grey.Lighten1)
                                .Padding(6).Row(r =>
                            {
                                r.RelativeItem().Column(cc =>
                                {
                                    cc.Item().Text("FORMA DE PAGO").FontSize(7).Bold().FontColor(Colors.Grey.Darken2).LetterSpacing(0.05f);
                                    cc.Item().Text(CondicionPagoLabel(v.CondicionPago)).FontSize(11).Bold().FontColor(Colors.Black);
                                });
                                if (v.IsPaid)
                                {
                                    r.AutoItem().AlignMiddle().Background(Colors.Green.Lighten4).Border(0.5f).BorderColor(Colors.Green.Lighten1)
                                        .Padding(4).Text("✓ PAGADA").Bold().FontSize(9).FontColor(Colors.Green.Darken3);
                                }
                                else
                                {
                                    r.AutoItem().AlignMiddle().Background(Colors.Yellow.Lighten4).Border(0.5f).BorderColor(Colors.Yellow.Darken1)
                                        .Padding(4).Text("⧗ PENDIENTE").Bold().FontSize(9).FontColor(Colors.Orange.Darken3);
                                }
                            });
                        });
                    });

                    if (esProforma)
                    {
                        col.Item().PaddingTop(4).Background(Colors.Yellow.Lighten4).Border(1).BorderColor(Colors.Yellow.Darken1)
                            .Padding(5).Text(t =>
                            {
                                t.Span("⚠ Proforma — preview de cómo quedaría con IVA cuando emitas factura por ARCA. ").Bold().FontColor("#92400e").FontSize(8);
                                t.Span("Para esta vista se aplica 21% al neto. Cuando se emita la factura real, los productos con IVA 10,5% (alimentos) se discriminarán por separado.").FontColor("#92400e").FontSize(8);
                            });
                    }
                });

                // ───── FOOTER (fijo al pie de la página) ─────
                // Los bloques de Entrega / Observaciones / Días de visita van ACÁ
                // para que queden siempre pegados al pie, no apretados contra los totales.
                page.Footer().Column(fc =>
                {
                    if (!string.IsNullOrWhiteSpace(v.ClienteDomicilioEntregaSnapshot))
                    {
                        fc.Item().PaddingTop(5).Border(1).BorderColor(Colors.Grey.Lighten1)
                            .Background(Colors.Grey.Lighten4).Padding(5).Text(t =>
                            {
                                t.Span("🚚 Entrega en: ").SemiBold();
                                t.Span(v.ClienteDomicilioEntregaSnapshot!);
                            });
                    }

                    if (!string.IsNullOrEmpty(v.ClienteComentariosComprobante))
                    {
                        fc.Item().PaddingTop(5).BorderLeft(3).BorderColor(Colors.Orange.Darken1)
                            .Background(Colors.Orange.Lighten4).Padding(5).Text(t =>
                            {
                                t.Span("📝 ").SemiBold();
                                t.Span(v.ClienteComentariosComprobante!).FontColor("#78350f");
                            });
                    }

                    if (!string.IsNullOrEmpty(v.Observaciones))
                    {
                        fc.Item().PaddingTop(5).Border(1).BorderColor(Colors.Grey.Lighten1).Padding(5).Text(t =>
                        {
                            t.Span("Observaciones: ").SemiBold();
                            t.Span(v.Observaciones!);
                        });
                    }

                    // Días de visita / reparto
                    fc.Item().PaddingTop(5).Border(1).BorderColor(Colors.Grey.Lighten1).Padding(5).Row(row =>
                    {
                        row.AutoItem().AlignMiddle().PaddingRight(6).Text("Días de visita / reparto:")
                            .FontSize(8).FontColor(Colors.Grey.Darken1);
                        foreach (var d in allDays)
                        {
                            var on = diasActivos.Contains(d);
                            var bg = on ? Colors.Grey.Darken4 : Colors.White;
                            var fg = on ? Colors.White : Colors.Grey.Darken2;
                            var bd = on ? Colors.Grey.Darken4 : Colors.Grey.Lighten1;
                            row.ConstantItem(36).PaddingHorizontal(2).Border(1).BorderColor(bd)
                                .Background(bg).AlignCenter().AlignMiddle().Padding(2)
                                .Text(d).Bold().FontSize(8).FontColor(fg);
                        }
                    });

                    // Línea final con "Gracias por tu compra"
                    fc.Item().PaddingTop(8).BorderTop(1).BorderColor(Colors.Grey.Lighten2).PaddingTop(4).AlignCenter().Text(t =>
                    {
                        t.Span("Gracias por tu compra ☕").FontSize(8).FontColor(Colors.Grey.Darken1);
                        if (!string.IsNullOrEmpty(cfg.NegocioWhatsappNumero))
                        {
                            t.Span("   ·   ").FontSize(8).FontColor(Colors.Grey.Darken1);
                            t.Span("Contactanos por WhatsApp").FontSize(8).FontColor("#25D366").Bold();
                        }
                    });
                });
            });
        }).GeneratePdf();

        return pdf;
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
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No se pudo cargar el logo del negocio Café (NegocioLogoUrl)");
            return null;
        }
    }

    /// <summary>
    /// Fallback: si no se pudo cargar el logo del CafeSetting, intentamos cargar el de la
    /// ficha de empresa ARCA Emisor (Logos Empresa/{CUIT}/logo.<ext>). Así con un solo logo
    /// cargado en la ficha alcanza para los dos PDFs (Café y ARCA).
    /// </summary>
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
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No se pudo cargar el logo fallback de ficha de empresa");
        }
        return null;
    }

    private static string TipoComprobanteCorto(string t) => t switch
    {
        "FA" => "A", "FB" => "B", "FC" => "C", "PRO" => "PRO", "X" => "X", _ => "X"
    };

    private static string TipoComprobanteLargo(string t) => t switch
    {
        "FA" => "Factura A",
        "FB" => "Factura B",
        "FC" => "Factura C",
        "PRO" => "Proforma",
        "X" => "Comprobante interno",
        _ => "Comprobante"
    };

    private static string CondicionIvaLabel(string c) => c switch
    {
        "RI" => "Responsable Inscripto",
        "MO" => "Monotributo",
        "EX" => "Exento",
        "CF" => "Consumidor Final",
        _ => c
    };

    private static string CondicionPagoLabel(string c) => c switch
    {
        "EFECTIVO" => "Efectivo",
        "TRANSFERENCIA" => "Transferencia",
        "DEBITO" => "Débito",
        "CREDITO" => "Crédito",
        "CTA_CORRIENTE" => "Cta. Corriente",
        "CHEQUE" => "Cheque",
        "V_PRIVADO" => "Privado",
        _ => c
    };

    private static string FormatoLabel(string f) => f switch
    {
        "1KG" => "1 kg",
        "MEDIO" => "1/2 kg",
        "CUARTO" => "1/4 kg",
        "UNIT" => "Unidad",
        "BULTO" => "Bulto",
        _ => f
    };
}
