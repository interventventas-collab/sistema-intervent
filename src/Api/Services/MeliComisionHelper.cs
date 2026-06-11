namespace Api.Services;

/// <summary>
/// 2026-06-11: Tabla maestra de comisiones de financiación de MeLi (Opción C — tabla fija).
/// Basada en datos relevados del Excel de Integraly + Simulador de costos de MeLi.
/// Si MeLi cambia las tarifas, actualizar acá.
///
/// La comisión TOTAL que cobra MeLi por venta = Comisión por Categoría (13% galletitas, varía)
///                                              + Comisión por Financiación (depende del tipo de cuotas)
///                                              + Cargo Fijo (~$2.500 si precio menor a $30k)
/// </summary>
public static class MeliComisionHelper
{
    /// <summary>2026-06-11: Cargo fijo que MeLi cobra cuando el precio es menor al umbral.
    /// Confirmado contra el simulador oficial: $22.525 → cargo fijo $2.505. Ajustar si MeLi cambia.</summary>
    public static decimal GetCargoFijo(decimal precioMeLi)
    {
        // Umbral conocido: si precio < $30.000 ARS, cobra cargo fijo. Si >= $30.000, $0.
        if (precioMeLi < 30000m) return 2500m;
        return 0m;
    }
    /// <summary>Porcentaje extra que MeLi cobra según el tipo de cuotas del InstallmentTag.</summary>
    public static decimal GetFinanciacionPct(string? installmentTag)
    {
        if (string.IsNullOrWhiteSpace(installmentTag)) return 0m;
        var lower = installmentTag.ToLowerInvariant();

        // 12 cuotas
        if (lower.Contains("12x_campaign")) return 19.2m;
        // 9 cuotas
        if (lower.Contains("9x_campaign")) return 15.7m;
        // 6 cuotas
        if (lower.Contains("6x_campaign")) return 12.3m;
        // 3 cuotas
        if (lower.Contains("3x_campaign")) return 8.4m;
        // Programa cuotas con interés bajo (3 a 12)
        if (lower.Contains("pcj-co-funded") || lower.Contains("co-funded")) return 5m;

        return 0m;
    }

    /// <summary>Label legible del esquema de cuotas para mostrar en UI.</summary>
    public static string GetInstallmentLabel(string? installmentTag)
    {
        if (string.IsNullOrWhiteSpace(installmentTag)) return "Sin cuotas";
        var lower = installmentTag.ToLowerInvariant();

        if (lower.Contains("12x_campaign")) return "12 al mismo precio";
        if (lower.Contains("9x_campaign")) return "9 al mismo precio";
        if (lower.Contains("6x_campaign")) return "6 al mismo precio";
        if (lower.Contains("3x_campaign")) return "3 al mismo precio";
        if (lower.Contains("pcj-co-funded") || lower.Contains("co-funded")) return "3 a 12 con interés bajo";

        return "Sin cuotas";
    }

    /// <summary>Label legible del tipo de publicación.</summary>
    public static string GetListingTypeLabel(string? listingTypeId)
    {
        if (string.IsNullOrWhiteSpace(listingTypeId)) return "?";
        return listingTypeId.ToLowerInvariant() switch
        {
            "gold_pro" => "Premium",
            "gold_special" => "Clásica",
            "gold_premium" => "Oro Premium",
            "silver" => "Plata",
            "bronze" => "Bronce",
            "free" => "Gratuita",
            _ => listingTypeId
        };
    }

    public record AnalisisMargenMlaItem(
        string MeliItemId,
        string ListingType,
        string ListingTypeLabel,
        string? InstallmentTag,
        string InstallmentLabel,
        bool FreeShipping,
        decimal PrecioMeLi,
        decimal ComisionCategoriaPct,
        decimal ComisionFinanciacionPct,
        decimal ComisionTotalPct,
        decimal ComisionMonto,
        decimal CargoFijo,
        decimal ShippingCostoEstimado,
        decimal Neto,
        decimal? PrecioFactor,
        bool PrecioIndependiente);

    /// <summary>Calcula el neto que le queda al vendedor para una MLA dada.</summary>
    public static AnalisisMargenMlaItem CalcularMla(
        string mlaId,
        string? listingType,
        string? installmentTag,
        bool freeShipping,
        decimal precioMeLi,
        decimal comisionCategoriaPct,
        decimal? precioFactor,
        bool precioIndependiente,
        decimal envioGratisCostoEstimado = 0m)
    {
        var financiacionPct = GetFinanciacionPct(installmentTag);
        var totalPct = comisionCategoriaPct + financiacionPct;
        var comisionMonto = System.Math.Round(precioMeLi * totalPct / 100m, 2);
        // 2026-06-11: cargo fijo de MeLi (cuando precio < $30k)
        var cargoFijo = GetCargoFijo(precioMeLi);
        // Si tiene envío gratis y nos pasaron un costo estimado, lo restamos del neto
        var shipping = freeShipping ? envioGratisCostoEstimado : 0m;

        var neto = System.Math.Round(precioMeLi - comisionMonto - cargoFijo - shipping, 2);

        return new AnalisisMargenMlaItem(
            MeliItemId: mlaId,
            ListingType: listingType ?? "",
            ListingTypeLabel: GetListingTypeLabel(listingType),
            InstallmentTag: installmentTag,
            InstallmentLabel: GetInstallmentLabel(installmentTag),
            FreeShipping: freeShipping,
            PrecioMeLi: precioMeLi,
            ComisionCategoriaPct: comisionCategoriaPct,
            ComisionFinanciacionPct: financiacionPct,
            ComisionTotalPct: totalPct,
            ComisionMonto: comisionMonto,
            CargoFijo: cargoFijo,
            ShippingCostoEstimado: shipping,
            Neto: neto,
            PrecioFactor: precioFactor,
            PrecioIndependiente: precioIndependiente);
    }
}
