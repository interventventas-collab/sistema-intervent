using Api.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Globalization;

namespace Api.Services;

/// <summary>
/// Genera el PDF de las "Listas de precios personalizadas" (estilo TAKE AWAY).
/// Layout:
///   - Header: logo + datos de contacto + bloque opcional "PRECIOS SIN IVA / mes año"
///   - Título grande del nombre de la lista (TAKE AWAY)
///   - Por cada sección: banda oscura con título + col header "PRECIO" + filas con SKU/nombre/NOVEDAD/precio
/// </summary>
public class CafeListaCustomPdfService
{
    private readonly FileStorageService _files;
    private static readonly CultureInfo Es = new("es-AR");

    static CafeListaCustomPdfService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public CafeListaCustomPdfService(FileStorageService files)
    {
        _files = files;
    }

    public record PdfInput(CafeSetting Negocio, ListaInfo Lista, List<SeccionInfo> Secciones);
    public record ListaInfo(string Nombre, string? NumeroLista, string? Observaciones, string? TipoCliente, string? ClienteNombre);
    public record SeccionInfo(string Titulo, List<ItemInfo> Items);
    public record ItemInfo(string? Sku, string Nombre, string? Marca, string? Detalle, decimal? Precio, bool EsNovedad);

    public byte[] GenerarPdf(PdfInput inp)
    {
        var logoBytes = TryLoadLogoBytes(inp.Negocio.NegocioLogoUrl) ?? TryLoadLogoFallback(inp.Negocio.NegocioCuit);
        var headerImageBytes = TryLoadLogoBytes(inp.Negocio.ListaPreciosHeaderImageUrl);
        var nombreEmpresa = inp.Negocio.NegocioNombre ?? "Empresa";

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(25);
                page.DefaultTextStyle(t => t.FontSize(9));

                // ═══════════ HEADER ═══════════
                page.Header().Column(headerCol =>
                {
                    headerCol.Item().PaddingBottom(6).BorderBottom(1.5f).BorderColor(Colors.Black).Row(row =>
                    {
                        // Izquierda: logo
                        row.RelativeItem(1).AlignLeft().AlignMiddle().Column(c =>
                        {
                            if (logoBytes is not null)
                                c.Item().Height(60).AlignLeft().Image(logoBytes).FitArea();
                            else
                                c.Item().Text(nombreEmpresa).FontSize(18).Bold();
                        });

                        // Centro: titulo "LISTA DE PRECIOS MAYORISTAS" + contactos
                        row.RelativeItem(2).AlignCenter().Column(c =>
                        {
                            c.Item().AlignCenter().Text("LISTA DE PRECIOS MAYORISTAS").FontSize(12).Bold();
                            if (!string.IsNullOrWhiteSpace(inp.Lista.Observaciones))
                                c.Item().AlignCenter().Text(inp.Lista.Observaciones!).FontSize(10).Italic().FontColor(Colors.Grey.Darken1);
                            var tels = new List<string>();
                            if (!string.IsNullOrEmpty(inp.Negocio.NegocioTelefono)) tels.Add(inp.Negocio.NegocioTelefono!);
                            if (!string.IsNullOrEmpty(inp.Negocio.NegocioTelefono2)) tels.Add(inp.Negocio.NegocioTelefono2!);
                            if (!string.IsNullOrEmpty(inp.Negocio.NegocioWhatsappNumero) && !tels.Contains(inp.Negocio.NegocioWhatsappNumero!))
                                tels.Add(inp.Negocio.NegocioWhatsappNumero!);
                            if (tels.Count > 0)
                                c.Item().AlignCenter().Text(string.Join("  //  ", tels)).FontSize(10).SemiBold();
                            if (!string.IsNullOrEmpty(inp.Negocio.NegocioEmail))
                                c.Item().AlignCenter().Text(inp.Negocio.NegocioEmail!.ToUpperInvariant()).FontSize(9).FontColor(Colors.Grey.Darken2);
                        });

                        // Derecha: bloque opcional "PRECIOS SIN IVA / NumeroLista"
                        row.RelativeItem(1).AlignRight().AlignMiddle().Column(c =>
                        {
                            if (!string.IsNullOrWhiteSpace(inp.Lista.NumeroLista))
                            {
                                c.Item().AlignRight().Text("PRECIOS SIN IVA").FontSize(8).Bold().FontColor(Colors.Grey.Darken2);
                                c.Item().AlignRight().Text(inp.Lista.NumeroLista!).FontSize(11).Bold();
                            }
                            if (!string.IsNullOrWhiteSpace(inp.Lista.ClienteNombre))
                                c.Item().PaddingTop(2).AlignRight().Text(inp.Lista.ClienteNombre!).FontSize(8).FontColor(Colors.Grey.Darken1);
                        });
                    });

                    // Titulo grande de la lista (TAKE AWAY)
                    headerCol.Item().PaddingTop(8).PaddingBottom(4).AlignCenter()
                        .Text(inp.Lista.Nombre.ToUpperInvariant()).FontSize(32).Bold()
                        .FontColor(Colors.Grey.Lighten1).LetterSpacing(3f);
                });

                // ═══════════ CONTENIDO ═══════════
                page.Content().PaddingTop(8).Column(col =>
                {
                    if (inp.Secciones.Count == 0)
                    {
                        col.Item().AlignCenter().PaddingTop(20)
                            .Text("Lista vacía — agregá secciones e items desde el sistema").FontSize(11).Italic().FontColor(Colors.Grey.Medium);
                    }
                    foreach (var sec in inp.Secciones)
                    {
                        // Banda de sección (negra con título a la izquierda y "PRECIO" a la derecha)
                        col.Item().PaddingTop(4).Background(Colors.Black).Padding(4).Row(r =>
                        {
                            r.RelativeItem().Text(sec.Titulo.ToUpperInvariant()).FontSize(10).Bold().FontColor(Colors.White);
                            r.AutoItem().Text("PRECIO").FontSize(10).Bold().FontColor(Colors.White);
                        });

                        // Tabla de items
                        col.Item().Table(tbl =>
                        {
                            tbl.ColumnsDefinition(cd =>
                            {
                                cd.ConstantColumn(80);   // SKU
                                cd.RelativeColumn(5);    // Nombre
                                cd.ConstantColumn(45);   // NOVEDAD
                                cd.ConstantColumn(80);   // Precio
                            });
                            foreach (var it in sec.Items)
                            {
                                tbl.Cell().BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten2).Padding(3)
                                    .AlignLeft().Text(it.Sku ?? "").FontSize(8).Italic().FontColor(Colors.Grey.Darken2);
                                tbl.Cell().BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten2).Padding(3)
                                    .Text(t =>
                                    {
                                        // 2026-06-16: marca como prefijo gris antes del nombre
                                        if (!string.IsNullOrWhiteSpace(it.Marca))
                                            t.Span(it.Marca!.ToUpperInvariant() + " · ").FontSize(8).Bold().FontColor(Colors.Grey.Darken2);
                                        t.Span(it.Nombre).FontSize(10).SemiBold();
                                        if (!string.IsNullOrEmpty(it.Detalle))
                                            t.Span($"  · {it.Detalle}").FontSize(8).FontColor(Colors.Grey.Darken1);
                                    });
                                tbl.Cell().BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten2).Padding(3).AlignCenter()
                                    .Element(e =>
                                    {
                                        if (it.EsNovedad)
                                            e.AlignMiddle().Background(Colors.Red.Darken1).Padding(2).AlignCenter()
                                                .Text("NOVEDAD").FontSize(6.5f).Bold().FontColor(Colors.White);
                                        else
                                            e.Text("");
                                    });
                                tbl.Cell().BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten2).Padding(3)
                                    .AlignRight().Text(it.Precio.HasValue ? "$" + it.Precio.Value.ToString("N0", Es) : "—")
                                    .FontSize(11).Bold().Italic();
                            }
                        });
                    }
                });

                // ═══════════ FOOTER ═══════════
                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("Generado el " + DateTime.UtcNow.AddHours(-3).ToString("dd/MM/yyyy HH:mm", Es))
                        .FontSize(7).FontColor(Colors.Grey.Medium);
                    if (!string.IsNullOrEmpty(inp.Negocio.NegocioWeb))
                    {
                        t.Span("  ·  ").FontSize(7).FontColor(Colors.Grey.Medium);
                        t.Span(inp.Negocio.NegocioWeb!).FontSize(7).FontColor(Colors.Grey.Darken1).SemiBold();
                    }
                });
            });
        }).GeneratePdf();
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
        catch { return null; }
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
        catch { }
        return null;
    }
}
