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
/// Para OTROS (PVP por producto):
///   OTRO  -> producto.Pvp2 (precio fijo a mano, obligatorio)
///   BAR   -> si producto.BarPctSobreCosto != null: costo * (1 + BarPctSobreCosto/100)
///            si null: cae al PVP (Pvp2)
///   -> SIN redondeo (el PVP a mano es el numero literal que el usuario escribio).
///   Settings.MargenOtros* quedaron en desuso y se ignoran. Se respetan solo si
///   el producto OTROS no tiene Pvp2 cargado (defensivo, no deberia pasar luego de la migracion).
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

    /// <summary>
    /// Desglose de precio: lista (sin descuento), descuento %, precio final.
    ///
    /// Modelo unificado (CAFE y OTROS):
    ///   1. Lista = Pvp1 del producto (la "lista 100%"). Para fracciones de cafe, se calcula
    ///      sobre Pvp1/2 + costo fraccionamiento, redondeado.
    ///   2. Descuento = se busca en la matriz Cafe_ReglasPrecios por (tipo cliente, categoria producto, marcaId)
    ///      con jerarquia: override por marca > regla general por categoria. La marca con
    ///      BloqueaDescuento prendido fuerza descuento = 0.
    ///   3. Precio final = lista × (1 − desc/100).
    ///
    /// Pvp2 (sugerido proveedor) se conserva en el producto solo como referencia interna
    /// — NO se usa en el motor.
    /// </summary>
    public record PrecioBreakdown(decimal PrecioLista, decimal DescuentoPct, decimal PrecioFinal);

    public static PrecioBreakdown CalcularPrecioBreakdown(
        CafeProducto producto, string formato, string tipoCliente, CafeSetting settings,
        decimal descuentoPct = 0m)
    {
        // Modelo UNIFICADO (post 2026-05-12): se eliminaron los descuentos automaticos por matriz.
        // Tanto CAFE como OTROS usan PrecioBar / PrecioOtro como precios directos:
        //   - Cliente BAR:  PrecioBar  (fallback PrecioOtro si BAR esta vacio)
        //   - Cliente OTRO: PrecioOtro (fallback PrecioBar si OTRO esta vacio)
        //   - Si ninguno esta cargado: cae a Pvp1 / Pvp2 / PrecioPorKg (legacy, sin descuento).
        // El parametro descuentoPct ahora solo refleja el descuento MANUAL de linea (no la matriz).
        var desc = Math.Max(0m, Math.Min(100m, descuentoPct));
        decimal lista, final;

        // Elegir el precio directo segun el tipo de cliente
        decimal? precioDirecto;
        if (tipoCliente == TIPO_BAR)
            precioDirecto = producto.PrecioBar ?? producto.PrecioOtro;
        else
            precioDirecto = producto.PrecioOtro ?? producto.PrecioBar;

        // Fallback legacy si no hay precios directos (productos que nunca se configuraron)
        var listaBase = precioDirecto ?? producto.Pvp1 ?? producto.Pvp2 ?? producto.PrecioPorKg ?? 0m;

        if (producto.Categoria == "CAFE")
        {
            // CAFE: el precio cargado es POR KG. Fracciones se calculan + costo fraccionamiento.
            lista = formato switch
            {
                FORMATO_1KG => listaBase,
                FORMATO_MEDIO => (listaBase / 2m) + settings.CostoFraccionamiento,
                FORMATO_CUARTO => (listaBase / 4m) + settings.CostoFraccionamiento,
                _ => 0m
            };
            // AJUSTE TEMPORAL (pedido del usuario el 14/05/2026): el 1/2 KG para clientes OTRO
            // se redondea HACIA ARRIBA al múltiplo de RedondeoMultiplo (default $1000) para que
            // coincida exacto con la lista de precios FV (sugerida) que tienen impresa los clientes.
            // El mes que viene se recalculan los costos correctos y se quita este redondeo.
            if (formato == FORMATO_MEDIO && tipoCliente?.ToUpperInvariant() == "OTRO" && settings.RedondeoMultiplo > 0)
            {
                lista = Math.Ceiling(lista / settings.RedondeoMultiplo) * settings.RedondeoMultiplo;
            }
        }
        else
        {
            // OTROS: el precio cargado es directo (sin fraccionamiento).
            lista = listaBase;
        }

        lista = Math.Round(lista, 2, MidpointRounding.AwayFromZero);
        final = Math.Round(lista * (1m - desc / 100m), 2, MidpointRounding.AwayFromZero);
        return new PrecioBreakdown(lista, desc, final);
    }

    /// <summary>Calcula el precio unitario final (compat con codigo existente).</summary>
    public static decimal CalcularPrecioUnitario(CafeProducto producto, string formato, string tipoCliente, CafeSetting settings)
        => CalcularPrecioBreakdown(producto, formato, tipoCliente, settings).PrecioFinal;

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
