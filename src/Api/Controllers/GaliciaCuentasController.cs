using Api.Data;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-11-18: varias cuentas del Galicia, una por sociedad. La principal (Id más bajo) es
/// Palanica y sigue con todo lo de siempre (/api/galicia/account, cobranzas, Salieron...).
/// Las otras sociedades tienen su propio usuario/clave y horarios, y lo que trae el robot
/// queda APARTE (GaliciaAccountId = su Id; el filtro global las saca de todo lo de Palanica).
/// Acá: listarlas, cargarlas, traerlas a mano y ver su resumen (saldo + importes).
/// </summary>
[ApiController]
[Route("api/galicia/cuentas")]
[Authorize]
public class GaliciaCuentasController : ControllerBase
{
    private readonly GaliciaAccountService _accounts;
    private readonly GaliciaSyncService _sync;
    private readonly AppDbContext _db;

    public GaliciaCuentasController(GaliciaAccountService accounts, GaliciaSyncService sync, AppDbContext db)
    {
        _accounts = accounts;
        _sync = sync;
        _db = db;
    }

    public record CuentaDto(int Id, string Nombre, bool EsPrincipal, string Usuario, bool HasPassword, bool IsActive,
        bool AutoSyncEnabled, string? AutoSyncTimes, DateTime? LastAutoSyncAt,
        DateTime? UltimaLecturaAt = null, string? UltimaLecturaError = null);

    /// <summary>Todas las cuentas, la principal primero. Nombre = Alias (o "Palanica" / el usuario).</summary>
    [HttpGet]
    public async Task<IActionResult> Listar()
    {
        var lista = await _accounts.ListAsync();
        var principalId = lista.FirstOrDefault()?.Id;
        return Ok(lista.Select(a => new CuentaDto(a.Id,
            a.Alias ?? (a.Id == principalId ? "Palanica" : a.Usuario),
            a.Id == principalId, a.Usuario, a.HasPassword, a.IsActive,
            a.AutoSyncEnabled, a.AutoSyncTimes, a.LastAutoSyncAt,
            a.UltimaLecturaAt, a.UltimaLecturaError)).ToList());
    }

    /// <summary>Crea (id = 0) o actualiza una cuenta.</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Guardar(int id, [FromBody] GaliciaAccountService.SaveGaliciaAccountRequest req)
    {
        var (ok, error, dto) = await _accounts.SaveByIdAsync(id, req);
        if (!ok) return BadRequest(new { error });
        return Ok(dto);
    }

    public record TraerResultDto(bool Ok, string Texto);

    /// <summary>Trae los movimientos de esa cuenta ahora (sincrónico, puede tardar minutos).
    /// Movimientos y cheques van en dos pedidos: juntos pasarían los 10 min que nginx deja abierto.</summary>
    [HttpPost("{id:int}/traer/movimientos")]
    public async Task<IActionResult> TraerMovimientos(int id)
    {
        if (await _accounts.GetEntityAsync(id) is null) return NotFound(new { error = "No existe esa cuenta" });
        var r = await _sync.SincronizarAsync(id);
        return Ok(new TraerResultDto(r.Ok, r.Ok ? $"{r.Nuevos} movimientos nuevos" : (r.Error ?? "no se pudo")));
    }

    [HttpPost("{id:int}/traer/cheques")]
    public async Task<IActionResult> TraerCheques(int id)
    {
        if (await _accounts.GetEntityAsync(id) is null) return NotFound(new { error = "No existe esa cuenta" });
        var r = await _sync.SincronizarChequesAsync(id);
        return Ok(new TraerResultDto(r.Ok, r.Ok ? $"{r.Nuevos} cheques nuevos, {r.Actualizados} actualizados" : (r.Error ?? "no se pudo")));
    }

    public record MovDto(int Id, DateTime Fecha, string Nombre, string? Detalle, decimal Importe);
    public record ChequeDto(int Id, string Numero, string? Quien, DateTime? Vence, decimal Importe, string Estado);
    public record ResumenDto(int Id, string Nombre, decimal? Saldo, DateTime? UltimaFecha, DateTime? UltimoImportadoAt,
        List<MovDto> Entraron, List<MovDto> Salieron, List<ChequeDto> ChequesRecibidos, List<ChequeDto> ChequesEmitidos);

