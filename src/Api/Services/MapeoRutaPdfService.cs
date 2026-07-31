using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Api.Services;

/// <summary>
/// Genera el PDF del listado de una ruta de reparto (Mapeo).
/// Recibe columnas ya elegidas por el usuario (orden, nombre, dirección, teléfono, notas, etc.)
/// y las paradas agrupadas por repartidor. Cada grupo se imprime con el nombre del repartidor
/// (con su color) y una tabla con las paradas en orden de recorrido.
/// Las paradas entregadas quedan pintadas de verde suave para distinguirlas de un vistazo.
/// </summary>
public class MapeoRutaPdfService
{
    static MapeoRutaPdfService() { QuestPDF.Settings.License = LicenseType.Community; }

    public record Columna(string Header, float Weight);
    public record Fila(string[] Celdas, bool Entregado);
    public record Grupo(string Titulo, string? ColorHex, List<Fila> Filas);

    public byte[] Generar(string titulo, string subtitulo, List<Columna> columnas, List<Grupo> grupos)
    {
        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(28);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Header().Column(col =>
                {
                    col.Item().Text(titulo).FontSize(15).Bold();
                    if (!string.IsNullOrEmpty(subtitulo))
                        col.Item().Text(subtitulo).FontSize(9).FontColor(Colors.Grey.Medium);
                });

                page.Content().PaddingTop(6).Column(col =>
                {
                    foreach (var g in grupos)
                    {
                        col.Item().PaddingTop(10).Row(r =>
                        {
                            r.ConstantItem(11).Height(11).Background(SafeColor(g.ColorHex));
                            r.RelativeItem().PaddingLeft(6).Text($"{g.Titulo}  ({g.Filas.Count})").Bold().FontSize(11);
                        });

                        col.Item().PaddingTop(3).Table(table =>
                        {
                            table.ColumnsDefinition(cd =>
                            {
                                foreach (var c in columnas) cd.RelativeColumn(c.Weight);
                            });

                            foreach (var c in columnas)
                                table.Cell().Background(Colors.Grey.Lighten2).Padding(3)
                                     .Text(c.Header).Bold().FontSize(8);

                            bool alt = false;
                            foreach (var f in g.Filas)
                            {
                                var bg = f.Entregado
                                    ? Colors.Green.Lighten4
                                    : (alt ? Colors.Grey.Lighten4 : Colors.White);
                                foreach (var cel in f.Celdas)
                                    table.Cell().Background(bg).Padding(3)
                                         .Text(cel ?? string.Empty).FontSize(8);
                                alt = !alt;
                            }
                        });
                    }
                });

                page.Footer().AlignRight().Text(t =>
                {
                    t.Span("Página ").FontSize(8).FontColor(Colors.Grey.Medium);
                    t.CurrentPageNumber().FontSize(8).FontColor(Colors.Grey.Medium);
                    t.Span(" de ").FontSize(8).FontColor(Colors.Grey.Medium);
                    t.TotalPages().FontSize(8).FontColor(Colors.Grey.Medium);
                });
            });
        });
        return doc.GeneratePdf();
    }

    private static string SafeColor(string? hex)
        => string.IsNullOrWhiteSpace(hex)
            ? Colors.Grey.Medium
            : (hex.StartsWith("#") ? hex : "#" + hex);
}
