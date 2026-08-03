using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Api.Services;

/// <summary>
/// 2026-08-03: Datos que necesita el rotulo/etiqueta de transporte. Se arma en el controller
/// desde la venta + transporte + cliente + remitente. Todos los campos opcionales pueden venir
/// null; el servicio los muestra como "-" o deja la celda vacia (nunca crashea).
/// </summary>
public record RotuloData(
    // Empresa de transporte (bloque superior)
    string TransporteNombre, string? TransporteDireccion, string? TransporteTelefono,
    string? TransporteLocalidad, string? TransporteProvincia, string? TransporteCp, bool PagoDestino,
    // Destinatario (el cliente que recibe)
    string DestNombre, string? DestDniCuit, string? DestTelefono, string? DestDireccion,
    string? DestLocalidad, string? DestProvincia, string? DestCp,
    string? UsuarioMl, string? NotaProducto,   // linea "USUARIO ML : xxx (nota)" — puede ser null
    // Remitente (quien envia)
    string RemNombre, string? RemCuit, string? RemFantasia, string? RemDireccion,
    string? RemTelefono, string? RemLocalidad, string? RemProvincia, string? RemCp,
    // Pie
    bool EsFragil, int? CantidadBultos,
    byte[]? LogoBytes   // logo opcional arriba a la derecha; si es null, no dibujar logo
);

/// <summary>
/// 2026-08-03: Generador puro del PDF de rotulo/etiqueta de transporte.
/// Dos formatos: A4 vertical (GenerarA4) y etiqueta termica 10x15 cm para impresora Zebra (GenerarTermica).
/// Mismo contenido (transporte / destinatario / remitente / pie), el termico compactado.
/// No toca la base de datos: recibe un RotuloData ya armado y devuelve byte[].
/// </summary>
public class CafeRotuloPdfService
{
    // Azul de los encabezados de tabla (DESTINATARIO / REMITENTE)
    private static readonly string HeaderAzul = Colors.Blue.Darken2;

    static CafeRotuloPdfService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    /// <summary>Muestra el valor, o "-" si viene null/vacio.</summary>
    private static string V(string? s) => string.IsNullOrWhiteSpace(s) ? "-" : s.Trim();

    // ─────────────────────────────────────────────────────────────────────────
    //  A4
    // ─────────────────────────────────────────────────────────────────────────
    public byte[] GenerarA4(RotuloData data)
    {
        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.2f, Unit.Centimetre);
                page.DefaultTextStyle(t => t.FontSize(11).FontFamily("Helvetica"));

