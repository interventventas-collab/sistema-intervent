using System.Globalization;
using System.Text;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-08-03 (pedido Gabriel/Osmar/Germán): BOT INTERNO para empleados por WhatsApp.
///
/// Un empleado le escribe SU palabra clave (ej "1983") al WhatsApp de la empresa. La palabra
/// hace de usuario+clave: el bot sabe quién es y le manda un menú con las opciones que tiene
/// habilitadas (Stock, Precios, Pedidos del día, Saldo de cliente, Facturas de cliente). Al
/// tocar una opción, si hace falta un dato (ej "¿qué producto?") el bot lo pregunta y con el
/// próximo mensaje contesta la consulta.
///
/// Todo se configura desde "🤖 Automatizaciones y Alertas" (tabla Auto_MenuEmpleado). El bot
/// responde SIEMPRE por la misma línea por la que el empleado escribió (lineaId del webhook).
/// </summary>
public class WhatsAppEmpleadoBotService
{
    private readonly AppDbContext _db;
    private readonly MetaWhatsAppService _meta;
    private readonly CafeSaldosService _saldos;
    private readonly ILogger<WhatsAppEmpleadoBotService> _log;

    public WhatsAppEmpleadoBotService(AppDbContext db, MetaWhatsAppService meta,
        CafeSaldosService saldos, ILogger<WhatsAppEmpleadoBotService> log)
    {
        _db = db; _meta = meta; _saldos = saldos; _log = log;
    }

