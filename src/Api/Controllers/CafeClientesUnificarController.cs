using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-10-05: UNIFICAR dos clientes que son la misma persona (ej. "JAVIER TORNERO COLON 809" y
/// "JAVIER TORNERO TERRAROSA 120"). Todo lo del duplicado (ventas, cobranzas, cheques, alquileres,
/// precios pactados, chats de WhatsApp, etc.) pasa al cliente que QUEDA; su domicilio y teléfono se
/// agregan como domicilio alternativo / teléfono 2, y el duplicado queda Inactivo con una nota.
///
/// No se borra nada: los Id de cada fila movida quedan anotados en AuditLogs (EntityType
/// "CafeClienteUnificado") para poder dar marcha atrás a mano si se unificó el cliente equivocado.
/// </summary>
[ApiController]
[Route("api/cafe/clientes")]
[Authorize]
public class CafeClientesUnificarController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly CafeSaldosService _saldos;
    private readonly AuditLogService _audit;

    public CafeClientesUnificarController(AppDbContext db, CafeSaldosService saldos, AuditLogService audit)
    {
        _db = db;
        _saldos = saldos;
        _audit = audit;
    }

    /// <summary>Cada tabla que apunta a un cliente. <c>Etiqueta</c> es lo que ve el usuario en el
    /// cartel (null = se mueve igual pero no se muestra). <c>SinChoque</c> es una condición extra
    /// para las tablas con clave única: lo que el cliente que queda YA tiene, se deja en el
    /// duplicado (que queda inactivo) en vez de pisarlo.</summary>
    private record Tabla(string Nombre, string Columna, string? Etiqueta, string? SinChoque = null);

    private static readonly Tabla[] Tablas =
    {
        new("Cafe_Ventas", "ClienteId", "ventas"),
        new("Cafe_Cobranzas", "ClienteId", "cobranzas"),
        new("Cafe_Cheques", "ClienteOrigenId", "cheques"),
        new("Alq_Reservas", "ClienteId", "alquileres"),
        new("Alq_Cotizaciones", "ClienteId", "cotizaciones de alquiler"),
        new("Cafe_Preventas", "ClienteId", "pedidos"),
        new("Cafe_SaldosMigracion", "ClienteId", "saldos del sistema viejo"),
        new("Cafe_RedirigidasPendientes", "ClienteId", "redirigidas"),
        new("Cafe_Comodatos", "ClienteId", "máquinas en comodato"),
        new("Cafe_ClienteDirecciones", "ClienteId", "domicilios alternativos"),
        new("Cafe_PreciosEspecialesCliente", "ClienteId", "precios pactados",
            "NOT EXISTS (SELECT 1 FROM Cafe_PreciosEspecialesCliente q WHERE q.ClienteId = @queda AND q.ProductoId = t.ProductoId AND q.Formato = t.Formato)"),
        new("Cafe_ListasPreciosCustom", "ClienteId", "listas de precios"),
        new("Visitas", "ClienteId", "visitas"),
        new("Cafe_PlanesBonificacion", "ClienteId", "plan de bonificación",
            "NOT EXISTS (SELECT 1 FROM Cafe_PlanesBonificacion q WHERE q.ClienteId = @queda)"),
        new("Cafe_BonificacionesMes", "ClienteId", null,
            "NOT EXISTS (SELECT 1 FROM Cafe_BonificacionesMes q WHERE q.ClienteId = @queda AND q.Anio = t.Anio AND q.Mes = t.Mes)"),
        new("Cafe_AvisosDeuda", "ClienteId", "avisos de deuda"),
        new("Cafe_ClienteProductoDescartado", "ClienteId", null,
            "NOT EXISTS (SELECT 1 FROM Cafe_ClienteProductoDescartado q WHERE q.ClienteId = @queda AND q.ProductoId = t.ProductoId)"),
        new("Cafe_ExtractoMov_DescartadoPorCliente", "ClienteId", null,
            "NOT EXISTS (SELECT 1 FROM Cafe_ExtractoMov_DescartadoPorCliente q WHERE q.ClienteId = @queda AND q.MovimientoId = t.MovimientoId)"),
        new("WhatsApp_ContactoClientes", "ClienteId", "chats de WhatsApp",
            "NOT EXISTS (SELECT 1 FROM WhatsApp_ContactoClientes q WHERE q.ClienteId = @queda AND q.Numero = t.Numero)"),
        new("WhatsApp_TwilioContactos", "ClienteId", null),
        new("WhatsApp_ClienteElegido", "ClienteId", null),
        new("WhatsAppPedidosRecibidos", "ClienteId", null),
        new("TelegramAccounts", "ConvClienteId", null),
        new("Cafe_ClienteAltas", "ClienteIdCreado", null),
    };

    public record UnificarItemDto(string Que, int Cantidad);
    public record UnificarPreviewDto(
        int QuedaId, string QuedaCodigo, string QuedaNombre, decimal QuedaSaldo,
        int OtroId, string OtroCodigo, string OtroNombre, decimal OtroSaldo,
        string? OtroTelefono, string? OtroDomicilio,
        List<UnificarItemDto> Items);
    public record UnificarRequest(int OtroId);

    private async Task<bool> ExisteTabla(string t) =>
        (await _db.Database.SqlQueryRaw<int>(
            "SELECT CASE WHEN OBJECT_ID({0}) IS NULL THEN 0 ELSE 1 END AS Value", t).ToListAsync()).First() == 1;

    private string Where(Tabla t) =>
        $"t.[{t.Columna}] = @otro" + (t.SinChoque is null ? "" : " AND " + t.SinChoque);

    /// <summary>El cartel de "esto es lo que se va a pasar" antes de confirmar.</summary>
    [HttpGet("{id:int}/unificar/preview")]
    public async Task<IActionResult> Preview(int id, [FromQuery] int otroId)
    {
        var (queda, otro, error) = await Cargar(id, otroId);
        if (error is not null) return BadRequest(new { error });

        var items = new List<UnificarItemDto>();
        foreach (var t in Tablas.Where(t => t.Etiqueta is not null))
        {
            if (!await ExisteTabla(t.Nombre)) continue;
            var n = (await _db.Database.SqlQueryRaw<int>(
                $"DECLARE @otro int = {{0}}, @queda int = {{1}}; SELECT COUNT(*) AS Value FROM [{t.Nombre}] t WHERE {Where(t)}",
                otro!.Id, queda!.Id).ToListAsync()).First();
            if (n > 0) items.Add(new(t.Etiqueta!, n));
        }

        return Ok(new UnificarPreviewDto(
            queda!.Id, queda.Codigo ?? "", queda.Nombre, await _saldos.GetSaldoClienteAsync(queda.Id),
            otro!.Id, otro.Codigo ?? "", otro.Nombre, await _saldos.GetSaldoClienteAsync(otro.Id),
            Norm(otro.Telefono) ?? Norm(otro.Telefono2), DomicilioDe(otro),
            items));
    }

    [HttpPost("{id:int}/unificar")]
    public Task<IActionResult> Unificar(int id, [FromBody] UnificarRequest req)
        => _db.Database.CreateExecutionStrategy().ExecuteAsync(() => UnificarCore(id, req));

    private async Task<IActionResult> UnificarCore(int id, UnificarRequest req)
    {
        _db.ChangeTracker.Clear();   // si la estrategia reintenta, arranco de cero
        var (queda, otro, error) = await Cargar(id, req.OtroId);
        if (error is not null) return BadRequest(new { error });

        var movidos = new Dictionary<string, List<int>>();
        await using var tx = await _db.Database.BeginTransactionAsync();

        // 1) Mover todo lo que cuelga del duplicado. Anoto los Id para poder deshacerlo.
        foreach (var t in Tablas)
        {
            if (!await ExisteTabla(t.Nombre)) continue;
            var ids = await _db.Database.SqlQueryRaw<int>(
                $"DECLARE @otro int = {{0}}, @queda int = {{1}}; SELECT t.Id AS Value FROM [{t.Nombre}] t WHERE {Where(t)}",
                otro!.Id, queda!.Id).ToListAsync();
            if (ids.Count == 0) continue;
            await _db.Database.ExecuteSqlRawAsync(
                $"DECLARE @otro int = {{0}}, @queda int = {{1}}; UPDATE t SET [{t.Columna}] = @queda FROM [{t.Nombre}] t WHERE {Where(t)}",
                otro.Id, queda.Id);
            movidos[t.Nombre] = ids;
        }
        // Los vínculos de WhatsApp que ya tenía el que queda: el del duplicado sobra (si no, el chat
        // ofrece al cliente inactivo como opción).
        if (await ExisteTabla("WhatsApp_ContactoClientes"))
            await _db.Database.ExecuteSqlRawAsync(
                "DELETE FROM WhatsApp_ContactoClientes WHERE ClienteId = {0}", otro!.Id);
        // "Último cliente consultado" del menú automático (no tiene Id, no hace falta anotarlo).
        if (await ExisteTabla("Auto_MenuEstado"))
            await _db.Database.ExecuteSqlRawAsync(
                "UPDATE Auto_MenuEstado SET UltimoClienteId = {1} WHERE UltimoClienteId = {0}", otro!.Id, queda!.Id);

        // 2) Datos de la ficha: el que queda conserva lo suyo; solo se completan los huecos.
        var cambiosFicha = new List<string>();
        var telOtro = Norm(otro!.Telefono) ?? Norm(otro.Telefono2);
        if (telOtro is not null && !MismoTelefono(telOtro, queda!.Telefono) && !MismoTelefono(telOtro, queda.Telefono2))
        {
            if (Norm(queda.Telefono) is null) { queda.Telefono = telOtro; cambiosFicha.Add("Telefono"); }
            else if (Norm(queda.Telefono2) is null) { queda.Telefono2 = telOtro; cambiosFicha.Add("Telefono2"); }
        }
        if (Norm(queda!.RazonSocial) is null && Norm(otro.RazonSocial) is not null) { queda.RazonSocial = otro.RazonSocial; cambiosFicha.Add("RazonSocial"); }
        if (Norm(queda.Cuit) is null && Norm(otro.Cuit) is not null) { queda.Cuit = otro.Cuit; cambiosFicha.Add("Cuit"); }
        if (Norm(queda.CondicionIvaDefault) is null && Norm(otro.CondicionIvaDefault) is not null) { queda.CondicionIvaDefault = otro.CondicionIvaDefault; cambiosFicha.Add("CondicionIvaDefault"); }
        if (Norm(queda.Email) is null && Norm(otro.Email) is not null) { queda.Email = otro.Email; cambiosFicha.Add("Email"); }
        if (queda.MeliBuyerId is null && otro.MeliBuyerId is not null)
        {
            queda.MeliBuyerId = otro.MeliBuyerId; queda.MeliNickname = otro.MeliNickname;
            otro.MeliBuyerId = null; otro.MeliNickname = null;
            cambiosFicha.Add("MeliBuyerId");
        }
        if (queda.CodigoInterno is null && otro.CodigoInterno is not null)
        {
            queda.CodigoInterno = otro.CodigoInterno; otro.CodigoInterno = null;
            cambiosFicha.Add("CodigoInterno");
        }

        // 3) El domicilio del duplicado: si el que queda no tiene domicilio de entrega, pasa a ser
        // el suyo; si tiene otro distinto, se agrega como domicilio alternativo.
        int? direccionCreadaId = null;
        var domOtro = DomicilioDe(otro);
        if (domOtro is not null)
        {
            var domQueda = DomicilioDe(queda);
            var yaAlternativo = await _db.CafeClienteDirecciones
                .AnyAsync(d => d.ClienteId == queda.Id && d.Direccion == domOtro);
            if (domQueda is null)
            {
                queda.DomicilioEntrega = domOtro;
                queda.LocalidadEntrega ??= otro.LocalidadEntrega ?? otro.Localidad;
                queda.EntreCalles ??= otro.EntreCalles;
                if (Norm(queda.MapeoLink) is null && Norm(otro.MapeoLink) is not null)
                {
                    queda.MapeoLink = otro.MapeoLink; queda.MapeoLat = otro.MapeoLat; queda.MapeoLng = otro.MapeoLng;
                }
                cambiosFicha.Add("DomicilioEntrega");
            }
            else if (!string.Equals(domQueda, domOtro, StringComparison.OrdinalIgnoreCase) && !yaAlternativo)
            {
                var dir = new CafeClienteDireccion
                {
                    ClienteId = queda.Id,
                    Etiqueta = Recortar(otro.Nombre, 100),
                    Direccion = domOtro,
                    EntreCalles = otro.EntreCalles,
                    Localidad = otro.LocalidadEntrega ?? otro.Localidad,
                    Ciudad = otro.Ciudad,
                    Cp = otro.Cp,
                    Telefono = telOtro,
                    MapeoLink = otro.MapeoLink,
                    MapeoLat = otro.MapeoLat,
                    MapeoLng = otro.MapeoLng,
                    NotasInternas = Recortar(otro.Notas, 500),
                    EsPrincipal = false,
                    IsActive = true,
                };
                _db.CafeClienteDirecciones.Add(dir);
                await _db.SaveChangesAsync();
                direccionCreadaId = dir.Id;
            }
        }

        // 4) Notas: que se sepa de dónde vino, y el duplicado queda inactivo con el aviso.
        var hoy = PanoramaService.AhoraAr().ToString("dd/MM/yyyy");
        var notaQueda = $"[{hoy}] Se unificó con el cliente {otro.Codigo} ({otro.Nombre}).";
        if (Norm(otro.Notas) is not null) notaQueda += " Sus notas: " + otro.Notas!.Trim();
        queda.Notas = string.IsNullOrWhiteSpace(queda.Notas) ? notaQueda : queda.Notas.TrimEnd() + "\n" + notaQueda;
        otro.Notas = $"[{hoy}] UNIFICADO en el cliente {queda.Codigo} ({queda.Nombre}). No usar." +
                     (string.IsNullOrWhiteSpace(otro.Notas) ? "" : "\n" + otro.Notas);
        otro.IsActive = false;
        queda.UpdatedAt = otro.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        await _audit.LogAsync("CafeClienteUnificado", queda.Id.ToString(), "unificar",
            System.Text.Json.JsonSerializer.Serialize(new
            {
                quedaId = queda.Id, quedaCodigo = queda.Codigo,
                otroId = otro.Id, otroCodigo = otro.Codigo,
                movidos, cambiosFicha, direccionCreadaId,
            }));

        var total = movidos.Values.Sum(v => v.Count);
        return Ok(new { ok = true, movidos = total, mensaje = $"Listo: {otro.Nombre} quedó unificado en {queda.Nombre}." });
    }

    private async Task<(CafeCliente? queda, CafeCliente? otro, string? error)> Cargar(int id, int otroId)
    {
        if (id == otroId) return (null, null, "Elegiste el mismo cliente.");
        var queda = await _db.CafeClientes.FindAsync(id);
        var otro = await _db.CafeClientes.FindAsync(otroId);
        if (queda is null || otro is null) return (null, null, "No encontré uno de los dos clientes.");
        return (queda, otro, null);
    }

    private static string? DomicilioDe(CafeCliente c) => Norm(c.DomicilioEntrega) ?? Norm(c.Direccion);

    private static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? Recortar(string? s, int max) =>
        Norm(s) is { } v ? (v.Length > max ? v[..max] : v) : null;

    /// <summary>"91155838346" y "1155838346" son el mismo número: comparo los últimos 8 dígitos.</summary>
    private static bool MismoTelefono(string a, string? b)
    {
        static string Dig(string? s) => new string((s ?? "").Where(char.IsDigit).ToArray());
        var da = Dig(a); var db = Dig(b);
        if (da.Length < 8 || db.Length < 8) return da == db && da.Length > 0;
        return da[^8..] == db[^8..];
    }
}
