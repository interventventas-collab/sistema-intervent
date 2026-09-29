using System.Globalization;
using System.Text;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-09-29 — Avisos por WhatsApp a los internos (para empezar, Gabriel) sobre un cliente:
///
///   1. En cada venta de un cliente con plan de bonificación y "avisar en cada venta" prendido:
///      lo que se vendió, lo que lleva en el mes y cómo va la bonificación (Núcleo).
///   2. Lo que debe un cliente, con cada comprobante sin pagar: "ahora" o programado desde la
///      ficha (Cafe_AvisosDeuda, lo corre AvisosDeudaBackgroundService).
///
/// A quién: la libretita de personas (Auto_Personas) vía Auto_Destinatarios, así se pueden sumar
/// destinatarios sin tocar código. Todo sale por WhatsApp_MensajesProgramados con EsperarVentana:
/// si la persona no escribió en 24 hs, queda esperando y sale apenas escriba (y avisa en la
/// campanita). Así además queda registrado y se ve en su chat.
/// </summary>
public class ClienteAvisosWaService
{
    private readonly AppDbContext _db;
    private readonly CafeBonificacionService _bonif;
    private readonly CafeSaldosService _saldos;
    private readonly ILogger<ClienteAvisosWaService> _log;

    private static readonly CultureInfo Ar = CultureInfo.GetCultureInfo("es-AR");

    public ClienteAvisosWaService(AppDbContext db, CafeBonificacionService bonif, CafeSaldosService saldos,
        ILogger<ClienteAvisosWaService> log)
    {
        _db = db; _bonif = bonif; _saldos = saldos; _log = log;
    }

    public static string OrigenVenta(int ventaId) => $"bonif-venta:{ventaId}";

    private static string Kg(decimal v) => v.ToString("0.##", Ar);
    private static string Plata(decimal v) => "$" + v.ToString("N0", Ar);

    private static readonly string[] Meses = { "", "enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre" };

    // ─────────────────────────────────────────────────────────────────────
    //  Destinatarios
    // ─────────────────────────────────────────────────────────────────────

    public record Destino(int PersonaId, string Nombre, string Numero);

    /// <summary>Personas activas con WhatsApp, con el número como lo guarda la bandeja del chat.</summary>
    public async Task<List<Destino>> DestinosAsync(IEnumerable<int> personaIds)
    {
        var ids = personaIds.Distinct().ToList();
        if (ids.Count == 0) return new();
        var personas = await _db.AutoPersonas.AsNoTracking()
            .Where(p => p.Activo && ids.Contains(p.Id) && p.WhatsAppNumero != null && p.WhatsAppNumero != "")
            .ToListAsync();
        return personas
            .Select(p => new Destino(p.Id, p.Nombre, MetaWhatsAppService.ToInboxWhatsApp(p.WhatsAppNumero)))
            .Where(d => !string.IsNullOrWhiteSpace(d.Numero))
            .ToList();
    }

    public async Task<List<int>> PersonasDeClaveAsync(string clave) =>
        await _db.AutoDestinatarios.AsNoTracking().Where(d => d.AutoKey == clave).Select(d => d.PersonaId).ToListAsync();

    public async Task GuardarPersonasDeClaveAsync(string clave, IEnumerable<int> personaIds)
    {
        var viejos = await _db.AutoDestinatarios.Where(d => d.AutoKey == clave).ToListAsync();
        _db.AutoDestinatarios.RemoveRange(viejos);
        foreach (var pid in personaIds.Distinct())
            _db.AutoDestinatarios.Add(new AutoDestinatario { AutoKey = clave, PersonaId = pid });
        await _db.SaveChangesAsync();
    }

