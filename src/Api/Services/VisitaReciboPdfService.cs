using Api.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Api.Services;

/// <summary>
/// Genera el PDF del "RECIBO DE VISITA" (2026-08-05). Mismo formato/branding que el recibo de
/// entrega de ventas (CafeReciboEntregaPdfService): logo + encabezado del emisor, destinatario,
/// detalle libre (la descripción de la visita), un QR para sumar al reparto / hacer seguimiento,
/// y un cuadro de firma del receptor. Pensado para imprimir y salir a reparto.
/// </summary>
public class VisitaReciboPdfService
{
    private readonly ArcaEmisorService _emisorService;

    static VisitaReciboPdfService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public VisitaReciboPdfService(ArcaEmisorService emisorService)
    {
        _emisorService = emisorService;
    }

    public byte[] GenerarPdfBytes(Visita v, CafeSetting? cfg, byte[]? qr)
    {
        var emisorNombre = cfg?.NegocioNombre ?? "Frikaf";
        var emisorRazon = cfg?.NegocioRazonSocial;
        var emisorDir = cfg?.NegocioDireccion;
        var emisorCuit = cfg?.NegocioCuit;
        var emisorTel = cfg?.NegocioTelefono;
        var emisorEmail = cfg?.NegocioEmail;

        // Logo + datos faltantes desde la ficha del Emisor (igual que el recibo de entrega).
        byte[]? logoBytes = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(emisorCuit))
            {
                var ficha = _emisorService.GetEntityByCuitAsync(emisorCuit).GetAwaiter().GetResult();
                logoBytes = _emisorService.TryGetLogoBytes(ficha?.LogoPath);
                if (string.IsNullOrWhiteSpace(emisorRazon)) emisorRazon = ficha?.RazonSocial;
                if (string.IsNullOrWhiteSpace(emisorDir)) emisorDir = ficha?.Domicilio;
                if (string.IsNullOrWhiteSpace(emisorTel)) emisorTel = ficha?.Telefono;
                if (string.IsNullOrWhiteSpace(emisorEmail)) emisorEmail = ficha?.Email;
            }
        }
        catch { /* sin logo si falla */ }

        byte[]? firmaBytes = null;
        if (!string.IsNullOrWhiteSpace(v.FirmaBase64))
        {
            try
            {
                var b64 = v.FirmaBase64;
                var commaIdx = b64.IndexOf(',');
                if (commaIdx >= 0) b64 = b64.Substring(commaIdx + 1);
                firmaBytes = Convert.FromBase64String(b64);
            }
            catch { firmaBytes = null; }
        }

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(10).FontFamily("Helvetica"));

                page.Header().Row(row =>
                {
                    if (logoBytes != null && logoBytes.Length > 0)
                    {
                        row.ConstantItem(80).Image(logoBytes).FitArea();
                    }
                    row.RelativeItem().Column(col =>
                    {
                        col.Item().Text(emisorRazon ?? emisorNombre).FontSize(14).Bold();
                        if (!string.IsNullOrWhiteSpace(emisorDir)) col.Item().Text(emisorDir).FontSize(9);
                        if (!string.IsNullOrWhiteSpace(emisorCuit)) col.Item().Text($"CUIT: {emisorCuit}").FontSize(9);
                        if (!string.IsNullOrWhiteSpace(emisorTel)) col.Item().Text($"Tel: {emisorTel}").FontSize(9);
                        if (!string.IsNullOrWhiteSpace(emisorEmail)) col.Item().Text(emisorEmail).FontSize(9);
                    });
                    row.ConstantItem(160).AlignRight().Column(col =>
                    {
                        col.Item().AlignRight().Text("RECIBO DE VISITA").FontSize(13).Bold();
                        col.Item().AlignRight().Text($"N° {v.Numero:0000}").FontSize(11);
                        col.Item().AlignRight().Text($"Fecha: {v.CreatedAt.ToLocalTime():dd/MM/yyyy}").FontSize(9);
                    });
                });

                page.Content().PaddingVertical(0.6f, Unit.Centimetre).Column(content =>
                {
                    // Destinatario
                    content.Item().PaddingBottom(8).Background("#f4f6fa").Padding(10).Column(c =>
                    {
                        c.Item().Text("DESTINATARIO").FontSize(9).FontColor("#6b7280").Bold();
                        c.Item().Text(v.ClienteNombre).FontSize(12).Bold();
                        if (!string.IsNullOrWhiteSpace(v.Direccion))
                        {
                            var dir = v.Direccion + (string.IsNullOrWhiteSpace(v.Localidad) ? "" : $", {v.Localidad}");
                            c.Item().Text($"📍 {dir}").FontSize(10);
                        }
                        if (!string.IsNullOrWhiteSpace(v.Telefono)) c.Item().Text($"Tel: {v.Telefono}").FontSize(9);
                    });

                    // Detalle de la visita (descripción libre)
                    content.Item().PaddingTop(6).PaddingBottom(6).Text("DETALLE DE LA VISITA").FontSize(9).FontColor("#6b7280").Bold();
                    content.Item().Border(0.5f).BorderColor("#d1d5db").Padding(10).Column(c =>
                    {
                        c.Item().Text(string.IsNullOrWhiteSpace(v.Descripcion) ? "—" : v.Descripcion).FontSize(11);
                    });

                    // QR para sumar al reparto / seguimiento
                    if (qr != null && qr.Length > 0)
                    {
                        content.Item().PaddingTop(12).Background("#eef2ff").Border(0.5f).BorderColor("#c7d2fe").Padding(10).Row(r =>
                        {
                            r.ConstantItem(90).Height(90).Image(qr).FitArea();
                            r.RelativeItem().PaddingLeft(10).AlignMiddle().Column(c =>
                            {
                                c.Item().Text("📲 Escaneá para sumar al reparto y hacer seguimiento").FontSize(11).Bold();
                                c.Item().PaddingTop(3).Text("Con la pistola del Mapeo: agrega esta visita a la ruta. Con el celular: abre el estado y permite marcarla como realizada.").FontSize(8).FontColor("#4b5563");
                            });
                        });
                    }

                    // Confirmación de la visita (se completa a mano si hace falta)
                    content.Item().PaddingTop(12).PaddingBottom(6).Text("CONFIRMACION DE LA VISITA").FontSize(9).FontColor("#6b7280").Bold();
                    content.Item().Border(0.5f).BorderColor("#d1d5db").Padding(10).Column(c =>
                    {
                        var fechaTxt = v.RealizadaAt.HasValue ? v.RealizadaAt.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm") : "___________________";
                        c.Item().Text($"Fecha y hora de la visita: {fechaTxt}").FontSize(10);
                        c.Item().Text("Atendido por: ___________________").FontSize(10);
                        var recTxt = !string.IsNullOrWhiteSpace(v.NombreFirmante) ? v.NombreFirmante : "___________________";
                        c.Item().PaddingTop(4).Text($"Recibido por: {recTxt}").FontSize(10).Bold();
                        if (!string.IsNullOrWhiteSpace(v.ComentarioResolucion))
                        {
                            c.Item().PaddingTop(6).Background("#f0fdf4").Padding(6).Text($"💬 {v.ComentarioResolucion}").FontSize(10);
                        }
                    });

                    // Firma del receptor
                    content.Item().PaddingTop(14).Text("FIRMA DEL RECEPTOR").FontSize(9).FontColor("#6b7280").Bold();
                    content.Item().PaddingTop(4).Border(0.5f).BorderColor("#d1d5db").Padding(6).Column(c =>
                    {
                        if (firmaBytes != null && firmaBytes.Length > 0)
                        {
                            c.Item().Height(80).AlignCenter().Image(firmaBytes).FitArea();
                        }
                        else
                        {
                            c.Item().Height(80).AlignCenter().AlignMiddle().Text("(Firme aqui)").FontSize(9).FontColor("#d1d5db");
                        }
                    });
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("Documento generado por ").FontSize(8).FontColor("#9ca3af");
                    t.Span(emisorRazon ?? emisorNombre).FontSize(8).FontColor("#9ca3af").Bold();
                    t.Span($" — {DateTime.Now:dd/MM/yyyy HH:mm}").FontSize(8).FontColor("#9ca3af");
                });
            });
        });

        return doc.GeneratePdf();
    }
}
