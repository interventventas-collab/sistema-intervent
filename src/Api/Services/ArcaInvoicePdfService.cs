using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Api.Services;

/// <summary>
/// Generador puro de PDF de comprobantes ARCA. NO toca ARCA, NO toca disco.
/// Recibe los datos del emisor + comprobante + receptor y devuelve byte[]
/// del PDF.
/// </summary>
public class ArcaInvoicePdfService
{
    static ArcaInvoicePdfService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public byte[] GenerarPdfBytes(PdfEmisor emisor, PdfComprobante comp, PdfReceptor receptor, bool isHomologation)
    {
        var letra = LetraDelTipo(comp.CbteTipoNro);     // "A" / "B" / "C"
        var nombreTipo = comp.CbteTipoNombre;
        var discriminaIva = letra == "A"; // Solo A discrimina IVA en el PDF
        var esTipoC = letra == "C";

        // ---- QR ----
        var qrBytes = BuildQrAfipPng(emisor.Cuit, comp.Fecha, comp.PtoVta, comp.CbteTipoNro,
            comp.CbteNro, comp.ImpTotal, receptor.DocTipo, receptor.DocNro, comp.Cae ?? "");

        var pdfBytes = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(20);
                page.DefaultTextStyle(t => t.FontSize(9));

                // ───── HEADER ─────
                // 2026-06-16 v2: layout de 2 columnas (1 : 1.2) como cotización — logo+datos fiscales juntos
                // a la izquierda, cuadro A + numeración + bloque entrega + QR GRANDE a la derecha.
                // El QR ENTREGA pasa a 140px para que el repartidor escanee fácil desde el celu.
                page.Header().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        // ─── IZQUIERDA: logo + datos fiscales del emisor ───
                        row.RelativeItem(1).Column(c =>
                        {
                            if (emisor.LogoBytes is not null && emisor.LogoBytes.Length > 0)
                            {
                                c.Item().Height(45).AlignLeft().Image(emisor.LogoBytes).FitHeight();
                            }
                            c.Item().PaddingTop(4).Text("ORIGINAL").FontSize(8).SemiBold();
                            c.Item().Text(emisor.RazonSocial).FontSize(12).Bold();
                            c.Item().Text($"CUIT: {FormatCuit(emisor.Cuit)}").FontSize(8);
                            c.Item().Text($"Condición IVA: {emisor.CondicionIva}").FontSize(8);
                            if (!string.IsNullOrEmpty(emisor.Domicilio))
                                c.Item().Text(emisor.Domicilio!).FontSize(8);
                            if (!string.IsNullOrEmpty(emisor.IIBBNumero))
                            {
                                var label = string.IsNullOrEmpty(emisor.IIBBTipo) ? "IIBB" : $"IIBB {emisor.IIBBTipo}";
                                c.Item().Text($"{label}: {emisor.IIBBNumero}").FontSize(8);
                            }
                            if (emisor.InicioActividades.HasValue)
                            {
                                c.Item().Text($"Inicio de actividades: {emisor.InicioActividades.Value:dd/MM/yyyy}").FontSize(8);
                            }

                            // 2026-06-17 v8: bloque "PEDIDOS AL:" bajo los datos fiscales — reemplaza la franja horizontal
                            // de tels+email que antes vivia entre el header y la tabla.
                            var tieneTelEm = !string.IsNullOrWhiteSpace(emisor.Telefono);
                            var tieneTel2Em = !string.IsNullOrWhiteSpace(emisor.Telefono2);
                            var tieneEmailEm = !string.IsNullOrWhiteSpace(emisor.Email);
                            if (tieneTelEm || tieneTel2Em || tieneEmailEm)
                            {
                                c.Item().PaddingTop(6).Background(Colors.Grey.Lighten4).BorderLeft(2).BorderColor(Colors.Grey.Darken1).Padding(5).Column(cp =>
                                {
                                    cp.Item().Text("PEDIDOS AL:").FontSize(7).Bold().FontColor(Colors.Grey.Darken3).LetterSpacing(0.05f);
                                    if (tieneTelEm)
                                        cp.Item().PaddingTop(2).Text($"Tel. {emisor.Telefono}").FontSize(10).Bold();
                                    if (tieneTel2Em)
                                        cp.Item().Text($"Tel. {emisor.Telefono2}").FontSize(10).Bold();
                                    if (tieneEmailEm)
                                        cp.Item().PaddingTop(2).Text(emisor.Email!).FontSize(8).FontColor(Colors.Grey.Darken2);
                                });
                            }

                            // 2026-06-16 v3: bloque Receptor pegado debajo del emisor (antes era una fila aparte
                            // entre la franja de contacto y la tabla → ocupaba espacio para productos).
                            c.Item().PaddingTop(8).BorderTop(0.5f).BorderColor(Colors.Grey.Lighten1).PaddingTop(4).Column(cr =>
                            {
                                cr.Item().Text(t =>
                                {
                                    t.Span("Receptor: ").SemiBold().FontSize(9);
                                    t.Span(receptor.Nombre ?? "—").FontSize(9);
                                    if (!string.IsNullOrWhiteSpace(comp.TipoClienteTag))
                                    {
                                        var tag = comp.TipoClienteTag!.Trim().ToUpperInvariant();
                                        var tagLabel = tag == "BAR" ? " · BAR" : (tag == "OTRO" ? "" : $" · {tag}");
                                        if (!string.IsNullOrEmpty(tagLabel))
                                            t.Span(tagLabel).FontSize(8).FontColor(Colors.Grey.Darken1);
                                    }
                                    t.Span("   ·   ").FontSize(9).FontColor(Colors.Grey.Darken1);
                                    t.Span($"{NombreDocTipo(receptor.DocTipo)}: ").SemiBold().FontSize(9);
                                    t.Span(receptor.DocTipo == 80 ? FormatCuit(receptor.DocNro) : (receptor.DocNro ?? "—")).FontSize(9);
                                });
                                if (!string.IsNullOrEmpty(receptor.Domicilio))
                                    cr.Item().Text($"Domicilio: {receptor.Domicilio}").FontSize(9);
                                cr.Item().Text(t =>
                                {
                                    t.Span("Condición IVA: ").SemiBold().FontSize(9);
                                    t.Span(NombreCondicionIva(receptor.CondicionIvaId)).FontSize(9);
                                });
                                if (!string.IsNullOrWhiteSpace(receptor.CondicionVenta))
                                {
                                    cr.Item().Text(t =>
                                    {
                                        t.Span("Forma de pago: ").SemiBold().FontSize(9);
                                        t.Span(receptor.CondicionVenta!).FontSize(9);
                                    });
                                }
                                if (!string.IsNullOrWhiteSpace(comp.ComentariosCliente))
                                {
                                    cr.Item().PaddingTop(3).BorderLeft(2).BorderColor(Colors.Orange.Darken1)
                                        .Background(Colors.Orange.Lighten5).Padding(3)
                                        .Text(comp.ComentariosCliente!).FontSize(8).Italic().FontColor(Colors.Grey.Darken2);
                                }
                                if (!string.IsNullOrWhiteSpace(comp.Observaciones))
                                {
                                    cr.Item().PaddingTop(2).Text(t =>
                                    {
                                        t.Span("Observaciones: ").SemiBold().FontSize(8);
                                        t.Span(comp.Observaciones!).FontSize(8);
                                    });
                                }
                            });
                        });

                        // ─── DERECHA: cuadro A + tipo + numeración + bloque entrega + QR grande ───
                        row.RelativeItem(1.2f).PaddingLeft(10).Column(c =>
                        {
                            // Fila 1: cuadro A + numeración (cuadro chico 50px, queda al lado del texto)
                            c.Item().Row(headerR =>
                            {
                                headerR.ConstantItem(50).Border(1).Padding(3).Column(cc =>
                                {
                                    cc.Item().AlignCenter().Text(letra).FontSize(28).Bold();
                                    cc.Item().AlignCenter().Text($"COD. {comp.CbteTipoNro:00}").FontSize(7);
                                });
                                headerR.RelativeItem().PaddingLeft(6).Column(cc =>
                                {
                                    cc.Item().AlignRight().Text(nombreTipo).FontSize(13).Bold();
                                    cc.Item().AlignRight().Text($"Punto de Venta: {comp.PtoVta:00000}").FontSize(9);
                                    cc.Item().AlignRight().Text($"Comp. Nro: {comp.CbteNro:00000000}").FontSize(9);
                                    cc.Item().AlignRight().Text($"Fecha de Emisión: {FormatFecha(comp.Fecha)}").FontSize(9);
                                    // 2026-06-16: ref. interna del sistema (CAFE-2026-XXXX) para correlacionar con el correlativo interno.
                                    if (!string.IsNullOrWhiteSpace(comp.NumeroInterno))
                                    {
                                        cc.Item().PaddingTop(1).AlignRight()
                                            .Text($"Ref. interna: {comp.NumeroInterno}").FontSize(7).Italic().FontColor(Colors.Grey.Darken1);
                                    }
                                });
                            });

                            if (comp.IsPaid)
                            {
                                c.Item().PaddingTop(4).AlignRight().Row(r =>
                                {
                                    r.AutoItem().Background(Colors.Green.Lighten4).Border(1).BorderColor(Colors.Green.Darken1)
                                        .Padding(3).Text("PAGADO").FontSize(9).Bold().FontColor(Colors.Green.Darken3);
                                });
                            }
                            // Bloque entrega + QR grande debajo de la numeración.
                            RenderBloqueEntregaHeader(c, comp, receptor);
                        });
                    });

                    if (isHomologation)
                    {
                        col.Item().PaddingTop(6).Background(Colors.Yellow.Lighten2).Padding(4)
                            .AlignCenter().Text("AMBIENTE HOMOLOGACION - SIN VALOR FISCAL").FontSize(9).Bold();
                    }

                    // 2026-06-17 v8: la franja horizontal de contacto se eliminó — los teléfonos y el email
                    // ahora viven bajo los datos fiscales del emisor, dentro del bloque "PEDIDOS AL:".

                    // 2026-06-16 v3: el bloque Receptor se movio arriba al header (debajo del emisor)
                    // para que la tabla de productos arranque mas arriba y aproveche el espacio.
                });

                // ───── CONTENIDO (items) ─────
                page.Content().PaddingTop(10).Table(table =>
                {
                    // Tabla con orden: Cant / SKU / Producto / Formato / P.Unitario / Desc. / [IVA%] / Subtotal
                    // Letra A: muestra columna IVA% (8 columnas). Letras B/C: sin IVA% (7 columnas).
                    if (discriminaIva)
                    {
                        table.ColumnsDefinition(cols =>
                        {
                            cols.ConstantColumn(35); // Cant.
                            cols.ConstantColumn(50); // SKU
                            cols.RelativeColumn(4);  // Producto
                            cols.RelativeColumn(2);  // Formato
                            cols.ConstantColumn(60); // P. Unitario
                            cols.ConstantColumn(40); // Desc.
                            cols.ConstantColumn(38); // IVA %
                            cols.ConstantColumn(70); // Subtotal
                        });
                        table.Header(h =>
                        {
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(3).AlignRight().Text("Cant.").SemiBold();
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text("SKU").SemiBold();
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text("Producto").SemiBold();
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text("Formato").SemiBold();
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(3).AlignRight().Text("P. Unitario").SemiBold();
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(3).AlignRight().Text("Desc.").SemiBold();
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(3).AlignRight().Text("IVA %").SemiBold();
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(3).AlignRight().Text("Subtotal").SemiBold();
                        });
                        foreach (var it in comp.Items)
                        {
                            var sub = Math.Round(it.Cantidad * it.PrecioUnitario, 2, MidpointRounding.AwayFromZero);
                            var prod = it.Producto ?? it.Descripcion;
                            var fmt = it.Formato ?? "";
                            table.Cell().BorderBottom(0.3f).Padding(2).AlignRight().Text(it.Cantidad.ToString("N2", new CultureInfo("es-AR")));
                            table.Cell().BorderBottom(0.3f).Padding(2).Text(it.Sku ?? "").FontSize(8).FontColor(Colors.Blue.Darken2).SemiBold();
                            table.Cell().BorderBottom(0.3f).Padding(2).Text(prod);
                            table.Cell().BorderBottom(0.3f).Padding(2).Text(fmt).FontSize(8).FontColor(Colors.Grey.Darken1);
                            table.Cell().BorderBottom(0.3f).Padding(2).AlignRight().Text(t =>
                            {
                                if (it.DescuentoPct.HasValue && it.PrecioOriginal.HasValue)
                                {
                                    t.Span("$ " + it.PrecioOriginal.Value.ToString("N2", new CultureInfo("es-AR")))
                                        .Strikethrough().FontColor(Colors.Grey.Medium);
                                    t.Span("\n");
                                    t.Span("$ " + it.PrecioUnitario.ToString("N2", new CultureInfo("es-AR")));
                                }
                                else
                                {
                                    t.Span("$ " + it.PrecioUnitario.ToString("N2", new CultureInfo("es-AR")));
                                }
                            });
                            table.Cell().BorderBottom(0.3f).Padding(2).AlignRight().Text(
                                it.DescuentoPct.HasValue ? it.DescuentoPct.Value.ToString("0.##") + "%" : "—"
                            ).FontSize(8);
                            table.Cell().BorderBottom(0.3f).Padding(2).AlignRight().Text(it.AlicPct.ToString("0.##") + "%");
                            table.Cell().BorderBottom(0.3f).Padding(2).AlignRight().Text("$ " + sub.ToString("N2", new CultureInfo("es-AR")));
                        }
                    }
                    else
                    {
                        // B / C — sin IVA% (queda incluido en Precio U.) — 7 columnas.
                        table.ColumnsDefinition(cols =>
                        {
                            cols.ConstantColumn(35); // Cant.
                            cols.ConstantColumn(50); // SKU
                            cols.RelativeColumn(4);  // Producto
                            cols.RelativeColumn(2);  // Formato
                            cols.ConstantColumn(70); // P. Unitario (con IVA)
                            cols.ConstantColumn(40); // Desc.
                            cols.ConstantColumn(80); // Subtotal (con IVA)
                        });
                        table.Header(h =>
                        {
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(3).AlignRight().Text("Cant.").SemiBold();
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text("SKU").SemiBold();
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text("Producto").SemiBold();
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text("Formato").SemiBold();
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(3).AlignRight().Text("P. Unitario").SemiBold();
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(3).AlignRight().Text("Desc.").SemiBold();
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(3).AlignRight().Text("Subtotal").SemiBold();
                        });
                        foreach (var it in comp.Items)
                        {
                            var pct = esTipoC ? 0m : it.AlicPct;
                            var pcuConIva = Math.Round(it.PrecioUnitario * (1 + pct / 100m), 2, MidpointRounding.AwayFromZero);
                            var subConIva = Math.Round(it.Cantidad * pcuConIva, 2, MidpointRounding.AwayFromZero);
                            var pcuOrigConIva = it.PrecioOriginal.HasValue
                                ? Math.Round(it.PrecioOriginal.Value * (1 + pct / 100m), 2, MidpointRounding.AwayFromZero)
                                : 0m;
                            var prod = it.Producto ?? it.Descripcion;
                            var fmt = it.Formato ?? "";
                            table.Cell().BorderBottom(0.3f).Padding(2).AlignRight().Text(it.Cantidad.ToString("N2", new CultureInfo("es-AR")));
                            table.Cell().BorderBottom(0.3f).Padding(2).Text(it.Sku ?? "").FontSize(8).FontColor(Colors.Blue.Darken2).SemiBold();
                            table.Cell().BorderBottom(0.3f).Padding(2).Text(prod);
                            table.Cell().BorderBottom(0.3f).Padding(2).Text(fmt).FontSize(8).FontColor(Colors.Grey.Darken1);
                            table.Cell().BorderBottom(0.3f).Padding(2).AlignRight().Text(t =>
                            {
                                if (it.DescuentoPct.HasValue && it.PrecioOriginal.HasValue)
                                {
                                    t.Span("$ " + pcuOrigConIva.ToString("N2", new CultureInfo("es-AR")))
                                        .Strikethrough().FontColor(Colors.Grey.Medium);
                                    t.Span("\n");
                                    t.Span("$ " + pcuConIva.ToString("N2", new CultureInfo("es-AR")));
                                }
                                else
                                {
                                    t.Span("$ " + pcuConIva.ToString("N2", new CultureInfo("es-AR")));
                                }
                            });
                            table.Cell().BorderBottom(0.3f).Padding(2).AlignRight().Text(
                                it.DescuentoPct.HasValue ? it.DescuentoPct.Value.ToString("0.##") + "%" : "—"
                            ).FontSize(8);
                            table.Cell().BorderBottom(0.3f).Padding(2).AlignRight().Text("$ " + subConIva.ToString("N2", new CultureInfo("es-AR")));
                        }
                    }
                });

                // ───── FOOTER ─────
                page.Footer().Column(col =>
                {
                    // Totales (columna derecha)
                    col.Item().AlignRight().Column(c =>
                    {
                        if (comp.IvasDesglosados.Count > 0 && discriminaIva)
                        {
                            // Letra A: IVA discriminado por norma — Importe Neto Gravado + IVA por alícuota.
                            // Letra B/C: NO va en la columna de totales — se muestra en el bloque
                            // "Régimen de Transparencia Fiscal" al pie (estilo Contabilium).
                            c.Item().Text($"Importe Neto Gravado: $ {comp.ImpNeto.ToString("N2", new CultureInfo("es-AR"))}");
                            foreach (var iva in comp.IvasDesglosados)
                            {
                                c.Item().Text($"IVA {iva.Pct.ToString("0.##")}%: $ {iva.Importe.ToString("N2", new CultureInfo("es-AR"))}");
                            }
                        }
                        c.Item().PaddingTop(2).Text($"Importe Total: $ {comp.ImpTotal.ToString("N2", new CultureInfo("es-AR"))}").FontSize(11).Bold();
                    });

                    // Bloque "Régimen de Transparencia Fiscal al Consumidor (Ley 27.743)"
                    // SOLO aparece en Letra B (cuando el IVA va contenido en el precio).
                    // Layout estilo Contabilium: alineado a la izquierda, chico, gris, italic.
                    // Las 3 líneas son las que la Ley 27.743 obliga a mostrar a no-RI (CF, MO, EX).
                    if (!discriminaIva && comp.IvasDesglosados.Count > 0)
                    {
                        var ivaContenido = comp.IvasDesglosados.Sum(iva => iva.Importe);
                        col.Item().PaddingTop(6).AlignLeft().Column(rc =>
                        {
                            rc.Item().Text("Régimen de Transparencia Fiscal al Consumidor (Ley 27.743)")
                                .FontSize(7).Italic().FontColor(Colors.Grey.Darken1);
                            rc.Item().Text($"IVA contenido $ {ivaContenido.ToString("N2", new CultureInfo("es-AR"))}")
                                .FontSize(7).FontColor(Colors.Grey.Darken2);
                            rc.Item().Text("Otros impuestos Nacionales Indirectos $ 0,00")
                                .FontSize(7).FontColor(Colors.Grey.Darken2);
                        });
                    }

                    // 2026-06-16: el bloque DOMICILIO DE ENTREGA se movió al header (mitad derecha,
                    // debajo de "Fecha de Emisión"). Acá solo queda el concepto.

                    col.Item().PaddingTop(4).Text($"Concepto: {NombreConcepto(comp.Concepto)}").FontSize(8);

                    // 2026-06-16: franja de DATOS BANCARIOS — SOLO en facturas con CAE.
                    // El cliente la usa para hacer la transferencia. En cotizaciones tipo X no aparece.
                    if (!string.IsNullOrWhiteSpace(comp.Cae) &&
                        (!string.IsNullOrWhiteSpace(emisor.BancoNombre) ||
                         !string.IsNullOrWhiteSpace(emisor.BancoCbu) ||
                         !string.IsNullOrWhiteSpace(emisor.BancoAlias)))
                    {
                        col.Item().PaddingTop(6).Border(0.8f).BorderColor(Colors.Grey.Darken1)
                            .Background(Colors.Grey.Lighten4).Padding(5).Column(bc =>
                        {
                            bc.Item().AlignCenter().Text("DATOS PARA TRANSFERENCIA")
                                .FontSize(7).Bold().LetterSpacing(0.06f).FontColor(Colors.Grey.Darken3);
                            bc.Item().PaddingTop(2).Row(br =>
                            {
                                br.RelativeItem(1).AlignCenter().Column(c =>
                                {
                                    c.Item().AlignCenter().Text("Banco").FontSize(6).FontColor(Colors.Grey.Darken1);
                                    c.Item().AlignCenter().Text(emisor.BancoNombre ?? "—").FontSize(10).Bold();
                                });
                                br.RelativeItem(2).BorderLeft(0.4f).BorderRight(0.4f).BorderColor(Colors.Grey.Lighten1)
                                    .AlignCenter().Column(c =>
                                {
                                    c.Item().AlignCenter().Text("CBU").FontSize(6).FontColor(Colors.Grey.Darken1);
                                    c.Item().AlignCenter().Text(emisor.BancoCbu ?? "—").FontSize(10).Bold().FontFamily("Courier");
                                });
                                br.RelativeItem(1).AlignCenter().Column(c =>
                                {
                                    c.Item().AlignCenter().Text("Alias").FontSize(6).FontColor(Colors.Grey.Darken1);
                                    c.Item().AlignCenter().Text(emisor.BancoAlias ?? "—").FontSize(10).Bold();
                                });
                            });
                        });
                    }

                    col.Item().PaddingTop(4).LineHorizontal(0.5f);

                    col.Item().PaddingTop(4).Row(row =>
                    {
                        // QR
                        row.ConstantItem(75).Image(qrBytes);

                        // Centro
                        row.RelativeItem().PaddingLeft(8).Column(c =>
                        {
                            c.Item().Text("ARCA").FontSize(11).Bold();
                            c.Item().Text("AGENCIA DE RECAUDACION Y CONTROL ADUANERO").FontSize(7);
                            c.Item().PaddingTop(4).Text("Comprobante Autorizado").FontSize(9).Bold();
                            c.Item().Text("Esta Administración Federal no se responsabiliza por los datos ingresados en el detalle de la operación.").FontSize(6);
                        });

                        // Derecha — CAE
                        // 2026-06-16: el QR ENTREGA se movió al header (mitad derecha, junto al bloque de entrega).
                        row.ConstantItem(120).Column(c =>
                        {
                            c.Item().AlignRight().Text($"CAE Nº: {comp.Cae}").FontSize(9).SemiBold();
                            c.Item().AlignRight().Text($"Fecha Vto. CAE: {FormatFecha(comp.CaeVto ?? "")}").FontSize(9);
                        });
                    });

                    // 2026-06-16: dos sitios web al pie del comprobante, ancho completo.
                    RenderFooterWebs(col, emisor.Web, emisor.Web2);
                });
            });
        }).GeneratePdf();

        return pdfBytes;
    }

    // ============================================================
    // QR de ARCA (RG 4892)
    // ============================================================

    private byte[] BuildQrAfipPng(string cuitEmisor, string fechaYyyymmdd, int ptoVta, int cbteTipoNro,
        int cbteNro, decimal impTotal, int docTipoRec, string docNroRec, string cae)
    {
        var fechaIso = (fechaYyyymmdd?.Length == 8)
            ? $"{fechaYyyymmdd.Substring(0, 4)}-{fechaYyyymmdd.Substring(4, 2)}-{fechaYyyymmdd.Substring(6, 2)}"
            : DateTime.Today.ToString("yyyy-MM-dd");

        var cuitNum = long.TryParse(cuitEmisor, out var cn) ? cn : 0L;
        var caeNum = long.TryParse(cae, out var caN) ? caN : 0L;
        var docRecNum = long.TryParse(docNroRec, out var dn) ? dn : 0L;

        var payload = new
        {
            ver = 1,
            fecha = fechaIso,
            cuit = cuitNum,
            ptoVta,
            tipoCmp = cbteTipoNro,
            nroCmp = cbteNro,
            importe = impTotal,
            moneda = "PES",
            ctz = 1,
            tipoDocRec = docTipoRec,
            nroDocRec = docRecNum,
            tipoCodAut = "E",
            codAut = caeNum,
        };
        var json = JsonSerializer.Serialize(payload);
        var b64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var url = $"https://www.afip.gob.ar/fe/qr/?p={b64Url}";

        using var qrGen = new QRCodeGenerator();
        using var qrData = qrGen.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(qrData);
        return png.GetGraphic(6);
    }

    // ============================================================
    // Helpers
    // ============================================================

    public static string LetraDelTipo(int tipo) => tipo switch
    {
        1 or 2 or 3 => "A",
        6 or 7 or 8 => "B",
        11 or 12 or 13 => "C",
        51 or 52 or 53 => "M",
        _ => "?",
    };

    /// <summary>2026-06-16: franja gris ancho-completo con Tel. · email · Tel. Texto plano,
    /// sin emojis (las fuentes default de QuestPDF no soportan ✆ ni ✉ — quedan cuadraditos).</summary>
    private static void RenderFranjaContacto(QuestPDF.Fluent.ColumnDescriptor col, string? tel1, string? tel2, string? email)
    {
        if (string.IsNullOrWhiteSpace(tel1) && string.IsNullOrWhiteSpace(tel2) && string.IsNullOrWhiteSpace(email)) return;

        col.Item().PaddingTop(8).Background(Colors.Grey.Lighten3)
            .BorderTop(0.5f).BorderBottom(0.5f).BorderColor(Colors.Grey.Medium)
            .PaddingVertical(4).PaddingHorizontal(8).Row(row =>
        {
            row.RelativeItem().Text(t =>
            {
                if (!string.IsNullOrWhiteSpace(tel1))
                {
                    t.Span("Tel. ").FontSize(9).FontColor(Colors.Grey.Darken1);
                    t.Span(tel1).FontSize(11).Bold();
                }
            });
            row.RelativeItem().AlignCenter().Text(t =>
            {
                if (!string.IsNullOrWhiteSpace(email)) t.Span(email).FontSize(10);
            });
            row.RelativeItem().AlignRight().Text(t =>
            {
                if (!string.IsNullOrWhiteSpace(tel2))
                {
                    t.Span("Tel. ").FontSize(9).FontColor(Colors.Grey.Darken1);
                    t.Span(tel2).FontSize(11).Bold();
                }
            });
        });
    }

    /// <summary>2026-06-16: dos sitios web al pie del comprobante (izq / der). Si no hay datos no se muestra.</summary>
    private static void RenderFooterWebs(QuestPDF.Fluent.ColumnDescriptor col, string? web1, string? web2)
    {
        if (string.IsNullOrWhiteSpace(web1) && string.IsNullOrWhiteSpace(web2)) return;
        col.Item().PaddingTop(4).BorderTop(0.5f).BorderColor(Colors.Grey.Lighten1).PaddingTop(3).Row(row =>
        {
            row.RelativeItem().Text(t =>
            {
                if (!string.IsNullOrWhiteSpace(web1)) t.Span(web1).FontSize(10).Bold();
            });
            row.RelativeItem().AlignRight().Text(t =>
            {
                if (!string.IsNullOrWhiteSpace(web2)) t.Span(web2).FontSize(10).Bold();
            });
        });
    }

    /// <summary>2026-06-16: bloque compacto "DOMICILIO DE ENTREGA + Repartidor + Días + Pendiente + Comentarios + QR"
    /// que vive en el header (mitad derecha, debajo de "Fecha de Emisión"). Reemplaza al bloque grande de abajo.</summary>
    private static void RenderBloqueEntregaHeader(QuestPDF.Fluent.ColumnDescriptor col, PdfComprobante comp, PdfReceptor receptor)
    {
        var domicilio = !string.IsNullOrWhiteSpace(comp.DomicilioEntrega)
            ? comp.DomicilioEntrega
            : receptor.Domicilio;
        var tieneEntrega = !string.IsNullOrWhiteSpace(domicilio);
        var tieneQr = comp.QrRepartidorBytes is not null;
        if (!tieneEntrega && !tieneQr) return;

        var diasActivos = (comp.DiasVisita ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().ToUpperInvariant())
            .ToHashSet();
        var diasDelComp = new List<string> { "LUN", "MAR", "MIE", "JUE", "VIE", "SAB" };
        if (diasActivos.Contains("DOM")) diasDelComp.Add("DOM");

        col.Item().PaddingTop(6).Background(Colors.Grey.Lighten4).Border(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(5).Row(r =>
        {
            // Izquierda: domicilio + chips compactos (sin emojis ni chips de día — no entran con QR grande)
            r.RelativeItem().Column(cc =>
            {
                // 2026-06-17 v7: fuentes más grandes — la dirección estaba ilegible (8pt).
                cc.Item().Text("DOMICILIO DE ENTREGA").FontSize(8).Bold().FontColor(Colors.Grey.Darken2).LetterSpacing(0.05f);
                if (tieneEntrega) cc.Item().PaddingTop(3).Text(domicilio!).FontSize(11).Bold();

                // 2026-07-01: linea con dias de visita — se veia solo el domicilio, no que dia entrega.
                var diasMostrar = diasDelComp.Where(d => diasActivos.Contains(d)).ToList();
                if (diasMostrar.Count > 0)
                {
                    cc.Item().PaddingTop(3).Text(t =>
                    {
                        t.Span("Días: ").FontSize(8).Bold().FontColor(Colors.Grey.Darken2);
                        t.Span(string.Join(" · ", diasMostrar)).FontSize(9).Bold().FontColor(Colors.Blue.Darken2);
                    });
                }

                cc.Item().PaddingTop(4).Row(lineRow =>
                {
                    if (comp.Retira)
                    {
                        lineRow.AutoItem().AlignMiddle().PaddingRight(4).Background(Colors.Green.Lighten4)
                            .Border(0.5f).BorderColor(Colors.Green.Lighten1).Padding(3)
                            .Text("RETIRA").Bold().FontSize(8).FontColor(Colors.Green.Darken3);
                    }
                    else if (comp.EnRadar)
                    {
                        lineRow.AutoItem().AlignMiddle().PaddingRight(4).Background(Colors.Blue.Lighten4)
                            .Border(0.5f).BorderColor(Colors.Blue.Lighten1).Padding(3)
                            .Text("EN RADAR").Bold().FontSize(8).FontColor(Colors.Blue.Darken3);
                    }

                    if (comp.IsPaid)
                    {
                        lineRow.AutoItem().AlignMiddle().Background(Colors.Green.Lighten4)
                            .Border(0.5f).BorderColor(Colors.Green.Lighten1).Padding(3)
                            .Text("PAGADA").Bold().FontSize(8).FontColor(Colors.Green.Darken3);
                    }
                    else
                    {
                        lineRow.AutoItem().AlignMiddle().Background(Colors.Yellow.Lighten4)
                            .Border(0.5f).BorderColor(Colors.Yellow.Darken1).Padding(3)
                            .Text("PENDIENTE").Bold().FontSize(8).FontColor(Colors.Orange.Darken3);
                    }
                });
            });

            // Derecha: QR ENTREGA GRANDE (140px ≈ 3× del anterior 65px)
            if (tieneQr)
            {
                r.ConstantItem(150).PaddingLeft(6).Column(qc =>
                {
                    qc.Item().AlignCenter().Width(140).Image(comp.QrRepartidorBytes!);
                    qc.Item().AlignCenter().PaddingTop(2).Text("QR ENTREGA").FontSize(8).Bold().FontColor(Colors.Grey.Darken3);
                });
            }
        });
    }

    private static string FormatCuit(string cuit)
    {
        if (cuit?.Length == 11)
            return $"{cuit.Substring(0, 2)}-{cuit.Substring(2, 8)}-{cuit.Substring(10)}";
        return cuit ?? "";
    }

    private static string FormatFecha(string yyyymmdd)
    {
        if (string.IsNullOrEmpty(yyyymmdd)) return "";
        if (yyyymmdd.Length == 8 && yyyymmdd.All(char.IsDigit))
            return $"{yyyymmdd.Substring(6, 2)}/{yyyymmdd.Substring(4, 2)}/{yyyymmdd.Substring(0, 4)}";
        if (DateTime.TryParse(yyyymmdd, out var d)) return d.ToString("dd/MM/yyyy");
        return yyyymmdd;
    }

    private static string NombreDocTipo(int t) => t switch
    {
        80 => "CUIT",
        96 => "DNI",
        99 => "Consumidor Final",
        _ => $"Doc tipo {t}",
    };

    private static string NombreCondicionIva(int id) => id switch
    {
        1 => "Responsable Inscripto",
        4 => "Sujeto Exento",
        5 => "Consumidor Final",
        6 => "Responsable Monotributo",
        7 => "Sujeto No Categorizado",
        8 => "Proveedor del Exterior",
        9 => "Cliente del Exterior",
        10 => "IVA Liberado Ley 19.640",
        13 => "Monotributista Social",
        15 => "IVA No Alcanzado",
        16 => "Monotributo Trabajador Independiente Promovido",
        _ => $"Cond. {id}",
    };

    private static string NombreConcepto(int c) => c switch
    {
        1 => "Productos",
        2 => "Servicios",
        3 => "Productos y Servicios",
        _ => $"Concepto {c}",
    };

    private static string LabelCondicionPago(string c) => c.ToUpperInvariant() switch
    {
        "EFECTIVO" => "Efectivo",
        "TRANSFERENCIA" => "Transferencia bancaria",
        "MERCADOPAGO" => "Mercado Pago",
        "CHEQUE" => "Cheque",
        "CTA_CORRIENTE" => "Cuenta corriente",
        "V1" => "Pago V1",
        "V2" => "Pago V2",
        _ => c,
    };
}

