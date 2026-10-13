using Api.Data;
using Api.Models;
using Api.Services;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Pantalla "Cobranzas Mercado Pago" (menú MercadoLibre): los cobros que entraron a la cuenta de MP,
/// separados por origen (links del sistema / ventas MercadoLibre / transferencias, QR y otros), con
/// comisión, neto y liberación. Lee Mp_Pagos (lo llena MpPagosService). Pedido 2026-10-13.
/// </summary>
[ApiController]
[Route("api/mercadopago/cobros")]
[Authorize]
public class MpCobrosController : ControllerBase
{
    private readonly AppDbContext _db;
    public MpCobrosController(AppDbContext db) { _db = db; }

    private static readonly string[] EstadosVisibles = { "approved", "refunded", "charged_back" };

    public record CobroDto(int Id, long MpPaymentId, DateTime Fecha, string? Estado, string Origen,
        string? Quien, string? Detalle, string? MedioPago, string? TipoMedio, int? Cuotas,
        decimal Monto, decimal? MontoNeto, string? EstadoLiberacion, DateTime? FechaLiberacion);

    public record TotalOrigenDto(string Origen, int Cantidad, decimal Bruto, decimal Neto);

    public record CobrosDto(int Cantidad, decimal Bruto, decimal Neto, decimal PorLiberar,
        List<TotalOrigenDto> PorOrigen, List<CobroDto> Cobros, bool Truncado,
        DateTime? UltimaSincroAt, bool HayCuenta);

