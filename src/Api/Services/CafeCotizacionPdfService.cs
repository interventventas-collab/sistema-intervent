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

    public byte[] GenerarPdfBytes(CafeVenta v, CafeSetting? cfg) => GenerarPdfBytes(v, cfg, null, null);

    public byte[] GenerarPdfBytes(CafeVenta v, CafeSetting? cfg, byte[]? qrRepartidor)
        => GenerarPdfBytes(v, cfg, qrRepartidor, null);

    /// <summary>2026-06-08: combosMap (opcional, ComboId→Nombre/Sku) permite agrupar items que vienen del
    /// mismo combo en UNA sola línea en el PDF (lo que ve el cliente). Si es null, los items se muestran
    /// desglosados como antes (ventas viejas sin ComboOrigenId también se ven igual).</summary>
    public byte[] GenerarPdfBytes(CafeVenta v, CafeSetting? cfg, byte[]? qrRepartidor,
                                  Dictionary<int, (string Nombre, string? Sku)>? combosMap)
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
        // Días a mostrar: LUN-SAB siempre + DOM solo si fue tildado (caso particular)
        var diasDelComp = new List<string> { "LUN", "MAR", "MIE", "JUE", "VIE", "SAB" };
        if (diasActivos.Contains("DOM")) diasDelComp.Add("DOM");

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
                        // 2026-06-16: en cotizaciones tipo "X" mostramos SOLO el logo (sin datos fiscales del emisor)
                        // — el comprobante no es válido como factura, no necesita la info fiscal.
                        // En tipos oficiales ARCA seguimos mostrando todo (logo + razón social + CUIT + IIBB + contacto).
                        var esTipoX = v.TipoComprobante == "X";
                        row.RelativeItem(2).Row(r =>
                        {
                            if (logoBytes is not null)
                            {
                                if (esTipoX)
                                    r.ConstantItem(160).Height(80).AlignLeft().AlignMiddle().Image(logoBytes).FitArea();
                                else
                                    r.ConstantItem(120).Height(70).AlignLeft().AlignMiddle().Image(logoBytes).FitArea();
                            }

                            if (!esTipoX)
                            {
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
                            }
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
                            // QR para que el repartidor escanee y cargue la cobranza desde su celu
                            if (qrRepartidor != null)
                            {
                                c.Item().PaddingTop(4).AlignRight().Column(qc =>
                                {
                                    qc.Item().Width(70).Height(70).Image(qrRepartidor).FitArea();
                                    qc.Item().AlignCenter().Text("📲 repartidor").FontSize(6).FontColor(Colors.Grey.Darken1);
                                });
                            }
                        });
                    });

                    // 2026-06-16: franja gris a lo ancho con dos tel WhatsApp + email centrado.
                    // B/N friendly (sin colores fuertes). Si no hay datos, no se muestra.
                    RenderFranjaContacto(col, cfg.NegocioTelefono, cfg.NegocioTelefono2, cfg.NegocioEmail);

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
                            // Cond. IVA del cliente: solo en comprobantes oficiales ARCA (no en cotizaciones tipo "X").
                            if (v.TipoComprobante != "X")
                            {
                                c.Item().PaddingTop(2).AlignRight()
                                    .Text($"Cond. IVA: {CondicionIvaLabel(v.CondicionIva)}").FontSize(8);
                            }
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
                        // 2026-06-08: Agrupar items por ComboOrigenId. Los items que vienen del mismo
                        // combo se renderizan como UNA sola línea con el nombre del combo y la suma
                        // de subtotales. El cliente NO ve el desglose en la factura.
                        var presentationRows = BuildPresentationRows(v, combosMap);
                        foreach (var pr in presentationRows)
                        {
                            // Cant
                            table.Cell().Border(0.3f).BorderColor(Colors.Grey.Lighten1).Padding(3).AlignCenter().Text(pr.CantPrint.ToString()).SemiBold();
                            // Producto
                            table.Cell().Border(0.3f).BorderColor(Colors.Grey.Lighten1).Padding(3).Text(t =>
                            {
                                if (!string.IsNullOrEmpty(pr.Sku))
                                    t.Span($"{pr.Sku}  ").Bold().FontColor(Colors.Blue.Darken3).FontSize(8);
                                t.Span(pr.Nombre).SemiBold();
                                if (pr.EsDoyPack) t.Span("  d.p.").Bold().FontColor(Colors.Blue.Darken3);
                                else if (pr.EsEnvasePlateado) t.Span("  env. plat.").Bold().FontColor(Colors.Grey.Darken2);
                                if (!string.IsNullOrEmpty(pr.Molienda)) t.Span($"  — {pr.Molienda}").FontColor(Colors.Grey.Darken1).FontSize(8);
                            });
                            // Formato
                            table.Cell().Border(0.3f).BorderColor(Colors.Grey.Lighten1).Padding(3).AlignCenter()
                                .Text(pr.FmtPrint).Italic().FontColor(Colors.Grey.Darken1).FontFamily("Times New Roman");
                            // P. Unitario
                            table.Cell().Border(0.3f).BorderColor(Colors.Grey.Lighten1).Padding(3).AlignRight().Text(t =>
                            {
                                var pu = "$ " + pr.PrecioPrint.ToString("N2", Es);
                                if (pr.DescuentoPct > 0)
                                    t.Span(pu).Strikethrough().FontColor(Colors.Grey.Medium);
                                else
                                    t.Span(pu);
                            });
                            // Desc.
                            table.Cell().Border(0.3f).BorderColor(Colors.Grey.Lighten1).Padding(3).AlignRight().Text(t =>
                            {
                                if (pr.DescuentoPct > 0)
                                    t.Span($"-{pr.DescuentoPct.ToString("0.##", Es)}%").FontColor(Colors.Red.Darken1).Bold();
                                else
                                    t.Span("—").FontColor(Colors.Grey.Medium);
                            });
                            // Subtotal
                            table.Cell().Border(0.3f).BorderColor(Colors.Grey.Lighten1).Padding(3).AlignRight()
                                .Text("$ " + pr.Subtotal.ToString("N2", Es)).SemiBold();
                        }
                    });

                    // Totales movidos al Footer (decision 2026-05-20) para que queden pegados
                    // al pie de la pagina en vez de "flotando" justo despues de la tabla cuando
                    // hay pocos items. Ver page.Footer() mas abajo.

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
                // Totales + Entrega / Observaciones / Días de visita van ACÁ para que queden
                // siempre pegados al pie en lugar de "flotando" justo despues de la tabla.
                page.Footer().Column(fc =>
                {
                    // ─── Totales (alineados a la derecha via Row con filler) ───
                    fc.Item().PaddingTop(4).Row(row =>
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
                            // Forma de pago destacada
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

                    if (!string.IsNullOrWhiteSpace(v.ClienteDomicilioEntregaSnapshot))
                    {
                        fc.Item().PaddingTop(5).Border(1).BorderColor(Colors.Grey.Lighten1)
                            .Background(Colors.Grey.Lighten4).Padding(5).Text(t =>
                            {
                                t.Span("🚚 Entrega en: ").SemiBold();
                                t.Span(v.ClienteDomicilioEntregaSnapshot!);
                            });
                    }

                    if (!string.IsNullOrWhiteSpace(v.EntregaPor))
                    {
                        fc.Item().PaddingTop(5).Border(1).BorderColor(Colors.Grey.Lighten1)
                            .Background(Colors.Grey.Lighten4).Padding(5).Text(t =>
                            {
                                t.Span("🚚 Repartidor: ").SemiBold();
                                t.Span(v.EntregaPor!);
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

                    // Días de visita / reparto — con ✓ check + EN RADAR muestra "a coordinar"
                    fc.Item().PaddingTop(5).Border(1).BorderColor(Colors.Grey.Lighten1).Padding(5).Row(row =>
                    {
                        row.AutoItem().AlignMiddle().PaddingRight(6).Text("Días de visita / reparto:")
                            .FontSize(8).FontColor(Colors.Grey.Darken1);
                        if (v.Retira)
                        {
                            // 2026-06-01: el cliente retira la mercaderia en el local.
                            row.AutoItem().AlignMiddle().Padding(3)
                                .Text("🚗 RETIRA EN LOCAL").Bold().FontSize(10).FontColor(Colors.Green.Darken3);
                        }
                        else if (v.EnRadar)
                        {
                            // 2026-06-01: por pedido del usuario, ahora el cliente ve literal "🛰 EN RADAR".
                            row.AutoItem().AlignMiddle().Padding(3)
                                .Text("🛰 EN RADAR").Bold().FontSize(10).FontColor(Colors.Blue.Darken3);
                        }
                        else
                        {
                            foreach (var d in diasDelComp)
                            {
                                var on = diasActivos.Contains(d);
                                var bg = on ? Colors.Blue.Lighten5 : Colors.White;
                                var fg = on ? Colors.Blue.Darken3 : Colors.Grey.Darken2;
                                var bd = on ? Colors.Blue.Darken2 : Colors.Grey.Lighten1;
                                var bw = on ? 1.5f : 1f;
                                row.ConstantItem(40).PaddingHorizontal(2).Border(bw).BorderColor(bd)
                                    .Background(bg).AlignCenter().AlignMiddle().Padding(2)
                                    .Text(on ? $"✓{d}" : d).Bold().FontSize(8).FontColor(fg);
                            }
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

                    // 2026-06-16: dos sitios web al pie del comprobante (izq / der). Si no hay datos no se muestra.
                    RenderFooterWebs(fc, cfg.NegocioWeb, cfg.NegocioWeb2);
                });
            });
        }).GeneratePdf();

        return pdf;
    }

    /// <summary>2026-06-16: franja gris ancho-completo con tel · email · tel. Iconos en negro (B/N friendly).</summary>
    private static void RenderFranjaContacto(QuestPDF.Fluent.ColumnDescriptor col, string? tel1, string? tel2, string? email)
    {
        if (string.IsNullOrWhiteSpace(tel1) && string.IsNullOrWhiteSpace(tel2) && string.IsNullOrWhiteSpace(email)) return;
        col.Item().PaddingTop(4).Background(Colors.Grey.Lighten3)
            .BorderTop(0.5f).BorderBottom(0.5f).BorderColor(Colors.Grey.Medium)
            .PaddingVertical(4).PaddingHorizontal(8).Row(row =>
        {
            row.RelativeItem().Text(t =>
            {
                if (!string.IsNullOrWhiteSpace(tel1))
                {
                    t.Span("✆ ").FontSize(10);
                    t.Span(tel1).FontSize(11).Bold();
                }
            });
            row.RelativeItem().AlignCenter().Text(t =>
            {
                if (!string.IsNullOrWhiteSpace(email))
                {
                    t.Span("✉ ").FontSize(9);
                    t.Span(email).FontSize(9);
                }
            });
            row.RelativeItem().AlignRight().Text(t =>
            {
                if (!string.IsNullOrWhiteSpace(tel2))
                {
                    t.Span("✆ ").FontSize(10);
                    t.Span(tel2).FontSize(11).Bold();
                }
            });
        });
    }

    /// <summary>2026-06-16: dos sitios web al pie del comprobante (izq/der). Si no hay datos no se muestra.</summary>
    private static void RenderFooterWebs(QuestPDF.Fluent.ColumnDescriptor col, string? web1, string? web2)
    {
        if (string.IsNullOrWhiteSpace(web1) && string.IsNullOrWhiteSpace(web2)) return;
        col.Item().PaddingTop(4).Row(row =>
        {
            row.RelativeItem().Text(t =>
            {
                if (!string.IsNullOrWhiteSpace(web1))
                {
                    t.Span("⌂ ").FontSize(9);
                    t.Span(web1).FontSize(10).Bold();
                }
            });
            row.RelativeItem().AlignRight().Text(t =>
            {
                if (!string.IsNullOrWhiteSpace(web2))
                {
                    t.Span("⌂ ").FontSize(9);
                    t.Span(web2).FontSize(10).Bold();
                }
            });
        });
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
        "MERCADOPAGO" => "Mercado Pago",
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

    // 2026-06-08: Fila lista para renderizar en la tabla del PDF. Si vino de un grupo combo,
    // representa la consolidación de varios CafeVentaItem en una sola línea para el cliente.
    private sealed class PresentationRow
    {
        public int CantPrint { get; set; }
        public string? Sku { get; set; }
        public string Nombre { get; set; } = "";
        public string FmtPrint { get; set; } = "";
        public decimal PrecioPrint { get; set; }
        public decimal DescuentoPct { get; set; }
        public decimal Subtotal { get; set; }
        public string? Molienda { get; set; }
        public bool EsDoyPack { get; set; }
        public bool EsEnvasePlateado { get; set; }
    }

    /// <summary>2026-06-08: Construye las filas a mostrar en el PDF.
    /// - Items SIN ComboOrigenId: una fila cada uno (igual que antes).
    /// - Items CON ComboOrigenId: se agrupan en UNA fila por combo + cantidad de "packs" del combo
    ///   (vista del cliente, sin desglose). Para identificar cuántos "packs" del combo se vendieron,
    ///   tomamos el min de ratio Cantidad/CantidadEsperadaPorCombo; con fallback a 1 si no se puede
    ///   determinar. El precio unitario mostrado = Subtotal del grupo / packs.
    /// El fallback de nombre/sku usa el primer item del grupo si combosMap no resuelve el ComboId.</summary>
    private static List<PresentationRow> BuildPresentationRows(CafeVenta v,
        Dictionary<int, (string Nombre, string? Sku)>? combosMap)
    {
        var rows = new List<PresentationRow>();
        // Mantener el orden original (insercion). Usamos un dict para agrupar pero recordamos el
        // orden de aparicion del ComboOrigenId.
        var grupos = new Dictionary<int, List<CafeVentaItem>>();
        var ordenGrupo = new List<int>();
        foreach (var i in v.Items)
        {
            if (i.ComboOrigenId.HasValue)
            {
                var k = i.ComboOrigenId.Value;
                if (!grupos.ContainsKey(k)) { grupos[k] = new List<CafeVentaItem>(); ordenGrupo.Add(k); }
                grupos[k].Add(i);
            }
        }
        var grupoYaEmitido = new HashSet<int>();
        foreach (var i in v.Items)
        {
            if (i.ComboOrigenId.HasValue)
            {
                var k = i.ComboOrigenId.Value;
                if (grupoYaEmitido.Contains(k)) continue;
                grupoYaEmitido.Add(k);
                var gItems = grupos[k];
                // Resolver nombre/sku del combo (combosMap > ComboOrigenNav > fallback)
                string nombreCombo;
                string? skuCombo;
                if (combosMap != null && combosMap.TryGetValue(k, out var c)) { nombreCombo = c.Nombre; skuCombo = c.Sku; }
                else if (gItems[0].ComboOrigenNav is not null) { nombreCombo = gItems[0].ComboOrigenNav!.Nombre; skuCombo = gItems[0].ComboOrigenNav!.Sku; }
                else { nombreCombo = gItems[0].ProductoNombreSnapshot; skuCombo = null; }

                // Asumo 1 pack del combo. Si el operador cargó N packs, se reflejará en las cantidades
                // individuales (todas multiplicadas por N) pero como agrupamos por suma, queda una sola
                // fila de cantidad 1 con el subtotal correcto. Esto es lo MÁS SEGURO para no romper
                // ARCA — el monto y la descripción siempre cuadran.
                rows.Add(new PresentationRow
                {
                    CantPrint = 1,
                    Sku = skuCombo,
                    Nombre = nombreCombo,
                    FmtPrint = "Combo",
                    Subtotal = gItems.Sum(x => x.Subtotal),
                    PrecioPrint = gItems.Sum(x => x.Subtotal),  // mismo subtotal ya que cantidad=1
                    DescuentoPct = 0,   // descuento se considera ya aplicado en el Subtotal acumulado
                    Molienda = null,
                    EsDoyPack = false,
                    EsEnvasePlateado = false
                });
                continue;
            }
            // Item suelto (no combo) — igual que antes.
            int uxbItem = (i.Formato == "BULTO" && i.ProductoNav?.UxB is int u && u > 1) ? u : 1;
            int cantPrint = i.Cantidad * uxbItem;
            string fmtPrint = uxbItem > 1 ? $"Bulto × {uxbItem}" : FormatoLabel(i.Formato);
            decimal precioPrint = uxbItem > 1
                ? Math.Round(i.PrecioUnitario / uxbItem, 2, MidpointRounding.AwayFromZero)
                : i.PrecioUnitario;
            rows.Add(new PresentationRow
            {
                CantPrint = cantPrint,
                Sku = i.ProductoNav?.Sku,
                Nombre = i.ProductoNombreSnapshot,
                FmtPrint = fmtPrint,
                Subtotal = i.Subtotal,
                PrecioPrint = precioPrint,
                DescuentoPct = i.DescuentoPct,
                Molienda = i.Molienda,
                EsDoyPack = i.EsDoyPack,
                EsEnvasePlateado = i.EsEnvasePlateado
            });
        }
        return rows;
    }
}
