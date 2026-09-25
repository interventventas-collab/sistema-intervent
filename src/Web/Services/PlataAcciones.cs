using System.Globalization;
using Web.Models;

namespace Web.Services;

/// <summary>
/// 2026-09-25: a dónde lleva cada renglón de "Plata que entró". Siempre a la pantalla de siempre
/// (Nueva cobranza, o el cobrar de Cheques) con todo lleno: la bolsita no carga nada por su cuenta.
/// La usan la bolsita de arriba y la pantalla grande, así las dos hacen exactamente lo mismo.
/// </summary>
public static class PlataAcciones
{
    private static readonly CultureInfo EsAr = new("es-AR");

    public static string Plata(decimal v)
        => "$" + v.ToString(v % 1 == 0 ? "N0" : "N2", EsAr);

    /// <summary>Texto del botón: los de repartidor se "vuelcan" (ya hay cobro), el resto se "cobra".</summary>
    public static string Boton(PlataItemDto i) => i.Tipo is "REPARTIDOR" or "ALQ_REPARTIDOR" ? "volcar" : "cobrar";

    /// <summary>null = hace falta elegir el cliente antes (transferencia sin sugerencia).</summary>
    public static string? Url(PlataItemDto i, int? clienteElegido = null)
    {
        var cli = clienteElegido ?? i.ClienteId;
        return i.Tipo switch
        {
            "REPARTIDOR" when cli.HasValue => $"/cafe/tesoreria/cobranzas?cliente={cli}&pendienteId={i.Id}",
            "ALQ_REPARTIDOR" when cli.HasValue => $"/cafe/tesoreria/cobranzas?cliente={cli}&alqPendienteId={i.Id}",
            "TRANSFERENCIA" when cli.HasValue => $"/cafe/tesoreria/cobranzas?cliente={cli}&movimiento={i.Id}"
                                                 + (i.VentaId.HasValue && clienteElegido is null ? $"&venta={i.VentaId}" : ""),
            "ECHEQ" => $"/cafe/tesoreria/cheques-todos?cobrar=B-{i.Id}",
            "CHEQUE" => $"/cafe/tesoreria/cheques-todos?cobrar=C-{i.Id}",
            _ => null
        };
    }

    public static string Cuando(PlataItemDto i)
    {
        var ar = i.Llego.TimeOfDay == TimeSpan.Zero ? i.Llego : i.Llego.AddHours(-3);
        var hoy = DateTime.UtcNow.AddHours(-3).Date;
        if (ar.Date == hoy) return i.Llego.TimeOfDay == TimeSpan.Zero ? "hoy" : $"hoy {ar:HH:mm}";
        if (ar.Date == hoy.AddDays(-1)) return "ayer";
        return ar.ToString("dd/MM");
    }

    /// <summary>Motivos para apartar una transferencia que no es un cobro (3, sin lista desplegable).</summary>
    public static readonly string[] MotivosNoEsCobro = { "entre cuentas propias", "devolución", "otro, no es de un cliente" };
}