                page.Content().Column(col =>
                {
                    RenderBloqueTransporte(col, data, titulo: 12, cuerpo: 11, logoW: 130);
                    col.Item().PaddingTop(10).Element(e => RenderDestinatario(e, data, header: 13, label: 11, valor: 12));
                    col.Item().PaddingTop(10).Element(e => RenderRemitente(e, data, header: 13, label: 11, valor: 12));
                    col.Item().PaddingTop(16).Element(e => RenderPie(e, data, fragil: 46, bultos: 16));
                });
            });
        });
        return doc.GeneratePdf();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  TERMICA 10 x 15 cm (Zebra)
    // ─────────────────────────────────────────────────────────────────────────
    public byte[] GenerarTermica(RotuloData data)
    {
        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                // 10 cm de ancho x 15 cm de alto
                page.Size(new PageSize(10f, 15f, Unit.Centimetre));
                page.Margin(4, Unit.Millimetre);
                page.DefaultTextStyle(t => t.FontSize(7).FontFamily("Helvetica"));

                page.Content().Column(col =>
                {
                    RenderBloqueTransporte(col, data, titulo: 7, cuerpo: 7, logoW: 55);
                    col.Item().PaddingTop(4).Element(e => RenderDestinatario(e, data, header: 8, label: 7, valor: 7));
                    col.Item().PaddingTop(4).Element(e => RenderRemitente(e, data, header: 8, label: 7, valor: 7));
                    col.Item().PaddingTop(6).Element(e => RenderPie(e, data, fragil: 22, bultos: 9));
                });
            });
        });
        return doc.GeneratePdf();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Bloque superior: empresa de transporte (izq) + logo (der)
    // ─────────────────────────────────────────────────────────────────────────
    private static void RenderBloqueTransporte(QuestPDF.Fluent.ColumnDescriptor col, RotuloData d,
        float titulo, float cuerpo, float logoW)
    {
        col.Item().Row(row =>
        {
            // Izquierda: datos del transporte, centrado
            row.RelativeItem().AlignCenter().Column(c =>
            {
                c.Item().AlignCenter().Text("Empresa de transporte:").FontSize(titulo).Bold();
                c.Item().AlignCenter().Text(V(d.TransporteNombre)).FontSize(titulo + 1).Bold();
                if (d.PagoDestino)
                    c.Item().AlignCenter().Text("Pago destino").FontSize(cuerpo).Bold().FontColor(Colors.Red.Darken2);

                var locProv = string.Join(" – ", new[] { d.TransporteLocalidad, d.TransporteProvincia }
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
                if (!string.IsNullOrWhiteSpace(locProv))
                    c.Item().AlignCenter().Text(locProv).FontSize(cuerpo);

                var dirTel = string.Join("    ", new[] { d.TransporteDireccion, d.TransporteTelefono }
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
                if (!string.IsNullOrWhiteSpace(dirTel))
                    c.Item().PaddingTop(3).AlignCenter().Border(0.8f).BorderColor(Colors.Grey.Darken1)
                        .Padding(3).Text(dirTel).FontSize(cuerpo);
                if (!string.IsNullOrWhiteSpace(d.TransporteCp))
                    c.Item().AlignCenter().Text($"CP: {d.TransporteCp}").FontSize(cuerpo);
            });

            // Derecha: logo (si viene)
            if (d.LogoBytes is not null && d.LogoBytes.Length > 0)
            {
                row.ConstantItem(logoW).AlignRight().AlignTop().Image(d.LogoBytes).FitArea();
            }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Tabla DESTINATARIO (encabezado azul/blanco)
    // ─────────────────────────────────────────────────────────────────────────
    private static void RenderDestinatario(IContainer cont, RotuloData d, float header, float label, float valor)
    {
        cont.Border(1).BorderColor(HeaderAzul).Column(col =>
        {
            col.Item().Background(HeaderAzul).Padding(4).AlignCenter()
                .Text("DESTINATARIO").FontSize(header).Bold().FontColor(Colors.White);

            Fila(col, "Nombre", V(d.DestNombre), label, valor, valorBold: true);
            Fila(col, "Dni-cuit", V(d.DestDniCuit), label, valor);
            Fila(col, "Telefono", V(d.DestTelefono), label, valor);
            Fila(col, "Dirección", V(d.DestDireccion), label, valor);
            FilaDoble(col, "Provincia", V(d.DestProvincia), "Localidad", V(d.DestLocalidad), label, valor);
            Fila(col, "C POSTAL", V(d.DestCp), label, valor);

            if (!string.IsNullOrWhiteSpace(d.UsuarioMl))
            {
                var nota = string.IsNullOrWhiteSpace(d.NotaProducto) ? "" : $" ({d.NotaProducto})";
                col.Item().BorderTop(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(4)
                    .Text($"USUARIO ML : {d.UsuarioMl}{nota}").FontSize(valor).Bold();
            }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Tabla REMITENTE (encabezado azul/blanco)
    // ─────────────────────────────────────────────────────────────────────────
    private static void RenderRemitente(IContainer cont, RotuloData d, float header, float label, float valor)
    {
        cont.Border(1).BorderColor(HeaderAzul).Column(col =>
        {
            col.Item().Background(HeaderAzul).Padding(4).AlignCenter()
                .Text("REMITENTE").FontSize(header).Bold().FontColor(Colors.White);

            Fila(col, "Nombre", V(d.RemNombre), label, valor, valorBold: true);
            Fila(col, "Empresa", V(d.RemFantasia), label, valor);
            Fila(col, "Direccion", V(d.RemDireccion), label, valor);
            Fila(col, "Telefono", V(d.RemTelefono), label, valor);
            FilaDoble(col, "Codigo Postal", V(d.RemCp), "Localidad", V(d.RemLocalidad), label, valor);
            FilaDoble(col, "Provincia", V(d.RemProvincia), "Pais", "ARGENTINA", label, valor);
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Pie: FRAGIL grande + cantidad de bultos
    // ─────────────────────────────────────────────────────────────────────────
    private static void RenderPie(IContainer cont, RotuloData d, float fragil, float bultos)
    {
        cont.Column(col =>
        {
            if (d.EsFragil)
                col.Item().AlignCenter().Text("FRAGIL").FontSize(fragil).Bold().FontColor(Colors.Red.Darken2);
            if (d.CantidadBultos.HasValue)
                col.Item().PaddingTop(4).AlignCenter().Text($"Cant Bultos: {d.CantidadBultos.Value}").FontSize(bultos).Bold();
        });
    }

    // ── Fila etiqueta (izq, negrita) + valor (der) ──
    private static void Fila(QuestPDF.Fluent.ColumnDescriptor col, string label, string valor,
        float labelSize, float valorSize, bool valorBold = false)
    {
        col.Item().BorderTop(0.5f).BorderColor(Colors.Grey.Lighten1).PaddingVertical(2).PaddingHorizontal(4).Row(r =>
        {
            r.ConstantItem(labelSize * 7f).Text($"{label}:").FontSize(labelSize).Bold();
            r.RelativeItem().Text(t =>
            {
                var span = t.Span(valor).FontSize(valorSize);
                if (valorBold) span.Bold();
            });
        });
    }

    // ── Fila con dos pares etiqueta/valor (izquierda y derecha) ──
    private static void FilaDoble(QuestPDF.Fluent.ColumnDescriptor col, string label1, string valor1,
        string label2, string valor2, float labelSize, float valorSize)
    {
        col.Item().BorderTop(0.5f).BorderColor(Colors.Grey.Lighten1).PaddingVertical(2).PaddingHorizontal(4).Row(r =>
        {
            r.RelativeItem().Row(rr =>
            {
                rr.ConstantItem(labelSize * 7f).Text($"{label1}:").FontSize(labelSize).Bold();
                rr.RelativeItem().Text(valor1).FontSize(valorSize);
            });
            r.RelativeItem().Row(rr =>
            {
                rr.ConstantItem(labelSize * 6f).Text($"{label2}:").FontSize(labelSize).Bold();
                rr.RelativeItem().Text(valor2).FontSize(valorSize);
            });
        });
    }
}
