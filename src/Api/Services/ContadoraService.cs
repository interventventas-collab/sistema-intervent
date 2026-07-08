using System.Net.Http.Headers;
using System.Text.Json;
using Api.Data;
using Api.DTOs;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-07-08: Modulo "Contadora". Arma el cuadro de VENTAS POR JURISDICCION (provincia de
/// destino) que la contadora usa para Ingresos Brutos, a partir de las ventas de MercadoLibre.
///
/// La provincia de cada venta sale del envio de MeLi (receiver_address.state.name). El robot de
/// envios solo guardaba la provincia de los envios Flex/ME1 (locales); el resto del pais quedaba
/// sin resolver. Este servicio hace un "backfill": le pregunta a MeLi la provincia de las ventas
/// que faltan y la guarda en MeliOrders.ProvinciaDestino (columna dedicada, no toca MeliShipments).
///
/// Neto = Total / 1,21 (IVA 21% general, igual criterio que usa la contadora).
/// </summary>
public class ContadoraService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MeliAccountService _accountService;
    private const decimal IvaAlicuota = 0.21m;
    private const string SinDato = "(sin dato)";

    public ContadoraService(AppDbContext db, IHttpClientFactory httpFactory, MeliAccountService accountService)
    {
        _db = db; _httpFactory = httpFactory; _accountService = accountService;
    }

    /// <summary>Normaliza el nombre de la provincia como lo espera la contadora.</summary>
    private static string Normalizar(string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return "(sin provincia)";
        var s = state.Trim();
        if (s == SinDato) return "(sin provincia)";
        // MeLi devuelve "Capital Federal"; la contadora lo llama "Ciudad de Buenos Aires".
        if (s.Equals("Capital Federal", StringComparison.OrdinalIgnoreCase)) return "Ciudad de Buenos Aires";
        return s;
    }

    /// <summary>
    /// Trae de MeLi la provincia de hasta <paramref name="lote"/> ventas pagas que todavia no la tienen,
    /// y la guarda en MeliOrders.ProvinciaDestino. Se llama repetido desde el front (barra de progreso)
    /// hasta que Pendientes = 0. Es SOLO LECTURA contra MeLi.
    /// </summary>
    public async Task<ContadoraBackfillResultDto> BackfillProvinciasAsync(int lote = 150)
    {
        if (lote < 1) lote = 1;
        if (lote > 500) lote = 500;

        var res = new ContadoraBackfillResultDto();

        // Cuantas faltan en total (para la barra de progreso).
        var pendientesQ = _db.MeliOrders.Where(o => o.Status == "paid" && o.ShippingId != null && o.ProvinciaDestino == null);
        res.PendientesAntes = await pendientesQ.Select(o => o.ShippingId!.Value).Distinct().CountAsync();
        if (res.PendientesAntes == 0)
        {
            res.Mensaje = "No hay ventas pendientes de resolver.";
            return res;
        }

        // Lote de envios distintos a resolver (con la cuenta a la que pertenecen, para el token).
        var lotePares = await pendientesQ
            .Select(o => new { ShipId = o.ShippingId!.Value, o.MeliAccountId })
            .Distinct()
            .OrderBy(x => x.ShipId)
            .Take(lote)
            .ToListAsync();

        // Token por cuenta (cacheado). Si no hay token valido, cortamos con mensaje claro.
        var accounts = await _accountService.GetAllAccountEntitiesAsync();
        var tokens = new Dictionary<int, string>();
        foreach (var acc in accounts)
        {
            var t = await _accountService.GetValidTokenAsync(acc);
            if (t is not null) tokens[acc.Id] = t;
        }
        if (tokens.Count == 0)
        {
            res.Mensaje = "No hay ninguna cuenta de MercadoLibre con token valido. Reconectala en Integraciones.";
            res.Pendientes = res.PendientesAntes;
            return res;
        }

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(25);

        foreach (var par in lotePares)
        {
            if (!tokens.TryGetValue(par.MeliAccountId, out var token)) { res.Errores++; continue; }
            try
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var resp = await http.GetAsync($"https://api.mercadolibre.com/shipments/{par.ShipId}");
                string? provincia = null;
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("receiver_address", out var ra) && ra.ValueKind == JsonValueKind.Object
                        && ra.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.Object
                        && st.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                    {
                        provincia = nm.GetString();
                    }
                }
                // Guardamos aunque venga vacio (sentinela) para no re-consultar infinitamente el mismo envio.
                var valor = string.IsNullOrWhiteSpace(provincia) ? SinDato : provincia!.Trim();
                var afectadas = await _db.MeliOrders
                    .Where(o => o.ShippingId == par.ShipId && o.ProvinciaDestino == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.ProvinciaDestino, valor));
                if (afectadas > 0) res.Resueltos++;
            }
            catch { res.Errores++; }

            await Task.Delay(120); // gentileza con la API de MeLi
        }

        res.Pendientes = await _db.MeliOrders
            .Where(o => o.Status == "paid" && o.ShippingId != null && o.ProvinciaDestino == null)
            .Select(o => o.ShippingId!.Value).Distinct().CountAsync();
        return res;
    }

    /// <summary>Arma el cuadro de ventas por jurisdiccion para el rango [desde, hasta] (por fecha de venta).</summary>
    public async Task<ContadoraJurisdiccionDto> GetVentasPorJurisdiccionAsync(DateTime? desde, DateTime? hasta)
    {
        var q = _db.MeliOrders.Where(o => o.Status == "paid");
        if (desde.HasValue) q = q.Where(o => o.DateCreated >= desde.Value);
        if (hasta.HasValue) { var h = hasta.Value.Date.AddDays(1); q = q.Where(o => o.DateCreated < h); }

        var datos = await q
            .Select(o => new { o.ProvinciaDestino, o.TotalAmount })
            .ToListAsync();

        var dto = new ContadoraJurisdiccionDto { Desde = desde, Hasta = hasta };

        // Rango de fechas de ventas disponible (guia para el usuario).
        if (await _db.MeliOrders.AnyAsync(o => o.Status == "paid"))
        {
            dto.VentasDesde = await _db.MeliOrders.Where(o => o.Status == "paid").MinAsync(o => o.DateCreated);
            dto.VentasHasta = await _db.MeliOrders.Where(o => o.Status == "paid").MaxAsync(o => o.DateCreated);
        }

        // Agrupar por provincia normalizada.
        var grupos = datos
            .GroupBy(x => Normalizar(x.ProvinciaDestino))
            .Select(g =>
            {
                var total = g.Sum(x => x.TotalAmount);
                var neto = Math.Round(total / (1 + IvaAlicuota), 2);
                return new ContadoraJurisdiccionRowDto
                {
                    Provincia = g.Key,
                    Cantidad = g.Count(),
                    Total = total,
                    Neto = neto
                };
            })
            .OrderByDescending(r => r.Neto)
            .ToList();

        dto.Filas = grupos;
        dto.CantidadTotal = grupos.Sum(r => r.Cantidad);
        dto.NetoTotal = grupos.Sum(r => r.Neto);
        dto.TotalConIva = grupos.Sum(r => r.Total);
        dto.IvaTotal = dto.TotalConIva - dto.NetoTotal;
        dto.SinProvincia = datos.Count(x => x.ProvinciaDestino == null);
        return dto;
    }

    /// <summary>Genera el Excel del cuadro con el formato de la contadora ("Argentina - Provincia").</summary>
    public async Task<byte[]> GenerarExcelAsync(DateTime? desde, DateTime? hasta)
    {
        var cuadro = await GetVentasPorJurisdiccionAsync(desde, hasta);
        using var wb = new ClosedXML.Excel.XLWorkbook();
        var ws = wb.AddWorksheet("Ventas por jurisdiccion");

        int r = 1;
        ws.Cell(r, 1).Value = "Destino";
        ws.Cell(r, 2).Value = "Cantidad";
        ws.Cell(r, 3).Value = "Total Neto";
        ws.Range(r, 1, r, 3).Style.Font.Bold = true;
        r++;

        foreach (var fila in cuadro.Filas)
        {
            var destino = fila.Provincia.StartsWith("(") ? fila.Provincia : $"Argentina - {fila.Provincia}";
            ws.Cell(r, 1).Value = destino;
            ws.Cell(r, 2).Value = fila.Cantidad;
            ws.Cell(r, 3).Value = fila.Neto;
            ws.Cell(r, 3).Style.NumberFormat.Format = "#,##0.00";
            r++;
        }

        r++;
        ws.Cell(r, 1).Value = "TOTAL NETO";
        ws.Cell(r, 2).Value = cuadro.CantidadTotal;
        ws.Cell(r, 3).Value = cuadro.NetoTotal;
        ws.Range(r, 1, r, 3).Style.Font.Bold = true;
        ws.Cell(r, 3).Style.NumberFormat.Format = "#,##0.00";
        r++;
        ws.Cell(r, 1).Value = "IVA 21%";
        ws.Cell(r, 3).Value = cuadro.IvaTotal;
        ws.Cell(r, 3).Style.NumberFormat.Format = "#,##0.00";
        r++;
        ws.Cell(r, 1).Value = "TOTAL c/IVA";
        ws.Cell(r, 3).Value = cuadro.TotalConIva;
        ws.Range(r, 1, r, 3).Style.Font.Bold = true;
        ws.Cell(r, 3).Style.NumberFormat.Format = "#,##0.00";

        ws.Columns().AdjustToContents();
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
