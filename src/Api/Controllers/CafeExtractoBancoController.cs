using Api.Data;
using Api.Models;
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
        int? ClienteSugeridoId, string? ClienteSugeridoNombre);

    public record SaldoBancoDto(decimal Saldo, DateTime UltimaFecha, int CantidadMovimientos);

    public record ImportResultDto(string Archivo, int Nuevos, int SinCambios, List<string> Errores);

    public record AsociarRequest(int VentaId, string? Operador);

    [HttpGet("saldo")]
    public async Task<IActionResult> Saldo()
    {
        var ult = await _db.CafeExtractoMovimientos.OrderByDescending(m => m.Fecha).ThenByDescending(m => m.Id)
            .FirstOrDefaultAsync();
        if (ult is null) return Ok(new SaldoBancoDto(0m, DateTime.MinValue, 0));
        var cant = await _db.CafeExtractoMovimientos.CountAsync();
        return Ok(new SaldoBancoDto(ult.Saldo, ult.Fecha, cant));
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
        if (asociado == "si") q = q.Where(m => m.VentaIdAsociada != null);
        else if (asociado == "no") q = q.Where(m => m.VentaIdAsociada == null);

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

        // Pre-cargar numeros de venta asociados
        var ventaIds = movs.Where(m => m.VentaIdAsociada.HasValue).Select(m => m.VentaIdAsociada!.Value).Distinct().ToList();
        var ventasDict = await _db.CafeVentas.Where(v => ventaIds.Contains(v.Id))
            .Select(v => new { v.Id, v.Numero })
            .ToDictionaryAsync(v => v.Id, v => v.Numero);

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
            if (m.VentaIdAsociada.HasValue && ventasDict.TryGetValue(m.VentaIdAsociada.Value, out var nro))
                ventaNumero = nro;
            return new ExtractoMovimientoDto(m.Id, m.Fecha, m.Descripcion, m.Debitos, m.Creditos, m.Saldo,
                m.Concepto, m.ObservacionesCliente, m.LeyendaAdicional1, m.LeyendaAdicional2, m.TipoMovimiento,
                m.VentaIdAsociada, ventaNumero, m.AsociadoPor, m.AsociadoAt,
                cliId, cliNom);
        }).ToList();
        return Ok(result);
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
            return BadRequest(new { error = "Subi al menos un archivo Excel del extracto" });
        var resultados = new List<ImportResultDto>();
        foreach (var file in files)
        {
            try
            {
                using var stream = file.OpenReadStream();
                using var wb = new XLWorkbook(stream);
                var ws = wb.Worksheets.First();
                var res = await ProcesarExtractoAsync(ws, file.FileName);
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

        var existentes = await _db.CafeExtractoMovimientos.Select(m => m.HashUnico).ToListAsync();
        var hashSet = new HashSet<string>(existentes);

        int nuevos = 0, sinCambios = 0;
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        for (int r = firstDataRow; r <= lastRow; r++)
        {
            try
            {
                string Get(string n) => idx.TryGetValue(n, out var c) ? (ws.Cell(r, c).GetString() ?? "").Trim() : "";
                DateTime? GetDate(string n)
                {
                    if (!idx.TryGetValue(n, out var c)) return null;
                    var cc = ws.Cell(r, c);
                    if (cc.DataType == XLDataType.DateTime) return cc.GetDateTime();
                    var s = (cc.GetString() ?? "").Trim();
                    if (string.IsNullOrEmpty(s)) return null;
                    return DateTime.TryParse(s, out var d) ? d : null;
                }
                decimal GetDec(string n)
                {
                    if (!idx.TryGetValue(n, out var c)) return 0m;
                    var cc = ws.Cell(r, c);
                    if (cc.DataType == XLDataType.Number) return (decimal)cc.GetDouble();
                    var s = (cc.GetString() ?? "").Trim().Replace(",", ".");
                    return decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0m;
                }

                var fecha = GetDate("Fecha");
                if (fecha is null) continue;
                var descripcion = Get("Descripción");
                var debitos = GetDec("Débitos");
                var creditos = GetDec("Créditos");
                var saldo = GetDec("Saldo");

                // Hash unico: fecha + descripcion + debitos + creditos + saldo
                var raw = $"{fecha:yyyyMMdd}|{descripcion}|{debitos}|{creditos}|{saldo}";
                using var sha = SHA256.Create();
                var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw))).Substring(0, 32);

                if (hashSet.Contains(hash)) { sinCambios++; continue; }

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
                hashSet.Add(hash);
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
