using Api.Data;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Api.Services;

/// <summary>
/// 2026-11-16: "Remito de alquiler" para el depósito. Pedido del dueño: que el alquiler aparezca en
/// Pedidos para armar como una venta más, "con el detalle del alquiler para imprimir y que puedan pegarle
/// el remito con el pedido a las cosas". SIN precios (aprobado en el dibujo): número grande, cliente,
/// dirección, teléfono, entrega/retiro, qué lleva con cantidades, la nota del dueño y firmas.
/// Lo usan DepositoTareasController (PDF suelto) y CafeVentasController (imprimir todo junto).
/// </summary>
public static class AlqRemitoPdfService
{
    static AlqRemitoPdfService() { QuestPDF.Settings.License = LicenseType.Community; }

    private static readonly string[] Dias = { "domingo", "lunes", "martes", "miércoles", "jueves", "viernes", "sábado" };

    /// <summary>PDF de la tarea ALQUILER (Dep_Tareas.Id). null si no existe o no es de alquiler.</summary>
    public static async Task<(byte[] Bytes, string Numero)?> GenerarAsync(AppDbContext db, int tareaId)
    {
        var t = await db.DepTareas.AsNoTracking().FirstOrDefaultAsync(x => x.Id == tareaId);
        if (t?.AlqReservaId is not int rid) return null;
        var r = await db.AlqReservas.AsNoTracking().Include(x => x.ClienteNav)
            .Include(x => x.Items).ThenInclude(i => i.EquipoNav)
            .FirstOrDefaultAsync(x => x.Id == rid);
        if (r is null) return null;

        var items = r.Items.Where(i => i.EquipoId != null && i.Cantidad > 0)
            .GroupBy(i => i.EquipoNav?.Nombre ?? "—")
            .Select(g => (Nombre: g.Key, Cantidad: g.Sum(x => x.Cantidad)))
            .OrderByDescending(x => x.Cantidad).ToList();
        var cliente = r.ClienteNav?.Nombre ?? "";
        var tel = r.ClienteNav?.Telefono;
        string Fecha(DateTime d) => $"{Dias[(int)d.DayOfWeek]} {d:dd/MM/yyyy}";
        var hora = string.IsNullOrWhiteSpace(r.HoraInicio) ? "" : $" · {r.HoraInicio}";

        var bytes = Document.Create(c => c.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(1.6f, Unit.Centimetre);
            page.DefaultTextStyle(x => x.FontSize(12).FontFamily("Helvetica"));
            page.Content().Column(col =>
            {
                col.Spacing(4);
                col.Item().Text("REMITO DE ALQUILER").FontSize(22).Bold();
                col.Item().Text(r.Numero).FontSize(34).Bold();
                col.Item().PaddingTop(6).Text(tx => { tx.Span("Cliente: ").Bold(); tx.Span(cliente); });
                if (!string.IsNullOrWhiteSpace(r.DireccionEvento))
                    col.Item().Text(tx => { tx.Span("Dirección: ").Bold(); tx.Span(r.DireccionEvento); });
                if (!string.IsNullOrWhiteSpace(tel))
                    col.Item().Text(tx => { tx.Span("Teléfono: ").Bold(); tx.Span(tel); });
                col.Item().PaddingTop(4).Text(tx =>
                {
                    tx.Span("Entrega: ").Bold(); tx.Span(Fecha(r.FechaEntrega) + hora);
                    tx.Span("      Retiro: ").Bold(); tx.Span(Fecha(r.FechaRetiro));
                });

                col.Item().PaddingTop(12).Table(table =>
                {
                    table.ColumnsDefinition(cd => { cd.ConstantColumn(70); cd.RelativeColumn(); cd.ConstantColumn(50); });
                    static IContainer Celda(IContainer x) => x.Border(1).BorderColor(Colors.Grey.Darken2).Padding(7);
                    table.Header(h =>
                    {
                        h.Cell().Background(Colors.Grey.Lighten3).Element(Celda).Text("Cant.").Bold().FontSize(11);
                        h.Cell().Background(Colors.Grey.Lighten3).Element(Celda).Text("Qué lleva").Bold().FontSize(11);
                        h.Cell().Background(Colors.Grey.Lighten3).Element(Celda).Text("Listo").Bold().FontSize(11);
                    });
                    foreach (var it in items)
                    {
                        table.Cell().Element(Celda).Text(it.Cantidad.ToString()).FontSize(16).Bold();
                        table.Cell().Element(Celda).Text(it.Nombre).FontSize(14);
                        table.Cell().Element(Celda).Text("");
                    }
                    if (items.Count == 0)
                    {
                        table.Cell().ColumnSpan(3).Element(Celda).Text("(el alquiler no tiene equipos cargados)").Italic();
                    }
                });

                if (!string.IsNullOrWhiteSpace(t.Comentario))
                    col.Item().PaddingTop(10).Text(tx => { tx.Span("Nota: ").Bold(); tx.Span(t.Comentario); });

                col.Item().PaddingTop(40).Row(row =>
                {
                    row.RelativeItem().Text("Armó: ______________________");
                    row.RelativeItem().AlignRight().Text("Recibí conforme: ______________________");
                });
            });
        })).GeneratePdf();
        return (bytes, r.Numero);
    }
}
