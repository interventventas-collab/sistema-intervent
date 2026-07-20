using Api.Data;
using Api.Models;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Importa el Excel de cheques del banco (Galicia / BBVA / etc.) a Cafe_ChequesBanco,
/// con deduplicación por el "ID del cheque" del banco (campo IdBanco) — así el import
/// es idempotente. Es la MISMA lógica que estaba dentro de CafeChequesBancoController,
/// extraída acá para que la usen tanto la subida manual del usuario como el flujo
/// automático de Galicia (robot que baja el .XLS solo).
/// </summary>
public class ChequesBancoImportService
{
    private readonly AppDbContext _db;
    private readonly ILogger<ChequesBancoImportService> _logger;

    public ChequesBancoImportService(AppDbContext db, ILogger<ChequesBancoImportService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public record ImportResultDto(string Archivo, string TipoDetectado, int Nuevos, int Actualizados, int SinCambios, List<string> Errores);

    /// <summary>Abre un Excel desde un Stream (subida manual) y procesa su primera hoja.</summary>
    public async Task<ImportResultDto> ImportStreamAsync(Stream stream, string fileName)
    {
        try
        {
            using var wb = new XLWorkbook(stream);
            var ws = wb.Worksheets.First();
            return await ProcesarHojaAsync(ws, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error procesando {file}", fileName);
            return new ImportResultDto(fileName, "ERROR", 0, 0, 0, new List<string> { ex.Message });
        }
    }

    /// <summary>Abre un Excel desde bytes (lo que baja el robot en base64) y procesa su primera hoja.</summary>
    public async Task<ImportResultDto> ImportBytesAsync(byte[] bytes, string fileName)
    {
        using var stream = new MemoryStream(bytes);
        return await ImportStreamAsync(stream, fileName);
    }

    /// <summary>El banco usa "Recibido" para cheques apenas acreditados y "Disponible" para
    /// los que ya pueden usarse. Para nuestro flujo (aplicar a cobranza) son lo mismo:
    /// cheques en cartera. Normalizamos a "Disponible" asi engancha con los filtros del
    /// list/stats y aparecen en la pestaña correcta.</summary>
    private static string NormalizarEstado(string tipo, string raw)
    {
        var s = (raw ?? "").Trim();
        if (string.Equals(tipo, "RECIBIDO", StringComparison.OrdinalIgnoreCase)
            && string.Equals(s, "Recibido", StringComparison.OrdinalIgnoreCase))
            return "Disponible";
        return s;
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
                    // Normalizamos: en RECIBIDOS el banco a veces pone "Recibido" (cheque
                    // recien acreditado) y a veces "Disponible" (ya en cartera). Para
                    // nosotros son lo mismo: un cheque que recibimos y todavia no usamos.
                    Estado = NormalizarEstado(tipoDetectado, Get("Estado")),
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
