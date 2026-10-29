using System.Globalization;
using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-10-29: ventanita de bienvenida. Se arma con BLOQUES sueltos: cada bloque es una clase que
/// implementa <see cref="IBienvenidaBloque"/> y se registra en Program.cs. Para sumar algo nuevo a
/// la bienvenida: crear otra clase acá abajo + una línea en Program.cs. La pantalla dibuja sola los
/// bloques de tipo "renglones"; los tipos especiales ("recordatorios", "traer-bancos") tienen su
/// propio dibujo en BienvenidaVentana.razor.
///
/// "Persona" = el operador elegido con el PIN (OSMAR, GERMAN, GABRIEL…). Varios comparten el mismo
/// usuario de login, por eso todo se decide por la persona y no por el usuario.
/// </summary>
public interface IBienvenidaBloque
{
    string Clave { get; }
    /// <summary>Posición en la ventanita (más chico = más arriba).</summary>
    int Orden { get; }
    /// <summary>Null = este bloque no le toca a esta persona.</summary>
    Task<BienvenidaBloqueDto?> ArmarAsync(BienvenidaContexto ctx);
}

public record BienvenidaContexto(string Persona, DateTime HoyAr)
{
    /// <summary>Los de oficina ven plata (saldos, cheques). Depósito no.</summary>
    public bool VePlata => PersonasOficina.Contains(Persona);
    public static readonly string[] PersonasOficina = { "OSMAR", "GERMAN", "GABRIEL" };
}

/// <summary>Un renglón: texto principal + detalle en gris. Alarma = va en rojo. Link = "ver →".</summary>
public record BienvenidaRenglonDto(string Texto, string? Detalle = null, bool Alarma = false, string? Link = null);

/// <summary>Tipo: "renglones" (genérico) | "recordatorios" | "traer-bancos".</summary>
public record BienvenidaBloqueDto(string Clave, string Titulo, string Tipo, List<BienvenidaRenglonDto> Renglones);

public class BienvenidaService
{
    private readonly IEnumerable<IBienvenidaBloque> _bloques;
    private readonly ILogger<BienvenidaService> _logger;

    public BienvenidaService(IEnumerable<IBienvenidaBloque> bloques, ILogger<BienvenidaService> logger)
    {
        _bloques = bloques;
        _logger = logger;
    }

    public async Task<List<BienvenidaBloqueDto>> ArmarAsync(string persona)
    {
        var ctx = new BienvenidaContexto(persona, PanoramaService.AhoraAr().Date);
        var lista = new List<BienvenidaBloqueDto>();
        foreach (var b in _bloques.OrderBy(b => b.Orden))
        {
            // Un bloque que falla no tira abajo la ventanita entera.
            try
            {
                var dto = await b.ArmarAsync(ctx);
                if (dto is not null) lista.Add(dto);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "[Bienvenida] falló el bloque {Clave}", b.Clave); }
        }
        return lista;
    }

    public static string Plata(decimal n) => "$ " + n.ToString("N0", new CultureInfo("es-AR"));

    /// <summary>"hoy 09:12" / "ayer 19:40" / "27/10 08:00" en hora Argentina. Recibe UTC.</summary>
    public static string Cuando(DateTime utc, DateTime hoyAr)
    {
        var ar = utc.AddHours(-3);
        if (ar.Date == hoyAr) return $"hoy {ar:HH:mm}";
        if (ar.Date == hoyAr.AddDays(-1)) return $"ayer {ar:HH:mm}";
        return $"{ar:dd/MM HH:mm}";
    }
}

/// <summary>Botón para traer a mano Shell Flota + Galicia (movimientos y cheques). Pedido de Osmar
/// 29/10: no quiere que el robot entre solo (miedo a que el banco lo detecte y bloquee).</summary>
public class BienvenidaBloqueTraerBancos : IBienvenidaBloque
{
    /// <summary>A quién le aparece el botón. Para sumar a otro, agregarlo acá.</summary>
    private static readonly string[] Personas = { "OSMAR" };
    private readonly AppDbContext _db;
    public BienvenidaBloqueTraerBancos(AppDbContext db) => _db = db;

    public string Clave => "traer-bancos";
    public int Orden => 10;

    public async Task<BienvenidaBloqueDto?> ArmarAsync(BienvenidaContexto ctx)
    {
        if (!Personas.Contains(ctx.Persona)) return null;
        var renglones = new List<BienvenidaRenglonDto>();

        var shell = await _db.ShellAccounts.AsNoTracking().FirstOrDefaultAsync();
        if (shell is not null)
            renglones.Add(new("Shell Flota · saldo " + (shell.LastSaldo ?? "—"),
                shell.LastSaldoAt is DateTime s ? "leído " + BienvenidaService.Cuando(s, ctx.HoyAr) : "nunca leído",
                false, "/integraciones/shell"));

        var ult = await _db.CafeExtractoMovimientos.AsNoTracking()
            .OrderByDescending(m => m.Fecha).ThenByDescending(m => m.Id).FirstOrDefaultAsync();
        var ultImport = await _db.CafeExtractoMovimientos.MaxAsync(m => (DateTime?)m.ImportadoAt);
        if (ult is not null)
            renglones.Add(new("Galicia · saldo " + BienvenidaService.Plata(ult.Saldo),
                ultImport is DateTime g ? "traído " + BienvenidaService.Cuando(g, ctx.HoyAr) : null,
                false, "/cafe/tesoreria/extracto-banco"));

        return new BienvenidaBloqueDto(Clave, "Bancos", "traer-bancos", renglones);
    }
}

