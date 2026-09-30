using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 30/09/2026 — respaldo automático de TODOS los comprobantes en Google Drive, por si se cae el
/// sistema. Pedido del dueño: "que se carguen todos los comprobantes de todo tipo acá siempre, y
/// cada tanto vacío la carpeta". La cuenta de Drive (intervent.facturacion) es solo para esto.
///
/// Queda así en Drive (dentro de la carpeta raíz de Integraciones → Google Drive):
///   Facturas / Notas de credito / Comprobantes X / Presupuestos / Recibos de cobranza /
///   Alquileres / Comodatos  →  2026  →  10-Octubre  →  PDF
///
/// Cómo decide qué subir: cada comprobante desde el 01/01/2026 que no esté en Drive_Respaldos,
/// o que se haya modificado (UpdatedAt) después de la última subida. Al volver a subir PISA el
/// mismo archivo (no quedan repetidos). Si vaciaron la carpeta, sube uno nuevo solo si el
/// comprobante se vuelve a editar — lo viejo que borraron no vuelve a aparecer.
///
/// Facturas y notas de crédito solo cuando ARCA ya las autorizó (antes no tienen CAE y el PDF
/// saldría como cotización). X, presupuestos, recibos y reservas anulados/cancelados no se suben.
///
/// ⚠ NO toca CafeVenta.DriveSubidoAt: ese campo decide qué ventas viejas aparecen en el tablero
/// de armado (EstadoPreparacion null + DriveSubidoAt != null). Si el robot lo llenara, TODAS las
/// ventas de oficina aparecerían para armar. Lo sigue llenando solo la nubecita.
///
/// El PDF se arma con los MISMOS métodos del botón "Descargar" de cada pantalla, en un scope
/// aparte que se descarta: esos métodos retocan la entidad para imprimir (domicilio con localidad,
/// datos del emisor) y no queremos que eso termine guardado en la base.
/// </summary>
public class DriveRespaldoService
{
    public const string KeyActivo = "drive.respaldo.activo";
    public static readonly DateTime Desde = new(2026, 1, 1);

    private readonly AppDbContext _db;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GoogleDriveService _drive;
    private readonly ILogger<DriveRespaldoService> _log;

    public DriveRespaldoService(AppDbContext db, IServiceScopeFactory scopeFactory,
        GoogleDriveService drive, ILogger<DriveRespaldoService> log)
    {
        _db = db; _scopeFactory = scopeFactory; _drive = drive; _log = log;
    }

    public record Candidato(string Tipo, int Id, DateTime Marca, DateTime Fecha, string Subtipo);

    public record Estado(bool Activo, bool DriveConectado, int Subidos, int Pendientes, int ConError,
        DateTime? UltimaSubidaAt, string? UltimoError);

    // ── Interruptor (arranca APAGADO: lo prende el usuario en Integraciones) ──

