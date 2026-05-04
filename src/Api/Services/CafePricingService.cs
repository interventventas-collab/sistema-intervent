using Api.Models;

namespace Api.Services;

/// <summary>
/// Motor de precios del modulo Cafe.
///
/// Para CAFE:
///   precio_kg = (cliente.tipo == BAR) ? producto.pvp1 : producto.pvp2
///   1 kg     -> precio_kg
///   1/2 kg   -> (precio_kg / 2) + costoFraccionamiento
///   1/4 kg   -> (precio_kg / 4) + costoFraccionamiento
///   -> redondeo hacia arriba a multiplo configurable (default 1000)
///
/// Para OTROS:
///   margen_pct = (cliente.tipo == BAR) ? settings.MargenOtrosBarPct : settings.MargenOtrosNoBarPct
///   precio_unit = costo * (1 + margen_pct/100)  -> redondeado al multiplo
/// </summary>
public static class CafePricingService
{
    public const string FORMATO_1KG = "1KG";
    public const string FORMATO_MEDIO = "MEDIO";
    public const string FORMATO_CUARTO = "CUARTO";
    public const string FORMATO_UNIT = "UNIT";

    public const string TIPO_BAR = "BAR";
    public const string TIPO_OTRO = "OTRO";

    /// <summary>Gramos por unidad segun el formato. Solo aplica a CAFE.</summary>
    public static decimal GramosPorUnidad(string formato) => formato switch
    {
        FORMATO_1KG => 1000m,
        FORMATO_MEDIO => 500m,
        FORMATO_CUARTO => 250m,
        _ => 0m
    };

    /// <summary>Etiqueta legible del formato (para PDFs y UI).</summary>
    public static string FormatoLabel(string formato) => formato switch
    {
        FORMATO_1KG => "1 kg",
        FORMATO_MEDIO => "1/2 kg",
        FORMATO_CUARTO => "1/4 kg",
        FORMATO_UNIT => "u.",
        _ => formato
    };

    /// <summary>Calcula el precio unitario de un item segun el motor.</summary>
    public static decimal CalcularPrecioUnitario(CafeProducto producto, string formato, string tipoCliente, CafeSetting settings)
    {
        if (producto.Categoria == "CAFE")
        {
            // PVP/kg segun tipo de cliente. Si falta el PVP elegido, cae al otro como fallback.
            decimal pvpKg;
            if (tipoCliente == TIPO_BAR)
                pvpKg = producto.Pvp1 ?? producto.Pvp2 ?? producto.PrecioPorKg ?? 0m;
            else
                pvpKg = producto.Pvp2 ?? producto.Pvp1 ?? producto.PrecioPorKg ?? 0m;

            decimal precio = formato switch
            {
                FORMATO_1KG => pvpKg,
                FORMATO_MEDIO => (pvpKg / 2m) + settings.CostoFraccionamiento,
                FORMATO_CUARTO => (pvpKg / 4m) + settings.CostoFraccionamiento,
                _ => 0m
            };
            return RedondearArriba(precio, settings.RedondeoMultiplo);
        }
        else // OTROS
        {
            var margenPct = tipoCliente == TIPO_BAR
                ? settings.MargenOtrosBarPct
                : settings.MargenOtrosNoBarPct;
            var precio = producto.Costo * (1m + margenPct / 100m);
            return RedondearArriba(precio, settings.RedondeoMultiplo);
        }
    }

    /// <summary>Costo unitario que va al item (para calcular margen).</summary>
    public static decimal CalcularCostoUnitario(CafeProducto producto, string formato)
    {
        if (producto.Categoria == "CAFE")
        {
            // Costo por gramo * gramos del formato. Si el costo está expresado por kg.
            // Asumimos: producto.Costo es el costo POR KG del cafe.
            var gramos = GramosPorUnidad(formato);
            return Math.Round(producto.Costo * (gramos / 1000m), 2, MidpointRounding.AwayFromZero);
        }
        return producto.Costo;
    }

    /// <summary>Redondeo hacia arriba al múltiplo indicado. Si multiplo &lt;= 0, no redondea.</summary>
    public static decimal RedondearArriba(decimal valor, decimal multiplo)
    {
        if (multiplo <= 0m || valor <= 0m) return Math.Round(valor, 2, MidpointRounding.AwayFromZero);
        return Math.Ceiling(valor / multiplo) * multiplo;
    }

    /// <summary>Tipo de cliente que se aplica para una venta. Default: OTRO.</summary>
    public static string ResolverTipo(string? tipo)
    {
        if (string.IsNullOrWhiteSpace(tipo)) return TIPO_OTRO;
        var t = tipo.Trim().ToUpperInvariant();
        return (t == TIPO_BAR || t == TIPO_OTRO) ? t : TIPO_OTRO;
    }
}
