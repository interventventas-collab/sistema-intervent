using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-10-06: cuánto le falta pagar a una MÁQUINA FINANCIADA.
///
/// Desde hoy la cuota de la máquina se carga como una cobranza común (entra a una caja, sale recibo),
/// igual que las señas de alquiler. Lo pagado sale de DOS lados:
///   - los pagos viejos anotados a mano en Cafe_ComodatoPagos (no pasaron por ninguna caja; no se tocan),
///   - las imputaciones de cobranzas VIGENTES con ese ComodatoId.
/// Máquina en dólares: se suma ImporteUsd (los dólares aplicados); en pesos, Importe.
///
/// <see cref="CafeComodato.SaldoFinanciamiento"/> es LA verdad de "cuánto falta" y otras pantallas lo
/// leen directo de la base: hay que llamar a <see cref="RecalcularAsync(AppDbContext, IEnumerable{int})"/>
/// cada vez que cambia algo de esto (cobranza nueva o anulada, pago viejo borrado, precio editado).
/// </summary>
public static class CafeComodatoSaldoService
{
    /// <summary>Lo cobrado por cobranzas vigentes, por máquina, en la moneda de la máquina.</summary>
    public record Cobrado(decimal Monto, int Cantidad);

    public static async Task<Dictionary<int, Cobrado>> CobradoPorCobranzasAsync(AppDbContext db, IEnumerable<int> comodatoIds)
    {
        var ids = comodatoIds.Distinct().ToList();
        if (ids.Count == 0) return new();
        var filas = await db.CafeCobranzasComprobantes.AsNoTracking()
            .Where(x => x.ComodatoId != null && ids.Contains(x.ComodatoId.Value) && x.Cobranza!.Estado == "VIGENTE")
            .Select(x => new { Id = x.ComodatoId!.Value, x.Importe, x.ImporteUsd, Moneda = x.Comodato != null ? x.Comodato.Moneda : "ARS" })
            .ToListAsync();
        return filas.GroupBy(f => f.Id).ToDictionary(g => g.Key, g => new Cobrado(
            g.Sum(f => f.Moneda == "USD" ? (f.ImporteUsd ?? 0m) : f.Importe), g.Count()));
    }

    /// <summary>Lo pagado en total (viejos + cobranzas), en la moneda de la máquina.</summary>
    public static async Task<Dictionary<int, decimal>> PagadoAsync(AppDbContext db, IEnumerable<int> comodatoIds)
    {
        var ids = comodatoIds.Distinct().ToList();
        if (ids.Count == 0) return new();
        var viejos = await db.CafeComodatoPagos.AsNoTracking()
            .Where(p => ids.Contains(p.ComodatoId))
            .GroupBy(p => p.ComodatoId)
            .Select(g => new { Id = g.Key, Total = g.Sum(p => p.Importe) })
            .ToDictionaryAsync(x => x.Id, x => x.Total);
        var cobrado = await CobradoPorCobranzasAsync(db, ids);
        return ids.ToDictionary(id => id, id =>
            (viejos.TryGetValue(id, out var v) ? v : 0m) + (cobrado.TryGetValue(id, out var c) ? c.Monto : 0m));
    }

    public static Task RecalcularAsync(AppDbContext db, int comodatoId, bool tocarEstado = true)
        => RecalcularAsync(db, new[] { comodatoId }, tocarEstado);

    /// <summary>Vuelve a calcular SaldoFinanciamiento = PrecioVenta − pagado, y el Estado:
    /// PAGADA si llegó a 0 (con precio cargado), y de vuelta a EN_CLIENTE si dejó de estar en 0
    /// (por ejemplo, se anuló la cobranza que la terminaba de pagar). Guarda los cambios.
    /// tocarEstado = false: sólo el saldo (lo usa el Editar de la máquina, donde el estado lo elige el operador).</summary>
    public static async Task RecalcularAsync(AppDbContext db, IEnumerable<int> comodatoIds, bool tocarEstado = true)
    {
        var ids = comodatoIds.Where(i => i > 0).Distinct().ToList();
        if (ids.Count == 0) return;
        var maquinas = await db.CafeComodatos.Where(c => ids.Contains(c.Id) && c.Modalidad == "FINANCIADA").ToListAsync();
        if (maquinas.Count == 0) return;
        var pagado = await PagadoAsync(db, maquinas.Select(m => m.Id));
        foreach (var c in maquinas)
        {
            var precio = c.PrecioVenta ?? 0m;
            c.SaldoFinanciamiento = precio - (pagado.TryGetValue(c.Id, out var p) ? p : 0m);
            // Solo se marca PAGADA si tiene precio cargado (una máquina sin precio no se da por pagada
            // con el primer pago). Desde los otros estados (taller, devuelta, baja) no se cambia nada.
            if (tocarEstado)
            {
                if (c.SaldoFinanciamiento <= 0.01m && precio > 0m && c.Estado == "EN_CLIENTE") c.Estado = "PAGADA";
                else if (c.Estado == "PAGADA" && c.SaldoFinanciamiento > 0.01m) c.Estado = "EN_CLIENTE";
            }
            c.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
    }

    /// <summary>"Máquina Marca Modelo" para recibos y listas.</summary>
    public static string Nombre(string? marca, string? modelo)
    {
        var mm = string.Join(" ", new[] { marca?.Trim(), modelo?.Trim() }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return string.IsNullOrWhiteSpace(mm) ? "Máquina" : $"Máquina {mm}";
    }
}