    /// <summary>Deja el texto en la cola de WhatsApp para cada destino. Devuelve a quiénes.</summary>
    private async Task<List<string>> EncolarAsync(List<Destino> destinos, string texto, string origen, DateTime programadoPara, string? creadoPor)
    {
        foreach (var d in destinos)
        {
            _db.WhatsAppMensajesProgramados.Add(new WhatsAppMensajeProgramado
            {
                Numero = d.Numero,
                Tipo = WhatsAppMensajeProgramado.TipoTexto,
                Texto = texto,
                CuerpoPreview = texto,
                ProgramadoPara = programadoPara,
                Estado = WhatsAppMensajeProgramado.EstadoPendiente,
                EsperarVentana = true,
                Origen = origen.Length > 60 ? origen[..60] : origen,
                CreadoPorNombre = creadoPor ?? "Sistema",
            });
        }
        await _db.SaveChangesAsync();
        return destinos.Select(d => d.Nombre).ToList();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  1. Aviso en cada venta (Núcleo)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Renglones de la bonificación para sumar a un mensaje ("Bonificación de septiembre…").</summary>
    public static string LineasBonificacion(CafeBonificacionService.ResumenDto r)
    {
        if (r.Plan is not { } p) return "";
        var hoy = PanoramaService.AhoraAr();
        var sb = new StringBuilder();
        sb.Append($"🎁 *Bonificación de {Meses[hoy.Month]}*\n");
        sb.Append($"Lleva {Kg(r.KgMesActual)} kg de {p.ProductoKgSku}\n");
        if (p.PctMensual > 0)
        {
            var redondeado = Math.Ceiling(r.PctMesActual);
            sb.Append($"{Kg(p.PctMensual)}% parcial: {Kg(r.PctMesActual)} kg");
            if (redondeado != r.PctMesActual && redondeado > 0) sb.Append($" (hoy serían {Kg(redondeado)} kg)");
            sb.Append('\n');
        }
        if (p.ProductoRegaloId != null)
        {
            sb.Append($"{p.ProductoRegaloSku}: {r.RegalosGanadosMes} ganadas · {r.RegalosEntregadosMes} entregadas");
            if (r.RegalosPendientes > 0) sb.Append($" · ⚠ {r.RegalosPendientes} sin entregar");
            sb.Append('\n');
        }
        if (p.PctMensual > 0)
            sb.Append($"A favor sin entregar: {Kg(r.KgAFavor)} kg de {p.ProductoKgSku}\n");
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Se llama al guardar una venta nueva. Si el cliente tiene plan con aviso prendido, deja el
    /// mensaje en la cola para dentro de 2 minutos: si en ese rato se manda la "copia por WhatsApp"
    /// a la misma persona, la copia lleva la bonificación y este se cancela (no le llegan dos).
    /// Nunca tira: una falla acá no puede romper la venta.
    /// </summary>
    public async Task EncolarAvisoVentaAsync(int ventaId, string? creadoPor, DateTime? programadoPara = null)
    {
        try
        {
            var v = await _db.CafeVentas.AsNoTracking().Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == ventaId);
            if (v?.ClienteId is not int cid) return;
            // Presupuestos no son venta; una factura que sale de convertir una cotización ya avisó como cotización.
            if (v.TipoComprobante == "PRO" || v.OrigenVentaId != null || v.Estado == "anulado") return;

            var plan = await _db.CafePlanesBonificacion.AsNoTracking().FirstOrDefaultAsync(p => p.ClienteId == cid);
            if (plan is not { Activo: true, AvisarEnCadaVenta: true }) return;

            var destinos = await DestinosAsync(await PersonasDeClaveAsync(CafeBonificacionService.ClaveAvisoVenta(cid)));
            if (destinos.Count == 0) return;

            var r = await _bonif.CalcularAsync(plan, null);
            var texto = new StringBuilder();
            texto.Append($"🧾 *Venta {v.Numero}* · {v.ClienteNombreSnapshot}\n");
            foreach (var i in (v.Items ?? new List<CafeVentaItem>()).Take(8))
            {
                var bonif = i.DescuentoPct >= 100 ? " (bonificado)" : "";
                texto.Append($"• {i.Cantidad} × {i.ProductoNombreSnapshot}{bonif}\n");
            }
            if ((v.Items?.Count ?? 0) > 8) texto.Append($"• y {v.Items!.Count - 8} más\n");
            texto.Append($"Total: {Plata(v.MontoCobrable())}\n\n");
            texto.Append(LineasBonificacion(r));

            await EncolarAsync(destinos, texto.ToString(), OrigenVenta(v.Id), programadoPara ?? DateTime.UtcNow.AddMinutes(2), creadoPor);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[Avisos cliente] no pude encolar el aviso de la venta {VentaId}", ventaId);
        }
    }

    /// <summary>La copia por WhatsApp a <paramref name="numeroInbox"/> ya lleva la bonificación:
    /// cancela el aviso automático de la misma venta que todavía no salió.</summary>
    public async Task CancelarAvisoVentaPendienteAsync(int ventaId, string numeroInbox)
    {
        var origen = OrigenVenta(ventaId);
        var filas = await _db.WhatsAppMensajesProgramados
            .Where(x => x.Origen == origen && x.Numero == numeroInbox && x.Estado == WhatsAppMensajeProgramado.EstadoPendiente)
            .ToListAsync();
        foreach (var f in filas)
        {
            f.Estado = WhatsAppMensajeProgramado.EstadoCancelado;
            f.Error = "Cancelado: la copia de la venta ya le llevó la bonificación.";
            f.UpdatedAt = DateTime.UtcNow;
        }
        if (filas.Count > 0) await _db.SaveChangesAsync();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  2. Lo que debe un cliente
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Texto con el total y cada comprobante sin pagar (el más viejo primero). Misma
    /// fórmula que "¿Quién me debe?" (CafeSaldosService). Null si el cliente no existe.</summary>
    public async Task<string?> ArmarTextoDeudaAsync(int clienteId)
    {
        var c = await _db.CafeClientes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == clienteId);
        if (c == null) return null;

        var saldo = await _saldos.GetSaldoClienteAsync(clienteId);
        var nombre = string.IsNullOrWhiteSpace(c.Nombre) ? (c.RazonSocial ?? "Cliente") : c.Nombre;
        var hoy = PanoramaService.AhoraAr().Date;

        if (saldo <= CafeSaldosService.Umbral)
            return $"✅ *{nombre}* no debe nada (al {hoy:dd/MM}).";

        var pendientes = (await _saldos.GetVentasCuentaAsync(clienteId))
            .Where(v => v.Pendiente)
            .OrderBy(v => v.Fecha).ThenBy(v => v.Id)
            .ToList();

        var sb = new StringBuilder();
        sb.Append($"💰 *{nombre} debe {Plata(saldo)}*\n");
        if (!string.IsNullOrWhiteSpace(c.RazonSocial) && c.RazonSocial != nombre) sb.Append($"{c.RazonSocial}\n");
        sb.Append($"{pendientes.Count} comprobante{(pendientes.Count == 1 ? "" : "s")} sin pagar:\n");
        const int max = 30;
        foreach (var v in pendientes.Take(max))
        {
            var parcial = v.Pagado > CafeSaldosService.Umbral ? $" (de {Plata(v.Cobrable)})" : "";
            sb.Append($"• {v.Numero} · {v.Fecha:dd/MM/yy} · debe {Plata(v.Saldo)}{parcial}\n");
        }
        if (pendientes.Count > max) sb.Append($"• y {pendientes.Count - max} más\n");

        // Pagos a cuenta (sin imputar a un comprobante): la suma de arriba da más que el saldo.
        var sumaComprobantes = pendientes.Sum(v => v.Saldo);
        var aCuenta = sumaComprobantes - saldo;
        if (aCuenta > 1m) sb.Append($"Menos {Plata(aCuenta)} pagados a cuenta\n");

        if (pendientes.Count > 0)
            sb.Append($"El más viejo tiene {(hoy - pendientes[0].Fecha.Date).Days} días\n");
        sb.Append($"_(al {hoy:dd/MM/yy})_");
        return sb.ToString();
    }

    /// <summary>Arma el texto y lo encola ya para las personas dadas. Devuelve (a quiénes, error).</summary>
    public async Task<(List<string> Enviados, string? Error)> EncolarDeudaAsync(int clienteId, IEnumerable<int> personaIds, string? creadoPor)
    {
        var destinos = await DestinosAsync(personaIds);
        if (destinos.Count == 0) return (new(), "Elegí al menos una persona que tenga WhatsApp cargado en la libretita.");
        var texto = await ArmarTextoDeudaAsync(clienteId);
        if (texto == null) return (new(), "No existe el cliente.");
        var nombres = await EncolarAsync(destinos, texto, $"deuda:{clienteId}", DateTime.UtcNow, creadoPor);
        return (nombres, null);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Programación (Cafe_AvisosDeuda)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Próximo envío en UTC, estrictamente después de <paramref name="despuesDeUtc"/>.
    /// Null si no hay más (una vez, ya pasada).</summary>
    public static DateTime? CalcularProximo(CafeAvisoDeuda a, DateTime despuesDeUtc)
    {
        var desdeAr = despuesDeUtc.AddHours(-3);
        var hora = TimeSpan.FromMinutes(a.HoraMin);
        DateTime? ar = null;
        switch (a.Frecuencia)
        {
            case CafeAvisoDeuda.FrecUnaVez:
                if (a.Fecha is DateTime f && f.Date + hora > desdeAr) ar = f.Date + hora;
                break;
            case CafeAvisoDeuda.FrecSemanal:
                if (a.DiaSemana is int ds and >= 1 and <= 7)
                    for (var i = 0; i <= 7 && ar == null; i++)
                    {
                        var d = desdeAr.Date.AddDays(i);
                        var dow = d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;
                        if (dow == ds && d + hora > desdeAr) ar = d + hora;
                    }
                break;
            case CafeAvisoDeuda.FrecMensual:
                if (a.DiaMes is int dm and >= 1 and <= 28)
                    for (var i = 0; i <= 1 && ar == null; i++)
                    {
                        var m = new DateTime(desdeAr.Year, desdeAr.Month, 1).AddMonths(i);
                        var d = new DateTime(m.Year, m.Month, dm);
                        if (d + hora > desdeAr) ar = d + hora;
                    }
                break;
        }
        return ar?.AddHours(3);
    }

    public static string Describir(CafeAvisoDeuda a)
    {
        var hora = $"{a.HoraMin / 60:00}:{a.HoraMin % 60:00}";
        string[] dias = { "", "lunes", "martes", "miércoles", "jueves", "viernes", "sábado", "domingo" };
        return a.Frecuencia switch
        {
            CafeAvisoDeuda.FrecUnaVez => $"el {a.Fecha:dd/MM/yyyy} a las {hora}",
            CafeAvisoDeuda.FrecSemanal => $"todos los {dias[Math.Clamp(a.DiaSemana ?? 0, 0, 7)]} a las {hora}",
            CafeAvisoDeuda.FrecMensual => $"el día {a.DiaMes} de cada mes a las {hora}",
            _ => a.Frecuencia
        };
    }

    /// <summary>Lo corre el robot cada minuto: manda los programados cuya hora llegó.</summary>
    public async Task ProcesarProgramadosAsync()
    {
        var ahora = DateTime.UtcNow;
        var toca = await _db.CafeAvisosDeuda
            .Where(a => a.Activo && a.ProximoEnvio != null && a.ProximoEnvio <= ahora)
            .OrderBy(a => a.ProximoEnvio).Take(20).ToListAsync();
        foreach (var a in toca)
        {
            try
            {
                var (enviados, error) = await EncolarDeudaAsync(a.ClienteId,
                    await PersonasDeClaveAsync(CafeAvisoDeuda.Clave(a.Id)), "Programado");
                a.UltimoEnvioAt = DateTime.UtcNow;
                a.UltimoResultado = error ?? ("Enviado a " + string.Join(", ", enviados));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[Avisos deuda] falló el programado {Id}", a.Id);
                a.UltimoResultado = "No salió por un error del sistema: " + ex.Message;
            }
            if (a.UltimoResultado?.Length > 400) a.UltimoResultado = a.UltimoResultado[..400];
            // Se calcula desde AHORA: si el servidor estuvo apagado, no manda una tanda de atrasados.
            a.ProximoEnvio = CalcularProximo(a, DateTime.UtcNow);
            if (a.ProximoEnvio == null) a.Activo = false;
            await _db.SaveChangesAsync();
        }
    }
}

/// <summary>2026-09-29: robot de los "mandale lo que debe" programados desde la ficha del cliente.</summary>
public class AvisosDeudaBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AvisosDeudaBackgroundService> _log;

    public AvisosDeudaBackgroundService(IServiceScopeFactory scopeFactory, ILogger<AvisosDeudaBackgroundService> log)
    {
        _scopeFactory = scopeFactory; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(70), stoppingToken); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ClienteAvisosWaService>().ProcesarProgramadosAsync();
            }
            catch (Exception ex) { _log.LogWarning(ex, "[Avisos deuda] error en el ciclo (no critico)"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
