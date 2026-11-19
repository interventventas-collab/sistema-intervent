using System.Globalization;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 18/11/2026: cargas de combustible de Shell Flota. Las guarda cuando el robot del saldo las
/// trae y arma lo que muestra la pantalla /combustible y la campanita (km que no avanzan,
/// dos cargas el mismo día, fuera de horario, rinde mucho peor que lo normal).
/// </summary>
public class ShellCargasService
{
    // Reglas de "algo raro". El horario es fijo por ahora (el dueño puede pedir otro).
    public const int KmMinimoEntreCargas = 20;
    public const int HoraDesde = 7;
    public const int HoraHasta = 21;
    public const decimal RindeMuyBajo = 0.6m; // 60 % de lo normal de ese vehículo

    private readonly AppDbContext _db;
    private readonly ILogger<ShellCargasService> _logger;
    public ShellCargasService(AppDbContext db, ILogger<ShellCargasService> logger) { _db = db; _logger = logger; }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Guarda las cargas nuevas (las repetidas se ignoran). Devuelve cuántas eran nuevas.</summary>
    public async Task<int> GuardarAsync(List<ShellCargaLeidaDto>? leidas)
    {
        if (leidas is null || leidas.Count == 0) return 0;
        var primeraVez = !await _db.ShellCargas.AnyAsync();
        var nuevas = new List<ShellCarga>();
        foreach (var l in leidas)
        {
            if (!DateTime.TryParseExact($"{l.Fecha} {l.Hora}", new[] { "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy H:mm:ss", "dd/MM/yyyy HH:mm", "dd/MM/yyyy H:mm" },
                    Inv, DateTimeStyles.None, out var fh)) continue;
            var importe = l.Importe ?? 0;
            var clave = $"{l.Tarjeta}|{fh:yyyyMMddHHmmss}|{importe.ToString("0.00", Inv)}";
            if (nuevas.Any(n => n.Clave == clave)) continue;
            nuevas.Add(new ShellCarga
            {
                Clave = clave,
                Placa = (l.Placa ?? "").Trim().ToUpperInvariant(),
                Vehiculo = $"{l.Marca} {l.Modelo}".Trim(),
                Tarjeta = l.Tarjeta,
                Estacion = l.Estacion,
                Producto = l.Producto,
                Transaccion = l.Transaccion,
                Conductor = string.IsNullOrWhiteSpace(l.Conductor) ? null : l.Conductor,
                FechaHoraAr = fh,
                Litros = l.Litros ?? 0,
                Importe = importe,
                PrecioLitro = l.Precio,
                Odometro = l.Odometro is decimal o && o > 0 ? (int)o : null,
                DetectadaAt = DateTime.UtcNow,
                Inicial = primeraVez,
            });
        }
        var claves = nuevas.Select(n => n.Clave).ToList();
        var existentes = await _db.ShellCargas.Where(c => claves.Contains(c.Clave)).Select(c => c.Clave).ToListAsync();
        var aGuardar = nuevas.Where(n => !existentes.Contains(n.Clave)).ToList();
        if (aGuardar.Count == 0) return 0;
        _db.ShellCargas.AddRange(aGuardar);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Shell: {N} cargas nuevas guardadas{Ini}", aGuardar.Count, primeraVez ? " (primera importación, sin avisos)" : "");
        return aGuardar.Count;
    }

    // ───────────── Análisis ─────────────

    public record CargaVista(int Id, string Placa, string Nombre, string? Vehiculo, string? Estacion, string? Producto,
        DateTime FechaHoraAr, decimal Litros, decimal Importe, decimal? PrecioLitro, int? Odometro,
        int? Anduvo, decimal? Rinde, List<string> Alarmas, DateTime DetectadaAt, bool Inicial);

    public record VehiculoResumen(string Placa, string Nombre, string? Vehiculo, int Cargas, decimal Litros, decimal Importe,
        decimal? Rinde, int? UltimoKm, string? Alarma);

    public record CombustibleDto(DateTime Desde, DateTime Hasta, int Cargas, decimal Litros, decimal Importe,
        List<VehiculoResumen> Vehiculos, List<CargaVista> Lista, string? Saldo, DateTime? SaldoAt);

    /// <summary>"KANGOO" → "Kangoo"; si el modelo arranca con una letra suelta ("Z TRUCK DUO") va la marca.</summary>
    public static string NombreCorto(string? vehiculo, string placa)
    {
        if (string.IsNullOrWhiteSpace(vehiculo)) return placa;
        var partes = vehiculo.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var palabra = partes.Length > 1 && partes[1].Length > 2 ? partes[1] : partes[0];
        return CultureInfo.GetCultureInfo("es-AR").TextInfo.ToTitleCase(palabra.ToLowerInvariant());
    }

    public static string EstacionLinda(string? e)
    {
        if (string.IsNullOrWhiteSpace(e)) return "—";
        var t = e.Trim();
        t = CultureInfo.GetCultureInfo("es-AR").TextInfo.ToTitleCase(t.ToLowerInvariant());
        // "Juan Xxiii" → "Juan XXIII", "Gnc" → "GNC".
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\b([IVX][ivx]+|Gnc|Ypf|Acc)\b", m => m.Value.ToUpperInvariant());
        return t;
    }

