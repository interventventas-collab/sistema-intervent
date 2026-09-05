using Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// CRUD de Cajas (Cafe → Tesorería → Cajas).
/// Una "caja" es un lugar donde vive plata: Efectivo, MP, Banco Galicia, Cheques en cartera, V_PRIVADO.
/// Configurables por el usuario. Cada caja tiene un saldo inicial editable.
/// El saldo CURRENT se calcula sumando saldo inicial + movimientos (Cobranzas + Egresos).
/// </summary>
[ApiController]
[Route("api/cafe/cajas")]
[Authorize]
public class CafeCajasController : ControllerBase
{
    private readonly AppDbContext _db;

    public CafeCajasController(AppDbContext db) { _db = db; }

    public record CajaDto(
        int Id, string Nombre, string Tipo, decimal SaldoInicial, int Orden,
        bool IsActive, string? Notas, decimal SaldoActual);

    public record CrearCajaRequest(string Nombre, string Tipo, decimal SaldoInicial, int? Orden, string? Notas);
    public record EditarCajaRequest(string Nombre, string Tipo, decimal SaldoInicial, int? Orden, bool IsActive, string? Notas);

    /// <summary>Lista todas las cajas con su saldo actual calculado.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool incluirInactivas = false)
    {
        var query = _db.CafeCajas.AsQueryable();
        if (!incluirInactivas) query = query.Where(c => c.IsActive);
        var cajas = await query.OrderBy(c => c.Orden).ThenBy(c => c.Nombre).ToListAsync();

        var saldos = await SaldosPorCajaAsync();
        var result = cajas.Select(c => new CajaDto(
            c.Id, c.Nombre, c.Tipo, c.SaldoInicial, c.Orden, c.IsActive, c.Notas,
            c.SaldoInicial + (saldos.TryGetValue(c.Id, out var t) ? t : 0m)
        )).ToList();
        return Ok(result);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var c = await _db.CafeCajas.FindAsync(id);
        if (c is null) return NotFound();
        return Ok(c);
    }

    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] CrearCajaRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "Nombre vacio" });
        if (await _db.CafeCajas.AnyAsync(c => c.Nombre == req.Nombre))
            return BadRequest(new { error = "Ya existe una caja con ese nombre" });
        var c = new Models.CafeCaja
        {
            Nombre = req.Nombre.Trim(),
            Tipo = (req.Tipo ?? "EFECTIVO").Trim().ToUpperInvariant(),
            SaldoInicial = req.SaldoInicial,
            Orden = req.Orden ?? 0,
            Notas = req.Notas
        };
        _db.CafeCajas.Add(c);
        await _db.SaveChangesAsync();
        return Ok(c);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Editar(int id, [FromBody] EditarCajaRequest req)
    {
        var c = await _db.CafeCajas.FindAsync(id);
        if (c is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "Nombre vacio" });
        // Verificar duplicado de nombre (excluyendo a si mismo)
        if (await _db.CafeCajas.AnyAsync(x => x.Nombre == req.Nombre && x.Id != id))
            return BadRequest(new { error = "Ya existe otra caja con ese nombre" });
        c.Nombre = req.Nombre.Trim();
        c.Tipo = (req.Tipo ?? c.Tipo).Trim().ToUpperInvariant();
        c.SaldoInicial = req.SaldoInicial;
        if (req.Orden.HasValue) c.Orden = req.Orden.Value;
        c.IsActive = req.IsActive;
        c.Notas = req.Notas;
        c.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(c);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Eliminar(int id)
    {
        var c = await _db.CafeCajas.FindAsync(id);
        if (c is null) return NotFound();
        // No permitir eliminar si tiene movimientos
        var tieneMovs = await _db.CafeCobranzasMedios.AnyAsync(m => m.CajaId == id);
        if (tieneMovs) return BadRequest(new { error = "La caja tiene movimientos. Desactivala en vez de eliminar." });
        _db.CafeCajas.Remove(c);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Movimientos de caja (05/09/2026)
    //
    // ⚠ Hasta hoy la caja SOLO SUMABA: entraban las cobranzas y no salia nada nunca, asi que el
    // saldo era un acumulado historico sin sentido ("Efectivo" mostraba $142.736.024). El dueño
    // pidio explicitamente "quiero saber cuanta plata tengo en cada caja", asi que el saldo pasa a
    // ser: saldo inicial + cobranzas − pagos a proveedor − salidas + entradas ± ajustes de arqueo.
    // ─────────────────────────────────────────────────────────────────────────────

    public record MovimientoDto(
        int Id, int CajaId, string CajaNombre, DateTime Fecha, string Tipo,
        decimal Importe, string Motivo, int? TransferenciaGrupoId, string? CargadoPor);

    public record SalidaRequest(DateTime? Fecha, decimal Importe, string Motivo);
    public record TransferenciaRequest(int DesdeCajaId, int HaciaCajaId, DateTime? Fecha, decimal Importe, string? Motivo);
    public record ArqueoRequest(DateTime? Fecha, decimal ContadoReal, string? Notas);

    /// <summary>Fecha argentina de hoy: el servidor va en UTC y a la noche ya esta en el dia siguiente.</summary>
    private static DateTime HoyAr() => DateTime.UtcNow.AddHours(-3).Date;

    private string? QuienSoy() => User?.Identity?.Name;

    /// <summary>
    /// Lo que cada caja movio desde su saldo inicial: cobranzas que entraron, pagos a proveedor que
    /// salieron y los movimientos cargados a mano (que ya vienen con signo).
    /// </summary>
    private async Task<Dictionary<int, decimal>> SaldosPorCajaAsync()
    {
        var acum = new Dictionary<int, decimal>();

        void Sumar(int cajaId, decimal importe)
            => acum[cajaId] = (acum.TryGetValue(cajaId, out var v) ? v : 0m) + importe;

        var cobranzas = await _db.CafeCobranzasMedios
            .GroupBy(m => m.CajaId)
            .Select(g => new { CajaId = g.Key, Total = g.Sum(x => x.Importe) })
            .ToListAsync();
        foreach (var x in cobranzas) Sumar(x.CajaId, x.Total);

        // Los pagos a proveedor todavia no se usan (0 al 05/09/2026), pero si algun dia se cargan
        // tienen que restar solos, sin que haya que acordarse de tocar esto.
        var pagos = await _db.CafePagosProveedorMedios
            .GroupBy(m => m.CajaId)
            .Select(g => new { CajaId = g.Key, Total = g.Sum(x => x.Importe) })
            .ToListAsync();
        foreach (var x in pagos) Sumar(x.CajaId, -x.Total);

        var movs = await _db.CafeCajaMovimientos
            .GroupBy(m => m.CajaId)
            .Select(g => new { CajaId = g.Key, Total = g.Sum(x => x.Importe) })
            .ToListAsync();
        foreach (var x in movs) Sumar(x.CajaId, x.Total);

        return acum;
    }

    /// <summary>Movimientos cargados a mano, el mas nuevo primero.</summary>
    [HttpGet("movimientos")]
    public async Task<IActionResult> Movimientos([FromQuery] int? cajaId = null, [FromQuery] int dias = 60)
    {
        var desde = HoyAr().AddDays(-Math.Max(1, dias));
        var q = _db.CafeCajaMovimientos.Include(m => m.Caja).Where(m => m.Fecha >= desde);
        if (cajaId.HasValue) q = q.Where(m => m.CajaId == cajaId.Value);
        var movs = await q.OrderByDescending(m => m.Fecha).ThenByDescending(m => m.Id).Take(500).ToListAsync();
        return Ok(movs.Select(m => new MovimientoDto(
            m.Id, m.CajaId, m.Caja?.Nombre ?? "", m.Fecha, m.Tipo, m.Importe, m.Motivo,
            m.TransferenciaGrupoId, m.CargadoPor)));
    }

    /// <summary>Plata que sale de una caja: nafta, un adelanto, lo que sea.</summary>
    [HttpPost("{id:int}/salida")]
    public async Task<IActionResult> Salida(int id, [FromBody] SalidaRequest req)
    {
        var caja = await _db.CafeCajas.FindAsync(id);
        if (caja is null) return NotFound(new { error = "No existe esa caja" });
        if (req.Importe <= 0) return BadRequest(new { error = "El importe tiene que ser mayor a cero" });
        if (string.IsNullOrWhiteSpace(req.Motivo)) return BadRequest(new { error = "Poné para qué fue la plata" });

        var mov = new Models.CafeCajaMovimiento
        {
            CajaId = id,
            Fecha = (req.Fecha ?? HoyAr()).Date,
            Tipo = "SALIDA",
            Importe = -Math.Abs(req.Importe),   // el signo lo pone el servidor, no la pantalla
            Motivo = req.Motivo.Trim(),
            CargadoPor = QuienSoy()
        };
        _db.CafeCajaMovimientos.Add(mov);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, id = mov.Id });
    }

    /// <summary>Pasar plata de una caja a otra (tipico: del efectivo al banco). Son dos renglones.</summary>
    [HttpPost("transferencia")]
    public async Task<IActionResult> Transferencia([FromBody] TransferenciaRequest req)
    {
        if (req.DesdeCajaId == req.HaciaCajaId) return BadRequest(new { error = "Elegí dos cajas distintas" });
        if (req.Importe <= 0) return BadRequest(new { error = "El importe tiene que ser mayor a cero" });
        var desde = await _db.CafeCajas.FindAsync(req.DesdeCajaId);
        var hacia = await _db.CafeCajas.FindAsync(req.HaciaCajaId);
        if (desde is null || hacia is null) return NotFound(new { error = "No existe alguna de las cajas" });

        var fecha = (req.Fecha ?? HoyAr()).Date;
        var quien = QuienSoy();
        var detalle = string.IsNullOrWhiteSpace(req.Motivo) ? "" : $" · {req.Motivo.Trim()}";

        var salida = new Models.CafeCajaMovimiento
        {
            CajaId = desde.Id, Fecha = fecha, Tipo = "TRANSFERENCIA",
            Importe = -Math.Abs(req.Importe),
            Motivo = $"A {hacia.Nombre}{detalle}", CargadoPor = quien
        };
        var entrada = new Models.CafeCajaMovimiento
        {
            CajaId = hacia.Id, Fecha = fecha, Tipo = "TRANSFERENCIA",
            Importe = Math.Abs(req.Importe),
            Motivo = $"De {desde.Nombre}{detalle}", CargadoPor = quien
        };
        _db.CafeCajaMovimientos.Add(salida);
        await _db.SaveChangesAsync();          // necesito el Id de la salida para atar las dos patas

        salida.TransferenciaGrupoId = salida.Id;
        entrada.TransferenciaGrupoId = salida.Id;
        _db.CafeCajaMovimientos.Add(entrada);
        await _db.SaveChangesAsync();

        return Ok(new { ok = true, grupoId = salida.Id });
    }

    /// <summary>
    /// "Hoy conté $X en el cajón": anota la diferencia contra lo que decia el sistema y el saldo
    /// queda clavado en lo que realmente hay. Es la red de seguridad para cuando alguien se olvida
    /// de cargar una salida.
    /// </summary>
    [HttpPost("{id:int}/arqueo")]
    public async Task<IActionResult> Arqueo(int id, [FromBody] ArqueoRequest req)
    {
        var caja = await _db.CafeCajas.FindAsync(id);
        if (caja is null) return NotFound(new { error = "No existe esa caja" });

        var saldos = await SaldosPorCajaAsync();
        var saldoSistema = caja.SaldoInicial + (saldos.TryGetValue(id, out var t) ? t : 0m);
        var diferencia = req.ContadoReal - saldoSistema;
        if (diferencia == 0m)
            return Ok(new { ok = true, sinCambios = true, saldoSistema, diferencia = 0m });

        var falta = diferencia < 0;
        var nota = string.IsNullOrWhiteSpace(req.Notas) ? "" : $" · {req.Notas.Trim()}";
        var mov = new Models.CafeCajaMovimiento
        {
            CajaId = id,
            Fecha = (req.Fecha ?? HoyAr()).Date,
            Tipo = "ARQUEO",
            Importe = diferencia,
            Motivo = $"Arqueo: contaste {Plata(req.ContadoReal)} y el sistema decía {Plata(saldoSistema)}"
                   + $" ({(falta ? "faltaban" : "sobraban")} {Plata(Math.Abs(diferencia))}){nota}",
            CargadoPor = QuienSoy()
        };
        _db.CafeCajaMovimientos.Add(mov);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, saldoSistema, diferencia, id = mov.Id });
    }

    /// <summary>Borra un movimiento. Si es una transferencia se van las dos patas juntas.</summary>
    [HttpDelete("movimientos/{id:int}")]
    public async Task<IActionResult> BorrarMovimiento(int id)
    {
        var mov = await _db.CafeCajaMovimientos.FindAsync(id);
        if (mov is null) return NotFound();
        if (mov.TransferenciaGrupoId.HasValue)
        {
            var patas = await _db.CafeCajaMovimientos
                .Where(m => m.TransferenciaGrupoId == mov.TransferenciaGrupoId).ToListAsync();
            _db.CafeCajaMovimientos.RemoveRange(patas);
        }
        else _db.CafeCajaMovimientos.Remove(mov);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Plata como la escribimos acá: $34.000. El contenedor corre en formato invariante.</summary>
    private static string Plata(decimal v)
    {
        var ar = new System.Globalization.CultureInfo("es-AR");
        return (v < 0 ? "-$" : "$") + Math.Abs(v).ToString("N0", ar);
    }
}