    /// <summary>
    /// Resumen de una cuenta que NO es la principal: saldo, lo que entró y salió en los últimos
    /// N días (importes) y los cheques recibidos en mano / emitidos sin pagar. Solo para mirar.
    /// </summary>
    [HttpGet("{id:int}/resumen")]
    public async Task<IActionResult> Resumen(int id, [FromQuery] int dias = 30)
    {
        var acc = await _accounts.GetEntityAsync(id);
        if (acc is null) return NotFound(new { error = "No existe esa cuenta" });
        var principalId = await _accounts.PrincipalIdAsync();
        if (acc.Id == principalId) return BadRequest(new { error = "La cuenta principal se ve en la ventana de siempre" });
        dias = Math.Clamp(dias, 1, 365);

        var movsQ = _db.CafeExtractoMovimientos.IgnoreQueryFilters().AsNoTracking().Where(m => m.GaliciaAccountId == id);
        var ult = await movsQ.OrderByDescending(m => m.Fecha).ThenByDescending(m => m.Id)
            .Select(m => new { m.Saldo, m.Fecha }).FirstOrDefaultAsync();
        var ultImportado = await movsQ.MaxAsync(m => (DateTime?)m.ImportadoAt);

        var desde = DateTime.UtcNow.AddHours(-3).Date.AddDays(-dias);
        var movs = await movsQ.Where(m => m.Fecha >= desde)
            .OrderByDescending(m => m.Fecha).ThenByDescending(m => m.Id)
            .Select(m => new { m.Id, m.Fecha, m.Descripcion, m.LeyendaAdicional1, m.Debitos, m.Creditos })
            .ToListAsync();
        var entraron = movs.Where(m => m.Creditos > 0).Select(m => MovDe(m.Id, m.Fecha, m.LeyendaAdicional1, m.Descripcion, m.Creditos)).ToList();
        var salieron = movs.Where(m => m.Debitos > 0).Select(m => MovDe(m.Id, m.Fecha, m.LeyendaAdicional1, m.Descripcion, m.Debitos)).ToList();

        var chq = await _db.CafeChequesBanco.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.GaliciaAccountId == id && (c.Tipo == "RECIBIDO" || c.Tipo == "EMITIDO"))
            .Select(c => new { c.Id, c.Tipo, c.Numero, c.ContraparteNombre, c.FechaPago, c.Importe, c.Estado })
            .ToListAsync();
        var noPendiente = new[] { "pagado", "anulado", "rechazado", "repudiado", "depositado", "acreditado" };
        var recibidos = chq.Where(c => c.Tipo == "RECIBIDO" && string.Equals(c.Estado, "Disponible", StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.FechaPago ?? DateTime.MaxValue)
            .Select(c => new ChequeDto(c.Id, c.Numero, c.ContraparteNombre, c.FechaPago, c.Importe, c.Estado)).ToList();
        var emitidos = chq.Where(c => c.Tipo == "EMITIDO" && !noPendiente.Contains((c.Estado ?? "").Trim().ToLowerInvariant()))
            .OrderBy(c => c.FechaPago ?? DateTime.MaxValue)
            .Select(c => new ChequeDto(c.Id, c.Numero, c.ContraparteNombre, c.FechaPago, c.Importe, c.Estado)).ToList();

        return Ok(new ResumenDto(acc.Id, acc.Alias ?? acc.Usuario, ult?.Saldo, ult?.Fecha, ultImportado,
            entraron, salieron, recibidos, emitidos));
    }

    /// <summary>Nombre = a quién/de quién (leyenda del banco); si no hay, la descripción del movimiento.</summary>
    private static MovDto MovDe(int id, DateTime fecha, string? quien, string? desc, decimal importe)
        => string.IsNullOrWhiteSpace(quien)
            ? new MovDto(id, fecha, desc ?? "(sin nombre)", null, importe)
            : new MovDto(id, fecha, quien.Trim(), desc, importe);
}