    /// <summary>Intenta atender el mensaje como parte del bot de empleados. Devuelve true si lo
    /// manejó (el webhook NO debe seguir con pedido/bienvenida). false = no era para este bot.</summary>
    public async Task<bool> TryHandleAsync(string fromWaId, string numero, string? tipo,
        string? idTocado, string? cuerpo, string? lineaId)
    {
        try
        {
            // 1) ¿Tocó una opción del menú del bot? (id "emp:accion:codigo")
            if (!string.IsNullOrEmpty(idTocado) && idTocado.StartsWith("emp:", StringComparison.Ordinal))
                return await ManejarOpcionAsync(fromWaId, numero, idTocado, lineaId);

            if (tipo != "text" || string.IsNullOrWhiteSpace(cuerpo)) return false;
            var texto = cuerpo.Trim();

            // 2) ¿Es una palabra clave de empleado? → abrir el menú
            var emp = await _db.AutoMenuEmpleados.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Activo && e.Codigo == texto);
            if (emp is not null)
            {
                // Seguridad opcional: la clave puede estar atada a un número puntual.
                if (!string.IsNullOrWhiteSpace(emp.SoloDesdeNumero) &&
                    !string.Equals(emp.SoloDesdeNumero, numero, StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogWarning("[BotEmpleado] código {Cod} usado desde {Num} pero está atado a otro número", emp.Codigo, numero);
                    return false; // que caiga al bot normal como si fuera un desconocido
                }
                await LimpiarEstadoAsync(numero);
                await EnviarMenuAsync(fromWaId, numero, emp, lineaId);
                return true;
            }

            // 3) ¿Hay una consulta pendiente para este número? → interpretar el texto como el dato.
            //    Mantenemos el "modo" activo (refrescando el vencimiento) así el empleado puede
            //    encadenar consultas del mismo tipo sin volver a tocar el menú. Para volver al menú,
            //    escribe de nuevo su palabra clave (se detecta arriba, en el paso 2).
            var estado = await _db.AutoMenuEstados.FirstOrDefaultAsync(x => x.Numero == numero);
            if (estado is not null && !string.IsNullOrEmpty(estado.Esperando))
            {
                if (estado.ExpiraAt < DateTime.UtcNow) { await LimpiarEstadoAsync(numero); return false; }
                var empEstado = await _db.AutoMenuEmpleados.AsNoTracking()
                    .FirstOrDefaultAsync(e => e.Activo && e.Codigo == estado.Codigo);
                if (empEstado is null) { await LimpiarEstadoAsync(numero); return false; }

                var resp = await ResolverConsultaAsync(estado.Esperando, texto, empEstado);
                await RefrescarEstadoAsync(numero); // sigue esperando el mismo tipo de dato
                await ResponderAsync(fromWaId, numero, resp, lineaId);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[BotEmpleado] error atendiendo a {Num}", numero);
            return false;
        }
    }

    // ─────────────── Menú ───────────────

    private async Task EnviarMenuAsync(string fromWaId, string numero, AutoMenuEmpleado emp, string? lineaId)
    {
        var filas = new List<(string Id, string Title, string? Desc)>();
        if (emp.OpStock)    filas.Add(($"emp:stock:{emp.Codigo}",    "📦 Stock",              "Cuánto hay de un producto"));
        if (emp.OpPrecios)  filas.Add(($"emp:precios:{emp.Codigo}",  "💲 Precios",            "Precio de un producto"));
        if (emp.OpPedidos)  filas.Add(($"emp:pedidos:{emp.Codigo}",  "🧾 Pedidos del día",    "Los pedidos que entraron hoy"));
        if (emp.OpSaldos)   filas.Add(($"emp:saldos:{emp.Codigo}",   "💰 Saldo de cliente",   "Cuánto debe un cliente"));
        if (emp.OpFacturas) filas.Add(($"emp:facturas:{emp.Codigo}", "📄 Facturas de cliente","Últimas facturas de un cliente"));

        if (filas.Count == 0)
        {
            await ResponderAsync(fromWaId, numero, $"Hola {emp.Nombre} 👋 Todavía no tenés opciones habilitadas. Pedile al administrador que te active alguna.", lineaId);
            return;
        }

        var cuerpo = $"👷 Hola {emp.Nombre} 👋\n¿Qué querés consultar?";
        var sid = await _meta.SendListAsync(fromWaId, cuerpo, "Ver opciones", filas, lineaPhoneId: lineaId);
        await RegistrarSalienteAsync(numero, cuerpo + " [menú empleado]", sid, lineaId);
    }

    private async Task<bool> ManejarOpcionAsync(string fromWaId, string numero, string idTocado, string? lineaId)
    {
        // id = "emp:accion:codigo"
        var partes = idTocado.Split(':');
        if (partes.Length < 3) return false;
        var accion = partes[1];
        var codigo = partes[2];

        var emp = await _db.AutoMenuEmpleados.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Activo && e.Codigo == codigo);
        if (emp is null) return false;

        if (!OpcionHabilitada(emp, accion))
        {
            await ResponderAsync(fromWaId, numero, "Esa opción no está habilitada para vos.", lineaId);
            return true;
        }

        // "pedidos" no necesita que escriba nada → contestamos al toque.
        if (accion == "pedidos")
        {
            var resp = await ResolverConsultaAsync("pedidos", "", emp);
            await ResponderAsync(fromWaId, numero, resp, lineaId);
            return true;
        }

        // El resto pide un dato → dejamos el bot "esperando".
        await GuardarEstadoAsync(numero, codigo, accion);

        // Saldos y Facturas: le mandamos el LISTADO de clientes que deben (con su número
        // abreviado #código), así responde con el número y no tiene que escribir el nombre.
        if (accion == "saldos" || accion == "facturas")
        {
            var listado = await ConstruirListadoDeudoresAsync(accion);
            await ResponderLargoAsync(fromWaId, numero, listado, lineaId);
            return true;
        }

        var pregunta = accion switch
        {
            "stock"    => "📦 Escribí el nombre o código del producto y te digo el stock.",
            "precios"  => "💲 Escribí el nombre o código del producto y te digo el precio.",
            _ => "Escribí el dato que querés consultar."
        };
        await ResponderAsync(fromWaId, numero, pregunta, lineaId);
        return true;
    }

    private static bool OpcionHabilitada(AutoMenuEmpleado e, string accion) => accion switch
    {
        "stock" => e.OpStock,
        "precios" => e.OpPrecios,
        "pedidos" => e.OpPedidos,
        "saldos" => e.OpSaldos,
        "facturas" => e.OpFacturas,
        _ => false
    };

    // ─────────────── Consultas ───────────────

    private async Task<string> ResolverConsultaAsync(string accion, string dato, AutoMenuEmpleado emp) => accion switch
    {
        "stock"    => await ConsultarProductoAsync(dato, precios: false),
        "precios"  => await ConsultarProductoAsync(dato, precios: true),
        "pedidos"  => await ConsultarPedidosDelDiaAsync(),
        "saldos"   => await ConsultarSaldoAsync(dato),
        "facturas" => await ConsultarFacturasAsync(dato),
        _ => "No entendí la consulta. Escribí tu palabra clave para volver a ver el menú."
    };

