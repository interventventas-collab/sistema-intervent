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
        // Cambios pedidos por el usuario 2026-05-20:
        // - Header: solo el nombre del negocio (sin logo, direccion, tel, mail, web, CUIT).
        //   El logo no se ve correctamente — pendiente arreglarlo en otro momento.
        // - Bloque superior derecho: solo "LISTA DE PRECIOS" + N° de lista si esta cargado.
        //   Sin Cliente/Tipo/Fecha (la fecha queda en el footer).
        // - Sin franja de marca/proveedor entre productos.
        // - Sin bloque "Condiciones comerciales" al final.
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
                        row.RelativeItem(2).AlignMiddle()
                            .Text(p.Negocio.Nombre ?? "Empresa").FontSize(17).Bold();

                        row.RelativeItem().AlignRight().Column(c =>
                        {
                            c.Item().AlignRight().Background(tituloColor).Padding(6).Text("LISTA DE PRECIOS")
                                .FontSize(11).Bold().FontColor(Colors.White);
                            if (!string.IsNullOrWhiteSpace(p.NumeroLista))
                            {
                                c.Item().PaddingTop(5).AlignRight().Text(t =>
                                {
                                    t.Span("Lista N° ").Bold().FontSize(10);
                                    t.Span(p.NumeroLista!).FontSize(10);
                                });
                            }
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
                        // Franja de marca/proveedor sacada por pedido del usuario 2026-05-20.

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

                    // Bloque "Observaciones / Condiciones comerciales" sacado por pedido del usuario 2026-05-20.

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