    public async Task<bool> EstaActivoAsync()
        => (await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == KeyActivo))?.Value == "1";

    public async Task SetActivoAsync(bool activo)
    {
        var s = await _db.AppSettings.FirstOrDefaultAsync(x => x.Key == KeyActivo);
        if (s is null) _db.AppSettings.Add(s = new AppSetting { Key = KeyActivo });
        s.Value = activo ? "1" : "0";
        s.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<Estado> GetEstadoAsync()
    {
        var activo = await EstaActivoAsync();
        var conectado = await _drive.EstaConfiguradoAsync();
        var pendientes = (await CandidatosAsync(int.MaxValue, respetarEspera: false)).Count;
        var subidos = await _db.DriveRespaldos.CountAsync(x => x.SubidoAt != null);
        var conError = await _db.DriveRespaldos.CountAsync(x => x.UltimoError != null);
        var ultimaSubida = await _db.DriveRespaldos.MaxAsync(x => (DateTime?)x.SubidoAt);
        var ultimoError = await _db.DriveRespaldos.Where(x => x.UltimoError != null)
            .OrderByDescending(x => x.UltimoIntentoAt).Select(x => x.UltimoError).FirstOrDefaultAsync();
        return new Estado(activo, conectado, subidos, pendientes, conError, ultimaSubida, ultimoError);
    }

    // ── Qué falta subir ──

    /// <summary>Comprobantes que faltan subir o que se editaron después de subirlos, los más nuevos
    /// primero (así lo del día sube enseguida y lo viejo de 2026 se va completando atrás).
    /// respetarEspera=true saltea los que fallaron hace poco (esperan más cada vez que fallan).</summary>
    public async Task<List<Candidato>> CandidatosAsync(int max, bool respetarEspera = true)
    {
        var filas = await _db.DriveRespaldos.AsNoTracking()
            .Select(x => new { x.Tipo, x.EntidadId, x.MarcaSubida, x.Intentos, x.UltimoIntentoAt })
            .ToListAsync();
        var porClave = filas.ToDictionary(x => (x.Tipo, x.EntidadId));

        var todos = new List<Candidato>();

        var ventas = await _db.CafeVentas.AsNoTracking()
            .Where(v => v.Fecha >= Desde)
            .Where(v => ((v.TipoComprobante == "FA" || v.TipoComprobante == "FB" || v.TipoComprobante == "FC"
                          || v.TipoComprobante == "NCA" || v.TipoComprobante == "NCB" || v.TipoComprobante == "NCC")
                         && v.ArcaEstado == "autorizado" && v.ArcaCae != null)
                        || ((v.TipoComprobante == "X" || v.TipoComprobante == "PRO") && v.Estado != "anulado"))
            .Select(v => new { v.Id, Marca = v.UpdatedAt ?? v.CreatedAt, v.Fecha, v.TipoComprobante })
            .ToListAsync();
        todos.AddRange(ventas.Select(v => new Candidato("VENTA", v.Id, v.Marca, v.Fecha, v.TipoComprobante ?? "")));

        var cobranzas = await _db.CafeCobranzas.AsNoTracking()
            .Where(c => c.Fecha >= Desde && c.Estado != "ANULADA")
            .Select(c => new { c.Id, Marca = c.UpdatedAt ?? c.CreatedAt, c.Fecha })
            .ToListAsync();
        todos.AddRange(cobranzas.Select(c => new Candidato("COBRANZA", c.Id, c.Marca, c.Fecha, "")));

        var reservas = await _db.AlqReservas.AsNoTracking()
            .Where(r => r.CreatedAt >= Desde)
            .Select(r => new { r.Id, Marca = r.UpdatedAt ?? r.CreatedAt, r.CreatedAt, r.Estado,
                r.ArcaEstado, r.ArcaCae, r.ArcaFecha, r.NcEstado, r.NcCae, r.NcFecha })
            .ToListAsync();
        foreach (var r in reservas)
        {
            var creadaAr = r.CreatedAt.AddHours(-3);
            if (r.Estado != "cancelado")
                todos.Add(new Candidato("ALQ_RESERVA", r.Id, r.Marca, creadaAr, ""));
            if (r.ArcaEstado == "autorizado" && !string.IsNullOrEmpty(r.ArcaCae))
                todos.Add(new Candidato("ALQ_FACTURA", r.Id, r.Marca, r.ArcaFecha ?? creadaAr, ""));
            if (r.NcEstado == "autorizado" && !string.IsNullOrEmpty(r.NcCae))
                todos.Add(new Candidato("ALQ_NC", r.Id, r.Marca, r.NcFecha ?? creadaAr, ""));
        }

        var comodatos = await _db.CafeComodatos.AsNoTracking()
            .Where(c => c.CreatedAt >= Desde)
            .Select(c => new { c.Id, Marca = c.UpdatedAt ?? c.CreatedAt, c.FechaEntrega, c.CreatedAt })
            .ToListAsync();
        todos.AddRange(comodatos.Select(c => new Candidato("COMODATO", c.Id, c.Marca,
            c.FechaEntrega ?? c.CreatedAt.AddHours(-3), "")));

        var ahora = DateTime.UtcNow;
        return todos
            .Where(c =>
            {
                if (!porClave.TryGetValue((c.Tipo, c.Id), out var f)) return true;
                if (f.MarcaSubida is not null && c.Marca <= f.MarcaSubida) return false;
                if (respetarEspera && f.Intentos > 0 && f.UltimoIntentoAt is not null)
                {
                    // 5, 10, 20, 40... minutos, como mucho 6 horas entre intentos.
                    var espera = TimeSpan.FromMinutes(Math.Min(360, 5 * Math.Pow(2, Math.Min(f.Intentos - 1, 7))));
                    if (f.UltimoIntentoAt.Value + espera > ahora) return false;
                }
                return true;
            })
            .OrderByDescending(c => c.Marca)
            .Take(max)
            .ToList();
    }

    // ── Subir ──

    /// <summary>Sube (o reemplaza) un comprobante y anota el resultado. Devuelve el id del archivo
    /// en Drive, o null si ese comprobante ya no existe / no corresponde subirlo. Si falla, anota el
    /// error y lo tira para arriba.</summary>
    public async Task<string?> SubirAsync(Candidato c)
    {
        var fila = await _db.DriveRespaldos.FirstOrDefaultAsync(x => x.Tipo == c.Tipo && x.EntidadId == c.Id);
        if (fila is null) _db.DriveRespaldos.Add(fila = new DriveRespaldo { Tipo = c.Tipo, EntidadId = c.Id });

        try
        {
            var (bytes, nombre) = await ArmarPdfAsync(c);
            if (bytes is null)
            {
                // Ya no existe o dejó de corresponder: se anota como "al día" para no reintentar.
                fila.MarcaSubida = c.Marca;
                fila.UltimoError = null;
                fila.Intentos = 0;
                await _db.SaveChangesAsync();
                return null;
            }
            if (!nombre.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) nombre += ".pdf";

            var carpetas = new[] { CarpetaDeTipo(c), c.Fecha.Year.ToString(), GoogleDriveService.NombreCarpetaMes(c.Fecha.Month) };
            var fileId = await _drive.SubirOReemplazarAsync(carpetas, nombre, bytes, fila.DriveFileId);

            fila.DriveFileId = fileId;
            fila.NombreArchivo = Recortar(nombre, 260);
            fila.Carpeta = string.Join("/", carpetas);
            fila.MarcaSubida = c.Marca;
            fila.SubidoAt = DateTime.UtcNow;
            fila.UltimoError = null;
            fila.Intentos = 0;
            fila.UltimoIntentoAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return fileId;
        }
        catch (Exception ex)
        {
            fila.UltimoError = Recortar(ex.Message, 1000);
            fila.Intentos++;
            fila.UltimoIntentoAt = DateTime.UtcNow;
            try { await _db.SaveChangesAsync(); } catch { /* no tapar el error original */ }
            throw;
        }
    }

    /// <summary>La nubecita de una venta: sube YA esa venta (aunque el respaldo automático esté
    /// apagado) a la misma carpeta y el mismo archivo que usa el robot. Devuelve el id en Drive.</summary>
    public async Task<string?> SubirVentaAhoraAsync(int ventaId)
    {
        var v = await _db.CafeVentas.AsNoTracking().Where(x => x.Id == ventaId)
            .Select(x => new { x.Id, Marca = x.UpdatedAt ?? x.CreatedAt, x.Fecha, x.TipoComprobante })
            .FirstOrDefaultAsync();
        if (v is null) return null;
        return await SubirAsync(new Candidato("VENTA", v.Id, v.Marca, v.Fecha, v.TipoComprobante ?? ""));
    }

    private async Task<(byte[]? bytes, string nombre)> ArmarPdfAsync(Candidato c)
    {
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        switch (c.Tipo)
        {
            case "VENTA":
            {
                var db = sp.GetRequiredService<AppDbContext>();
                var v = await db.CafeVentas.AsNoTracking()
                    .Include(x => x.Items).ThenInclude(i => i.ProductoNav)
                    .FirstOrDefaultAsync(x => x.Id == c.Id);
                if (v is null) return (null, "");
                var cfg = await db.CafeSettings.FindAsync(1);
                var ctrl = sp.GetRequiredService<Controllers.CafeVentasController>();
                return (await ctrl.GenerarPdfBytesAsync(v, cfg), Controllers.CafeVentasController.BuildPdfFilename(v));
            }
            case "COBRANZA":
            {
                var (bytes, nombre, _) = await sp.GetRequiredService<Controllers.CafeCobranzasController>().GenerarPdfBytesAsync(c.Id);
                return (bytes, nombre);
            }
            case "ALQ_RESERVA":
            {
                var (bytes, nombre) = await sp.GetRequiredService<Controllers.AlqReservasController>().GenerarPdfBytesAsync(c.Id);
                return (bytes, "Reserva - " + nombre);
            }
            case "ALQ_FACTURA":
                return await sp.GetRequiredService<Controllers.AlqReservasController>().GenerarFacturaPdfBytesAsync(c.Id);
            case "ALQ_NC":
                return await sp.GetRequiredService<Controllers.AlqReservasController>().GenerarNotaCreditoPdfBytesAsync(c.Id);
            case "COMODATO":
                return await sp.GetRequiredService<Controllers.CafeComodatosController>().GenerarPdfBytesAsync(c.Id);
            default:
                return (null, "");
        }
    }

    private static string CarpetaDeTipo(Candidato c) => c.Tipo switch
    {
        "VENTA" => c.Subtipo switch
        {
            "FA" or "FB" or "FC" => "Facturas",
            "NCA" or "NCB" or "NCC" => "Notas de credito",
            "X" => "Comprobantes X",
            "PRO" => "Presupuestos",
            _ => "Otros comprobantes"
        },
        "COBRANZA" => "Recibos de cobranza",
        "ALQ_RESERVA" or "ALQ_FACTURA" or "ALQ_NC" => "Alquileres",
        "COMODATO" => "Comodatos",
        _ => "Otros comprobantes"
    };

    private static string Recortar(string s, int max) => s.Length <= max ? s : s[..max];
}
