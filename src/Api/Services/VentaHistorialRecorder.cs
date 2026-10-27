using System.Globalization;
using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 27/10/2026: anota en Cafe_VentaHistorial lo que cambia de una venta, justo antes de guardarlo.
/// Lo llama AppDbContext.SaveChanges, así que cubre TODOS los caminos (compu, celu del repartidor,
/// Mapeo, cobranzas…) sin tener que acordarse en cada pantalla. Compara cómo estaba con cómo queda:
/// campos de la venta, renglones (productos/cantidades/precios), a quién se asignó o se pasó y la entrega.
/// Caso que lo originó: al pasar la 2110 a otro repartidor se borró quién la tenía y no se supo más.
/// </summary>
public static class VentaHistorialRecorder
{
    // La tabla la crea init.sql; si en algún lado todavía no existe, no anotamos (y la venta se guarda igual).
    private static bool _tablaOk;
    private static DateTime _tablaMiradaAt = DateTime.MinValue;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void Capturar(AppDbContext db, IHttpContextAccessor? http)
    {
        try { CapturarInterno(db, http); }
        catch (Exception ex)
        {
            // el historial nunca puede frenar que se guarde la venta; queda en el log de la API
            Console.WriteLine($"[historial venta] no se pudo anotar: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void CapturarInterno(AppDbContext db, IHttpContextAccessor? http)
    {
        var ct = db.ChangeTracker;
        if (ct.AutoDetectChangesEnabled) ct.DetectChanges();

        var ventasMod = ct.Entries<CafeVenta>().Where(e => e.State == EntityState.Modified).ToList();
        var ventasNuevas = ct.Entries<CafeVenta>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToHashSet();
        // Venta borrada: sus renglones y escaneos se van con ella, no son "cambios".
        var ventasBorradas = ct.Entries<CafeVenta>().Where(e => e.State == EntityState.Deleted).Select(e => e.Entity.Id).ToHashSet();
        var items = ct.Entries<CafeVentaItem>()
            .Where(e => e.State is EntityState.Added or EntityState.Deleted or EntityState.Modified)
            .Where(e => e.Entity.VentaId > 0 && !ventasBorradas.Contains(e.Entity.VentaId)
                        && (e.Entity.VentaNav is null || !ventasNuevas.Contains(e.Entity.VentaNav)))
            .ToList();
        var escaneos = ct.Entries<CafeQrEscaneo>()
            .Where(e => (e.State is EntityState.Added or EntityState.Deleted) && e.Entity.Accion == "cargado"
                        && e.Entity.VentaId > 0 && !ventasBorradas.Contains(e.Entity.VentaId))
            .ToList();
        var logsSinNombre = ct.Entries<CafeVentaPreparacionLog>()
            .Where(e => e.State == EntityState.Added && string.IsNullOrWhiteSpace(e.Entity.OperadorNombre))
            .ToList();
        var pend = db.HistorialPendiente as Pendiente;
        if (ventasMod.Count == 0 && items.Count == 0 && escaneos.Count == 0 && logsSinNombre.Count == 0 && pend is null) return;

        var (quien, desde) = Actor(db, http);

        // Armado: había renglones sin nombre ("quedó armada · no se sabe quién").
        if (quien != "sistema")
            foreach (var l in logsSinNombre) l.Entity.OperadorNombre = quien.Length > 120 ? quien[..120] : quien;

        if (ventasMod.Count == 0 && items.Count == 0 && escaneos.Count == 0 && pend is null) return;
        if (!TablaExiste(db)) return;

        // Nombres de repartidores que aparecen (escaneos y entregas).
        var repIds = escaneos.Select(e => e.Entity.RepartidorId)
            .Concat(escaneos.Where(e => e.State == EntityState.Deleted).Select(e => (int)e.OriginalValues[nameof(CafeQrEscaneo.RepartidorId)]!))
            .Concat(ventasMod.SelectMany(e => new[] {
                (int?)e.OriginalValues[nameof(CafeVenta.EntregadoPorRepartidorId)],
                e.Entity.EntregadoPorRepartidorId }).Where(x => x.HasValue).Select(x => x!.Value))
            .Distinct().ToList();
        var reps = repIds.Count == 0 ? new Dictionary<int, string>()
            : db.CafeRepartidores.AsNoTracking().Where(r => repIds.Contains(r.Id))
                .Select(r => new { r.Id, r.Nombre }).ToDictionary(r => r.Id, r => r.Nombre);
        string Rep(int? id) => id.HasValue && reps.TryGetValue(id.Value, out var n) ? n : "otro repartidor";

        // Lo de este guardado se suma a lo que quedó pendiente de guardados anteriores de la misma
        // transacción: editar productos borra todos los renglones y los vuelve a cargar en DOS guardados;
        // sin juntarlos diría "sacó todo" + "agregó todo" en vez de "Café 1 kg: 2 → 3".
        pend ??= new Pendiente();
        var notas = pend.Notas;
        void Nota(int ventaId, string tipo, string texto)
        {
            if (!notas.TryGetValue((ventaId, tipo), out var l)) notas[(ventaId, tipo)] = l = new List<string>();
            l.Add(texto);
        }

        foreach (var e in ventasMod) CambiosDeVenta(e, Nota, Rep, quien, desde);
        foreach (var g in items.GroupBy(i => i.Entity.VentaId)) AcumularRenglones(pend, g.Key, g.ToList());
        foreach (var g in escaneos.GroupBy(e => e.Entity.VentaId)) CambiosDeReparto(db, g.Key, g.ToList(), Nota, Rep, quien, desde);

        // Dentro de una transacción, si por ahora solo se BORRARON renglones (primer paso de editar
        // productos), esperamos al guardado que los vuelve a cargar. Las filas del historial se guardan
        // en la misma transacción: si se deshace, el historial también.
        if (db.Database.CurrentTransaction is not null && pend.Items.Count > 0
            && pend.Items.Values.All(p => p.despues.Count == 0))
        {
            db.HistorialPendiente = pend;
            return;
        }
        db.HistorialPendiente = null;
        foreach (var (ventaId, (antes, despues)) in pend.Items) DiferenciaRenglones(ventaId, antes, despues, Nota);

        var ahora = DateTime.UtcNow;
        foreach (var ((ventaId, tipo), lineas) in notas)
        {
            var detalle = string.Join("\n", lineas.Distinct());
            if (detalle.Length > 2000) detalle = detalle[..1997] + "...";
            db.CafeVentaHistorial.Add(new CafeVentaHistorial
            {
                VentaId = ventaId, Tipo = tipo, Quien = quien.Length > 100 ? quien[..100] : quien,
                Desde = desde, Detalle = detalle, CreatedAt = ahora
            });
        }
    }

    // ---------- quién y desde dónde ----------

    private static (string quien, string desde) Actor(AppDbContext db, IHttpContextAccessor? http)
    {
        var ctx = http?.HttpContext;
        if (ctx is null) return ("sistema", "sistema");
        var path = ctx.Request.Path.Value ?? "";

        if (ctx.GetRouteValue("tokenRepartidor") is string token && token.Length > 0)
        {
            var nombre = db.CafeRepartidores.AsNoTracking().Where(r => r.PublicToken == token).Select(r => r.Nombre).FirstOrDefault();
            return (nombre ?? "repartidor", "celu");
        }

        var op = ctx.Request.Headers["X-Operator-Name"].ToString().Trim().ToUpperInvariant();
        if (op.Length > 20) op = op[..20];
        if (op.Length == 0) op = ctx.User?.Identity?.Name ?? "";

        if (path.Contains("/repartidor-public", StringComparison.OrdinalIgnoreCase))
            return (op.Length > 0 ? op : "repartidor", "celu");
        var desde = path.Contains("mapeo", StringComparison.OrdinalIgnoreCase) ? "mapeo" : "compu";
        return (op.Length > 0 ? op : "sin nombre", desde);
    }

    private static bool TablaExiste(AppDbContext db)
    {
        if (_tablaOk) return true;
        if (DateTime.UtcNow - _tablaMiradaAt < TimeSpan.FromMinutes(5)) return false;
        _tablaMiradaAt = DateTime.UtcNow;
        _tablaOk = db.Database
            .SqlQueryRaw<int>("SELECT CASE WHEN OBJECT_ID('Cafe_VentaHistorial') IS NULL THEN 0 ELSE 1 END AS Value")
            .AsEnumerable().FirstOrDefault() == 1;
        return _tablaOk;
    }

    // ---------- la venta ----------

    private static readonly (string campo, string nombre, string tipo)[] CamposVenta =
    {
        (nameof(CafeVenta.Fecha), "fecha", "fecha"),
        (nameof(CafeVenta.ClienteNombreSnapshot), "cliente", "texto"),
        (nameof(CafeVenta.ClienteDomicilioEntregaSnapshot), "dirección de entrega", "texto"),
        (nameof(CafeVenta.MapeoLink), "ubicación de Maps", "link"),
        (nameof(CafeVenta.Observaciones), "observaciones", "texto"),
        (nameof(CafeVenta.ComentarioArmado), "comentario para armado", "texto"),
        (nameof(CafeVenta.TipoComprobante), "comprobante", "texto"),
        (nameof(CafeVenta.CondicionIva), "condición de IVA", "texto"),
        (nameof(CafeVenta.CondicionPago), "condición de pago", "texto"),
        (nameof(CafeVenta.Retira), "retira el cliente", "bool"),
        (nameof(CafeVenta.PorTransporte), "va por transporte", "bool"),
        (nameof(CafeVenta.TransporteEmpresa), "transporte", "texto"),
        (nameof(CafeVenta.TransporteDestino), "destino del transporte", "texto"),
        (nameof(CafeVenta.CantidadBultos), "bultos", "texto"),
        (nameof(CafeVenta.EsFragil), "frágil", "bool"),
        (nameof(CafeVenta.SolicitarFirmaEntrega), "pedir firma al entregar", "bool"),
        (nameof(CafeVenta.ComentarioEntrega), "comentario de entrega", "texto"),
        (nameof(CafeVenta.Descuento), "descuento", "plata"),
        (nameof(CafeVenta.Total), "total", "plata"),
    };

    private static void CambiosDeVenta(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<CafeVenta> e,
        Action<int, string, string> nota, Func<int?, string> rep, string quien, string desde)
    {
        var v = e.Entity;
        object? Orig(string p) => e.OriginalValues[p];

        // Anular / recuperar
        var estadoAntes = Orig(nameof(CafeVenta.Estado)) as string;
        if (estadoAntes != v.Estado)
        {
            if (v.Estado == "anulado") nota(v.Id, "ESTADO", "anuló la venta");
            else if (estadoAntes == "anulado") nota(v.Id, "ESTADO", "recuperó la venta (estaba anulada)");
        }

        // Entrega
        var atAntes = (DateTime?)Orig(nameof(CafeVenta.EntregadoAt));
        var repAntes = (int?)Orig(nameof(CafeVenta.EntregadoPorRepartidorId));
        if (atAntes is null && v.EntregadoAt is not null)
        {
            var t = "marcó entregada";
            if (v.EntregadoPorRepartidorId.HasValue && !(desde == "celu" && rep(v.EntregadoPorRepartidorId) == quien))
                t += " por " + rep(v.EntregadoPorRepartidorId);
            else if (!v.EntregadoPorRepartidorId.HasValue && v.Retira) t += " (retiró el cliente)";
            if (Math.Abs((DateTime.UtcNow - v.EntregadoAt.Value).TotalMinutes) > 15)
                t += " · con fecha " + Ar(v.EntregadoAt.Value).ToString("dd/MM/yyyy HH:mm", Inv);
            nota(v.Id, "ENTREGA", t);
        }
        else if (atAntes is not null && v.EntregadoAt is null)
        {
            var t = "desmarcó la entrega (figuraba entregada";
            if (repAntes.HasValue) t += " por " + rep(repAntes);
            t += " el " + Ar(atAntes.Value).ToString("dd/MM/yyyy", Inv) + ")";
            nota(v.Id, "ENTREGA", t);
        }
        else if (atAntes is not null && v.EntregadoAt is not null)
        {
            if (repAntes != v.EntregadoPorRepartidorId)
                nota(v.Id, "ENTREGA", $"cambió quién la entregó: {(repAntes.HasValue ? rep(repAntes) : "nadie")} → {(v.EntregadoPorRepartidorId.HasValue ? rep(v.EntregadoPorRepartidorId) : "nadie")}");
            if (Math.Abs((atAntes.Value - v.EntregadoAt.Value).TotalMinutes) >= 1)
                nota(v.Id, "ENTREGA", $"cambió la fecha de entrega: {Ar(atAntes.Value).ToString("dd/MM HH:mm", Inv)} → {Ar(v.EntregadoAt.Value).ToString("dd/MM HH:mm", Inv)}");
        }

        // Campos comunes
        foreach (var (campo, nombre, tipo) in CamposVenta)
        {
            var a = Orig(campo);
            var b = e.CurrentValues[campo];
            if (Igual(a, b, tipo)) continue;
            if (campo == nameof(CafeVenta.ComentarioEntrega) && atAntes is null && v.EntregadoAt is not null)
            {
                if (b is string s && s.Trim().Length > 0) nota(v.Id, "ENTREGA", $"comentario: \"{Corto(s)}\"");
                continue;
            }
            var texto = tipo switch
            {
                "link" => string.IsNullOrWhiteSpace(b as string) ? "sacó la ubicación de Maps propia de la venta" : "cambió la ubicación de Maps",
                _ => $"{nombre}: {Mostrar(a, tipo)} → {Mostrar(b, tipo)}",
            };
            nota(v.Id, "EDICION", texto);
        }
    }

    private static bool Igual(object? a, object? b, string tipo)
    {
        if (tipo is "texto" or "link")
            return string.Equals((a?.ToString() ?? "").Trim(), (b?.ToString() ?? "").Trim(), StringComparison.Ordinal);
        if (tipo == "fecha" && a is DateTime da && b is DateTime db) return da.Date == db.Date;
        return Equals(a, b);
    }

    private static string Mostrar(object? x, string tipo) => tipo switch
    {
        "bool" => x is true ? "sí" : "no",
        "plata" => x is decimal d ? Plata(d) : "—",
        "fecha" => x is DateTime f ? f.ToString("dd/MM/yyyy", Inv) : "—",
        _ => string.IsNullOrWhiteSpace(x?.ToString()) ? "(vacío)" : $"\"{Corto(x!.ToString()!)}\"",
    };

    // ---------- los renglones ----------

    internal sealed class Pendiente
    {
        public readonly Dictionary<(int, string), List<string>> Notas = new();
        public readonly Dictionary<int, (Dictionary<string, (int cant, decimal precio, decimal dto)> antes,
                                          Dictionary<string, (int cant, decimal precio, decimal dto)> despues)> Items = new();
    }

    private static void AcumularRenglones(Pendiente pend, int ventaId,
        List<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<CafeVentaItem>> entries)
    {
        // Antes = borrados + modificados (como estaban). Después = agregados + modificados (como quedan).
        // Así "borrar todo y volver a cargar lo mismo" no anota nada.
        if (!pend.Items.TryGetValue(ventaId, out var par))
            pend.Items[ventaId] = par = (new(), new());
        var (antes, despues) = par;
        void Sumar(Dictionary<string, (int, decimal, decimal)> d, Func<string, object?> val)
        {
            var k = NombreRenglon((string?)val(nameof(CafeVentaItem.ProductoNombreSnapshot)), (string?)val(nameof(CafeVentaItem.Formato)),
                (string?)val(nameof(CafeVentaItem.Categoria)), (string?)val(nameof(CafeVentaItem.Molienda)));
            var c = (int)(val(nameof(CafeVentaItem.Cantidad)) ?? 0);
            var p = (decimal)(val(nameof(CafeVentaItem.PrecioUnitario)) ?? 0m);
            var dto = (decimal)(val(nameof(CafeVentaItem.DescuentoPct)) ?? 0m);
            d[k] = d.TryGetValue(k, out var x) ? (x.Item1 + c, p, dto) : (c, p, dto);
        }
        foreach (var e in entries)
        {
            if (e.State is EntityState.Deleted or EntityState.Modified) Sumar(antes, p => e.OriginalValues[p]);
            if (e.State is EntityState.Added or EntityState.Modified) Sumar(despues, p => e.CurrentValues[p]);
        }
    }

    private static void DiferenciaRenglones(int ventaId, Dictionary<string, (int cant, decimal precio, decimal dto)> antes,
        Dictionary<string, (int cant, decimal precio, decimal dto)> despues, Action<int, string, string> nota)
    {
        foreach (var (k, a) in antes)
        {
            if (!despues.TryGetValue(k, out var b)) { nota(ventaId, "EDICION", $"sacó {k} (eran {a.cant})"); continue; }
            if (a.cant != b.cant) nota(ventaId, "EDICION", $"{k}: {a.cant} → {b.cant}");
            if (a.precio != b.precio) nota(ventaId, "EDICION", $"{k}: precio {Plata(a.precio)} → {Plata(b.precio)}");
            if (a.dto != b.dto) nota(ventaId, "EDICION", $"{k}: descuento {a.dto.ToString("0.##", Inv)}% → {b.dto.ToString("0.##", Inv)}%");
        }
        foreach (var (k, b) in despues)
            if (!antes.ContainsKey(k)) nota(ventaId, "EDICION", $"agregó {b.cant} × {k}");
    }

    private static string NombreRenglon(string? nombre, string? formato, string? categoria, string? molienda)
    {
        var n = (nombre ?? "").Trim();
        if (categoria == "CAFE")
        {
            var f = formato switch { "1KG" => "1 kg", "MEDIO" => "½ kg", "CUARTO" => "¼ kg", _ => "" };
            if (f.Length > 0 && !n.Contains(f, StringComparison.OrdinalIgnoreCase)) n += " " + f;
            if (!string.IsNullOrWhiteSpace(molienda)) n += " " + molienda.Trim().ToLowerInvariant();
        }
        return n;
    }

    // ---------- reparto ----------

    private static void CambiosDeReparto(AppDbContext db, int ventaId,
        List<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<CafeQrEscaneo>> entries,
        Action<int, string, string> nota, Func<int?, string> rep, string quien, string desde)
    {
        var sacados = entries.Where(e => e.State == EntityState.Deleted)
            .Select(e => (int)e.OriginalValues[nameof(CafeQrEscaneo.RepartidorId)]!).Distinct().ToList();
        var puestos = entries.Where(e => e.State == EntityState.Added).Select(e => e.Entity.RepartidorId).Distinct().ToList();
        var nuevos = puestos.Except(sacados).ToList();
        var viejos = sacados.Except(puestos).ToList();
        if (nuevos.Count == 0 && viejos.Count == 0) return;

        // Contexto para que se note un caso como el de la 2110 (venta vieja o ya entregada).
        var aviso = "";
        if (nuevos.Count > 0)
        {
            var v = db.CafeVentas.Local.FirstOrDefault(x => x.Id == ventaId)
                    ?? db.CafeVentas.AsNoTracking().FirstOrDefault(x => x.Id == ventaId);
            if (v is not null)
            {
                var dias = (Ar(DateTime.UtcNow).Date - v.Fecha.Date).Days;
                if (dias >= 3) aviso += $" · la venta es del {v.Fecha:dd/MM} (hace {dias} días)";
                var entregaAntes = db.Entry(v).State == EntityState.Detached ? v.EntregadoAt
                    : (DateTime?)db.Entry(v).OriginalValues[nameof(CafeVenta.EntregadoAt)];
                if (entregaAntes.HasValue) aviso += $" · ya figuraba entregada el {Ar(entregaAntes.Value):dd/MM}";
            }
        }

        var esCelu = desde == "celu";
        string texto;
        if (nuevos.Count > 0 && viejos.Count > 0)
            texto = $"pasó la venta de {string.Join(", ", viejos.Select(i => rep(i)))} a {string.Join(", ", nuevos.Select(i => rep(i)))}";
        else if (nuevos.Count > 0)
            texto = esCelu && nuevos.Count == 1 && rep(nuevos[0]) == quien ? "se la cargó" : $"asignó la venta a {string.Join(", ", nuevos.Select(i => rep(i)))}";
        else
            texto = esCelu && viejos.Count == 1 && rep(viejos[0]) == quien ? "la sacó de su lista"
                : $"le sacó la venta a {string.Join(", ", viejos.Select(i => rep(i)))}";
        nota(ventaId, "REPARTO", texto + aviso);
    }

    // ---------- formato ----------

    internal static DateTime Ar(DateTime utc) => utc.AddHours(-3);

    internal static string Plata(decimal d)
    {
        var s = Math.Round(d, 2).ToString("#,0.##", Inv); // 1,234.5
        return "$" + s.Replace(",", "#").Replace(".", ",").Replace("#", ".");
    }

    private static string Corto(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 120 ? s[..117] + "..." : s;
    }
}
