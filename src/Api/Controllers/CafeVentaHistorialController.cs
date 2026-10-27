using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 27/10/2026: "Ver historial" de una venta, de punta a punta: carga, armado, reparto, entrega,
/// cobranzas, notas de crédito, envíos y modificaciones. Junta lo que ya se guardaba en cada tabla
/// (sirve también para ventas viejas) con Cafe_VentaHistorial (lo nuevo: cambios, quién la pasó a quién).
/// También el aviso antes de asignar una venta vieja o ya entregada (caso 2110 del 27/10).
/// </summary>
[ApiController]
[Route("api/cafe/ventas")]
[Authorize]
public class CafeVentaHistorialController : ControllerBase
{
    private readonly AppDbContext _db;
    public CafeVentaHistorialController(AppDbContext db) { _db = db; }

    /// <summary>Un renglón del historial. Cuando = UTC. Tipo: CARGA | ARMADO | REPARTO | ENTREGA | COBRO |
    /// EDICION | ESTADO | ENVIO | NC. Tachado = cobro anulado/rechazado. Alerta = para pintar en rojo.</summary>
    public record HistorialItemDto(DateTime Cuando, string Tipo, string? Quien, string? Desde, string Texto,
        List<string> Detalle, bool Tachado = false, bool Alerta = false);

    public record HistorialDto(int VentaId, string Numero, string? Cliente, decimal Total, List<HistorialItemDto> Items);

    [HttpGet("{id:int}/historial")]
    public async Task<IActionResult> Historial(int id)
    {
        var v = await _db.CafeVentas.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return NotFound(new { error = "Venta no encontrada" });

        var items = new List<HistorialItemDto>();
        var reps = await _db.CafeRepartidores.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Nombre);
        string Rep(int? rid) => rid.HasValue && reps.TryGetValue(rid.Value, out var n) ? n : "repartidor";
        var total = (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m) ? v.ArcaImpTotal.Value : v.Total;

        // ---- Lo nuevo (desde el 27/10/2026) ----
        var propias = await _db.CafeVentaHistorial.AsNoTracking().Where(h => h.VentaId == id).OrderBy(h => h.Id).ToListAsync();
        DateTime? Primera(string tipo) => propias.Where(h => h.Tipo == tipo).Select(h => (DateTime?)h.CreatedAt).Min();
        var desdeReparto = Primera("REPARTO");
        var hayEntregaNueva = propias.Any(h => h.Tipo == "ENTREGA");
        foreach (var h in propias)
        {
            var lineas = h.Detalle.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
            var texto = lineas.Count == 1 ? lineas[0] : h.Tipo switch
            {
                "EDICION" => "modificó la venta",
                _ => lineas[0],
            };
            var detalle = lineas.Count == 1 ? new List<string>() : (h.Tipo == "EDICION" ? lineas : lineas.Skip(1).ToList());
            var alerta = h.Tipo == "ESTADO" && texto.StartsWith("anuló")
                         || h.Detalle.Contains("ya figuraba entregada") || h.Detalle.Contains("desmarcó");
            items.Add(new HistorialItemDto(h.CreatedAt, h.Tipo, Nombre(h.Quien), h.Desde, texto, detalle, Alerta: alerta));
        }

        // ---- Carga ----
        {
            var t = $"cargó la venta · {v.TipoComprobante} · {VentaHistorialRecorder.Plata(total)}";
            var det = new List<string>();
            if (v.OrigenVentaId.HasValue)
            {
                var o = await _db.CafeVentas.AsNoTracking().Where(x => x.Id == v.OrigenVentaId).Select(x => x.Numero).FirstOrDefaultAsync();
                if (o is not null) det.Add($"viene de la cotización {o}");
            }
            if (v.VentaOrigenNcId.HasValue)
            {
                var o = await _db.CafeVentas.AsNoTracking().Where(x => x.Id == v.VentaOrigenNcId).Select(x => x.Numero).FirstOrDefaultAsync();
                if (o is not null) det.Add($"es la nota de crédito de {o}");
            }
            items.Add(new HistorialItemDto(v.CreatedAt, "CARGA", Nombre(v.CreadoPorOperador), "compu", t, det));
        }
        if (v.DriveSubidoAt.HasValue)
            items.Add(new HistorialItemDto(v.DriveSubidoAt.Value, "CARGA", null, "sistema",
                v.DriveSubidasCount > 1 ? $"se guardó el PDF en Drive (la última de {v.DriveSubidasCount} veces)" : "se guardó el PDF en Drive", new()));
        if (v.ImpresaAt.HasValue)
            items.Add(new HistorialItemDto(v.ImpresaAt.Value, "ARMADO", null, "compu",
                v.ImpresaCount > 1 ? $"se imprimió (la última de {v.ImpresaCount} veces)" : "se imprimió", new()));
        if (v.FacturadaComoVentaId.HasValue)
        {
            var f = await _db.CafeVentas.AsNoTracking().Where(x => x.Id == v.FacturadaComoVentaId)
                .Select(x => new { x.Numero, x.CreatedAt, x.CreadoPorOperador }).FirstOrDefaultAsync();
            if (f is not null) items.Add(new HistorialItemDto(f.CreatedAt, "CARGA", Nombre(f.CreadoPorOperador), "compu", $"se pasó a factura {f.Numero}", new()));
        }