// ============================================================
// Models para el PDF (estructura plana, no se persiste)
// ============================================================

public class PdfEmisor
{
    public string Cuit { get; set; } = "";
    public string RazonSocial { get; set; } = "";
    public string CondicionIva { get; set; } = "Responsable Inscripto";
    public string? Domicilio { get; set; }
    /// <summary>Tipo de IIBB: "CM" (Convenio Multilateral) o "Local".</summary>
    public string? IIBBTipo { get; set; }
    public string? IIBBNumero { get; set; }
    public DateTime? InicioActividades { get; set; }
    /// <summary>Bytes del logo (PNG/JPG/WEBP). Null si no hay logo.</summary>
    public byte[]? LogoBytes { get; set; }

    // 2026-06-16: contacto + datos bancarios (franja del PDF de factura).
    public string? Telefono { get; set; }
    public string? Telefono2 { get; set; }
    public string? Email { get; set; }
    public string? Web { get; set; }
    public string? Web2 { get; set; }
    public string? BancoNombre { get; set; }
    public string? BancoCbu { get; set; }
    public string? BancoAlias { get; set; }
}

public class PdfReceptor
{
    public int DocTipo { get; set; } = 99;
    public string? DocNro { get; set; }
    public string? Nombre { get; set; }
    public string? Domicilio { get; set; }
    public int CondicionIvaId { get; set; } = 5;
    /// <summary>Texto legible para la forma/condición de pago (ej. "Efectivo", "Transferencia").
    /// Si está vacío, no se muestra en el PDF.</summary>
    public string? CondicionVenta { get; set; }
}

