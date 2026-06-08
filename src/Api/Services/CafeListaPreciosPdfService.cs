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
        // Color del cuadradito "LISTA" + linea separadora. NEGRO para BAR, verde para OTRO.
        var tituloColor = p.TipoCliente == "BAR" ? "#000000" : "#15803d";
        var logoBytes = TryLoadLogoBytes(p.Negocio.LogoUrl) ?? TryLoadLogoFallback(p.Negocio.Cuit);
        // Si el negocio cargo una imagen completa para el header de listas, la usamos en vez del header armado a mano.
        var headerImageBytes = TryLoadLogoBytes(p.Negocio.ListaPreciosHeaderImageUrl);

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(25);
                // Default fuente sin especificar — usa la de QuestPDF (Lato) que soporta los emojis 📞 📱 ✉ 🌐
                page.DefaultTextStyle(t => t.FontSize(9));

                // ── HEADER ──
                // Si el usuario cargo una imagen custom para el header, la usamos a la izquierda con
                // el cuadradito "LISTA / N°" a la derecha. Sino, fallback al header armado a mano.
                page.Header().Column(headerCol =>
                {
                    if (headerImageBytes is not null)
                    {
                        headerCol.Item().PaddingBottom(8).BorderBottom(2).BorderColor(tituloColor).Row(row =>
                        {
                            row.RelativeItem().AlignLeft().AlignMiddle()
                                .Height(110).Image(headerImageBytes).FitArea();
                            row.ConstantItem(95).AlignRight().AlignTop().Column(c =>
                            {
                                c.Item().AlignRight().Width(80).Border(1).BorderColor(tituloColor).Column(cc =>
                                {
                                    cc.Item().Background(tituloColor).AlignCenter().Padding(4)
                                        .Text("LISTA").FontSize(10).Bold().FontColor(Colors.White).LetterSpacing(0.08f);
                                    if (!string.IsNullOrWhiteSpace(p.NumeroLista))
                                    {
                                        cc.Item().AlignCenter().Padding(6)
                                            .Text(p.NumeroLista!).FontSize(18).Bold().FontColor(tituloColor);
                                    }
                                    else
                                    {
                                        cc.Item().AlignCenter().Padding(6).Text(" ").FontSize(18);
                                    }
                                });
                            });
                        });
                        return; // saltamos el header de fallback
                    }

                    headerCol.Item().PaddingBottom(8).BorderBottom(2).BorderColor(tituloColor).Row(row =>
                    {
                        // IZQUIERDA: telefonos grandes + mail + web (en lugar de direccion)
                        row.RelativeItem(2).Column(c =>
                        {
                            if (!string.IsNullOrEmpty(p.Negocio.Telefono))
                            {
                                c.Item().Text(t =>
                                {
                                    t.Span("📞 ").FontSize(15);
                                    t.Span(p.Negocio.Telefono!).FontSize(17).SemiBold();
                                });
                            }
                            if (!string.IsNullOrEmpty(p.Negocio.WhatsappNumero))
                            {
                                c.Item().PaddingTop(4).Text(t =>
                                {
                                    t.Span("📱 ").FontSize(15);
                                    t.Span(p.Negocio.WhatsappNumero!).FontSize(17).SemiBold();
                                });
                            }
                            if (!string.IsNullOrEmpty(p.Negocio.Email))
                            {
                                c.Item().PaddingTop(4).Text(t =>
                                {
                                    t.Span("✉ ").FontSize(9).FontColor(Colors.Grey.Darken2);
                                    t.Span(p.Negocio.Email!).FontSize(9).FontColor(Colors.Grey.Darken2);
                                });
                            }
                            if (!string.IsNullOrEmpty(p.Negocio.Web))
                            {
                                c.Item().PaddingTop(2).Text(t =>
                                {
                                    t.Span("🌐 ").FontSize(9).FontColor(Colors.Grey.Darken2);
                                    t.Span(p.Negocio.Web!).FontSize(9).FontColor(Colors.Grey.Darken2).SemiBold();
                                });
                            }
                        });

                        // CENTRO: logo + nombre del negocio
                        row.RelativeItem(2).AlignCenter().Column(c =>
                        {
                            if (logoBytes is not null)
                            {
                                c.Item().AlignCenter().Width(55).Height(55).Image(logoBytes).FitArea();
                                c.Item().PaddingTop(4);
                            }
                            var nombre = p.Negocio.Nombre ?? "Empresa";
                            var partes = nombre.Split(new[] { " By ", " by " }, StringSplitOptions.None);
                            if (partes.Length == 2)
                            {
                                c.Item().AlignCenter().Text(partes[0] + " By").FontSize(18).Bold().LetterSpacing(0.5f);
                                c.Item().AlignCenter().Text(partes[1]).FontSize(18).Bold().LetterSpacing(0.5f);
                            }
                            else
                            {
                                c.Item().AlignCenter().Text(nombre).FontSize(18).Bold().LetterSpacing(0.5f);
                            }
                        });

                        // DERECHA: cuadradito "LISTA" + numero
                        row.RelativeItem().AlignRight().AlignTop().Column(c =>
                        {
                            c.Item().AlignRight().Width(80).Border(1).BorderColor(tituloColor).Column(cc =>
                            {
                                cc.Item().Background(tituloColor).AlignCenter().Padding(4)
                                    .Text("LISTA").FontSize(10).Bold().FontColor(Colors.White).LetterSpacing(0.08f);
                                if (!string.IsNullOrWhiteSpace(p.NumeroLista))
                                {
                                    cc.Item().AlignCenter().Padding(6)
                                        .Text(p.NumeroLista!).FontSize(18).Bold().FontColor(tituloColor);
                                }
                                else
                                {
                                    cc.Item().AlignCenter().Padding(6)
                                        .Text(" ").FontSize(18);
                                }
                            });
                        });
                    });

                    // Banner "PRECIOS VIGENTES DESDE..." sacado por pedido del usuario 2026-05-20 (noche).
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

                        // 2026-06-08: SKU primero (columna angosta), después Producto, después precios.
                        // Tabla CAFE (SKU · Producto · 1kg · 1/2 · 1/4)
                        if (g.ItemsCafe.Count > 0)
                        {
                            col.Item().PaddingTop(4).Table(t =>
                            {
                                t.ColumnsDefinition(cd =>
                                {
                                    cd.ConstantColumn(35);          // SKU (más angosto)
                                    cd.RelativeColumn(4);           // Producto
                                    cd.ConstantColumn(70);          // 1 kg
                                    cd.ConstantColumn(70);          // 1/2 kg
                                    cd.ConstantColumn(70);          // 1/4 kg
                                });

                                t.Header(h =>
                                {
                                    h.Cell().BorderBottom(1).BorderColor(Colors.Grey.Medium).PaddingVertical(4).Text("SKU").Bold().FontSize(8.5f);
                                    h.Cell().BorderBottom(1).BorderColor(Colors.Grey.Medium).PaddingVertical(4).Text("☕ Producto").Bold().FontSize(8.5f);
                                    h.Cell().BorderBottom(1).BorderColor(Colors.Grey.Medium).PaddingVertical(4).AlignRight().Text("1 kg").Bold().FontSize(8.5f);
                                    h.Cell().BorderBottom(1).BorderColor(Colors.Grey.Medium).PaddingVertical(4).AlignRight().Text("1/2 kg").Bold().FontSize(8.5f);
                                    h.Cell().BorderBottom(1).BorderColor(Colors.Grey.Medium).PaddingVertical(4).AlignRight().Text("1/4 kg").Bold().FontSize(8.5f);
                                });

                                bool alt = false;
                                foreach (var i in g.ItemsCafe)
                                {
                                    var bg = alt ? Colors.Grey.Lighten4 : Colors.White;
                                    alt = !alt;
                                    t.Cell().Background(bg).PaddingVertical(3).PaddingLeft(2).Text(i.Sku ?? "—").FontSize(8).FontFamily("Courier").Bold();
                                    t.Cell().Background(bg).PaddingVertical(3).PaddingLeft(4).Text(i.Nombre).FontSize(9);
                                    t.Cell().Background(bg).PaddingVertical(3).AlignRight().PaddingRight(2).Text($"$ {i.Precio1Kg.ToString("N0", Es)}").FontSize(9).Bold();
                                    t.Cell().Background(bg).PaddingVertical(3).AlignRight().PaddingRight(2).Text($"$ {i.PrecioMedio.ToString("N0", Es)}").FontSize(9).Bold();
                                    t.Cell().Background(bg).PaddingVertical(3).AlignRight().PaddingRight(2).Text($"$ {i.PrecioCuarto.ToString("N0", Es)}").FontSize(9).Bold();
                                }
                            });
                        }

                        // Tabla OTROS (SKU · Producto · Precio)
                        if (g.ItemsOtros.Count > 0)
                        {
                            col.Item().PaddingTop(4).Table(t =>
                            {
                                t.ColumnsDefinition(cd =>
                                {
                                    cd.ConstantColumn(45);          // SKU (más angosto)
                                    cd.RelativeColumn(5);           // Producto
                                    cd.ConstantColumn(85);          // Precio
                                });
                                t.Header(h =>
                                {
                                    h.Cell().BorderBottom(1).BorderColor(Colors.Grey.Medium).PaddingVertical(4).Text("SKU").Bold().FontSize(8.5f);
                                    h.Cell().BorderBottom(1).BorderColor(Colors.Grey.Medium).PaddingVertical(4).Text("📦 Producto").Bold().FontSize(8.5f);
                                    h.Cell().BorderBottom(1).BorderColor(Colors.Grey.Medium).PaddingVertical(4).AlignRight().Text("Precio").Bold().FontSize(8.5f);
                                });
                                bool alt = false;
                                foreach (var i in g.ItemsOtros)
                                {
                                    var bg = alt ? Colors.Grey.Lighten4 : Colors.White;
                                    alt = !alt;
                                    t.Cell().Background(bg).PaddingVertical(3).PaddingLeft(2).Text(i.Sku ?? "—").FontSize(8).FontFamily("Courier").Bold();
                                    t.Cell().Background(bg).PaddingVertical(3).PaddingLeft(4).Text(i.Nombre).FontSize(9);
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

    /// <summary>Fallback: si NegocioLogoUrl apunta a un archivo que no existe, busca el
    /// logo por convencion en 'Logos Empresa/{cuit}/logo.{png|jpg|...}'.</summary>
    private byte[]? TryLoadLogoFallback(string? cuitRaw)
    {
        if (string.IsNullOrWhiteSpace(cuitRaw)) return null;
        var cuit = new string(cuitRaw.Where(char.IsDigit).ToArray());
        if (cuit.Length != 11) return null;
        try
        {
            var ext = new[] { ".png", ".jpg", ".jpeg", ".webp" };
            foreach (var e in ext)
            {
                var rel = $"Logos Empresa/{cuit}/logo{e}";
                var abs = _files.ResolveSafe(rel);
                if (File.Exists(abs)) return File.ReadAllBytes(abs);
            }
        }
        catch { }
        return null;
    }
}