        // ---- Armado ----
        var logs = await _db.CafeVentaPreparacionLogs.AsNoTracking().Where(l => l.VentaId == id).OrderBy(l => l.Id).ToListAsync();
        foreach (var l in logs)
        {
            if (l.EstadoNuevo == "ENTREGADO") continue; // la entrega va con su propio renglón
            var texto = l.EstadoNuevo switch
            {
                "PARA_PREPARAR" when l.EstadoAnterior is null => "entró a Para armar",
                "PARA_PREPARAR" when l.EstadoAnterior == "PARA_PREPARAR" => "",
                "PARA_PREPARAR" => "volvió a Para armar",
                "EN_PREPARACION" => "empezó a armarla",
                "LISTO" => "quedó armada",
                "EN_CAMINO" => "salió a reparto",
                _ => l.EstadoNuevo.ToLowerInvariant(),
            };
            if (texto.Length == 0) continue;
            var (quien, desde) = OperadorArmado(l.OperadorNombre);
            var det = new List<string>();
            if (!string.IsNullOrWhiteSpace(l.Notas) && !l.Notas.StartsWith("Entro al tablero")) det.Add(l.Notas.Trim());
            items.Add(new HistorialItemDto(l.CreatedAt, "ARMADO", quien, desde, texto, det));
        }

        // ---- Reparto viejo (antes de que se anotara en el historial) ----
        var escaneos = await _db.CafeQrEscaneos.AsNoTracking().Where(e => e.VentaId == id && e.Accion == "cargado").ToListAsync();
        foreach (var e in escaneos.Where(e => desdeReparto is null || e.CreatedAt < desdeReparto.Value.AddSeconds(-5)))
        {
            var ip = e.Ip ?? "";
            if (ip == "admin-reasignar") items.Add(new(e.CreatedAt, "REPARTO", null, "compu", $"se la asignaron a {Rep(e.RepartidorId)}", new()));
            else if (ip == "mapeo-asignar") items.Add(new(e.CreatedAt, "REPARTO", null, "mapeo", $"se la asignaron a {Rep(e.RepartidorId)}", new()));
            else if (ip.StartsWith("link-")) items.Add(new(e.CreatedAt, "REPARTO", ip[5..], "celu", $"se la asignó a {Rep(e.RepartidorId)}", new()));
            else items.Add(new(e.CreatedAt, "REPARTO", Rep(e.RepartidorId), "celu", "se la cargó (escaneó el QR)", new()));
        }
        var rechazos = await _db.CafeRepartidorRechazos.AsNoTracking()
            .Where(r => r.Origen == "venta_cafe" && r.ReferenciaId == id).ToListAsync();
        foreach (var r in rechazos)
        {
            var det = string.IsNullOrWhiteSpace(r.Descripcion) ? new List<string>() : new List<string> { r.Descripcion!.Trim() };
            items.Add(new(r.CreatedAt, "REPARTO", Rep(r.RepartidorId), "celu", $"la rechazó · {r.Motivo}", det, Alerta: true));
        }

