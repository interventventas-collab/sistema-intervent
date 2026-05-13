using Api.Data;
using Api.DTOs;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/cafe/clientes")]
[Authorize]
public class CafeClientesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly GoogleMapsLinkResolverService _mapsResolver;
    private static readonly string[] TiposValidos = { "BAR", "OTRO" };

    public CafeClientesController(AppDbContext db, GoogleMapsLinkResolverService mapsResolver)
    {
        _db = db;
        _mapsResolver = mapsResolver;
    }

    private static CafeClienteDto Map(CafeCliente c) => new(
        c.Id, c.Codigo, c.Nombre, c.RazonSocial, c.Tipo,
        c.Cuit, c.Telefono, c.Email,
        c.Direccion, c.Localidad, c.Ciudad, c.Cp,
        c.CondicionIvaDefault,
        c.DomicilioEntrega,
        c.Notas, c.ComentariosComprobante,
        c.IsActive, c.CreatedAt, c.UpdatedAt,
        c.CodigoInterno, c.MapeoLink,
        c.MapeoLat, c.MapeoLng);

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var list = await _db.CafeClientes.OrderBy(c => c.Nombre).ToListAsync();
        return Ok(list.Select(Map).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var c = await _db.CafeClientes.FindAsync(id);
        if (c is null) return NotFound(new { error = "Cliente no encontrado" });
        return Ok(Map(c));
    }

    public record MovimientoCuentaDto(
        DateTime Fecha, string Tipo, string Numero, decimal Debe, decimal Haber, decimal SaldoAcumulado, string? Detalle);
    public record EstadoCuentaDto(int ClienteId, string ClienteNombre, decimal Saldo, List<MovimientoCuentaDto> Movimientos);

    /// <summary>
    /// Estado de cuenta del cliente: lista cronologica de ventas (debe) y cobranzas (haber)
    /// + saldo final. Para la ficha de cliente "Tab Cuenta corriente".
    /// </summary>
    [HttpGet("{id:int}/estado-cuenta")]
    public async Task<IActionResult> EstadoCuenta(int id)
    {
        var cliente = await _db.CafeClientes.FindAsync(id);
        if (cliente is null) return NotFound();

        // Ventas vigentes del cliente
        var ventas = await _db.CafeVentas
            .Where(v => v.ClienteId == id && v.Estado != "anulado")
            .Select(v => new { v.Id, v.Fecha, v.Numero, v.Total })
            .ToListAsync();
        // Cobranzas vigentes y sus retenciones
        var cobranzas = await _db.CafeCobranzas
            .Where(c => c.ClienteId == id && c.Estado == "VIGENTE")
            .Select(c => new { c.Id, c.Fecha, c.Numero, c.Total, c.Retenciones })
            .ToListAsync();
        // Comprobantes de cobranzas (para saber a que venta se aplicaron, opcional para detalle)
        // Por simplicidad la cobranza la mostramos como un haber total (Total + Retenciones).

        var movs = new List<(DateTime fecha, string tipo, string num, decimal debe, decimal haber, string? det)>();
        foreach (var v in ventas)
            movs.Add((v.Fecha, "Venta", v.Numero ?? $"#{v.Id}", v.Total, 0m, null));
        foreach (var c in cobranzas)
            movs.Add((c.Fecha, "Cobranza", c.Numero, 0m, c.Total + c.Retenciones,
                c.Retenciones > 0 ? $"(incluye ${c.Retenciones:N2} retenciones)" : null));

        movs = movs.OrderBy(x => x.fecha).ToList();
        decimal acum = 0m;
        var result = new List<MovimientoCuentaDto>(movs.Count);
        foreach (var m in movs)
        {
            acum += m.debe - m.haber;
            result.Add(new MovimientoCuentaDto(m.fecha, m.tipo, m.num, m.debe, m.haber, acum, m.det));
        }
        return Ok(new EstadoCuentaDto(id, cliente.Nombre, acum, result));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCafeClienteRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre))
            return BadRequest(new { error = "El nombre es obligatorio" });
        var tipo = NormTipo(req.Tipo);
        var c = new CafeCliente
        {
            Codigo = await GenerarCodigoAsync(),
            Nombre = req.Nombre.Trim(),
            RazonSocial = Norm(req.RazonSocial),
            Tipo = tipo,
            Cuit = Norm(req.Cuit),
            Telefono = Norm(req.Telefono),
            Email = Norm(req.Email),
            Direccion = Norm(req.Direccion),
            Localidad = Norm(req.Localidad),
            Ciudad = Norm(req.Ciudad),
            Cp = Norm(req.Cp),
            CondicionIvaDefault = Norm(req.CondicionIvaDefault),
            DomicilioEntrega = Norm(req.DomicilioEntrega),
            Notas = Norm(req.Notas),
            ComentariosComprobante = Norm(req.ComentariosComprobante),
            MapeoLink = Norm(req.MapeoLink),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        // Si vino MapeoLink, intentamos resolverlo y guardar las coords automáticamente.
        if (!string.IsNullOrEmpty(c.MapeoLink))
        {
            var coords = await _mapsResolver.TryResolverCoordenadasAsync(c.MapeoLink);
            if (coords.HasValue) { c.MapeoLat = coords.Value.lat; c.MapeoLng = coords.Value.lng; }
        }
        _db.CafeClientes.Add(c);
        await _db.SaveChangesAsync();
        return Ok(Map(c));
    }

    /// <summary>
    /// Devuelve el siguiente codigo secuencial. Pad a 4 digitos para los primeros 9999.
    /// Si ya existe alguno >= 9999 (improbable pero por las dudas), arranca con 5 digitos.
    /// </summary>
    private async Task<string> GenerarCodigoAsync()
    {
        var maxNum = await _db.CafeClientes
            .Where(c => c.Codigo != null)
            .Select(c => c.Codigo!)
            .ToListAsync();
        int max = 0;
        foreach (var s in maxNum)
        {
            if (int.TryParse(s, out var n) && n > max) max = n;
        }
        var siguiente = max + 1;
        return siguiente < 10000 ? siguiente.ToString("D4") : siguiente.ToString();
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCafeClienteRequest req)
    {
        var c = await _db.CafeClientes.FindAsync(id);
        if (c is null) return NotFound(new { error = "Cliente no encontrado" });
        if (req.Nombre is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "El nombre no puede ser vacio" });
            c.Nombre = req.Nombre.Trim();
        }
        if (req.RazonSocial is not null) c.RazonSocial = Norm(req.RazonSocial);
        if (req.Tipo is not null) c.Tipo = NormTipo(req.Tipo);
        if (req.Cuit is not null) c.Cuit = Norm(req.Cuit);
        if (req.Telefono is not null) c.Telefono = Norm(req.Telefono);
        if (req.Email is not null) c.Email = Norm(req.Email);
        if (req.Direccion is not null) c.Direccion = Norm(req.Direccion);
        if (req.Localidad is not null) c.Localidad = Norm(req.Localidad);
        if (req.Ciudad is not null) c.Ciudad = Norm(req.Ciudad);
        if (req.Cp is not null) c.Cp = Norm(req.Cp);
        if (req.CondicionIvaDefault is not null) c.CondicionIvaDefault = Norm(req.CondicionIvaDefault);
        if (req.DomicilioEntrega is not null) c.DomicilioEntrega = Norm(req.DomicilioEntrega);
        if (req.Notas is not null) c.Notas = Norm(req.Notas);
        if (req.ComentariosComprobante is not null) c.ComentariosComprobante = Norm(req.ComentariosComprobante);
        if (req.IsActive.HasValue) c.IsActive = req.IsActive.Value;
        // MapeoLink: si vino, actualizo. Si vino ClearMapeoLink, lo vacío.
        // Si el link cambió (o se agregó por primera vez), intentamos extraer coords del link de Google Maps.
        var linkPrevio = c.MapeoLink;
        if (req.MapeoLink is not null) c.MapeoLink = Norm(req.MapeoLink);
        else if (req.ClearMapeoLink) { c.MapeoLink = null; c.MapeoLat = null; c.MapeoLng = null; }
        if (!string.IsNullOrEmpty(c.MapeoLink) && c.MapeoLink != linkPrevio)
        {
            var coords = await _mapsResolver.TryResolverCoordenadasAsync(c.MapeoLink);
            if (coords.HasValue)
            {
                c.MapeoLat = coords.Value.lat;
                c.MapeoLng = coords.Value.lng;
            }
            // Si no se pudo resolver, mantenemos las coords previas (o null si nunca tuvo).
            // El usuario puede usar el botón "Re-extraer coords" después.
        }
        c.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(c));
    }

    /// <summary>Vuelve a resolver el MapeoLink del cliente y actualiza MapeoLat/Lng.
    /// Útil si la extracción inicial falló (Google rate-limit, formato extraño, etc.).</summary>
    [HttpPost("{id:int}/reextraer-coords")]
    public async Task<IActionResult> ReExtraerCoords(int id)
    {
        var c = await _db.CafeClientes.FindAsync(id);
        if (c is null) return NotFound(new { error = "Cliente no encontrado" });
        if (string.IsNullOrEmpty(c.MapeoLink))
            return BadRequest(new { error = "El cliente no tiene MapeoLink cargado." });
        var coords = await _mapsResolver.TryResolverCoordenadasAsync(c.MapeoLink);
        if (!coords.HasValue)
            return BadRequest(new { error = "No se pudieron extraer coordenadas del link. Probá con otro link o ingresá las coordenadas manualmente." });
        c.MapeoLat = coords.Value.lat;
        c.MapeoLng = coords.Value.lng;
        c.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(c));
    }

    /// <summary>Asigna un código interno correlativo al cliente. Si ya tiene uno, lo respeta.
    /// El correlativo se calcula como MAX(CodigoInterno actual) + 1.</summary>
    [HttpPost("{id:int}/asignar-codigo-interno")]
    public async Task<IActionResult> AsignarCodigoInterno(int id)
    {
        var c = await _db.CafeClientes.FindAsync(id);
        if (c is null) return NotFound(new { error = "Cliente no encontrado" });
        if (c.CodigoInterno.HasValue)
            return Ok(Map(c));   // ya tenía uno, lo respetamos
        var maxActual = await _db.CafeClientes
            .Where(x => x.CodigoInterno != null)
            .MaxAsync(x => (int?)x.CodigoInterno) ?? 0;
        c.CodigoInterno = maxActual + 1;
        c.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(c));
    }

    /// <summary>Saca el código interno (vuelve a null). Útil si el operador lo asignó por error.</summary>
    [HttpDelete("{id:int}/codigo-interno")]
    public async Task<IActionResult> QuitarCodigoInterno(int id)
    {
        var c = await _db.CafeClientes.FindAsync(id);
        if (c is null) return NotFound(new { error = "Cliente no encontrado" });
        c.CodigoInterno = null;
        c.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(c));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var c = await _db.CafeClientes.FindAsync(id);
        if (c is null) return NotFound(new { error = "Cliente no encontrado" });
        _db.CafeClientes.Remove(c);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    private static string NormTipo(string? t)
    {
        if (string.IsNullOrWhiteSpace(t)) return "OTRO";
        var v = t.Trim().ToUpperInvariant();
        return TiposValidos.Contains(v) ? v : "OTRO";
    }

    private static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
