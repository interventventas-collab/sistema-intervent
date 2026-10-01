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
    public const string TODOS = "TODOS";   // 2026-10-01: todas las solapas juntas

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
        string? Observaciones,
        // 2026-10-01: el banco ya lo muestra endosado a alguien y aca sigue "en mano"
        // (se endoso desde el home banking y nadie lo anoto). A quien, y si es un proveedor nuestro.
        string? BancoEndosadoA = null,
        int? BancoEndosadoProveedorId = null);

    public record ConteosDto(int EnMano, int EnBanco, int Usados, int Rechazados, int APagar, int Duplicados,
        int EndososSinAnotar = 0, decimal EndososSinAnotarImporte = 0m, int Todos = 0);
    public record ResumenVistaDto(int Cantidad, decimal Importe, DateTime? PrimerVencimiento);
    /// <summary>La linea de arriba: lo que tengo, lo que vence esta semana y lo que tengo que pagar.</summary>
    public record CabeceraDto(int EnManoCant, decimal EnManoImporte, decimal VenceSemanaImporte,
        int APagarCant, decimal APagarImporte);
    /// <summary>Un dia del listado de proximos vencimientos: lo que entra y lo que tengo que pagar.</summary>
    public record VtoDiaDto(DateTime Fecha, decimal Entra, decimal Pago);
    public record UnificadoResponse(ConteosDto Conteos, ResumenVistaDto Resumen, List<ChequeUniDto> Filas,
        CabeceraDto Cabecera, List<VtoDiaDto> Proximos);

    /// <summary>Numero comparable: sin espacios, guiones ni ceros a la izquierda. El banco y el
    /// usuario escriben el mismo numero de formas distintas ("0004471209" vs "4471209").</summary>
    private static string NormNumero(string? n)
    {
        var s = new string((n ?? "").Where(char.IsLetterOrDigit).ToArray()).TrimStart('0');
        return s.Length == 0 ? "0" : s;
    }

    /// <summary>El banco ya lo mando a otro: "Endoso enviado", "Endosado", o un renglon del listado de endosados.</summary>
    private static bool BancoLoEndoso(CafeChequeBanco b)
        => b.Tipo == "ENDOSADO" || (b.Estado ?? "").StartsWith("Endos", StringComparison.OrdinalIgnoreCase);

    private static string SoloDigitos(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

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

        // 2026-10-01: endosos hechos desde el home banking que no se anotaron aca. El banco trae a
        // quien fue (ContraparteCuit); lo cruzamos con los proveedores por CUIT. Solo los recientes:
        // los endosos de antes de mayo son historia del sistema viejo.
        var desdeEndoso = DateTime.UtcNow.Date.AddDays(-60);
        var endososBanco = banco.Where(b => b.Tipo != "EMITIDO" && BancoLoEndoso(b)
            && (b.FechaPago ?? b.UpdatedAt ?? b.CreatedAt) >= desdeEndoso).ToList();
        var provPorCuit = new Dictionary<string, (int Id, string Nombre)>();
        if (endososBanco.Count > 0)
        {
            var provs = await _db.CafeProveedores.AsNoTracking()
                .Where(p => p.Cuit != null && p.Cuit != "")
                .Select(p => new { p.Id, p.Nombre, p.Cuit, p.CuentaCorriente }).ToListAsync();
            // Si hay dos fichas con el mismo CUIT, gana la que lleva cuenta corriente.
            foreach (var p in provs.OrderByDescending(p => p.CuentaCorriente))
            {
                var k = SoloDigitos(p.Cuit);
                if (k.Length > 0 && !provPorCuit.ContainsKey(k)) provPorCuit[k] = (p.Id, p.Nombre);
            }
        }
        (string? a, int? provId) EndosadoEnBanco(CafeChequeBanco b)
        {
            var nombre = string.IsNullOrWhiteSpace(b.ContraparteNombre) ? b.BeneficiarioActualNombre : b.ContraparteNombre;
            var cuit = SoloDigitos(string.IsNullOrWhiteSpace(b.ContraparteCuit) ? b.BeneficiarioActualCuit : b.ContraparteCuit);
            if (provPorCuit.TryGetValue(cuit, out var p)) return (p.Nombre, p.Id);
            return (nombre ?? "un tercero", null);
        }

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
            string? endA = null; int? endProvId = null;
            if (c.Estado == "EN_CARTERA" && endososBanco.Count > 0)
            {
                var eb = endososBanco.FirstOrDefault(b => c.ChequeBancoId.HasValue && b.Id == c.ChequeBancoId.Value)
                      ?? endososBanco.FirstOrDefault(b => NormNumero(b.Numero) == NormNumero(c.Numero) && Math.Abs(b.Importe - c.Importe) < 0.01m);
                if (eb is not null) (endA, endProvId) = EndosadoEnBanco(eb);
            }
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
                null, null, null, c.Observaciones, endA, endProvId));
            // Ya salio del banco: lo unico que queda por hacer es anotar a quien se endoso.
            if (endA is not null)
                filas[^1] = filas[^1] with { PuedeImputar = false, PuedeDepositar = false, PuedeVentanilla = false, PuedeRechazar = false };
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
                // 2026-10-01: endosado desde el home banking y sin anotar: sigue "en mano" para el
                // sistema hasta que se anote a quien se le pago con el.
                else if (b.Tipo == "RECIBIDO" && BancoLoEndoso(b) && endososBanco.Contains(b)) { v = EN_MANO; estado = "En mano"; }
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
                // 2026-09-25: tambien el que el banco ya muestra usado sin haber pasado por una
                // cobranza (se cobro o endoso el mismo dia que llego). Solo los de los ultimos
                // 60 dias: los de antes de mayo son historia y se cobraron por el sistema viejo.
                PuedeImputar: v == EN_MANO || (b.Tipo != "EMITIDO" && !b.CobranzaId.HasValue
                    && string.Equals(b.Estado, "Pagado", StringComparison.OrdinalIgnoreCase)
                    && b.FechaPago >= DateTime.UtcNow.Date.AddDays(-60)),
                PuedeDepositar: v == EN_MANO, PuedeVentanilla: v == EN_MANO, PuedeEndosar: v == EN_MANO,
                PuedeRechazar: v == EN_MANO, PuedeAcreditar: false,
                null, null, null, b.Motivo));
            if (v == EN_MANO && !esDisponible)
            {
                var (a, pid) = EndosadoEnBanco(b);
                // ClienteNombre venia de ContraparteNombre, que ahora es a quien se endoso, no quien lo dio.
                filas[^1] = filas[^1] with { ClienteNombre = null, PuedeImputar = false, PuedeDepositar = false, PuedeVentanilla = false, PuedeRechazar = false,
                    BancoEndosadoA = a, BancoEndosadoProveedorId = pid };
            }
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
            Duplicados: pares.Count / 2,
            EndososSinAnotar: filas.Count(f => f.Vista == EN_MANO && f.BancoEndosadoA is not null),
            EndososSinAnotarImporte: filas.Where(f => f.Vista == EN_MANO && f.BancoEndosadoA is not null).Sum(f => f.Importe),
            Todos: filas.Count);

        // 2026-09-25: la linea de arriba y los proximos vencimientos (lo que antes habia que ir
        // a buscar al calendario de la portada). "Entra" = lo que tengo en mano o en el banco.
        var hoy = ProveedorCtaCteService.HoyAr();
        var entran = filas.Where(f => f.Vista is EN_MANO or EN_BANCO).ToList();
        var pagos = filas.Where(f => f.Vista == A_PAGAR).ToList();
        var cabecera = new CabeceraDto(
            EnManoCant: filas.Count(f => f.Vista == EN_MANO),
            EnManoImporte: filas.Where(f => f.Vista == EN_MANO).Sum(f => f.Importe),
            VenceSemanaImporte: entran.Where(f => f.Vence.HasValue && f.Vence.Value.Date >= hoy && f.Vence.Value.Date <= hoy.AddDays(7)).Sum(f => f.Importe),
            APagarCant: pagos.Count,
            APagarImporte: pagos.Sum(f => f.Importe));
        var proximos = entran.Select(f => (f.Vence, Entra: f.Importe, Pago: 0m))
            .Concat(pagos.Select(f => (f.Vence, Entra: 0m, Pago: f.Importe)))
            .Where(x => x.Vence.HasValue && x.Vence.Value.Date >= hoy)
            .GroupBy(x => x.Vence!.Value.Date)
            .OrderBy(g => g.Key).Take(8)
            .Select(g => new VtoDiaDto(g.Key, g.Sum(x => x.Entra), g.Sum(x => x.Pago)))
            .ToList();

        var deLaVista = vista == TODOS ? filas.ToList() : filas.Where(f => f.Vista == vista).ToList();

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

        // 2026-10-01: lo que ya paso (usados, rebotados, todos) va con lo mas nuevo arriba; lo que
        // todavia tengo que hacer (en mano, en el banco, a pagar) con lo proximo a vencer arriba.
        deLaVista = vista is USADOS or RECHAZADOS or TODOS
            ? deLaVista.OrderByDescending(f => f.Vence ?? DateTime.MinValue).ThenByDescending(f => f.Id).ToList()
            : deLaVista.OrderBy(f => f.Vence ?? DateTime.MaxValue).ThenBy(f => f.Id).ToList();

        return Ok(new UnificadoResponse(conteos, resumen, deLaVista, cabecera, proximos));
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
        // 2026-10-01: tambien el que ya se endoso desde el home banking, para poder anotar el endoso.
        if (!string.Equals(b.Estado, "Disponible", StringComparison.OrdinalIgnoreCase) && !BancoLoEndoso(b))
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