        // ---- Entrega (la que figura hoy, si no quedó anotada en el historial nuevo) ----
        if (v.EntregadoAt.HasValue)
        {
            var det = new List<string>();
            if (!string.IsNullOrWhiteSpace(v.NombreReceptor))
                det.Add($"recibió {v.NombreReceptor.Trim()}{(string.IsNullOrWhiteSpace(v.DniReceptor) ? "" : $" (DNI {v.DniReceptor.Trim()})")}{(v.EntregaFirmadaAt.HasValue ? " · firmó" : "")}");
            if (!string.IsNullOrWhiteSpace(v.MotivoSinFirma)) det.Add($"sin firma: {v.MotivoSinFirma.Trim()}");
            if (!hayEntregaNueva)
            {
                if (!string.IsNullOrWhiteSpace(v.ComentarioEntrega)) det.Insert(0, $"comentario: \"{v.ComentarioEntrega.Trim()}\"");
                var quien = v.EntregadoPorRepartidorId.HasValue ? Rep(v.EntregadoPorRepartidorId) : null;
                var texto = quien is not null ? "la entregó" : (v.Retira ? "la retiró el cliente" : "quedó entregada (sin decir quién)");
                items.Add(new(v.EntregadoAt.Value, "ENTREGA", quien, null, texto, det));
            }
            else if (det.Count > 0)
            {
                items.Add(new(v.EntregaFirmadaAt ?? v.EntregadoAt.Value, "ENTREGA", null, "celu", det[0], det.Skip(1).ToList()));
            }
        }

        // ---- Cobros ----
        var saldoMovs = new List<(DateTime cuando, decimal importe)>();
        var pendientes = await _db.CafeCobranzasPendientes.AsNoTracking().Where(p => p.VentaId == id).ToListAsync();
        var cobranzasDePendientes = pendientes.Where(p => p.CobranzaCreadaId.HasValue).Select(p => p.CobranzaCreadaId!.Value).ToHashSet();
        foreach (var p in pendientes)
        {
            var det = new List<string>();
            if (!string.IsNullOrWhiteSpace(p.Notas)) det.Add(p.Notas.Trim());
            var estado = p.Estado switch
            {
                "APROBADA" => $"lo aprobó {Nombre(p.RevisadaPor) ?? "la oficina"}{(p.RevisadaAt.HasValue ? $" el {VentaHistorialRecorder.Ar(p.RevisadaAt.Value):dd/MM HH:mm}" : "")}",
                "RECHAZADA" => $"lo rechazó {Nombre(p.RevisadaPor) ?? "la oficina"}{(string.IsNullOrWhiteSpace(p.RechazadaMotivo) ? "" : $": {p.RechazadaMotivo}")}",
                _ => "falta que la oficina lo apruebe",
            };
            det.Add(estado);
            items.Add(new(p.CreatedAt, "COBRO", Rep(p.RepartidorId), "celu",
                $"cobró {VentaHistorialRecorder.Plata(p.Importe)} en efectivo", det, Tachado: p.Estado == "RECHAZADA"));
        }

