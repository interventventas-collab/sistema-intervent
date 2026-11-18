using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 18/11/2026 — Almanaque fijo de vencimientos de ARCA y Convenio Multilateral para PALANICA SRL
/// (CUIT 30-71721214-9 → terminación 9). Pedido del dueño: que la tarjeta Impuestos avise antes de
/// que venza y diga si la contadora ya mandó el VEP.
/// Fechas sacadas de los calendarios publicados (gestionyeficiencia.com, geniocontable.com,
/// calendariofiscal.com.ar) el 18/11/2026; CM de octubre movido al 20/11 por RG CA 11/2026.
/// ARCA publica el año siguiente en diciembre: cuando se acaben las fechas, cargar 2027 acá.
/// Concepto = el mismo que usa ImpuestosService.Normalizar para los VEP (así se cruzan).
/// </summary>
public static class VencimientosArca
{
    public record Venc(string Concepto, string Nombre, string Periodo, DateTime Vence);

    public static readonly List<Venc> Almanaque = new()
    {
        new("Aportes empleados", "Cargas sociales (F.931)", "2026-10", new DateTime(2026, 11, 13)),
        new("Ganancias", "Anticipo de Ganancias", "2026-11", new DateTime(2026, 11, 17)),
        new("Ingresos brutos", "Ingresos brutos (Convenio)", "2026-10", new DateTime(2026, 11, 20)),
        new("IVA", "IVA", "2026-10", new DateTime(2026, 11, 25)),
        new("Aportes empleados", "Cargas sociales (F.931)", "2026-11", new DateTime(2026, 12, 11)),
        new("Ganancias", "Anticipo de Ganancias", "2026-12", new DateTime(2026, 12, 16)),
        new("Ingresos brutos", "Ingresos brutos (Convenio)", "2026-11", new DateTime(2026, 12, 18)),
        new("IVA", "IVA", "2026-11", new DateTime(2026, 12, 24)),
        new("Ingresos brutos", "Ingresos brutos (Convenio)", "2026-12", new DateTime(2027, 1, 20)),
    };

    public static DateTime CargadoHasta => Almanaque.Max(v => v.Vence);

    /// <summary>Estado: PAGADO (VEP pagado) · BANCO (débito AFIP sin nombre ese día) · VEP (llegó, sin pagar) · SIN_VEP.</summary>
    public record VencEstado(string Concepto, string Nombre, string Periodo, DateTime Vence, string Estado,
        decimal? Importe, DateTime? PagadoFecha, int? VepId);

    public static async Task<List<VencEstado>> EvaluarAsync(AppDbContext db, DateTime desde, DateTime hasta)
    {
        // Se evalúa desde 15 días antes del rango pedido para que los débitos del banco se repartan siempre igual.
        var desdeEval = desde == DateTime.MinValue ? desde : desde.AddDays(-15);
        var lista = Almanaque.Where(v => v.Vence >= desdeEval && v.Vence <= hasta).OrderBy(v => v.Vence).ToList();
        if (lista.Count == 0) return new();
        var min = lista.Min(v => v.Vence).AddDays(-40);
        var max = lista.Max(v => v.Vence).AddDays(10);
        var veps = await db.ImpuestosVeps.AsNoTracking()
            .Where(v => v.Estado != "DESCARTADO" && (v.Entidad == "SRL" || v.Entidad == null)
                && ((v.MailFecha != null && v.MailFecha >= min && v.MailFecha <= max)
                    || (v.PagadoFecha != null && v.PagadoFecha >= min && v.PagadoFecha <= max)
                    || (v.Vence != null && v.Vence >= min && v.Vence <= max)))
            .Select(v => new { v.Id, v.Concepto, v.Periodo, v.Importe, v.Estado, v.MailFecha, v.PagadoFecha, v.Vence, v.Origen })
            .ToListAsync();

        var res = new List<VencEstado>();
        var hoy = ImpuestosService.HoyAr;
        var usados = new HashSet<int>();
        foreach (var v in lista)
        {
            bool Cerca(DateTime? d, int antes, int despues) => d.HasValue && d.Value.Date >= v.Vence.AddDays(-antes) && d.Value.Date <= v.Vence.AddDays(despues);
            // Un VEP es "de este vencimiento" si dice el mismo período, o si (sin período) se pagó / llegó / vence
            // cerca: pagado hasta 12 días antes y 5 después; sin pagar, llegado hasta 12 días antes.
            // (18/11: con 30 días tomaba el IIBB de septiembre pagado el 21/10 como el de octubre.) Cada VEP, una sola vez.
            var mios = veps.Where(x => x.Concepto == v.Concepto && x.Origen != "BANCO" && !usados.Contains(x.Id)
                && (x.Periodo == v.Periodo || (x.Periodo == null && (x.Estado == "PAGADO"
                    ? Cerca(x.PagadoFecha, 12, 5)
                    : Cerca(x.Vence, 5, 5) || Cerca(x.MailFecha?.AddHours(-3), 12, 2)))))
                .ToList();
            foreach (var m in mios) usados.Add(m.Id);
            var pag = mios.Where(x => x.Estado == "PAGADO").OrderByDescending(x => x.PagadoFecha).FirstOrDefault();
            if (pag is not null) { res.Add(new(v.Concepto, v.Nombre, v.Periodo, v.Vence, "PAGADO", mios.Where(x => x.Estado == "PAGADO").Sum(x => x.Importe), pag.PagadoFecha, pag.Id)); continue; }
            var pend = mios.FirstOrDefault(x => x.Estado == "PENDIENTE");
            if (pend is not null) { res.Add(new(v.Concepto, v.Nombre, v.Periodo, v.Vence, "VEP", mios.Where(x => x.Estado == "PENDIENTE").Sum(x => x.Importe), null, pend.Id)); continue; }
            // El banco solo dice "IMP.AFIP": si ya venció (u hoy) y hubo un débito así entre 3 días antes y 5
            // después, probablemente es éste. Cada débito se usa para un solo vencimiento.
            var banco = v.Vence.Date > hoy ? null : veps.Where(x => x.Origen == "BANCO" && x.Estado == "PAGADO"
                && !usados.Contains(x.Id) && Cerca(x.PagadoFecha, 3, 5)).OrderBy(x => x.PagadoFecha).FirstOrDefault();
            if (banco is not null)
            {
                usados.Add(banco.Id);
                res.Add(new(v.Concepto, v.Nombre, v.Periodo, v.Vence, "BANCO", banco.Importe, banco.PagadoFecha, banco.Id));
                continue;
            }
            res.Add(new(v.Concepto, v.Nombre, v.Periodo, v.Vence, "SIN_VEP", null, null, null));
        }
        return res.Where(r => r.Vence >= desde).ToList();
    }
}
