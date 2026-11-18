using Api.Data;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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

    public FacturasRecibidasController(AppDbContext db, ComprasAfipRobot robot, ProveedorCtaCteService ctacte)
    { _db = db; _robot = robot; _ctacte = ctacte; }

    public record FacturaDto(string Id, DateTime Llego, DateTime? Fecha, string Proveedor, string? Cuit, int? ProveedorId,
        string Tipo, string Numero, decimal Total, bool EsNotaCredito, bool TienePdf, bool Nueva, string? Pago,
        // 18/11/2026 (2ª): categoría del proveedor, comentario, por dónde se pagó, sugerencia y si lleva cta cte.
        string? Categoria = null, string? Comentario = null, string? PagoVia = null, SugerenciaDto? Sugerencia = null,
        bool CuentaCorriente = false, string? ReglaPago = null, bool PagoManual = false);
    /// <summary>Un pago que PUEDE ser de esta factura (mismo importe, fechas cerca). Origen MP o BCO.</summary>
    public record SugerenciaDto(string Origen, int MovId, DateTime Fecha, decimal Importe, string Texto);
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

    /// <summary>Por defecto, lo que llegó en los últimos 60 días. mes=yyyy-MM filtra por fecha de la factura.</summary>
    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] string? mes, [FromQuery] string? q)
    {
        var vistas = await VistasHastaAsync();
        var qq = _db.ContadoraComprobantes.AsNoTracking().Where(c => c.Naturaleza == "COMPRA");
        if (!string.IsNullOrWhiteSpace(mes) && DateTime.TryParse(mes + "-01", out var ini))
        {
            var fin = ini.AddMonths(1);
            qq = qq.Where(c => c.FechaEmision >= ini && c.FechaEmision < fin);
        }
        else
        {
            var desde = DateTime.UtcNow.AddDays(-60);
            qq = qq.Where(c => c.PrimeraVezEn >= desde || c.FechaEmision >= desde);
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim();
            qq = qq.Where(c => (c.ReceptorNombre != null && c.ReceptorNombre.Contains(t)) || (c.ReceptorDoc != null && c.ReceptorDoc.Contains(t)));
        }
        var filas = await qq.OrderByDescending(c => c.PrimeraVezEn).ThenByDescending(c => c.FechaEmision).Take(500)
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
            else if (!f.EsNotaCredito && ex?.PagoMedio != null)
            {
                pago = "pagada"; manual = true;
                via = ex.PagoMedio + (ex.PagoFecha is DateTime pf ? " · " + pf.ToString("dd/MM") : "");
            }
            else if (!f.EsNotaCredito && cfg?.ReglaPago != null)
            {
                pago = "pagada"; via = TextoRegla(cfg.ReglaPago);
            }
            else if (!f.EsNotaCredito && f.ReceptorDoc == CuitMercadoLibre)
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
            var sug = await SugerirPagosAsync(sinSaber, extras);
            res = res.Select(r => sug.TryGetValue(r.Id, out var sg) ? r with { Sugerencia = sg } : r).ToList();
        }
        return Ok(res);
    }

    /// <summary>Abrió la lista: lo que hay deja de ser "nuevo".</summary>
    [HttpPost("marcar-vistas")]
    public async Task<IActionResult> MarcarVistas()
    {
        await ComprasAfipRobot.GuardarAsync(_db, ComprasAfipRobot.KeyVistas, DateTime.UtcNow.ToString("o"));
        return Ok(new { ok = true });
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

    public record PagoReq(string Medio, DateTime? Fecha, string? Origen, int? MovId);
    /// <summary>Marcar pagada: a mano (medio + fecha) o confirmando una sugerencia (Origen MP/BCO + MovId).</summary>
    [HttpPost("{id}/pago")]
    public async Task<IActionResult> MarcarPago(string id, [FromBody] PagoReq req)
    {
        var e = await ExtraAsync(id, true);
        if (e is null) return NotFound(new { error = "Factura no encontrada" });
        e.PagoMpMovId = null; e.PagoExtractoMovId = null;
        DateTime? fecha = req.Fecha;
        if (req.Origen == "MP" && req.MovId is int mp)
        {
            var m = await _db.MpMovimientos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == mp);
            if (m is null) return BadRequest(new { error = "No encontré ese movimiento de Mercado Pago" });
            e.PagoMpMovId = mp; fecha ??= m.Fecha;
        }
        else if (req.Origen == "BCO" && req.MovId is int bc)
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

    public record RechazoReq(string Origen, int MovId);
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

    /// <summary>
    /// Busca, para cada factura sin estado, un pago que salió por casi el mismo importe:
    /// · Banco Galicia (débitos del extracto): ±1 %, de 3 días antes a 40 días después de la factura.
    ///   Si el texto del banco trae el nombre del proveedor, va primero.
    /// · Mercado Pago (pagos que salieron de la cuenta, tipo SETTLEMENT): ±3 % (las compras de MeLi
    ///   suelen sumar el envío), de 10 días antes (se paga al comprar) a 20 días después.
    /// El reporte de MP NO dice a quién se le pagó, por eso es solo una sugerencia que se confirma.
    /// Cada movimiento se sugiere a una sola factura y no se ofrece si ya está usado o se dijo "No".
    /// </summary>
    private async Task<Dictionary<string, SugerenciaDto>> SugerirPagosAsync(List<FacturaDto> facturas, Dictionary<string, Api.Models.ComprasFacturaExtra> extras)
    {
        var desde = facturas.Min(f => f.Fecha!.Value).AddDays(-10);
        var hasta = facturas.Max(f => f.Fecha!.Value).AddDays(40);
        var usadosMp = (await _db.ComprasFacturaExtras.AsNoTracking().Where(e => e.PagoMpMovId != null).Select(e => e.PagoMpMovId!.Value).ToListAsync()).ToHashSet();
        var usadosBco = (await _db.ComprasFacturaExtras.AsNoTracking().Where(e => e.PagoExtractoMovId != null).Select(e => e.PagoExtractoMovId!.Value).ToListAsync())
            .Concat(await _db.ContadoraComprobantePagos.AsNoTracking().Where(p => p.ExtractoMovId != null && !p.Anulado).Select(p => p.ExtractoMovId!.Value).ToListAsync())
            .ToHashSet();

        var mps = (await _db.MpMovimientos.AsNoTracking()
                .Where(m => m.MontoNeto < 0 && m.TipoTransaccion == "SETTLEMENT" && m.Fecha >= desde && m.Fecha <= hasta)
                .Select(m => new { m.Id, m.Fecha, m.MontoNeto, m.ReferenciaExterna }).ToListAsync())
            .Where(m => !usadosMp.Contains(m.Id))
            .GroupBy(m => new { m.Fecha, m.MontoNeto, m.ReferenciaExterna }).Select(g => g.First())   // el reporte a veces repite la fila
            .ToList();
        var bcos = (await _db.CafeExtractoMovimientos.AsNoTracking()
                .Where(m => m.Debitos > 0 && m.IgnoradoAt == null && m.Fecha >= desde && m.Fecha <= hasta)
                .Select(m => new { m.Id, m.Fecha, m.Debitos, m.Descripcion, m.LeyendaAdicional1, m.LeyendaAdicional2 }).ToListAsync())
            .Where(m => !usadosBco.Contains(m.Id)).ToList();

        var cands = new List<(string Fid, double Puntos, SugerenciaDto S)>();
        foreach (var f in facturas)
        {
            var fe = f.Fecha!.Value;
            var total = f.Total;
            if (total <= 0) continue;
            var rech = extras.TryGetValue(f.Id, out var ex) ? (ex.Rechazados ?? "").Split(',').ToHashSet() : new HashSet<string>();
            var palabras = f.Proveedor.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 4 && w is not ("ARGENTINA" or "SOCIEDAD" or "ANONIMA" or "S.R.L." or "SERVICIOS")).ToList();
            foreach (var b in bcos)
            {
                if (rech.Contains($"BCO:{b.Id}")) continue;
                var dif = Math.Abs(b.Debitos - total) / total;
                var dias = (b.Fecha - fe).TotalDays;
                if (dif > 0.01m || dias < -3 || dias > 40) continue;
                var txt = $"{b.Descripcion} {b.LeyendaAdicional1} {b.LeyendaAdicional2}".ToUpperInvariant();
                var nombre = palabras.Any(w => txt.Contains(w));
                var puntos = (double)dif * 100 + Math.Abs(dias) / 10 - (nombre ? 5 : 0);
                cands.Add((f.Id, puntos, new SugerenciaDto("BCO", b.Id, b.Fecha, b.Debitos,
                    $"Galicia {b.Fecha:dd/MM} · ${b.Debitos.ToString("#,0", Ar)} · {Recortar($"{b.Descripcion} {b.LeyendaAdicional1}", 60)}")));
            }
            foreach (var m in mps)
            {
                if (rech.Contains($"MP:{m.Id}")) continue;
                var imp = -m.MontoNeto;
                var dif = Math.Abs(imp - total) / total;
                var dias = (m.Fecha - fe).TotalDays;
                if (dif > 0.03m || dias < -10 || dias > 20) continue;
                var esMeli = m.ReferenciaExterna != null && m.ReferenciaExterna.StartsWith("2000");
                var puntos = (double)dif * 100 + Math.Abs(dias) / 10 + 1;   // MP pesa un poco menos que el banco
                cands.Add((f.Id, puntos, new SugerenciaDto("MP", m.Id, m.Fecha, imp,
                    $"Mercado Pago {m.Fecha:dd/MM} · ${imp.ToString("#,0", Ar)}{(esMeli ? " (compra MeLi)" : "")}")));
            }
        }
        // Lo mejor primero; cada factura y cada movimiento, una sola vez.
        var res = new Dictionary<string, SugerenciaDto>();
        var tomados = new HashSet<string>();
        foreach (var c in cands.OrderBy(c => c.Puntos))
        {
            var k = $"{c.S.Origen}:{c.S.MovId}";
            if (res.ContainsKey(c.Fid) || tomados.Contains(k)) continue;
            res[c.Fid] = c.S; tomados.Add(k);
        }
        return res;
    }

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
