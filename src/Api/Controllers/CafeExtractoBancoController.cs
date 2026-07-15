using Api.Data;
using Api.Models;
using Api.Services;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace Api.Controllers;

/// <summary>
/// Extracto bancario importado desde el Excel del banco. Permite ver el saldo actual
/// de la cuenta y asociar manualmente cada ingreso con una venta. Pedido 2026-05-19.
/// </summary>
[ApiController]
[Route("api/cafe/extracto-banco")]
[Authorize]
public class CafeExtractoBancoController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<CafeExtractoBancoController> _logger;

    public CafeExtractoBancoController(AppDbContext db, ILogger<CafeExtractoBancoController> logger)
    {
        _db = db;
        _logger = logger;
    }

    public record ExtractoMovimientoDto(int Id, DateTime Fecha, string? Descripcion,
        decimal Debitos, decimal Creditos, decimal Saldo, string? Concepto, string? ObservacionesCliente,
        string? LeyendaAdicional1, string? LeyendaAdicional2, string? TipoMovimiento,
        int? VentaIdAsociada, string? VentaNumeroAsociada, string? AsociadoPor, DateTime? AsociadoAt,
        // Sugerencia de cliente: si el CUIT del extracto coincide con un cliente
        int? ClienteSugeridoId, string? ClienteSugeridoNombre,
        // 2026-06-19: si la venta asociada pertenece a una cobranza con imputaciones multiples,
        // devolvemos los numeros de TODAS las ventas imputadas y el numero de la cobranza.
        // Asi la UI muestra "✓ CAFE-2026-0073, CAFE-2026-0206" en vez de solo el primero.
        List<string>? ComprobantesAsociados = null,
        string? CobranzaNumeroAsociada = null,
        // 2026-07-11: SinCobranza = el mov quedo asociado a una venta pero esa venta NO tiene
        // ninguna cobranza VIGENTE que la impute (proceso a medias: se asocio y no se cargo la
        // cobranza). La UI lo pinta en rojo con boton "Retomar cobranza". VentaClienteId es el
        // cliente de la venta asociada, para poder precargar Cobranzas al retomar.
        bool SinCobranza = false,
        int? VentaClienteId = null);

    public record SaldoBancoDto(decimal Saldo, DateTime UltimaFecha, int CantidadMovimientos, DateTime? UltimoImportadoAt);

    public record ImportResultDto(string Archivo, int Nuevos, int SinCambios, List<string> Errores);

    public record AsociarRequest(int VentaId, string? Operador);

    [HttpGet("saldo")]
    public async Task<IActionResult> Saldo()
    {
        var ult = await _db.CafeExtractoMovimientos.OrderByDescending(m => m.Fecha).ThenByDescending(m => m.Id)
            .FirstOrDefaultAsync();
        if (ult is null) return Ok(new SaldoBancoDto(0m, DateTime.MinValue, 0, null));
        var cant = await _db.CafeExtractoMovimientos.CountAsync();
        var ultImportado = await _db.CafeExtractoMovimientos.MaxAsync(m => (DateTime?)m.ImportadoAt);
        return Ok(new SaldoBancoDto(ult.Saldo, ult.Fecha, cant, ultImportado));
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] DateTime? desde, [FromQuery] DateTime? hasta,
        [FromQuery] string? tipo = null, [FromQuery] string? asociado = null, [FromQuery] int take = 500)
    {
        var q = _db.CafeExtractoMovimientos.AsQueryable();
        if (desde.HasValue) q = q.Where(m => m.Fecha >= desde.Value.Date);
        if (hasta.HasValue) q = q.Where(m => m.Fecha <= hasta.Value.Date);
        if (tipo == "ingresos") q = q.Where(m => m.Creditos > 0);
        else if (tipo == "egresos") q = q.Where(m => m.Debitos > 0);
        // 2026-07-11: ventas que tienen al menos una cobranza VIGENTE que las imputa.
        // Sirve para el filtro "sin-completar" y para la marca SinCobranza del DTO.
        var ventasConCobranzaVigente = _db.CafeCobranzasComprobantes
            .Where(cc => cc.VentaId != null && cc.Cobranza!.Estado == "VIGENTE")
            .Select(cc => cc.VentaId!.Value);

        if (asociado == "si") q = q.Where(m => m.VentaIdAsociada != null);
        else if (asociado == "no") q = q.Where(m => m.VentaIdAsociada == null);
        else if (asociado == "sin-completar")
            q = q.Where(m => m.VentaIdAsociada != null
                && !ventasConCobranzaVigente.Contains(m.VentaIdAsociada.Value));

        var movs = await q.OrderByDescending(m => m.Fecha).ThenByDescending(m => m.Id)
            .Take(Math.Clamp(take, 1, 2000))
            .ToListAsync();

        // Pre-cargar clientes por CUIT para sugerencias
        var cuits = movs.Where(m => !string.IsNullOrEmpty(m.LeyendaAdicional2))
            .Select(m => m.LeyendaAdicional2!).Distinct().ToList();
        var clientesPorCuit = await _db.CafeClientes
            .Where(c => c.IsActive && c.Cuit != null && cuits.Contains(c.Cuit))
            .Select(c => new { c.Id, c.Nombre, c.Cuit })
            .ToListAsync();
        var byCuit = clientesPorCuit.GroupBy(c => c.Cuit!).ToDictionary(g => g.Key, g => g.First());

        // Pre-cargar numeros de venta asociados (y el cliente, para poder retomar la cobranza)
        var ventaIds = movs.Where(m => m.VentaIdAsociada.HasValue).Select(m => m.VentaIdAsociada!.Value).Distinct().ToList();
        var ventasDict = (await _db.CafeVentas.Where(v => ventaIds.Contains(v.Id))
            .Select(v => new { v.Id, v.Numero, v.ClienteId })
            .ToListAsync())
            .ToDictionary(v => v.Id, v => v);

        // 2026-07-11: de las ventas asociadas, cuales YA tienen una cobranza VIGENTE. El resto
        // quedaron a medias (asociadas sin cobranza) -> SinCobranza = true en el DTO.
        var ventasSaldadas = (await _db.CafeCobranzasComprobantes
            .Where(cc => cc.VentaId.HasValue && ventaIds.Contains(cc.VentaId.Value)
                && cc.Cobranza!.Estado == "VIGENTE")
            .Select(cc => cc.VentaId!.Value)
            .Distinct()
            .ToListAsync())
            .ToHashSet();

        // 2026-06-19: para cada venta asociada, buscar la cobranza que la imputo. Si esa cobranza
        // tiene MAS imputaciones, las devolvemos todas para que la UI muestre los N comprobantes.
        // Caso tipico: transferencia bancaria que cancela 2 facturas a la vez.
        var imputacionesAsoc = await _db.CafeCobranzasComprobantes
            .Where(i => i.VentaId.HasValue && ventaIds.Contains(i.VentaId.Value))
            .Select(i => new { i.CobranzaId, VentaId = i.VentaId!.Value })
            .ToListAsync();
        var ventaToCobranzaId = imputacionesAsoc
            .GroupBy(x => x.VentaId)
            .ToDictionary(g => g.Key, g => g.First().CobranzaId);
        var cobranzaIds = imputacionesAsoc.Select(x => x.CobranzaId).Distinct().ToList();
        var todasImpDeCobranzas = await _db.CafeCobranzasComprobantes
            .Where(i => cobranzaIds.Contains(i.CobranzaId) && i.VentaId.HasValue)
            .Join(_db.CafeVentas, i => i.VentaId!.Value, v => v.Id,
                  (i, v) => new { i.CobranzaId, v.Numero })
            .ToListAsync();
        var compsPorCobranza = todasImpDeCobranzas
            .GroupBy(x => x.CobranzaId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Numero).Distinct().OrderBy(n => n).ToList());
        var cobranzaNumeros = await _db.CafeCobranzas
            .Where(c => cobranzaIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Numero })
            .ToDictionaryAsync(c => c.Id, c => c.Numero);

        var result = movs.Select(m =>
        {
            int? cliId = null;
            string? cliNom = null;
            if (!string.IsNullOrEmpty(m.LeyendaAdicional2) && byCuit.TryGetValue(m.LeyendaAdicional2, out var c))
            {
                cliId = c.Id;
                cliNom = c.Nombre;
            }
            string? ventaNumero = null;
            int? ventaClienteId = null;
            if (m.VentaIdAsociada.HasValue && ventasDict.TryGetValue(m.VentaIdAsociada.Value, out var vinfo))
            {
                ventaNumero = vinfo.Numero;
                ventaClienteId = vinfo.ClienteId;
            }
            List<string>? comprobantes = null;
            string? cobranzaNumero = null;
            if (m.VentaIdAsociada.HasValue
                && ventaToCobranzaId.TryGetValue(m.VentaIdAsociada.Value, out var cobId))
            {
                if (compsPorCobranza.TryGetValue(cobId, out var lista)) comprobantes = lista;
                if (cobranzaNumeros.TryGetValue(cobId, out var cnum)) cobranzaNumero = cnum;
            }
            // Asociado a una venta que todavia no tiene cobranza vigente = proceso a medias.
            bool sinCobranza = m.VentaIdAsociada.HasValue && !ventasSaldadas.Contains(m.VentaIdAsociada.Value);
            return new ExtractoMovimientoDto(m.Id, m.Fecha, m.Descripcion, m.Debitos, m.Creditos, m.Saldo,
                m.Concepto, m.ObservacionesCliente, m.LeyendaAdicional1, m.LeyendaAdicional2, m.TipoMovimiento,
                m.VentaIdAsociada, ventaNumero, m.AsociadoPor, m.AsociadoAt,
                cliId, cliNom,
                comprobantes, cobranzaNumero,
                sinCobranza, ventaClienteId);
        }).ToList();
        return Ok(result);
    }

    public record MovimientoDisponibleDto(int Id, DateTime Fecha, string? Descripcion, decimal Importe,
        string? Concepto, int? VentaIdAsociada, string? VentaNumeroAsociada);

    /// <summary>Movimientos del extracto que ya estan asociados al cliente pero que NO se usaron
    /// todavia en una cobranza (CobranzaUsadaId IS NULL). Para sugerirlos como formas de cobro
    /// en /cafe/tesoreria/cobranzas. Tambien trae los SIN asociar pero que matchean por CUIT del cliente.</summary>
    [HttpGet("asociados-sin-cobrar/{clienteId:int}")]
    public async Task<IActionResult> AsociadosSinCobrar(int clienteId)
    {
        var cliente = await _db.CafeClientes.FirstOrDefaultAsync(c => c.Id == clienteId);
        if (cliente is null) return Ok(new List<MovimientoDisponibleDto>());

        // 2026-06-03: traer ids de movimientos que el usuario marco como "no es de este cliente"
        var descartadosIds = await _db.CafeExtractoMovDescartadosPorCliente
            .Where(d => d.ClienteId == clienteId)
            .Select(d => d.MovimientoId)
            .ToListAsync();

        var q = _db.CafeExtractoMovimientos
            .Where(m => m.Creditos > 0 && m.CobranzaUsadaId == null
                && !descartadosIds.Contains(m.Id)  // 2026-06-03: filtrar descartados
                && (m.VentaIdAsociada != null
                    || (cliente.Cuit != null && m.LeyendaAdicional2 == cliente.Cuit)));
        // Filtramos por venta asociada al cliente O por CUIT del cliente
        var movs = await q.OrderByDescending(m => m.Fecha).ToListAsync();
        var ventaIds = movs.Where(m => m.VentaIdAsociada.HasValue).Select(m => m.VentaIdAsociada!.Value).ToList();
        var clientesPorVenta = await _db.CafeVentas
            .Where(v => ventaIds.Contains(v.Id))
            .Select(v => new { v.Id, v.ClienteId, v.Numero })
            .ToListAsync();
        var clientesDict = clientesPorVenta.ToDictionary(v => v.Id);

        // Mantener solo: 1) sin venta pero CUIT matchea, o 2) con venta del cliente actual
        var filtrados = movs.Where(m =>
            (!m.VentaIdAsociada.HasValue && cliente.Cuit != null && m.LeyendaAdicional2 == cliente.Cuit)
            || (m.VentaIdAsociada.HasValue && clientesDict.TryGetValue(m.VentaIdAsociada.Value, out var v) && v.ClienteId == clienteId)
        ).ToList();

        var result = filtrados.Select(m =>
        {
            string? ventaNumero = null;
            if (m.VentaIdAsociada.HasValue && clientesDict.TryGetValue(m.VentaIdAsociada.Value, out var v))
                ventaNumero = v.Numero;
            return new MovimientoDisponibleDto(m.Id, m.Fecha, m.Descripcion, m.Creditos, m.Concepto,
                m.VentaIdAsociada, ventaNumero);
        }).ToList();
        return Ok(result);
    }

    public record MarcarUsadosRequest(List<int> MovimientoIds, int CobranzaId);

    /// <summary>Marca un set de movimientos del extracto como usados en una cobranza puntual.
    /// Llamado desde la pantalla de Cobranzas despues de crear/guardar una cobranza que incluyo
    /// movimientos del extracto como medios de pago. Idempotente.</summary>
    [HttpPost("marcar-usados")]
    public async Task<IActionResult> MarcarUsados([FromBody] MarcarUsadosRequest req)
    {
        if (req.MovimientoIds is null || req.MovimientoIds.Count == 0) return Ok(new { actualizados = 0 });
        var movs = await _db.CafeExtractoMovimientos.Where(m => req.MovimientoIds.Contains(m.Id)).ToListAsync();
        foreach (var m in movs) m.CobranzaUsadaId = req.CobranzaId;
        await _db.SaveChangesAsync();
        return Ok(new { actualizados = movs.Count });
    }

    // ─── 2026-06-03: Descartar/restaurar movimiento bancario para un cliente puntual ──────
    public record DescartarRequest(string? Operador);

    /// <summary>Marca un movimiento del extracto como "no es de este cliente". A partir de ahora
    /// no se sugiere mas en la pantalla de cobranzas de ese cliente. Es reversible.</summary>
    [HttpPost("{movId:int}/descartar-cliente/{clienteId:int}")]
    public async Task<IActionResult> DescartarParaCliente(int movId, int clienteId, [FromBody] DescartarRequest? req = null)
    {
        var existe = await _db.CafeExtractoMovDescartadosPorCliente
            .FirstOrDefaultAsync(d => d.MovimientoId == movId && d.ClienteId == clienteId);
        if (existe != null) return Ok(new { ok = true, yaEstaba = true });
        _db.CafeExtractoMovDescartadosPorCliente.Add(new CafeExtractoMovDescartadoPorCliente
        {
            MovimientoId = movId,
            ClienteId = clienteId,
            DescartadoAt = DateTime.UtcNow,
            DescartadoPor = string.IsNullOrWhiteSpace(req?.Operador) ? null : req.Operador.Trim()
        });
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, yaEstaba = false });
    }

    /// <summary>Restaura un movimiento descartado: vuelve a aparecer en el modal de cobranzas
    /// de ese cliente.</summary>
    [HttpDelete("{movId:int}/descartar-cliente/{clienteId:int}")]
    public async Task<IActionResult> RestaurarParaCliente(int movId, int clienteId)
    {
        var row = await _db.CafeExtractoMovDescartadosPorCliente
            .FirstOrDefaultAsync(d => d.MovimientoId == movId && d.ClienteId == clienteId);
        if (row == null) return Ok(new { ok = true, noEstaba = true });
        _db.CafeExtractoMovDescartadosPorCliente.Remove(row);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, noEstaba = false });
    }

    public record DescartadoDto(int MovimientoId, DateTime Fecha, string? Descripcion, decimal Importe, DateTime DescartadoAt, string? DescartadoPor);

    /// <summary>Lista los movimientos que fueron descartados para un cliente (para mostrarlos
    /// en el toggle "Ver descartados" del modal de cobranzas y permitir restaurar).</summary>
    [HttpGet("descartados/{clienteId:int}")]
    public async Task<IActionResult> ListarDescartados(int clienteId)
    {
        var rows = await _db.CafeExtractoMovDescartadosPorCliente
            .Where(d => d.ClienteId == clienteId)
            .Join(_db.CafeExtractoMovimientos, d => d.MovimientoId, m => m.Id,
                (d, m) => new DescartadoDto(m.Id, m.Fecha, m.Descripcion, m.Creditos, d.DescartadoAt, d.DescartadoPor))
            .OrderByDescending(d => d.DescartadoAt)
            .ToListAsync();
        return Ok(rows);
    }

    [HttpPost("{id:int}/asociar")]
    public async Task<IActionResult> Asociar(int id, [FromBody] AsociarRequest req)
    {
        var m = await _db.CafeExtractoMovimientos.FirstOrDefaultAsync(x => x.Id == id);
        if (m is null) return NotFound();
        var v = await _db.CafeVentas.FirstOrDefaultAsync(x => x.Id == req.VentaId);
        if (v is null) return BadRequest(new { error = "Venta no existe" });
        m.VentaIdAsociada = req.VentaId;
        m.AsociadoPor = string.IsNullOrWhiteSpace(req.Operador) ? null : req.Operador.Trim();
        m.AsociadoAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { id = m.Id, ventaId = v.Id, ventaNumero = v.Numero });
    }

    [HttpDelete("{id:int}/asociar")]
    public async Task<IActionResult> Desasociar(int id)
    {
        var m = await _db.CafeExtractoMovimientos.FirstOrDefaultAsync(x => x.Id == id);
        if (m is null) return NotFound();
        m.VentaIdAsociada = null;
        m.AsociadoPor = null;
        m.AsociadoAt = null;
        await _db.SaveChangesAsync();
        return Ok();
    }

    [HttpPost("import")]
    [RequestSizeLimit(50 * 1024 * 1024)]
    public async Task<IActionResult> Import(IFormFileCollection files)
    {
        if (files == null || files.Count == 0)
            return BadRequest(new { error = "Subi al menos un archivo del extracto" });
        var resultados = new List<ImportResultDto>();
        foreach (var file in files)
        {
            try
            {
                var ext = System.IO.Path.GetExtension(file.FileName).ToLowerInvariant();
                ImportResultDto res;
                if (ext == ".csv" || ext == ".txt")
                {
                    res = await ProcesarCsvAsync(file);
                }
                else
                {
                    using var stream = file.OpenReadStream();
                    using var wb = new XLWorkbook(stream);
                    var ws = wb.Worksheets.First();
                    res = await ProcesarExtractoAsync(ws, file.FileName);
                }
                resultados.Add(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error procesando extracto {file}", file.FileName);
                resultados.Add(new ImportResultDto(file.FileName, 0, 0, new List<string> { ex.Message }));
            }
        }
        return Ok(resultados);
    }

    /// <summary>Parser de CSV del extracto del Galicia. Mas robusto que el Excel porque
    /// el banco lo exporta con fechas en formato dd/MM/yyyy directo, sin ISO strings.</summary>
    private async Task<ImportResultDto> ProcesarCsvAsync(IFormFile file)
    {
        var errores = new List<string>();
        using var reader = new StreamReader(file.OpenReadStream(), System.Text.Encoding.UTF8, true);
        var allText = await reader.ReadToEndAsync();
        // Detectar separador (puede ser ; , o tab segun region)
        char sep = ',';
        var firstLine = allText.Split('\n').FirstOrDefault() ?? "";
        if (firstLine.Count(c => c == ';') > firstLine.Count(c => c == ','))
            sep = ';';
        else if (firstLine.Count(c => c == '\t') > firstLine.Count(c => c == ','))
            sep = '\t';

        var lineas = allText.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (lineas.Count < 2)
            return new ImportResultDto(file.FileName, 0, 0, new List<string> { "CSV vacio o sin datos" });

        // Parsear primera linea como header. NORMALIZAMOS removiendo espacios para que matchee
        // tanto "Leyendas Adicionales 1" (Excel) como "Leyendas Adicionales1" (CSV).
        var headers = ParseCsvLine(lineas[0], sep);
        var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Length; i++)
        {
            var key = headers[i].Trim();
            if (!idx.ContainsKey(key)) idx[key] = i;
            var normalizado = new string(key.Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (!idx.ContainsKey(normalizado)) idx[normalizado] = i;
        }

        if (!idx.ContainsKey("Fecha") || !idx.ContainsKey("Saldo"))
            return new ImportResultDto(file.FileName, 0, 0, new List<string> { "CSV no parece ser un extracto bancario (faltan columnas Fecha o Saldo). Detecte: " + string.Join(", ", headers.Take(5)) });

        // Dedup por CANTIDAD de copias por clave (fecha+desc+débito+crédito), NO por saldo.
        // Ver Services/ExtractoDedup.
        var conteoExistente = (await _db.CafeExtractoMovimientos
                .Select(m => new { m.Fecha, m.Descripcion, m.Debitos, m.Creditos })
                .ToListAsync())
            .GroupBy(x => ExtractoDedup.Clave(x.Fecha, x.Descripcion, x.Debitos, x.Creditos))
            .ToDictionary(g => g.Key, g => g.Count());
        var vistosEnImport = new Dictionary<string, int>();
        int nuevos = 0, sinCambios = 0;

        for (int li = 1; li < lineas.Count; li++)
        {
            try
            {
                var celdas = ParseCsvLine(lineas[li], sep);
                // GetCol busca por nombre exacto y por nombre sin espacios (para que matchee
                // tanto "Leyendas Adicionales 1" como "Leyendas Adicionales1").
                string GetCol(string n)
                {
                    if (idx.TryGetValue(n, out var ix1) && ix1 < celdas.Length) return celdas[ix1].Trim();
                    var sinEsp = new string(n.Where(c => !char.IsWhiteSpace(c)).ToArray());
                    if (idx.TryGetValue(sinEsp, out var ix2) && ix2 < celdas.Length) return celdas[ix2].Trim();
                    return "";
                }
                DateTime? ParseFecha(string s)
                {
                    if (string.IsNullOrWhiteSpace(s)) return null;
                    var formatos = new[] { "dd/MM/yyyy", "dd-MM-yyyy", "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss.fffZ", "yyyy-MM-ddTHH:mm:ssZ", "MM/dd/yyyy" };
                    foreach (var fmt in formatos)
                        if (DateTime.TryParseExact(s, fmt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var d))
                            return d;
                    return DateTime.TryParse(s, out var d2) ? d2 : null;
                }
                decimal ParseDec(string s) =>
                    decimal.TryParse(s.Replace(".", "").Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0m;

                var fecha = ParseFecha(GetCol("Fecha"));
                if (fecha is null) continue;
                var desc = GetCol("Descripción");
                var debitos = ParseDec(GetCol("Débitos"));
                var creditos = ParseDec(GetCol("Créditos"));
                var saldo = ParseDec(GetCol("Saldo"));

                var clave = ExtractoDedup.Clave(fecha.Value, desc, debitos, creditos);
                int yaEnDb = conteoExistente.TryGetValue(clave, out var ne) ? ne : 0;
                int ocurrencia = (vistosEnImport.TryGetValue(clave, out var vv) ? vv : 0) + 1;
                vistosEnImport[clave] = ocurrencia;
                if (ocurrencia <= yaEnDb) { sinCambios++; continue; }
                var hash = ExtractoDedup.Hash(clave, ocurrencia);

                var entrada = new CafeExtractoMovimiento
                {
                    Fecha = fecha.Value,
                    Descripcion = NullIfEmpty(desc),
                    Origen = NullIfEmpty(GetCol("Origen")),
                    Debitos = debitos,
                    Creditos = creditos,
                    Saldo = saldo,
                    GrupoConceptos = NullIfEmpty(GetCol("Grupo de Conceptos")),
                    Concepto = NullIfEmpty(GetCol("Concepto")),
                    NumeroTerminal = NullIfEmpty(GetCol("Número de Terminal")),
                    ObservacionesCliente = NullIfEmpty(GetCol("Observaciones Cliente")),
                    NumeroComprobante = NullIfEmpty(GetCol("Número de Comprobante")),
                    LeyendaAdicional1 = NullIfEmpty(GetCol("Leyendas Adicionales 1")),
                    LeyendaAdicional2 = NullIfEmpty(GetCol("Leyendas Adicionales 2")),
                    LeyendaAdicional3 = NullIfEmpty(GetCol("Leyendas Adicionales 3")),
                    LeyendaAdicional4 = NullIfEmpty(GetCol("Leyendas Adicionales 4")),
                    TipoMovimiento = NullIfEmpty(GetCol("Tipo de Movimiento")),
                    HashUnico = hash,
                    ArchivoOrigen = file.FileName,
                    ImportadoAt = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow
                };
                _db.CafeExtractoMovimientos.Add(entrada);
                nuevos++;
            }
            catch (Exception ex)
            {
                errores.Add($"Linea {li + 1}: {ex.Message}");
            }
        }
        await _db.SaveChangesAsync();
        return new ImportResultDto(file.FileName, nuevos, sinCambios, errores);
    }

    /// <summary>Parser simple de CSV que respeta comillas. Soporta campos con separador adentro
    /// si estan entre comillas dobles.</summary>
    private static string[] ParseCsvLine(string line, char sep)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (c == sep && !inQuotes) { result.Add(current.ToString()); current.Clear(); continue; }
            if (c == '\r') continue;
            current.Append(c);
        }
        result.Add(current.ToString());
        return result.ToArray();
    }

    private async Task<ImportResultDto> ProcesarExtractoAsync(IXLWorksheet ws, string fileName)
    {
        var errores = new List<string>();
        // El extracto del Galicia tiene header en fila 1
        var headerRow = 1;
        var firstDataRow = 2;

        var headers = ws.Row(headerRow).CellsUsed().ToDictionary(c => c.Address.ColumnNumber, c => (c.GetString() ?? "").Trim());
        if (headers.Count == 0) return new ImportResultDto(fileName, 0, 0, new List<string> { "No se encontro header en fila 1" });
        var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in headers.OrderBy(k => k.Key)) if (!idx.ContainsKey(kv.Value)) idx[kv.Value] = kv.Key;

        // Verificar columnas minimas
        if (!idx.ContainsKey("Fecha") || !idx.ContainsKey("Saldo"))
            return new ImportResultDto(fileName, 0, 0, new List<string> { "El archivo no parece ser un extracto bancario (faltan Fecha o Saldo)" });

        // Dedup por CANTIDAD de copias por clave (fecha+desc+débito+crédito), NO por saldo.
        // Ver Services/ExtractoDedup.
        var conteoExistente = (await _db.CafeExtractoMovimientos
                .Select(m => new { m.Fecha, m.Descripcion, m.Debitos, m.Creditos })
                .ToListAsync())
            .GroupBy(x => ExtractoDedup.Clave(x.Fecha, x.Descripcion, x.Debitos, x.Creditos))
            .ToDictionary(g => g.Key, g => g.Count());
        var vistosEnImport = new Dictionary<string, int>();

        int nuevos = 0, sinCambios = 0;
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        for (int r = firstDataRow; r <= lastRow; r++)
        {
            try
            {
                // ── Lectores defensivos ──
                // Cada celda se intenta como raw value (object), si falla como string, si falla
                // como GetFormattedString. Asi sobrevivimos a celdas con t="d" (ISO strings)
                // que ClosedXML no maneja bien.
                string SafeRaw(int col)
                {
                    var cc = ws.Cell(r, col);
                    // Intento 1: .Value como object (XLCellValue tiene .ToString() seguro)
                    try { var v = cc.Value; if (v.IsDateTime) return v.GetDateTime().ToString("o"); if (v.IsNumber) return v.GetNumber().ToString(System.Globalization.CultureInfo.InvariantCulture); if (v.IsText) return v.GetText(); return v.ToString() ?? ""; } catch { }
                    // Intento 2: GetString tradicional
                    try { return cc.GetString() ?? ""; } catch { }
                    // Intento 3: GetFormattedString
                    try { return cc.GetFormattedString() ?? ""; } catch { }
                    return "";
                }
                string Get(string n) => idx.TryGetValue(n, out var c) ? SafeRaw(c).Trim() : "";
                DateTime? GetDate(string n)
                {
                    if (!idx.TryGetValue(n, out var c)) return null;
                    var raw = SafeRaw(c).Trim();
                    if (string.IsNullOrEmpty(raw)) return null;
                    // Probar formato ISO primero (caso comun con t="d")
                    var formatos = new[] { "yyyy-MM-ddTHH:mm:ss.fffZ", "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss.fffffffZ", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd", "dd/MM/yyyy", "dd-MM-yyyy", "MM/dd/yyyy" };
                    foreach (var fmt in formatos)
                    {
                        if (DateTime.TryParseExact(raw, fmt, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d))
                            return d.ToLocalTime();
                    }
                    // Excel serial date numerico
                    if (double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var num)
                        && num > 1 && num < 100000)
                    {
                        try { return DateTime.FromOADate(num); } catch { }
                    }
                    // Fallback general
                    if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d2))
                        return d2.ToLocalTime();
                    return null;
                }
                decimal GetDec(string n)
                {
                    if (!idx.TryGetValue(n, out var c)) return 0m;
                    var raw = SafeRaw(c).Trim().Replace(",", ".");
                    if (string.IsNullOrEmpty(raw)) return 0m;
                    return decimal.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0m;
                }

                var fecha = GetDate("Fecha");
                if (fecha is null) continue;
                var descripcion = Get("Descripción");
                var debitos = GetDec("Débitos");
                var creditos = GetDec("Créditos");
                var saldo = GetDec("Saldo");

                var clave = ExtractoDedup.Clave(fecha.Value, descripcion, debitos, creditos);
                int yaEnDb = conteoExistente.TryGetValue(clave, out var ne) ? ne : 0;
                int ocurrencia = (vistosEnImport.TryGetValue(clave, out var vv) ? vv : 0) + 1;
                vistosEnImport[clave] = ocurrencia;
                if (ocurrencia <= yaEnDb) { sinCambios++; continue; }
                var hash = ExtractoDedup.Hash(clave, ocurrencia);

                var entrada = new CafeExtractoMovimiento
                {
                    Fecha = fecha.Value,
                    Descripcion = NullIfEmpty(descripcion),
                    Origen = NullIfEmpty(Get("Origen")),
                    Debitos = debitos,
                    Creditos = creditos,
                    Saldo = saldo,
                    GrupoConceptos = NullIfEmpty(Get("Grupo de Conceptos")),
                    Concepto = NullIfEmpty(Get("Concepto")),
                    NumeroTerminal = NullIfEmpty(Get("Número de Terminal")),
                    ObservacionesCliente = NullIfEmpty(Get("Observaciones Cliente")),
                    NumeroComprobante = NullIfEmpty(Get("Número de Comprobante")),
                    LeyendaAdicional1 = NullIfEmpty(Get("Leyendas Adicionales 1")),
                    LeyendaAdicional2 = NullIfEmpty(Get("Leyendas Adicionales 2")),
                    LeyendaAdicional3 = NullIfEmpty(Get("Leyendas Adicionales 3")),
                    LeyendaAdicional4 = NullIfEmpty(Get("Leyendas Adicionales 4")),
                    TipoMovimiento = NullIfEmpty(Get("Tipo de Movimiento")),
                    HashUnico = hash,
                    ArchivoOrigen = fileName,
                    ImportadoAt = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow
                };
                _db.CafeExtractoMovimientos.Add(entrada);
                nuevos++;
            }
            catch (Exception ex)
            {
                errores.Add($"Fila {r}: {ex.Message}");
            }
        }
        await _db.SaveChangesAsync();
        return new ImportResultDto(fileName, nuevos, sinCambios, errores);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
