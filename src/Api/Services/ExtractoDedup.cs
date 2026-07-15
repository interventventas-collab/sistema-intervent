using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Api.Services;

/// <summary>
/// Deduplicación de movimientos del extracto bancario, compartida por los 3 caminos
/// de importación (robot Galicia, subir CSV, subir Excel).
///
/// PROBLEMA QUE RESUELVE: el hash viejo incluía el Saldo (saldo corriente de la cuenta).
/// El robot del Galicia baja el extracto varias veces por día y, entre bajada y bajada,
/// el saldo del MISMO movimiento cambia (porque en el medio entraron/salieron otros
/// movimientos). Con el saldo adentro del hash, el mismo movimiento parecía "nuevo" y se
/// cargaba de nuevo → duplicados (ej: una transferencia recibida aparecía 3 veces).
///
/// SOLUCIÓN: la clave de negocio es fecha+descripción+débito+crédito (sin saldo). Para no
/// perder movimientos legítimamente idénticos el mismo día (misma fecha+desc+importe, que
/// el banco lista como filas separadas), se dedupe por CANTIDAD: se cuentan cuántas copias
/// de esa clave ya existen y cuántas trae el archivo, y solo se agregan las que faltan.
/// El número de ocurrencia (1-based) mantiene único el hash (la tabla tiene índice único
/// en HashUnico) sin depender del saldo.
/// </summary>
public static class ExtractoDedup
{
    /// <summary>Clave de negocio de un movimiento (NO incluye Saldo a propósito).
    /// Los importes se normalizan a 2 decimales con cultura invariante: un importe vacío
    /// leído del archivo llega como 0 (sin decimales) pero en la DB está como 0.00, y sin
    /// normalizar "0" != "0.00" rompía el match → se re-importaba TODO como nuevo.</summary>
    public static string Clave(DateTime fecha, string? descripcion, decimal debitos, decimal creditos)
        => string.Format(CultureInfo.InvariantCulture, "{0:yyyyMMdd}|{1}|{2:0.00}|{3:0.00}",
            fecha, (descripcion ?? "").Trim(), debitos, creditos);

    /// <summary>Hash único de la N-ésima ocurrencia de una clave (1-based).</summary>
    public static string Hash(string clave, int ocurrencia)
    {
        using var sha = SHA256.Create();
        var raw = $"{clave}|#{ocurrencia}";
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw))).Substring(0, 32);
    }
}
