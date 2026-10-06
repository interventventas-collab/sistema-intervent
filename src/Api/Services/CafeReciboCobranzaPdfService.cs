using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Globalization;

namespace Api.Services;

/// <summary>
/// Genera el PDF del recibo de cobranza (Cafe → Tesorería → Cobranza → Imprimir).
/// Documento simple: cabecera con datos del negocio + tabla de comprobantes cobrados
/// + tabla de formas de cobro + retenciones + total + observaciones.
/// </summary>
public class CafeReciboCobranzaPdfService
{
    private readonly ILogger<CafeReciboCobranzaPdfService> _logger;
    private static readonly CultureInfo Es = new("es-AR");

    static CafeReciboCobranzaPdfService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public CafeReciboCobranzaPdfService(ILogger<CafeReciboCobranzaPdfService> logger)
    {
        _logger = logger;
    }

    /// <summary>Un renglón de "Aplicado a:". Saldo = lo que queda debiendo HOY de ese comprobante
    /// (null si no corresponde: a cuenta, nota de crédito, venta anulada).</summary>
    public record ReciboLinea(string Numero, decimal Importe, bool ACuenta, decimal? Saldo,
        // 2026-10-06: cuota de MÁQUINA FINANCIADA. Detalle = renglón chico abajo ("USD 500 (dólar $1.450 =
        // $725.000)" y "Le falta pagar de la máquina ..."). SaldoTexto = lo que va en la columna de saldo
        // (puede ser en dólares, por eso la máquina NO suma en el "SALDO PENDIENTE" en pesos: Saldo = null).
        string? Detalle = null, string? SaldoTexto = null);

    /// <summary>2026-10-06: el detalle de la forma de cobro. En la caja de dólares agrega cuántos USD
    /// entraron y a qué dólar (el importe del renglón queda en pesos, como todo el recibo).</summary>
    public static string? ReferenciaMedio(CafeCobranzaMedio m)
    {
        if (m.ImporteUsd is not > 0m) return m.Referencia;
        var usd = $"USD {m.ImporteUsd.Value.ToString("N0", Es)}"
                + (m.CotizacionUsd is > 0m ? $" (dólar $ {m.CotizacionUsd.Value.ToString("N0", Es)})" : "");
        return string.IsNullOrWhiteSpace(m.Referencia) ? usd : $"{usd} · {m.Referencia}";
    }

    /// <summary>2026-10-05: arma los renglones del recibo. Antes lo cobrado a un ALQUILER salía como
    /// "A CUENTA (sin imputar a comprobante)" porque solo se miraba VentaId; ahora sale "Alquiler RES-…".
    /// Y cada renglón lleva su saldo pendiente (pedido del negocio: que el recibo diga siempre cuánto falta).</summary>
    public static async Task<List<ReciboLinea>> LineasAsync(AppDbContext db, CafeCobranza c)
    {
        var ventaIds = c.Comprobantes.Where(x => x.VentaId != null).Select(x => x.VentaId!.Value).Distinct().ToList();
        var reservaIds = c.Comprobantes.Where(x => x.ReservaId != null).Select(x => x.ReservaId!.Value).Distinct().ToList();
        var comodatoIds = c.Comprobantes.Where(x => x.ComodatoId != null).Select(x => x.ComodatoId!.Value).Distinct().ToList();

        var ventas = await db.CafeVentas.AsNoTracking().Where(v => ventaIds.Contains(v.Id))
            .Select(v => new { v.Id, v.Numero, v.Total, v.ArcaImpTotal, v.TipoComprobante, v.Estado, v.NotaCreditoVentaId })
            .ToDictionaryAsync(v => v.Id);
        var pagadoVentas = await db.CafeCobranzasComprobantes.AsNoTracking()
            .Where(x => x.VentaId != null && ventaIds.Contains(x.VentaId.Value) && x.Cobranza!.Estado == "VIGENTE")
            .GroupBy(x => x.VentaId!.Value)
            .Select(g => new { Id = g.Key, Pagado = g.Sum(x => x.Importe) })
            .ToDictionaryAsync(x => x.Id, x => x.Pagado);
        var reservas = await db.AlqReservas.AsNoTracking().Where(r => reservaIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Numero, r.MontoTotal, r.ArcaImpTotal, r.Sena, r.MontoCobrado, r.Estado })
            .ToDictionaryAsync(r => r.Id);
        // 2026-10-06: máquinas financiadas. Lo que falta sale de SaldoFinanciamiento (lo mantiene al día
        // CafeComodatoSaldoService al crear/anular cobranzas), en la moneda de la máquina.
        var maquinas = await db.CafeComodatos.AsNoTracking().Where(m => comodatoIds.Contains(m.Id))
            .Select(m => new { m.Id, m.Marca, m.Modelo, m.Moneda, m.SaldoFinanciamiento })
            .ToDictionaryAsync(m => m.Id);

