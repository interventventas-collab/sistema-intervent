using Api.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Api.Services;

/// <summary>
/// Genera el PDF del recibo de entrega de una venta.
/// Contiene: datos del emisor, datos del cliente, detalle de la venta, fecha y hora de entrega,
/// nombre del repartidor, nombre del receptor (si firmó) e imagen de la firma (si firmó),
/// o motivo de por qué no hay firma.
/// 2026-06-22: nuevo servicio para soportar el flujo "el repartidor entregó y el cliente firmó".
/// </summary>
public class CafeReciboEntregaPdfService
{
    private readonly ArcaEmisorService _emisorService;

    public CafeReciboEntregaPdfService(ArcaEmisorService emisorService)
    {
        _emisorService = emisorService;
    }

    public byte[] GenerarPdfBytes(CafeVenta v, CafeCliente? cliente, CafeRepartidor? repartidor, CafeSetting? cfg)
    {
        var emisorNombre = cfg?.NegocioNombre ?? "Frikaf";
        var emisorRazon = cfg?.NegocioRazonSocial;
        var emisorDir = cfg?.NegocioDireccion;
        var emisorCuit = cfg?.NegocioCuit;
        var emisorTel = cfg?.NegocioTelefono;
        var emisorEmail = cfg?.NegocioEmail;

        // Logo opcional desde la ficha del Emisor (mismo que usan los otros PDFs)
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
                        col.Item().AlignRight().Text("RECIBO DE ENTREGA").FontSize(13).Bold();
                        col.Item().AlignRight().Text($"N° {v.Numero}").FontSize(11);
                        col.Item().AlignRight().Text($"Fecha venta: {v.Fecha:dd/MM/yyyy}").FontSize(9);
                    });
                });

                page.Content().PaddingVertical(0.6f, Unit.Centimetre).Column(content =>
                {
                    // Datos del cliente
                    content.Item().PaddingBottom(8).Background("#f4f6fa").Padding(10).Column(c =>
                    {
                        c.Item().Text("DESTINATARIO").FontSize(9).FontColor("#6b7280").Bold();
                        c.Item().Text(cliente?.Nombre ?? v.ClienteNombreSnapshot ?? "—").FontSize(12).Bold();
                        var rs = cliente?.RazonSocial ?? v.ClienteRazonSocialSnapshot;
                        if (!string.IsNullOrWhiteSpace(rs)) c.Item().Text(rs).FontSize(10);
                        var domEntr = !string.IsNullOrWhiteSpace(v.ClienteDomicilioEntregaSnapshot)
                            ? v.ClienteDomicilioEntregaSnapshot
                            : (cliente?.DomicilioEntrega ?? cliente?.Direccion);
                        if (!string.IsNullOrWhiteSpace(domEntr)) c.Item().Text($"📍 {domEntr}").FontSize(10);
                        if (!string.IsNullOrWhiteSpace(cliente?.Cuit)) c.Item().Text($"CUIT: {cliente.Cuit}").FontSize(9);
                    });

                    // Detalle de la entrega
                    content.Item().PaddingTop(6).PaddingBottom(6).Text("DETALLE DE LA ENTREGA").FontSize(9).FontColor("#6b7280").Bold();
                    content.Item().Border(0.5f).BorderColor("#d1d5db").Padding(8).Column(c =>
                    {
                        c.Item().Row(r =>
                        {
                            r.RelativeItem().Text("Producto").Bold().FontSize(9);
                            r.ConstantItem(50).AlignRight().Text("Cant.").Bold().FontSize(9);
                        });
                        // Agrupacion combo: emitir 1 fila por combo origen (sumando cantidades)
                        var emitidos = new HashSet<int>();
                        foreach (var it in v.Items)
                        {
                            if (it.ComboOrigenId.HasValue)
                            {
                                var cid = it.ComboOrigenId.Value;
                                if (emitidos.Contains(cid)) continue;
                                emitidos.Add(cid);
                                var grupo = v.Items.Where(x => x.ComboOrigenId == cid).ToList();
                                c.Item().PaddingTop(2).Row(rr =>
                                {
                                    rr.RelativeItem().Text(grupo[0].ProductoNombreSnapshot + " (combo)").FontSize(10);
                                    rr.ConstantItem(50).AlignRight().Text("1").FontSize(10);
                                });
                                continue;
                            }
                            c.Item().PaddingTop(2).Row(rr =>
                            {
                                var nombre = it.ProductoNombreSnapshot;
                                if (!string.IsNullOrEmpty(it.Formato)) nombre += $" — {it.Formato}";
                                rr.RelativeItem().Text(nombre).FontSize(10);
                                rr.ConstantItem(50).AlignRight().Text(it.Cantidad.ToString("0.##")).FontSize(10);
                            });
                        }
                    });

                    // 2026-06-22: Datos de la entrega — siempre se muestra el bloque, con campos vacios
                    // ("____________") si la venta no esta entregada todavia. Asi el repartidor puede imprimir
                    // y completar a mano si necesita.
                    content.Item().PaddingTop(12).PaddingBottom(6).Text("CONFIRMACION DE ENTREGA").FontSize(9).FontColor("#6b7280").Bold();
                    content.Item().Border(0.5f).BorderColor("#d1d5db").Padding(10).Column(c =>
                    {
                        var fechaEntrega = v.EntregaFirmadaAt ?? v.EntregadoAt;
                        var fechaTxt = fechaEntrega.HasValue ? fechaEntrega.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm") : "___________________";
                        c.Item().Text($"Fecha y hora de entrega: {fechaTxt}").FontSize(10);
                        var repTxt = repartidor?.Nombre ?? "___________________";
                        c.Item().Text($"Entregado por: {repTxt}").FontSize(10);
                        var recTxt = !string.IsNullOrWhiteSpace(v.NombreReceptor) ? v.NombreReceptor : "___________________";
                        c.Item().PaddingTop(4).Text($"Recibido por: {recTxt}").FontSize(10).Bold();
                        var dniTxt = !string.IsNullOrWhiteSpace(v.DniReceptor) ? v.DniReceptor : "___________________";
                        c.Item().Text($"DNI: {dniTxt}").FontSize(10);
                        if (!string.IsNullOrWhiteSpace(v.MotivoSinFirma))
                        {
                            c.Item().PaddingTop(6).Background("#fef3c7").Padding(6).Text($"⚠ Sin firma — Motivo: {v.MotivoSinFirma}").FontSize(10);
                        }
                    });

                    // 2026-06-22: Firma — SIEMPRE se muestra el recuadro. Si hay imagen, se renderiza.
                    // Si no, queda vacio para que el receptor firme a mano cuando se imprime.
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
