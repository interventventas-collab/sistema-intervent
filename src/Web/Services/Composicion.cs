using System.Text.RegularExpressions;
using Web.Models;

namespace Web.Services;

/// <summary>
/// 2026-09-28 — Pedido del dueño: los productos que son "tacho + tapa" o "caja + tapa" muestran al final
/// del nombre, entre paréntesis, los códigos que los componen: "(C2917NEG + C7008NEG)", "(4× 9242TR + 4× 7046-AZ)".
/// Solo se MUESTRA: el nombre guardado no cambia (facturas, PDF y MeLi siguen igual).
/// Los productos "shell" traen el texto armado desde la API (campo Composicion).
/// </summary>
public static class Composicion
{
    /// <summary>"(A + B)" de un compuesto a partir de sus items. Vacío si no tiene items.</summary>
    public static string DeCompuesto(CafeComboDto c)
    {
        if (c.Items is null || c.Items.Count == 0) return "";
        return "(" + string.Join(" + ", c.Items.OrderBy(i => i.SortOrder).Select(i =>
            (i.Cantidad == 1 ? "" : $"{i.Cantidad}× ") + (string.IsNullOrWhiteSpace(i.ProductoSku) ? i.ProductoNombre : i.ProductoSku))) + ")";
    }

    private static readonly Regex SkuPack = new(@"-X\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// ¿Es un PACK (varios compuestos iguales juntos: X2, X4, X6…) y no un compuesto de 1 tacho + 1 tapa?
    /// Pack = el código termina en "-X{n}", o todas las piezas vienen repetidas un múltiplo común
    /// (4 cajas + 4 tapas). "Mesa + 4 sillas" (1 y 4) NO es pack.
    /// </summary>
    public static bool EsPack(CafeComboDto c)
    {
        if (!string.IsNullOrWhiteSpace(c.Sku) && SkuPack.IsMatch(c.Sku.Trim())) return true;
        if (c.Items is null || c.Items.Count == 0) return false;
        int mcd = 0;
        foreach (var i in c.Items) mcd = Mcd(mcd, Math.Abs(i.Cantidad));
        return mcd > 1;
    }

    private static int Mcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }
}
