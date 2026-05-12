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
        var desc = Math.Max(0m, Math.Min(100m, descuentoPct));
        decimal lista, final;

        if (producto.Categoria == "CAFE")
        {
            // CAFE:
            //   - lista_kg = Pvp1 (= "lista 100%")
            //   - lista_formato = lista_kg/N + costoFracc
            //   - final = lista_formato × (1 − desc/100)  (math limpia: el descuento aplica al total del formato)
            //   - SIN redondeo arriba — para que la matematica del comprobante cuadre exacto.
            var listaKg = producto.Pvp1 ?? producto.Pvp2 ?? producto.PrecioPorKg ?? 0m;

            lista = formato switch
            {
                FORMATO_1KG => listaKg,
                FORMATO_MEDIO => (listaKg / 2m) + settings.CostoFraccionamiento,
                FORMATO_CUARTO => (listaKg / 4m) + settings.CostoFraccionamiento,
                _ => 0m
            };
            lista = Math.Round(lista, 2, MidpointRounding.AwayFromZero);
            final = Math.Round(lista * (1m - desc / 100m), 2, MidpointRounding.AwayFromZero);
        }
        else
        {
            // OTROS — modelo NUEVO (2 precios directos según tipo cliente).
            //   - Cliente BAR: usa PrecioBar si está cargado; sino, FALLBACK a PrecioOtro
            //     (porque "el sugerido es el que cobramos a cualquier cliente por defecto").
            //   - Cliente OTRO: usa siempre PrecioOtro.
            // Si NI siquiera PrecioOtro está cargado, caemos al modelo LEGACY (Pvp1/Pvp2 + matriz).
            decimal? precioDirecto;
            if (tipoCliente == TIPO_BAR)
                precioDirecto = producto.PrecioBar ?? producto.PrecioOtro;
            else
                precioDirecto = producto.PrecioOtro;

            if (precioDirecto.HasValue)
            {
                // Modelo nuevo: el precio cargado es la "lista" directa para ese tipo de cliente.
                // El descuento de línea se aplica por encima si hay.
                lista = precioDirecto.Value;
                final = Math.Round(lista * (1m - desc / 100m), 2, MidpointRounding.AwayFromZero);
            }
            else
            {
                // Modelo legacy: Pvp1 (fallback Pvp2) + descuento de línea (la matriz se
                // aplica afuera, en CafeVentasController). Sin fraccionamiento.
                lista = producto.Pvp1 ?? producto.Pvp2 ?? 0m;
                final = Math.Round(lista * (1m - desc / 100m), 2, MidpointRounding.AwayFromZero);
            }
        }

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
