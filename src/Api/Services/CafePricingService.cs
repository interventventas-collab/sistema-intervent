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
    public const string FORMATO_BULTO = "BULTO";  // SOLO OTROS: vende 1 bulto = UxB unidades, precio = PrecioBulto/Otro

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
        FORMATO_BULTO => "bulto",
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
        decimal descuentoPct = 0m, DateTime? fechaPara = null)
    {
        // Modelo UNIFICADO (post 2026-05-12): se eliminaron los descuentos automaticos por matriz.
        // Tanto CAFE como OTROS usan PrecioBar / PrecioOtro como precios directos:
        //   - Cliente BAR:  PrecioBar  (fallback PrecioOtro si BAR esta vacio)
        //   - Cliente OTRO: PrecioOtro (fallback PrecioBar si OTRO esta vacio)
        //   - Si ninguno esta cargado: cae a Pvp1 / Pvp2 / PrecioPorKg (legacy, sin descuento).
        // El parametro descuentoPct ahora solo refleja el descuento MANUAL de linea (no la matriz).
        // fechaPara: si se pasa, decide entre Actual y Futuro evaluando contra esa fecha en vez
        // de DateTime.Today. Usado para previsualizar la lista de precios "vigentes desde X".
        var desc = Math.Max(0m, Math.Min(100m, descuentoPct));
        decimal lista, final;

        // Determinar si usamos precios futuros para esta evaluacion.
        bool usaFuturos;
        if (fechaPara.HasValue)
        {
            usaFuturos = producto.FechaAplicaPreciosFuturos.HasValue
                && fechaPara.Value.Date >= producto.FechaAplicaPreciosFuturos.Value.Date
                && (producto.PrecioPorKgFuturo.HasValue || producto.PrecioBarFuturo.HasValue
                    || producto.PrecioOtroFuturo.HasValue || producto.PrecioBultoFuturo.HasValue
                    || producto.PrecioBultoOtroFuturo.HasValue);
        }
        else
        {
            usaFuturos = producto.UsaPreciosFuturos;
        }

        decimal? precioBarEf = usaFuturos && producto.PrecioBarFuturo.HasValue ? producto.PrecioBarFuturo : producto.PrecioBar;
        decimal? precioOtroEf = usaFuturos && producto.PrecioOtroFuturo.HasValue ? producto.PrecioOtroFuturo : producto.PrecioOtro;
        decimal? precioBultoEf = usaFuturos && producto.PrecioBultoFuturo.HasValue ? producto.PrecioBultoFuturo : producto.PrecioBulto;
        decimal? precioBultoOtroEf = usaFuturos && producto.PrecioBultoOtroFuturo.HasValue ? producto.PrecioBultoOtroFuturo : producto.PrecioBultoOtro;
        decimal? precioPorKgEf = usaFuturos && producto.PrecioPorKgFuturo.HasValue ? producto.PrecioPorKgFuturo : producto.PrecioPorKg;

        // Elegir el precio directo segun el tipo de cliente.
        // Si el formato es BULTO (solo OTROS), usamos PrecioBulto* como precio del bulto.
        decimal? precioDirecto;
        if (formato == FORMATO_BULTO)
        {
            if (tipoCliente == TIPO_BAR)
                precioDirecto = precioBultoEf ?? precioBultoOtroEf;
            else
                precioDirecto = precioBultoOtroEf ?? precioBultoEf;
        }
        else
        {
            if (tipoCliente == TIPO_BAR)
                precioDirecto = precioBarEf ?? precioOtroEf;
            else
                precioDirecto = precioOtroEf ?? precioBarEf;
        }

        // Fallback legacy si no hay precios directos (productos que nunca se configuraron).
        var listaBase = precioDirecto ?? producto.Pvp1 ?? producto.Pvp2 ?? precioPorKgEf ?? 0m;

        if (producto.Categoria == "CAFE")
        {
            // Costo de fraccionamiento efectivo: si hay un "futuro" cargado en settings y la
            // fecha de evaluacion ya lo alcanzo, usamos ese; sino el actual.
            DateTime fechaEvalFracc = fechaPara ?? DateTime.Today;
            bool usaFraccFuturo = settings.CostoFraccionamientoFuturo.HasValue
                && settings.FechaAplicaFraccionamientoFuturo.HasValue
                && fechaEvalFracc.Date >= settings.FechaAplicaFraccionamientoFuturo.Value.Date;
            decimal costoFracc = usaFraccFuturo
                ? settings.CostoFraccionamientoFuturo!.Value
                : settings.CostoFraccionamiento;

            // CAFE: el precio cargado es POR KG. Fracciones se calculan + costo fraccionamiento.
            lista = formato switch
            {
                FORMATO_1KG => listaBase,
                FORMATO_MEDIO => (listaBase / 2m) + costoFracc,
                FORMATO_CUARTO => (listaBase / 4m) + costoFracc,
                _ => 0m
            };
            // Redondeo HACIA ARRIBA al multiplo de RedondeoMultiplo (default $1000).
            // - Si estamos en modo "fraccionamiento futuro": aplica a TODAS las fracciones
            //   (1/2 y 1/4) y a TODOS los tipos (BAR y OTRO).
            // - Si estamos en modo actual (legacy): solo 1/2 OTRO, como ajuste temporal del
            //   14/05/2026 para que coincida con la lista FV impresa.
            bool esFraccion = formato == FORMATO_MEDIO || formato == FORMATO_CUARTO;
            if (settings.RedondeoMultiplo > 0 && esFraccion)
            {
                bool aplicarRedondeo;
                if (usaFraccFuturo)
                    aplicarRedondeo = true;
                else
                    aplicarRedondeo = formato == FORMATO_MEDIO && tipoCliente?.ToUpperInvariant() == "OTRO";

                if (aplicarRedondeo)
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

    /// <summary>Subtotal de linea: cantidad × precioUnitFinal, redondeado.
    ///
    /// Antes (descartado el 2026-05-14): este metodo aplicaba un descuento automatico por bulto
    /// cuando cantidad >= UxB. El usuario lo vio en un ticket impreso ("1000 × $40 = $35.000") y dijo
    /// "queda sin lógica" — no entendia por que la math no cerraba. Decision: pasar al modelo
    /// "Unidad de Medida" estandar de mayoristas: el operador elige explicitamente Suelto vs Bulto
    /// desde el dropdown de formato en la venta. CalcularPrecioBreakdown se encarga de usar el
    /// PrecioBulto cuando formato=BULTO. Asi 1 linea = 1 formato, sin magia oculta.
    /// </summary>
    public static decimal CalcularSubtotalConBulto(CafeProducto prod, string tipoCliente, decimal precioUnitFinal, decimal cantidad)
    {
        return Math.Round(precioUnitFinal * cantidad, 2, MidpointRounding.AwayFromZero);
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
        // OTROS: si es BULTO, 1 "unidad cargada" = UxB unidades reales, asi que el costo unitario
        // de la linea es costo_unitario × UxB. Sino, costo unitario tal cual.
        if (formato == FORMATO_BULTO && producto.UxB.HasValue && producto.UxB.Value > 0)
        {
            return Math.Round(producto.Costo * producto.UxB.Value, 2, MidpointRounding.AwayFromZero);
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
