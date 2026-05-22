using Api.Data;
using Api.Models;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// CRUD + importacion del listado de cheques que descarga el banco (Galicia / BBVA / etc.).
/// El usuario sube los 3 Excel del banco (recibidos / emitidos / endosados), aca los parseamos
/// y guardamos cada cheque en Cafe_ChequesBanco. La clave para no duplicar al re-importar es
/// el "ID del cheque" del banco (campo IdBanco).
/// </summary>
[ApiController]
[Route("api/cafe/cheques-banco")]
[Authorize]
public class CafeChequesBancoController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<CafeChequesBancoController> _logger;

    public CafeChequesBancoController(AppDbContext db, ILogger<CafeChequesBancoController> logger)
    {
        _db = db;
        _logger = logger;
    }

    public record ChequeBancoDto(int Id, string IdBanco, string Tipo, string Numero,
        string? Cmc7, string? Clausula, string? BancoEmisor, DateTime? FechaEmision, DateTime? FechaPago,
        decimal Importe, string Estado, string? Motivo, string? CuentaLibradora, string? CbuDeposito,
        string? LibradorNombre, string? LibradorCuit,
        string? BeneficiarioActualNombre, string? BeneficiarioActualCuit,
        string? ContraparteNombre, string? ContraparteCuit,
        int CantidadEndosos, int CantidadCesiones, int CantidadAvales,
        // Linkeo a cobranza (cuando el e-cheq se asocio)
        int? CafeChequeId = null, int? CobranzaId = null, string? CobranzaNumero = null);

    public record ChequesResumenDto(int Cantidad, decimal Importe);
    public record StatsDto(
        ChequesResumenDto EmitidosPorPagar,    // Tipo=EMITIDO, Estado=Aceptado
        ChequesResumenDto RecibidosDisponibles, // Tipo=RECIBIDO, Estado=Disponible
        ChequesResumenDto EmitidosPagados,      // historico
        ChequesResumenDto RecibidosUsados);     // historico (pagados + endosados)

    [HttpGet("stats")]
    public async Task<IActionResult> Stats()
    {
        var todos = await _db.CafeChequesBanco.ToListAsync();
        ChequesResumenDto Sum(IEnumerable<CafeChequeBanco> xs)
        {
            var l = xs.ToList();
            return new ChequesResumenDto(l.Count, l.Sum(x => x.Importe));
        }
        return Ok(new StatsDto(
            EmitidosPorPagar: Sum(todos.Where(x => x.Tipo == "EMITIDO" && string.Equals(x.Estado, "Aceptado", StringComparison.OrdinalIgnoreCase))),
            RecibidosDisponibles: Sum(todos.Where(x => x.Tipo == "RECIBIDO" && string.Equals(x.Estado, "Disponible", StringComparison.OrdinalIgnoreCase))),
            EmitidosPagados: Sum(todos.Where(x => x.Tipo == "EMITIDO" && string.Equals(x.Estado, "Pagado", StringComparison.OrdinalIgnoreCase))),
            RecibidosUsados: Sum(todos.Where(x => x.Tipo != "EMITIDO" && !string.Equals(x.Estado, "Disponible", StringComparison.OrdinalIgnoreCase)))
        ));
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? tipo = null, [FromQuery] string? estado = null,
        [FromQuery] string? q = null, [FromQuery] int take = 500)
    {
        var query = _db.CafeChequesBanco.Include(c => c.Cobranza).AsQueryable();
        if (!string.IsNullOrWhiteSpace(tipo)) query = query.Where(c => c.Tipo == tipo.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(estado))
        {
            query = query.Where(c => c.Estado == estado);
            // Si pide Disponibles, excluir los que ya fueron asociados a una cobranza
            // (el flag CafeChequeId no es null cuando el e-cheq se uso para cobrar al cliente).
            if (string.Equals(estado, "Disponible", StringComparison.OrdinalIgnoreCase))
                query = query.Where(c => c.CafeChequeId == null);
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim();
            query = query.Where(c => c.Numero.Contains(t) ||
                (c.ContraparteNombre != null && c.ContraparteNombre.Contains(t)) ||
                (c.LibradorNombre != null && c.LibradorNombre.Contains(t)));
        }
        var list = await query
            .OrderBy(c => c.FechaPago ?? DateTime.MaxValue)
            .ThenBy(c => c.Id)
            .Take(Math.Clamp(take, 1, 2000))
            .Select(c => new ChequeBancoDto(c.Id, c.IdBanco, c.Tipo, c.Numero,
                c.Cmc7, c.Clausula, c.BancoEmisor, c.FechaEmision, c.FechaPago,
                c.Importe, c.Estado, c.Motivo, c.CuentaLibradora, c.CbuDeposito,
                c.LibradorNombre, c.LibradorCuit,
                c.BeneficiarioActualNombre, c.BeneficiarioActualCuit,
                c.ContraparteNombre, c.ContraparteCuit,
                c.CantidadEndosos, c.CantidadCesiones, c.CantidadAvales,
                c.CafeChequeId, c.CobranzaId, c.Cobranza != null ? c.Cobranza.Numero : null))
            .ToListAsync();
        return Ok(list);
    }

    public record SugerenciaClienteDto(int Id, string Nombre, string? RazonSocial, string? Cuit, int? CodigoInterno);
    public record ImputarComprobanteItem(int? VentaId, decimal Importe);
    public record AsociarECheqRequest(
        int ClienteId,
        decimal Retenciones,
        string? Observaciones,
        List<ImputarComprobanteItem> Comprobantes);

    /// <summary>Busca clientes que matchean al librador del e-cheq por CUIT exacto.
    /// Util para que el modal pre-cargue el cliente correcto sin tipearlo.</summary>
    [HttpGet("{id:int}/cliente-sugerido")]
    public async Task<IActionResult> ClienteSugerido(int id)
    {
        var ec = await _db.CafeChequesBanco.FindAsync(id);
        if (ec is null) return NotFound();
        var cuit = ec.LibradorCuit;
        if (string.IsNullOrWhiteSpace(cuit)) return Ok(new List<SugerenciaClienteDto>());
        var matches = await _db.CafeClientes
            .Where(c => c.IsActive && c.Cuit == cuit)
            .OrderBy(c => c.Nombre)
            .Select(c => new SugerenciaClienteDto(c.Id, c.Nombre, c.RazonSocial, c.Cuit, c.CodigoInterno))
            .ToListAsync();
        return Ok(matches);
    }

    /// <summary>
    /// Toma un e-cheq Disponible (del extracto bancario) y lo asocia a una cobranza:
    ///   1. Crea un CafeCheque "espejo" en cartera (linkeado al e-cheq via ChequeBancoId).
    ///   2. Crea una CafeCobranza para el cliente con el medio "Cheques en cartera" apuntando a ese cheque.
    ///   3. Imputa la cobranza a las facturas del cliente que vengan en req.Comprobantes.
    ///   4. Marca el e-cheq como utilizado (CafeChequeId + CobranzaId) para sacarlo de "Disponibles".
    /// La suma de Comprobantes debe igualar e-cheq.Importe + Retenciones.
    /// </summary>
    [HttpPost("{id:int}/asociar-cobranza")]
    public async Task<IActionResult> AsociarCobranza(int id, [FromBody] AsociarECheqRequest req)
    {
        var ec = await _db.CafeChequesBanco.FindAsync(id);
        if (ec is null) return NotFound(new { error = "E-cheq no encontrado" });
        if (ec.CafeChequeId.HasValue || ec.CobranzaId.HasValue)
            return BadRequest(new { error = "Este e-cheq ya está asociado a una cobranza" });
        if (!string.Equals(ec.Estado, "Disponible", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = $"El e-cheq no está Disponible (estado actual: {ec.Estado})" });

        var cliente = await _db.CafeClientes.FindAsync(req.ClienteId);
        if (cliente is null) return BadRequest(new { error = "Cliente no encontrado" });
        if (req.Comprobantes == null || req.Comprobantes.Count == 0)
            return BadRequest(new { error = "Imputá el cheque al menos a un comprobante (o como 'a cuenta')" });

        var sumComprobantes = req.Comprobantes.Sum(c => c.Importe);
        var retenciones = Math.Max(0m, req.Retenciones);
        if (Math.Abs(sumComprobantes - (ec.Importe + retenciones)) > 0.01m)
            return BadRequest(new { error = $"No cuadra: imputado ${sumComprobantes:N2} ≠ importe del e-cheq ${ec.Importe:N2} + retenciones ${retenciones:N2}" });

        // Resolver caja de cheques en cartera
        var caja = await _db.CafeCajas.FirstOrDefaultAsync(c => c.Tipo == "CHEQUES_CARTERA" && c.IsActive);
        if (caja is null) return BadRequest(new { error = "No hay una caja de tipo CHEQUES_CARTERA configurada" });

        // Generar numero correlativo de cobranza
        var ultimoNum = await _db.CafeCobranzas.Select(c => c.Numero).ToListAsync();
        var maxSec = 0;
        foreach (var num in ultimoNum)
        {
            var parts = (num ?? "").Split('-');
            if (parts.Length >= 2 && int.TryParse(parts[^1], out var n) && n > maxSec) maxSec = n;
        }
        var numeroCobranza = $"0100-{(maxSec + 1):D8}";

        // 1. Crear cobranza
        var cobranza = new CafeCobranza
        {
            Numero = numeroCobranza,
            Fecha = DateTime.UtcNow,
            ClienteId = req.ClienteId,
            Total = ec.Importe,
            Retenciones = retenciones,
            Operador = User?.Identity?.Name,
            Observaciones = string.IsNullOrWhiteSpace(req.Observaciones)
                ? $"Cobranza por e-cheq {ec.BancoEmisor} N° {ec.Numero}"
                : req.Observaciones.Trim(),
            Estado = "VIGENTE"
        };
        _db.CafeCobranzas.Add(cobranza);
        await _db.SaveChangesAsync();

        // 2. Crear el CafeCheque espejo en cartera
        var cheque = new CafeCheque
        {
            Numero = ec.Numero,
            Banco = ec.BancoEmisor ?? "(sin nombre)",
            BancoId = ec.BancoId,
            Emisor = ec.LibradorNombre,
            Importe = ec.Importe,
            FechaCobro = ec.FechaPago,
            FechaVencimiento = ec.FechaPago,
            ClienteOrigenId = req.ClienteId,
            Estado = "EN_CARTERA",
            FechaCambioEstado = DateTime.UtcNow,
            CobranzaOrigenId = cobranza.Id,
            ChequeBancoId = ec.Id,
            Observaciones = $"Importado del extracto bancario (ID banco: {ec.IdBanco})",
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeCheques.Add(cheque);
        await _db.SaveChangesAsync();

        // 3. Comprobantes imputados
        foreach (var comp in req.Comprobantes)
        {
            _db.CafeCobranzasComprobantes.Add(new CafeCobranzaComprobante
            {
                CobranzaId = cobranza.Id,
                VentaId = comp.VentaId,
                Importe = comp.Importe
            });
        }

        // 4. Medio de pago: el cheque que recién creamos
        _db.CafeCobranzasMedios.Add(new CafeCobranzaMedio
        {
            CobranzaId = cobranza.Id,
            CajaId = caja.Id,
            Importe = ec.Importe,
            Referencia = $"E-cheq #{ec.Numero} ({ec.BancoEmisor})",
            ChequeId = cheque.Id
        });

        // 5. Marcar el e-cheq como utilizado para que salga de Disponibles
        ec.CafeChequeId = cheque.Id;
        ec.CobranzaId = cobranza.Id;
        ec.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        // 6. Sincronizar IsPaid de las ventas imputadas
        var ventaIds = req.Comprobantes.Where(c => c.VentaId.HasValue).Select(c => c.VentaId!.Value).Distinct().ToList();
        if (ventaIds.Count > 0)
        {
            var ventas = await _db.CafeVentas.Where(v => ventaIds.Contains(v.Id)).ToListAsync();
            var pagado = await _db.CafeCobranzasComprobantes
                .Where(c => c.VentaId != null && ventaIds.Contains(c.VentaId!.Value)
                    && c.Cobranza!.Estado == "VIGENTE")
                .GroupBy(c => c.VentaId!.Value)
                .Select(g => new { Id = g.Key, Total = g.Sum(x => x.Importe) })
                .ToDictionaryAsync(x => x.Id, x => x.Total);
            foreach (var v in ventas)
            {
                var pag = pagado.GetValueOrDefault(v.Id, 0m);
                var totalCobrar = (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m) ? v.ArcaImpTotal.Value : v.Total;
                v.IsPaid = pag >= totalCobrar - 0.01m;
            }
            await _db.SaveChangesAsync();
        }

        return Ok(new { cobranzaId = cobranza.Id, numero = numeroCobranza, chequeId = cheque.Id });
    }

    public record ImportResultDto(string Archivo, string TipoDetectado, int Nuevos, int Actualizados, int SinCambios, List<string> Errores);

    [HttpPost("import")]
    [RequestSizeLimit(50 * 1024 * 1024)]
    public async Task<IActionResult> Import(IFormFileCollection files)
    {
        if (files == null || files.Count == 0)
            return BadRequest(new { error = "Subi al menos un archivo Excel del banco." });
        var resultados = new List<ImportResultDto>();
        foreach (var file in files)
        {
            try
            {
                using var stream = file.OpenReadStream();
                using var wb = new XLWorkbook(stream);
                var ws = wb.Worksheets.First();
                var res = await ProcesarHojaAsync(ws, file.FileName);
                resultados.Add(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error procesando {file}", file.FileName);
                resultados.Add(new ImportResultDto(file.FileName, "ERROR", 0, 0, 0,
                    new List<string> { ex.Message }));
            }
        }
        return Ok(resultados);
    }

    /// <summary>Procesa una hoja del Excel del banco. Auto-detecta el tipo (RECIBIDO/EMITIDO/ENDOSADO)
    /// segun los headers (ej: "Recibido de" vs "Emitido a" vs "Enviado a"). Insert o update por IdBanco.</summary>
    private async Task<ImportResultDto> ProcesarHojaAsync(IXLWorksheet ws, string fileName)
    {
        var errores = new List<string>();
        // Los Excel del banco tienen 2 filas de headers: la primera con grupos ("Datos del cheque" repetido),
        // la segunda con los nombres reales de columnas. Tomamos la fila 2 como header real.
        var headerRow = 2;
        var firstDataRow = 3;
        var headers = ws.Row(headerRow).CellsUsed().ToDictionary(c => c.Address.ColumnNumber, c => (c.GetString() ?? "").Trim());
        if (headers.Count == 0)
            return new ImportResultDto(fileName, "ERROR", 0, 0, 0, new List<string> { "No se encontraron headers en la fila 2." });

        // Indice inverso: nombre de header → numero de columna
        // Si una columna se repite (caso del historial de endosos), tomamos la PRIMERA.
        var idxByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in headers.OrderBy(k => k.Key))
        {
            if (!idxByName.ContainsKey(kv.Value)) idxByName[kv.Value] = kv.Key;
        }

        // Detectar tipo segun headers presentes
        string tipoDetectado;
        if (idxByName.ContainsKey("Recibido de")) tipoDetectado = "RECIBIDO";
        else if (idxByName.ContainsKey("Emitido a") && idxByName.ContainsKey("Cuenta libradora")) tipoDetectado = "EMITIDO";
        else if (idxByName.ContainsKey("Enviado a")) tipoDetectado = "ENDOSADO";
        else
            return new ImportResultDto(fileName, "ERROR", 0, 0, 0,
                new List<string> { "No se pudo detectar el tipo. Esperaba columnas 'Recibido de', 'Emitido a' o 'Enviado a'." });

        // El nombre de la columna "Contraparte" cambia segun tipo
        var colContraparte = tipoDetectado switch
        {
            "RECIBIDO" => "Recibido de",
            "EMITIDO" => "Emitido a",
            "ENDOSADO" => "Enviado a",
            _ => ""
        };

        // Cargamos todos los cheques existentes una vez para evitar N queries
        var existentes = await _db.CafeChequesBanco.ToDictionaryAsync(c => c.IdBanco);

        int nuevos = 0, actualizados = 0, sinCambios = 0;
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        for (int r = firstDataRow; r <= lastRow; r++)
        {
            try
            {
                string Get(string name)
                {
                    if (!idxByName.TryGetValue(name, out var col)) return "";
                    var c = ws.Cell(r, col);
                    return (c.GetString() ?? "").Trim();
                }
                DateTime? GetDate(string name)
                {
                    if (!idxByName.TryGetValue(name, out var col)) return null;
                    var c = ws.Cell(r, col);
                    if (c.DataType == XLDataType.DateTime) return c.GetDateTime();
                    var s = (c.GetString() ?? "").Trim();
                    if (string.IsNullOrEmpty(s)) return null;
                    if (DateTime.TryParse(s, out var d)) return d;
                    return null;
                }
                decimal GetDecimal(string name)
                {
                    if (!idxByName.TryGetValue(name, out var col)) return 0m;
                    var c = ws.Cell(r, col);
                    if (c.DataType == XLDataType.Number) return (decimal)c.GetDouble();
                    var s = (c.GetString() ?? "").Trim().Replace(",", ".");
                    return decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0m;
                }
                int GetInt(string name)
                {
                    if (!idxByName.TryGetValue(name, out var col)) return 0;
                    var c = ws.Cell(r, col);
                    if (c.DataType == XLDataType.Number) return (int)c.GetDouble();
                    return int.TryParse(c.GetString() ?? "", out var i) ? i : 0;
                }

                var idBanco = Get("ID del cheque");
                if (string.IsNullOrWhiteSpace(idBanco)) continue; // fila vacia / mal formada

                // El librador y beneficiario actual aparecen 2 veces en el header (grupos). El IDX nos dio el primero.
                // Para los 2 "Razon social" + 2 "CUIT/CUIL/CDI" siguientes, accedemos por offset basados en la posicion.
                // Estrategia: buscar TODAS las apariciones de esas columnas y tomarlas en orden.
                var razonSocialCols = headers.Where(kv => kv.Value == "Razón social").OrderBy(kv => kv.Key).Select(kv => kv.Key).ToList();
                var cuitCols = headers.Where(kv => kv.Value == "CUIT/CUIL/CDI").OrderBy(kv => kv.Key).Select(kv => kv.Key).ToList();

                string? libradorNombre = null, libradorCuit = null, beneficiarioNombre = null, beneficiarioCuit = null;
                if (razonSocialCols.Count >= 1) libradorNombre = (ws.Cell(r, razonSocialCols[0]).GetString() ?? "").Trim();
                if (razonSocialCols.Count >= 2) beneficiarioNombre = (ws.Cell(r, razonSocialCols[1]).GetString() ?? "").Trim();
                // CUITs: los 2 primeros del header son del librador + contraparte (en orden de aparicion).
                // Como el orden de columnas no es identico entre archivos, mejor mapeamos por proximidad: la columna
                // CUIT inmediatamente a la derecha de "Razón social" la asociamos con esa razon social.
                if (razonSocialCols.Count >= 1)
                {
                    var c1 = cuitCols.FirstOrDefault(c => c > razonSocialCols[0]);
                    if (c1 > 0) libradorCuit = (ws.Cell(r, c1).GetString() ?? "").Trim();
                }
                if (razonSocialCols.Count >= 2)
                {
                    var c2 = cuitCols.FirstOrDefault(c => c > razonSocialCols[1]);
                    if (c2 > 0) beneficiarioCuit = (ws.Cell(r, c2).GetString() ?? "").Trim();
                }

                var contraparteNombre = Get(colContraparte);
                // CUIT de la contraparte: la primera columna "CUIT/CUIL/CDI" que aparece despues de la columna contraparte
                string? contraparteCuit = null;
                if (idxByName.TryGetValue(colContraparte, out var colCp))
                {
                    var cCp = cuitCols.FirstOrDefault(c => c > colCp);
                    if (cCp > 0) contraparteCuit = (ws.Cell(r, cCp).GetString() ?? "").Trim();
                }

                var entrada = new CafeChequeBanco
                {
                    IdBanco = idBanco,
                    Tipo = tipoDetectado,
                    Numero = Get("Nº de cheque"),
                    Cmc7 = NullIfEmpty(Get("CMC7")),
                    Clausula = NullIfEmpty(Get("Cláusula")),
                    BancoEmisor = NullIfEmpty(Get("Banco emisor")),
                    FechaEmision = GetDate("Fecha de emisión"),
                    FechaPago = GetDate("Fecha de pago"),
                    Importe = GetDecimal("Importe"),
                    Estado = Get("Estado"),
                    Motivo = NullIfEmpty(Get("Motivo y descripción")),
                    CuentaLibradora = NullIfEmpty(Get("Cuenta libradora")),
                    CbuDeposito = NullIfEmpty(Get("CBU Deposito")),
                    LibradorNombre = NullIfEmpty(libradorNombre),
                    LibradorCuit = NullIfEmpty(libradorCuit),
                    BeneficiarioActualNombre = NullIfEmpty(beneficiarioNombre),
                    BeneficiarioActualCuit = NullIfEmpty(beneficiarioCuit),
                    ContraparteNombre = NullIfEmpty(contraparteNombre),
                    ContraparteCuit = NullIfEmpty(contraparteCuit),
                    CantidadEndosos = GetInt("Cantidad de endosos"),
                    CantidadCesiones = GetInt("Cantidad de cesiones"),
                    CantidadAvales = GetInt("Cantidad de avales"),
                };

                if (existentes.TryGetValue(idBanco, out var existing))
                {
                    // Update si hubo cambios reales (sino "sin cambios")
                    bool cambio = existing.Estado != entrada.Estado
                        || existing.FechaPago != entrada.FechaPago
                        || existing.Importe != entrada.Importe
                        || existing.Motivo != entrada.Motivo
                        || existing.CbuDeposito != entrada.CbuDeposito
                        || existing.BeneficiarioActualNombre != entrada.BeneficiarioActualNombre;
                    if (cambio)
                    {
                        existing.Estado = entrada.Estado;
                        existing.FechaPago = entrada.FechaPago;
                        existing.Importe = entrada.Importe;
                        existing.Motivo = entrada.Motivo;
                        existing.CbuDeposito = entrada.CbuDeposito;
                        existing.BeneficiarioActualNombre = entrada.BeneficiarioActualNombre;
                        existing.BeneficiarioActualCuit = entrada.BeneficiarioActualCuit;
                        existing.ContraparteNombre = entrada.ContraparteNombre;
                        existing.ContraparteCuit = entrada.ContraparteCuit;
                        existing.UpdatedAt = DateTime.UtcNow;
                        actualizados++;
                    }
                    else sinCambios++;
                }
                else
                {
                    _db.CafeChequesBanco.Add(entrada);
                    existentes[idBanco] = entrada;
                    nuevos++;
                }
            }
            catch (Exception ex)
            {
                errores.Add($"Fila {r}: {ex.Message}");
            }
        }

        await _db.SaveChangesAsync();
        return new ImportResultDto(fileName, tipoDetectado, nuevos, actualizados, sinCambios, errores);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
