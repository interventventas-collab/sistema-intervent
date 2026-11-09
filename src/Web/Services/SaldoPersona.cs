using System.Globalization;

namespace Web.Services;

/// <summary>
/// 09/11/2026 — Cómo se nombra el saldo entre la empresa y un empleado, igual en todas las pantallas.
///
/// Antes cada pantalla lo decía desde su lado ("le debemos", "te debemos", "nos debe") y en
/// Cobranzas salía "le debemos -$396.864" cuando en realidad el empleado había cobrado de más.
/// "A favor de Walter" / "A favor de Palanica" significa lo mismo lo lea quien lo lea.
///
/// Saldo positivo = la empresa le debe al empleado. Negativo = cobró de más.
/// </summary>
public static class SaldoPersona
{
    private const string Empresa = "Palanica";
    private static readonly CultureInfo Ar = CultureInfo.GetCultureInfo("es-AR");

    public static string AFavor(string? nombre, decimal saldo) =>
        saldo < 0 ? $"A favor de {Empresa}" : $"A favor de {PrimerNombre(nombre)}";

    /// <summary>
    /// El pedacito que va atrás de "Viajes" o "Sueldo": " · a favor de Walter $1.500.000".
    /// Con saldo cero no agrega nada: la sesión con huella recibe los montos en cero a propósito
    /// y no hay que hacerle creer que esa persona no tiene saldo.
    /// </summary>
    public static string Detalle(string? nombre, decimal saldo)
    {
        if (saldo == 0) return "";
        var quien = AFavor(nombre, saldo);
        return $" · a{quien[1..]} ${Math.Abs(saldo).ToString("N0", Ar)}";
    }

    private static string PrimerNombre(string? nombre)
    {
        var primero = (nombre ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrEmpty(primero) ? "el empleado" : Ar.TextInfo.ToTitleCase(primero.ToLower(Ar));
    }
}
