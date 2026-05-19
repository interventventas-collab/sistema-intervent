using Api.Models;

namespace Api.Services;

/// <summary>
/// Helper centralizado para calcular el "monto cobrable" de una venta de Café.
///
/// El campo CafeVenta.Total guarda el NETO sin IVA (asi se diseno el cotizador).
/// Para facturas A/B/C con IVA discriminado, el monto real que tiene que pagar el
/// cliente es el TOTAL CON IVA — guardado en CafeVenta.ArcaImpTotal.
///
/// Bug encontrado el 2026-05-19: la pantalla de Cobranzas y todas las que muestran
/// "lo que debe el cliente" estaban usando v.Total directo, por lo que el saldo
/// quedaba sin el IVA (el cliente "debia menos" de lo real, $31k menos en una
/// factura de $179k por ejemplo).
///
/// Fix: usar este helper en TODOS los lugares donde se compute saldo, marcado de
/// pagada, o monto que el cliente debe pagar. No tocamos CafeVenta.Total para no
/// romper el calculo del cotizador (subtotal - descuento = total neto), pero
/// MontoCobrable() siempre devuelve el numero "verdadero" que ve el cliente.
///
/// Importante: este cambio NO afecta a ARCA ni a la facturacion oficial. ARCA ya
/// tiene los importes correctos guardados en sus propios campos. Esto solo
/// corrige lo que el sistema le muestra al usuario sobre saldos de clientes.
/// </summary>
public static class CafeVentaSaldoHelper
{
    /// <summary>Monto que el cliente realmente debe pagar por esta venta. Para
    /// facturas A/B/C con CAE de ARCA, devuelve el total con IVA (ArcaImpTotal).
    /// Para cotizaciones tipo X, proformas y facturas sin ARCA, devuelve Total
    /// (que en esos casos ya incluye todo lo cobrable porque no hay IVA discriminado).</summary>
    public static decimal MontoCobrable(this CafeVenta v)
    {
        // Si ARCA registro un importe total (factura electronica con CAE), ese es el real.
        if (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m)
            return v.ArcaImpTotal.Value;
        // Fallback: el Total guardado por el cotizador. Para tipo X y proforma esto es
        // correcto (no hay IVA discriminado). Para facturas viejas sin ArcaImpTotal
        // tambien funciona como aproximacion (el cliente lo paga al neto que ve en pantalla).
        return v.Total;
    }
}
