using Api.Data;
using Api.DTOs;
using Api.Models;
using Api.Services;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/cafe/listas-precios")]
[Authorize]
public class CafeListasPreciosController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly CafeListaPreciosPdfService _pdfSvc;
    public CafeListasPreciosController(AppDbContext db, CafeListaPreciosPdfService pdfSvc)
    {
        _db = db;
        _pdfSvc = pdfSvc;
    }

    /// <summary>Genera la previsualizacion de la lista de precios. Usa CafePricingService para
    /// calcular los precios — NO duplica logica de pricing.</summary>
    [HttpPost("preview")]
    public async Task<IActionResult> Preview([FromBody] CafeListaPreciosFiltroRequest req)
    {
        var (preview, _) = await BuildPreviewAsync(req);
        return Ok(preview);
    }

    /// <summary>Exporta la lista de precios a Excel (.xlsx). Misma data que el preview.</summary>
    [HttpPost("export-excel")]
    public async Task<IActionResult> ExportExcel([FromBody] CafeListaPreciosFiltroRequest req)
    {
        var (preview, _) = await BuildPreviewAsync(req);
        var bytes = BuildExcel(preview);
        var clienteSlug = preview.Cliente?.Nombre is string n ? Slug(n) : "general";
        var filename = $"lista-precios-{clienteSlug}-{preview.Fecha:yyyyMMdd}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", filename);
    }

    /// <summary>Exporta la lista de precios a PDF (PDF/1.7 estandar generado con QuestPDF).
    /// Reemplaza el flujo viejo de "imprimir HTML desde el browser" que generaba PDFs no
    /// estandar que algunos lectores moviles no abrian.</summary>
    [HttpPost("export-pdf")]
    public async Task<IActionResult> ExportPdf([FromBody] CafeListaPreciosFiltroRequest req)
    {
        var (preview, _) = await BuildPreviewAsync(req);
        var bytes = _pdfSvc.GenerarPdf(preview);
        var clienteSlug = preview.Cliente?.Nombre is string n ? Slug(n) : "general";
        var tipoSlug = preview.TipoCliente.ToLowerInvariant();
        var filename = $"lista-precios-{tipoSlug}-{clienteSlug}-{preview.Fecha:yyyyMMdd}.pdf";
        return File(bytes, "application/pdf", filename);
    }

    // ============================================================
    // Build preview (compartido por preview JSON y export Excel)
    // ============================================================

    private async Task<(CafeListaPreciosPreviewDto, CafeSetting)> BuildPreviewAsync(CafeListaPreciosFiltroRequest req)
    {
        var settings = await _db.CafeSettings.FindAsync(1) ?? new CafeSetting { Id = 1 };
        var negocio = new CafeListaPreciosNegocioDto(
            settings.NegocioNombre, settings.NegocioTelefono,
            settings.NegocioWhatsappNumero, settings.NegocioDireccion, settings.NegocioCuit,
            settings.NegocioEmail, settings.NegocioWeb, settings.NegocioLogoUrl);

        // Cliente y tipo
        CafeListaPreciosClienteDto? clienteDto = null;
        string tipo = "OTRO";
        if (req.ClienteId.HasValue && req.ClienteId.Value > 0)
        {
            var c = await _db.CafeClientes.FindAsync(req.ClienteId.Value);
            if (c is not null)
            {
                tipo = CafePricingService.ResolverTipo(c.Tipo);
                clienteDto = new CafeListaPreciosClienteDto(c.Id, c.Codigo, c.Nombre, tipo, c.Telefono, c.Email);
            }
        }
        else
        {
            tipo = CafePricingService.ResolverTipo(req.Tipo);
        }

        // Productos: filtrar por marcas, categoria, activos.
        var pq = _db.CafeProductos.Include(p => p.MarcaNav).Where(p => p.IsActive).AsQueryable();
        if (req.MarcaIds is not null && req.MarcaIds.Count > 0)
            pq = pq.Where(p => p.MarcaId != null && req.MarcaIds.Contains(p.MarcaId.Value));
        if (!string.IsNullOrWhiteSpace(req.Categoria))
        {
            var cat = req.Categoria.Trim().ToUpperInvariant();
            if (cat == "CAFE" || cat == "OTROS") pq = pq.Where(p => p.Categoria == cat);
        }
        var productos = await pq.OrderBy(p => p.Nombre).ToListAsync();

        // Cargar marcas con ProveedorNav para nombre del proveedor (si la marca esta linkeada)
        var marcaIds = productos.Where(p => p.MarcaId.HasValue).Select(p => p.MarcaId!.Value).Distinct().ToList();
        var marcas = await _db.CafeMarcas.Include(m => m.ProveedorNav)
            .Where(m => marcaIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id);

        // Descuentos automaticos por matriz: DESHABILITADO (decision usuario 2026-05-20).
        // Antes resolvia desc% por (tipo cliente x categoria x marca) desde Cafe_ReglasPrecios y
        // mostraba el precio "lista" tachado con el final destacado. Ahora la lista entrega un solo
        // precio = el final que se cobra, sin tachas ni banner verde. Si el usuario en el futuro
        // quiere descuentos por linea, los aplica manual en la venta.

        // Devuelve (lista, final, descPct) para un producto y formato.
        // Si req.FechaVigencia tiene valor, se calcula usando los precios futuros que ya estarian
        // aplicados a esa fecha (sirve para imprimir la lista nueva antes de que entre en vigencia).
        var fechaPara = req.FechaVigencia?.Date;
        (decimal lista, decimal final, decimal desc) Breakdown(CafeProducto p, string formato)
        {
            var b = CafePricingService.CalcularPrecioBreakdown(p, formato, tipo, settings, 0m, fechaPara);
            return (b.PrecioLista, b.PrecioFinal, b.DescuentoPct);
        }

        // Agrupar por marca. Productos sin marca van bajo "(Sin marca)".
        var grupos = productos
            .GroupBy(p => p.MarcaId)
            .Select(g =>
            {
                CafeMarca? m = g.Key.HasValue && marcas.TryGetValue(g.Key.Value, out var mm) ? mm : null;
                var nombre = m?.Nombre ?? "(Sin marca)";
                var proveedor = m?.ProveedorNav?.Nombre;

                var cafes = g.Where(p => p.Categoria == "CAFE")
                    .OrderBy(p => string.IsNullOrEmpty(p.Sku) ? 1 : 0)
                    .ThenBy(p => SkuLetras(p.Sku))
                    .ThenBy(p => SkuNumero(p.Sku))
                    .ThenBy(p => p.Sku)
                    .ThenBy(p => p.Nombre)
                    .Select(p =>
                    {
                        var b1 = Breakdown(p, "1KG");
                        var b2 = Breakdown(p, "MEDIO");
                        var b3 = Breakdown(p, "CUARTO");
                        return new CafeListaPreciosItemCafeDto(
                            p.Id, p.Sku, p.Nombre,
                            b1.final, b2.final, b3.final,
                            b1.lista, b2.lista, b3.lista,
                            b1.desc);
                    })
                    .ToList();
                var otros = g.Where(p => p.Categoria == "OTROS")
                    .OrderBy(p => string.IsNullOrEmpty(p.Sku) ? 1 : 0)
                    .ThenBy(p => SkuLetras(p.Sku))
                    .ThenBy(p => SkuNumero(p.Sku))
                    .ThenBy(p => p.Sku)
                    .ThenBy(p => p.Nombre)
                    .Select(p =>
                    {
                        var b = Breakdown(p, "UNIT");
                        return new CafeListaPreciosItemOtroDto(p.Id, p.Sku, p.Nombre, b.final, b.lista, b.desc);
                    })
                    .ToList();
                return new CafeListaPreciosMarcaGroupDto(g.Key, nombre, proveedor, cafes, otros);
            })
            .OrderBy(x => x.MarcaNombre == "(Sin marca)" ? "ZZZ" : x.MarcaNombre)  // sin marca al final
            .ToList();

        var hoy = DateTime.UtcNow.Date;
        // Solo mostramos "Vigente desde" si la fecha pedida es estrictamente posterior a hoy
        // (no tiene sentido decir "vigente desde hoy" — es la lista actual).
        DateTime? vigenteDesde = (fechaPara.HasValue && fechaPara.Value > hoy) ? fechaPara : null;
        var preview = new CafeListaPreciosPreviewDto(
            hoy, hoy.AddDays(7), tipo, negocio, clienteDto, grupos,
            string.IsNullOrWhiteSpace(req.Observaciones) ? null : req.Observaciones.Trim(),
            vigenteDesde,
            string.IsNullOrWhiteSpace(req.NumeroLista) ? null : req.NumeroLista.Trim());
        return (preview, settings);
    }

    private static byte[] BuildExcel(CafeListaPreciosPreviewDto p)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Lista de precios");
        int row = 1;

        // Header
        ws.Cell(row, 1).Value = "LISTA DE PRECIOS";
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 1).Style.Font.FontSize = 16;
        ws.Range(row, 1, row, 5).Merge();
        row++;
        ws.Cell(row, 1).Value = p.Negocio.Nombre ?? "";
        ws.Cell(row, 1).Style.Font.Bold = true;
        row++;
        if (!string.IsNullOrEmpty(p.Negocio.Telefono)) { ws.Cell(row++, 1).Value = "Tel: " + p.Negocio.Telefono; }
        if (!string.IsNullOrEmpty(p.Negocio.WhatsappNumero)) { ws.Cell(row++, 1).Value = "WhatsApp: " + p.Negocio.WhatsappNumero; }
        if (!string.IsNullOrEmpty(p.Negocio.Email)) { ws.Cell(row++, 1).Value = "Email: " + p.Negocio.Email; }
        if (!string.IsNullOrEmpty(p.Negocio.Web)) { ws.Cell(row++, 1).Value = "Web: " + p.Negocio.Web; }
        if (!string.IsNullOrEmpty(p.Negocio.Direccion)) { ws.Cell(row++, 1).Value = p.Negocio.Direccion; }
        if (!string.IsNullOrEmpty(p.Negocio.Cuit)) { ws.Cell(row++, 1).Value = "CUIT: " + p.Negocio.Cuit; }
        row++;
        ws.Cell(row, 1).Value = "Cliente:";
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 2).Value = p.Cliente?.Nombre ?? "(General)";
        row++;
        ws.Cell(row, 1).Value = "Tipo:";
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 2).Value = p.TipoCliente;
        row++;
        ws.Cell(row, 1).Value = "Fecha:";
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 2).Value = p.Fecha.ToString("dd/MM/yyyy");
        row++;

        if (p.VigenteDesde.HasValue)
        {
            ws.Cell(row, 1).Value = $"📅 PRECIOS VIGENTES DESDE EL {p.VigenteDesde.Value:dd/MM/yyyy}";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontColor = XLColor.FromHtml("#92400e");
            ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#fef3c7");
            ws.Range(row, 1, row, 5).Merge();
            row++;
        }

        // Banner de descuentos aplicados (si hay)
        decimal? descCafe = null, descOtros = null;
        foreach (var gx in p.Grupos)
        {
            foreach (var c in gx.ItemsCafe) { if (descCafe is null && c.DescuentoPct > 0) descCafe = c.DescuentoPct; }
            foreach (var o in gx.ItemsOtros) { if (descOtros is null && o.DescuentoPct > 0) descOtros = o.DescuentoPct; }
        }
        if (descCafe.HasValue || descOtros.HasValue)
        {
            var partes = new List<string>();
            if (descCafe.HasValue) partes.Add($"-{descCafe.Value:0.##}% en CAFÉ");
            if (descOtros.HasValue) partes.Add($"-{descOtros.Value:0.##}% en OTROS");
            row++;
            ws.Cell(row, 1).Value = "💰 Descuentos aplicados: " + string.Join(" · ", partes);
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontColor = XLColor.FromHtml("#15803d");
            ws.Range(row, 1, row, 5).Merge();
        }
        row += 2;

        // Cuerpo
        foreach (var g in p.Grupos)
        {
            ws.Cell(row, 1).Value = "MARCA: " + g.MarcaNombre;
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#fef3c7");
            ws.Range(row, 1, row, 5).Merge();
            row++;
            if (!string.IsNullOrEmpty(g.ProveedorNombre))
            {
                ws.Cell(row, 1).Value = "Proveedor: " + g.ProveedorNombre;
                ws.Cell(row, 1).Style.Font.FontSize = 9;
                ws.Cell(row, 1).Style.Font.Italic = true;
                row++;
            }

            if (g.ItemsCafe.Count > 0)
            {
                // Header tabla CAFE
                ws.Cell(row, 1).Value = "Producto";
                ws.Cell(row, 2).Value = "SKU";
                ws.Cell(row, 3).Value = "1 kg";
                ws.Cell(row, 4).Value = "1/2 kg";
                ws.Cell(row, 5).Value = "1/4 kg";
                ws.Range(row, 1, row, 5).Style.Font.Bold = true;
                ws.Range(row, 1, row, 5).Style.Fill.BackgroundColor = XLColor.FromHtml("#f3f4f6");
                row++;
                foreach (var i in g.ItemsCafe)
                {
                    ws.Cell(row, 1).Value = i.Nombre;
                    ws.Cell(row, 2).Value = i.Sku ?? "";
                    ws.Cell(row, 3).Value = i.Precio1Kg;
                    ws.Cell(row, 3).Style.NumberFormat.Format = "$#,##0.00";
                    ws.Cell(row, 4).Value = i.PrecioMedio;
                    ws.Cell(row, 4).Style.NumberFormat.Format = "$#,##0.00";
                    ws.Cell(row, 5).Value = i.PrecioCuarto;
                    ws.Cell(row, 5).Style.NumberFormat.Format = "$#,##0.00";
                    row++;
                }
            }
            if (g.ItemsOtros.Count > 0)
            {
                ws.Cell(row, 1).Value = "Producto";
                ws.Cell(row, 2).Value = "SKU";
                ws.Cell(row, 3).Value = "Precio";
                ws.Range(row, 1, row, 3).Style.Font.Bold = true;
                ws.Range(row, 1, row, 3).Style.Fill.BackgroundColor = XLColor.FromHtml("#f3f4f6");
                row++;
                foreach (var i in g.ItemsOtros)
                {
                    ws.Cell(row, 1).Value = i.Nombre;
                    ws.Cell(row, 2).Value = i.Sku ?? "";
                    ws.Cell(row, 3).Value = i.Precio;
                    ws.Cell(row, 3).Style.NumberFormat.Format = "$#,##0.00";
                    row++;
                }
            }
            row++;
        }

        // Aviso IVA fijo, siempre visible
        ws.Cell(row, 1).Value = "⚠ LOS PRECIOS NO INCLUYEN IVA";
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 1).Style.Font.FontColor = XLColor.FromHtml("#b91c1c");
        ws.Cell(row, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Range(row, 1, row, 5).Merge();
        row += 2;

        if (!string.IsNullOrEmpty(p.Observaciones))
        {
            ws.Cell(row, 1).Value = "Observaciones:";
            ws.Cell(row, 1).Style.Font.Bold = true;
            row++;
            ws.Cell(row, 1).Value = p.Observaciones;
            ws.Range(row, 1, row, 5).Merge();
            ws.Cell(row, 1).Style.Alignment.WrapText = true;
            row++;
        }

        // Disclaimer fijo
        ws.Cell(row, 1).Value = "Los precios pueden variar sin previo aviso.";
        ws.Cell(row, 1).Style.Font.Italic = true;
        ws.Cell(row, 1).Style.Font.FontColor = XLColor.FromHtml("#6b7280");
        ws.Range(row, 1, row, 5).Merge();
        row++;

        ws.Columns().AdjustToContents();
        ws.Column(1).Width = Math.Min(45, ws.Column(1).Width);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static string Slug(string s)
    {
        var clean = new string(s.Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-').ToArray());
        return clean.Replace(' ', '-').ToLowerInvariant();
    }

    // Orden natural de SKU: "F1" → letras "F", numero 1; "F11" → letras "F", numero 11.
    private static string SkuLetras(string? sku)
    {
        if (string.IsNullOrEmpty(sku)) return "";
        var i = 0;
        while (i < sku.Length && !char.IsDigit(sku[i])) i++;
        return sku.Substring(0, i).ToUpperInvariant();
    }
    private static int SkuNumero(string? sku)
    {
        if (string.IsNullOrEmpty(sku)) return 0;
        var i = 0;
        while (i < sku.Length && !char.IsDigit(sku[i])) i++;
        var start = i;
        while (i < sku.Length && char.IsDigit(sku[i])) i++;
        if (i == start) return 0;
        return int.TryParse(sku.AsSpan(start, i - start), out var n) ? n : 0;
    }
}