public class PdfComprobante
{
    public int CbteTipoNro { get; set; }
    public string CbteTipoNombre { get; set; } = "";
    public int PtoVta { get; set; }
    public int CbteNro { get; set; }
    /// <summary>2026-06-16: numero interno del sistema (CAFE-2026-XXXX). Se muestra abajo del numero
    /// oficial ARCA como "Ref. interna" para que el operador pueda correlacionar ambos.</summary>
    public string? NumeroInterno { get; set; }
    public string Fecha { get; set; } = ""; // yyyymmdd
    public int Concepto { get; set; } = 1;
    public List<PdfItem> Items { get; set; } = new();
    public List<PdfIvaDesglose> IvasDesglosados { get; set; } = new();
    public decimal ImpNeto { get; set; }
    public decimal ImpTotal { get; set; }
    public string? Cae { get; set; }
    public string? CaeVto { get; set; } // yyyymmdd
    /// <summary>2026-06-12: QR de entrega del repartidor (el mismo de las cotizaciones).
    /// Si viene null no se muestra. NO confundir con el QR fiscal de ARCA.</summary>
    public byte[]? QrRepartidorBytes { get; set; }
    // ---- Extras de UX (de la cotización portados al ARCA, manteniendo prolijidad fiscal) ----
    /// <summary>Si la venta está marcada como pagada, se muestra un sello "PAGADO" discreto.</summary>
    public bool IsPaid { get; set; }
    /// <summary>Etiqueta del tipo de cliente (BAR / OTRO). Si null no se muestra.</summary>
    public string? TipoClienteTag { get; set; }
    /// <summary>Días de visita/reparto (LUN/MAR/...) como CSV. Si null no se muestra.</summary>
    public string? DiasVisita { get; set; }
    /// <summary>"EN RADAR" — uso interno: cuando estemos por la zona. Si está en true,
    /// el footer reemplaza los pills de días por "a coordinar". El cliente NUNCA ve "EN RADAR" textual.</summary>
    public bool EnRadar { get; set; }

