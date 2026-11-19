using Api.Data;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Api.Controllers;

/// <summary>
/// 18/11/2026 — "Facturas recibidas": las facturas que nos hacen los proveedores (las trae de ARCA
/// <see cref="ComprasAfipRobot"/> todos los días a las 7). Las que entraron después de la última vez
/// que el dueño abrió la lista son "nuevas" (tarjeta Compras del inicio y campanita). Solo admin.
/// Maqueta aprobada: scratchpad/Compras_e_Impuestos_inicio_PROPUESTA.png
/// </summary>
[ApiController]
[Route("api/compras/facturas-recibidas")]
[Authorize(Roles = "admin")]
public class FacturasRecibidasController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ComprasAfipRobot _robot;
    private readonly ProveedorCtaCteService _ctacte;
    private readonly ComprasPagosOrigenService _origen;

    public FacturasRecibidasController(AppDbContext db, ComprasAfipRobot robot, ProveedorCtaCteService ctacte, ComprasPagosOrigenService origen)
    { _db = db; _robot = robot; _ctacte = ctacte; _origen = origen; }

    public record FacturaDto(string Id, DateTime Llego, DateTime? Fecha, string Proveedor, string? Cuit, int? ProveedorId,
        string Tipo, string Numero, decimal Total, bool EsNotaCredito, bool TienePdf, bool Nueva, string? Pago,
        // 18/11/2026 (2ª): categoría del proveedor, comentario, por dónde se pagó, sugerencia y si lleva cta cte.
        string? Categoria = null, string? Comentario = null, string? PagoVia = null, SugerenciaDto? Sugerencia = null,
        bool CuentaCorriente = false, string? ReglaPago = null, bool PagoManual = false,
        // 18/11/2026 (5ª): pagada encontrada sola (CUIT en la transferencia del Galicia, débito automático, compra MeLi).
        // Clave para deshacerla ("BCO:123" / "ORD:2000…"); null si no fue automática.
        string? PagoAuto = null);
    /// <summary>Un pago que PUEDE ser de esta factura (mismo importe, fechas cerca). Origen MP o BCO.</summary>
    public record SugerenciaDto(string Origen, long MovId, DateTime Fecha, decimal Importe, string Texto);
    public record PorProveedorDto(string Nombre, int Cantidad, decimal Total);
    public record EstadoDto(DateTime? Ultima, string? Resultado, bool Corriendo, DateTime? VistasHasta);
    public record ResumenDto(int Nuevas, decimal TotalNuevas, List<PorProveedorDto> PorProveedor, EstadoDto Estado);

    /// <summary>"Vistas hasta": la primera vez arranca ahora (lo que ya había no es "nuevo").</summary>
    private async Task<DateTime> VistasHastaAsync()
    {
        var v = ComprasAfipRobot.ParseUtc(await ComprasAfipRobot.LeerAsync(_db, ComprasAfipRobot.KeyVistas));
        if (v.HasValue) return v.Value;
        var ahora = DateTime.UtcNow;
        await ComprasAfipRobot.GuardarAsync(_db, ComprasAfipRobot.KeyVistas, ahora.ToString("o"));
        return ahora;
    }

    private async Task<EstadoDto> EstadoAsync(DateTime vistas) => new(
        ComprasAfipRobot.ParseUtc(await ComprasAfipRobot.LeerAsync(_db, ComprasAfipRobot.KeyUltima)),
        await ComprasAfipRobot.LeerAsync(_db, ComprasAfipRobot.KeyResultado),
        ComprasAfipRobot.Corriendo, vistas);

    [HttpGet("resumen")]
    public async Task<IActionResult> Resumen()
    {
        var vistas = await VistasHastaAsync();
        var nuevas = await _db.ContadoraComprobantes.AsNoTracking()
            .Where(c => c.Naturaleza == "COMPRA" && c.PrimeraVezEn > vistas && !c.EsNotaCredito)
            .Select(c => new { c.ReceptorNombre, c.ReceptorDoc, c.Total })
            .ToListAsync();
        var porProv = nuevas.GroupBy(n => n.ReceptorDoc ?? n.ReceptorNombre ?? "?")
            .Select(g => new PorProveedorDto(Lindo(g.First().ReceptorNombre), g.Count(), g.Sum(x => x.Total)))
            .OrderByDescending(x => x.Total).ToList();
        return Ok(new ResumenDto(nuevas.Count, nuevas.Sum(n => n.Total), porProv, await EstadoAsync(vistas)));
    }

    [HttpGet("estado")]
    public async Task<IActionResult> Estado() => Ok(await EstadoAsync(await VistasHastaAsync()));

    /// <summary>Por defecto, lo que llegó en los últimos 60 días. mes=yyyy-MM filtra por fecha de la factura.
    /// 18/11/2026: desde/hasta=yyyy-MM-dd (fecha de la factura) para elegir fechas a mano.</summary>
    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] string? mes, [FromQuery] string? q, [FromQuery] DateTime? desde, [FromQuery] DateTime? hasta)
    {
        var vistas = await VistasHastaAsync();
        var qq = _db.ContadoraComprobantes.AsNoTracking().Where(c => c.Naturaleza == "COMPRA");
        if (desde.HasValue || hasta.HasValue)
        {
            var d0 = (desde ?? new DateTime(2000, 1, 1)).Date;
            var d1 = (hasta ?? DateTime.UtcNow.AddHours(-3)).Date.AddDays(1);
            qq = qq.Where(c => c.FechaEmision >= d0 && c.FechaEmision < d1);
        }
        else if (!string.IsNullOrWhiteSpace(mes) && DateTime.TryParse(mes + "-01", out var ini))
        {
            var fin = ini.AddMonths(1);
            qq = qq.Where(c => c.FechaEmision >= ini && c.FechaEmision < fin);
        }
        else
        {
            var hace60 = DateTime.UtcNow.AddDays(-60);
            qq = qq.Where(c => c.PrimeraVezEn >= hace60 || c.FechaEmision >= hace60);
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim();
            qq = qq.Where(c => (c.ReceptorNombre != null && c.ReceptorNombre.Contains(t)) || (c.ReceptorDoc != null && c.ReceptorDoc.Contains(t)));
        }
        var filas = await qq.OrderByDescending(c => c.PrimeraVezEn).ThenByDescending(c => c.FechaEmision).Take(3000)
            .Select(c => new
            {
                c.IdComprobante, c.PrimeraVezEn, c.FechaEmision, c.ReceptorNombre, c.ReceptorDoc, c.TipoComprobante,
                c.PuntoVenta, c.NumeroComprobante, c.Total, c.EsNotaCredito, TienePdf = c.PdfPath != null
            })
            .ToListAsync();

        // Proveedor del sistema por CUIT y, si lleva cuenta corriente, cuánto le queda de cada factura.
        var cuits = filas.Where(f => f.ReceptorDoc != null).Select(f => f.ReceptorDoc!).Distinct().ToList();
        var provs = (await _db.CafeProveedores.AsNoTracking().Where(p => p.Cuit != null && cuits.Contains(p.Cuit))
                .Select(p => new { p.Id, p.Cuit, p.CuentaCorriente }).ToListAsync())
            .GroupBy(p => p.Cuit!).ToDictionary(g => g.Key, g => g.First());
        var conCta = provs.Values.Where(p => p.CuentaCorriente).Select(p => p.Id).ToList();
        var saldos = new Dictionary<string, decimal>();
        if (conCta.Count > 0)
            foreach (var c in (await _ctacte.CalcularAsync(conCta)).Values)
                foreach (var d in c.Docs.Where(d => d.AfipIdComprobante != null))
                    saldos[d.AfipIdComprobante!] = d.Saldo;

        // 18/11/2026 (2ª): lo que se anotó por proveedor (categoría, cómo se paga) y por factura.
        var cuitsTodos = filas.Where(f => f.ReceptorDoc != null).Select(f => f.ReceptorDoc!).Distinct().ToList();
        var cfgs = await _db.ComprasProveedorConfigs.AsNoTracking().Where(c => cuitsTodos.Contains(c.Cuit)).ToDictionaryAsync(c => c.Cuit);
        var ids = filas.Select(f => f.IdComprobante).ToList();
        var extras = await _db.ComprasFacturaExtras.AsNoTracking().Where(e => ids.Contains(e.IdComprobante)).ToDictionaryAsync(e => e.IdComprobante);

        // 19/11/2026: pagos de Tesorería a facturas de proveedores SIN cuenta corriente (💸 Pagar / desde el banco).
        var pagosTes = (await _db.CafePagosProveedorComprobantes.AsNoTracking()
                .Where(c => c.AfipIdComprobante != null && ids.Contains(c.AfipIdComprobante) && c.Pago!.Estado == "VIGENTE")
                .Select(c => new { c.AfipIdComprobante, c.Importe, c.Pago!.Numero, c.Pago.Fecha, c.Pago.ExtractoMovId,
                    Caja = c.Pago.Medios.Select(m => m.Caja!.Nombre).FirstOrDefault() })
                .ToListAsync())
            .GroupBy(x => x.AfipIdComprobante!).ToDictionary(g => g.Key, g => g.ToList());

        var ar = System.Globalization.CultureInfo.GetCultureInfo("es-AR");
        var res = filas.Select(f =>
        {
            var p = f.ReceptorDoc != null && provs.TryGetValue(f.ReceptorDoc, out var x) ? x : null;
            var cfg = f.ReceptorDoc != null && cfgs.TryGetValue(f.ReceptorDoc, out var cc) ? cc : null;
            extras.TryGetValue(f.IdComprobante, out var ex);
            string? pago = null, via = null;
            var manual = false;
            if (p?.CuentaCorriente == true && !f.EsNotaCredito)
            {
                pago = saldos.TryGetValue(f.IdComprobante, out var s)
                    ? (s <= 0.01m ? "pagada" : s < f.Total - 0.01m ? "debe " + s.ToString("$#,0", ar) : "a pagar")
                    : "anterior a la cuenta corriente";
                via = "cuenta corriente";
            }
            else if (!f.EsNotaCredito && pagosTes.TryGetValue(f.IdComprobante, out var pts))
            {
                var pagadoTes = pts.Sum(x => x.Importe);
                var u = pts.OrderByDescending(x => x.Fecha).First();
                pago = pagadoTes >= f.Total - 0.01m ? "pagada" : "debe " + (f.Total - pagadoTes).ToString("$#,0", ar);
                via = $"Pago {u.Numero} · {(u.ExtractoMovId != null ? "transferencia " : "")}{u.Caja ?? "Tesorería"} {u.Fecha.AddHours(-3):dd/MM}";
            }
            else if (!f.EsNotaCredito && ex?.PagoMedio != null)
            {
                pago = "pagada"; manual = true;
                via = ex.PagoMedio + (ex.PagoFecha is DateTime pf ? " · " + pf.ToString("dd/MM") : "");
            }
            else if (!f.EsNotaCredito && cfg?.ReglaPago != null)
            {
                pago = "pagada"; via = TextoRegla(cfg.ReglaPago);
            }
            else if (!f.EsNotaCredito && (f.ReceptorDoc == CuitMercadoLibre || f.ReceptorDoc == CuitMeliLog))
            {
                pago = "pagada"; via = "descontada por Mercado Pago";
            }
            return new FacturaDto(f.IdComprobante, f.PrimeraVezEn, f.FechaEmision, Lindo(f.ReceptorNombre), f.ReceptorDoc, p?.Id,
                string.IsNullOrWhiteSpace(f.TipoComprobante) ? "Factura" : f.TipoComprobante!,
                ProveedorCtaCteService.NumeroAfip(f.PuntoVenta, f.NumeroComprobante),
                f.EsNotaCredito ? -f.Total : f.Total, f.EsNotaCredito, f.TienePdf, f.PrimeraVezEn > vistas, pago,
                cfg?.Categoria, ex?.Comentario, via, null, p?.CuentaCorriente == true, cfg?.ReglaPago, manual);
        }).ToList();

        // Sugerencias de pago para las que no se sabe: Mercado Pago y banco Galicia.
        var sinSaber = res.Where(r => r.Pago == null && !r.EsNotaCredito && r.Fecha != null).ToList();
        if (sinSaber.Count > 0)
        {
            // 18/11/2026 (5ª): primero lo SEGURO (CUIT del Galicia, débito automático, compra MeLi), después sugerencias.
            var (auto, sug) = await EmparejarAsync(sinSaber, extras);
            res = res.Select(r => auto.TryGetValue(r.Id, out var a) ? r with { Pago = "pagada", PagoVia = a.Via, PagoAuto = a.Clave }
                                : sug.TryGetValue(r.Id, out var sg) ? r with { Sugerencia = sg } : r).ToList();
        }
        return Ok(res);
    }

    /// <summary>19/11/2026: pagos del Galicia a proveedores que el sistema reconoció en "Facturas recibidas" pero
    /// todavía no están cargados como pago (los del botón 🏦 Registrar el pago). Para la campanita y el numerito
    /// del botón. Mismo cálculo que la lista (últimos 60 días); queda 3 min en memoria.</summary>
    public const string KeyAvisoBanco = "fr-aviso-banco", KeyAvisoCruce = "fr-aviso-cruce";
    public record AvisoBancoDto(int Movimientos, decimal Total, List<string> Proveedores, DateTime? Ultimo);
    public async Task<AvisoBancoDto> PagosBancoSinRegistrarAsync()
    {
        var cache = HttpContext.RequestServices.GetRequiredService<IMemoryCache>();
        if (cache.TryGetValue(KeyAvisoBanco, out AvisoBancoDto? c) && c is not null) return c;
        var r = await Listar(null, null, null, null);
        var filas = (r as OkObjectResult)?.Value as List<FacturaDto> ?? new();
        var grupos = filas.Where(f => !f.EsNotaCredito && f.PagoAuto != null && f.PagoAuto.StartsWith("BCO:"))
            .GroupBy(f => f.PagoAuto!).ToList();
        var res = new AvisoBancoDto(grupos.Count, grupos.Sum(g => g.Sum(f => f.Total)),
            grupos.Select(g => g.First().Proveedor).Distinct().ToList(),
            grupos.Count == 0 ? null : grupos.Max(g => g.Max(f => f.Fecha ?? f.Llego)));
        cache.Set(KeyAvisoBanco, res, TimeSpan.FromMinutes(3));
        return res;
    }

    /// <summary>Abrió la lista: lo que hay deja de ser "nuevo".</summary>
    [HttpPost("marcar-vistas")]
    public async Task<IActionResult> MarcarVistas()
    {
        await ComprasAfipRobot.GuardarAsync(_db, ComprasAfipRobot.KeyVistas, DateTime.UtcNow.ToString("o"));
        return Ok(new { ok = true });
    }

    /// <summary>19/11/2026: todos los que nos facturaron en el año pasan a ser proveedores (se crean los que faltan,
    /// con nombre y CUIT de ARCA). Lo llama la pantalla al abrir; no hace nada si no falta ninguno.</summary>
    [HttpPost("proveedores-faltantes")]
    public async Task<IActionResult> ProveedoresFaltantes() => Ok(new { creados = await _ctacte.CrearFaltantesDesdeArcaAsync() });

    /// <summary>19/11/2026: proveedor de la factura para "💸 Pagar" (si no estaba cargado, se crea).</summary>
    [HttpPost("{id}/proveedor")]
    public async Task<IActionResult> ProveedorDeFactura(string id)
    {
        var f = await _db.ContadoraComprobantes.AsNoTracking().Where(c => c.IdComprobante == id && c.Naturaleza == "COMPRA")
            .Select(c => new { c.ReceptorDoc, c.ReceptorNombre }).FirstOrDefaultAsync();
        if (f is null) return NotFound(new { error = "Factura no encontrada" });
        var p = await _ctacte.AsegurarProveedorAsync(f.ReceptorDoc, f.ReceptorNombre);
        if (p is null) return BadRequest(new { error = "La factura no trae un CUIT válido" });
        return Ok(new { proveedorId = p.Id });
    }

    /// <summary>"↻ Traer ahora": entra a ARCA en segundo plano (1–3 minutos).</summary>
    [HttpPost("traer-ahora")]
    public IActionResult TraerAhora()
    {
        var op = Request.Headers["X-Operator-Name"].FirstOrDefault();
        var iniciado = _robot.Iniciar(string.IsNullOrWhiteSpace(op) ? "a mano" : $"a mano ({op.Trim()})");
        return Ok(new { iniciado, corriendo = true });
    }

    // ═══════════ 18/11/2026 (2ª): categorías, comentarios y pagos ═══════════

    public const string CuitMercadoLibre = "30703088534";
    public const string CuitMeliLog = "30715686054";   // envíos de MeLi: también se descuentan de lo cobrado en MP
    public static readonly string[] CategoriasBase =
        { "Mercadería", "Insumos", "Servicios", "Seguros", "Peajes", "Comisiones MeLi/MP", "Envíos", "Mantenimiento", "Impuestos y bancos", "Otros" };

    private static string TextoRegla(string r) => r switch
    {
        "DEBITO" => "débito automático (regla del proveedor)",
        "TARJETA" => "con tarjeta (regla del proveedor)",
        "MP" => "por Mercado Pago (regla del proveedor)",
        "EFECTIVO" => "en efectivo (regla del proveedor)",
        _ => r,
    };

    private string Quien() => Request.Headers["X-Operator-Name"].FirstOrDefault() is { Length: > 0 } op ? op.Trim() : (User.Identity?.Name ?? "?");

    [HttpGet("categorias")]
    public async Task<IActionResult> Categorias()
    {
        var usadas = await _db.ComprasProveedorConfigs.AsNoTracking().Where(c => c.Categoria != null).Select(c => c.Categoria!).Distinct().ToListAsync();
        return Ok(CategoriasBase.Concat(usadas.Where(u => !CategoriasBase.Contains(u)).OrderBy(u => u)).ToList());
    }

    public record CategoriaReq(string? Categoria);
    /// <summary>La categoría es del PROVEEDOR: vale para todas sus facturas (las de antes y las que vengan).</summary>
    [HttpPut("proveedor/{cuit}/categoria")]
    public async Task<IActionResult> PonerCategoria(string cuit, [FromBody] CategoriaReq req)
    {
        var c = await ConfigAsync(cuit);
        c.Categoria = string.IsNullOrWhiteSpace(req.Categoria) ? null : req.Categoria.Trim()[..Math.Min(60, req.Categoria.Trim().Length)];
        c.UpdatedAt = DateTime.UtcNow; c.UpdatedBy = Quien();
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    public record ReglaReq(string? Regla);
    [HttpPut("proveedor/{cuit}/regla")]
    public async Task<IActionResult> PonerRegla(string cuit, [FromBody] ReglaReq req)
    {
        var r = string.IsNullOrWhiteSpace(req.Regla) ? null : req.Regla.Trim().ToUpperInvariant();
        if (r is not (null or "DEBITO" or "TARJETA" or "MP" or "EFECTIVO")) return BadRequest(new { error = "Regla desconocida" });
        var c = await ConfigAsync(cuit);
        c.ReglaPago = r; c.UpdatedAt = DateTime.UtcNow; c.UpdatedBy = Quien();
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    private async Task<Api.Models.ComprasProveedorConfig> ConfigAsync(string cuit)
    {
        cuit = cuit.Trim();
        var c = await _db.ComprasProveedorConfigs.FirstOrDefaultAsync(x => x.Cuit == cuit);
        if (c is null) { c = new Api.Models.ComprasProveedorConfig { Cuit = cuit }; _db.ComprasProveedorConfigs.Add(c); }
        return c;
    }

    private async Task<Api.Models.ComprasFacturaExtra?> ExtraAsync(string id, bool crear)
    {
        if (!await _db.ContadoraComprobantes.AnyAsync(c => c.IdComprobante == id && c.Naturaleza == "COMPRA")) return null;
        var e = await _db.ComprasFacturaExtras.FirstOrDefaultAsync(x => x.IdComprobante == id);
        if (e is null && crear) { e = new Api.Models.ComprasFacturaExtra { IdComprobante = id }; _db.ComprasFacturaExtras.Add(e); }
        return e;
    }

    public record ComentarioReq(string? Texto);
    [HttpPut("{id}/comentario")]
    public async Task<IActionResult> Comentario(string id, [FromBody] ComentarioReq req)
    {
        var e = await ExtraAsync(id, true);
        if (e is null) return NotFound(new { error = "Factura no encontrada" });
        var t = (req.Texto ?? "").Trim();
        e.Comentario = t == "" ? null : t[..Math.Min(500, t.Length)];
        e.UpdatedAt = DateTime.UtcNow; e.UpdatedBy = Quien();
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    public record PagoReq(string Medio, DateTime? Fecha, string? Origen, long? MovId);
    /// <summary>Marcar pagada: a mano (medio + fecha) o confirmando una sugerencia (Origen MP/BCO + MovId).</summary>
    [HttpPost("{id}/pago")]
    public async Task<IActionResult> MarcarPago(string id, [FromBody] PagoReq req)
    {
        var e = await ExtraAsync(id, true);
        if (e is null) return NotFound(new { error = "Factura no encontrada" });
        e.PagoMpMovId = null; e.PagoExtractoMovId = null;
        DateTime? fecha = req.Fecha;
        if (req.Origen == "MP" && req.MovId is long mp0 && mp0 <= int.MaxValue && (int)mp0 is var mp)
        {
            var m = await _db.MpMovimientos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == mp);
            if (m is null) return BadRequest(new { error = "No encontré ese movimiento de Mercado Pago" });
            e.PagoMpMovId = mp; fecha ??= m.Fecha;
        }
        else if (req.Origen == "BCO" && req.MovId is long bc0 && bc0 <= int.MaxValue && (int)bc0 is var bc)
        {
            var m = await _db.CafeExtractoMovimientos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == bc);
            if (m is null) return BadRequest(new { error = "No encontré ese movimiento del banco" });
            e.PagoExtractoMovId = bc; fecha ??= m.Fecha;
        }
        var medio = (req.Medio ?? "").Trim();
        e.PagoMedio = medio == "" ? "Pagada" : medio[..Math.Min(30, medio.Length)];
        e.PagoFecha = fecha;
        e.UpdatedAt = DateTime.UtcNow; e.UpdatedBy = Quien();
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    [HttpDelete("{id}/pago")]
    public async Task<IActionResult> DesmarcarPago(string id)
    {
        var e = await ExtraAsync(id, false);
        if (e is null) return Ok(new { ok = true });
        e.PagoMedio = null; e.PagoFecha = null; e.PagoMpMovId = null; e.PagoExtractoMovId = null;
        e.UpdatedAt = DateTime.UtcNow; e.UpdatedBy = Quien();
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    public record RechazoReq(string Origen, long MovId);
    /// <summary>"No es este": esa sugerencia no se vuelve a ofrecer para esta factura.</summary>
    [HttpPost("{id}/rechazar")]
    public async Task<IActionResult> Rechazar(string id, [FromBody] RechazoReq req)
    {
        var e = await ExtraAsync(id, true);
        if (e is null) return NotFound(new { error = "Factura no encontrada" });
        var k = $"{req.Origen}:{req.MovId}";
        var l = (e.Rechazados ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!l.Contains(k)) l.Add(k);
        e.Rechazados = string.Join(',', l.TakeLast(60));
        e.UpdatedAt = DateTime.UtcNow; e.UpdatedBy = Quien();
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    private record Auto(string Via, string Clave);
    private record MovBco(int Id, DateTime Fecha, decimal Imp, string Texto, string Desc);

    /// <summary>
    /// 18/11/2026 (5ª, pedido del dueño: "estoy seguro que el 90 % lo podés matchear"). Medido con datos
    /// reales de 60 días: ~36 % sale seguro + MeLi/Meli Log automáticas + reglas por proveedor ≈ 65 %.
    /// SEGURO (sale "pagada" sola, con ✕ para deshacer):
    ///  1. Galicia "Trf Inmed Proveed" con el CUIT del proveedor en la leyenda. Una transferencia puede pagar
    ///     varias facturas del mismo CUIT: se van llenando por fecha hasta su importe (+1 %).
    ///  2. Galicia débito / pago de servicio con el nombre del proveedor y el importe ±2 %.
    ///  3. Compra en Mercado Libre (como comprador): la orden y la factura tienen el MISMO importe (±$1),
    ///     factura de 2 días antes a 20 después de la compra.
    /// SUGERENCIA (amarillo, Sí/No): débito del Galicia ±1 % o pago de MP ±3 %, con la descripción del pago.
    /// Lo que se dijo "No" (o se deshizo con ✕) queda en Rechazados y no vuelve.
    /// </summary>
    private async Task<(Dictionary<string, Auto> Auto, Dictionary<string, SugerenciaDto> Sug)> EmparejarAsync(
        List<FacturaDto> facturas, Dictionary<string, Api.Models.ComprasFacturaExtra> extras)
    {
        var auto = new Dictionary<string, Auto>();
        var sug = new Dictionary<string, SugerenciaDto>();
        var desde = facturas.Min(f => f.Fecha!.Value).AddDays(-25);
        var hasta = facturas.Max(f => f.Fecha!.Value).AddDays(60);
        var usadosMp = (await _db.ComprasFacturaExtras.AsNoTracking().Where(e => e.PagoMpMovId != null).Select(e => e.PagoMpMovId!.Value).ToListAsync()).ToHashSet();
        var usadosBco = (await _db.ComprasFacturaExtras.AsNoTracking().Where(e => e.PagoExtractoMovId != null).Select(e => e.PagoExtractoMovId!.Value).ToListAsync())
            .Concat(await _db.ContadoraComprobantePagos.AsNoTracking().Where(p => p.ExtractoMovId != null && !p.Anulado).Select(p => p.ExtractoMovId!.Value).ToListAsync())
            .Concat(await _db.CafePagosProveedor.AsNoTracking().Where(p => p.ExtractoMovId != null && p.Estado == "VIGENTE").Select(p => p.ExtractoMovId!.Value).ToListAsync())
            .ToHashSet();
        HashSet<string> Rech(string fid) => extras.TryGetValue(fid, out var ex) ? (ex.Rechazados ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet() : new();

        var bcos = (await _db.CafeExtractoMovimientos.AsNoTracking()
                .Where(m => m.Debitos > 0 && m.IgnoradoAt == null && m.Fecha >= desde && m.Fecha <= hasta)
                .Select(m => new { m.Id, m.Fecha, m.Debitos, m.Descripcion, m.LeyendaAdicional1, m.LeyendaAdicional2, m.LeyendaAdicional3 }).ToListAsync())
            .Where(m => !usadosBco.Contains(m.Id))
            .Select(m => new MovBco(m.Id, m.Fecha, m.Debitos, $"{m.Descripcion} {m.LeyendaAdicional1} {m.LeyendaAdicional2} {m.LeyendaAdicional3}".ToUpperInvariant(),
                                    Recortar($"{m.Descripcion} · {m.LeyendaAdicional1}", 60)))
            .OrderBy(m => m.Fecha).ToList();
        var restoBco = bcos.ToDictionary(b => b.Id, b => b.Imp);   // lo que le queda a cada movimiento por cubrir

        // 1) Transferencia con el CUIT del proveedor
        foreach (var f in facturas.Where(f => !string.IsNullOrEmpty(f.Cuit)).OrderBy(f => f.Fecha))
        {
            var rech = Rech(f.Id);
            var b = bcos.FirstOrDefault(b => b.Texto.Contains(f.Cuit!) && !rech.Contains($"BCO:{b.Id}")
                                             && (b.Fecha - f.Fecha!.Value).TotalDays is >= -3 and <= 60
                                             && restoBco[b.Id] >= f.Total * 0.99m);
            if (b is null) continue;
            restoBco[b.Id] -= f.Total;
            auto[f.Id] = new Auto($"Galicia {b.Fecha:dd/MM} · transferencia a {f.Proveedor}{(b.Imp > f.Total * 1.01m ? $" (${b.Imp.ToString("#,0", Ar)} en total)" : "")}", $"BCO:{b.Id}");
        }
        // 2) Débito / pago de servicio con el nombre, importe ±2 %
        foreach (var f in facturas.Where(f => !auto.ContainsKey(f.Id)))
        {
            var rech = Rech(f.Id);
            var pal = Palabras(f.Proveedor);
            if (pal.Count == 0) continue;
            var b = bcos.FirstOrDefault(b => restoBco[b.Id] == b.Imp && !rech.Contains($"BCO:{b.Id}")
                                             && pal.Any(w => b.Texto.Contains(w)) && Math.Abs(b.Imp - f.Total) <= Math.Max(1m, f.Total * 0.02m)
                                             && (b.Fecha - f.Fecha!.Value).TotalDays is >= -3 and <= 60);
            if (b is null) continue;
            restoBco[b.Id] = 0;
            auto[f.Id] = new Auto($"Galicia {b.Fecha:dd/MM} · {b.Desc}", $"BCO:{b.Id}");
        }
        // 3) Compra en Mercado Libre con el mismo importe
        List<ComprasPagosOrigenService.CompraMeli> compras = new();
        try { compras = await _origen.ComprasAsync(desde); } catch { }
        var ordUsadas = new HashSet<long>();
        foreach (var f in facturas.Where(f => !auto.ContainsKey(f.Id)).OrderBy(f => f.Fecha))
        {
            var rech = Rech(f.Id);
            var o = compras.Where(o => !ordUsadas.Contains(o.OrderId) && !rech.Contains($"ORD:{o.OrderId}")
                                       && Math.Abs(o.Total - f.Total) <= 1m
                                       && (f.Fecha!.Value - o.Fecha.Date).TotalDays is >= -2 and <= 20)
                           .OrderBy(o => Math.Abs((f.Fecha!.Value - o.Fecha.Date).TotalDays)).FirstOrDefault();
            if (o is null) continue;
            ordUsadas.Add(o.OrderId);
            auto[f.Id] = new Auto($"Compra MeLi {o.Fecha.ToLocalTime():dd/MM} · {Recortar(o.Titulo, 45)} · {o.Vendedor}", $"ORD:{o.OrderId}");
        }
        // Las compras MeLi ya emparejadas ocupan su pago de MP.
        var pagosDeOrden = compras.Where(o => ordUsadas.Contains(o.OrderId)).SelectMany(o => o.PagosMp).ToHashSet();

        // ── Sugerencias para el resto ──
        // Del banco no se sugieren impuestos/comisiones, ni transferencias a OTRO proveedor (traen otro CUIT).
        static bool EsGastoBanco(MovBco b) => b.Texto.StartsWith("IMP.") || b.Texto.StartsWith("ING. BRUTOS") || b.Texto.StartsWith("PERCEP")
            || b.Texto.StartsWith("IVA ") || b.Texto.StartsWith("COMISION") || b.Texto.StartsWith("SERVICIO ACREDITAMIENTO") || b.Texto.StartsWith("IMP ");
        static bool OtroCuit(MovBco b, string? cuit) => b.Texto.Contains("PROVEED")
            && System.Text.RegularExpressions.Regex.Matches(b.Texto, @"\b\d{11}\b").Any(m => m.Value != cuit);
        var resto = facturas.Where(f => !auto.ContainsKey(f.Id) && f.Total > 0).ToList();
        if (resto.Count == 0) return (auto, sug);
        var mps = (await _db.MpMovimientos.AsNoTracking()
                .Where(m => m.MontoNeto < 0 && m.TipoTransaccion == "SETTLEMENT" && m.Fecha >= desde && m.Fecha <= hasta)
                .Select(m => new { m.Id, m.Fecha, m.MontoNeto, m.ReferenciaExterna, m.SourceId }).ToListAsync())
            .Where(m => !usadosMp.Contains(m.Id) && (m.SourceId == null || !pagosDeOrden.Contains(m.SourceId))
                        && !(m.ReferenciaExterna ?? "").StartsWith("MELIPAYMENTS") && !(m.ReferenciaExterna ?? "").StartsWith("loyal_"))
            .GroupBy(m => new { m.Fecha, m.MontoNeto, m.ReferenciaExterna }).Select(g => g.First())
            .ToList();
        var descs = new Dictionary<string, string>();
        try { descs = await _origen.DescripcionesMpAsync(mps.Where(m => m.SourceId != null).Select(m => m.SourceId!)); } catch { }
        var compraPorPago = compras.SelectMany(o => o.PagosMp.Select(p => (p, o))).GroupBy(x => x.p).ToDictionary(g => g.Key, g => g.First().o);

        var cands = new List<(string Fid, double Puntos, SugerenciaDto S)>();
        foreach (var f in resto)
        {
            var fe = f.Fecha!.Value;
            var total = f.Total;
            var rech = Rech(f.Id);
            var pal = Palabras(f.Proveedor);
            foreach (var b in bcos.Where(b => restoBco[b.Id] == b.Imp))
            {
                if (rech.Contains($"BCO:{b.Id}") || EsGastoBanco(b) || OtroCuit(b, f.Cuit)) continue;
                var dif = Math.Abs(b.Imp - total) / total;
                var dias = (b.Fecha - fe).TotalDays;
                if (dif > 0.01m || dias < -3 || dias > 40) continue;
                var nombre = pal.Any(w => b.Texto.Contains(w));
                cands.Add((f.Id, (double)dif * 100 + Math.Abs(dias) / 10 - (nombre ? 5 : 0),
                    new SugerenciaDto("BCO", b.Id, b.Fecha, b.Imp, $"Galicia {b.Fecha:dd/MM} · ${b.Imp.ToString("#,0", Ar)} · {b.Desc}")));
            }
            foreach (var m in mps)
            {
                if (rech.Contains($"MP:{m.Id}")) continue;
                var imp = -m.MontoNeto;
                var dif = Math.Abs(imp - total) / total;
                var dias = (m.Fecha - fe).TotalDays;
                if (dif > 0.03m || dias < -10 || dias > 20) continue;
                var desc = m.SourceId != null && descs.TryGetValue(m.SourceId, out var dd) ? dd : "";
                if (desc == "(pasado a inversión)" || desc.StartsWith("Suscripción a Meli", StringComparison.OrdinalIgnoreCase)) continue;
                if (m.SourceId != null && compraPorPago.TryGetValue(m.SourceId, out var oc)) desc = $"compra MeLi: {oc.Titulo} · {oc.Vendedor}";
                var nombre = pal.Any(w => desc.ToUpperInvariant().Contains(w));
                cands.Add((f.Id, (double)dif * 100 + Math.Abs(dias) / 10 + 1 - (nombre ? 5 : 0),
                    new SugerenciaDto("MP", m.Id, m.Fecha, imp, $"Mercado Pago {m.Fecha:dd/MM} · ${imp.ToString("#,0", Ar)}{(desc == "" ? "" : " · " + Recortar(desc, 55))}")));
            }
        }
        var tomados = new HashSet<string>();
        foreach (var c in cands.OrderBy(c => c.Puntos))
        {
            var k = $"{c.S.Origen}:{c.S.MovId}";
            if (sug.ContainsKey(c.Fid) || tomados.Contains(k)) continue;
            sug[c.Fid] = c.S; tomados.Add(k);
        }
        return (auto, sug);
    }

    private static List<string> Palabras(string proveedor)
        => System.Text.RegularExpressions.Regex.Replace(proveedor.ToUpperInvariant(), "[^A-ZÁÉÍÓÚÑ0-9 ]", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4 && w is not ("ARGENTINA" or "SOCIEDAD" or "ANONIMA" or "SERVICIOS" or "GRUPO" or "RESPONSABILIDAD" or "LIMITADA"))
            .ToList();

    private static readonly System.Globalization.CultureInfo Ar = System.Globalization.CultureInfo.GetCultureInfo("es-AR");
    private static string Recortar(string? s, int n) => string.IsNullOrWhiteSpace(s) ? "" : (s.Trim().Length <= n ? s.Trim() : s.Trim()[..n] + "…");

    /// <summary>"GOOD FOOD SA" → "Good Food SA" (AFIP manda todo en mayúsculas).</summary>
    public static string Lindo(string? n)
    {
        if (string.IsNullOrWhiteSpace(n)) return "?";
        var siglas = new HashSet<string> { "SA", "SRL", "SAS", "SH", "SACIF", "SAIC", "SACI", "SAICF", "S.A.", "S.R.L.", "S", "A", "C", "I", "F" };
        return string.Join(' ', n.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => siglas.Contains(w.ToUpperInvariant()) ? w.ToUpperInvariant()
                : w.Length <= 1 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }
}
