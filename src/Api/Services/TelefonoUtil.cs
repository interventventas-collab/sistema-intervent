namespace Api.Services;

/// <summary>
/// 2026-07-17: MercadoLibre devuelve el telefono del comprador OFUSCADO (ej "XXXXXXX", sin ningun
/// digito) mientras no lo libera (el envio todavia no avanzo). Recien cuando el envio esta en el
/// momento justo manda el numero real. Esta funcion distingue un telefono REAL de uno tapado:
/// consideramos real solo si tiene al menos 6 digitos.
/// </summary>
public static class TelefonoUtil
{
    public static bool EsReal(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return false;
        return phone.Count(char.IsDigit) >= 6;
    }
}