    /// <summary>2026-06-01: RETIRA — el cliente retira la mercaderia en el local. Si true, en el
    /// footer del PDF se muestra "🚗 RETIRA EN LOCAL" en lugar de los pills de días.</summary>
    public bool Retira { get; set; }
    /// <summary>Quien entrega la venta (Gabriel, Nacho, Maxi, Alexis, Miguel, Rodrigo, o
    /// 'Logistica tercerizada'). Si esta seteado se muestra en el bloque DOMICILIO DE ENTREGA.</summary>
    public string? EntregaPor { get; set; }
    /// <summary>Comentarios para el comprobante cargados en la ficha del cliente.</summary>
    public string? ComentariosCliente { get; set; }
    /// <summary>Observaciones internas de la venta. Si null no se muestra.</summary>
    public string? Observaciones { get; set; }
    /// <summary>Condición de pago: EFECTIVO / TRANSFERENCIA / MERCADOPAGO / CHEQUE / CTA_CORRIENTE / V*.
    /// Se imprime con un label legible bajo el total.</summary>
    public string? CondicionPago { get; set; }
    /// <summary>Domicilio de entrega del cliente (distinto al fiscal). Se imprime destacado
    /// abajo del comprobante para que el chofer/repartidor lo vea facil.</summary>
    public string? DomicilioEntrega { get; set; }
}