        var aplicaciones = await _db.CafeCobranzasComprobantes.AsNoTracking()
            .Where(c => c.VentaId == id)
            .Select(c => new { c.CobranzaId, c.Importe })
            .ToListAsync();
        var cobIds = aplicaciones.Select(a => a.CobranzaId).Distinct().ToList();
        var cobranzas = await _db.CafeCobranzas.AsNoTracking().Where(c => cobIds.Contains(c.Id)).ToListAsync();
        var medios = await _db.CafeCobranzasMedios.AsNoTracking().Where(m => cobIds.Contains(m.CobranzaId))
            .Include(m => m.Caja).Include(m => m.Cheque).ToListAsync();
        var adjuntos = await _db.CafeCobranzaAdjuntos.AsNoTracking().Where(a => cobIds.Contains(a.CobranzaId))
            .GroupBy(a => a.CobranzaId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        foreach (var c in cobranzas)
        {
            var aplicado = aplicaciones.Where(a => a.CobranzaId == c.Id).Sum(a => a.Importe);
            var vigente = c.Estado == "VIGENTE";
            if (vigente) saldoMovs.Add((c.CreatedAt, aplicado));
            if (cobranzasDePendientes.Contains(c.Id) && vigente) continue; // ya está como "cobró en efectivo"

            var det = new List<string>();
            foreach (var m in medios.Where(m => m.CobranzaId == c.Id))
            {
                var caja = m.Caja?.Nombre ?? "caja";
                var linea = m.Cheque is not null
                    ? $"cheque {m.Cheque.Banco} Nº {m.Cheque.Numero}{(m.Cheque.FechaCobro.HasValue ? $" para cobrar el {m.Cheque.FechaCobro.Value:dd/MM/yyyy}" : "")} · {VentaHistorialRecorder.Plata(m.Importe)}"
                    : $"{caja} · {VentaHistorialRecorder.Plata(m.Importe)}";
                if (!string.IsNullOrWhiteSpace(m.Referencia)) linea += $" · {m.Referencia.Trim()}";
                if (!string.IsNullOrWhiteSpace(m.RedirigidoDestino)) linea += $" · redirigido a {m.RedirigidoDestino.Trim()}";
                det.Add(linea);
            }
            if (c.Total > aplicado + 0.01m) det.Add($"la cobranza fue de {VentaHistorialRecorder.Plata(c.Total)} en total (cubre otras ventas)");
            if (c.Retenciones > 0) det.Add($"retenciones {VentaHistorialRecorder.Plata(c.Retenciones)}");
            if (adjuntos.TryGetValue(c.Id, out var na)) det.Add(na == 1 ? "tiene un comprobante adjunto" : $"tiene {na} comprobantes adjuntos");
            if (!string.IsNullOrWhiteSpace(c.Observaciones)) det.Add(c.Observaciones.Trim());
            if (VentaHistorialRecorder.Ar(c.CreatedAt).Date != c.Fecha.Date) det.Add($"con fecha {c.Fecha:dd/MM/yyyy}");
            if (!vigente) det.Add("anulada");
            items.Add(new(c.CreatedAt, "COBRO", Nombre(c.Operador), "compu",
                $"cobranza {c.Numero} · {VentaHistorialRecorder.Plata(aplicado)}", det, Tachado: !vigente));
        }

        // ---- Notas de crédito ----
        var ncs = await _db.CafeVentas.AsNoTracking().Where(x => x.VentaOrigenNcId == id && x.Estado != "anulado")
            .Select(x => new { x.Numero, x.CreatedAt, x.CreadoPorOperador, x.Total, x.ArcaImpTotal }).ToListAsync();
        foreach (var nc in ncs)
        {
            var imp = (nc.ArcaImpTotal ?? 0m) > 0m ? nc.ArcaImpTotal!.Value : nc.Total;
            saldoMovs.Add((nc.CreatedAt, imp));
            items.Add(new(nc.CreatedAt, "NC", Nombre(nc.CreadoPorOperador), "compu", $"nota de crédito {nc.Numero} · {VentaHistorialRecorder.Plata(imp)}", new()));
        }

        // Saldo después de cada pago (solo ventas que se cobran: no cotizaciones ni notas de crédito).
        if (saldoMovs.Count > 0 && !v.TipoComprobante.StartsWith("NC") && v.Estado != "anulado")
        {
            var saldo = total;
            foreach (var (cuando, imp) in saldoMovs.OrderBy(s => s.cuando))
            {
                saldo -= imp;
                var idx = items.FindLastIndex(i => i.Cuando == cuando && (i.Tipo == "COBRO" || i.Tipo == "NC"));
                var leyenda = saldo <= 0.01m ? "queda pagada" : $"queda debiendo {VentaHistorialRecorder.Plata(saldo)}";
                if (idx >= 0) items[idx].Detalle.Add(leyenda);
                else
                {
                    // Cobranza que salió de un cobro del repartidor: la leyenda va en ese renglón.
                    var p = items.FindLastIndex(i => i.Tipo == "COBRO" && !i.Tachado && i.Cuando <= cuando);
                    if (p >= 0) items[p].Detalle.Add(leyenda);
                }
            }
        }

        // ---- Envíos (mail / WhatsApp) ----
        var envios = await _db.CafeVentasEnvios.AsNoTracking().Where(e => e.VentaId == id && e.Estado != "CANCELADO" && e.Estado != "PENDIENTE").ToListAsync();
        foreach (var e in envios)
        {
            var canal = e.Canal == "EMAIL" ? "mail" : "WhatsApp";
            var ok = e.Estado == "ENVIADO";
            var texto = ok ? $"se mandó por {canal}{(string.IsNullOrWhiteSpace(e.Destino) ? "" : $" a {e.Destino}")}"
                           : $"no se pudo mandar por {canal}{(string.IsNullOrWhiteSpace(e.Destino) ? "" : $" a {e.Destino}")}";
            items.Add(new(e.EnviadoAt ?? e.UpdatedAt, "ENVIO", null, e.Automatico ? "sistema" : "compu", texto, new(), Alerta: !ok));
        }

        var orden = items.Select((it, i) => (it, i)).OrderBy(x => x.it.Cuando).ThenBy(x => x.i).Select(x => x.it).ToList();
        return Ok(new HistorialDto(v.Id, v.Numero, v.ClienteNombreSnapshot, total, orden));
    }

