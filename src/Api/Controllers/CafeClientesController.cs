using Api.Data;
using Api.DTOs;
using Api.Models;
using Api.Services;
using ClosedXML.Excel;
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
    private readonly CafeSaldosService _saldos;
    // 2026-08-21: "ALQUILERES" marca a los clientes del negocio de alquileres. Es solo una
    // etiqueta: para precios de productos el motor lo trata como OTRO (Comercial).
    private static readonly string[] TiposValidos = { "BAR", "OTRO", "ALQUILERES" };

    public CafeClientesController(AppDbContext db, GoogleMapsLinkResolverService mapsResolver, CafeSaldosService saldos)
    {
        _db = db;
        _mapsResolver = mapsResolver;
        _saldos = saldos;
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
        c.MapeoLat, c.MapeoLng,
        c.TieneMiniImpresora,
        c.SolicitarFirmaEntrega,
        c.Telefono2, c.EntreCalles,
        c.MeliBuyerId, c.MeliNickname,
        c.EnviarSiempreEmail, c.EnviarSiempreWhatsapp);

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

    /// <summary>2026-08-14: devuelve el cliente del sistema que ya está vinculado a un comprador de
    /// MercadoLibre (por su BuyerId), o null si ninguno lo tiene. Lo usa el buscador de la ficha para
    /// avisar "este usuario de ML ya está cargado como el cliente X" y no darlo de alta dos veces.</summary>
    [HttpGet("by-meli-buyer/{buyerId:long}")]
    public async Task<IActionResult> GetByMeliBuyer(long buyerId)
    {
        var c = await _db.CafeClientes.FirstOrDefaultAsync(x => x.MeliBuyerId == buyerId);
        return Ok(c is null ? null : Map(c));
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
        var dto = await GetEstadoCuentaAsync(id);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>2026-08-06: núcleo reutilizable del estado de cuenta (lo usa también el aviso de venta
    /// por WhatsApp para mandarle la cuenta corriente al interno). Devuelve null si el cliente no existe.</summary>
    public async Task<EstadoCuentaDto?> GetEstadoCuentaAsync(int id)
    {
        var cliente = await _db.CafeClientes.FindAsync(id);
        if (cliente is null) return null;

        // 2026-08-21: el armado de los movimientos vive en CafeSaldosService, la MISMA fórmula
        // que usa el panel "¿Quién me debe?". Antes acá había una cuenta propia que acreditaba
        // la cobranza entera al cliente que pagaba, aunque el pago cancelara facturas de una
        // sucursal hermana — por eso la ficha y el panel no coincidían.
        var movs = await _saldos.GetMovimientosClienteAsync(id);

        decimal acum = 0m;
        var result = new List<MovimientoCuentaDto>(movs.Count);
        foreach (var m in movs)
        {
            acum += m.Debe - m.Haber;
            result.Add(new MovimientoCuentaDto(m.Fecha, m.Tipo, m.Numero, m.Debe, m.Haber, acum, m.Detalle));
        }
        return new EstadoCuentaDto(id, cliente.Nombre, acum, result);
    }

    // 2026-08-05: ficha rápida del cliente para mostrar DENTRO del chat de WhatsApp (tarjeta
    // desplegable). Junta en una sola llamada: datos de contacto + link de Maps + saldo de cuenta
    // corriente + últimas N facturas/ventas con su estado (pagada / debe $X). Así el operador ve
    // todo sin salir de la conversación.
    public record FichaChatVentaDto(
        int Id, DateTime Fecha, string Numero, string? Tipo, decimal Total, decimal Pagado, decimal Saldo, string Estado);
    public record FichaChatDto(
        int ClienteId, string Nombre, string? RazonSocial, string? Cuit, string? CondicionIva,
        string? Telefono, string? Telefono2, string? Email, string? Direccion, string? Localidad,
        string? MapeoLink, string? Notas, string? ComentariosComprobante,
        int? CodigoInterno, decimal Saldo, List<FichaChatVentaDto> Ventas);

    [HttpGet("{id:int}/ficha-chat")]
    public async Task<IActionResult> FichaChat(int id, [FromQuery] int limitVentas = 8)
    {
        var c = await _db.CafeClientes.FindAsync(id);
        if (c is null) return NotFound(new { error = "Cliente no encontrado" });

        // 2026-08-21: ventas y saldo salen de CafeSaldosService, la misma fórmula del panel
        // "¿Quién me debe?" y de la ficha del cliente. Ya vienen con lo cobrado de cada venta,
        // las notas de crédito y los pagos a cuenta contemplados.
        var ventas = (await _saldos.GetVentasCuentaAsync(id))
            .OrderByDescending(v => v.Fecha).ThenByDescending(v => v.Id)
            .ToList();
        var saldo = await _saldos.GetSaldoClienteAsync(id);

        var limite = Math.Clamp(limitVentas, 1, 50);
        var ventasDto = ventas.Take(limite).Select(v =>
        {
            // En una Nota de Crédito (o en una factura ya anulada por NC) no tiene sentido
            // "debe/pagada": no hay nada que cobrar.
            var saldoV = (v.EsNotaCredito || v.AnuladaPorNc) ? 0m : v.Saldo;
            return new FichaChatVentaDto(v.Id, v.Fecha, v.Numero, v.TipoComprobante,
                v.Cobrable, v.Pagado, saldoV, "emitido");
        }).ToList();

        return Ok(new FichaChatDto(
            c.Id, c.Nombre, c.RazonSocial, c.Cuit, c.CondicionIvaDefault,
            c.Telefono, c.Telefono2, c.Email, c.Direccion, c.Localidad,
            c.MapeoLink, c.Notas, c.ComentariosComprobante,
            c.CodigoInterno, saldo, ventasDto));
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
            Telefono2 = Norm(req.Telefono2),
            Email = Norm(req.Email),
            Direccion = Norm(req.Direccion),
            EntreCalles = Norm(req.EntreCalles),
            Localidad = Norm(req.Localidad),
            Ciudad = Norm(req.Ciudad),
            Cp = Norm(req.Cp),
            CondicionIvaDefault = Norm(req.CondicionIvaDefault),
            DomicilioEntrega = Norm(req.DomicilioEntrega),
            Notas = Norm(req.Notas),
            ComentariosComprobante = Norm(req.ComentariosComprobante),
            MapeoLink = Norm(req.MapeoLink),
            MeliBuyerId = req.MeliBuyerId,
            MeliNickname = Norm(req.MeliNickname),
            EnviarSiempreEmail = req.EnviarSiempreEmail,
            EnviarSiempreWhatsapp = req.EnviarSiempreWhatsapp,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        // Si vino MapeoLink, intentamos resolverlo y guardar las coords automáticamente.
        if (!string.IsNullOrEmpty(c.MapeoLink))
        {
            var coords = await _mapsResolver.TryResolverCoordenadasAsync(c.MapeoLink);
            if (coords.HasValue) { c.MapeoLat = coords.Value.lat; c.MapeoLng = coords.Value.lng; }
        }
        // Si el frontend pre-asignó un código interno (con el botón "Asignar código" antes de guardar),
        // lo respetamos si está libre; si está tomado por otro cliente, asignamos el siguiente disponible
        // para no romper la carga (el frontend muestra el código real en el toast de éxito).
        if (req.CodigoInterno.HasValue && req.CodigoInterno.Value > 0)
        {
            var pedido = req.CodigoInterno.Value;
            var existe = await _db.CafeClientes.AnyAsync(x => x.CodigoInterno == pedido);
            if (!existe) { c.CodigoInterno = pedido; }
            else
            {
                var maxActual = await _db.CafeClientes
                    .Where(x => x.CodigoInterno != null)
                    .MaxAsync(x => (int?)x.CodigoInterno) ?? 0;
                c.CodigoInterno = maxActual + 1;
            }
        }
        _db.CafeClientes.Add(c);
        await _db.SaveChangesAsync();
        return Ok(Map(c));
    }

    /// <summary>Devuelve el próximo código interno disponible (MAX + 1) SIN asignarlo a ningún cliente.
    /// Lo usa el frontend cuando el usuario aprieta "Asignar código" antes de guardar un cliente nuevo:
    /// muestra el número que va a tener, y al guardar lo manda en el payload de Create.</summary>
    [HttpGet("next-codigo-interno")]
    public async Task<IActionResult> GetNextCodigoInterno()
    {
        var maxActual = await _db.CafeClientes
            .Where(x => x.CodigoInterno != null)
            .MaxAsync(x => (int?)x.CodigoInterno) ?? 0;
        return Ok(new { codigoInterno = maxActual + 1 });
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
        if (req.Telefono2 is not null) c.Telefono2 = Norm(req.Telefono2);
        if (req.Email is not null) c.Email = Norm(req.Email);
        if (req.Direccion is not null) c.Direccion = Norm(req.Direccion);
        if (req.EntreCalles is not null) c.EntreCalles = Norm(req.EntreCalles);
        if (req.Localidad is not null) c.Localidad = Norm(req.Localidad);
        if (req.Ciudad is not null) c.Ciudad = Norm(req.Ciudad);
        if (req.Cp is not null) c.Cp = Norm(req.Cp);
        if (req.CondicionIvaDefault is not null) c.CondicionIvaDefault = Norm(req.CondicionIvaDefault);
        if (req.DomicilioEntrega is not null) c.DomicilioEntrega = Norm(req.DomicilioEntrega);
        if (req.Notas is not null) c.Notas = Norm(req.Notas);
        if (req.ComentariosComprobante is not null) c.ComentariosComprobante = Norm(req.ComentariosComprobante);
        if (req.IsActive.HasValue) c.IsActive = req.IsActive.Value;
        if (req.TieneMiniImpresora.HasValue) c.TieneMiniImpresora = req.TieneMiniImpresora.Value;
        if (req.SolicitarFirmaEntrega.HasValue) c.SolicitarFirmaEntrega = req.SolicitarFirmaEntrega.Value;
        // 2026-08-20: tildes "mandarle siempre el comprobante" (mail / WhatsApp).
        if (req.EnviarSiempreEmail.HasValue) c.EnviarSiempreEmail = req.EnviarSiempreEmail.Value;
        if (req.EnviarSiempreWhatsapp.HasValue) c.EnviarSiempreWhatsapp = req.EnviarSiempreWhatsapp.Value;
        // 2026-08-14: vínculo con comprador de MercadoLibre. ClearMeliVinculo lo borra; si no,
        // un MeliBuyerId con valor lo setea/actualiza (junto con el nickname que venga).
        if (req.ClearMeliVinculo) { c.MeliBuyerId = null; c.MeliNickname = null; }
        else if (req.MeliBuyerId.HasValue) { c.MeliBuyerId = req.MeliBuyerId; c.MeliNickname = Norm(req.MeliNickname); }
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

        // 2026-06-19: cuando se editan datos del cliente, propagamos los nuevos valores
        // como SNAPSHOT en TODAS sus ventas historicas. SOLO sincronizamos IDENTIDAD
        // (Nombre, Tipo, Telefono, RazonSocial, Cuit) — son atributos del cliente entero,
        // no cambian segun la venta.
        // NO tocamos DOMICILIOS (Direccion, DomicilioEntrega, Localidad, Ciudad, Cp).
        // Esos son por venta: si una sucursal cambio de direccion entre ventas viejas
        // y la actual, perderiamos la trazabilidad de a donde se entrego cada cosa.
        // Caso real que motivo el fix: PANADERIA LA MILAGROSA con 2 sucursales — antes
        // era 1 cliente con domicilio editado entre ventas, ahora son 2 clientes
        // separados, pero las ventas viejas se mantienen con su domicilio historico.
        // El snapshot de la venta guarda SOLO el tipo que entiende el motor de precios
        // (BAR/OTRO): un cliente "ALQUILERES" cotiza como Comercial.
        var tipoParaVentas = CafePricingService.ResolverTipo(c.Tipo);
        await _db.CafeVentas.Where(v => v.ClienteId == c.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(v => v.ClienteNombreSnapshot, c.Nombre)
            .SetProperty(v => v.ClienteTipoSnapshot, tipoParaVentas)
            .SetProperty(v => v.ClienteTelefonoSnapshot, c.Telefono)
            .SetProperty(v => v.ClienteRazonSocialSnapshot, c.RazonSocial)
            .SetProperty(v => v.ClienteCuitSnapshot, c.Cuit));

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

    // ===== 2026-07-29: Direcciones de entrega múltiples por cliente =====
    private static CafeDireccionDto MapDir(CafeClienteDireccion d) => new(
        d.Id, d.ClienteId, d.Etiqueta, d.Direccion,
        d.EntreCalles, d.Localidad, d.Ciudad, d.Cp, d.Telefono,
        d.MapeoLink, d.MapeoLat, d.MapeoLng, d.EsPrincipal, d.IsActive, d.NotasInternas);

    /// <summary>Lista las direcciones de entrega de un cliente (la principal primero).</summary>
    [HttpGet("{id:int}/direcciones")]
    public async Task<IActionResult> GetDirecciones(int id)
    {
        var existe = await _db.CafeClientes.AnyAsync(c => c.Id == id);
        if (!existe) return NotFound(new { error = "Cliente no encontrado" });
        var dirs = await _db.CafeClienteDirecciones
            .Where(d => d.ClienteId == id && d.IsActive)
            .OrderByDescending(d => d.EsPrincipal).ThenBy(d => d.Etiqueta).ThenBy(d => d.Id)
            .ToListAsync();
        return Ok(dirs.Select(MapDir).ToList());
    }

    [HttpPost("{id:int}/direcciones")]
    public async Task<IActionResult> CrearDireccion(int id, [FromBody] CafeDireccionUpsertRequest req)
    {
        var cli = await _db.CafeClientes.FindAsync(id);
        if (cli is null) return NotFound(new { error = "Cliente no encontrado" });
        if (string.IsNullOrWhiteSpace(req.Direccion))
            return BadRequest(new { error = "La dirección es obligatoria" });
        var d = new CafeClienteDireccion
        {
            ClienteId = id,
            Etiqueta = Norm(req.Etiqueta),
            Direccion = req.Direccion.Trim(),
            EntreCalles = Norm(req.EntreCalles),
            NotasInternas = Norm(req.NotasInternas),
            Localidad = Norm(req.Localidad),
            Ciudad = Norm(req.Ciudad),
            Cp = Norm(req.Cp),
            Telefono = Norm(req.Telefono),
            MapeoLink = Norm(req.MapeoLink),
            EsPrincipal = req.EsPrincipal,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        if (!string.IsNullOrEmpty(d.MapeoLink))
        {
            var coords = await _mapsResolver.TryResolverCoordenadasAsync(d.MapeoLink);
            if (coords.HasValue) { d.MapeoLat = coords.Value.lat; d.MapeoLng = coords.Value.lng; }
        }
        // Los domicilios alternativos NUNCA son principales: el principal es siempre el
        // "domicilio de siempre" (Cafe_Clientes.DomicilioEntrega). Por eso no auto-marcamos nada.
        d.EsPrincipal = false;
        _db.CafeClienteDirecciones.Add(d);
        await _db.SaveChangesAsync();
        return Ok(MapDir(d));
    }

    [HttpPut("direcciones/{dirId:int}")]
    public async Task<IActionResult> EditarDireccion(int dirId, [FromBody] CafeDireccionUpsertRequest req)
    {
        var d = await _db.CafeClienteDirecciones.FindAsync(dirId);
        if (d is null || !d.IsActive) return NotFound(new { error = "Dirección no encontrada" });
        if (string.IsNullOrWhiteSpace(req.Direccion))
            return BadRequest(new { error = "La dirección es obligatoria" });
        var linkPrevio = d.MapeoLink;
        d.Etiqueta = Norm(req.Etiqueta);
        d.Direccion = req.Direccion.Trim();
        d.EntreCalles = Norm(req.EntreCalles);
        d.NotasInternas = Norm(req.NotasInternas);
        d.Localidad = Norm(req.Localidad);
        d.Ciudad = Norm(req.Ciudad);
        d.Cp = Norm(req.Cp);
        d.Telefono = Norm(req.Telefono);
        d.MapeoLink = Norm(req.MapeoLink);
        d.EsPrincipal = req.EsPrincipal;
        if (string.IsNullOrEmpty(d.MapeoLink)) { d.MapeoLat = null; d.MapeoLng = null; }
        else if (d.MapeoLink != linkPrevio)
        {
            var coords = await _mapsResolver.TryResolverCoordenadasAsync(d.MapeoLink);
            if (coords.HasValue) { d.MapeoLat = coords.Value.lat; d.MapeoLng = coords.Value.lng; }
        }
        d.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        if (d.EsPrincipal) await DesmarcarOtrasPrincipales(d.ClienteId, d.Id);
        return Ok(MapDir(d));
    }

    [HttpDelete("direcciones/{dirId:int}")]
    public async Task<IActionResult> BorrarDireccion(int dirId)
    {
        var d = await _db.CafeClienteDirecciones.FindAsync(dirId);
        if (d is null) return NotFound(new { error = "Dirección no encontrada" });
        _db.CafeClienteDirecciones.Remove(d);
        await _db.SaveChangesAsync();
        // Si borramos la principal y quedan otras, promovemos la primera a principal.
        if (d.EsPrincipal)
        {
            var otra = await _db.CafeClienteDirecciones
                .Where(x => x.ClienteId == d.ClienteId && x.IsActive)
                .OrderBy(x => x.Id).FirstOrDefaultAsync();
            if (otra is not null) { otra.EsPrincipal = true; await _db.SaveChangesAsync(); }
        }
        return Ok(new { ok = true });
    }

    /// <summary>Deja EsPrincipal=1 solo en la dirección indicada; apaga las demás del mismo cliente.</summary>
    private async Task DesmarcarOtrasPrincipales(int clienteId, int dirIdPrincipal)
    {
        await _db.CafeClienteDirecciones
            .Where(x => x.ClienteId == clienteId && x.Id != dirIdPrincipal && x.EsPrincipal)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.EsPrincipal, false));
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

        // Un cliente NO se puede borrar del todo si tiene movimientos historicos
        // (ventas, cobranzas o cheques): borrarlo rompe la trazabilidad de esos registros.
        // En ese caso lo "eliminamos" de forma suave: lo marcamos Inactivo y el frontend
        // lo oculta de la lista. Asi el usuario igual se lo saca de encima.
        var tieneVentas = await _db.CafeVentas.AnyAsync(v => v.ClienteId == id);
        var tieneCobranzas = await _db.CafeCobranzas.AnyAsync(x => x.ClienteId == id);
        var tieneCheques = await _db.CafeCheques.AnyAsync(x => x.ClienteOrigenId == id);

        if (!tieneVentas && !tieneCobranzas && !tieneCheques)
        {
            // No tiene nada enganchado (de lo conocido) -> intentamos el borrado real.
            _db.CafeClientes.Remove(c);
            try
            {
                await _db.SaveChangesAsync();
                return Ok(new { deleted = true });
            }
            catch (DbUpdateException)
            {
                // Quedaba algo enganchado que no chequeamos arriba (otra tabla con FK).
                // Limpiamos el intento fallido y caemos al borrado suave.
                _db.ChangeTracker.Clear();
                c = await _db.CafeClientes.FindAsync(id);
                if (c is null) return Ok(new { deleted = true });
            }
        }

        // Borrado suave: marcar Inactivo.
        var motivos = new List<string>();
        if (tieneVentas) motivos.Add("ventas");
        if (tieneCobranzas) motivos.Add("cobranzas");
        if (tieneCheques) motivos.Add("cheques");
        var detalle = motivos.Count > 0 ? string.Join(" y ", motivos) : "movimientos";

        c!.IsActive = false;
        c.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Conflict(new
        {
            softDeleted = true,
            error = $"No se puede borrar del todo: el cliente tiene {detalle} registradas en el sistema. " +
                    "Lo marcamos como Inactivo y lo sacamos de la lista."
        });
    }

    private static string NormTipo(string? t)
    {
        if (string.IsNullOrWhiteSpace(t)) return "OTRO";
        var v = t.Trim().ToUpperInvariant();
        return TiposValidos.Contains(v) ? v : "OTRO";
    }

    private static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ============================================================
    // Saldos pendientes — vista consolidada por cliente
    // ============================================================

    // ClienteSaldoPendienteDto se movió a Api.Services.CafeSaldosService (se reusa desde el aviso diario).

    // 2026-06-06: ventas ocasionales (sin cliente del catálogo) con saldo pendiente.
    public record VentaOcasionalSaldoDto(
        int VentaId,
        string Numero,
        DateTime Fecha,
        string ClienteNombreSnapshot,
        string? TipoComprobante,
        decimal Total,
        decimal Pagado,
        decimal Saldo,
        int DiasMora);

    /// <summary>Lista TODOS los clientes con saldo pendiente (deudores), agrupados.
    /// Saldo pendiente = SUM(ventas emitidas).Total - SUM(cobranzas vigentes asignadas a esas ventas).
    /// Las ventas creadas como "saldo de migración" del sistema viejo se incluyen igual (son ventas tipo X).
    /// Solo devuelve clientes con saldo > 0.</summary>
    // ─── Token publico del panel de saldos ─── (mismo patron que nominas/panel)
    private const string ClientesPanelTokenKey = "clientes.panel.public_token";

    private async Task<string> GetOrCreateClientesPanelTokenAsync()
    {
        var existing = await _db.AppSettings.FindAsync(ClientesPanelTokenKey);
        if (existing is not null && !string.IsNullOrEmpty(existing.Value)) return existing.Value;
        var token = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .Replace("/", "_").Replace("+", "-").TrimEnd('=');
        if (existing is null) _db.AppSettings.Add(new AppSetting { Key = ClientesPanelTokenKey, Value = token });
        else existing.Value = token;
        await _db.SaveChangesAsync();
        return token;
    }

    [HttpGet("saldos-pendientes/public-token")]
    public async Task<IActionResult> GetClientesPanelPublicToken()
    {
        var token = await GetOrCreateClientesPanelTokenAsync();
        return Ok(new { token });
    }

    [HttpPost("saldos-pendientes/public-token/regenerate")]
    public async Task<IActionResult> RegenerateClientesPanelPublicToken()
    {
        var existing = await _db.AppSettings.FindAsync(ClientesPanelTokenKey);
        var nuevo = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .Replace("/", "_").Replace("+", "-").TrimEnd('=');
        if (existing is null) _db.AppSettings.Add(new AppSetting { Key = ClientesPanelTokenKey, Value = nuevo });
        else existing.Value = nuevo;
        await _db.SaveChangesAsync();
        return Ok(new { token = nuevo });
    }

    [HttpGet("saldos-pendientes/publica/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetSaldosPendientesPublic(string token)
    {
        var saved = await _db.AppSettings.FindAsync(ClientesPanelTokenKey);
        if (saved is null || string.IsNullOrEmpty(saved.Value) || saved.Value != token) return NotFound();
        return await GetSaldosPendientes();
    }

    [HttpGet("saldos-pendientes")]
    public async Task<IActionResult> GetSaldosPendientes()
        => Ok(await _saldos.GetSaldosPendientesAsync());

    /// <summary>Manda AHORA por Telegram el resumen de deudas por cliente (el mismo que sale
    /// automático cada mañana). Sirve para probarlo sin esperar a las 8am.</summary>
    [HttpPost("deudores-diario/enviar-ahora")]
    public async Task<IActionResult> EnviarDeudoresAhora([FromServices] DeudoresDiarioNotifier notifier, CancellationToken ct)
        => Ok(await notifier.EnviarResumenAsync(ct));

    /// <summary>2026-06-06: Lista las ventas "ocasionales" (sin cliente del catálogo) con saldo pendiente.
    /// Estas ventas se cargan sin cliente para no llenar el catálogo con clientes de una sola compra,
    /// pero igual hay que poder cobrarlas y verlas como deuda.
    /// Saldo = Total venta - cobranzas vigentes imputadas a esa venta.</summary>
    [HttpGet("saldos-ocasionales")]
    public async Task<IActionResult> GetSaldosOcasionales()
    {
        // 2026-08-21: mismo motor que el resto (CafeSaldosService): ventas sin cliente del
        // catálogo, no anuladas, sin presupuestos, con lo cobrado ya descontado.
        var ventas = await _saldos.GetVentasCuentaAsync(soloSinCliente: true);
        if (ventas.Count == 0) return Ok(new List<VentaOcasionalSaldoDto>());

        var hoy = DateTime.UtcNow.AddHours(-3).Date;
        var result = ventas
            .Where(v => v.Pendiente) // deja afuera NC y facturas anuladas por NC
            .Select(v => new VentaOcasionalSaldoDto(
                v.Id,
                v.Numero,
                v.Fecha,
                string.IsNullOrWhiteSpace(v.ClienteNombreSnapshot) ? "(sin nombre)" : v.ClienteNombreSnapshot!,
                v.TipoComprobante,
                v.Cobrable,
                v.Pagado,
                v.Saldo,
                (int)(hoy - v.Fecha.Date).TotalDays))
            .OrderBy(x => x.Fecha) // más antigua primero (mayor urgencia)
            .ToList();
        return Ok(result);
    }

    public class ExportSaldosRequest
    {
        /// <summary>Si está vacío, exporta TODOS los clientes con saldo. Si vienen ids, exporta solo esos.</summary>
        public List<int>? ClienteIds { get; set; }
    }

    /// <summary>Exporta las cuentas corrientes de los clientes seleccionados (o todos los deudores)
    /// a un Excel. Hoja 1 con el resumen + 1 hoja por cada cliente con sus comprobantes pendientes.</summary>
    [HttpPost("saldos-pendientes/excel")]
    public async Task<IActionResult> ExportSaldosExcel([FromBody] ExportSaldosRequest req)
    {
        // 2026-08-21: los números salen de CafeSaldosService (misma fórmula que el panel
        // "¿Quién me debe?"): ya vienen con notas de crédito, pagos a cuenta y saldos a favor
        // descontados. Antes el Excel tenía su propia cuenta y hasta contaba los presupuestos.
        var deudores = await _saldos.GetSaldosPendientesAsync();
        if (req.ClienteIds is not null && req.ClienteIds.Count > 0)
            deudores = deudores.Where(d => req.ClienteIds.Contains(d.ClienteId)).ToList();
        if (deudores.Count == 0)
            return BadRequest(new { error = "No hay clientes con saldo pendiente que coincidan" });

        var clienteIds = deudores.Select(d => d.ClienteId).ToList();
        // Comprobantes que quedan pendientes de cobro, para el detalle de cada hoja.
        var ventasConSaldo = (await _saldos.GetVentasCuentaAsync())
            .Where(v => v.Pendiente && v.ClienteId.HasValue && clienteIds.Contains(v.ClienteId.Value))
            .ToList();

        var clientes = await _db.CafeClientes.Where(c => clienteIds.Contains(c.Id)).ToListAsync();
        var clientesDict = clientes.ToDictionary(c => c.Id);

        var hoy = DateTime.UtcNow.AddHours(-3).Date;
        var esCulture = new System.Globalization.CultureInfo("es-AR");

        using var wb = new XLWorkbook();

        // ===== HOJA 1: RESUMEN =====
        var ws = wb.Worksheets.Add("Resumen");
        ws.Cell(1, 1).Value = "Saldos pendientes de clientes";
        ws.Range(1, 1, 1, 7).Merge().Style.Font.SetBold(true).Font.SetFontSize(14);
        ws.Cell(2, 1).Value = $"Generado: {hoy:dd/MM/yyyy}";
        ws.Range(2, 1, 2, 7).Merge().Style.Font.SetItalic(true).Font.SetFontColor(XLColor.DarkGray);

        // 10 columnas — pedido del usuario 2026-05-19: separar saldo "Cotizacion" (X/PRO) de
        // saldo "Factura" (FA/FB/FC), ademas del saldo total. 2026-08-21: se agrega "A favor"
        // (pagos a cuenta + notas de credito + facturas pagadas de mas), para que se entienda
        // por que el total no es la simple suma de cotizacion + factura.
        var headers = new[] { "Cliente", "Tipo", "Teléfono", "N° pendientes", "Días vencido",
            "Más antigua", "📝 Saldo Cotización (X)", "📋 Saldo Factura (A/B/C)", "💚 A favor / a cuenta", "Saldo total" };
        for (int i = 0; i < headers.Length; i++)
        {
            var c = ws.Cell(4, i + 1);
            c.Value = headers[i];
            c.Style.Font.SetBold(true);
            c.Style.Fill.SetBackgroundColor(XLColor.LightGray);
            c.Style.Border.SetBottomBorder(XLBorderStyleValues.Thin);
        }

        var resumen = deudores
            .Select(d => new {
                d.ClienteId,
                Cliente = clientesDict.TryGetValue(d.ClienteId, out var c) ? c : null,
                Cantidad = d.CantidadVentasPendientes,
                Saldo = d.SaldoPendiente,
                SaldoCotizacion = d.SaldoCotizacion,
                SaldoFactura = d.SaldoFactura,
                Credito = d.CreditoAFavor,
                FechaMasAntigua = d.FechaMasAntigua
            })
            .OrderBy(x => x.FechaMasAntigua)
            .ToList();

        int row = 5;
        foreach (var r in resumen)
        {
            ws.Cell(row, 1).Value = r.Cliente?.Nombre ?? "(sin nombre)";
            ws.Cell(row, 2).Value = r.Cliente?.Tipo ?? "OTRO";
            ws.Cell(row, 3).Value = r.Cliente?.Telefono ?? "";
            ws.Cell(row, 4).Value = r.Cantidad;
            ws.Cell(row, 5).Value = (int)(hoy - r.FechaMasAntigua.Date).TotalDays;
            ws.Cell(row, 6).Value = r.FechaMasAntigua; ws.Cell(row, 6).Style.DateFormat.Format = "dd/MM/yyyy";
            ws.Cell(row, 7).Value = r.SaldoCotizacion; ws.Cell(row, 7).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 8).Value = r.SaldoFactura; ws.Cell(row, 8).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 9).Value = r.Credito; ws.Cell(row, 9).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 10).Value = r.Saldo; ws.Cell(row, 10).Style.NumberFormat.Format = "#,##0.00";
            row++;
        }
        // Fila TOTAL
        ws.Cell(row, 1).Value = "TOTAL";
        ws.Cell(row, 1).Style.Font.SetBold(true);
        ws.Range(row, 1, row, 6).Merge().Style.Font.SetBold(true);
        ws.Cell(row, 7).Value = resumen.Sum(r => r.SaldoCotizacion);
        ws.Cell(row, 7).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(row, 7).Style.Font.SetBold(true);
        ws.Cell(row, 8).Value = resumen.Sum(r => r.SaldoFactura);
        ws.Cell(row, 8).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(row, 8).Style.Font.SetBold(true);
        ws.Cell(row, 9).Value = resumen.Sum(r => r.Credito);
        ws.Cell(row, 9).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(row, 9).Style.Font.SetBold(true);
        ws.Cell(row, 10).Value = resumen.Sum(r => r.Saldo);
        ws.Cell(row, 10).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(row, 10).Style.Font.SetBold(true);
        ws.Range(row, 1, row, 10).Style.Fill.SetBackgroundColor(XLColor.LightYellow);

        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(4);

        // ===== UNA HOJA POR CADA CLIENTE =====
        foreach (var r in resumen)
        {
            // Sanitizar nombre de la hoja (Excel no permite ciertos chars, max 31 chars)
            var sheetName = SanitizeSheetName(r.Cliente?.Nombre ?? $"Cliente {r.ClienteId}");
            // Evitar duplicados (puede haber 2 clientes con mismo nombre truncado)
            var sName = sheetName;
            int n = 2;
            while (wb.Worksheets.Any(x => x.Name == sName)) { sName = sheetName.Substring(0, Math.Min(sheetName.Length, 28)) + $"({n++})"; }
            var ws2 = wb.Worksheets.Add(sName);

            ws2.Cell(1, 1).Value = r.Cliente?.Nombre ?? "?";
            ws2.Range(1, 1, 1, 6).Merge().Style.Font.SetBold(true).Font.SetFontSize(13);
            if (r.Cliente is not null)
            {
                int infoRow = 2;
                if (!string.IsNullOrEmpty(r.Cliente.Cuit))
                {
                    ws2.Cell(infoRow, 1).Value = $"CUIT/DNI: {r.Cliente.Cuit}";
                    ws2.Range(infoRow, 1, infoRow, 6).Merge();
                    infoRow++;
                }
                if (!string.IsNullOrEmpty(r.Cliente.Telefono))
                {
                    ws2.Cell(infoRow, 1).Value = $"Teléfono: {r.Cliente.Telefono}";
                    ws2.Range(infoRow, 1, infoRow, 6).Merge();
                    infoRow++;
                }
                if (!string.IsNullOrEmpty(r.Cliente.DomicilioEntrega ?? r.Cliente.Direccion))
                {
                    ws2.Cell(infoRow, 1).Value = $"Dirección: {r.Cliente.DomicilioEntrega ?? r.Cliente.Direccion}";
                    ws2.Range(infoRow, 1, infoRow, 6).Merge();
                }
            }

            var detHeaders = new[] { "N° comprobante", "Fecha", "Tipo", "Total", "Cobrado", "Saldo" };
            int hRow = 6;
            for (int i = 0; i < detHeaders.Length; i++)
            {
                var c = ws2.Cell(hRow, i + 1);
                c.Value = detHeaders[i];
                c.Style.Font.SetBold(true);
                c.Style.Fill.SetBackgroundColor(XLColor.LightGray);
            }
            int dRow = hRow + 1;
            var itemsCliente = ventasConSaldo.Where(v => v.ClienteId == r.ClienteId).OrderBy(v => v.Fecha).ToList();
            foreach (var v in itemsCliente)
            {
                ws2.Cell(dRow, 1).Value = v.Numero;
                ws2.Cell(dRow, 2).Value = v.Fecha; ws2.Cell(dRow, 2).Style.DateFormat.Format = "dd/MM/yyyy";
                ws2.Cell(dRow, 3).Value = v.TipoComprobante;
                ws2.Cell(dRow, 4).Value = v.Cobrable; ws2.Cell(dRow, 4).Style.NumberFormat.Format = "#,##0.00";
                ws2.Cell(dRow, 5).Value = v.Pagado; ws2.Cell(dRow, 5).Style.NumberFormat.Format = "#,##0.00";
                ws2.Cell(dRow, 6).Value = v.Saldo; ws2.Cell(dRow, 6).Style.NumberFormat.Format = "#,##0.00";
                dRow++;
            }
            // 2026-08-21: plata del cliente que no esta aplicada a estos comprobantes (pagos a
            // cuenta, notas de credito, facturas pagadas de mas). Se resta del total adeudado.
            if (r.Credito > 0.50m)
            {
                ws2.Cell(dRow, 1).Value = "A favor / a cuenta (se descuenta)";
                ws2.Range(dRow, 1, dRow, 5).Merge().Style.Font.SetItalic(true);
                ws2.Cell(dRow, 6).Value = -r.Credito;
                ws2.Cell(dRow, 6).Style.NumberFormat.Format = "#,##0.00";
                ws2.Cell(dRow, 6).Style.Font.SetFontColor(XLColor.Green);
                dRow++;
            }
            // Fila TOTAL
            ws2.Cell(dRow, 1).Value = "TOTAL ADEUDADO";
            ws2.Range(dRow, 1, dRow, 5).Merge().Style.Font.SetBold(true);
            ws2.Cell(dRow, 6).Value = r.Saldo;
            ws2.Cell(dRow, 6).Style.NumberFormat.Format = "#,##0.00";
            ws2.Cell(dRow, 6).Style.Font.SetBold(true);
            ws2.Range(dRow, 1, dRow, 6).Style.Fill.SetBackgroundColor(XLColor.LightYellow);

            ws2.Columns().AdjustToContents();
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var bytes = ms.ToArray();
        var filename = $"saldos-pendientes_{hoy:yyyyMMdd}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", filename);
    }

    /// <summary>Sanitiza un nombre de cliente para usarlo como nombre de hoja Excel:
    /// max 31 chars, sin / \ ? * [ ].</summary>
    private static string SanitizeSheetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Cliente";
        foreach (var bad in new[] { '/', '\\', '?', '*', '[', ']', ':' })
            name = name.Replace(bad, '-');
        if (name.Length > 31) name = name.Substring(0, 31);
        return name.Trim();
    }
}