    private async Task<string> ConsultarProductoAsync(string q, bool precios)
    {
        q = q.Trim();
        if (q.Length < 2) return "Escribí al menos 2 letras del producto.";
        var prods = await _db.Products.AsNoTracking()
            .Where(p => p.IsActive && (
                p.Sku == q ||
                (p.Sku != null && p.Sku.Contains(q)) ||
                p.Title.Contains(q) ||
                (p.DisplayName != null && p.DisplayName.Contains(q))))
            .OrderBy(p => p.Title)
            .Take(8)
            .ToListAsync();

        if (prods.Count == 0)
            return $"No encontré ningún producto con «{q}». Probá con otra palabra o el código.";

        var titulo = precios ? "💲 Precios" : "📦 Stock";
        var lineas = prods.Select(p =>
        {
            var nombre = string.IsNullOrWhiteSpace(p.DisplayName) ? p.Title : p.DisplayName!;
            var sku = string.IsNullOrWhiteSpace(p.Sku) ? "" : $" [{p.Sku}]";
            if (precios)
            {
                var pr = Money(p.RetailPrice);
                if (p.RetailPrice2 is > 0) pr += " / " + Money(p.RetailPrice2.Value);
                return $"• {nombre}{sku}: {pr}";
            }
            return $"• {nombre}{sku}: {p.Stock.ToString("0.##", CultureInfo.InvariantCulture)} {p.StockUnit}";
        });
        return $"{titulo} (coincidencias con «{q}»):\n" + string.Join("\n", lineas)
             + "\n\nEscribí otro producto, o tu palabra clave para el menú.";
    }

    private async Task<string> ConsultarPedidosDelDiaAsync()
    {
        var inicioDiaUtc = DateTime.UtcNow.AddHours(-3).Date.AddHours(3); // medianoche AR → UTC
        var pedidos = await _db.WhatsAppPedidosRecibidos.AsNoTracking()
            .Where(p => p.RecibidoAt >= inicioDiaUtc)
            .OrderByDescending(p => p.RecibidoAt)
            .Take(15)
            .ToListAsync();

        if (pedidos.Count == 0) return "🧾 Hoy todavía no entró ningún pedido.";

        var lineas = pedidos.Select(p =>
        {
            var quien = !string.IsNullOrWhiteSpace(p.ClienteNombre) ? p.ClienteNombre! : p.Telefono;
            var hora = p.RecibidoAt.AddHours(-3).ToString("HH:mm");
            return $"• {hora} — {quien} ({p.Estado})";
        });
        return $"🧾 Pedidos de hoy: {pedidos.Count}\n" + string.Join("\n", lineas)
             + "\n\nEscribí tu palabra clave para volver al menú.";
    }

    /// <summary>Listado de clientes que deben, ordenado de mayor a menor, con su número abreviado
    /// (#CódigoInterno). El empleado responde con el número para ver el detalle.</summary>
    private async Task<string> ConstruirListadoDeudoresAsync(string accion)
    {
        var saldos = (await _saldos.GetSaldosPendientesAsync())
            .Where(s => s.SaldoPendiente > 0)
            .OrderByDescending(s => s.SaldoPendiente)
            .ToList();

        var titulo = accion == "facturas"
            ? $"📄 FACTURAS POR CLIENTE — clientes con saldo ({saldos.Count}), de mayor a menor:"
            : $"💰 CLIENTES QUE DEBEN ({saldos.Count}), de mayor a menor:";

        if (saldos.Count == 0)
            return "No hay clientes con saldo pendiente ahora. Igual podés escribir el nombre de un cliente para consultarlo.";

        var lineas = saldos.Select(s =>
        {
            var cod = s.CodigoInterno.HasValue ? $"#{s.CodigoInterno}" : "#—";
            return $"{cod}  {s.Nombre} — {Money(s.SaldoPendiente)}";
        });
        var pie = accion == "facturas"
            ? "\n\n👉 Respondé con el número (ej: 134) para ver sus facturas, o escribí el nombre."
            : "\n\n👉 Respondé con el número (ej: 134) para ver el detalle, o escribí el nombre.";
        return titulo + "\n" + string.Join("\n", lineas) + pie;
    }