public class PdfItem
{
    /// <summary>Descripción full (legacy): "Café Brasil Selección — EN GRANOS · 1KG".
    /// Se usa como fallback si Sku/Producto/Formato no están seteados.</summary>
    public string Descripcion { get; set; } = "";
    /// <summary>SKU del producto (ej "F1", "C8733BL"). Si null/vacío, no se muestra en columna SKU.</summary>
    public string? Sku { get; set; }
    /// <summary>Nombre del producto separado (ej "Café Brasil Selección"). Si null usa Descripcion.</summary>
    public string? Producto { get; set; }
    /// <summary>Formato del producto separado (ej "EN GRANOS · 1KG"). Si null queda vacío.</summary>
    public string? Formato { get; set; }
    public decimal Cantidad { get; set; }
    /// <summary>Precio unitario FINAL (con descuento ya aplicado si lo hay).</summary>
    public decimal PrecioUnitario { get; set; }
    public decimal AlicPct { get; set; }
    /// <summary>Si el item tiene descuento de linea, este es el precio ORIGINAL sin descuento.
    /// Null si no hay descuento. Usado por el PDF para mostrar el precio tachado.</summary>
    public decimal? PrecioOriginal { get; set; }
    /// <summary>Porcentaje de descuento aplicado a esta linea (0-100). Null si no hay.</summary>
    public decimal? DescuentoPct { get; set; }
}

public class PdfIvaDesglose
{
    public decimal Pct { get; set; }
    public decimal Importe { get; set; }
}
