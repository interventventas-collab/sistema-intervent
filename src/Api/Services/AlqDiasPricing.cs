namespace Api.Services;

using Api.Data;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// 2026-09-10 — Alquiler por varios días.
///
/// La regla del negocio: el primer día se cobra entero y cada día extra vale un porcentaje del
/// precio de un día (por default 50%). No baja más: el segundo, el tercero y el décimo valen todos
/// lo mismo. Con un producto de $1.000 y 50%: 1 día $1.000 · 2 días $1.500 · 3 días $2.000.
///
/// El FLETE nunca se multiplica — se cobra una sola vez sean 1 o 5 días. En el presupuesto eso sale
/// solo porque el flete es un campo aparte; en la reserva el flete es un renglón más, y por eso cada
/// renglón lleva su propio tilde <c>MultiplicaPorDias</c>.
///
/// ⚠ Los días cobrados NO se deducen de las fechas de entrega y retiro: esas son logística nuestra
/// (cuándo conviene dejar y pasar a buscar el equipo) y son independientes de lo que se factura.
/// </summary>
public static class AlqDiasPricing
{
    /// <summary>Clave en AppSettings del porcentaje del día extra.</summary>
    public const string KeyPorcentaje = "alq.dias.porcentaje";

    /// <summary>Mitad de precio, que es como lo venían cobrando a mano.</summary>
    public const decimal PorcentajeDefault = 50m;

    /// <summary>Días válidos: nunca menos de 1 (un alquiler siempre es al menos un día).</summary>
    public static int Normalizar(int dias) => Math.Clamp(dias, 1, 365);

    /// <summary>El porcentaje siempre entre 0 y 100 — un 150% haría que el día extra salga más caro.</summary>
    public static decimal NormalizarPorcentaje(decimal pct) => Math.Clamp(pct, 0m, 100m);

    /// <summary>
    /// Por cuánto se multiplica el precio de un día. 1 día → 1. Con 50%: 2 días → 1,5 · 3 días → 2.
    /// </summary>
    public static decimal Factor(int dias, decimal porcentaje)
        => 1m + (Normalizar(dias) - 1) * (NormalizarPorcentaje(porcentaje) / 100m);

    /// <summary>Lee el porcentaje configurado. Si no está cargado o es basura, cae en 50%.</summary>
    public static async Task<decimal> PorcentajeAsync(AppDbContext db)
    {
        var s = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == KeyPorcentaje);
        if (s is null || string.IsNullOrWhiteSpace(s.Value)) return PorcentajeDefault;
        return decimal.TryParse(s.Value, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? NormalizarPorcentaje(v)
            : PorcentajeDefault;
    }

    /// <summary>"Alquiler por 3 días" / "Alquiler por 1 día" — el renglón que ve el cliente.</summary>
    public static string Leyenda(int dias)
    {
        var d = Normalizar(dias);
        return d == 1 ? "Alquiler por 1 día" : $"Alquiler por {d} días";
    }
}