    private async Task<string> ConsultarSaldoAsync(string q)
    {
        q = q.Trim();
        // ¿Respondió con un NÚMERO de cliente? → detalle completo de ese cliente.
        if (EsNumeroCliente(q, out var codigo))
        {
            var cli = await _db.CafeClientes.AsNoTracking().FirstOrDefaultAsync(c => c.CodigoInterno == codigo);
            if (cli is null) return $"No encontré ningún cliente con el número #{q}. Probá con otro número o escribí el nombre.";
            return await DetalleClienteAsync(cli);
        }

        if (q.Length < 2) return "Escribí al menos 2 letras del nombre del cliente (o el número de cliente).";
        var saldos = await _saldos.GetSaldosPendientesAsync();
        var match = saldos
            .Where(s => (s.Nombre ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderByDescending(s => Math.Abs(s.SaldoPendiente))
            .Take(10)
            .ToList();

        if (match.Count == 0)
            return $"No encontré ningún cliente con deuda que coincida con «{q}». (Si no debe nada, no aparece en la lista.)";

        var lineas = match.Select(s =>
        {
            var cod = s.CodigoInterno.HasValue ? $"#{s.CodigoInterno} " : "";
            return $"• {cod}{s.Nombre}: {Money(s.SaldoPendiente)}";
        });
        return $"💰 Coincidencias con «{q}»:\n" + string.Join("\n", lineas)
             + "\n\n👉 Respondé con el número para el detalle, o escribí otro nombre.";
    }

    private async Task<string> ConsultarFacturasAsync(string q)
    {
        q = q.Trim();
        // ¿Respondió con un NÚMERO de cliente? → sus facturas.
        if (EsNumeroCliente(q, out var codigo))
        {
            var cli = await _db.CafeClientes.AsNoTracking().FirstOrDefaultAsync(c => c.CodigoInterno == codigo);
            if (cli is null) return $"No encontré ningún cliente con el número #{q}. Probá con otro número o escribí el nombre.";
            return await FacturasDeClienteAsync(cli);
        }

        if (q.Length < 2) return "Escribí al menos 2 letras del nombre del cliente (o el número de cliente).";
        var clientes = await _db.CafeClientes.AsNoTracking()
            .Where(c => c.Nombre.Contains(q) || (c.RazonSocial != null && c.RazonSocial.Contains(q)))
            .OrderBy(c => c.Nombre)
            .Take(8)
            .ToListAsync();

        if (clientes.Count == 0)
            return $"No encontré ningún cliente con «{q}».";

        if (clientes.Count > 1)
        {
            var nombres = clientes.Select(c => $"• {(c.CodigoInterno.HasValue ? $"#{c.CodigoInterno} " : "")}{c.Nombre}");
            return $"Encontré varios clientes con «{q}». Respondé con el número o afiná el nombre:\n" + string.Join("\n", nombres)
                 + "\n\n👉 Respondé con el número, o escribí otro nombre.";
        }

        return await FacturasDeClienteAsync(clientes[0]);
    }

    /// <summary>Detalle de cuenta de un cliente: saldo (con desglose cotización/factura) + últimas facturas.</summary>
    private async Task<string> DetalleClienteAsync(CafeCliente cli)
    {
        var saldos = await _saldos.GetSaldosPendientesAsync();
        var s = saldos.FirstOrDefault(x => x.ClienteId == cli.Id);
        var cod = cli.CodigoInterno.HasValue ? $"(#{cli.CodigoInterno})" : "";

        var sb = new StringBuilder();
        sb.Append($"📋 {cli.Nombre} {cod}".TrimEnd()).Append('\n');
        if (s is not null && s.SaldoPendiente != 0)
        {
            sb.Append($"💰 Debe: {Money(s.SaldoPendiente)} — hace {s.DiasMasAntigua} días\n");
            if (s.SaldoCotizacion != 0) sb.Append($"   • Cotización (X): {Money(s.SaldoCotizacion)}\n");
            if (s.SaldoFactura != 0) sb.Append($"   • Factura (A/B/C): {Money(s.SaldoFactura)}\n");
            sb.Append($"   • {s.CantidadVentasPendientes} comprobantes pendientes\n");
        }
        else sb.Append("💰 No tiene saldo pendiente.\n");

        var ventas = await _db.CafeVentas.AsNoTracking()
            .Where(v => v.ClienteId == cli.Id && v.Estado != "anulado")
            .OrderByDescending(v => v.Fecha).Take(6).ToListAsync();
        if (ventas.Count > 0)
        {
            sb.Append("📄 Últimas facturas:\n");
            foreach (var v in ventas)
                sb.Append($" • {v.Numero} — {v.Fecha:dd/MM/yy} — {Money(v.Total)} {(v.IsPaid ? "✅ pagada" : "⏳ impaga")}\n");
        }
        sb.Append("\n👉 Respondé otro número/nombre, o tu palabra clave para el menú.");
        return sb.ToString();
    }

    /// <summary>Solo las últimas facturas de un cliente (para la opción 📄 Facturas).</summary>
    private async Task<string> FacturasDeClienteAsync(CafeCliente cli)
    {
        var ventas = await _db.CafeVentas.AsNoTracking()
            .Where(v => v.ClienteId == cli.Id && v.Estado != "anulado")
            .OrderByDescending(v => v.Fecha).Take(8).ToListAsync();

        var cod = cli.CodigoInterno.HasValue ? $"(#{cli.CodigoInterno})" : "";
        if (ventas.Count == 0)
            return $"📄 {cli.Nombre} {cod}".TrimEnd() + " no tiene facturas cargadas."
                 + "\n\n👉 Respondé otro número/nombre, o tu palabra clave para el menú.";

        var lineas = ventas.Select(v => $" • {v.Numero} — {v.Fecha:dd/MM/yy} — {Money(v.Total)} {(v.IsPaid ? "✅ pagada" : "⏳ impaga")}");
        return $"📄 Últimas facturas de {cli.Nombre} {cod}".TrimEnd() + ":\n" + string.Join("\n", lineas)
             + "\n\n👉 Respondé otro número/nombre, o tu palabra clave para el menú.";
    }

    /// <summary>True si el texto es un número de cliente (solo dígitos, 1..7 cifras).</summary>
    private static bool EsNumeroCliente(string q, out int codigo)
    {
        codigo = 0;
        var t = q.TrimStart('#').Trim();
        return t.Length is >= 1 and <= 7 && t.All(char.IsDigit) && int.TryParse(t, out codigo);
    }

    // ─────────────── Estado (memoria corta) ───────────────

    private async Task GuardarEstadoAsync(string numero, string codigo, string esperando)
    {
        var e = await _db.AutoMenuEstados.FirstOrDefaultAsync(x => x.Numero == numero);
        if (e is null) { e = new AutoMenuEstado { Numero = numero }; _db.AutoMenuEstados.Add(e); }
        e.Codigo = codigo;
        e.Esperando = esperando;
        e.ExpiraAt = DateTime.UtcNow.AddMinutes(15);
        e.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    private async Task RefrescarEstadoAsync(string numero)
    {
        var e = await _db.AutoMenuEstados.FirstOrDefaultAsync(x => x.Numero == numero);
        if (e is null) return;
        e.ExpiraAt = DateTime.UtcNow.AddMinutes(15);
        e.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    private async Task LimpiarEstadoAsync(string numero)
    {
        var e = await _db.AutoMenuEstados.FirstOrDefaultAsync(x => x.Numero == numero);
        if (e is not null) { _db.AutoMenuEstados.Remove(e); await _db.SaveChangesAsync(); }
    }

    // ─────────────── Envío ───────────────

    private async Task ResponderAsync(string fromWaId, string numero, string texto, string? lineaId)
    {
        var sid = await _meta.SendTextAsync(fromWaId, texto, lineaPhoneId: lineaId);
        await RegistrarSalienteAsync(numero, texto, sid, lineaId);
    }

    /// <summary>Manda un texto largo partiéndolo en varios mensajes (WhatsApp corta ~4096 chars).
    /// Parte por renglones para no cortar una línea al medio.</summary>
    private async Task ResponderLargoAsync(string fromWaId, string numero, string texto, string? lineaId)
    {
        const int MAX = 3500;
        if (texto.Length <= MAX) { await ResponderAsync(fromWaId, numero, texto, lineaId); return; }

        var sb = new StringBuilder();
        foreach (var linea in texto.Split('\n'))
        {
            if (sb.Length + linea.Length + 1 > MAX && sb.Length > 0)
            {
                await ResponderAsync(fromWaId, numero, sb.ToString().TrimEnd(), lineaId);
                sb.Clear();
            }
            sb.Append(linea).Append('\n');
        }
        if (sb.Length > 0) await ResponderAsync(fromWaId, numero, sb.ToString().TrimEnd(), lineaId);
    }

    private async Task RegistrarSalienteAsync(string numero, string cuerpo, string? sid, string? lineaId)
    {
        _db.WhatsAppTwilioMensajes.Add(new WhatsAppTwilioMensaje
        {
            Direccion = "OUTGOING",
            Numero = numero,
            Cuerpo = cuerpo,
            LineaPhoneId = lineaId,
            TwilioMessageSid = sid,
            Canal = "CLOUD",
            Procesado = true,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }

    /// <summary>Formatea plata al estilo argentino ($1.234.567) sin depender de la cultura del server.</summary>
    private static string Money(decimal v)
        => "$" + v.ToString("#,##0", CultureInfo.InvariantCulture).Replace(",", ".");
}
