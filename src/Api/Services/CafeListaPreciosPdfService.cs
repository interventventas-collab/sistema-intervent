using Api.DTOs;
using Api.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Globalization;

namespace Api.Services;

/// <summary>
/// Genera el PDF "Lista de precios" usando QuestPDF — reemplaza el viejo flujo de
/// "imprimir HTML desde el navegador y guardar como PDF" que generaba PDFs no estandar
/// que algunos lectores moviles no abrian. Este PDF es PDF/1.7 puro y se abre en
/// cualquier lector (Android, iOS, Adobe Reader, etc).
///
/// Estructura: header con datos del negocio + bloque "LISTA DE PRECIOS" a la derecha,
/// banner amarillo "VIGENTE DESDE X" si aplica, y por cada marca una tabla con sus
/// productos (cafe en 4 columnas y otros en 3).
///
/// Pedido del usuario 2026-05-20.
/// </summary>
public class CafeListaPreciosPdfService
{
    private readonly FileStorageService _files;
    private static readonly CultureInfo Es = new("es-AR");

    static CafeListaPreciosPdfService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public CafeListaPreciosPdfService(FileStorageService files)
    {
        _files = files;
    }

    public byte[] GenerarPdf(CafeListaPreciosPreviewDto p)
    {
        var logoBytes = TryLoadLogoBytes(p.Negocio.LogoUrl);
        var tituloColor = p.TipoCliente == "BAR" ? "#1d4ed8" : "#15803d";

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(25);
                page.DefaultTextStyle(t => t.FontSize(9));

                // ── HEADER ──
                page.Header().Column(headerCol =>
                {
                    headerCol.Item().PaddingBottom(8).BorderBottom(2).BorderColor(tituloColor).Row(row =>
                    {
                        row.RelativeItem(2).Row(r =>
                        {
                            if (logoBytes is not null)
                                r.ConstantItem(110).Height(65).AlignLeft().AlignMiddle().Image(logoBytes).FitArea();
                            r.RelativeItem().PaddingLeft(logoBytes is null ? 0 : 10).Column(c =>
                            {
                                c.Item().Text(p.Negocio.Nombre ?? "Empresa").FontSize(15).Bold();
                                if (!string.IsNullOrEmpty(p.Negocio.Direccion))
                                    c.Item().Text("📍 " + p.Negocio.Direccion!).FontSize(8).FontColor(Colors.Grey.Darken1);
                                var linea = new List<string>();
                                if (!string.IsNullOrEmpty(p.Negocio.Telefono)) linea.Add("📞 " + p.Negocio.Telefono);
                                if (!string.IsNullOrEmpty(p.Negocio.WhatsappNumero)) linea.Add("📱 " + p.Negocio.WhatsappNumero);
                                if (linea.Count > 0)
                                    c.Item().Text(string.Join("   ", linea)).FontSize(8).FontColor(Colors.Grey.Darken1);
                                if (!string.IsNullOrEmpty(p.Negocio.Email))
                                    c.Item().Text("✉ " + p.Negocio.Email!).FontSize(8).FontColor(Colors.Grey.Darken1);
                                if (!string.IsNullOrEmpty(p.Negocio.Web))
                                    c.Item().Text("🌐 " + p.Negocio.Web!).FontSize(8).FontColor(Colors.Grey.Darken1);
                                if (!string.IsNullOrEmpty(p.Negocio.Cuit))
                                    c.Item().Text("CUIT: " + p.Negocio.Cuit!).FontSize(7).FontColor(Colors.Grey.Darken2);
                            });
                        });
                        row.RelativeItem().AlignRight().Column(c =>
                        {
                            c.Item().AlignRight().Background(tituloColor).Padding(6).Text("LISTA DE PRECIOS")
                                .FontSize(11).Bold().FontColor(Colors.White);
                            c.Item().PaddingTop(5).AlignRight().Text(t =>
                            {
                                t.Span("Cliente: ").Bold().FontSize(9);
                                t.Span(p.Cliente?.Nombre ?? "(General)").FontSize(9);
                            });
                            c.Item().AlignRight().Text(t =>
                            {
                                t.Span("Tipo: ").Bold().FontSize(9);
                                t.Span(p.TipoCliente == "BAR" ? "🍺 BAR" : "🏢 Comercial").FontSize(9);
                            });
                            c.Item().AlignRight().Text(t =>
                            {
                                t.Span("Fecha: ").Bold().FontSize(9);
                                t.Span(p.Fecha.ToString("dd/MM/yyyy")).FontSize(9);
                            });
                        });
                    });

                    // BANNER "VIGENTE DESDE X" si aplica
                    if (p.VigenteDesde.HasValue)
                    {
                        headerCol.Item().PaddingTop(8).Background(Colors.Amber.Lighten3).Border(1.5f).BorderColor(Colors.Amber.Darken1)
                            .Padding(8).AlignCenter()
                            .Text($"📅 PRECIOS VIGENTES DESDE EL {p.VigenteDesde.Value:dd/MM/yyyy}")
                            .FontSize(11).Bold().FontColor(Colors.Amber.Darken4);
                    }
                });

                // ── CONTENT: por marca ──
                page.Content().PaddingTop(8).Column(col =>
                {
                    if (p.Grupos.Count == 0)
                    {
                        col.Item().PaddingTop(40).AlignCenter().Text("No hay productos para los filtros seleccionados.")
                            .FontSize(11).FontColor(Colors.Grey.Darken1);
                        return;
                    }

                    foreach (var g in p.Grupos)
                    {
                        col.Item().PaddingTop(8).Background(Colors.Amber.Lighten4).Padding(6).Row(r =>
                        {
                            r.RelativeItem().Text(t =>
                            {
                                t.Span(g.MarcaNombre).FontSize(11).Bold().FontColor(Colors.Amber.Darken4);
                                if (!string.IsNullOrEmpty(g.ProveedorNombre))
                                    t.Span($"   ·   proveedor: {g.ProveedorNombre}").FontSize(8).Italic().FontColor(Colors.Amber.Darken2);
                            });
                            r.ConstantItem(80).AlignRight().Text($"{g.ItemsCafe.Count + g.ItemsOtros.Count} prod.")
                                .FontSize(8).FontColor(Colors.Amber.Darken2);
                        });

                        // Tabla CAFE (Producto · SKU · 1kg · 1/2 · 1/4)
                        if (g.ItemsCafe.Count > 0)
                        {
                            col.Item().PaddingTop(4).Table(t =>
                            {
                                t.ColumnsDefinition(cd =>
                                {
                                    cd.RelativeColumn(4);          // Producto
                                    cd.ConstantColumn(45);          // SKU
                                    cd.ConstantColumn(70);          // 1 kg
                                    cd.ConstantColumn(70);          // 1/2 kg
                                    cd.ConstantColumn(70);          // 1/4 kg
                                });

                                t.Header(h =>
                                {
                                    h.Cell().BorderBottom(1).BorderColor(Colors.Grey.Medium).PaddingVertical(4).Text("☕ Producto").Bold().FontSize(8.5f);
                                    h.Cell().BorderBottom(1).BorderColor(Colors.Grey.Medium).PaddingVertical(4).AlignCenter().Text("SKU").Bold().FontSize(8.5f);
                                    h.Cell().BorderBottom(1).BorderColor(Colors.Grey.Medium).PaddingVertical(4).AlignRight().Text("1 kg").Bold().FontSize(8.5f);
                                    h.Cell().BorderBottom(1).BorderColor(Colors.Grey.Medium).PaddingVertical(4).AlignRight().Text("1/2 kg").Bold().FontSize(8.5f);
                                    h.Cell().BorderBottom(1).BorderColor(Colors.Grey.Medium).PaddingVertical(4).AlignRight().Text("1/4 kg").Bold().FontSize(8.5f);
                                });

                                bool alt = false;
                                foreach (var i in g.ItemsCafe)
                                {
                                    var bg = alt ? Colors.Grey.Lighten4 : Colors.White;
                                    alt = !alt;
                                    t.Cell().Background(bg).PaddingVertical(3).PaddingLeft(2).Text(i.Nombre).FontSize(9);
                                    t.Cell().Background(bg).PaddingVertical(3).AlignCenter().Text(i.Sku ?? "—").FontSize(8).FontFamily("Courier");
                                    t.Cell().Background(bg).PaddingVertical(3).AlignRight().PaddingRight(2).Text($"$ {i.Precio1Kg.ToString("N0", Es)}").FontSize(9).Bold();
                                    t.Cell().Background(bg).PaddingVertical(3).AlignRight().PaddingRight(2).Text($"$ {i.PrecioMedio.ToString("N0", Es)}").FontSize(9).Bold();
                                    t.Cell().Background(bg).PaddingVertical(3).AlignRight().PaddingRight(2).Text($"$ {i.PrecioCuarto.ToString("N0", Es)}").FontSize(9).Bold();
                                }
                            });
                        }

                        // Tabla OTROS (Producto · SKU · Precio)
                        if (g.ItemsOtros.Count > 0)
                        {
                            col.Item().PaddingTop(4).Table(t =>
                            {
                                t.ColumnsDefinition(cd =>
                                {
                                    cd.RelativeColumn(5);
                                    cd.ConstantColumn(55);
                                    cd.ConstantColumn(85);
                                });
                                t.Header(h =>
                                {
                                    h.Cell().BorderBottom(1).BorderColor(Colors.Grey.Medium).PaddingVertical(4).Text("📦 Producto").Bold().FontSize(8.5f);
                                    h.Cell().BorderBottom(1).BorderColor(Colors.Grey.Medium).PaddingVertical(4).AlignCenter().Text("SKU").Bold().FontSize(8.5f);
                                    h.Cell().BorderBottom(1).BorderColor(Colors.Grey.Medium).PaddingVertical(4).AlignRight().Text("Precio").Bold().FontSize(8.5f);
                                });
                                bool alt = false;
                                foreach (var i in g.ItemsOtros)
                                {
                                    var bg = alt ? Colors.Grey.Lighten4 : Colors.White;
                                    alt = !alt;
                                    t.Cell().Background(bg).PaddingVertical(3).PaddingLeft(2).Text(i.Nombre).FontSize(9);
                                    t.Cell().Background(bg).PaddingVertical(3).AlignCenter().Text(i.Sku ?? "—").FontSize(8).FontFamily("Courier");
                                    t.Cell().Background(bg).PaddingVertical(3).AlignRight().PaddingRight(2).Text($"$ {i.Precio.ToString("N0", Es)}").FontSize(9).Bold();
                                }
                            });
                        }
                    }

                    // OBSERVACIONES
                    if (!string.IsNullOrEmpty(p.Observaciones))
                    {
                        col.Item().PaddingTop(10).Border(0.5f).BorderColor(Colors.Grey.Medium).Padding(8).Column(c =>
                        {
                            c.Item().Text("Observaciones").FontSize(8).Bold().FontColor(Colors.Grey.Darken2);
                            c.Item().PaddingTop(2).Text(p.Observaciones!).FontSize(9);
                        });
                    }

                    // AVISO IVA — fijo, visible siempre
                    col.Item().PaddingTop(8).Background(Colors.Red.Lighten4).Border(1).BorderColor(Colors.Red.Medium)
                        .Padding(6).AlignCenter()
                        .Text("⚠ LOS PRECIOS NO INCLUYEN IVA").FontSize(10).Bold().FontColor(Colors.Red.Darken3);
                });

                // ── FOOTER ──
                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("Los precios pueden variar sin previo aviso · ").FontSize(7).FontColor(Colors.Grey.Darken1).Italic();
                    t.Span($"Generado {DateTime.Now:dd/MM/yyyy HH:mm}").FontSize(7).FontColor(Colors.Grey.Darken1).Italic();
                });
            });
        }).GeneratePdf();
    }

    private byte[]? TryLoadLogoBytes(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var path = url.TrimStart('/');
            var prefix = "api/files/download?path=";
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var qp = path.Substring(prefix.Length);
                var decoded = System.Web.HttpUtility.UrlDecode(qp);
                var full = _files.ResolveSafe(decoded);
                if (File.Exists(full)) return File.ReadAllBytes(full);
            }
        }
        catch { }
        return null;
    }
}