    public record AvisoAsignarDto(bool Avisar, string Numero, DateTime Fecha, int Dias, DateTime? EntregadoAt,
        string? EntregadoPor, int? OtraVentaId, string? OtraVentaNumero, DateTime? OtraVentaFecha);

    /// <summary>Antes de asignar una venta a un repartidor: avisa si ya figura entregada o si es de hace
    /// 3 días o más, y ofrece otra venta más nueva del mismo cliente que todavía nadie tiene.</summary>
    [HttpGet("{id:int}/aviso-asignar")]
    public async Task<IActionResult> AvisoAsignar(int id)
    {
        var a = await CalcularAvisoAsync(_db, id);
        return a is null ? NotFound(new { error = "Venta no encontrada" }) : Ok(a);
    }

    internal static async Task<AvisoAsignarDto?> CalcularAvisoAsync(AppDbContext db, int id)
    {
        var v = await db.CafeVentas.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return null;
        var hoy = VentaHistorialRecorder.Ar(DateTime.UtcNow).Date;
        var dias = (hoy - v.Fecha.Date).Days;
        string? entregadoPor = null;
        if (v.EntregadoPorRepartidorId.HasValue)
            entregadoPor = await db.CafeRepartidores.AsNoTracking().Where(r => r.Id == v.EntregadoPorRepartidorId).Select(r => r.Nombre).FirstOrDefaultAsync();
        else if (v.EntregadoAt.HasValue && v.Retira) entregadoPor = "retiró el cliente";

        var avisar = v.EntregadoAt.HasValue || dias >= 3;
        int? otraId = null; string? otraNum = null; DateTime? otraFecha = null;
        if (avisar && v.ClienteId.HasValue)
        {
            var conRepartidor = db.CafeQrEscaneos.Where(e => e.Accion == "cargado").Select(e => e.VentaId);
            var otra = await db.CafeVentas.AsNoTracking()
                .Where(x => x.ClienteId == v.ClienteId && x.Id != v.Id && x.Estado != "anulado"
                            && x.EntregadoAt == null && x.Fecha >= v.Fecha && !x.TipoComprobante.StartsWith("NC")
                            && x.FacturadaComoVentaId == null && !conRepartidor.Contains(x.Id))
                .OrderByDescending(x => x.Fecha).ThenByDescending(x => x.Id)
                .Select(x => new { x.Id, x.Numero, x.Fecha }).FirstOrDefaultAsync();
            if (otra is not null) { otraId = otra.Id; otraNum = otra.Numero; otraFecha = otra.Fecha; }
        }
        return new AvisoAsignarDto(avisar, v.Numero, v.Fecha, dias, v.EntregadoAt, entregadoPor, otraId, otraNum, otraFecha);
    }

    // "OSMAR" -> "Osmar"; "german" -> "German". Los nombres de sistema quedan como están.
    private static string? Nombre(string? n)
    {
        if (string.IsNullOrWhiteSpace(n)) return null;
        n = n.Trim();
        if (n is "sistema" or "sin nombre") return n;
        return string.Join(" ", n.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Length <= 1 ? p.ToUpperInvariant() : char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
    }

    private static (string? quien, string? desde) OperadorArmado(string? op)
    {
        if (string.IsNullOrWhiteSpace(op)) return (null, null);
        op = op.Trim();
        if (op.StartsWith("repartidor:", StringComparison.OrdinalIgnoreCase)) return (Nombre(op[11..]), "celu");
        if (op.StartsWith("admin", StringComparison.OrdinalIgnoreCase)) return (null, "compu");
        return (Nombre(op), "compu");
    }
}
