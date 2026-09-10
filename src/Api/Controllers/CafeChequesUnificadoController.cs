using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-09-10 — Vista UNIFICADA de cheques. Pedido del usuario: "cuando quiero cobrar o
/// endosar un cheque no se donde buscarlo, al estar en varios lados me marea".
///
/// El problema real: los cheques viven en DOS tablas que no se hablan.
///   - Cafe_Cheques      : los que el usuario carga a mano (o nacen de una cobranza)
///   - Cafe_ChequesBanco : los que baja el Excel/robot del banco
/// El mismo papel puede estar en las dos y quedar duplicado en pantalla, con nombres de
/// estado distintos ("EN_CARTERA" vs "Disponible"). Aca los juntamos en UNA lista, agrupados
/// por la SITUACION en la que estan (que es lo que el usuario se pregunta), no por de donde
/// vino el dato.
///
/// Este controller es SOLO LECTURA + el vinculo de duplicados. Las acciones (depositar, cobrar,
/// rechazar, imputar) siguen viviendo en CafeChequesController; el endoso, en
/// CafePagosProveedorController. No se duplico ninguna regla de negocio.
/// </summary>
[ApiController]
[Route("api/cafe/cheques-unificado")]
[Authorize]
public class CafeChequesUnificadoController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AuditLogService _audit;

    public CafeChequesUnificadoController(AppDbContext db, AuditLogService audit) { _db = db; _audit = audit; }

    // Vistas (solapas de la pantalla)
    public const string EN_MANO = "EN_MANO";
    public const string EN_BANCO = "EN_BANCO";
    public const string USADOS = "USADOS";
    public const string RECHAZADOS = "RECHAZADOS";
    public const string A_PAGAR = "A_PAGAR";

    public record ChequeUniDto(
        string Key,               // "C-12" (cartera) o "B-34" (banco). Identidad en la pantalla.
        string Origen,            // CARTERA | BANCO
        int Id,
        string Numero,
        string Banco,
        string? Emisor,           // quien firmo el cheque
        int? ClienteId,
        string? ClienteNombre,
        decimal Importe,
        DateTime? Vence,
        string Vista,
        string EstadoTexto,       // lo que se muestra ("En mano", "En el banco", "Endosado"...)
        bool PuedeImputar,        // hacer la cobranza con este cheque
        bool PuedeDepositar,
        bool PuedeVentanilla,
        bool PuedeEndosar,
        bool PuedeRechazar,
        bool PuedeAcreditar,
        // Duplicado detectado: el MISMO papel cargado por los dos caminos
        string? DuplicadoKey,
        int? DuplicadoCarteraId,
        int? DuplicadoBancoId,
        string? Observaciones);

    public record ConteosDto(int EnMano, int EnBanco, int Usados, int Rechazados, int APagar, int Duplicados);
    public record ResumenVistaDto(int Cantidad, decimal Importe, DateTime? PrimerVencimiento);
    public record UnificadoResponse(ConteosDto Conteos, ResumenVistaDto Resumen, List<ChequeUniDto> Filas);

    /// <summary>Numero comparable: sin espacios, guiones ni ceros a la izquierda. El banco y el
    /// usuario escriben el mismo numero de formas distintas ("0004471209" vs "4471209").</summary>
    private static string NormNumero(string? n)
    {
        var s = new string((n ?? "").Where(char.IsLetterOrDigit).ToArray()).TrimStart('0');
        return s.Length == 0 ? "0" : s;
    }

    private static bool Coincide(string? texto, string q)
        => !string.IsNullOrEmpty(texto) && texto.Contains(q, StringComparison.OrdinalIgnoreCase);

    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] string vista = EN_MANO, [FromQuery] string? q = null)
    {
        vista = (vista ?? EN_MANO).ToUpperInvariant();

        var cartera = await _db.CafeCheques.Include(c => c.ClienteOrigen)
            .OrderByDescending(c => c.CreatedAt).Take(4000).ToListAsync();
        var banco = await _db.CafeChequesBanco
            .OrderByDescending(c => c.Id).Take(4000).ToListAsync();

        var filas = new List<ChequeUniDto>();

        foreach (var c in cartera)
        {
            var (v, estado) = c.Estado switch
            {
                "EN_CARTERA" => (EN_MANO, "En mano"),
                "DEPOSITADO" => (EN_BANCO, "Lo mandé al banco"),
                "ACREDITADO" => (USADOS, "El banco lo acreditó"),
                "COBRADO_VENTANILLA" => (USADOS, "Cobrado por ventanilla"),
                "ENDOSADO" => (USADOS, "Endosado a un proveedor"),
                "RECHAZADO" => (RECHAZADOS, "Rebotó"),
                _ => (USADOS, c.Estado)
            };
            filas.Add(new ChequeUniDto(
                $"C-{c.Id}", "CARTERA", c.Id, c.Numero, c.Banco, c.Emisor,
                c.ClienteOrigenId, c.ClienteOrigen?.Nombre,
                c.Importe, c.FechaVencimiento ?? c.FechaCobro, v, estado,
                PuedeImputar: c.Estado == "EN_CARTERA" && !c.CobranzaOrigenId.HasValue,
                PuedeDepositar: c.Estado == "EN_CARTERA",
                PuedeVentanilla: c.Estado == "EN_CARTERA",
                PuedeEndosar: c.Estado == "EN_CARTERA",
                PuedeRechazar: c.Estado is "EN_CARTERA" or "DEPOSITADO",
                PuedeAcreditar: c.Estado == "DEPOSITADO",
                null, null, null, c.Observaciones));
        }

        foreach (var b in banco)
        {
            var esDisponible = string.Equals(b.Estado, "Disponible", StringComparison.OrdinalIgnoreCase);
            string v; string estado;
            if (b.Tipo == "EMITIDO")
            {
                // Los mios: los que TENGO QUE PAGAR. Solo los que el banco todavia no cobro.
                var porPagar = string.Equals(b.Estado, "Aceptado", StringComparison.OrdinalIgnoreCase) || esDisponible;
                if (string.Equals(b.Estado, "Rechazado", StringComparison.OrdinalIgnoreCase)) { v = RECHAZADOS; estado = "Rebotó"; }
                else if (porPagar) { v = A_PAGAR; estado = "Lo tengo que pagar"; }
                else { v = USADOS; estado = "Ya lo pagué"; }
            }
            else
            {
                // Recibido o endosado por mi. Si ya tiene espejo en cartera, la fila de cartera
                // manda: no la repetimos aca (si no, volviamos a mostrar el mismo cheque dos veces).
                if (b.CafeChequeId.HasValue) continue;
                if (string.Equals(b.Estado, "Rechazado", StringComparison.OrdinalIgnoreCase)) { v = RECHAZADOS; estado = "Rebotó"; }
                else if (esDisponible) { v = EN_MANO; estado = "En mano"; }
                else if (string.Equals(b.Estado, "Endosado", StringComparison.OrdinalIgnoreCase)) { v = USADOS; estado = "Endosado a un proveedor"; }
                else { v = USADOS; estado = b.Estado; }
            }

            filas.Add(new ChequeUniDto(
                $"B-{b.Id}", "BANCO", b.Id, b.Numero, b.BancoEmisor ?? "—",
                b.Tipo == "EMITIDO" ? b.ContraparteNombre : b.LibradorNombre,
                null, b.Tipo == "EMITIDO" ? null : b.ContraparteNombre,
                b.Importe, b.FechaPago, v, estado,
                // Un e-cheq del banco todavia no es un cheque "del sistema", pero la pantalla ofrece
                // las mismas acciones: antes de ejecutarlas lo trae a cartera (traer-a-cartera).
                // Asi el usuario no tiene que imputarselo a un cliente solo para poder endosarlo.
                PuedeImputar: v == EN_MANO,
                PuedeDepositar: v == EN_MANO, PuedeVentanilla: v == EN_MANO, PuedeEndosar: v == EN_MANO,
                PuedeRechazar: v == EN_MANO, PuedeAcreditar: false,
                null, null, null, b.Motivo));
        }

        // ─── Duplicados: el mismo papel por los dos caminos ──────────────────────────
        // Solo tiene sentido entre lo que esta EN MANO: mismo numero (normalizado) + mismo importe.
        var enManoCartera = filas.Where(f => f.Vista == EN_MANO && f.Origen == "CARTERA").ToList();
        var enManoBanco = filas.Where(f => f.Vista == EN_MANO && f.Origen == "BANCO").ToList();
        var pares = new Dictionary<string, (int carteraId, int bancoId, string otraKey)>();
        foreach (var fc in enManoCartera)
        {
            var fb = enManoBanco.FirstOrDefault(x =>
                NormNumero(x.Numero) == NormNumero(fc.Numero) && Math.Abs(x.Importe - fc.Importe) < 0.01m
                && !pares.ContainsKey(x.Key));
            if (fb is null) continue;
            pares[fc.Key] = (fc.Id, fb.Id, fb.Key);
            pares[fb.Key] = (fc.Id, fb.Id, fc.Key);
        }
        if (pares.Count > 0)
        {
            for (int i = 0; i < filas.Count; i++)
            {
                if (!pares.TryGetValue(filas[i].Key, out var par)) continue;
                filas[i] = filas[i] with { DuplicadoKey = par.otraKey, DuplicadoCarteraId = par.carteraId, DuplicadoBancoId = par.bancoId };
            }
        }

        var conteos = new ConteosDto(
            EnMano: filas.Count(f => f.Vista == EN_MANO),
            EnBanco: filas.Count(f => f.Vista == EN_BANCO),
            Usados: filas.Count(f => f.Vista == USADOS),
            Rechazados: filas.Count(f => f.Vista == RECHAZADOS),
            APagar: filas.Count(f => f.Vista == A_PAGAR),
            Duplicados: pares.Count / 2);

        var deLaVista = filas.Where(f => f.Vista == vista).ToList();

        // El resumen del pie es de la VISTA COMPLETA, no de lo buscado: si filtras por un cliente
        // querés seguir viendo cuánto tenés en total.
        DateTime? primerVto = deLaVista.Where(f => f.Vence.HasValue)
            .Select(f => (DateTime?)f.Vence!.Value).OrderBy(d => d).FirstOrDefault();
        var resumen = new ResumenVistaDto(deLaVista.Count, deLaVista.Sum(f => f.Importe), primerVto);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim();
            var soloDigitos = new string(t.Where(char.IsDigit).ToArray());
            deLaVista = deLaVista.Where(f =>
                Coincide(f.Numero, t) || Coincide(f.Banco, t) || Coincide(f.Emisor, t)
                || Coincide(f.ClienteNombre, t) || Coincide(f.Observaciones, t)
                || (soloDigitos.Length > 0 && NormNumero(f.Numero).Contains(soloDigitos))
                || (soloDigitos.Length >= 3 && ((long)f.Importe).ToString().Contains(soloDigitos)))
                .ToList();
        }

        deLaVista = deLaVista
            .OrderBy(f => f.Vence ?? DateTime.MaxValue)
            .ThenBy(f => f.Id)
            .ToList();

        return Ok(new UnificadoResponse(conteos, resumen, deLaVista));
    }

    /// <summary>
    /// Trae un e-cheq del banco a la cartera del sistema: crea el CafeCheque "espejo" en
    /// EN_CARTERA vinculado al e-cheq, SIN cobranza (no toca la deuda de ningun cliente ni
    /// mueve caja — igual que un alta manual). Hace falta porque las acciones (depositar,
    /// ventanilla, endosar, rebotar) viven sobre CafeCheque: hasta ahora, para endosar un
    /// cheque que solo estaba en el listado del banco, habia que imputarselo a un cliente
    /// primero. Devuelve el id del cheque de cartera; si ya existia, devuelve ese.
    /// </summary>
    [HttpPost("traer-a-cartera/{echeqId:int}")]
    public async Task<IActionResult> TraerACartera(int echeqId)
    {
        var b = await _db.CafeChequesBanco.FindAsync(echeqId);
        if (b is null) return NotFound(new { error = "No encontré el cheque del banco" });
        if (b.CafeChequeId.HasValue) return Ok(new { chequeId = b.CafeChequeId.Value, yaEstaba = true });
        if (b.Tipo == "EMITIDO")
            return BadRequest(new { error = "Ese cheque lo firmaste vos, no es un cheque que hayas recibido" });
        if (!string.Equals(b.Estado, "Disponible", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = $"El cheque no está disponible en el banco (está {b.Estado})" });

        var ch = new CafeCheque
        {
            Numero = b.Numero,
            Banco = b.BancoEmisor ?? "(sin nombre)",
            BancoId = b.BancoId,
            Emisor = b.LibradorNombre,
            Importe = b.Importe,
            FechaCobro = b.FechaPago,
            FechaVencimiento = b.FechaPago,
            Estado = "EN_CARTERA",
            FechaCambioEstado = DateTime.UtcNow,
            ChequeBancoId = b.Id,
            Observaciones = $"Del listado del banco (ID banco: {b.IdBanco})",
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeCheques.Add(ch);
        await _db.SaveChangesAsync();

        b.CafeChequeId = ch.Id;
        b.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await _audit.LogAsync("CafeCheque", ch.Id.ToString(), "TRAER_A_CARTERA",
            $"E-cheq del banco #{b.Id} ({b.BancoEmisor} N° {b.Numero} por ${b.Importe:N2}) traído a cartera");
        return Ok(new { chequeId = ch.Id, yaEstaba = false });
    }

    public record UnirRequest(int ChequeCarteraId, int ChequeBancoId);

    /// <summary>Une el cheque de cartera con su e-cheq del banco: es el mismo papel cargado por
    /// los dos caminos. Deja UNA sola fila (la de cartera, que es la que tiene las acciones) y
    /// saca el e-cheq de "en mano".</summary>
    [HttpPost("unir")]
    public async Task<IActionResult> Unir([FromBody] UnirRequest req)
    {
        var c = await _db.CafeCheques.FindAsync(req.ChequeCarteraId);
        if (c is null) return NotFound(new { error = "No encontré el cheque de cartera" });
        var b = await _db.CafeChequesBanco.FindAsync(req.ChequeBancoId);
        if (b is null) return NotFound(new { error = "No encontré el cheque del banco" });
        if (b.CafeChequeId.HasValue)
            return BadRequest(new { error = "El cheque del banco ya está unido a otro" });
        if (c.ChequeBancoId.HasValue)
            return BadRequest(new { error = "El cheque de cartera ya está unido a otro" });
        if (Math.Abs(c.Importe - b.Importe) > 0.01m || NormNumero(c.Numero) != NormNumero(b.Numero))
            return BadRequest(new { error = "No son el mismo cheque: no coinciden el número y el importe" });

        c.ChequeBancoId = b.Id;
        c.UpdatedAt = DateTime.UtcNow;
        b.CafeChequeId = c.Id;
        // Si el de cartera ya venia de una cobranza, el e-cheq hereda ese vinculo.
        if (c.CobranzaOrigenId.HasValue && !b.CobranzaId.HasValue) b.CobranzaId = c.CobranzaOrigenId;
        // Datos que suele tener el banco y no el alta a mano.
        if (string.IsNullOrWhiteSpace(c.Emisor) && !string.IsNullOrWhiteSpace(b.LibradorNombre)) c.Emisor = b.LibradorNombre;
        if (!c.FechaVencimiento.HasValue && b.FechaPago.HasValue) c.FechaVencimiento = b.FechaPago;
        b.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await _audit.LogAsync("CafeCheque", c.Id.ToString(), "UNIR_DUPLICADO",
            $"Cheque {c.Banco} N° {c.Numero} por ${c.Importe:N2} unido con el e-cheq del banco #{b.Id} (ID banco {b.IdBanco})");
        return Ok(new { ok = true });
    }
}