/// <summary>Lo del día: pedidos en marcha, cheques a pagar, saldo Shell y notas del calendario.</summary>
public class BienvenidaBloqueHoy : IBienvenidaBloque
{
    private readonly AppDbContext _db;
    public BienvenidaBloqueHoy(AppDbContext db) => _db = db;

    public string Clave => "hoy";
    public int Orden => 20;

    public async Task<BienvenidaBloqueDto?> ArmarAsync(BienvenidaContexto ctx)
    {
        var r = new List<BienvenidaRenglonDto>();
        var hoy = ctx.HoyAr;

        // Shell: Osmar ya lo ve en el bloque "Bancos".
        if (ctx.VePlata && ctx.Persona != "OSMAR")
        {
            var shell = await _db.ShellAccounts.AsNoTracking().FirstOrDefaultAsync();
            if (shell?.LastSaldo is string saldo)
                r.Add(new("Shell Flota · saldo " + saldo,
                    shell.LastSaldoAt is DateTime s ? "leído " + BienvenidaService.Cuando(s, hoy) : null,
                    false, "/integraciones/shell"));
        }

        // Pedidos en marcha: mismo criterio que el tablero de Preparación (últimos 7 días).
        var desde = DateTime.UtcNow.Date.AddDays(-7);
        var pedidos = await _db.CafeVentas.AsNoTracking()
            .Where(v => v.PreparacionOcultoAt == null
                && (v.EstadoPreparacion == "PARA_PREPARAR" || v.EstadoPreparacion == "EN_PREPARACION"
                    || v.EstadoPreparacion == "LISTO" || v.EstadoPreparacion == "EN_CAMINO")
                && v.Estado != "anulado"
                && (v.CreatedAt >= desde || (v.PreparacionUpdatedAt != null && v.PreparacionUpdatedAt >= desde)))
            .Select(v => new { v.EstadoPreparacion, v.EntregaPor })
            .ToListAsync();
        if (pedidos.Count > 0)
        {
            var partes = new List<string>();
            var paraArmar = pedidos.Count(p => p.EstadoPreparacion is "PARA_PREPARAR" or "EN_PREPARACION");
            if (paraArmar > 0) partes.Add($"{paraArmar} para armar");
            var sinRep = pedidos.Count(p => string.IsNullOrWhiteSpace(p.EntregaPor));
            if (sinRep > 0) partes.Add($"{sinRep} sin repartidor");
            var mios = pedidos.Count(p => p.EntregaPor != null
                && p.EntregaPor.Contains(ctx.Persona, StringComparison.OrdinalIgnoreCase));
            if (mios > 0) partes.Add($"{mios} los llevás vos");
            r.Add(new($"Pedidos por entregar · {pedidos.Count}", string.Join(" · ", partes), false, "/cafe/preparacion"));
        }

        if (ctx.VePlata)
        {
            var en7 = hoy.AddDays(7);
            var cheques = await _db.CafeChequesBanco.AsNoTracking()
                .Where(c => c.Tipo == "EMITIDO" && (c.Estado == "Aceptado" || c.Estado == "Disponible")
                    && c.FechaPago.HasValue && c.FechaPago.Value.Date >= hoy && c.FechaPago.Value.Date <= en7)
                .Select(c => new { Fecha = c.FechaPago!.Value, c.Importe, c.BeneficiarioActualNombre })
                .ToListAsync();
            var deHoy = cheques.Where(c => c.Fecha.Date == hoy).ToList();
            if (deHoy.Count > 0)
            {
                var quien = deHoy.Count == 1 && !string.IsNullOrWhiteSpace(deHoy[0].BeneficiarioActualNombre)
                    ? $"cheque {deHoy[0].BeneficiarioActualNombre} " : $"{deHoy.Count} cheques ";
                r.Add(new("A pagar hoy · " + quien + BienvenidaService.Plata(deHoy.Sum(c => c.Importe)),
                    null, true, "/cafe/tesoreria/cheques-todos"));
            }
            var proximos = cheques.Where(c => c.Fecha.Date > hoy).ToList();
            if (proximos.Count > 0)
                r.Add(new($"Cheques próximos 7 días · {proximos.Count}",
                    BienvenidaService.Plata(proximos.Sum(c => c.Importe)) + " · el primero el " +
                    proximos.Min(c => c.Fecha).ToString("dd/MM"),
                    false, "/cafe/tesoreria/cheques-todos"));
        }

        // Lo anotado a mano en el calendario del Dashboard para hoy.
        if (ctx.VePlata)
        {
            var notas = await _db.CafeCalendarioNotas.AsNoTracking()
                .Where(n => n.Fecha == hoy).OrderBy(n => n.Id).ToListAsync();
            foreach (var n in notas)
                r.Add(new("Calendario · " + n.Titulo,
                    n.Importe is decimal imp ? BienvenidaService.Plata(imp) : n.Descripcion, false, null));
        }

        return r.Count == 0 ? null : new BienvenidaBloqueDto(Clave, "Hoy", "renglones", r);
    }
}

/// <summary>Recordatorios propios y notas que deja uno para otro. La lista la trae la pantalla
/// aparte (/api/bienvenida/recordatorios) porque se tilda y se agrega sin recargar todo.</summary>
public class BienvenidaBloqueRecordatorios : IBienvenidaBloque
{
    public string Clave => "recordatorios";
    public int Orden => 30;
    public Task<BienvenidaBloqueDto?> ArmarAsync(BienvenidaContexto ctx)
        => Task.FromResult<BienvenidaBloqueDto?>(
            new BienvenidaBloqueDto(Clave, "Recordatorios y notas", "recordatorios", new()));
}
