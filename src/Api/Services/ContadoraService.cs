using System.Net.Http.Headers;
using System.Text.Json;
using Api.Data;
using Api.DTOs;
using Api.Models;
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

    // ═══════════════════ ETAPA 2: Libro IVA Ventas (facturas emitidas por MeLi) ═══════════════════

    /// <summary>
    /// Baja de MeLi la factura de venta de hasta <paramref name="lote"/> ordenes pagas que todavia no
    /// la tienen bajada, y la guarda en MeliFacturas. Se llama repetido desde el front hasta Pendientes=0.
    /// Si una orden no tiene factura en MeLi, guarda una fila con Status='SIN_FACTURA' para no reintentarla.
    /// </summary>
    public async Task<ContadoraBackfillResultDto> BackfillFacturasAsync(int lote = 120)
    {
        if (lote < 1) lote = 1;
        if (lote > 400) lote = 400;
        var res = new ContadoraBackfillResultDto();

        var pendientesQ = _db.MeliOrders.Where(o => o.Status == "paid"
            && !_db.MeliFacturas.Any(f => f.MeliOrderId == o.MeliOrderId));
        res.PendientesAntes = await pendientesQ.Select(o => o.MeliOrderId).Distinct().CountAsync();
        if (res.PendientesAntes == 0) { res.Mensaje = "No hay ventas pendientes de bajar factura."; return res; }

        var lotePares = await pendientesQ
            .Select(o => new { o.MeliOrderId, o.MeliAccountId })
            .Distinct().OrderByDescending(x => x.MeliOrderId).Take(lote).ToListAsync();

        var accounts = await _accountService.GetAllAccountEntitiesAsync();
        var tokens = new Dictionary<int, string>();
        var userIds = new Dictionary<int, long>();
        foreach (var acc in accounts)
        {
            var t = await _accountService.GetValidTokenAsync(acc);
            if (t is not null) { tokens[acc.Id] = t; userIds[acc.Id] = acc.MeliUserId; }
        }
        if (tokens.Count == 0)
        {
            res.Mensaje = "No hay ninguna cuenta de MercadoLibre con token valido. Reconectala en Integraciones.";
            res.Pendientes = res.PendientesAntes;
            return res;
        }

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);

        foreach (var par in lotePares)
        {
            if (!tokens.TryGetValue(par.MeliAccountId, out var token)) { res.Errores++; continue; }
            var userId = userIds[par.MeliAccountId];
            var fac = new MeliFactura { MeliOrderId = par.MeliOrderId, MeliAccountId = par.MeliAccountId, SyncedAt = DateTime.UtcNow };
            try
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var resp = await http.GetAsync($"https://api.mercadolibre.com/users/{userId}/invoices/orders/{par.MeliOrderId}");
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    ParseFactura(doc.RootElement, fac);
                }
                else
                {
                    fac.Status = "SIN_FACTURA"; // 404 u otro → la orden no tiene factura en MeLi
                }
                _db.MeliFacturas.Add(fac);
                await _db.SaveChangesAsync();
                res.Resueltos++;
            }
            catch { res.Errores++; }
            await Task.Delay(120);
        }

        res.Pendientes = await _db.MeliOrders
            .Where(o => o.Status == "paid" && !_db.MeliFacturas.Any(f => f.MeliOrderId == o.MeliOrderId))
            .Select(o => o.MeliOrderId).Distinct().CountAsync();
        return res;
    }

    /// <summary>Parsea el JSON de la factura de MeLi hacia la entidad.</summary>
    private static void ParseFactura(JsonElement root, MeliFactura fac)
    {
        fac.Status = GetStr(root, "status") ?? "authorized";
        if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var invId)) fac.InvoiceId = invId;
        if (root.TryGetProperty("invoice_series", out var se) && se.TryGetInt32(out var serie)) fac.PuntoVenta = serie;
        if (root.TryGetProperty("invoice_number", out var nu) && nu.TryGetInt64(out var num)) fac.NumeroComprobante = num;
        if (root.TryGetProperty("issued_date", out var isd) && isd.ValueKind == JsonValueKind.String
            && DateTime.TryParse(isd.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var fe))
            fac.FechaEmision = fe;

        if (root.TryGetProperty("issuer", out var iss) && iss.ValueKind == JsonValueKind.Object)
        {
            fac.EmisorNombre = GetStr(iss, "name");
            if (iss.TryGetProperty("identifications", out var ii)) fac.EmisorCuit = GetStr(ii, "cuit");
        }
        if (root.TryGetProperty("recipient", out var rec) && rec.ValueKind == JsonValueKind.Object)
        {
            fac.ReceptorNombre = GetStr(rec, "name");
            if (rec.TryGetProperty("identifications", out var ri))
            {
                fac.ReceptorTaxType = GetStr(ri, "tax_type");
                fac.ReceptorDoc = GetStr(ri, "cuit") ?? GetStr(ri, "cuil") ?? GetStr(ri, "dni");
            }
        }
        // Letra: A si el receptor es responsable inscripto (tiene CUIT), B en el resto (consumidor final).
        var tt = (fac.ReceptorTaxType ?? "").ToUpperInvariant();
        fac.Letra = (tt.Contains("RESPONSABLE") || tt.Contains("INSCRIPTO")) ? "A" : "B";

        // Importes desde fiscal_data.fiscal_amounts.
        if (root.TryGetProperty("amount", out var am) && am.TryGetDecimal(out var total)) fac.Total = total;
        decimal iva = 0m; decimal? neto = null;
        if (root.TryGetProperty("fiscal_data", out var fd) && fd.TryGetProperty("fiscal_amounts", out var fa)
            && fa.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in fa.EnumerateArray())
            {
                var name = GetStr(el, "name");
                if (!el.TryGetProperty("attributes", out var at)) continue;
                if (name == "IVA" && at.TryGetProperty("viva", out var viva) && viva.TryGetDecimal(out var vivaV))
                    iva += vivaV;
                else if (name == "original_value" && at.TryGetProperty("value", out var ov) && ov.TryGetDecimal(out var ovV))
                    neto = ovV;
            }
        }
        fac.Iva = iva;
        fac.Neto = neto ?? (fac.Total - iva);
        if (fac.Provincia is null && root.TryGetProperty("shipment", out var sh) && sh.TryGetProperty("destination", out var de)
            && de.TryGetProperty("state", out var stt) && stt.TryGetProperty("name", out var stn) && stn.ValueKind == JsonValueKind.String)
            fac.Provincia = stn.GetString();
    }

    private static string? GetStr(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Empresas (CUIT emisor) que aparecen en las facturas bajadas, para el filtro "por empresa".</summary>
    public async Task<List<ContadoraEmpresaDto>> GetEmpresasAsync()
    {
        return await _db.MeliFacturas
            .Where(f => f.EmisorCuit != null)
            .GroupBy(f => new { f.EmisorCuit, f.EmisorNombre })
            .Select(g => new ContadoraEmpresaDto { Cuit = g.Key.EmisorCuit!, Nombre = g.Key.EmisorNombre ?? g.Key.EmisorCuit! })
            .OrderBy(e => e.Nombre)
            .ToListAsync();
    }

    private IQueryable<MeliFactura> FiltrarFacturas(DateTime? desde, DateTime? hasta, string? empresaCuit,
        int? puntoVenta, string? letra, string? provincia, string? search)
    {
        var q = _db.MeliFacturas.Where(f => f.Status != "SIN_FACTURA");
        if (desde.HasValue) q = q.Where(f => f.FechaEmision >= desde.Value);
        if (hasta.HasValue) { var h = hasta.Value.Date.AddDays(1); q = q.Where(f => f.FechaEmision < h); }
        if (!string.IsNullOrWhiteSpace(empresaCuit)) q = q.Where(f => f.EmisorCuit == empresaCuit);
        if (puntoVenta.HasValue) q = q.Where(f => f.PuntoVenta == puntoVenta.Value);
        if (!string.IsNullOrWhiteSpace(letra)) q = q.Where(f => f.Letra == letra);
        if (!string.IsNullOrWhiteSpace(provincia)) q = q.Where(f => f.Provincia != null && f.Provincia.Contains(provincia));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(f => (f.ReceptorNombre != null && f.ReceptorNombre.Contains(s))
                          || (f.ReceptorDoc != null && f.ReceptorDoc.Contains(s))
                          || (f.NumeroComprobante != null && f.NumeroComprobante.ToString()!.Contains(s)));
        }
        return q;
    }

    /// <summary>Resumen del Libro IVA Ventas agrupado por empresa + punto de venta + letra.</summary>
    public async Task<ContadoraLibroIvaDto> GetLibroIvaVentasAsync(DateTime? desde, DateTime? hasta,
        string? empresaCuit, int? puntoVenta, string? letra, string? provincia, string? search)
    {
        var q = FiltrarFacturas(desde, hasta, empresaCuit, puntoVenta, letra, provincia, search);
        var datos = await q.Select(f => new { f.EmisorCuit, f.EmisorNombre, f.PuntoVenta, f.Letra, f.Neto, f.Iva, f.Total }).ToListAsync();

        var filas = datos
            .GroupBy(x => new { x.EmisorCuit, x.EmisorNombre, x.PuntoVenta, x.Letra })
            .Select(g => new LibroIvaResumenRowDto
            {
                EmpresaCuit = g.Key.EmisorCuit,
                EmpresaNombre = g.Key.EmisorNombre,
                PuntoVenta = g.Key.PuntoVenta,
                Letra = g.Key.Letra,
                Cantidad = g.Count(),
                Neto = g.Sum(x => x.Neto),
                Iva = g.Sum(x => x.Iva),
                Total = g.Sum(x => x.Total)
            })
            .OrderBy(r => r.EmpresaNombre).ThenBy(r => r.PuntoVenta).ThenBy(r => r.Letra)
            .ToList();

        var dto = new ContadoraLibroIvaDto
        {
            Filas = filas,
            CantidadTotal = filas.Sum(r => r.Cantidad),
            NetoTotal = filas.Sum(r => r.Neto),
            IvaTotal = filas.Sum(r => r.Iva),
            TotalTotal = filas.Sum(r => r.Total),
            SinFactura = await _db.MeliFacturas.CountAsync(f => f.Status == "SIN_FACTURA"),
            Pendientes = await _db.MeliOrders.Where(o => o.Status == "paid"
                && !_db.MeliFacturas.Any(f => f.MeliOrderId == o.MeliOrderId)).Select(o => o.MeliOrderId).Distinct().CountAsync()
        };
        return dto;
    }

    /// <summary>Detalle: lista de facturas (paginada) segun los filtros.</summary>
    public async Task<ContadoraFacturasPageDto> GetFacturasAsync(DateTime? desde, DateTime? hasta,
        string? empresaCuit, int? puntoVenta, string? letra, string? provincia, string? search, int page = 1, int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 500) pageSize = 50;
        var q = FiltrarFacturas(desde, hasta, empresaCuit, puntoVenta, letra, provincia, search);
        var total = await q.CountAsync();
        var items = await q
            .OrderByDescending(f => f.FechaEmision).ThenByDescending(f => f.NumeroComprobante)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(f => new ContadoraFacturaDto
            {
                MeliOrderId = f.MeliOrderId,
                EmpresaCuit = f.EmisorCuit,
                EmpresaNombre = f.EmisorNombre,
                PuntoVenta = f.PuntoVenta,
                NumeroComprobante = f.NumeroComprobante,
                FechaEmision = f.FechaEmision,
                Letra = f.Letra,
                ReceptorNombre = f.ReceptorNombre,
                ReceptorDoc = f.ReceptorDoc,
                Provincia = f.Provincia,
                Neto = f.Neto, Iva = f.Iva, Total = f.Total
            })
            .ToListAsync();
        return new ContadoraFacturasPageDto { Items = items, Total = total, Page = page, PageSize = pageSize };
    }

    /// <summary>Excel del Libro IVA Ventas: una hoja resumen + una hoja detalle (segun filtros).</summary>
    public async Task<byte[]> GenerarLibroIvaExcelAsync(DateTime? desde, DateTime? hasta,
        string? empresaCuit, int? puntoVenta, string? letra, string? provincia, string? search)
    {
        var resumen = await GetLibroIvaVentasAsync(desde, hasta, empresaCuit, puntoVenta, letra, provincia, search);
        var detalle = await GetFacturasAsync(desde, hasta, empresaCuit, puntoVenta, letra, provincia, search, 1, 500);

        using var wb = new ClosedXML.Excel.XLWorkbook();
        var wr = wb.AddWorksheet("Resumen");
        int r = 1;
        foreach (var h in new[] { "Empresa", "CUIT", "Pto Venta", "Tipo", "Cantidad", "Neto", "IVA", "Total" })
            wr.Cell(1, r++).Value = h;
        wr.Range(1, 1, 1, 8).Style.Font.Bold = true;
        r = 2;
        foreach (var f in resumen.Filas)
        {
            wr.Cell(r, 1).Value = f.EmpresaNombre; wr.Cell(r, 2).Value = f.EmpresaCuit;
            wr.Cell(r, 3).Value = f.PuntoVenta; wr.Cell(r, 4).Value = f.Letra;
            wr.Cell(r, 5).Value = f.Cantidad; wr.Cell(r, 6).Value = f.Neto;
            wr.Cell(r, 7).Value = f.Iva; wr.Cell(r, 8).Value = f.Total;
            r++;
        }
        wr.Cell(r, 1).Value = "TOTALES"; wr.Cell(r, 5).Value = resumen.CantidadTotal;
        wr.Cell(r, 6).Value = resumen.NetoTotal; wr.Cell(r, 7).Value = resumen.IvaTotal; wr.Cell(r, 8).Value = resumen.TotalTotal;
        wr.Range(r, 1, r, 8).Style.Font.Bold = true;
        wr.Range(2, 6, r, 8).Style.NumberFormat.Format = "#,##0.00";
        wr.Columns().AdjustToContents();

        var wd = wb.AddWorksheet("Detalle");
        int c = 1;
        foreach (var h in new[] { "Fecha", "Empresa", "Pto Venta", "Numero", "Tipo", "Cliente", "Doc", "Provincia", "Neto", "IVA", "Total" })
            wd.Cell(1, c++).Value = h;
        wd.Range(1, 1, 1, 11).Style.Font.Bold = true;
        int rr = 2;
        foreach (var f in detalle.Items)
        {
            wd.Cell(rr, 1).Value = f.FechaEmision?.ToString("dd/MM/yyyy");
            wd.Cell(rr, 2).Value = f.EmpresaNombre;
            wd.Cell(rr, 3).Value = f.PuntoVenta;
            wd.Cell(rr, 4).Value = f.NumeroComprobante;
            wd.Cell(rr, 5).Value = f.Letra;
            wd.Cell(rr, 6).Value = f.ReceptorNombre;
            wd.Cell(rr, 7).Value = f.ReceptorDoc;
            wd.Cell(rr, 8).Value = f.Provincia;
            wd.Cell(rr, 9).Value = f.Neto; wd.Cell(rr, 10).Value = f.Iva; wd.Cell(rr, 11).Value = f.Total;
            rr++;
        }
        wd.Range(2, 9, rr, 11).Style.NumberFormat.Format = "#,##0.00";
        wd.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