    /// <summary>Calcula km andados, rinde y alarmas de cada carga (necesita las anteriores de cada vehículo).</summary>
    public static List<CargaVista> Analizar(List<ShellCarga> cargas)
    {
        var res = new List<CargaVista>();
        foreach (var g in cargas.GroupBy(c => c.Placa))
        {
            var orden = g.OrderBy(c => c.FechaHoraAr).ToList();
            var nombre = NombreCorto(orden.Last().Vehiculo, g.Key);
            var vistas = new List<CargaVista>();
            ShellCarga? prev = null;
            foreach (var c in orden)
            {
                var al = new List<string>();
                int? anduvo = null; decimal? rinde = null;
                if (prev?.Odometro is int po && c.Odometro is int co)
                {
                    anduvo = co - po;
                    if (anduvo < KmMinimoEntreCargas)
                        al.Add(anduvo < 0 ? $"los km bajaron ({anduvo:+#;-#;0})" : $"km +{anduvo} desde la carga anterior");
                    else if (c.Litros > 0)
                        rinde = Math.Round(anduvo.Value / c.Litros, 1);
                }
                if (prev != null && prev.FechaHoraAr.Date == c.FechaHoraAr.Date)
                    al.Add("segunda carga en el día");
                if (c.FechaHoraAr.Hour < HoraDesde || c.FechaHoraAr.Hour >= HoraHasta)
                    al.Add($"fuera de horario ({c.FechaHoraAr:HH:mm})");
                vistas.Add(new CargaVista(c.Id, c.Placa, nombre, c.Vehiculo, EstacionLinda(c.Estacion), c.Producto,
                    c.FechaHoraAr, c.Litros, c.Importe, c.PrecioLitro, c.Odometro, anduvo, rinde, al, c.DetectadaAt, c.Inicial));
                prev = c;
            }
            // Si en la mayoría de las cargas los km no avanzan, los km de ese vehículo son inventados:
            // los saltos redondos (+100, +1000) darían un rinde falso, así que no se muestra ninguno.
            var kmMal = vistas.Count(v => v.Alarmas.Any(a => a.StartsWith("km +") || a.StartsWith("los km bajaron")));
            if (vistas.Count > 1 && kmMal * 2 >= vistas.Count)
                vistas = vistas.Select(v => v with { Rinde = null }).ToList();
            // Rinde mucho peor que lo normal de ESE vehículo (mediana de sus rindes válidos).
            var rindes = vistas.Where(v => v.Rinde.HasValue).Select(v => v.Rinde!.Value).OrderBy(x => x).ToList();
            if (rindes.Count >= 3)
            {
                var mediana = rindes[rindes.Count / 2];
                foreach (var v in vistas.Where(v => v.Rinde.HasValue && v.Rinde < mediana * RindeMuyBajo))
                    v.Alarmas.Add($"rinde {v.Rinde:0.#} km/l (lo normal ~{mediana:0.#})");
            }
            res.AddRange(vistas);
        }
        return res;
    }

    public async Task<CombustibleDto> ConsultarAsync(DateTime desde, DateTime hasta)
    {
        // Se traen 60 días antes para saber los km de la carga anterior a la primera del período.
        var todas = await _db.ShellCargas.AsNoTracking()
            .Where(c => c.FechaHoraAr >= desde.AddDays(-60) && c.FechaHoraAr < hasta.AddDays(1))
            .ToListAsync();
        var analizadas = Analizar(todas)
            .Where(c => c.FechaHoraAr >= desde && c.FechaHoraAr < hasta.AddDays(1))
            .OrderByDescending(c => c.FechaHoraAr).ToList();

        var vehiculos = analizadas.GroupBy(c => c.Placa).Select(g =>
        {
            var l = g.ToList();
            var kmMal = l.Count(c => c.Alarmas.Any(a => a.StartsWith("km +") || a.StartsWith("los km bajaron")));
            var conRinde = l.Where(c => c.Rinde.HasValue && c.Anduvo.HasValue).ToList();
            decimal? rinde = null;
            if (kmMal * 2 < l.Count && conRinde.Count > 0)
            {
                var lit = conRinde.Sum(c => c.Litros);
                if (lit > 0) rinde = Math.Round(conRinde.Sum(c => (decimal)c.Anduvo!.Value) / lit, 1);
            }
            string? alarma = kmMal * 2 >= l.Count && l.Count > 1 ? "los km no avanzan: anotan cualquier número"
                : kmMal > 0 ? $"{kmMal} carga{(kmMal == 1 ? "" : "s")} con km que no avanzan" : null;
            return new VehiculoResumen(g.Key, l[0].Nombre, l[0].Vehiculo, l.Count, l.Sum(c => c.Litros), l.Sum(c => c.Importe),
                rinde, l.Max(c => c.Odometro), alarma);
        }).OrderByDescending(v => v.Importe).ToList();

        var acc = await _db.ShellAccounts.AsNoTracking().OrderByDescending(a => a.Id).FirstOrDefaultAsync();
        return new CombustibleDto(desde, hasta, analizadas.Count, analizadas.Sum(c => c.Litros), analizadas.Sum(c => c.Importe),
            vehiculos, analizadas, acc?.LastSaldo, acc?.LastSaldoAt);
    }

    /// <summary>Para la campanita: cargas nuevas de los últimos 3 días (no las de la primera importación).</summary>
    public async Task<List<CargaVista>> ParaCampanitaAsync()
    {
        var desdeDet = DateTime.UtcNow.AddDays(-3);
        var hayNuevas = await _db.ShellCargas.AnyAsync(c => !c.Inicial && c.DetectadaAt >= desdeDet);
        if (!hayNuevas) return new();
        var desde = DateTime.UtcNow.AddHours(-3).Date.AddDays(-60);
        var todas = await _db.ShellCargas.AsNoTracking().Where(c => c.FechaHoraAr >= desde).ToListAsync();
        return Analizar(todas).Where(c => !c.Inicial && c.DetectadaAt >= desdeDet)
            .OrderByDescending(c => c.DetectadaAt).ThenByDescending(c => c.FechaHoraAr).Take(20).ToList();
    }
}
