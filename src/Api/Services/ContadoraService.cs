using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Api.Data;
using Api.DTOs;
using Api.Models;
using ClosedXML.Excel;
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
    private readonly FileStorageService _storage;
    private readonly ILogger<ContadoraService> _logger;
    private const decimal IvaAlicuota = 0.21m;
    private const string SinDato = "(sin dato)";

    public ContadoraService(AppDbContext db, IHttpClientFactory httpFactory, MeliAccountService accountService,
        FileStorageService storage, ILogger<ContadoraService> logger)
    {
        _db = db; _httpFactory = httpFactory; _accountService = accountService; _storage = storage; _logger = logger;
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
            catch (Exception ex)
            {
                res.Errores++;
                // Descartar la entidad no guardada para que no rompa el proximo SaveChanges del mismo contexto.
                _db.ChangeTracker.Clear();
                if (res.Mensaje is null) res.Mensaje = ex.GetBaseException().Message;
                _logger.LogError(ex, "[Contadora] Error bajando factura de orden {Order}", par.MeliOrderId);
            }
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
        fac.InvoiceId = LongOf(root, "id") ?? 0;
        var serie = LongOf(root, "invoice_series");
        if (serie.HasValue) fac.PuntoVenta = (int)serie.Value;
        fac.NumeroComprobante = LongOf(root, "invoice_number");
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
        fac.Total = DecProp(root, "amount") ?? 0m;
        decimal iva = 0m; decimal? neto = null;
        if (root.TryGetProperty("fiscal_data", out var fd) && fd.TryGetProperty("fiscal_amounts", out var fa)
            && fa.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in fa.EnumerateArray())
            {
                var name = GetStr(el, "name");
                if (!el.TryGetProperty("attributes", out var at)) continue;
                if (name == "IVA") iva += DecProp(at, "viva") ?? 0m;
                else if (name == "original_value") neto = DecProp(at, "value");
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

    /// <summary>Lee un entero largo que MeLi puede mandar como numero O como texto.</summary>
    private static long? LongOf(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    /// <summary>Lee un decimal que MeLi puede mandar como numero O como texto.</summary>
    private static decimal? DecProp(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(),
                System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

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

    // ═══════════════════ ETAPA 3: Importar el reporte oficial de MeLi (con notas de credito) ═══════════════════

    /// <summary>
    /// Importa el/los Excel del "Reporte de facturas y notas de creditos" de MeLi que estan en una
    /// subcarpeta de la Carpeta Compartida (por ej. "Compartido/facturas meli"). Lee todos los .xlsx.
    /// </summary>
    public async Task<ContadoraImportResultDto> ImportarReporteCarpetaAsync(string subcarpeta)
    {
        var res = new ContadoraImportResultDto();
        string full;
        try { full = _storage.ResolveSafe(subcarpeta); }
        catch { res.Ok = false; res.Mensaje = "Carpeta invalida."; return res; }
        if (!Directory.Exists(full)) { res.Ok = false; res.Mensaje = $"No existe la carpeta '{subcarpeta}'."; return res; }

        var archivos = Directory.EnumerateFiles(full, "*.xlsx", SearchOption.TopDirectoryOnly)
            .Where(f => !Path.GetFileName(f).StartsWith("~$"))
            .OrderBy(f => f).ToList();
        if (archivos.Count == 0) { res.Ok = false; res.Mensaje = $"No hay archivos .xlsx en '{subcarpeta}'."; return res; }

        foreach (var path in archivos)
        {
            using var fs = File.OpenRead(path);
            var a = await ImportarUnArchivoAsync(fs, Path.GetFileName(path));
            AcumularArchivo(res, a);
        }
        res.Mensaje = $"Importados {res.Facturas} facturas y {res.NotasCredito} notas de credito ({res.Nuevos} nuevos, {res.Actualizados} actualizados).";
        return res;
    }

    /// <summary>Importa una lista de archivos subidos (nombre + contenido).</summary>
    public async Task<ContadoraImportResultDto> ImportarReporteArchivosAsync(IEnumerable<(string nombre, Stream contenido)> archivos)
    {
        var res = new ContadoraImportResultDto();
        foreach (var (nombre, contenido) in archivos)
        {
            var a = await ImportarUnArchivoAsync(contenido, nombre);
            AcumularArchivo(res, a);
        }
        if (res.Archivos.Count == 0) { res.Ok = false; res.Mensaje = "No se recibio ningun archivo."; return res; }
        res.Mensaje = $"Importados {res.Facturas} facturas y {res.NotasCredito} notas de credito ({res.Nuevos} nuevos, {res.Actualizados} actualizados).";
        return res;
    }

    private static void AcumularArchivo(ContadoraImportResultDto res, ContadoraImportArchivoDto a)
    {
        res.Archivos.Add(a);
        if (!a.Ok) { res.Ok = res.Ok && res.Archivos.Any(x => x.Ok); return; }
        res.Facturas += a.Facturas; res.NotasCredito += a.NotasCredito;
        res.Nuevos += a.Nuevos; res.Actualizados += a.Actualizados;
    }

    /// <summary>Parsea un Excel de reporte y hace upsert (por IdComprobante) en ContadoraComprobantes.</summary>
    private async Task<ContadoraImportArchivoDto> ImportarUnArchivoAsync(Stream stream, string nombre)
    {
        var a = new ContadoraImportArchivoDto { Archivo = nombre };
        List<ContadoraComprobante> parsed;
        int leidas = 0, omitidos = 0;
        string? cuit = null;
        try
        {
            using var wb = new XLWorkbook(stream);
            var ws = wb.Worksheets.FirstOrDefault(w => w.Name.Trim().Equals("Facturas", StringComparison.OrdinalIgnoreCase))
                     ?? wb.Worksheets.OrderByDescending(w => w.LastRowUsed()?.RowNumber() ?? 0).First();

            // Fila de titulos: la que tiene "Numero de venta" en alguna celda (primeras 15 filas).
            int headerRow = 0;
            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
            var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
            for (int r = 1; r <= Math.Min(15, lastRow) && headerRow == 0; r++)
                for (int c = 1; c <= lastCol; c++)
                    if (Norm(ws.Cell(r, c).GetString()) == "numero de venta") { headerRow = r; break; }
            if (headerRow == 0) { a.Ok = false; a.Error = "No parece un reporte de MeLi (no encontre los titulos)."; return a; }

            // Mapa normalizado titulo -> columna.
            var map = new Dictionary<string, int>();
            for (int c = 1; c <= lastCol; c++)
            {
                var h = Norm(ws.Cell(headerRow, c).GetString());
                if (h.Length > 0 && !map.ContainsKey(h)) map[h] = c;
            }
            int Col(params string[] keys)
            {
                foreach (var k in keys) if (map.TryGetValue(k, out var c)) return c;
                return 0;
            }

            // CUIT del emisor: celda del encabezado que arranca con "CUIT".
            for (int r = 1; r < headerRow && cuit == null; r++)
                for (int c = 1; c <= lastCol; c++)
                {
                    var t = ws.Cell(r, c).GetString();
                    if (Norm(t).StartsWith("cuit")) { cuit = SoloDigitos(t); break; }
                }
            a.EmpresaCuit = cuit;

            int cNumVenta = Col("numero de venta"), cNumEnvio = Col("numero de envio"),
                cTipoOp = Col("tipo de operacion"), cNumFac = Col("numero de factura"),
                cPtoVta = Col("punto de venta"), cCae = Col("cae"), cFecha = Col("fecha de emision"),
                cTipoComp = Col("tipo de comprobante"), cEstado = Col("estado del comprobante"),
                cIdComp = Col("id del comprobante"), cTipoDoc = Col("tipo de documento"),
                cNumDoc = Col("numero de documento"), cCondIva = Col("condicion de iva"),
                cNombre = Col("nombre / razon social", "nombre / razon socia", "nombre/razon social"),
                cProv = Col("provincia"), cProvEnv = Col("provincia de envio"),
                cNeto = Col("imp. neto gravado", "imp neto gravado"),
                cBase105 = Col("base iva 10,5%", "base iva 10.5%"), cIva105 = Col("importe iva 10,5%", "importe iva 10.5%"),
                cBase21 = Col("base iva 21%"), cIva21 = Col("importe iva 21%"),
                cEnvNeto = Col("costos neto envio"), cEnvIva = Col("importe iva envio"), cEnvTot = Col("importe total envio"),
                cConcep = Col("importe conceptos"), cOtros = Col("importe otros impuestos"),
                cNoGrav = Col("importe neto no gravado"), cExento = Col("importe exento"), cTotal = Col("importe total");

            if (cIdComp == 0 || cTipoComp == 0) { a.Ok = false; a.Error = "Faltan columnas clave (ID del comprobante / Tipo de comprobante)."; return a; }

            var porId = new Dictionary<string, ContadoraComprobante>();
            for (int r = headerRow + 1; r <= lastRow; r++)
            {
                leidas++;
                var tipoComp = ws.Cell(r, cTipoComp).GetString().Trim();
                if (tipoComp.Length == 0) { omitidos++; continue; }          // fila de detalle de producto
                var estado = cEstado > 0 ? ws.Cell(r, cEstado).GetString().Trim() : "";
                if (estado.Length > 0 && !estado.Equals("Aprobada", StringComparison.OrdinalIgnoreCase)) { omitidos++; continue; }
                var idComp = ws.Cell(r, cIdComp).GetString().Trim();
                if (idComp.Length == 0) { omitidos++; continue; }

                var esNC = Norm(tipoComp).StartsWith("nota de cred");
                var e = new ContadoraComprobante
                {
                    Origen = "MELI_REPORTE",
                    EmisorCuit = cuit,
                    IdComprobante = idComp,
                    NumeroVenta = cNumVenta > 0 ? Lng(ws.Cell(r, cNumVenta)) : null,
                    NumeroEnvio = cNumEnvio > 0 ? Lng(ws.Cell(r, cNumEnvio)) : null,
                    TipoOperacion = cTipoOp > 0 ? NullIfEmpty(ws.Cell(r, cTipoOp).GetString()) : null,
                    TipoComprobante = tipoComp,
                    EsNotaCredito = esNC,
                    Letra = Norm(tipoComp).EndsWith("a") ? "A" : (Norm(tipoComp).EndsWith("b") ? "B" : null),
                    PuntoVenta = cPtoVta > 0 ? (int?)(Lng(ws.Cell(r, cPtoVta)) ?? 0) : null,
                    NumeroComprobante = cNumFac > 0 ? Lng(ws.Cell(r, cNumFac)) : null,
                    Cae = cCae > 0 ? NullIfEmpty(ws.Cell(r, cCae).GetString()) : null,
                    FechaEmision = cFecha > 0 ? Fecha(ws.Cell(r, cFecha)) : null,
                    Estado = NullIfEmpty(estado),
                    ReceptorTipoDoc = cTipoDoc > 0 ? NullIfEmpty(ws.Cell(r, cTipoDoc).GetString()) : null,
                    ReceptorDoc = cNumDoc > 0 ? NullIfEmpty(ws.Cell(r, cNumDoc).GetString()) : null,
                    ReceptorCondIva = cCondIva > 0 ? NullIfEmpty(ws.Cell(r, cCondIva).GetString()) : null,
                    ReceptorNombre = cNombre > 0 ? NullIfEmpty(ws.Cell(r, cNombre).GetString()) : null,
                    Provincia = cProv > 0 ? NullIfEmpty(ws.Cell(r, cProv).GetString()) : null,
                    ProvinciaEnvio = cProvEnv > 0 ? NullIfEmpty(ws.Cell(r, cProvEnv).GetString()) : null,
                    NetoGravado = Dec(ws, r, cNeto), BaseIva105 = Dec(ws, r, cBase105), Iva105 = Dec(ws, r, cIva105),
                    BaseIva21 = Dec(ws, r, cBase21), Iva21 = Dec(ws, r, cIva21),
                    EnvioNeto = Dec(ws, r, cEnvNeto), EnvioIva = Dec(ws, r, cEnvIva), EnvioTotal = Dec(ws, r, cEnvTot),
                    Conceptos = Dec(ws, r, cConcep), OtrosImpuestos = Dec(ws, r, cOtros),
                    NoGravado = Dec(ws, r, cNoGrav), Exento = Dec(ws, r, cExento), Total = Dec(ws, r, cTotal),
                    ArchivoOrigen = nombre,
                    ImportadoEn = DateTime.UtcNow
                };
                porId[idComp] = e; // ultimo gana si un mismo id apareciera repetido en el archivo
            }
            parsed = porId.Values.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Contadora] Error leyendo reporte {Archivo}", nombre);
            a.Ok = false; a.Error = "No pude leer el Excel: " + ex.GetBaseException().Message;
            return a;
        }

        // Upsert por IdComprobante (en bloques para no exceder parametros de SQL).
        var ids = parsed.Select(p => p.IdComprobante).ToList();
        var existentes = new Dictionary<string, ContadoraComprobante>();
        foreach (var chunk in Chunk(ids, 1000))
        {
            var found = await _db.ContadoraComprobantes.Where(c => chunk.Contains(c.IdComprobante)).ToListAsync();
            foreach (var f in found) existentes[f.IdComprobante] = f;
        }

        foreach (var p in parsed)
        {
            if (existentes.TryGetValue(p.IdComprobante, out var ex))
            {
                CopiarCampos(p, ex);
                a.Actualizados++;
            }
            else { _db.ContadoraComprobantes.Add(p); a.Nuevos++; }

            int signo = p.EsNotaCredito ? -1 : 1;
            if (p.EsNotaCredito) a.NotasCredito++; else a.Facturas++;
            a.NetoNeto += signo * p.NetoGravado;
            a.IvaNeto += signo * (p.Iva21 + p.Iva105);
            a.TotalNeto += signo * p.Total;
            if (p.FechaEmision.HasValue)
            {
                if (!a.PeriodoDesde.HasValue || p.FechaEmision < a.PeriodoDesde) a.PeriodoDesde = p.FechaEmision;
                if (!a.PeriodoHasta.HasValue || p.FechaEmision > a.PeriodoHasta) a.PeriodoHasta = p.FechaEmision;
            }
        }
        await _db.SaveChangesAsync();
        _logger.LogInformation("[Contadora] Reporte {Archivo}: {Fac} facturas, {NC} NC, {Nuevos} nuevos, {Act} act.",
            nombre, a.Facturas, a.NotasCredito, a.Nuevos, a.Actualizados);
        return a;
    }

    private static void CopiarCampos(ContadoraComprobante src, ContadoraComprobante dst)
    {
        dst.EmisorCuit = src.EmisorCuit; dst.NumeroVenta = src.NumeroVenta; dst.NumeroEnvio = src.NumeroEnvio;
        dst.TipoOperacion = src.TipoOperacion; dst.TipoComprobante = src.TipoComprobante; dst.EsNotaCredito = src.EsNotaCredito;
        dst.Letra = src.Letra; dst.PuntoVenta = src.PuntoVenta; dst.NumeroComprobante = src.NumeroComprobante;
        dst.Cae = src.Cae; dst.FechaEmision = src.FechaEmision; dst.Estado = src.Estado;
        dst.ReceptorTipoDoc = src.ReceptorTipoDoc; dst.ReceptorDoc = src.ReceptorDoc; dst.ReceptorCondIva = src.ReceptorCondIva;
        dst.ReceptorNombre = src.ReceptorNombre; dst.Provincia = src.Provincia; dst.ProvinciaEnvio = src.ProvinciaEnvio;
        dst.NetoGravado = src.NetoGravado; dst.BaseIva105 = src.BaseIva105; dst.Iva105 = src.Iva105;
        dst.BaseIva21 = src.BaseIva21; dst.Iva21 = src.Iva21;
        dst.EnvioNeto = src.EnvioNeto; dst.EnvioIva = src.EnvioIva; dst.EnvioTotal = src.EnvioTotal;
        dst.Conceptos = src.Conceptos; dst.OtrosImpuestos = src.OtrosImpuestos; dst.NoGravado = src.NoGravado;
        dst.Exento = src.Exento; dst.Total = src.Total; dst.ArchivoOrigen = src.ArchivoOrigen; dst.ImportadoEn = src.ImportadoEn;
    }

    // ── Consultas sobre los comprobantes importados (con NC restando) ──

    private IQueryable<ContadoraComprobante> FiltrarComprobantes(DateTime? desde, DateTime? hasta,
        string? empresaCuit, int? puntoVenta, string? letra, string? provincia, string? search)
    {
        var q = _db.ContadoraComprobantes.AsQueryable();
        if (desde.HasValue) q = q.Where(c => c.FechaEmision >= desde.Value);
        if (hasta.HasValue) { var h = hasta.Value.Date.AddDays(1); q = q.Where(c => c.FechaEmision < h); }
        if (!string.IsNullOrWhiteSpace(empresaCuit)) q = q.Where(c => c.EmisorCuit == empresaCuit);
        if (puntoVenta.HasValue) q = q.Where(c => c.PuntoVenta == puntoVenta.Value);
        if (!string.IsNullOrWhiteSpace(letra)) q = q.Where(c => c.Letra == letra);
        if (!string.IsNullOrWhiteSpace(provincia)) q = q.Where(c => c.Provincia != null && c.Provincia.Contains(provincia));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(c => (c.ReceptorNombre != null && c.ReceptorNombre.Contains(s))
                          || (c.ReceptorDoc != null && c.ReceptorDoc.Contains(s))
                          || (c.NumeroComprobante != null && c.NumeroComprobante.ToString()!.Contains(s)));
        }
        return q;
    }

    /// <summary>Empresas (CUIT) que aparecen en los comprobantes importados.</summary>
    public async Task<List<ContadoraEmpresaDto>> GetReporteEmpresasAsync()
    {
        return await _db.ContadoraComprobantes.Where(c => c.EmisorCuit != null)
            .GroupBy(c => c.EmisorCuit!)
            .Select(g => new ContadoraEmpresaDto { Cuit = g.Key, Nombre = g.Key })
            .OrderBy(e => e.Cuit).ToListAsync();
    }

    /// <summary>Resumen del Libro IVA Ventas desde los comprobantes importados (NC restan).</summary>
    public async Task<ContadoraReporteResumenDto> GetReporteResumenAsync(DateTime? desde, DateTime? hasta,
        string? empresaCuit, int? puntoVenta, string? letra, string? provincia, string? search)
    {
        var dto = new ContadoraReporteResumenDto();
        dto.SinDatos = !await _db.ContadoraComprobantes.AnyAsync();

        var datos = await FiltrarComprobantes(desde, hasta, empresaCuit, puntoVenta, letra, provincia, search)
            .Select(c => new { c.EmisorCuit, c.PuntoVenta, c.Letra, c.EsNotaCredito, c.NetoGravado, c.Iva21, c.Iva105, c.Total })
            .ToListAsync();

        dto.Filas = datos
            .GroupBy(x => new { x.EmisorCuit, x.PuntoVenta, x.Letra })
            .Select(g => new LibroIvaResumenRowDto
            {
                EmpresaCuit = g.Key.EmisorCuit,
                EmpresaNombre = g.Key.EmisorCuit,
                PuntoVenta = g.Key.PuntoVenta,
                Letra = g.Key.Letra,
                Cantidad = g.Count(),
                Neto = g.Sum(x => (x.EsNotaCredito ? -1 : 1) * x.NetoGravado),
                Iva = g.Sum(x => (x.EsNotaCredito ? -1 : 1) * (x.Iva21 + x.Iva105)),
                Total = g.Sum(x => (x.EsNotaCredito ? -1 : 1) * x.Total)
            })
            .OrderBy(r => r.EmpresaCuit).ThenBy(r => r.PuntoVenta).ThenBy(r => r.Letra)
            .ToList();

        dto.CantidadFacturas = datos.Count(x => !x.EsNotaCredito);
        dto.CantidadNotasCredito = datos.Count(x => x.EsNotaCredito);
        dto.NetoTotal = dto.Filas.Sum(r => r.Neto);
        dto.IvaTotal = dto.Filas.Sum(r => r.Iva);
        dto.TotalTotal = dto.Filas.Sum(r => r.Total);
        return dto;
    }

    /// <summary>Meses cargados (para mostrar "lo que ya tengo importado").</summary>
    public async Task<List<ContadoraCargaDto>> GetReporteCargasAsync(string? empresaCuit)
    {
        var q = _db.ContadoraComprobantes.Where(c => c.FechaEmision != null);
        if (!string.IsNullOrWhiteSpace(empresaCuit)) q = q.Where(c => c.EmisorCuit == empresaCuit);
        var datos = await q.Select(c => new { c.EmisorCuit, c.FechaEmision, c.EsNotaCredito, c.NetoGravado, c.Iva21, c.Iva105, c.Total }).ToListAsync();
        return datos
            .GroupBy(x => new { x.EmisorCuit, x.FechaEmision!.Value.Year, x.FechaEmision!.Value.Month })
            .Select(g => new ContadoraCargaDto
            {
                EmpresaCuit = g.Key.EmisorCuit,
                Anio = g.Key.Year, Mes = g.Key.Month,
                Facturas = g.Count(x => !x.EsNotaCredito),
                NotasCredito = g.Count(x => x.EsNotaCredito),
                NetoNeto = g.Sum(x => (x.EsNotaCredito ? -1 : 1) * x.NetoGravado),
                IvaNeto = g.Sum(x => (x.EsNotaCredito ? -1 : 1) * (x.Iva21 + x.Iva105)),
                TotalNeto = g.Sum(x => (x.EsNotaCredito ? -1 : 1) * x.Total)
            })
            .OrderByDescending(c => c.Anio).ThenByDescending(c => c.Mes).ThenBy(c => c.EmpresaCuit)
            .ToList();
    }

    /// <summary>Detalle paginado de comprobantes importados (NC con importes en negativo).</summary>
    public async Task<ContadoraComprobantesPageDto> GetReporteComprobantesAsync(DateTime? desde, DateTime? hasta,
        string? empresaCuit, int? puntoVenta, string? letra, string? provincia, string? search, int page = 1, int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 500) pageSize = 50;
        var q = FiltrarComprobantes(desde, hasta, empresaCuit, puntoVenta, letra, provincia, search);
        var total = await q.CountAsync();
        var raw = await q.OrderByDescending(c => c.FechaEmision).ThenByDescending(c => c.NumeroComprobante)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(c => new { c.IdComprobante, c.EmisorCuit, c.EsNotaCredito, c.TipoComprobante, c.PuntoVenta, c.NumeroComprobante,
                c.FechaEmision, c.Letra, c.ReceptorNombre, c.ReceptorDoc, c.Provincia, c.NetoGravado, c.Iva21, c.Iva105, c.Total })
            .ToListAsync();
        var items = raw.Select(c => new ContadoraComprobanteDto
        {
            IdComprobante = c.IdComprobante, EmpresaCuit = c.EmisorCuit, EsNotaCredito = c.EsNotaCredito,
            TipoComprobante = c.TipoComprobante, PuntoVenta = c.PuntoVenta, NumeroComprobante = c.NumeroComprobante,
            FechaEmision = c.FechaEmision, Letra = c.Letra, ReceptorNombre = c.ReceptorNombre, ReceptorDoc = c.ReceptorDoc,
            Provincia = c.Provincia,
            Neto = (c.EsNotaCredito ? -1 : 1) * c.NetoGravado,
            Iva = (c.EsNotaCredito ? -1 : 1) * (c.Iva21 + c.Iva105),
            Total = (c.EsNotaCredito ? -1 : 1) * c.Total
        }).ToList();
        return new ContadoraComprobantesPageDto { Items = items, Total = total, Page = page, PageSize = pageSize };
    }

    /// <summary>Excel del Libro IVA Ventas (importado): hoja resumen + hoja detalle. NC en negativo.</summary>
    public async Task<byte[]> GenerarReporteExcelAsync(DateTime? desde, DateTime? hasta,
        string? empresaCuit, int? puntoVenta, string? letra, string? provincia, string? search)
    {
        var resumen = await GetReporteResumenAsync(desde, hasta, empresaCuit, puntoVenta, letra, provincia, search);
        var detalle = await GetReporteComprobantesAsync(desde, hasta, empresaCuit, puntoVenta, letra, provincia, search, 1, 500);

        using var wb = new XLWorkbook();
        var wr = wb.AddWorksheet("Resumen");
        int r = 1;
        foreach (var h in new[] { "CUIT", "Pto Venta", "Tipo", "Cantidad", "Neto", "IVA", "Total" }) wr.Cell(1, r++).Value = h;
        wr.Range(1, 1, 1, 7).Style.Font.Bold = true;
        r = 2;
        foreach (var f in resumen.Filas)
        {
            wr.Cell(r, 1).Value = f.EmpresaCuit; wr.Cell(r, 2).Value = f.PuntoVenta; wr.Cell(r, 3).Value = f.Letra;
            wr.Cell(r, 4).Value = f.Cantidad; wr.Cell(r, 5).Value = f.Neto; wr.Cell(r, 6).Value = f.Iva; wr.Cell(r, 7).Value = f.Total;
            r++;
        }
        wr.Cell(r, 1).Value = "TOTALES (NC ya restadas)";
        wr.Cell(r, 5).Value = resumen.NetoTotal; wr.Cell(r, 6).Value = resumen.IvaTotal; wr.Cell(r, 7).Value = resumen.TotalTotal;
        wr.Range(r, 1, r, 7).Style.Font.Bold = true;
        wr.Range(2, 5, r, 7).Style.NumberFormat.Format = "#,##0.00";
        wr.Columns().AdjustToContents();

        var wd = wb.AddWorksheet("Detalle");
        int c = 1;
        foreach (var h in new[] { "Fecha", "CUIT", "Pto Venta", "Numero", "Comprobante", "Cliente", "Doc", "Provincia", "Neto", "IVA", "Total" })
            wd.Cell(1, c++).Value = h;
        wd.Range(1, 1, 1, 11).Style.Font.Bold = true;
        int rr = 2;
        foreach (var f in detalle.Items)
        {
            wd.Cell(rr, 1).Value = f.FechaEmision?.ToString("dd/MM/yyyy");
            wd.Cell(rr, 2).Value = f.EmpresaCuit; wd.Cell(rr, 3).Value = f.PuntoVenta; wd.Cell(rr, 4).Value = f.NumeroComprobante;
            wd.Cell(rr, 5).Value = f.TipoComprobante; wd.Cell(rr, 6).Value = f.ReceptorNombre; wd.Cell(rr, 7).Value = f.ReceptorDoc;
            wd.Cell(rr, 8).Value = f.Provincia; wd.Cell(rr, 9).Value = f.Neto; wd.Cell(rr, 10).Value = f.Iva; wd.Cell(rr, 11).Value = f.Total;
            rr++;
        }
        wd.Range(2, 9, rr, 11).Style.NumberFormat.Format = "#,##0.00";
        wd.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // ── Helpers de parseo del Excel ──

    private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
    {
        for (int i = 0; i < source.Count; i += size) yield return source.GetRange(i, Math.Min(size, source.Count - i));
    }

    private static string? NullIfEmpty(string? s) { s = s?.Trim(); return string.IsNullOrEmpty(s) ? null : s; }

    /// <summary>Normaliza texto: minusculas, sin acentos, espacios colapsados. Para comparar titulos.</summary>
    private static string Norm(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = s.Trim().ToLowerInvariant().Replace("\n", " ").Replace("\r", " ");
        var sb = new StringBuilder();
        foreach (var ch in t.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        var r = sb.ToString().Normalize(NormalizationForm.FormC);
        while (r.Contains("  ")) r = r.Replace("  ", " ");
        return r.Trim();
    }

    private static string? SoloDigitos(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var sb = new StringBuilder();
        foreach (var ch in s) if (char.IsDigit(ch)) sb.Append(ch);
        return sb.Length == 0 ? null : sb.ToString();
    }

    private static long? Lng(IXLCell cell)
    {
        if (cell == null || cell.IsEmpty()) return null;
        if (cell.DataType == XLDataType.Number) return (long)Math.Round(cell.GetDouble());
        var s = SoloDigitos(cell.GetString());
        return s != null && long.TryParse(s, out var n) ? n : null;
    }

    private static DateTime? Fecha(IXLCell cell)
    {
        if (cell == null || cell.IsEmpty()) return null;
        if (cell.DataType == XLDataType.DateTime) { try { return cell.GetDateTime(); } catch { } }
        var s = cell.GetString().Trim();
        string[] fmts = { "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy H:mm:ss", "dd/MM/yyyy HH:mm", "dd/MM/yyyy", "d/M/yyyy HH:mm:ss", "d/M/yyyy" };
        if (DateTime.TryParseExact(s, fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        return DateTime.TryParse(s, CultureInfo.GetCultureInfo("es-AR"), DateTimeStyles.None, out var d2) ? d2 : null;
    }

    /// <summary>Lee un monto de una celda. Acepta numero o texto tipo "$ 1.234,56" (es-AR).</summary>
    private static decimal Dec(IXLWorksheet ws, int row, int col)
    {
        if (col <= 0) return 0m;
        var cell = ws.Cell(row, col);
        if (cell.IsEmpty()) return 0m;
        if (cell.DataType == XLDataType.Number) return (decimal)cell.GetDouble();
        var s = cell.GetString().Trim();
        if (s.Length == 0) return 0m;
        s = s.Replace("$", "").Replace(" ", "").Trim();
        // es-AR: miles con punto, decimales con coma.
        if (s.Contains(',')) s = s.Replace(".", "").Replace(",", ".");
        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
    }
}
