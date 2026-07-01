using Api.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Globalization;

namespace Api.Services;

/// <summary>
/// Genera el PDF del comprobante de una reserva de alquiler (cliente, equipos, fechas
/// incluida la del evento, seña/saldo, QR del repartidor y condiciones). Pedido 2026-06-29.
/// </summary>
public class AlqReservaPdfService
{
    private static readonly CultureInfo Es = new("es-AR");
    private static readonly string[] Estados = { "reservado", "confirmado", "entregado", "finalizado", "cancelado" };

    static AlqReservaPdfService() { QuestPDF.Settings.License = LicenseType.Community; }

    private static string Money(decimal v) => "$" + v.ToString("N2", Es);
    private static string FechaLarga(DateTime d) => d.ToString("dddd dd 'de' MMMM 'de' yyyy", Es);

    public byte[] Generar(AlqReserva r, CafeSetting? cfg, byte[]? qr, string? condiciones)
    {
        cfg ??= new CafeSetting();
        // 2026-07-01: las reservas de alquiler usan la marca propia INTEREVENTOS (no la del café).
        const string negocio = "INTEREVENTOS ®";
        const string webAlquileres = "www.intereventos.com.ar";
        var conPrecios = r.Items.Any(i => i.PrecioUnitario > 0);
        var subtotal = r.Items.Sum(i => i.Cantidad * i.PrecioUnitario);
        var saldo = Math.Max(0m, r.MontoTotal - r.Sena - r.MontoCobrado);

        var pdf = Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(28);
                page.DefaultTextStyle(t => t.FontSize(10).FontColor("#111827"));

                // ===== Cabecera =====
                page.Header().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text(negocio).FontSize(18).Bold().FontColor("#111827");
                            var contacto = new List<string>();
                            if (!string.IsNullOrWhiteSpace(cfg.NegocioTelefono)) contacto.Add("Tel: " + cfg.NegocioTelefono);
                            if (!string.IsNullOrWhiteSpace(cfg.NegocioEmail)) contacto.Add(cfg.NegocioEmail!);
                            contacto.Add(webAlquileres);
                            if (contacto.Count > 0) c.Item().Text(string.Join("  ·  ", contacto)).FontSize(8).FontColor("#6b7280");
                        });
                        row.ConstantItem(150).Column(c =>
                        {
                            c.Item().AlignRight().Text("RESERVA").FontSize(15).Bold().FontColor("#1d4ed8");
                            c.Item().AlignRight().Text(r.Numero).FontSize(11).Bold();
                            c.Item().AlignRight().Text("Emitida: " + DateTime.Today.ToString("dd/MM/yyyy", Es)).FontSize(8).FontColor("#6b7280");
                            c.Item().AlignRight().Text("Estado: " + Capitalizar(r.Estado)).FontSize(9).FontColor("#374151");
                        });
                        if (qr is not null)
                        {
                            row.ConstantItem(90).AlignRight().Column(c =>
                            {
                                c.Item().AlignRight().Width(84).Image(qr);
                                c.Item().AlignRight().Text("Escaneá para entregar/retirar/cobrar").FontSize(6).FontColor("#6b7280");
                            });
                        }
                    });
                    col.Item().PaddingTop(6).LineHorizontal(1).LineColor("#d1d5db");
                });

                // ===== Contenido =====
                page.Content().PaddingVertical(8).Column(col =>
                {
                    // Banner Fecha del evento (destacado)
                    if (r.FechaEvento.HasValue)
                    {
                        col.Item().PaddingBottom(8).Background("#f5f3ff").Border(1.5f).BorderColor("#7c3aed")
                           .Padding(8).Column(c =>
                        {
                            c.Item().AlignCenter().Text("FECHA DEL EVENTO").FontSize(8).Bold().FontColor("#6d28d9");
                            c.Item().AlignCenter().Text(FechaLarga(r.FechaEvento.Value)).FontSize(16).Bold().FontColor("#5b21b6");
                        });
                    }

                    // Cliente + Evento (2 columnas)
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Border(1).BorderColor("#d1d5db").Padding(8).Column(c =>
                        {
                            c.Item().Text("CLIENTE").FontSize(8).Bold().FontColor("#6b7280");
                            c.Item().Text(r.ClienteNav?.Nombre ?? "—").FontSize(11).Bold();
                            if (!string.IsNullOrWhiteSpace(r.ClienteNav?.DniCuit)) c.Item().Text("DNI/CUIT: " + r.ClienteNav!.DniCuit).FontSize(9);
                            if (!string.IsNullOrWhiteSpace(r.ClienteNav?.Telefono)) c.Item().Text("Tel: " + r.ClienteNav!.Telefono).FontSize(9);
                            if (!string.IsNullOrWhiteSpace(r.ClienteNav?.Email)) c.Item().Text(r.ClienteNav!.Email!).FontSize(9);
                        });
                        row.ConstantItem(8);
                        row.RelativeItem().Border(1).BorderColor("#d1d5db").Padding(8).Column(c =>
                        {
                            c.Item().Text("EVENTO").FontSize(8).Bold().FontColor("#6b7280");
                            c.Item().Text(t => { t.Span("Entrega: ").Bold(); t.Span(r.FechaEntrega.ToString("dd/MM/yyyy", Es) + " (" + DiaSemana(r.FechaEntrega) + ")"); });
                            c.Item().Text(t => { t.Span("Retiro: ").Bold(); t.Span(r.FechaRetiro.ToString("dd/MM/yyyy", Es) + " (" + DiaSemana(r.FechaRetiro) + ")"); });
                            if (!string.IsNullOrWhiteSpace(r.HoraInicio) || !string.IsNullOrWhiteSpace(r.HoraFin))
                                c.Item().Text("Horario: " + (r.HoraInicio ?? "") + (string.IsNullOrWhiteSpace(r.HoraFin) ? "" : " a " + r.HoraFin)).FontSize(9);
                            if (!string.IsNullOrWhiteSpace(r.DireccionEvento))
                                c.Item().Text(t => { t.Span("Dirección: ").Bold(); t.Span(r.DireccionEvento!); });
                        });
                    });

                    // Equipos
                    col.Item().PaddingTop(10).Text("EQUIPOS").FontSize(8).Bold().FontColor("#6b7280");
                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(cd =>
                        {
                            cd.ConstantColumn(45);   // cant
                            cd.ConstantColumn(70);   // sku
                            cd.RelativeColumn();      // nombre
                            if (conPrecios) { cd.ConstantColumn(75); cd.ConstantColumn(80); }
                        });
                        table.Header(h =>
                        {
                            h.Cell().Background("#f3f4f6").Padding(4).Text("Cant.").FontSize(8).Bold();
                            h.Cell().Background("#f3f4f6").Padding(4).Text("SKU").FontSize(8).Bold();
                            h.Cell().Background("#f3f4f6").Padding(4).Text("Equipo").FontSize(8).Bold();
                            if (conPrecios)
                            {
                                h.Cell().Background("#f3f4f6").Padding(4).AlignRight().Text("P. Unit.").FontSize(8).Bold();
                                h.Cell().Background("#f3f4f6").Padding(4).AlignRight().Text("Subtotal").FontSize(8).Bold();
                            }
                        });
                        foreach (var i in r.Items)
                        {
                            table.Cell().BorderBottom(0.5f).BorderColor("#e5e7eb").Padding(4).Text(i.Cantidad.ToString()).FontSize(9);
                            table.Cell().BorderBottom(0.5f).BorderColor("#e5e7eb").Padding(4).Text(i.EquipoNav?.Sku ?? "—").FontSize(9);
                            table.Cell().BorderBottom(0.5f).BorderColor("#e5e7eb").Padding(4).Text(i.EquipoNav?.Nombre ?? "—").FontSize(9);
                            if (conPrecios)
                            {
                                table.Cell().BorderBottom(0.5f).BorderColor("#e5e7eb").Padding(4).AlignRight().Text(Money(i.PrecioUnitario)).FontSize(9);
                                table.Cell().BorderBottom(0.5f).BorderColor("#e5e7eb").Padding(4).AlignRight().Text(Money(i.Cantidad * i.PrecioUnitario)).FontSize(9);
                            }
                        }
                    });

                    // Totales (a la derecha)
                    col.Item().PaddingTop(10).AlignRight().Width(240).Border(1).BorderColor("#d1d5db").Padding(8).Column(c =>
                    {
                        if (conPrecios)
                        {
                            c.Item().Row(rw => { rw.RelativeItem().Text("Subtotal:"); rw.ConstantItem(100).AlignRight().Text(Money(subtotal)); });
                            if (r.Descuento > 0)
                                c.Item().Row(rw => { rw.RelativeItem().Text("Descuento:"); rw.ConstantItem(100).AlignRight().Text("- " + Money(r.Descuento)).FontColor("#dc2626"); });
                        }
                        c.Item().PaddingVertical(3).LineHorizontal(0.5f).LineColor("#d1d5db");
                        c.Item().Row(rw => { rw.RelativeItem().Text("TOTAL:").Bold(); rw.ConstantItem(100).AlignRight().Text(Money(r.MontoTotal)).Bold().FontColor("#059669"); });
                        c.Item().Row(rw => { rw.RelativeItem().Text("Seña pagada:"); rw.ConstantItem(100).AlignRight().Text(Money(r.Sena)).FontColor("#059669"); });
                        if (r.MontoCobrado > 0)
                            c.Item().Row(rw => { rw.RelativeItem().Text("Cobrado en mano:"); rw.ConstantItem(100).AlignRight().Text(Money(r.MontoCobrado)).FontColor("#059669"); });
                        c.Item().Row(rw => { rw.RelativeItem().Text("Saldo pendiente:").Bold(); rw.ConstantItem(100).AlignRight().Text(Money(saldo)).Bold().FontColor(saldo > 0 ? "#dc2626" : "#059669"); });
                    });

                    // Condiciones
                    if (!string.IsNullOrWhiteSpace(condiciones))
                    {
                        col.Item().PaddingTop(14).Text("CONDICIONES").FontSize(8).Bold().FontColor("#6b7280");
                        col.Item().Text(condiciones).FontSize(8).FontColor("#374151");
                    }
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("Reserva " + r.Numero + " · ").FontSize(7).FontColor("#9ca3af");
                    t.CurrentPageNumber().FontSize(7).FontColor("#9ca3af");
                    t.Span("/").FontSize(7).FontColor("#9ca3af");
                    t.TotalPages().FontSize(7).FontColor("#9ca3af");
                });
            });
        });

        return pdf.GeneratePdf();
    }

    private static string Capitalizar(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);

    private static string DiaSemana(DateTime d) => d.DayOfWeek switch
    {
        DayOfWeek.Monday => "LUNES", DayOfWeek.Tuesday => "MARTES", DayOfWeek.Wednesday => "MIÉRCOLES",
        DayOfWeek.Thursday => "JUEVES", DayOfWeek.Friday => "VIERNES", DayOfWeek.Saturday => "SÁBADO",
        DayOfWeek.Sunday => "DOMINGO", _ => ""
    };
}
