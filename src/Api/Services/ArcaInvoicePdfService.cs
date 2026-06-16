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
                page.Header().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        // Izquierda — emisor (con logo arriba si tiene)
                        row.RelativeItem().Column(c =>
                        {
                            if (emisor.LogoBytes is not null && emisor.LogoBytes.Length > 0)
                            {
                                c.Item().Height(50).AlignLeft().Image(emisor.LogoBytes).FitHeight();
                            }
                            c.Item().PaddingTop(4).Text("ORIGINAL").FontSize(8).SemiBold();
                            c.Item().Text(emisor.RazonSocial).FontSize(13).Bold();
                            c.Item().Text($"CUIT: {FormatCuit(emisor.Cuit)}").FontSize(9);
                            c.Item().Text($"Condición IVA: {emisor.CondicionIva}").FontSize(9);
                            if (!string.IsNullOrEmpty(emisor.Domicilio))
                                c.Item().Text(emisor.Domicilio!).FontSize(9);
                            if (!string.IsNullOrEmpty(emisor.IIBBNumero))
                            {
                                var label = string.IsNullOrEmpty(emisor.IIBBTipo) ? "IIBB" : $"IIBB {emisor.IIBBTipo}";
                                c.Item().Text($"{label}: {emisor.IIBBNumero}").FontSize(9);
                            }
                            if (emisor.InicioActividades.HasValue)
                            {
                                c.Item().Text($"Inicio de actividades: {emisor.InicioActividades.Value:dd/MM/yyyy}").FontSize(9);
                            }
                        });

                        // Centro — letra grande con borde
                        row.ConstantItem(80).Border(2).Padding(4).Column(c =>
                        {
                            c.Item().AlignCenter().Text(letra).FontSize(40).Bold();
                            c.Item().AlignCenter().Text($"COD. {comp.CbteTipoNro:00}").FontSize(8);
                        });

                        // Derecha — tipo + numeración + fecha + sello PAGADO + bloque entrega + QR.
                        // 2026-06-16: el bloque DOMICILIO DE ENTREGA + QR ahora vive ACÁ (mitad derecha del header)
                        // en lugar de abajo, para ganar espacio para items y agrupar la info de despacho.
                        // PaddingLeft(10) en la columna derecha para que el bloque gris NO toque el cuadro "A · COD.01".
                        row.RelativeItem().PaddingLeft(10).Column(c =>
                        {
                            c.Item().AlignRight().Text(nombreTipo).FontSize(13).Bold();
                            c.Item().AlignRight().Text($"Punto de Venta: {comp.PtoVta:00000}   Comp. Nro: {comp.CbteNro:00000000}").FontSize(9);
                            c.Item().AlignRight().Text($"Fecha de Emisión: {FormatFecha(comp.Fecha)}").FontSize(9);
                            if (comp.IsPaid)
                            {
                                c.Item().PaddingTop(4).AlignRight().Row(r =>
                                {
                                    r.AutoItem().Background(Colors.Green.Lighten4).Border(1).BorderColor(Colors.Green.Darken1)
                                        .Padding(3).Text("✓ PAGADO").FontSize(9).Bold().FontColor(Colors.Green.Darken3);
                                });
                            }
                            // Bloque entrega + QR debajo de "Fecha de Emisión".
                            RenderBloqueEntregaHeader(c, comp, receptor);
                        });
                    });

                    if (isHomologation)
                    {
                        col.Item().PaddingTop(6).Background(Colors.Yellow.Lighten2).Padding(4)
                            .AlignCenter().Text("AMBIENTE HOMOLOGACION - SIN VALOR FISCAL").FontSize(9).Bold();
                    }

                    // Receptor
                    col.Item().PaddingTop(8).BorderTop(0.5f).PaddingTop(6).Column(c =>
                    {
                        c.Item().Row(r =>
                        {
                            r.RelativeItem().Text(t =>
                            {
                                t.Span("Receptor: ").SemiBold();
                                t.Span(receptor.Nombre ?? "—");
                                if (!string.IsNullOrWhiteSpace(comp.TipoClienteTag))
                                {
                                    var tag = comp.TipoClienteTag!.Trim().ToUpperInvariant();
                                    var tagLabel = tag == "BAR" ? " · BAR" : (tag == "OTRO" ? "" : $" · {tag}");
                                    if (!string.IsNullOrEmpty(tagLabel))
                                        t.Span(tagLabel).FontSize(8).FontColor(Colors.Grey.Darken1);
                                }
                            });
                            r.RelativeItem().AlignRight().Text(t =>
                            {
                                t.Span($"{NombreDocTipo(receptor.DocTipo)}: ").SemiBold();
                                // CUIT con guiones; otros docs van crudos
                                t.Span(receptor.DocTipo == 80 ? FormatCuit(receptor.DocNro) : (receptor.DocNro ?? "—"));
                            });
                        });
                        if (!string.IsNullOrEmpty(receptor.Domicilio))
                            c.Item().Text($"Domicilio: {receptor.Domicilio}").FontSize(9);
                        c.Item().Text(t =>
                        {
                            t.Span("Condición IVA: ").SemiBold();
                            t.Span(NombreCondicionIva(receptor.CondicionIvaId));
                        });
                        if (!string.IsNullOrWhiteSpace(receptor.CondicionVenta))
                        {
                            c.Item().Text(t =>
                            {
                                t.Span("Forma de pago: ").SemiBold();
                                t.Span(receptor.CondicionVenta!);
                            });
                        }
                        // Días de visita ya NO se muestra acá — vive abajo en el bloque
                        // DOMICILIO DE ENTREGA junto con los pills LUN-DOM (más prolijo y único lugar).
                        // Comentarios del cliente para el comprobante (notas del cliente que aparecen en TODOS sus comprobantes)
                        if (!string.IsNullOrWhiteSpace(comp.ComentariosCliente))
                        {
                            c.Item().PaddingTop(3).BorderLeft(2).BorderColor(Colors.Orange.Darken1)
                                .Background(Colors.Orange.Lighten5).Padding(3)
                                .Text(comp.ComentariosCliente!).FontSize(8).Italic().FontColor(Colors.Grey.Darken2);
                        }
                        // Observaciones de esta venta (notas internas que el operador puso en la venta)
                        if (!string.IsNullOrWhiteSpace(comp.Observaciones))
                        {
                            c.Item().PaddingTop(2).Text(t =>
                            {
                                t.Span("Observaciones: ").SemiBold().FontSize(8);
                                t.Span(comp.Observaciones!).FontSize(8);
                            });
                        }
                    });
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
            // Izquierda: domicilio + repartidor + días + estado + comentarios
            r.RelativeItem().Column(cc =>
            {
                cc.Item().Text("DOMICILIO DE ENTREGA").FontSize(6).Bold().FontColor(Colors.Grey.Darken2).LetterSpacing(0.05f);
                if (tieneEntrega) cc.Item().Text(domicilio!).FontSize(9).Bold();

                // Línea horizontal: Repartidor + Días + estado
                cc.Item().PaddingTop(3).Row(lineRow =>
                {
                    if (!string.IsNullOrWhiteSpace(comp.EntregaPor))
                    {
                        lineRow.AutoItem().AlignMiddle().PaddingRight(6).Text(t =>
                        {
                            t.Span("Repartidor: ").FontSize(7).FontColor(Colors.Grey.Darken1);
                            t.Span(comp.EntregaPor!).Bold().FontSize(8).FontColor(Colors.Grey.Darken4);
                        });
                    }

                    if (comp.Retira)
                    {
                        lineRow.AutoItem().AlignMiddle().Text("🚗 RETIRA").Bold().FontSize(7).FontColor(Colors.Green.Darken3);
                    }
                    else if (comp.EnRadar)
                    {
                        lineRow.AutoItem().AlignMiddle().Text("🛰 EN RADAR").Bold().FontSize(7).FontColor(Colors.Blue.Darken3);
                    }
                    else if (diasActivos.Count > 0)
                    {
                        lineRow.AutoItem().AlignMiddle().PaddingRight(3).Text("Días:").FontSize(6).FontColor(Colors.Grey.Darken1);
                        foreach (var d in diasDelComp)
                        {
                            var on = diasActivos.Contains(d);
                            var bg = on ? Colors.Blue.Lighten5 : Colors.White;
                            var fg = on ? Colors.Blue.Darken3 : Colors.Grey.Darken1;
                            var bd = on ? Colors.Blue.Darken2 : Colors.Grey.Lighten1;
                            var bw = on ? 1f : 0.5f;
                            lineRow.ConstantItem(13).PaddingHorizontal(0.5f).Border(bw).BorderColor(bd)
                                .Background(bg).AlignCenter().AlignMiddle()
                                .Text(d.Substring(0, 1)).Bold().FontSize(6).FontColor(fg);
                        }
                    }

                    // Sello PAGADA/PENDIENTE compacto al final de la línea
                    if (comp.IsPaid)
                    {
                        lineRow.AutoItem().PaddingLeft(6).AlignMiddle().Background(Colors.Green.Lighten4)
                            .Border(0.5f).BorderColor(Colors.Green.Lighten1).Padding(2)
                            .Text("✓ PAGADA").Bold().FontSize(6).FontColor(Colors.Green.Darken3);
                    }
                    else
                    {
                        lineRow.AutoItem().PaddingLeft(6).AlignMiddle().Background(Colors.Yellow.Lighten4)
                            .Border(0.5f).BorderColor(Colors.Yellow.Darken1).Padding(2)
                            .Text("⧗ PENDIENTE").Bold().FontSize(6).FontColor(Colors.Orange.Darken3);
                    }
                });

                // Comentarios (Observaciones de la venta) — debajo, separados por línea punteada
                if (!string.IsNullOrWhiteSpace(comp.Observaciones))
                {
                    cc.Item().PaddingTop(4).BorderTop(0.5f).BorderColor(Colors.Grey.Lighten1).PaddingTop(3)
                        .Text(t =>
                        {
                            t.Span("Comentarios: ").FontSize(6).Bold().FontColor(Colors.Grey.Darken2).LetterSpacing(0.05f);
                            t.Span(comp.Observaciones!).FontSize(7).FontColor(Colors.Grey.Darken3);
                        });
                }
            });

            // Derecha: QR ENTREGA
            if (tieneQr)
            {
                r.ConstantItem(75).PaddingLeft(6).Column(qc =>
                {
                    qc.Item().AlignCenter().Width(65).Image(comp.QrRepartidorBytes!);
                    qc.Item().AlignCenter().Text("QR ENTREGA").FontSize(6).FontColor(Colors.Grey.Darken1);
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
