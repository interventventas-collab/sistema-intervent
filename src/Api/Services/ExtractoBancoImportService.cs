using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Importa un CSV del extracto de Banco Galicia a CafeExtractoMovimientos, con
/// deduplicación por hash (no reinserta movimientos ya cargados). Es la MISMA
/// lógica que CafeExtractoBancoController.ProcesarCsvAsync, extraída acá para que
/// el flujo automático de Galicia (robot) pueda reusarla sin subir un archivo a mano.
/// </summary>
public class ExtractoBancoImportService
{
    private readonly AppDbContext _db;

    public ExtractoBancoImportService(AppDbContext db) { _db = db; }

    public record Resultado(int Nuevos, int SinCambios, List<string> Errores);

    /// <summary>Importa el contenido de texto de un CSV del Galicia.</summary>
    public async Task<Resultado> ImportCsvTextAsync(string allText, string archivoOrigen)
    {
        var errores = new List<string>();

        // El CSV del Galicia viene con BOM al inicio (U+FEFF). Si no se saca, la
        // primera columna queda como "﻿Fecha" y no matchea "Fecha".
        // (El importador por upload lo saca vía StreamReader detectEncoding; acá,
        // como recibimos el texto ya decodificado, lo removemos a mano.)
        allText = allText.TrimStart('﻿', '￾', '​');

        // Detectar separador (puede ser ; , o tab segun region)
        char sep = ',';
        var firstLine = allText.Split('\n').FirstOrDefault() ?? "";
        if (firstLine.Count(c => c == ';') > firstLine.Count(c => c == ','))
            sep = ';';
        else if (firstLine.Count(c => c == '\t') > firstLine.Count(c => c == ','))
            sep = '\t';

        var lineas = allText.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (lineas.Count < 2)
            return new Resultado(0, 0, new List<string> { "CSV vacio o sin datos" });

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
            return new Resultado(0, 0, new List<string> { "CSV no parece ser un extracto bancario (faltan columnas Fecha o Saldo). Detecte: " + string.Join(", ", headers.Take(5)) });

        // Dedup por CANTIDAD de copias por clave (fecha+desc+débito+crédito), NO por saldo.
        // Ver ExtractoDedup para el porqué. conteoExistente = cuántas copias de cada clave ya
        // están en la DB; vistosEnImport = cuántas llevamos vistas en ESTE archivo.
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
                        if (DateTime.TryParseExact(s, fmt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d))
                            return d;
                    return DateTime.TryParse(s, out var d2) ? d2 : null;
                }
                decimal ParseDec(string s) =>
                    decimal.TryParse(s.Replace(".", "").Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;

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
                    ArchivoOrigen = archivoOrigen,
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
        return new Resultado(nuevos, sinCambios, errores);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Parser simple de CSV que respeta comillas dobles.</summary>
    private static string[] ParseCsvLine(string line, char sep)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else inQuotes = !inQuotes;
            }
            else if (c == sep && !inQuotes)
            {
                result.Add(sb.ToString());
                sb.Clear();
            }
            else if (c != '\r')
            {
                sb.Append(c);
            }
        }
        result.Add(sb.ToString());
        return result.ToArray();
    }
}