    private const int MaxFilas = 1000;

    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] DateTime? desde, [FromQuery] DateTime? hasta,
        [FromQuery] string? origen, [FromQuery] string? q)
    {
        var todos = await FiltrarAsync(desde, hasta, q);
        var aprobados = todos.Where(c => c.Estado == "approved").ToList();
        var porOrigen = aprobados.GroupBy(c => c.Origen)
            .Select(g => new TotalOrigenDto(g.Key, g.Count(), g.Sum(c => c.Monto), g.Sum(c => c.MontoNeto ?? c.Monto)))
            .ToList();

        var lista = string.IsNullOrEmpty(origen) || origen == "todo" ? todos : todos.Where(c => c.Origen == origen).ToList();
        var ok = lista.Where(c => c.Estado == "approved").ToList();
        var ahora = DateTime.UtcNow;
        var porLiberar = ok.Where(c => !Liberado(c, ahora)).Sum(c => c.MontoNeto ?? c.Monto);

        var cuenta = await _db.MpAccounts.AsNoTracking().OrderBy(a => a.Id).FirstOrDefaultAsync();
        var hayCuenta = cuenta is not null && cuenta.IsActive && !string.IsNullOrEmpty(cuenta.AccessToken);
        var ultima = new[] { MpPagosService.UltimaSincroAt, cuenta?.LastAutoSyncAt }.Where(d => d.HasValue).Max();

        return Ok(new CobrosDto(ok.Count, ok.Sum(c => c.Monto), ok.Sum(c => c.MontoNeto ?? c.Monto), porLiberar,
            porOrigen, lista.Take(MaxFilas).ToList(), lista.Count > MaxFilas, ultima, hayCuenta));
    }

    [HttpGet("excel")]
    public async Task<IActionResult> Excel([FromQuery] DateTime? desde, [FromQuery] DateTime? hasta,
        [FromQuery] string? origen, [FromQuery] string? q)
    {
        var todos = await FiltrarAsync(desde, hasta, q);
        var lista = string.IsNullOrEmpty(origen) || origen == "todo" ? todos : todos.Where(c => c.Origen == origen).ToList();
        var ahora = DateTime.UtcNow;

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Cobranzas MP");
        var headers = new[] { "Fecha", "Hora", "Origen", "Quién pagó", "Detalle", "Medio", "Estado",
            "Pagó", "Comisión MP", "Te quedó", "Liberación", "N° pago MP" };
        for (int i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];
        ws.Row(1).Style.Font.Bold = true;
        int row = 2;
        foreach (var c in lista)
        {
            var ar = c.Fecha.AddHours(-3);
            ws.Cell(row, 1).Value = ar.Date;
            ws.Cell(row, 1).Style.DateFormat.Format = "dd/MM/yyyy";
            ws.Cell(row, 2).Value = ar.ToString("HH:mm");
            ws.Cell(row, 3).Value = c.Origen switch { "link" => "Link del sistema", "meli" => "MercadoLibre", _ => "Transferencia / QR / otro" };
            ws.Cell(row, 4).Value = c.Quien ?? "";
            ws.Cell(row, 5).Value = c.Detalle ?? "";
            ws.Cell(row, 6).Value = MedioTexto(c);
            ws.Cell(row, 7).Value = c.Estado switch { "approved" => "Aprobado", "refunded" => "Devuelto", "charged_back" => "Contracargo", _ => c.Estado ?? "" };
            ws.Cell(row, 8).Value = c.Monto;
            ws.Cell(row, 9).Value = c.MontoNeto.HasValue ? c.Monto - c.MontoNeto.Value : 0m;
            ws.Cell(row, 10).Value = c.MontoNeto ?? c.Monto;
            ws.Cell(row, 11).Value = Liberado(c, ahora) ? "Liberado" : c.FechaLiberacion.HasValue ? "Se libera " + c.FechaLiberacion.Value.AddHours(-3).ToString("dd/MM") : "Pendiente";
            ws.Cell(row, 12).Value = c.MpPaymentId.ToString();
            for (int col = 8; col <= 10; col++) ws.Cell(row, col).Style.NumberFormat.Format = "#,##0.00";
            row++;
        }
        ws.Columns().AdjustToContents();
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Cobranzas_MercadoPago.xlsx");
    }

    private static bool Liberado(CobroDto c, DateTime ahora)
        => c.EstadoLiberacion == "released" || (c.FechaLiberacion.HasValue && c.FechaLiberacion.Value <= ahora);

    private static string MedioTexto(CobroDto c)
    {
        var t = c.TipoMedio switch
        {
            "account_money" => "Dinero en cuenta",
            "debit_card" => "Débito",
            "credit_card" => "Crédito",
            "prepaid_card" => "Prepaga",
            "ticket" => "Efectivo",
            "bank_transfer" => "Transferencia",
            "atm" => "Cajero",
            _ => c.MedioPago switch
            {
                "account_money" => "Dinero en cuenta",
                null or "" => "",
                var m when m.StartsWith("deb") => "Débito",
                _ => c.MedioPago
            }
        };
        if (c.Cuotas is int n && n > 1) t += $" {n} cuotas";
        return t;
    }

    /// <summary>Filtra por fecha argentina (desde/hasta inclusive) y por texto (nombre, mail,
    /// detalle o monto). Deduce el origen de los cobros traídos antes de guardar "Origen".</summary>
    private async Task<List<CobroDto>> FiltrarAsync(DateTime? desde, DateTime? hasta, string? q)
    {
        var hoyAr = DateTime.UtcNow.AddHours(-3).Date;
        var d = (desde ?? hoyAr.AddDays(-30)).Date.AddHours(3);           // 00:00 AR en UTC
        var h = (hasta ?? hoyAr).Date.AddDays(1).AddHours(3);

        var query = _db.MpPagos.AsNoTracking()
            .Where(p => p.Fecha >= d && p.Fecha < h && EstadosVisibles.Contains(p.Estado!));

        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim();
            var limpio = t.Replace("$", "").Replace(" ", "");
            decimal? monto = null;
            var montoTxt = limpio.Contains(',') ? limpio.Replace(".", "").Replace(',', '.') : limpio.Replace(".", "");
            if (decimal.TryParse(montoTxt, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var m)) monto = m;
            query = query.Where(p => (p.PayerNombre != null && p.PayerNombre.Contains(t))
                || (p.PayerEmail != null && p.PayerEmail.Contains(t))
                || (p.Descripcion != null && p.Descripcion.Contains(t))
                || (monto != null && (p.Monto == monto || p.MontoNeto == monto)));
        }

        var pagos = await query.OrderByDescending(p => p.Fecha).Take(20000).ToListAsync();
        if (pagos.Count == 0) return new();

        // Datos de MeLi (n° de orden y título) para los cobros que son ventas de MercadoLibre.
        var ids = pagos.Select(p => p.MpPaymentId).ToList();
        var ordenes = new Dictionary<long, (long orden, string titulo)>();
        foreach (var chunk in ids.Chunk(2000))
        {
            var ch = chunk.ToList();
            var rows = await _db.MeliOrders.AsNoTracking()
                .Where(o => o.MpPaymentId != null && ch.Contains(o.MpPaymentId.Value))
                .Select(o => new { o.MpPaymentId, o.MeliOrderId, o.ItemTitle }).ToListAsync();
            foreach (var r in rows) ordenes.TryAdd(r.MpPaymentId!.Value, (r.MeliOrderId, r.ItemTitle));
        }

        // Para los links del sistema: para quién era y el concepto.
        var refsLink = pagos.Where(p => p.ReferenciaExterna != null && p.ReferenciaExterna.StartsWith("AIML-LINK-"))
            .Select(p => p.ReferenciaExterna!).Distinct().ToList();
        var links = refsLink.Count == 0 ? new Dictionary<string, MpLinkPago>()
            : await _db.MpLinksPago.AsNoTracking().Where(l => refsLink.Contains(l.ReferenciaExterna))
                .ToDictionaryAsync(l => l.ReferenciaExterna);

        var res = new List<CobroDto>(pagos.Count);
        foreach (var p in pagos)
        {
            var origen = p.Origen;
            if (string.IsNullOrEmpty(origen))
                origen = p.ReferenciaExterna?.StartsWith("AIML-LINK-") == true ? "link"
                    : ordenes.ContainsKey(p.MpPaymentId) ? "meli" : "otro";
            if (origen == "otro" && ordenes.ContainsKey(p.MpPaymentId)) origen = "meli";

            var quien = !string.IsNullOrWhiteSpace(p.PayerNombre) ? p.PayerNombre : p.PayerEmail;
            string? detalle;
            if (origen == "meli" && ordenes.TryGetValue(p.MpPaymentId, out var o))
                detalle = $"MeLi #{o.orden} · {o.titulo}";
            else if (origen == "link" && p.ReferenciaExterna != null && links.TryGetValue(p.ReferenciaExterna, out var l))
            {
                detalle = $"Link · {l.Concepto}";
                if (!string.IsNullOrWhiteSpace(l.Cliente)) quien = l.Cliente + (string.IsNullOrWhiteSpace(quien) ? "" : $" ({quien})");
            }
            else detalle = string.IsNullOrWhiteSpace(p.Descripcion) ? (origen == "meli" ? "Venta MercadoLibre" : null) : p.Descripcion;

            res.Add(new CobroDto(p.Id, p.MpPaymentId, p.Fecha, p.Estado, origen, quien, detalle,
                p.MedioPago, p.TipoMedio, p.Cuotas, p.Monto, p.MontoNeto, p.EstadoLiberacion, p.FechaLiberacion));
        }
        return res;
    }
}
