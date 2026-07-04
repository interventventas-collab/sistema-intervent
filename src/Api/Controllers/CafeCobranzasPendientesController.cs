using Api.Data;
using Api.Models;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Api.Controllers;

/// <summary>
/// Cobranzas precargadas por los repartidores desde la pantalla mobile /repartidor/{token}.
/// El admin las revisa aca y las APRUEBA (que crea CafeCobranza real) o RECHAZA. Pedido 2026-05-19.
/// </summary>
[ApiController]
[Route("api/cafe/cobranzas-pendientes")]
[Authorize]
public class CafeCobranzasPendientesController : ControllerBase
{
    private readonly AppDbContext _db;
    public CafeCobranzasPendientesController(AppDbContext db) { _db = db; }

    public record PendienteDto(int Id, int VentaId, string VentaNumero, int? ClienteId, string? ClienteNombre,
        decimal VentaTotal, int RepartidorId, string RepartidorNombre, decimal Importe,
        bool MarcadoEntregado, string? Notas, string Estado, DateTime CreatedAt,
        // 2026-06-10: true si la venta ya estaba 100% cobrada al momento de listar
        // (sirve para destacar en admin: hay que asignar a otra venta del mismo cliente)
        bool VentaYaCobrada = false);

    public record ArqueoItemDto(int VentaId, string VentaNumero, string? ClienteNombre, decimal Importe,
        bool MarcadoEntregado, string Estado, DateTime CreatedAt, bool EsAlquiler = false);
    public record ArqueoDto(int RepartidorId, string RepartidorNombre, DateTime Fecha,
        decimal TotalPendiente, decimal TotalAprobado, int CantPendiente, int CantAprobado, List<ArqueoItemDto> Items);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? estado = "PENDIENTE",
        [FromQuery] int? repartidorId = null,
        [FromQuery] DateTime? desde = null,
        [FromQuery] DateTime? hasta = null)
    {
        var q = _db.CafeCobranzasPendientes
            .Include(p => p.Venta)
            .Include(p => p.Repartidor)
            .AsQueryable();
        if (!string.IsNullOrWhiteSpace(estado) && estado != "todos")
            q = q.Where(p => p.Estado == estado.ToUpperInvariant());
        if (repartidorId.HasValue && repartidorId.Value > 0)
            q = q.Where(p => p.RepartidorId == repartidorId.Value);
        if (desde.HasValue)
        {
            var d = desde.Value.Date;
            q = q.Where(p => p.CreatedAt >= d);
        }
        if (hasta.HasValue)
        {
            var h = hasta.Value.Date.AddDays(1); // hasta inclusive
            q = q.Where(p => p.CreatedAt < h);
        }
        var raw = await q.OrderByDescending(p => p.CreatedAt)
            .Select(p => new {
                p.Id, p.VentaId, VentaNumero = p.Venta!.Numero,
                ClienteId = p.Venta.ClienteId,
                ClienteNombre = p.Venta.ClienteNombreSnapshot,
                VentaTotal = p.Venta.Total,
                p.RepartidorId, RepartidorNombre = p.Repartidor!.Nombre,
                p.Importe, p.MarcadoEntregado, p.Notas, p.Estado, p.CreatedAt
            })
            .ToListAsync();
        // Calcular VentaYaCobrada por cada item (sumar cobranzas vigentes por venta)
        var ventaIds = raw.Select(x => x.VentaId).Distinct().ToList();
        var pagosDic = ventaIds.Count == 0
            ? new Dictionary<int, decimal>()
            : await _db.CafeCobranzasComprobantes
                .Where(c => c.VentaId.HasValue && ventaIds.Contains(c.VentaId.Value) && c.Cobranza!.Estado == "VIGENTE")
                .GroupBy(c => c.VentaId!.Value)
                .Select(g => new { g.Key, S = g.Sum(x => x.Importe) })
                .ToDictionaryAsync(x => x.Key, x => x.S);
        var l = raw.Select(x =>
        {
            var pagado = pagosDic.TryGetValue(x.VentaId, out var pg) ? pg : 0m;
            var yaCobrada = x.VentaTotal > 0m && pagado >= x.VentaTotal - 0.01m;
            return new PendienteDto(x.Id, x.VentaId, x.VentaNumero, x.ClienteId, x.ClienteNombre,
                x.VentaTotal, x.RepartidorId, x.RepartidorNombre,
                x.Importe, x.MarcadoEntregado, x.Notas, x.Estado, x.CreatedAt, yaCobrada);
        }).ToList();
        return Ok(l);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 2026-06-10: Descargas Excel + PDF de la planilla de control
    // ─────────────────────────────────────────────────────────────────────
    private async Task<List<PendienteDto>> GetFiltradoAsync(string? estado, int? repartidorId, DateTime? desde, DateTime? hasta)
    {
        var q = _db.CafeCobranzasPendientes
            .Include(p => p.Venta)
            .Include(p => p.Repartidor)
            .AsQueryable();
        if (!string.IsNullOrWhiteSpace(estado) && estado != "todos")
            q = q.Where(p => p.Estado == estado.ToUpperInvariant());
        if (repartidorId.HasValue && repartidorId.Value > 0)
            q = q.Where(p => p.RepartidorId == repartidorId.Value);
        if (desde.HasValue)
        {
            var d = desde.Value.Date;
            q = q.Where(p => p.CreatedAt >= d);
        }
        if (hasta.HasValue)
        {
            var h = hasta.Value.Date.AddDays(1);
            q = q.Where(p => p.CreatedAt < h);
        }
        return await q.OrderByDescending(p => p.CreatedAt)
            .Select(p => new PendienteDto(p.Id, p.VentaId,
                p.Venta!.Numero, p.Venta.ClienteId, p.Venta.ClienteNombreSnapshot,
                p.Venta.Total, p.RepartidorId, p.Repartidor!.Nombre,
                p.Importe, p.MarcadoEntregado, p.Notas, p.Estado, p.CreatedAt, false))
            .ToListAsync();
    }

    [HttpGet("export-excel")]
    public async Task<IActionResult> ExportExcel([FromQuery] string? estado = null,
        [FromQuery] int? repartidorId = null,
        [FromQuery] DateTime? desde = null,
        [FromQuery] DateTime? hasta = null)
    {
        var items = await GetFiltradoAsync(estado, repartidorId, desde, hasta);
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Cobranzas");
        // Header
        var headers = new[] { "Fecha", "Hora", "Repartidor", "Venta", "Cliente", "Importe", "Estado", "Entregado", "Observaciones" };
        for (int i = 0; i < headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
            ws.Cell(1, i + 1).Style.Font.Bold = true;
            ws.Cell(1, i + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#1d4ed8");
            ws.Cell(1, i + 1).Style.Font.FontColor = XLColor.White;
        }
        int row = 2;
        foreach (var c in items)
        {
            var local = c.CreatedAt.ToLocalTime();
            ws.Cell(row, 1).Value = local.ToString("dd/MM/yyyy");
            ws.Cell(row, 2).Value = local.ToString("HH:mm");
            ws.Cell(row, 3).Value = c.RepartidorNombre;
            ws.Cell(row, 4).Value = c.VentaNumero;
            ws.Cell(row, 5).Value = c.ClienteNombre ?? "(sin cliente)";
            ws.Cell(row, 6).Value = c.Importe;
            ws.Cell(row, 6).Style.NumberFormat.Format = "$#,##0.00";
            ws.Cell(row, 7).Value = c.Estado;
            ws.Cell(row, 8).Value = c.MarcadoEntregado ? "Si" : "No";
            ws.Cell(row, 9).Value = c.Notas ?? "";
            // Color por estado
            if (c.Estado == "APROBADA")
                ws.Cell(row, 7).Style.Fill.BackgroundColor = XLColor.FromHtml("#d1fae5");
            else if (c.Estado == "PENDIENTE")
                ws.Cell(row, 7).Style.Fill.BackgroundColor = XLColor.FromHtml("#fef3c7");
            else if (c.Estado == "RECHAZADA")
                ws.Cell(row, 7).Style.Fill.BackgroundColor = XLColor.FromHtml("#fee2e2");
            row++;
        }
        // Total al final
        if (items.Count > 0)
        {
            ws.Cell(row + 1, 5).Value = "TOTAL";
            ws.Cell(row + 1, 5).Style.Font.Bold = true;
            ws.Cell(row + 1, 6).Value = items.Sum(x => x.Importe);
            ws.Cell(row + 1, 6).Style.Font.Bold = true;
            ws.Cell(row + 1, 6).Style.NumberFormat.Format = "$#,##0.00";
        }
        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var nombre = $"cobranzas-control-{DateTime.Now:yyyy-MM-dd-HHmm}.xlsx";
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            nombre);
    }

    [HttpGet("export-pdf")]
    public async Task<IActionResult> ExportPdf([FromQuery] string? estado = null,
        [FromQuery] int? repartidorId = null,
        [FromQuery] DateTime? desde = null,
        [FromQuery] DateTime? hasta = null)
    {
        var items = await GetFiltradoAsync(estado, repartidorId, desde, hasta);
        var total = items.Sum(x => x.Importe);
        var filtroTexto = $"Estado: {estado ?? "Todos"}";
        if (repartidorId.HasValue && repartidorId.Value > 0)
        {
            var rep = await _db.CafeRepartidores.FirstOrDefaultAsync(r => r.Id == repartidorId.Value);
            filtroTexto += $" · Repartidor: {rep?.Nombre ?? "?"}";
        }
        else filtroTexto += " · Repartidor: Todos";
        if (desde.HasValue) filtroTexto += $" · Desde: {desde.Value:dd/MM/yyyy}";
        if (hasta.HasValue) filtroTexto += $" · Hasta: {hasta.Value:dd/MM/yyyy}";

        var pdf = Document.Create(c =>
        {
            c.Page(p =>
            {
                p.Size(PageSizes.A4.Landscape());
                p.Margin(20);
                p.PageColor(Colors.White);
                p.DefaultTextStyle(x => x.FontSize(9).FontFamily("Arial"));

                p.Header().Column(col =>
                {
                    col.Item().Text("💰 Planilla de Cobranzas a Aprobar").Bold().FontSize(16);
                    col.Item().Text(filtroTexto).FontSize(9).FontColor(Colors.Grey.Darken1);
                    col.Item().Text($"Generada: {DateTime.Now:dd/MM/yyyy HH:mm}").FontSize(8).FontColor(Colors.Grey.Medium);
                });

                p.Content().PaddingVertical(10).Table(t =>
                {
                    t.ColumnsDefinition(cd =>
                    {
                        cd.RelativeColumn(1.2f); // Fecha
                        cd.RelativeColumn(0.8f); // Hora
                        cd.RelativeColumn(1.5f); // Repartidor
                        cd.RelativeColumn(1.6f); // Venta
                        cd.RelativeColumn(3f);   // Cliente
                        cd.RelativeColumn(1.4f); // Importe
                        cd.RelativeColumn(1.2f); // Estado
                        cd.RelativeColumn(0.8f); // Entregado
                    });
                    // Header
                    t.Header(h =>
                    {
                        void Th(string txt) => h.Cell().Background(Colors.Blue.Darken3)
                            .PaddingVertical(4).PaddingHorizontal(3)
                            .Text(txt).FontColor(Colors.White).Bold().FontSize(9);
                        Th("Fecha"); Th("Hora"); Th("Repartidor"); Th("Venta"); Th("Cliente"); Th("Importe"); Th("Estado"); Th("Entreg.");
                    });
                    foreach (var c in items)
                    {
                        var local = c.CreatedAt.ToLocalTime();
                        void Td(string txt) => t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2)
                            .PaddingVertical(3).PaddingHorizontal(3).Text(txt).FontSize(8);
                        Td(local.ToString("dd/MM/yyyy"));
                        Td(local.ToString("HH:mm"));
                        Td(c.RepartidorNombre);
                        Td(c.VentaNumero);
                        Td(c.ClienteNombre ?? "(sin cliente)");
                        t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2)
                            .PaddingVertical(3).PaddingHorizontal(3).AlignRight()
                            .Text($"${c.Importe:N2}").Bold().FontSize(8);
                        Td(c.Estado);
                        Td(c.MarcadoEntregado ? "Si" : "-");
                    }
                    // Total
                    if (items.Count > 0)
                    {
                        t.Cell().ColumnSpan(5).BorderTop(1).PaddingTop(5).AlignRight().Text("TOTAL").Bold().FontSize(10);
                        t.Cell().BorderTop(1).PaddingTop(5).AlignRight().Text($"${total:N2}").Bold().FontSize(11);
                        t.Cell().ColumnSpan(2).BorderTop(1);
                    }
                });

                p.Footer().AlignCenter().Text(x =>
                {
                    x.Span("Página ");
                    x.CurrentPageNumber();
                    x.Span(" de ");
                    x.TotalPages();
                });
            });
        });

        var bytes = pdf.GeneratePdf();
        var nombre = $"cobranzas-control-{DateTime.Now:yyyy-MM-dd-HHmm}.pdf";
        return File(bytes, "application/pdf", nombre);
    }

    /// <summary>Cantidad de cobranzas pendientes (para badge en topbar).</summary>
    [HttpGet("count-pendientes")]
    public async Task<IActionResult> CountPendientes()
    {
        var c = await _db.CafeCobranzasPendientes.CountAsync(p => p.Estado == "PENDIENTE");
        return Ok(new { count = c });
    }

    /// <summary>Detalle de una cobranza pendiente para precargarla en el modal de Nueva cobranza.</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var p = await _db.CafeCobranzasPendientes
            .Include(x => x.Venta).Include(x => x.Repartidor)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        return Ok(new PendienteDto(p.Id, p.VentaId,
            p.Venta?.Numero ?? "?", p.Venta?.ClienteId, p.Venta?.ClienteNombreSnapshot,
            p.Venta?.Total ?? 0m, p.RepartidorId, p.Repartidor?.Nombre ?? "?",
            p.Importe, p.MarcadoEntregado, p.Notas, p.Estado, p.CreatedAt));
    }

    public record VincularRequest(int CobranzaId, string? Operador);

    /// <summary>Marca la cobranza pendiente como APROBADA vinculandola a una CafeCobranza ya
    /// creada por el admin desde el modal de Nueva Cobranza. La crea_cion de la cobranza
    /// real con sus imputaciones la hace /cafe/tesoreria/cobranzas con los datos pre-cargados
    /// (cliente + efectivo + importe). Aca solo hacemos el marcado. Tambien sincroniza el
    /// estado "entregado" si el repartidor lo habia tildado.</summary>
    [HttpPost("{id:int}/vincular")]
    public async Task<IActionResult> Vincular(int id, [FromBody] VincularRequest req)
    {
        var p = await _db.CafeCobranzasPendientes.Include(x => x.Venta)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        if (p.Estado != "PENDIENTE") return BadRequest(new { error = $"Ya esta {p.Estado}" });
        p.Estado = "APROBADA";
        p.CobranzaCreadaId = req.CobranzaId;
        p.RevisadaPor = req.Operador;
        p.RevisadaAt = DateTime.UtcNow;
        // Si la cobranza tilde "entregado", actualizar la venta
        if (p.MarcadoEntregado && p.Venta is not null)
        {
            p.Venta.EntregadoPorRepartidorId = p.RepartidorId;
            // 2026-07-03 FIX BUG: usar la fecha en que el repartidor marco entregado
            // desde su celu (p.CreatedAt), NO el momento en que el admin aprueba.
            // Antes se pisaba con DateTime.UtcNow y se perdia la fecha real de la
            // entrega. Ejemplo: repartidor entrega 02/07 18:33, admin aprueba
            // 03/07 08:26, y el sistema marcaba 03/07 08:26 como "Entregada".
            p.Venta.EntregadoAt = p.CreatedAt;
            if (p.Venta.EstadoPreparacion != null)
            {
                var estadoAntApr1 = p.Venta.EstadoPreparacion;
                p.Venta.EstadoPreparacion = "ENTREGADO";
                p.Venta.PreparacionUpdatedAt = DateTime.UtcNow;
                _db.CafeVentaPreparacionLogs.Add(new Models.CafeVentaPreparacionLog
                {
                    VentaId = p.Venta.Id, EstadoAnterior = estadoAntApr1, EstadoNuevo = "ENTREGADO",
                    OperadorNombre = req.Operador ?? "admin",
                    Notas = "Admin asocio cobranza pendiente a venta — marca entregada",
                    CreatedAt = DateTime.UtcNow
                });
            }
        }
        await _db.SaveChangesAsync();
        return Ok();
    }

    public record AprobarRequest(string? Operador, int? CajaId);

    /// <summary>Aprueba una cobranza pendiente — crea una CafeCobranza real con un solo medio
    /// (efectivo). El CajaId opcional es la caja de efectivo a usar; si no viene, usa la primera
    /// caja activa tipo EFECTIVO. Imputa el importe contra la venta del repartidor.</summary>
    [HttpPost("{id:int}/aprobar")]
    public async Task<IActionResult> Aprobar(int id, [FromBody] AprobarRequest req)
    {
        var p = await _db.CafeCobranzasPendientes.Include(x => x.Venta).Include(x => x.Repartidor)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        if (p.Estado != "PENDIENTE") return BadRequest(new { error = $"Ya esta {p.Estado}" });
        if (p.Venta is null) return BadRequest(new { error = "La venta asociada no existe" });

        // Buscar caja de efectivo
        var caja = req.CajaId.HasValue
            ? await _db.CafeCajas.FirstOrDefaultAsync(c => c.Id == req.CajaId.Value)
            : await _db.CafeCajas.FirstOrDefaultAsync(c => c.IsActive && c.Tipo == "EFECTIVO");
        if (caja is null) return BadRequest(new { error = "No hay caja de efectivo activa. Crea una en Tesoreria → Cajas." });

        // Numero de cobranza correlativo (similar logica que CafeCobranzasController)
        var prox = (await _db.CafeCobranzas.OrderByDescending(c => c.Id).Select(c => (int?)c.Id).FirstOrDefaultAsync() ?? 0) + 1;
        var numero = $"COB-{DateTime.Now.Year:0000}-{prox:0000}";

        var cobranza = new CafeCobranza
        {
            Numero = numero,
            Fecha = DateTime.UtcNow,
            ClienteId = p.Venta.ClienteId ?? 0,
            Total = p.Importe,
            Retenciones = 0,
            Operador = req.Operador ?? p.Repartidor?.Nombre,
            Observaciones = $"Auto-generada desde cobranza precargada #{p.Id} por {p.Repartidor?.Nombre} (repartidor)" + (string.IsNullOrEmpty(p.Notas) ? "" : $" — {p.Notas}"),
            Estado = "VIGENTE",
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeCobranzas.Add(cobranza);
        await _db.SaveChangesAsync();

        // Imputacion a la venta
        _db.CafeCobranzasComprobantes.Add(new CafeCobranzaComprobante
        {
            CobranzaId = cobranza.Id,
            VentaId = p.VentaId,
            Importe = p.Importe
        });
        // Medio: efectivo en la caja
        _db.CafeCobranzasMedios.Add(new CafeCobranzaMedio
        {
            CobranzaId = cobranza.Id,
            CajaId = caja.Id,
            Importe = p.Importe,
            Referencia = $"Cobrado por {p.Repartidor?.Nombre}"
        });

        // Sincronizar IsPaid si saldo cubierto
        var totalPagado = await _db.CafeCobranzasComprobantes
            .Where(c => c.VentaId == p.VentaId && c.Cobranza!.Estado == "VIGENTE").SumAsync(c => c.Importe);
        totalPagado += p.Importe; // incluir la que estamos por guardar
        var totalCobrable = (p.Venta.ArcaImpTotal.HasValue && p.Venta.ArcaImpTotal.Value > 0m) ? p.Venta.ArcaImpTotal.Value : p.Venta.Total;
        p.Venta.IsPaid = totalPagado >= totalCobrable - 0.01m;

        // Si tilde "entregue", anotar repartidor + actualizar tablero de preparacion
        if (p.MarcadoEntregado)
        {
            p.Venta.EntregadoPorRepartidorId = p.RepartidorId;
            // 2026-07-03 FIX BUG: usar la fecha en que el repartidor marco "entregado"
            // desde su celu (p.CreatedAt), NO cuando el admin aprueba.
            p.Venta.EntregadoAt = p.CreatedAt;
            if (p.Venta.EstadoPreparacion != null)
            {
                var estadoAntApr2 = p.Venta.EstadoPreparacion;
                p.Venta.EstadoPreparacion = "ENTREGADO";
                p.Venta.PreparacionUpdatedAt = DateTime.UtcNow;
                // 2026-06-09 log
                _db.CafeVentaPreparacionLogs.Add(new Models.CafeVentaPreparacionLog
                {
                    VentaId = p.Venta.Id, EstadoAnterior = estadoAntApr2, EstadoNuevo = "ENTREGADO",
                    OperadorNombre = req.Operador ?? "admin",
                    Notas = "Admin aprobo cobranza pendiente — marca entregada",
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        p.Estado = "APROBADA";
        p.CobranzaCreadaId = cobranza.Id;
        p.RevisadaPor = req.Operador;
        p.RevisadaAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(new { id = p.Id, cobranzaId = cobranza.Id, numero });
    }

    public record RechazarRequest(string? Motivo, string? Operador);

    [HttpPost("{id:int}/rechazar")]
    public async Task<IActionResult> Rechazar(int id, [FromBody] RechazarRequest req)
    {
        var p = await _db.CafeCobranzasPendientes.FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        if (p.Estado != "PENDIENTE") return BadRequest(new { error = $"Ya esta {p.Estado}" });
        p.Estado = "RECHAZADA";
        p.RechazadaMotivo = req.Motivo?.Trim();
        p.RevisadaPor = req.Operador;
        p.RevisadaAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok();
    }

    /// <summary>Arqueo del dia para un repartidor: muestra lo que cobro hoy (sumando aprobadas
    /// + pendientes), ordenado por venta. Sirve para que el repartidor rinda la plata.</summary>
    [HttpGet("arqueo/{repartidorId:int}")]
    public async Task<IActionResult> Arqueo(int repartidorId, [FromQuery] DateTime? fecha)
    {
        var dia = (fecha ?? DateTime.Today).Date;
        var diaFin = dia.AddDays(1);
        var rep = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.Id == repartidorId);
        if (rep is null) return NotFound();
        var pendientes = await _db.CafeCobranzasPendientes
            .Include(p => p.Venta)
            .Where(p => p.RepartidorId == repartidorId
                && p.CreatedAt >= dia && p.CreatedAt < diaFin
                && p.Estado != "RECHAZADA")
            .OrderBy(p => p.CreatedAt)
            .ToListAsync();
        var items = pendientes.Select(p => new ArqueoItemDto(
            p.VentaId, p.Venta?.Numero ?? "?", p.Venta?.ClienteNombreSnapshot,
            p.Importe, p.MarcadoEntregado, p.Estado, p.CreatedAt)).ToList();

        // 2026-06-26: sumar también los cobros de ALQUILER del repartidor en el día.
        var pendAlq = await _db.AlqCobranzasPendientes
            .Include(p => p.Reserva).ThenInclude(r => r!.ClienteNav)
            .Where(p => p.RepartidorId == repartidorId
                && p.CreatedAt >= dia && p.CreatedAt < diaFin
                && p.Estado != "RECHAZADA")
            .OrderBy(p => p.CreatedAt)
            .ToListAsync();
        items.AddRange(pendAlq.Select(p => new ArqueoItemDto(
            p.ReservaId, p.Reserva?.Numero ?? "?", p.Reserva?.ClienteNav?.Nombre,
            p.Importe, p.MarcadoEntregado, p.Estado, p.CreatedAt, EsAlquiler: true)));

        var totalPendiente = pendientes.Where(p => p.Estado == "PENDIENTE").Sum(p => p.Importe)
                           + pendAlq.Where(p => p.Estado == "PENDIENTE").Sum(p => p.Importe);
        var totalAprobado = pendientes.Where(p => p.Estado == "APROBADA").Sum(p => p.Importe)
                          + pendAlq.Where(p => p.Estado == "APROBADA").Sum(p => p.Importe);
        var cantP = pendientes.Count(p => p.Estado == "PENDIENTE") + pendAlq.Count(p => p.Estado == "PENDIENTE");
        var cantA = pendientes.Count(p => p.Estado == "APROBADA") + pendAlq.Count(p => p.Estado == "APROBADA");
        var itemsOrdenados = items.OrderBy(i => i.CreatedAt).ToList();
        return Ok(new ArqueoDto(rep.Id, rep.Nombre, dia, totalPendiente, totalAprobado, cantP, cantA, itemsOrdenados));
    }

    /// <summary>2026-06-10: Arqueo de TODOS los repartidores en un dia. Devuelve una lista
    /// de ArqueoDto, una por cada repartidor que tenga al menos una cobranza ese dia.</summary>
    [HttpGet("arqueo/todos")]
    public async Task<IActionResult> ArqueoTodos([FromQuery] DateTime? fecha)
    {
        var dia = (fecha ?? DateTime.Today).Date;
        var diaFin = dia.AddDays(1);
        var pendientes = await _db.CafeCobranzasPendientes
            .Include(p => p.Venta)
            .Include(p => p.Repartidor)
            .Where(p => p.CreatedAt >= dia && p.CreatedAt < diaFin
                && p.Estado != "RECHAZADA")
            .OrderBy(p => p.CreatedAt)
            .ToListAsync();
        // 2026-06-26: también los cobros de ALQUILER del día.
        var pendAlq = await _db.AlqCobranzasPendientes
            .Include(p => p.Reserva).ThenInclude(r => r!.ClienteNav)
            .Include(p => p.Repartidor)
            .Where(p => p.CreatedAt >= dia && p.CreatedAt < diaFin && p.Estado != "RECHAZADA")
            .OrderBy(p => p.CreatedAt)
            .ToListAsync();

        // Items unificados (ventas + alquiler) con su repartidor.
        var todos = pendientes.Select(p => new {
                p.RepartidorId, Nombre = p.Repartidor?.Nombre ?? "?",
                Item = new ArqueoItemDto(p.VentaId, p.Venta?.Numero ?? "?", p.Venta?.ClienteNombreSnapshot,
                    p.Importe, p.MarcadoEntregado, p.Estado, p.CreatedAt) })
            .Concat(pendAlq.Select(p => new {
                p.RepartidorId, Nombre = p.Repartidor?.Nombre ?? "?",
                Item = new ArqueoItemDto(p.ReservaId, p.Reserva?.Numero ?? "?", p.Reserva?.ClienteNav?.Nombre,
                    p.Importe, p.MarcadoEntregado, p.Estado, p.CreatedAt, EsAlquiler: true) }))
            .ToList();

        var resultados = todos
            .GroupBy(p => new { p.RepartidorId, RepartidorNombre = p.Nombre })
            .Select(g =>
            {
                var items = g.Select(x => x.Item).OrderBy(i => i.CreatedAt).ToList();
                var totalP = items.Where(p => p.Estado == "PENDIENTE").Sum(p => p.Importe);
                var totalA = items.Where(p => p.Estado == "APROBADA").Sum(p => p.Importe);
                var cantP = items.Count(p => p.Estado == "PENDIENTE");
                var cantA = items.Count(p => p.Estado == "APROBADA");
                return new ArqueoDto(g.Key.RepartidorId, g.Key.RepartidorNombre, dia,
                    totalP, totalA, cantP, cantA, items);
            })
            .OrderByDescending(a => a.TotalPendiente + a.TotalAprobado)
            .ToList();

        return Ok(resultados);
    }
}
