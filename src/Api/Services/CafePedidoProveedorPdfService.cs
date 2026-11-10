using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Api.Services;

public record PedidoProveedorLinea(string? Codigo, string Nombre, decimal Cantidad, string Unidad, int? Bultos, int? UnidadesPorBulto);

/// <summary>2026-11-10 — El PDF del pedido al proveedor: qué se pide y cuánto. A propósito sin
/// stock ni ideal, que son datos internos que el proveedor no necesita.</summary>
public class CafePedidoProveedorPdfService
{
    private static readonly CultureInfo Es = new("es-AR");

    static CafePedidoProveedorPdfService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public byte[] Generar(int numero, string? marca, DateTime cerradoAtUtc, string? remitente, IReadOnlyList<PedidoProveedorLinea> lineas)
    {
        var fecha = cerradoAtUtc.AddHours(-3).ToString("dd/MM/yyyy", Es);
        var conBultos = lineas.Any(l => l.Bultos.HasValue);

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(30);
                page.DefaultTextStyle(t => t.FontSize(10));

                page.Header().PaddingBottom(14).Row(row =>
                {
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text("Pedido a proveedor").FontSize(18).Bold();
                        if (!string.IsNullOrWhiteSpace(remitente)) c.Item().Text(remitente).FontColor(Colors.Grey.Darken1);
                    });
                    row.ConstantItem(170).AlignRight().Column(c =>
                    {
                        c.Item().AlignRight().Text($"N° {numero}").Bold();
                        c.Item().AlignRight().Text(fecha);
                        c.Item().AlignRight().Text(string.IsNullOrWhiteSpace(marca) ? "Sin marca" : marca).Bold();
                    });
                });

                page.Content().Table(t =>
                {
                    t.ColumnsDefinition(col =>
                    {
                        col.ConstantColumn(90);
                        col.RelativeColumn();
                        col.ConstantColumn(80);
                        if (conBultos) col.ConstantColumn(90);
                    });

                    static IContainer Cab(IContainer c) => c.BorderBottom(1).BorderColor(Colors.Grey.Darken1).PaddingVertical(5);
                    static IContainer Cel(IContainer c) => c.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(5);

                    t.Header(h =>
                    {
                        h.Cell().Element(Cab).Text("Código").Bold();
                        h.Cell().Element(Cab).Text("Producto").Bold();
                        h.Cell().Element(Cab).AlignRight().Text("Cantidad").Bold();
                        if (conBultos) h.Cell().Element(Cab).AlignRight().Text("Bultos").Bold();
                    });

                    foreach (var l in lineas)
                    {
                        t.Cell().Element(Cel).Text(l.Codigo ?? "");
                        t.Cell().Element(Cel).Text(l.Nombre);
                        t.Cell().Element(Cel).AlignRight().Text($"{l.Cantidad.ToString("0.##", Es)} {l.Unidad}");
                        if (conBultos)
                            t.Cell().Element(Cel).AlignRight().Text(l.Bultos.HasValue ? $"{l.Bultos} x {l.UnidadesPorBulto}" : "");
                    }
                });

                page.Footer().AlignCenter().Text(x =>
                {
                    x.Span("Página ").FontSize(8).FontColor(Colors.Grey.Darken1);
                    x.CurrentPageNumber().FontSize(8).FontColor(Colors.Grey.Darken1);
                    x.Span(" de ").FontSize(8).FontColor(Colors.Grey.Darken1);
                    x.TotalPages().FontSize(8).FontColor(Colors.Grey.Darken1);
                });
            });
        }).GeneratePdf();
    }
}