        var lineas = new List<ReciboLinea>();
        foreach (var x in c.Comprobantes)
        {
            if (x.ComodatoId is int mid)
            {
                if (!maquinas.TryGetValue(mid, out var mq)) { lineas.Add(new ReciboLinea($"Máquina #{mid} (cuota)", x.Importe, false, null)); continue; }
                var usd = mq.Moneda == "USD";
                var falta = Math.Max(0m, mq.SaldoFinanciamiento ?? 0m);
                var faltaTxt = usd ? $"USD {falta.ToString("N2", Es)}" : $"$ {Fmt(falta)}";
                var detalle = new List<string>();
                if (usd && x.ImporteUsd is > 0m)
                    detalle.Add($"USD {x.ImporteUsd.Value.ToString("N2", Es)} (dólar $ {Fmt(Math.Round(x.Importe / x.ImporteUsd.Value, 2))} = $ {Fmt(x.Importe)})");
                detalle.Add($"Le falta pagar de la máquina {faltaTxt}");
                lineas.Add(new ReciboLinea($"{CafeComodatoSaldoService.Nombre(mq.Marca, mq.Modelo)} (cuota)", x.Importe, false, null,
                    string.Join(" · ", detalle), faltaTxt));
            }
            else if (x.ReservaId is int rid)
            {
                if (!reservas.TryGetValue(rid, out var r)) { lineas.Add(new ReciboLinea($"Alquiler #{rid}", x.Importe, false, null)); continue; }
                var monto = r.ArcaImpTotal is > 0m ? r.ArcaImpTotal.Value : r.MontoTotal;
                decimal? saldo = r.Estado == "cancelado" ? null : Math.Max(0m, monto - r.Sena - r.MontoCobrado);
                lineas.Add(new ReciboLinea($"Alquiler {r.Numero}", x.Importe, false, saldo));
            }
            else if (x.VentaId is int vid && ventas.TryGetValue(vid, out var v))
            {
                var esNc = v.TipoComprobante is not null && v.TipoComprobante.StartsWith("NC", StringComparison.OrdinalIgnoreCase);
                var cobrable = v.ArcaImpTotal is > 0m ? v.ArcaImpTotal.Value : v.Total;
                decimal? saldo = esNc || v.Estado == "anulado" || v.NotaCreditoVentaId.HasValue
                    ? null
                    : Math.Max(0m, cobrable - (pagadoVentas.TryGetValue(vid, out var p) ? p : 0m));
                lineas.Add(new ReciboLinea(v.Numero ?? $"#{vid}", x.Importe, false, saldo));
            }
            else
                lineas.Add(new ReciboLinea("", x.Importe, true, null));
        }
        return lineas;
    }

    public byte[] GenerarPdfBytes(
        CafeCobranza cobranza,
        CafeCliente cliente,
        List<ReciboLinea> comprobantes,
        List<(string cajaNombre, decimal importe, string? referencia, string? chequeInfo)> medios,
        CafeSetting? cfg)
    {
        cfg ??= new CafeSetting();

        var pdf = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(20);
                page.DefaultTextStyle(t => t.FontSize(9));

                // ─── HEADER ───
                page.Header().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text(cfg.NegocioNombre ?? "Mi Empresa").FontSize(14).SemiBold();
                            if (!string.IsNullOrWhiteSpace(cfg.NegocioRazonSocial) && cfg.NegocioRazonSocial != cfg.NegocioNombre)
                                c.Item().Text(cfg.NegocioRazonSocial).FontSize(8);
                            if (!string.IsNullOrWhiteSpace(cfg.NegocioCuit))
                                c.Item().Text($"CUIT: {cfg.NegocioCuit}").FontSize(8);
                            if (!string.IsNullOrWhiteSpace(cfg.NegocioDireccion))
                                c.Item().Text(cfg.NegocioDireccion).FontSize(8);
                            if (!string.IsNullOrWhiteSpace(cfg.NegocioTelefono))
                                c.Item().Text($"Tel: {cfg.NegocioTelefono}").FontSize(8);
                        });

                        row.ConstantItem(170).Column(c =>
                        {
                            c.Item().AlignRight().Background("#1d4ed8").Padding(8).Column(cc =>
                            {
                                cc.Item().AlignCenter().Text("RECIBO DE COBRANZA").FontColor(Colors.White).SemiBold().FontSize(11);
                                cc.Item().AlignCenter().Text($"N° {cobranza.Numero}").FontColor(Colors.White).SemiBold().FontSize(10);
                            });
                            c.Item().PaddingTop(4).AlignRight().Text($"Fecha: {cobranza.Fecha.ToLocalTime():dd/MM/yyyy}").FontSize(9);
                        });
                    });

                    col.Item().PaddingTop(8).BorderTop(0.5f).PaddingTop(6).Column(c =>
                    {
                        c.Item().Text(t =>
                        {
                            t.Span("Recibido de: ").SemiBold();
                            t.Span(cliente.Nombre).SemiBold();
                        });
                        if (!string.IsNullOrWhiteSpace(cliente.RazonSocial) && cliente.RazonSocial != cliente.Nombre)
                            c.Item().Text(t => { t.Span("Razón social: ").SemiBold(); t.Span(cliente.RazonSocial); });
                        if (!string.IsNullOrWhiteSpace(cliente.Cuit))
                            c.Item().Text(t => { t.Span("CUIT/DNI: ").SemiBold(); t.Span(cliente.Cuit); });
                        if (!string.IsNullOrWhiteSpace(cliente.Direccion))
                            c.Item().Text(cliente.Direccion).FontSize(8);
                    });
                });

                // ─── BODY ───
                page.Content().PaddingVertical(10).Column(col =>
                {
                    col.Spacing(8);

                    // Comprobantes
                    col.Item().Text("Aplicado a:").SemiBold().FontSize(10);
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c => { c.RelativeColumn(2); c.RelativeColumn(); c.RelativeColumn(); });
                        t.Header(h =>
                        {
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(4).Text("Comprobante").SemiBold().FontSize(9);
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(4).AlignRight().Text("Importe").SemiBold().FontSize(9);
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(4).AlignRight().Text("Saldo pendiente").SemiBold().FontSize(9);
                        });
                        foreach (var c in comprobantes)
                        {
                            // 2026-10-06: la cuota de máquina lleva abajo el detalle (USD / lo que le falta).
                            t.Cell().Padding(3).Column(cc =>
                            {
                                cc.Item().Text(c.ACuenta ? "A CUENTA (sin imputar a comprobante)" : c.Numero).FontSize(9);
                                if (!string.IsNullOrWhiteSpace(c.Detalle))
                                    cc.Item().Text(c.Detalle).FontSize(7.5f).FontColor("#6b7280");
                            });
                            t.Cell().Padding(3).AlignRight().Text($"$ {Fmt(c.Importe)}").FontSize(9);
                            t.Cell().Padding(3).AlignRight().Text(c.Saldo is decimal sd ? $"$ {Fmt(sd)}" : (c.SaldoTexto ?? "—")).FontSize(9);
                        }
                    });

                    // Formas de cobro
                    col.Item().PaddingTop(6).Text("Forma de cobro:").SemiBold().FontSize(10);
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c => { c.RelativeColumn(2); c.RelativeColumn(2); c.RelativeColumn(); });
                        t.Header(h =>
                        {
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(4).Text("Forma").SemiBold().FontSize(9);
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(4).Text("Detalle").SemiBold().FontSize(9);
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(4).AlignRight().Text("Importe").SemiBold().FontSize(9);
                        });
                        foreach (var m in medios)
                        {
                            t.Cell().Padding(3).Text(m.cajaNombre).FontSize(9);
                            t.Cell().Padding(3).Text((m.referencia ?? "") + (string.IsNullOrEmpty(m.chequeInfo) ? "" : " · " + m.chequeInfo)).FontSize(8);
                            t.Cell().Padding(3).AlignRight().Text($"$ {Fmt(m.importe)}").FontSize(9);
                        }
                    });

                    // Totales
                    col.Item().PaddingTop(8).AlignRight().Column(c =>
                    {
                        c.Item().Width(220).Row(r =>
                        {
                            r.RelativeItem().Text("Subtotal medios de cobro:").FontSize(9);
                            r.AutoItem().Text($"$ {Fmt(cobranza.Total)}").SemiBold().FontSize(9);
                        });
                        if (cobranza.Retenciones > 0)
                        {
                            c.Item().Width(220).Row(r =>
                            {
                                r.RelativeItem().Text("+ Retenciones sufridas:").FontSize(9);
                                r.AutoItem().Text($"$ {Fmt(cobranza.Retenciones)}").SemiBold().FontSize(9);
                            });
                        }
                        c.Item().Width(220).PaddingTop(2).Background("#1d4ed8").Padding(4).Row(r =>
                        {
                            r.RelativeItem().Text("TOTAL CANCELADO").FontColor(Colors.White).SemiBold().FontSize(10);
                            r.AutoItem().Text($"$ {Fmt(cobranza.Total + cobranza.Retenciones)}").FontColor(Colors.White).SemiBold().FontSize(11);
                        });
                    });

                    // 2026-10-05: cuánto queda debiendo de lo que se pagó en este recibo (al día de hoy).
                    var conSaldo = comprobantes.Where(x => x.Saldo.HasValue).ToList();
                    if (conSaldo.Count > 0)
                    {
                        var saldoTotal = conSaldo.Sum(x => x.Saldo!.Value);
                        var hoyAr = DateTime.UtcNow.AddHours(-3);
                        col.Item().AlignRight().Width(220).Border(1).BorderColor(Colors.Grey.Lighten1).Padding(4).Row(r =>
                        {
                            r.RelativeItem().Text($"SALDO PENDIENTE al {hoyAr:dd/MM/yyyy}").SemiBold().FontSize(9);
                            r.AutoItem().Text($"$ {Fmt(saldoTotal)}").SemiBold().FontSize(10)
                                .FontColor(saldoTotal > 0.5m ? Colors.Red.Darken2 : Colors.Green.Darken2);
                        });
                    }

                    if (!string.IsNullOrWhiteSpace(cobranza.Observaciones))
                    {
                        col.Item().PaddingTop(8).BorderTop(0.3f).PaddingTop(4).Column(c =>
                        {
                            c.Item().Text("Observaciones:").SemiBold().FontSize(9);
                            c.Item().Text(cobranza.Observaciones).FontSize(8);
                        });
                    }

                    // Firmas
                    col.Item().PaddingTop(30).Row(r =>
                    {
                        r.RelativeItem().Column(c =>
                        {
                            c.Item().BorderTop(0.5f).PaddingTop(2).AlignCenter().Text("Firma del cliente").FontSize(8);
                        });
                        r.ConstantItem(40);
                        r.RelativeItem().Column(c =>
                        {
                            c.Item().BorderTop(0.5f).PaddingTop(2).AlignCenter().Text("Firma del recibidor").FontSize(8);
                        });
                    });
                });

                // ─── FOOTER ───
                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("Recibo emitido el ").FontSize(7).FontColor("#6b7280");
                    t.Span(DateTime.Now.ToString("dd/MM/yyyy HH:mm")).FontSize(7).FontColor("#6b7280");
                    if (!string.IsNullOrWhiteSpace(cobranza.Operador))
                        t.Span($" · Operador: {cobranza.Operador}").FontSize(7).FontColor("#6b7280");
                });
            });
        }).GeneratePdf();

        return pdf;
    }

    private static string Fmt(decimal v) => v.ToString("N2", Es);
}
