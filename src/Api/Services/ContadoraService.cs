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

    private readonly ArcaScrapingService _arca;

    public ContadoraService(AppDbContext db, IHttpClientFactory httpFactory, MeliAccountService accountService,
        FileStorageService storage, ILogger<ContadoraService> logger, ArcaScrapingService arca)
    {
        _db = db; _httpFactory = httpFactory; _accountService = accountService; _storage = storage; _logger = logger; _arca = arca;
    }

    /// <summary>Toma el CSV que el scraper de AFIP ya descargó (emitidos = ventas, recibidos = compras) en la última
    /// corrida y lo importa con el importador completo del Libro IVA. Así las compras (y ventas) entran de AFIP sin
    /// subir archivos a mano. Devuelve un resumen combinado.</summary>
    public async Task<ContadoraImportResultDto> ImportarUltimoScrapeAfipAsync()
    {
        var status = await _arca.GetStatusAsync();
        var r = status.Result;
        if (r is null || (string.IsNullOrEmpty(r.RecibidosCsv) && string.IsNullOrEmpty(r.EmitidosCsv)))
            return new ContadoraImportResultDto { Ok = false, Mensaje = "No hay una descarga de AFIP reciente para importar. Corré primero la bajada en Integraciones → ARCA." };

        var res = new ContadoraImportResultDto { Ok = true };
        var partes = new List<string>();

        if (!string.IsNullOrEmpty(r.RecibidosCsv))
        {
            using var ms = new MemoryStream(Convert.FromBase64String(r.RecibidosCsv));
            var comp = await ImportarComprasArchivosAsync(new[] { ("recibidos_afip.csv", (Stream)ms) });
            partes.Add($"Compras: {comp.Facturas} fac + {comp.NotasCredito} NC ({comp.Nuevos} nuevas)");
            res.Archivos.AddRange(comp.Archivos);
        }
        if (!string.IsNullOrEmpty(r.EmitidosCsv))
        {
            using var ms = new MemoryStream(Convert.FromBase64String(r.EmitidosCsv));
            var vent = await ImportarVentasAfipArchivosAsync(new[] { ("emitidos_afip.csv", (Stream)ms) });
            partes.Add($"Ventas: {vent.Facturas} fac + {vent.NotasCredito} NC ({vent.Nuevos} nuevas)");
            res.Archivos.AddRange(vent.Archivos);
        }
        res.Mensaje = "Importado de AFIP → " + string.Join(" · ", partes);
        _logger.LogInformation("[Contadora] Import scrape AFIP: {Msg}", res.Mensaje);
        return res;
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
            if (headerRow == 0)
            {
                // Puede ser el Excel de compras de AFIP (mismo directorio): lo ignoramos sin marcar error.
                if (EsRecibidosAfip(ws, lastRow, lastCol)) { a.Ok = true; a.Error = "(archivo de compras AFIP, se ignora en ventas)"; return a; }
                a.Ok = false; a.Error = "No parece un reporte de MeLi (no encontre los titulos)."; return a;
            }

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
                    Provincia = NormProv(cProv > 0 ? ws.Cell(r, cProv).GetString() : null),
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
        dst.Origen = src.Origen; dst.Concepto = src.Concepto;
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

    /// <summary>CUIT por defecto cuando la factura del sistema no tiene certificado vinculado (operamos con un solo CUIT).</summary>
    private const string CuitPorDefecto = "30717212149"; // PALANICA HERMANOS S.R.L.

    /// <summary>
    /// Sincroniza las facturas que emite NUESTRO sistema por AFIP (Cafe_Ventas con CAE) hacia
    /// ContadoraComprobantes con Origen='SISTEMA', para que aparezcan en el mismo Libro IVA que las
    /// de MeLi. Notas de credito restan. Todo interno (no depende de subir archivos). Idempotente:
    /// clave IdComprobante = "SIS-{VentaId}"; reprocesar solo actualiza.
    /// </summary>
    public async Task<ContadoraImportResultDto> SincronizarSistemaAsync()
    {
        var res = new ContadoraImportResultDto();
        var a = new ContadoraImportArchivoDto { Archivo = "Facturas del sistema (AFIP)" };

        string[] fiscales = { "FA", "FB", "FC", "NCA", "NCB", "NCC" };
        var ventas = await _db.CafeVentas
            .Where(v => v.ArcaCae != null && v.Estado != "anulado" && fiscales.Contains(v.TipoComprobante))
            .Select(v => new
            {
                v.Id, v.TipoComprobante, v.Concepto, v.ArcaPtoVta, v.ArcaCbteNro, v.ArcaCae, v.Fecha,
                v.ArcaWebserviceAccountId, v.CondicionIva,
                v.NombreReceptor, v.DniReceptor, v.ClienteRazonSocialSnapshot, v.ClienteNombreSnapshot, v.ClienteCuitSnapshot,
                v.ArcaImpNeto, v.ArcaImpIVA, v.ArcaImpTotal
            })
            .ToListAsync();

        // CUIT emisor por certificado.
        var cuitPorAccount = await _db.ArcaWebserviceAccounts.ToDictionaryAsync(w => w.Id, w => w.Cuit);

        var ids = ventas.Select(v => "SIS-" + v.Id).ToList();
        var existentes = new Dictionary<string, ContadoraComprobante>();
        foreach (var chunk in Chunk(ids, 1000))
            foreach (var f in await _db.ContadoraComprobantes.Where(c => chunk.Contains(c.IdComprobante)).ToListAsync())
                existentes[f.IdComprobante] = f;

        foreach (var v in ventas)
        {
            var tipo = v.TipoComprobante;
            var esNC = tipo.StartsWith("NC");
            var letra = tipo.EndsWith("A") ? "A" : tipo.EndsWith("B") ? "B" : tipo.EndsWith("C") ? "C" : null;
            string tipoLabel = tipo switch
            {
                "FA" => "Factura A", "FB" => "Factura B", "FC" => "Factura C",
                "NCA" => "Nota de Crédito A", "NCB" => "Nota de Crédito B", "NCC" => "Nota de Crédito C",
                _ => tipo
            };
            string? cuitReal = (v.ArcaWebserviceAccountId.HasValue && cuitPorAccount.TryGetValue(v.ArcaWebserviceAccountId.Value, out var cu) && !string.IsNullOrWhiteSpace(cu))
                ? cu : null;
            // PALANICA es Responsable Inscripto: SOLO emite A y B, nunca C. Una Factura/NC C es de otro CUIT
            // (monotributo de los hermanos). Si no sabemos su CUIT real, la salteamos para NO colgarsela a PALANICA.
            if (letra == "C" && (cuitReal == null || cuitReal == CuitPorDefecto)) continue;
            string? emisor = cuitReal ?? CuitPorDefecto;

            var e = new ContadoraComprobante
            {
                Origen = "SISTEMA",
                Concepto = v.Concepto,
                EmisorCuit = emisor,
                IdComprobante = "SIS-" + v.Id,
                TipoOperacion = esNC ? "Cancelacion" : "Venta",
                TipoComprobante = tipoLabel,
                EsNotaCredito = esNC,
                Letra = letra,
                PuntoVenta = v.ArcaPtoVta,
                NumeroComprobante = v.ArcaCbteNro,
                Cae = v.ArcaCae,
                FechaEmision = v.Fecha,
                Estado = "Aprobada",
                ReceptorCondIva = v.CondicionIva,
                ReceptorNombre = NullIfEmpty(v.NombreReceptor) ?? NullIfEmpty(v.ClienteRazonSocialSnapshot) ?? NullIfEmpty(v.ClienteNombreSnapshot),
                ReceptorDoc = NullIfEmpty(v.DniReceptor) ?? NullIfEmpty(v.ClienteCuitSnapshot),
                NetoGravado = v.ArcaImpNeto ?? 0m,
                BaseIva21 = v.ArcaImpNeto ?? 0m,
                Iva21 = v.ArcaImpIVA ?? 0m,   // el sistema guarda el IVA total (hoy 21%); FC = 0
                Total = v.ArcaImpTotal ?? 0m,
                ArchivoOrigen = "Sistema (AFIP)",
                ImportadoEn = DateTime.UtcNow
            };

            if (existentes.TryGetValue(e.IdComprobante, out var ex)) { CopiarCampos(e, ex); ex.Origen = "SISTEMA"; ex.Concepto = e.Concepto; a.Actualizados++; }
            else { _db.ContadoraComprobantes.Add(e); a.Nuevos++; }
            if (esNC) a.NotasCredito++; else a.Facturas++;
            int signo = esNC ? -1 : 1;
            a.NetoNeto += signo * e.NetoGravado; a.IvaNeto += signo * e.Iva21; a.TotalNeto += signo * e.Total;
        }
        await _db.SaveChangesAsync();
        a.EmpresaCuit = CuitPorDefecto;
        AcumularArchivo(res, a);
        res.Mensaje = $"Facturas del sistema: {a.Facturas} facturas y {a.NotasCredito} NC ({a.Nuevos} nuevas, {a.Actualizados} actualizadas).";
        _logger.LogInformation("[Contadora] Sync sistema: {Fac} fac, {NC} NC, {N} nuevas, {A} act.", a.Facturas, a.NotasCredito, a.Nuevos, a.Actualizados);
        return res;
    }

    /// <summary>Vuelca al Libro IVA Ventas las facturas de MercadoLibre que el robot ya bajó por la API
    /// (tabla MeliFacturas), como Origen='MELI_API'. Así las ventas de MeLi entran SOLAS, sin subir el reporte.
    /// OJO: la API de MeLi da facturas pero NO notas de crédito → para el total completo (con NC) manda AFIP,
    /// que gana en la deduplicación cuando está cargado. MELI_API es la cifra automática del día a día.</summary>
    public async Task<ContadoraImportResultDto> SincronizarMeliApiAsync()
    {
        var res = new ContadoraImportResultDto();
        var a = new ContadoraImportArchivoDto { Archivo = "Ventas de MercadoLibre (API)" };

        var facturas = await _db.MeliFacturas
            .Where(f => f.Status != null && f.Status != "SIN_FACTURA"
                && f.PuntoVenta != null && f.NumeroComprobante != null && f.EmisorCuit != null)
            .Select(f => new
            {
                f.PuntoVenta, f.NumeroComprobante, f.FechaEmision, f.Letra, f.EmisorCuit,
                f.ReceptorNombre, f.ReceptorDoc, f.ReceptorTaxType, f.Provincia, f.Neto, f.Iva, f.Total
            })
            .ToListAsync();

        // Una fila por comprobante (pto+letra+numero). Si por algún motivo hay repetidos, me quedo con el último.
        var porId = new Dictionary<string, ContadoraComprobante>();
        foreach (var f in facturas)
        {
            var id = $"MAPI-{f.PuntoVenta}-{f.Letra}-{f.NumeroComprobante}";
            porId[id] = new ContadoraComprobante
            {
                Naturaleza = "VENTA",
                Origen = "MELI_API",
                EmisorCuit = f.EmisorCuit,
                IdComprobante = id,
                TipoOperacion = "Venta",
                TipoComprobante = "Factura " + (f.Letra ?? ""),
                EsNotaCredito = false,
                Letra = f.Letra,
                PuntoVenta = f.PuntoVenta,
                NumeroComprobante = f.NumeroComprobante,
                FechaEmision = f.FechaEmision,
                Estado = "Aprobada",
                ReceptorCondIva = f.ReceptorTaxType,
                ReceptorNombre = f.ReceptorNombre,
                ReceptorDoc = f.ReceptorDoc,
                Provincia = NormProv(f.Provincia),
                NetoGravado = f.Neto, BaseIva21 = f.Neto,
                Iva21 = f.Iva,   // IVA del producto (la API no separa el del envío; AFIP lo trae completo)
                Total = f.Total,
                ArchivoOrigen = "MercadoLibre (API)",
                ImportadoEn = DateTime.UtcNow
            };
        }
        var parsed = porId.Values.ToList();

        var ids = parsed.Select(p => p.IdComprobante).ToList();
        var existentes = new Dictionary<string, ContadoraComprobante>();
        foreach (var chunk in Chunk(ids, 1000))
            foreach (var f in await _db.ContadoraComprobantes.Where(c => chunk.Contains(c.IdComprobante)).ToListAsync())
                existentes[f.IdComprobante] = f;

        foreach (var p in parsed)
        {
            if (existentes.TryGetValue(p.IdComprobante, out var ex)) { CopiarCampos(p, ex); ex.Provincia = p.Provincia ?? ex.Provincia; a.Actualizados++; }
            else { _db.ContadoraComprobantes.Add(p); a.Nuevos++; }
            a.Facturas++;
            a.NetoNeto += p.NetoGravado; a.IvaNeto += p.Iva21 + p.Iva105; a.TotalNeto += p.Total;
        }
        await _db.SaveChangesAsync();
        a.EmpresaCuit = parsed.FirstOrDefault()?.EmisorCuit;
        AcumularArchivo(res, a);
        res.Mensaje = $"Ventas de MeLi (API): {a.Facturas} facturas ({a.Nuevos} nuevas, {a.Actualizados} actualizadas).";
        _logger.LogInformation("[Contadora] Sync MeLi API: {Fac} fac, {N} nuevas, {A} act.", a.Facturas, a.Nuevos, a.Actualizados);
        return res;
    }

    // ═══════════════════ COMPRAS: importar "Mis Comprobantes Recibidos" de AFIP ═══════════════════

    private static bool EsRecibidosAfip(IXLWorksheet ws, int lastRow, int lastCol)
    {
        for (int r = 1; r <= Math.Min(6, lastRow); r++)
            for (int c = 1; c <= lastCol; c++)
            {
                var n = Norm(ws.Cell(r, c).GetString());
                if (n == "denominacion emisor" || n.Contains("comprobantes recibidos")) return true;
            }
        return false;
    }

    /// <summary>Importa los "Mis Comprobantes Recibidos" de AFIP (Excel .xlsx o CSV) que esten en una subcarpeta
    /// compartida, incluyendo las subcarpetas (por ej. "TODOS LOS AÑOS FACTURAS RECIBIDAS").</summary>
    public async Task<ContadoraImportResultDto> ImportarComprasCarpetaAsync(string subcarpeta)
    {
        var res = new ContadoraImportResultDto();
        string full;
        try { full = _storage.ResolveSafe(subcarpeta); }
        catch { res.Ok = false; res.Mensaje = "Carpeta invalida."; return res; }
        if (!Directory.Exists(full)) { res.Ok = false; res.Mensaje = $"No existe la carpeta '{subcarpeta}'."; return res; }

        var archivos = Directory.EnumerateFiles(full, "*.*", SearchOption.AllDirectories)
            .Where(f => { var e = Path.GetExtension(f).ToLowerInvariant(); return e == ".xlsx" || e == ".csv"; })
            .Where(f => !Path.GetFileName(f).StartsWith("~$")).OrderBy(f => f).ToList();
        int reconocidos = 0;
        foreach (var path in archivos)
        {
            using var fs = File.OpenRead(path);
            var a = await ImportarUnRecibidoAsync(fs, Path.GetFileName(path));
            if (a is null) continue; // no es un recibidos de AFIP, se ignora
            reconocidos++;
            AcumularArchivo(res, a);
        }
        if (reconocidos == 0) { res.Ok = false; res.Mensaje = "No encontre ningun 'Mis Comprobantes Recibidos' de AFIP (Excel o CSV) en la carpeta ni en sus subcarpetas."; return res; }
        res.Mensaje = $"Compras importadas: {res.Facturas} comprobantes y {res.NotasCredito} NC ({res.Nuevos} nuevos, {res.Actualizados} actualizados).";
        return res;
    }

    /// <summary>Importa archivos de compras subidos por el usuario.</summary>
    public async Task<ContadoraImportResultDto> ImportarComprasArchivosAsync(IEnumerable<(string nombre, Stream contenido)> archivos)
    {
        var res = new ContadoraImportResultDto();
        int reconocidos = 0;
        foreach (var (nombre, contenido) in archivos)
        {
            var a = await ImportarUnRecibidoAsync(contenido, nombre);
            if (a is null) { res.Archivos.Add(new ContadoraImportArchivoDto { Archivo = nombre, Ok = false, Error = "No es un 'Mis Comprobantes Recibidos' de AFIP." }); continue; }
            reconocidos++;
            AcumularArchivo(res, a);
        }
        if (reconocidos == 0) { res.Ok = false; res.Mensaje = "El archivo no parece un 'Mis Comprobantes Recibidos' de AFIP."; return res; }
        res.Mensaje = $"Compras importadas: {res.Facturas} comprobantes y {res.NotasCredito} NC ({res.Nuevos} nuevos, {res.Actualizados} actualizados).";
        return res;
    }

    /// <summary>Importa los "Mis Comprobantes Emitidos" de AFIP (ventas, Excel o CSV) de una subcarpeta, incluyendo subcarpetas.</summary>
    public async Task<ContadoraImportResultDto> ImportarVentasAfipCarpetaAsync(string subcarpeta)
    {
        var res = new ContadoraImportResultDto();
        string full;
        try { full = _storage.ResolveSafe(subcarpeta); }
        catch { res.Ok = false; res.Mensaje = "Carpeta invalida."; return res; }
        if (!Directory.Exists(full)) { res.Ok = false; res.Mensaje = $"No existe la carpeta '{subcarpeta}'."; return res; }

        var archivos = Directory.EnumerateFiles(full, "*.*", SearchOption.AllDirectories)
            .Where(f => { var e = Path.GetExtension(f).ToLowerInvariant(); return e == ".xlsx" || e == ".csv"; })
            .Where(f => !Path.GetFileName(f).StartsWith("~$")).OrderBy(f => f).ToList();
        int reconocidos = 0;
        foreach (var path in archivos)
        {
            using var fs = File.OpenRead(path);
            var a = await ImportarUnAfipAsync(fs, Path.GetFileName(path), esVenta: true);
            if (a is null) continue; // no es un emitidos de AFIP, se ignora
            reconocidos++;
            AcumularArchivo(res, a);
        }
        if (reconocidos == 0) { res.Ok = false; res.Mensaje = "No encontre ningun 'Mis Comprobantes Emitidos' de AFIP (Excel o CSV) en la carpeta ni en sus subcarpetas."; return res; }
        res.Mensaje = $"Ventas de AFIP importadas: {res.Facturas} comprobantes y {res.NotasCredito} NC ({res.Nuevos} nuevos, {res.Actualizados} actualizados).";
        return res;
    }

    /// <summary>Importa archivos de ventas de AFIP (emitidos) subidos por el usuario.</summary>
    public async Task<ContadoraImportResultDto> ImportarVentasAfipArchivosAsync(IEnumerable<(string nombre, Stream contenido)> archivos)
    {
        var res = new ContadoraImportResultDto();
        int reconocidos = 0;
        foreach (var (nombre, contenido) in archivos)
        {
            var a = await ImportarUnAfipAsync(contenido, nombre, esVenta: true);
            if (a is null) { res.Archivos.Add(new ContadoraImportArchivoDto { Archivo = nombre, Ok = false, Error = "No es un 'Mis Comprobantes Emitidos' de AFIP." }); continue; }
            reconocidos++;
            AcumularArchivo(res, a);
        }
        if (reconocidos == 0) { res.Ok = false; res.Mensaje = "El archivo no parece un 'Mis Comprobantes Emitidos' de AFIP."; return res; }
        res.Mensaje = $"Ventas de AFIP importadas: {res.Facturas} comprobantes y {res.NotasCredito} NC ({res.Nuevos} nuevos, {res.Actualizados} actualizados).";
        return res;
    }

    private Task<ContadoraImportArchivoDto?> ImportarUnRecibidoAsync(Stream stream, string nombre)
        => ImportarUnAfipAsync(stream, nombre, esVenta: false);

    /// <summary>Parsea un "Mis Comprobantes" de AFIP (Excel .xlsx o CSV). esVenta=true → EMITIDOS (ventas, IVA débito),
    /// esVenta=false → RECIBIDOS (compras, IVA crédito). Devuelve null si el archivo no es de ese tipo.</summary>
    private async Task<ContadoraImportArchivoDto?> ImportarUnAfipAsync(Stream stream, string nombre, bool esVenta)
    {
        var a = new ContadoraImportArchivoDto { Archivo = nombre };
        List<ContadoraComprobante> parsed;
        // En EMITIDOS la contraparte es el "Receptor" (cliente); en RECIBIDOS es el "Emisor" (proveedor).
        var denomParty = esVenta ? "denominacion receptor" : "denominacion emisor";
        var tituloEsperado = esVenta ? "comprobantes emitidos" : "comprobantes recibidos";
        try
        {
            IGrid g;
            var esCsv = Path.GetExtension(nombre).ToLowerInvariant() == ".csv";
            XLWorkbook? wb = null;
            if (esCsv) g = CsvGrid.Leer(stream);
            else { wb = new XLWorkbook(stream); var ws = wb.Worksheets.OrderByDescending(w => w.LastRowUsed()?.RowNumber() ?? 0).First(); g = new XlsxGrid(ws); }

            try
            {
                var lastRow = g.LastRow;
                var lastCol = g.LastCol;

                // ¿Es el tipo de archivo que pedimos (emitidos vs recibidos)? Lo distinguimos por la columna de la
                // contraparte: "Denominacion Receptor" solo esta en emitidos, "Denominacion Emisor" solo en recibidos.
                bool esEsperado = false;
                for (int r = 1; r <= Math.Min(6, lastRow) && !esEsperado; r++)
                    for (int c = 1; c <= lastCol; c++)
                    {
                        var n = Norm(g.Text(r, c));
                        if (n == denomParty || n.Contains(tituloEsperado)) { esEsperado = true; break; }
                    }
                if (!esEsperado) return null;

                int headerRow = 0;
                for (int r = 1; r <= Math.Min(8, lastRow) && headerRow == 0; r++)
                    for (int c = 1; c <= lastCol; c++)
                        if (Norm(g.Text(r, c)) == denomParty) { headerRow = r; break; }
                if (headerRow == 0) { a.Ok = false; a.Error = "No encontre los titulos del reporte de AFIP."; return a; }

                var map = new Dictionary<string, int>();
                for (int c = 1; c <= lastCol; c++)
                {
                    var h = Norm(g.Text(headerRow, c));
                    if (h.Length > 0 && !map.ContainsKey(h)) map[h] = c;
                }
                int Col(params string[] keys) { foreach (var k in keys) if (map.TryGetValue(k, out var c)) return c; return 0; }

                // Nombres de columna: el Excel viejo (2021/2022) y el CSV nuevo (2023+) usan textos distintos.
                int cFecha = Col("fecha", "fecha de emision"), cTipo = Col("tipo", "tipo de comprobante"), cPtoVta = Col("punto de venta"),
                    cNumD = Col("numero desde", "nro. desde", "numero"), cCae = Col("cod. autorizacion", "codigo de autorizacion", "cae"),
                    cNetoTotal = Col("neto gravado total", "imp. neto gravado total", "imp. neto gravado"),
                    cIva105 = Col("iva 10,5%", "iva 10.5%"), cTotalIva = Col("total iva"),
                    cNoGrav = Col("neto no gravado", "imp. neto no gravado"), cExento = Col("op. exentas", "imp. op. exentas", "op exentas"),
                    cOtros = Col("otros tributos"), cImpTotal = Col("imp. total", "imp total");
                // La contraparte (cliente en ventas, proveedor en compras) y su documento.
                int cDocParty = esVenta ? Col("nro. doc. receptor", "nro doc receptor") : Col("nro. doc. emisor", "nro doc emisor");
                int cDenomParty = Col(denomParty);
                // En recibidos MI CUIT esta en la columna Receptor; en emitidos no hay columna (soy el emisor) → lo saco del nombre del archivo.
                int cMiCuitCol = esVenta ? 0 : Col("nro. doc. receptor", "nro doc receptor");

                // MI CUIT: en recibidos del titulo/columna receptor; en emitidos del nombre del archivo (…_30717212149_…), o el default.
                string? miCuit = null;
                for (int r = 1; r < headerRow && miCuit == null; r++)
                    for (int c = 1; c <= lastCol; c++)
                    {
                        var t = g.Text(r, c);
                        if (Norm(t).Contains(tituloEsperado)) { miCuit = SoloDigitos(t); break; }
                    }
                if (esVenta && string.IsNullOrEmpty(miCuit))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(nombre, @"\b\d{11}\b");
                    miCuit = m.Success ? m.Value : CuitPorDefecto;
                }

                var porId = new Dictionary<string, ContadoraComprobante>();
                for (int r = headerRow + 1; r <= lastRow; r++)
                {
                    var tipoRaw = cTipo > 0 ? g.Text(r, cTipo) : "";
                    if (tipoRaw.Length == 0) continue;
                    var codigo = tipoRaw.Split('-')[0].Trim();
                    var labelHint = tipoRaw.Contains('-') ? tipoRaw[(tipoRaw.IndexOf('-') + 1)..].Trim() : null;
                    var (letra, esNC, tipoLabel) = TipoInfo(codigo, labelHint);

                    if (!esVenta && cMiCuitCol > 0 && miCuit == null) miCuit = SoloDigitos(g.Text(r, cMiCuitCol));
                    var partyCuit = cDocParty > 0 ? SoloDigitos(g.Text(r, cDocParty)) : null;   // cliente (venta) / proveedor (compra)
                    var emisorMi = esVenta ? (miCuit ?? CuitPorDefecto) : (cMiCuitCol > 0 ? SoloDigitos(g.Text(r, cMiCuitCol)) : null) ?? miCuit;
                    var cae = cCae > 0 ? SoloDigitos(g.Text(r, cCae)) : null;
                    var pto = cPtoVta > 0 ? (int?)(g.Lng(r, cPtoVta) ?? 0) : null;
                    var num = cNumD > 0 ? g.Lng(r, cNumD) : null;
                    // OJO: el CAE de AFIP NO es unico por comprobante (una Factura y su NC/ND lo comparten, y los tiques no traen).
                    // Ventas: como soy el unico emisor, mi pto+numero+tipo es unico. Compras: la clave es proveedor+tipo+pto+numero.
                    var id = esVenta ? $"VAFIP-{codigo}-{pto}-{num}" : $"COMPRA-{partyCuit}-{codigo}-{pto}-{num}";

                    var neto = g.Dec(r, cNetoTotal);
                    var iva105 = g.Dec(r, cIva105);
                    var ivaTotal = g.Dec(r, cTotalIva);
                    var e = new ContadoraComprobante
                    {
                        Naturaleza = esVenta ? "VENTA" : "COMPRA",
                        Origen = esVenta ? "AFIP_EMITIDOS" : "AFIP_RECIBIDOS",
                        EmisorCuit = emisorMi,       // mi empresa
                        IdComprobante = id,
                        TipoOperacion = esNC ? (esVenta ? "Nota de credito" : "Cancelacion") : (esVenta ? "Venta" : "Compra"),
                        TipoComprobante = tipoLabel,
                        EsNotaCredito = esNC,
                        Letra = letra,
                        PuntoVenta = pto,
                        NumeroComprobante = num,
                        Cae = cae,
                        FechaEmision = cFecha > 0 ? g.Fecha(r, cFecha) : null,
                        Estado = "Aprobada",
                        ReceptorNombre = cDenomParty > 0 ? NullIfEmpty(g.Text(r, cDenomParty)) : null,  // cliente (venta) / proveedor (compra)
                        ReceptorDoc = partyCuit,
                        NetoGravado = neto, BaseIva21 = neto,
                        Iva105 = iva105,
                        Iva21 = Math.Round(ivaTotal - iva105, 2), // el resto del IVA (21% + otras alicuotas) para que Iva21+Iva105 = IVA total
                        NoGravado = g.Dec(r, cNoGrav), Exento = g.Dec(r, cExento), OtrosImpuestos = g.Dec(r, cOtros),
                        Total = g.Dec(r, cImpTotal),
                        ArchivoOrigen = nombre,
                        ImportadoEn = DateTime.UtcNow
                    };
                    porId[id] = e;
                }
                a.EmpresaCuit = esVenta ? miCuit : (parsed0(porId) ?? miCuit);
                parsed = porId.Values.ToList();
            }
            finally { wb?.Dispose(); }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Contadora] Error leyendo AFIP {Tipo} {Archivo}", esVenta ? "emitidos" : "recibidos", nombre);
            a.Ok = false; a.Error = "No pude leer el archivo: " + ex.GetBaseException().Message;
            return a;
        }

        // MATCH con MeLi: para las ventas de un punto de venta de MercadoLibre, traigo la PROVINCIA cruzando por
        // punto de venta + numero contra lo que ya importe del reporte de MeLi (que si tiene la provincia).
        if (esVenta) await CompletarProvinciaDesdeMeliAsync(parsed);

        var natur = esVenta ? "VENTA" : "COMPRA";
        var ids = parsed.Select(p => p.IdComprobante).ToList();
        var existentes = new Dictionary<string, ContadoraComprobante>();
        foreach (var chunk in Chunk(ids, 1000))
            foreach (var f in await _db.ContadoraComprobantes.Where(c => chunk.Contains(c.IdComprobante)).ToListAsync())
                existentes[f.IdComprobante] = f;

        foreach (var p in parsed)
        {
            if (existentes.TryGetValue(p.IdComprobante, out var ex)) { CopiarCampos(p, ex); ex.Naturaleza = natur; ex.Provincia = p.Provincia ?? ex.Provincia; a.Actualizados++; }
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
        _logger.LogInformation("[Contadora] AFIP {Tipo} {Archivo}: {Fac} comp, {NC} NC, {N} nuevos, {A} act.", esVenta ? "emitidos" : "recibidos", nombre, a.Facturas, a.NotasCredito, a.Nuevos, a.Actualizados);
        return a;
    }

    // Para compras: MI CUIT sale de la 1ra fila parseada si no lo saque del titulo.
    private static string? parsed0(Dictionary<string, ContadoraComprobante> porId)
        => porId.Values.FirstOrDefault()?.EmisorCuit;

    /// <summary>Para las ventas de AFIP, completa la Provincia cruzando por punto de venta + numero contra los
    /// comprobantes del reporte de MeLi (Origen='MELI_REPORTE'), que si traen la provincia de destino.</summary>
    private async Task CompletarProvinciaDesdeMeliAsync(List<ContadoraComprobante> ventas)
    {
        var conNum = ventas.Where(v => v.PuntoVenta.HasValue && v.NumeroComprobante.HasValue).ToList();
        if (conNum.Count == 0) return;
        var ptos = conNum.Select(v => v.PuntoVenta!.Value).Distinct().ToList();
        var nums = conNum.Select(v => v.NumeroComprobante!.Value).Distinct().ToList();

        // Traigo del reporte de MeLi los (pto, numero, provincia) que caen en el rango, y armo el lookup.
        var mapa = new Dictionary<(int, long), string>();
        foreach (var chunk in Chunk(nums, 1000))
        {
            var filas = await _db.ContadoraComprobantes
                .Where(c => c.Origen == "MELI_REPORTE" && c.Provincia != null
                    && c.PuntoVenta != null && ptos.Contains(c.PuntoVenta.Value)
                    && c.NumeroComprobante != null && chunk.Contains(c.NumeroComprobante.Value))
                .Select(c => new { c.PuntoVenta, c.NumeroComprobante, c.Provincia })
                .ToListAsync();
            foreach (var f in filas)
                mapa[(f.PuntoVenta!.Value, f.NumeroComprobante!.Value)] = f.Provincia!;
        }
        foreach (var v in conNum)
            if (mapa.TryGetValue((v.PuntoVenta!.Value, v.NumeroComprobante!.Value), out var prov))
                v.Provincia = prov;
    }

    // ═══════════════════ BALANZA DE IVA (ventas - compras) ═══════════════════

    /// <summary>Balanza de IVA por mes: IVA de ventas (debito) - IVA de compras (credito) = saldo.</summary>
    public async Task<ContadoraBalanzaDto> GetBalanzaAsync(DateTime? desde, DateTime? hasta, string? empresaCuit)
    {
        var q = _db.ContadoraComprobantes.AsQueryable();
        if (desde.HasValue) q = q.Where(c => c.FechaEmision >= desde.Value);
        if (hasta.HasValue) { var h = hasta.Value.Date.AddDays(1); q = q.Where(c => c.FechaEmision < h); }
        if (!string.IsNullOrWhiteSpace(empresaCuit)) q = q.Where(c => c.EmisorCuit == empresaCuit);

        var datos = await q.Where(c => c.FechaEmision != null)
            .Select(c => new { c.Naturaleza, c.Origen, c.PuntoVenta, c.Letra, c.TipoComprobante, c.NumeroComprobante, c.FechaEmision, c.EsNotaCredito, c.Iva21, c.Iva105 })
            .ToListAsync();

        // Contar cada venta una sola vez (AFIP > reporte MeLi/sistema > API MeLi). Las compras no se tocan.
        var ventas = DedupPorClave(datos.Where(x => x.Naturaleza == "VENTA").ToList(),
            x => x.Origen, x => x.PuntoVenta, x => x.Letra, x => x.TipoComprobante, x => x.EsNotaCredito, x => x.NumeroComprobante);
        datos = ventas.Concat(datos.Where(x => x.Naturaleza != "VENTA")).ToList();

        var filas = datos
            .GroupBy(x => new { x.FechaEmision!.Value.Year, x.FechaEmision!.Value.Month })
            .Select(g =>
            {
                decimal ivaV = g.Where(x => x.Naturaleza == "VENTA").Sum(x => (x.EsNotaCredito ? -1 : 1) * (x.Iva21 + x.Iva105));
                decimal ivaC = g.Where(x => x.Naturaleza == "COMPRA").Sum(x => (x.EsNotaCredito ? -1 : 1) * (x.Iva21 + x.Iva105));
                return new ContadoraBalanzaMesDto
                {
                    Anio = g.Key.Year, Mes = g.Key.Month,
                    IvaVentas = ivaV, IvaCompras = ivaC, Saldo = ivaV - ivaC
                };
            })
            .OrderByDescending(f => f.Anio).ThenByDescending(f => f.Mes)
            .ToList();

        return new ContadoraBalanzaDto
        {
            Filas = filas,
            IvaVentasTotal = filas.Sum(f => f.IvaVentas),
            IvaComprasTotal = filas.Sum(f => f.IvaCompras),
            SaldoTotal = filas.Sum(f => f.Saldo)
        };
    }

    // ═══════════════════ CONTROL / doble-check (AFIP vs MeLi/sistema) ═══════════════════

    /// <summary>Concilia las ventas de AFIP (oficial) contra las de MeLi/sistema, cruzando por punto de venta +
    /// letra + numero. Devuelve cuantas coinciden, cuantas estan en una sola fuente, y cuales difieren en el monto.</summary>
    public async Task<ContadoraControlDto> GetControlAsync(DateTime? desde, DateTime? hasta)
    {
        var dto = new ContadoraControlDto();
        // CUITs de los que TENEMOS AFIP (hoy solo PALANICA). El control solo puede comparar esos; las ventas de
        // otros CUITs (ej. las Facturas C de los hermanos monotributo) tienen su propio AFIP que no tenemos, asi
        // que NO se pueden marcar como "faltan en AFIP" — las dejamos afuera para no dar falsos positivos.
        var cuitsAfip = await _db.ContadoraComprobantes
            .Where(c => c.Origen == "AFIP_EMITIDOS" && c.EmisorCuit != null)
            .Select(c => c.EmisorCuit!).Distinct().ToListAsync();
        dto.SinAfip = cuitsAfip.Count == 0;

        var q = _db.ContadoraComprobantes.Where(c => c.Naturaleza == "VENTA" && c.EmisorCuit != null && cuitsAfip.Contains(c.EmisorCuit));
        if (desde.HasValue) q = q.Where(c => c.FechaEmision >= desde.Value);
        if (hasta.HasValue) { var h = hasta.Value.Date.AddDays(1); q = q.Where(c => c.FechaEmision < h); }

        var filas = await q.Select(c => new { c.Origen, c.PuntoVenta, c.Letra, c.TipoComprobante, c.EsNotaCredito, c.NumeroComprobante, c.FechaEmision, c.ReceptorNombre, c.Iva21, c.Iva105, c.EnvioIva }).ToListAsync();

        // Agrupo por clave de comprobante y en cada grupo separo AFIP de la otra fuente (MeLi/sistema).
        var grupos = filas.GroupBy(x => ClaveComprobante(x.PuntoVenta, x.Letra, ClaseComprobante(x.TipoComprobante, x.EsNotaCredito), x.NumeroComprobante));
        var revisar = new List<ContadoraControlItemDto>();
        const decimal tol = 1m; // tolerancia de $1 por redondeos
        foreach (var g in grupos)
        {
            var afip = g.FirstOrDefault(x => x.Origen == "AFIP_EMITIDOS");
            var otro = g.FirstOrDefault(x => x.Origen == "MELI_REPORTE" || x.Origen == "SISTEMA");
            var ivaA = afip is null ? 0m : (afip.EsNotaCredito ? -1 : 1) * (afip.Iva21 + afip.Iva105);
            // MeLi informa el IVA del producto y el del envio por separado; AFIP los tiene juntos → sumo el envio para comparar igual.
            var ivaO = otro is null ? 0m : (otro.EsNotaCredito ? -1 : 1) * (otro.Iva21 + otro.Iva105 + otro.EnvioIva);

            if (afip != null && otro != null)
            {
                if (Math.Abs(ivaA - ivaO) <= tol) { dto.CoincidenCant++; dto.CoincidenIva += ivaA; }
                else
                {
                    dto.DifierenCant++; dto.DifierenIva += ivaA;
                    revisar.Add(new ContadoraControlItemDto
                    {
                        Tipo = "Difiere el monto", Fecha = afip.FechaEmision, PuntoVenta = afip.PuntoVenta, Letra = afip.Letra,
                        Numero = afip.NumeroComprobante, Cliente = afip.ReceptorNombre,
                        Fuente = otro.Origen == "SISTEMA" ? "Sistema" : "MercadoLibre", IvaAfip = ivaA, IvaOtro = ivaO
                    });
                }
            }
            else if (afip != null) { dto.SoloAfipCant++; dto.SoloAfipIva += ivaA; }
            else if (otro != null)
            {
                dto.SoloMeliCant++; dto.SoloMeliIva += ivaO;
                revisar.Add(new ContadoraControlItemDto
                {
                    Tipo = "Solo en MeLi/sistema", Fecha = otro.FechaEmision, PuntoVenta = otro.PuntoVenta, Letra = otro.Letra,
                    Numero = otro.NumeroComprobante, Cliente = otro.ReceptorNombre,
                    Fuente = otro.Origen == "SISTEMA" ? "Sistema" : "MercadoLibre", IvaAfip = 0m, IvaOtro = ivaO
                });
            }
        }
        dto.Revisar = revisar.OrderByDescending(r => r.Fecha).Take(300).ToList();
        return dto;
    }

    // ── Consultas sobre los comprobantes importados (con NC restando) ──

    private IQueryable<ContadoraComprobante> FiltrarComprobantes(DateTime? desde, DateTime? hasta,
        string? empresaCuit, int? puntoVenta, string? letra, string? provincia, string? search, string? origen = null, string naturaleza = "VENTA")
    {
        var q = _db.ContadoraComprobantes.Where(c => c.Naturaleza == naturaleza);
        if (!string.IsNullOrWhiteSpace(origen)) q = q.Where(c => c.Origen == origen);
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

    /// <summary>Clase del comprobante para la clave: F (factura/tique), ND (nota de débito), NC (nota de crédito).
    /// OJO: una Factura A y una Nota de Débito A pueden tener el MISMO número (numeraciones separadas en AFIP),
    /// por eso hay que distinguirlas o se pisan.</summary>
    private static string ClaseComprobante(string? tipoComprobante, bool esNC)
    {
        var n = Norm(tipoComprobante);
        string b = esNC ? "NC" : (n.Contains("debito") ? "ND" : "F");
        return n.Contains("fce") ? b + "F" : b;   // FCE es un tipo aparte (numeración propia)
    }

    /// <summary>Clave que identifica un comprobante: punto de venta + letra + clase (F/ND/NC) + numero.
    /// MeLi emite Factura A y B en el mismo pto de venta con numeraciones separadas, por eso NO alcanza con pto+numero.</summary>
    private static string ClaveComprobante(int? pto, string? letra, string clase, long? num)
        => $"{pto}|{letra}|{clase}|{num}";

    /// <summary>Prioridad de cada fuente cuando la MISMA venta aparece en varias (menor = manda).
    /// AFIP es lo oficial y completo (con NC) → gana. Después el reporte de MeLi / las del sistema.
    /// La API de MeLi es la más automática pero no trae NC → va última.</summary>
    private static int PrioridadOrigen(string? origen) => origen switch
    {
        "AFIP_EMITIDOS" => 1,
        "MELI_REPORTE" => 2,
        "SISTEMA" => 2,
        "MELI_API" => 3,
        _ => 4
    };

    /// <summary>Deja UNA sola fila por comprobante (pto+letra+NC+numero), la de mayor prioridad de fuente.
    /// Así una venta que está en AFIP y también en MeLi/sistema se cuenta una vez. Las filas sin número
    /// (no se pueden identificar) se dejan pasar tal cual.</summary>
    private static List<T> DedupPorClave<T>(IEnumerable<T> rows, Func<T, string?> origen, Func<T, int?> pto,
        Func<T, string?> letra, Func<T, string?> tipoComp, Func<T, bool> esNC, Func<T, long?> num)
    {
        var lista = rows.ToList();
        string Clave(T r) => ClaveComprobante(pto(r), letra(r), ClaseComprobante(tipoComp(r), esNC(r)), num(r));
        var mejor = new Dictionary<string, int>();
        foreach (var r in lista)
        {
            if (pto(r) == null || num(r) == null) continue;
            var k = Clave(r);
            var p = PrioridadOrigen(origen(r));
            if (!mejor.TryGetValue(k, out var m) || p < m) mejor[k] = p;
        }
        var visto = new HashSet<string>();
        var salida = new List<T>();
        foreach (var r in lista)
        {
            if (pto(r) == null || num(r) == null) { salida.Add(r); continue; }
            var k = Clave(r);
            if (PrioridadOrigen(origen(r)) == mejor[k] && visto.Add(k)) salida.Add(r);
        }
        return salida;
    }

    /// <summary>Empresas (CUIT) que aparecen en los comprobantes importados, con su razon social si la conocemos.</summary>
    public async Task<List<ContadoraEmpresaDto>> GetReporteEmpresasAsync()
    {
        var cuits = await _db.ContadoraComprobantes.Where(c => c.Naturaleza == "VENTA" && c.EmisorCuit != null)
            .Select(c => c.EmisorCuit!).Distinct().ToListAsync();
        var razon = await _db.ArcaEmisores.Where(e => cuits.Contains(e.Cuit))
            .ToDictionaryAsync(e => e.Cuit, e => e.RazonSocial);
        return cuits
            .Select(c => new ContadoraEmpresaDto
            {
                Cuit = c,
                Nombre = razon.TryGetValue(c, out var rs) && !string.IsNullOrWhiteSpace(rs) ? $"{rs} ({c})" : c
            })
            .OrderBy(e => e.Nombre).ToList();
    }

    /// <summary>Provincias presentes en los comprobantes importados (para el desplegable del filtro).</summary>
    public async Task<List<string>> GetReporteProvinciasAsync()
    {
        return await _db.ContadoraComprobantes
            .Where(c => c.Naturaleza == "VENTA" && c.Provincia != null && c.Provincia != "")
            .Select(c => c.Provincia!)
            .Distinct().OrderBy(p => p).ToListAsync();
    }

    /// <summary>Resumen del Libro IVA Ventas desde los comprobantes importados (NC restan).</summary>
    public async Task<ContadoraReporteResumenDto> GetReporteResumenAsync(DateTime? desde, DateTime? hasta,
        string? empresaCuit, int? puntoVenta, string? letra, string? provincia, string? search, string? origen = null, string naturaleza = "VENTA")
    {
        var dto = new ContadoraReporteResumenDto();
        dto.SinDatos = !await _db.ContadoraComprobantes.AnyAsync(c => c.Naturaleza == naturaleza);

        var datos = await FiltrarComprobantes(desde, hasta, empresaCuit, puntoVenta, letra, provincia, search, origen, naturaleza)
            .Select(c => new { c.Origen, c.EmisorCuit, c.PuntoVenta, c.NumeroComprobante, c.Letra, c.TipoComprobante, c.EsNotaCredito, c.NetoGravado, c.Iva21, c.Iva105, c.Total })
            .ToListAsync();

        // Contar cada venta una sola vez: si el usuario NO filtro por un origen puntual, dejo una fila por
        // comprobante (la de mayor prioridad: AFIP > reporte MeLi/sistema > API MeLi).
        if (naturaleza == "VENTA" && string.IsNullOrWhiteSpace(origen))
            datos = DedupPorClave(datos, x => x.Origen, x => x.PuntoVenta, x => x.Letra, x => x.TipoComprobante, x => x.EsNotaCredito, x => x.NumeroComprobante);

        // Razón social por CUIT (para mostrar el nombre y no solo el número).
        var cuits = datos.Where(x => x.EmisorCuit != null).Select(x => x.EmisorCuit!).Distinct().ToList();
        var razon = await _db.ArcaEmisores.Where(e => cuits.Contains(e.Cuit)).ToDictionaryAsync(e => e.Cuit, e => e.RazonSocial);
        string? Nombre(string? cuit) => cuit != null && razon.TryGetValue(cuit, out var rs) && !string.IsNullOrWhiteSpace(rs) ? rs : cuit;

        dto.Filas = datos
            .GroupBy(x => new { x.EmisorCuit, x.PuntoVenta, x.Letra })
            .Select(g => new LibroIvaResumenRowDto
            {
                EmpresaCuit = g.Key.EmisorCuit,
                EmpresaNombre = Nombre(g.Key.EmisorCuit),
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
    public async Task<List<ContadoraCargaDto>> GetReporteCargasAsync(string? empresaCuit, string? origen = null)
    {
        var q = _db.ContadoraComprobantes.Where(c => c.Naturaleza == "VENTA" && c.FechaEmision != null);
        if (!string.IsNullOrWhiteSpace(empresaCuit)) q = q.Where(c => c.EmisorCuit == empresaCuit);
        if (!string.IsNullOrWhiteSpace(origen)) q = q.Where(c => c.Origen == origen);
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
        string? empresaCuit, int? puntoVenta, string? letra, string? provincia, string? search, int page = 1, int pageSize = 50, string? origen = null, string naturaleza = "VENTA")
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 500) pageSize = 50;
        var q = FiltrarComprobantes(desde, hasta, empresaCuit, puntoVenta, letra, provincia, search, origen, naturaleza);
        var total = await q.CountAsync();
        var raw = await q.OrderByDescending(c => c.FechaEmision).ThenByDescending(c => c.NumeroComprobante)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(c => new { c.IdComprobante, c.Origen, c.Concepto, c.EmisorCuit, c.EsNotaCredito, c.TipoComprobante, c.PuntoVenta, c.NumeroComprobante,
                c.FechaEmision, c.Letra, c.ReceptorNombre, c.ReceptorDoc, c.Provincia, c.NetoGravado, c.Iva21, c.Iva105, c.Total })
            .ToListAsync();
        var items = raw.Select(c => new ContadoraComprobanteDto
        {
            IdComprobante = c.IdComprobante, Origen = c.Origen, Concepto = ConceptoLabel(c.Concepto), EmpresaCuit = c.EmisorCuit, EsNotaCredito = c.EsNotaCredito,
            TipoComprobante = c.TipoComprobante, PuntoVenta = c.PuntoVenta, NumeroComprobante = c.NumeroComprobante,
            FechaEmision = c.FechaEmision, Letra = c.Letra, ReceptorNombre = c.ReceptorNombre, ReceptorDoc = c.ReceptorDoc,
            Provincia = c.Provincia,
            Neto = (c.EsNotaCredito ? -1 : 1) * c.NetoGravado,
            Iva = (c.EsNotaCredito ? -1 : 1) * (c.Iva21 + c.Iva105),
            Total = (c.EsNotaCredito ? -1 : 1) * c.Total
        }).ToList();
        return new ContadoraComprobantesPageDto { Items = items, Total = total, Page = page, PageSize = pageSize };
    }

    private static string? ConceptoLabel(int? c) => c switch { 1 => "Productos", 2 => "Servicios", 3 => "Productos y Servicios", _ => null };

    /// <summary>Excel del Libro IVA Ventas (importado): hoja resumen + hoja detalle. NC en negativo.</summary>
    public async Task<byte[]> GenerarReporteExcelAsync(DateTime? desde, DateTime? hasta,
        string? empresaCuit, int? puntoVenta, string? letra, string? provincia, string? search, string? origen = null, string naturaleza = "VENTA")
    {
        var resumen = await GetReporteResumenAsync(desde, hasta, empresaCuit, puntoVenta, letra, provincia, search, origen, naturaleza);
        var detalle = await GetReporteComprobantesAsync(desde, hasta, empresaCuit, puntoVenta, letra, provincia, search, 1, 500, origen, naturaleza);

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

    // Provincias canonicas (24 jurisdicciones AR) para unificar los nombres crudos de MeLi
    // ("CORDOBA"/"Córdoba", "Capital Federal"/"CIUDAD AUTONOMA BUENOS AIRES", etc.).
    private static readonly Dictionary<string, string> _provMap = BuildProvMap();
    private static Dictionary<string, string> BuildProvMap()
    {
        var canon = new[]
        {
            "Buenos Aires", "Ciudad Autónoma de Buenos Aires", "Catamarca", "Chaco", "Chubut", "Córdoba",
            "Corrientes", "Entre Ríos", "Formosa", "Jujuy", "La Pampa", "La Rioja", "Mendoza", "Misiones",
            "Neuquén", "Río Negro", "Salta", "San Juan", "San Luis", "Santa Cruz", "Santa Fe",
            "Santiago del Estero", "Tierra del Fuego", "Tucumán"
        };
        var d = new Dictionary<string, string>();
        foreach (var c in canon) d[Norm(c)] = c;
        foreach (var a in new[] { "capital federal", "ciudad autonoma buenos aires", "ciudad autonoma de buenos aires",
            "ciudad de buenos aires", "caba", "c a b a" })
            d[a] = "Ciudad Autónoma de Buenos Aires";
        d["tierra del fuego antartida e islas del atlantico sur"] = "Tierra del Fuego";
        return d;
    }

    /// <summary>Nombre unificado de la provincia. Si no la reconoce, la deja como vino.</summary>
    private static string? NormProv(string? raw)
    {
        var t = raw?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        return _provMap.TryGetValue(Norm(t), out var c) ? c : t;
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

    /// <summary>Traduce el tipo de comprobante de AFIP (viene como "11 - Factura C" en el Excel viejo, o solo el
    /// codigo "11" en el CSV) a (letra, esNotaCredito, etiqueta para mostrar).</summary>
    private static (string? letra, bool esNC, string label) TipoInfo(string codigo, string? labelHint)
    {
        // Letra por codigo AFIP: A={1,2,3}, B={6,7,8}, C={11,12,13}, M={51,52,53}, Tiques {81,82,83},
        // FCE MiPyME (Factura de Credito Electronica): A={201,202,203}, B={206,207,208}, C={211,212,213}.
        string? letra = codigo switch
        {
            "1" or "2" or "3" or "4" or "5" or "81" or "201" or "202" or "203" => "A",
            "6" or "7" or "8" or "82" or "206" or "207" or "208" => "B",
            "11" or "12" or "13" or "83" or "211" or "212" or "213" => "C",
            "51" or "52" or "53" => "M",
            _ => null
        };
        // Notas de credito de AFIP: 3 (A), 8 (B), 13 (C), 53 (M), 203/208/213 (FCE).
        bool esNC = codigo is "3" or "8" or "13" or "53" or "203" or "208" or "213" || (labelHint != null && Norm(labelHint).Contains("credito"));
        // Notas de debito: 2 (A), 7 (B), 12 (C), 52 (M), 202/207/212 (FCE) — NO son NC (suman como una factura).
        bool esND = codigo is "2" or "7" or "12" or "52" or "202" or "207" or "212" || (labelHint != null && Norm(labelHint).Contains("debito"));

        // FCE (Factura de Crédito Electrónica MiPyME): tipo APARTE de la factura común, con numeración propia.
        // Hay que marcarlo distinto para que una Factura A común y una FCE A con el mismo número no se pisen.
        bool esFCE = codigo is "201" or "202" or "203" or "206" or "207" or "208" or "211" or "212" or "213";

        // Si el archivo trajo texto (Excel viejo), lo usamos; si no, armamos uno lindo desde el codigo.
        string label;
        if (!string.IsNullOrWhiteSpace(labelHint)) label = labelHint!.Trim();
        else { var tipo = esNC ? "Nota de crédito" : esND ? "Nota de débito" : "Factura"; var suf = esFCE ? " FCE" : ""; label = letra != null ? $"{tipo}{suf} {letra}" : $"Comprobante {codigo}"; }

        // Si no lo pudimos deducir por codigo, probamos por el final del texto.
        if (letra == null && labelHint != null)
            letra = labelHint.TrimEnd().EndsWith("A") ? "A" : labelHint.TrimEnd().EndsWith("B") ? "B" : labelHint.TrimEnd().EndsWith("C") ? "C" : null;

        return (letra, esNC, label);
    }

    // ═══════════ Grilla generica: misma logica de parseo para Excel (.xlsx) y CSV ═══════════

    /// <summary>Abstraccion de una planilla (1-based). Permite parsear igual un Excel y un CSV.</summary>
    private interface IGrid
    {
        int LastRow { get; }          // ultima fila con datos (1-based)
        int LastCol { get; }          // ultima columna con datos (1-based)
        string Text(int r, int c);    // texto de la celda (trim), "" si vacia/fuera de rango
        decimal Dec(int r, int c);    // monto (acepta numero o "$1.234,56" es-AR)
        long? Lng(int r, int c);
        DateTime? Fecha(int r, int c);
    }

    private sealed class XlsxGrid : IGrid
    {
        private readonly IXLWorksheet _ws;
        public XlsxGrid(IXLWorksheet ws) { _ws = ws; LastRow = ws.LastRowUsed()?.RowNumber() ?? 0; LastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0; }
        public int LastRow { get; }
        public int LastCol { get; }
        public string Text(int r, int c) => (r <= 0 || c <= 0) ? "" : _ws.Cell(r, c).GetString().Trim();
        public decimal Dec(int r, int c) => ContadoraService.Dec(_ws, r, c);
        public long? Lng(int r, int c) => c <= 0 ? null : ContadoraService.Lng(_ws.Cell(r, c));
        public DateTime? Fecha(int r, int c) => c <= 0 ? null : ContadoraService.Fecha(_ws.Cell(r, c));
    }

    private sealed class CsvGrid : IGrid
    {
        private readonly List<string[]> _rows;
        private CsvGrid(List<string[]> rows) { _rows = rows; LastRow = rows.Count; LastCol = rows.Count == 0 ? 0 : rows.Max(x => x.Length); }
        public int LastRow { get; }
        public int LastCol { get; }

        /// <summary>Lee un CSV de AFIP (separador ';', decimales es-AR, encoding utf-8 con BOM).</summary>
        public static CsvGrid Leer(Stream stream)
        {
            string texto;
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                var bytes = ms.ToArray();
                try { texto = new UTF8Encoding(false, true).GetString(bytes); }
                catch { texto = Encoding.Latin1.GetString(bytes); }
            }
            if (texto.Length > 0 && texto[0] == '﻿') texto = texto[1..];
            var rows = new List<string[]>();
            foreach (var linea in texto.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
            {
                if (linea.Length == 0 && rows.Count == 0) continue;
                rows.Add(PartirLinea(linea));
            }
            // sacamos lineas finales totalmente vacias
            while (rows.Count > 0 && rows[^1].All(string.IsNullOrWhiteSpace)) rows.RemoveAt(rows.Count - 1);
            return new CsvGrid(rows);
        }

        // Separa por ';' respetando comillas dobles.
        private static string[] PartirLinea(string linea)
        {
            var campos = new List<string>();
            var sb = new StringBuilder();
            bool enComillas = false;
            for (int i = 0; i < linea.Length; i++)
            {
                var ch = linea[i];
                if (ch == '"') { if (enComillas && i + 1 < linea.Length && linea[i + 1] == '"') { sb.Append('"'); i++; } else enComillas = !enComillas; }
                else if (ch == ';' && !enComillas) { campos.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(ch);
            }
            campos.Add(sb.ToString());
            return campos.ToArray();
        }

        private string Raw(int r, int c)
        {
            if (r <= 0 || c <= 0 || r > _rows.Count) return "";
            var row = _rows[r - 1];
            return c <= row.Length ? (row[c - 1] ?? "").Trim() : "";
        }

        public string Text(int r, int c) => Raw(r, c);

        public decimal Dec(int r, int c)
        {
            var s = Raw(r, c);
            if (s.Length == 0) return 0m;
            s = s.Replace("$", "").Replace(" ", "").Trim();
            if (s.Contains(',')) s = s.Replace(".", "").Replace(",", ".");
            return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
        }

        public long? Lng(int r, int c)
        {
            var s = SoloDigitos(Raw(r, c));
            return s != null && long.TryParse(s, out var n) ? n : null;
        }

        public DateTime? Fecha(int r, int c)
        {
            var s = Raw(r, c);
            if (s.Length == 0) return null;
            string[] fmts = { "yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd HH:mm:ss", "dd/MM/yyyy HH:mm:ss" };
            if (DateTime.TryParseExact(s, fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
            return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2) ? d2 : null;
        }
    }
}
