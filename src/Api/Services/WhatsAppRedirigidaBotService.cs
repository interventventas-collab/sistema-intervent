using System.Globalization;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-09-26 (pedido del dueño): CARGAR UNA REDIRIGIDA ESCRIBIENDO "redi" AL WHATSAPP FRIKAF.
///
/// Un número autorizado (la misma lista del "PAGO", más los habilitados solo para esto) escribe
/// "redi" a la línea FRIKAF (11 2252-5458). El bot pregunta, en este orden:
///   1) ¿Quién la envía?  → escribe parte del nombre del cliente y elige, o lo deja con sus palabras
///   2) ¿Quién la recibe? → empleado o proveedor de la lista, "queda en la privada", o con sus palabras
///      (si es un empleado que también cobra viajes: ¿viajes o sueldo?)
///   3) ¿Cuánto?
///   4) ¿Foto o comprobante? → los que mande (foto, PDF, lo que sea), o "Listo"
///   5) Resumen → Confirmar
/// Queda PENDIENTE en Cafe_RedirigidasPendientes y aparece en la bolsita 💰 (Cobranzas a aprobar).
/// NO toca plata: la cobranza de verdad la hace la oficina al volcar, y ahí se copian los adjuntos.
/// Sin IA: son preguntas con listas y botones, como el PAGO.
/// </summary>
public class WhatsAppRedirigidaBotService
{
    /// <summary>La línea FRIKAF (11 2252-5458). Sin línea (null) = la default, que también es FRIKAF.</summary>
    public const string LineaFrikaf = "1242473442281483";
    private const int MinutosParaContestar = 30;

    private readonly AppDbContext _db;
    private readonly MetaWhatsAppService _meta;
    private readonly ILogger<WhatsAppRedirigidaBotService> _log;

    public WhatsAppRedirigidaBotService(AppDbContext db, MetaWhatsAppService meta, ILogger<WhatsAppRedirigidaBotService> log)
    {
        _db = db; _meta = meta; _log = log;
    }

    private static readonly HashSet<string> Arranque = new(StringComparer.OrdinalIgnoreCase)
    { "redi", "redirigida", "redirigido", "redirigidas" };

    /// <summary>true = lo atendió este asistente (el webhook no sigue). false = no era para acá.</summary>
    public async Task<bool> TryHandleAsync(string fromWaId, string numero, string? tipo,
        string? idInteractivo, string? cuerpo, string? lineaId, string? mediaUrl, string? mediaNombre)
    {
        if (lineaId is not null && lineaId != LineaFrikaf) return false;
        try
        {
            if (!string.IsNullOrEmpty(idInteractivo) && idInteractivo.StartsWith("redi:", StringComparison.Ordinal))
                return await ManejarBotonAsync(fromWaId, numero, idInteractivo, lineaId);

            var texto = (cuerpo ?? "").Trim();
            if (tipo == "text" && Arranque.Contains(texto))
            {
                var aut = await BuscarAutorizadoAsync(numero);
                if (aut is null)
                {
                    _log.LogInformation("[BotRedi] '{Num}' escribió redi pero no está autorizado", numero);
                    return false;
                }
                await IniciarAsync(fromWaId, numero, aut.Nombre, lineaId);
                return true;
            }

            var p = await BorradorAsync(numero);
            if (p is null) return false;

            if (tipo == "text" && EsCancelar(texto))
            {
                await DescartarAsync(p);
                await ResponderAsync(fromWaId, numero, "👍 Listo, cancelé la redirigida. Cuando quieras, escribí *redi* para empezar de nuevo.", lineaId);
                return true;
            }

            // Foto / PDF / archivo mientras pide comprobantes
            if (tipo is "image" or "document" or "video" or "audio" or "sticker")
            {
                if (p.Paso != "adjunto")
                {
                    await ResponderAsync(fromWaId, numero, "📎 El comprobante te lo pido al final. Contestá primero la pregunta de arriba.", lineaId);
                    return true;
                }
                return await AgregarAdjuntoAsync(fromWaId, numero, p, mediaUrl, mediaNombre, lineaId);
            }
            if (tipo != "text" || texto.Length == 0) return true;

            return await ManejarTextoAsync(fromWaId, numero, p, texto, lineaId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[BotRedi] error atendiendo a {Num}", numero);
            try { await ResponderAsync(fromWaId, numero, "Uf, algo falló cargando la redirigida. Escribí *redi* para empezar de nuevo.", lineaId); } catch { }
            return true;
        }
    }

    // ─────────────── Arranque ───────────────

    private async Task IniciarAsync(string fromWaId, string numero, string nombre, string? lineaId)
    {
        // Una sola carga por número: si había una a medias, se descarta.
        var viejos = await _db.CafeRedirigidasPendientes.Include(x => x.Adjuntos)
            .Where(x => x.EnviadoNumero == numero && x.Estado == "BORRADOR").ToListAsync();
        foreach (var v in viejos) { _db.CafeRedirigidasPendientesAdjuntos.RemoveRange(v.Adjuntos); _db.CafeRedirigidasPendientes.Remove(v); }

        _db.CafeRedirigidasPendientes.Add(new CafeRedirigidaPendiente
        {
            Estado = "BORRADOR", Paso = "cliente", ExpiraAt = DateTime.UtcNow.AddMinutes(MinutosParaContestar),
            EnviadoPor = nombre, EnviadoNumero = numero
        });
        await _db.SaveChangesAsync();
        await ResponderAsync(fromWaId, numero,
            $"🔁 *Cargar una REDIRIGIDA*\nHola {nombre} 👋\n\n*¿Quién la envía?* Escribí el nombre del cliente (o una parte).\n_(escribí *salir* para cancelar)_", lineaId);
    }

    // ─────────────── Texto según la pregunta ───────────────

    private async Task<bool> ManejarTextoAsync(string fromWaId, string numero, CafeRedirigidaPendiente p, string texto, string? lineaId)
    {
        switch (p.Paso)
        {
            case "cliente": return await BuscarClienteAsync(fromWaId, numero, p, texto, lineaId);
            case "recibe": return await BuscarRecibeAsync(fromWaId, numero, p, texto, lineaId);
            case "importe":
                var monto = MontoParser.Parse(texto);
                if (monto is null || monto <= 0)
                {
                    await ResponderAsync(fromWaId, numero, "No entendí el importe. Escribí solo el número, por ejemplo *45000*.", lineaId);
                    return true;
                }
                p.Importe = monto.Value;
                await Paso(p, "adjunto");
                await PedirAdjuntoAsync(fromWaId, numero, p, lineaId);
                return true;
            case "adjunto":
                await PedirAdjuntoAsync(fromWaId, numero, p, lineaId, "Mandá la foto o el archivo, o tocá *Listo*.");
                return true;
        }
        await ResponderAsync(fromWaId, numero, "👆 Tocá una de las opciones de arriba, o escribí *salir* para cancelar.", lineaId);
        return true;
    }

    // 1) Cliente ─────────────────────────────────────────

    private async Task<bool> BuscarClienteAsync(string fromWaId, string numero, CafeRedirigidaPendiente p, string q, string? lineaId)
    {
        if (q.Length < 2) { await ResponderAsync(fromWaId, numero, "Escribí al menos 2 letras del nombre del cliente.", lineaId); return true; }
        p.ClienteTexto = Recortar(q, 200);
        await Paso(p, "cliente");
        var qn = q.ToLower();
        var cands = await _db.CafeClientes.AsNoTracking()
            .Where(c => c.IsActive && (c.Nombre.Contains(q) || (c.RazonSocial != null && c.RazonSocial.Contains(q))))
            .Select(c => new { c.Id, c.Nombre, c.RazonSocial, c.Localidad })
            .Take(60).ToListAsync();
        var top = cands.OrderBy(c => c.Nombre.ToLower().StartsWith(qn) ? 0 : 1).ThenBy(c => c.Nombre).Take(8).ToList();
        var filas = top.Select(c => ($"redi:cli:{c.Id}", Recortar(c.Nombre, 24),
            (string?)Recortar(c.RazonSocial != null && c.RazonSocial != c.Nombre ? c.RazonSocial : (c.Localidad ?? ""), 72))).ToList();
        filas.Add(("redi:clitexto", "✍️ Dejarlo así", Recortar($"«{q}» — lo eligen en la oficina", 72)));
        filas.Add(("redi:salir", "❌ Salir", null));
        var cuerpo = top.Count == 0
            ? $"No encontré clientes con «{q}». Podés escribir otro nombre, o dejarlo así y lo eligen en la oficina al volcar."
            : $"Clientes con «{q}». Elegí uno, o dejalo así con tus palabras:" + (cands.Count > 8 ? "\n(si no está, escribí más letras)" : "");
        var sid = await _meta.SendListAsync(fromWaId, cuerpo, "Ver clientes", filas, lineaPhoneId: lineaId);
        await RegistrarAsync(numero, cuerpo + " [lista clientes]", sid, lineaId);
        return true;
    }

    private async Task PreguntarRecibeAsync(string fromWaId, string numero, CafeRedirigidaPendiente p, string? lineaId)
    {
        await Paso(p, "recibe");
        var quien = p.ClienteId.HasValue
            ? (await _db.CafeClientes.AsNoTracking().Where(c => c.Id == p.ClienteId).Select(c => c.Nombre).FirstOrDefaultAsync())
            : p.ClienteTexto;
        var botones = new List<(string, string)> { ("redi:priv", "🔒 Queda en privada"), ("redi:salir", "❌ Salir") };
        var cuerpo = $"✅ La envía: *{quien}*\n\n*¿Quién la recibe?* Escribí el nombre del empleado o del proveedor.\nSi no le llegó a nadie, tocá *Queda en privada*.";
        var sid = await _meta.SendButtonsAsync(fromWaId, cuerpo, botones, lineaPhoneId: lineaId);
        await RegistrarAsync(numero, cuerpo, sid, lineaId);
    }

    // 2) Quién la recibe ─────────────────────────────────

    private async Task<bool> BuscarRecibeAsync(string fromWaId, string numero, CafeRedirigidaPendiente p, string q, string? lineaId)
    {
        if (q.Length < 2) { await ResponderAsync(fromWaId, numero, "Escribí al menos 2 letras del nombre.", lineaId); return true; }
        p.RecibeTexto = Recortar(q, 200);
        await Paso(p, "recibe");
        var emps = await _db.NomEmpleados.AsNoTracking().Where(e => e.IsActive && e.Nombre.Contains(q))
            .OrderBy(e => e.Nombre).Take(4).Select(e => new { e.Id, e.Nombre }).ToListAsync();
        // Solo los proveedores habilitados para recibir redirigidos (mismo tilde que la cobranza).
        var provs = await _db.CafeProveedores.AsNoTracking().Where(v => v.IsActive && v.AceptaRedirigido && v.Nombre.Contains(q))
            .OrderBy(v => v.Nombre).Take(4).Select(v => new { v.Id, v.Nombre }).ToListAsync();
        var filas = emps.Select(e => ($"redi:emp:{e.Id}", Recortar(e.Nombre, 24), (string?)"Empleado"))
            .Concat(provs.Select(v => ($"redi:prov:{v.Id}", Recortar(v.Nombre, 24), (string?)"Proveedor"))).ToList();
        filas.Add(("redi:rectexto", "✍️ Dejarlo así", Recortar($"«{q}» — lo eligen en la oficina", 72)));
        filas.Add(("redi:priv", "🔒 Queda en la privada", "No le llegó a nadie"));
        var cuerpo = emps.Count + provs.Count == 0
            ? $"No encontré empleados ni proveedores con «{q}». Podés escribir otro nombre, o dejarlo así y lo eligen en la oficina."
            : $"Con «{q}» encontré estos. Elegí uno, o dejalo así con tus palabras:";
        var sid = await _meta.SendListAsync(fromWaId, cuerpo, "Ver opciones", filas, lineaPhoneId: lineaId);
        await RegistrarAsync(numero, cuerpo + " [lista quién recibe]", sid, lineaId);
        return true;
    }

    // ─────────────── Botones / opciones de lista ───────────────

    private async Task<bool> ManejarBotonAsync(string fromWaId, string numero, string id, string? lineaId)
    {
        var p = await BorradorAsync(numero);
        if (p is null)
        {
            await ResponderAsync(fromWaId, numero, "Se venció la carga anterior. Escribí *redi* para empezar de nuevo.", lineaId);
            return true;
        }
        var partes = id.Split(':'); // redi : que [: valor]
        var que = partes.Length >= 2 ? partes[1] : "";
        var valor = partes.Length >= 3 ? partes[2] : "";

        switch (que)
        {
            case "salir":
                await DescartarAsync(p);
                await ResponderAsync(fromWaId, numero, "👍 Listo, cancelé la redirigida. Cuando quieras, escribí *redi* para empezar de nuevo.", lineaId);
                return true;

            case "cli" when int.TryParse(valor, out var cliId):
                if (!await _db.CafeClientes.AnyAsync(c => c.Id == cliId)) return true;
                p.ClienteId = cliId; p.ClienteTexto = null;
                await PreguntarRecibeAsync(fromWaId, numero, p, lineaId);
                return true;

            case "clitexto":
                p.ClienteId = null;
                await PreguntarRecibeAsync(fromWaId, numero, p, lineaId);
                return true;

            case "emp" when int.TryParse(valor, out var empId):
            {
                var emp = await _db.NomEmpleados.AsNoTracking().FirstOrDefaultAsync(e => e.Id == empId);
                if (emp is null) return true;
                p.RecibeTipo = "EMPLEADO"; p.EmpleadoId = empId; p.ProveedorId = null; p.RecibeTexto = null;
                // Igual que la cobranza: solo se pregunta viajes/sueldo al que cobra por entrega.
                var tieneViajes = await _db.ViajesEmpleados.AnyAsync(v => v.IsActive && v.NomEmpleadoId == empId);
                if (tieneViajes)
                {
                    await Paso(p, "destino");
                    var botones = new List<(string, string)> { ("redi:dest:viajes", "🚚 De los viajes"), ("redi:dest:sueldo", "💵 Del sueldo") };
                    var cuerpo = $"✅ La recibe: *{emp.Nombre}*\n¿Se le descuenta de los *viajes* o del *sueldo*?";
                    var sid = await _meta.SendButtonsAsync(fromWaId, cuerpo, botones, lineaPhoneId: lineaId);
                    await RegistrarAsync(numero, cuerpo, sid, lineaId);
                    return true;
                }
                p.Destino = "sueldo";
                await PedirImporteAsync(fromWaId, numero, p, $"✅ La recibe: *{emp.Nombre}* (se le descuenta del sueldo)", lineaId);
                return true;
            }

            case "dest":
                p.Destino = valor == "viajes" ? "viajes" : "sueldo";
                await PedirImporteAsync(fromWaId, numero, p, $"✅ Se descuenta de los {(p.Destino == "viajes" ? "viajes" : "sueldo")}", lineaId);
                return true;

            case "prov" when int.TryParse(valor, out var provId):
            {
                var prov = await _db.CafeProveedores.AsNoTracking().FirstOrDefaultAsync(v => v.Id == provId);
                if (prov is null) return true;
                p.RecibeTipo = "PROVEEDOR"; p.ProveedorId = provId; p.EmpleadoId = null; p.Destino = null; p.RecibeTexto = null;
                await PedirImporteAsync(fromWaId, numero, p, $"✅ La recibe: *{prov.Nombre}* (proveedor)", lineaId);
                return true;
            }

            case "rectexto":
                p.RecibeTipo = "TEXTO"; p.EmpleadoId = null; p.ProveedorId = null; p.Destino = null;
                await PedirImporteAsync(fromWaId, numero, p, $"✅ La recibe: *{p.RecibeTexto}* (lo eligen en la oficina)", lineaId);
                return true;

            case "priv":
                p.RecibeTipo = "PRIVADA"; p.EmpleadoId = null; p.ProveedorId = null; p.Destino = null; p.RecibeTexto = null;
                await PedirImporteAsync(fromWaId, numero, p, "✅ Queda en la privada", lineaId);
                return true;

            case "listo":
                return await MostrarResumenAsync(fromWaId, numero, p, lineaId);

            case "ok":
                if (p.Paso != "confirmar") return true;
                p.Estado = "PENDIENTE"; p.Paso = null; p.ExpiraAt = null; p.UpdatedAt = DateTime.UtcNow;
                p.CreatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                await ResponderAsync(fromWaId, numero, "✅ *Listo, quedó en la bolsita 💰* para volcar en la oficina.\nGracias 🙌", lineaId);
                return true;
        }
        return true;
    }

    // 3) Importe / 4) Comprobantes / 5) Resumen ──────────

    private async Task PedirImporteAsync(string fromWaId, string numero, CafeRedirigidaPendiente p, string encabezado, string? lineaId)
    {
        await Paso(p, "importe");
        await ResponderAsync(fromWaId, numero, $"{encabezado}\n\n*¿Cuánto?* Escribí el importe (ej: 45000).", lineaId);
    }

    private async Task PedirAdjuntoAsync(string fromWaId, string numero, CafeRedirigidaPendiente p, string? lineaId, string? prefijo = null)
    {
        var cant = await _db.CafeRedirigidasPendientesAdjuntos.CountAsync(a => a.PendienteId == p.Id);
        var cuerpo = prefijo ?? (cant == 0
            ? $"✅ Importe: *{Money(p.Importe)}*\n\n📎 *¿Querés mandar una foto o el comprobante?* Mandalo ahora (foto, captura, PDF, lo que sea). Si no hay, tocá *Listo*."
            : $"📎 Recibido ({cant}). ¿Mandás otro? Si no, tocá *Listo*.");
        var botones = new List<(string, string)> { ("redi:listo", cant == 0 ? "✅ Listo, sin foto" : "✅ Listo"), ("redi:salir", "❌ Salir") };
        var sid = await _meta.SendButtonsAsync(fromWaId, cuerpo, botones, lineaPhoneId: lineaId);
        await RegistrarAsync(numero, cuerpo, sid, lineaId);
    }

    private async Task<bool> AgregarAdjuntoAsync(string fromWaId, string numero, CafeRedirigidaPendiente p, string? mediaUrl, string? mediaNombre, string? lineaId)
    {
        // El webhook ya bajó el archivo de Meta y lo guardó en /data/whatsapp-uploads: la URL termina
        // en /files/{token}{ext}. Con el token se encuentra el archivo guardado.
        var token = string.IsNullOrEmpty(mediaUrl) ? null : Path.GetFileNameWithoutExtension(mediaUrl.Split('?')[0]);
        var up = token is null ? null : await _db.WhatsAppTwilioUploads.AsNoTracking().FirstOrDefaultAsync(u => u.Token == token);
        if (up is null)
        {
            await ResponderAsync(fromWaId, numero, "⚠️ No pude guardar ese archivo. Probá mandarlo de nuevo, o tocá *Listo* para seguir sin él.", lineaId);
            return true;
        }
        _db.CafeRedirigidasPendientesAdjuntos.Add(new CafeRedirigidaPendienteAdjunto
        {
            PendienteId = p.Id, StoredFilename = up.StoredFilename,
            NombreOriginal = Recortar(mediaNombre ?? up.OriginalFilename, 260),
            MimeType = up.ContentType, Tamano = up.SizeBytes
        });
        await Paso(p, "adjunto");
        await PedirAdjuntoAsync(fromWaId, numero, p, lineaId);
        return true;
    }

    private async Task<bool> MostrarResumenAsync(string fromWaId, string numero, CafeRedirigidaPendiente p, string? lineaId)
    {
        if (p.Importe <= 0) { await PedirImporteAsync(fromWaId, numero, p, "Falta el importe.", lineaId); return true; }
        await Paso(p, "confirmar");
        var cliente = p.ClienteId.HasValue
            ? await _db.CafeClientes.AsNoTracking().Where(c => c.Id == p.ClienteId).Select(c => c.Nombre).FirstOrDefaultAsync()
            : $"{p.ClienteTexto} (lo eligen en la oficina)";
        var recibe = p.RecibeTipo switch
        {
            "EMPLEADO" => (await _db.NomEmpleados.AsNoTracking().Where(e => e.Id == p.EmpleadoId).Select(e => e.Nombre).FirstOrDefaultAsync())
                          + $" · {(p.Destino == "viajes" ? "de los viajes" : "del sueldo")}",
            "PROVEEDOR" => await _db.CafeProveedores.AsNoTracking().Where(v => v.Id == p.ProveedorId).Select(v => v.Nombre).FirstOrDefaultAsync() + " (proveedor)",
            "PRIVADA" => "queda en la privada",
            _ => $"{p.RecibeTexto} (lo eligen en la oficina)"
        };
        var cant = await _db.CafeRedirigidasPendientesAdjuntos.CountAsync(a => a.PendienteId == p.Id);
        var cuerpo = $"🔁 *Redirigida — revisá:*\n\n👤 La envía: {cliente}\n➡️ La recibe: {recibe}\n💲 Importe: {Money(p.Importe)}\n📎 Comprobantes: {(cant == 0 ? "ninguno" : cant.ToString())}";
        var botones = new List<(string, string)> { ("redi:ok", "✅ Confirmar"), ("redi:salir", "❌ Cancelar") };
        var sid = await _meta.SendButtonsAsync(fromWaId, cuerpo, botones, lineaPhoneId: lineaId);
        await RegistrarAsync(numero, cuerpo, sid, lineaId);
        return true;
    }

    // ─────────────── Estado ───────────────

    private async Task<CafeRedirigidaPendiente?> BorradorAsync(string numero)
    {
        var p = await _db.CafeRedirigidasPendientes
            .Where(x => x.EnviadoNumero == numero && x.Estado == "BORRADOR")
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync();
        if (p is null) return null;
        if (p.ExpiraAt < DateTime.UtcNow) { await DescartarAsync(p); return null; }
        return p;
    }

    private async Task Paso(CafeRedirigidaPendiente p, string paso)
    {
        p.Paso = paso;
        p.ExpiraAt = DateTime.UtcNow.AddMinutes(MinutosParaContestar);
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    private async Task DescartarAsync(CafeRedirigidaPendiente p)
    {
        var adj = await _db.CafeRedirigidasPendientesAdjuntos.Where(a => a.PendienteId == p.Id).ToListAsync();
        _db.CafeRedirigidasPendientesAdjuntos.RemoveRange(adj);
        _db.CafeRedirigidasPendientes.Remove(p);
        await _db.SaveChangesAsync();
    }

    /// <summary>Misma lista que el PAGO (PagosMovil_WaAutorizados), incluidos los "solo redirigida".
    /// Tolerante por los últimos 10 dígitos, igual que el PAGO.</summary>
    private async Task<PagosMovilWaAutorizado?> BuscarAutorizadoAsync(string numero)
    {
        var activos = await _db.PagosMovilWaAutorizados.AsNoTracking().Where(a => a.Activo).ToListAsync();
        var exacto = activos.FirstOrDefault(a => a.Numero == numero);
        if (exacto is not null) return exacto;
        var inDig = SoloDigitos(numero);
        if (inDig.Length < 10) return null;
        var cola = inDig[^10..];
        return activos.FirstOrDefault(a => { var d = SoloDigitos(a.Numero); return d.Length >= 10 && d[^10..] == cola; });
    }

    private static string SoloDigitos(string s) => new((s ?? "").Where(char.IsDigit).ToArray());

    private static readonly HashSet<string> Cancelar = new(StringComparer.OrdinalIgnoreCase)
    { "cancelar", "cancela", "salir", "chau", "basta", "fin", "terminar" };
    private static bool EsCancelar(string t) => Cancelar.Contains((t ?? "").Trim());

    // ─────────────── Envío / registro en el chat ───────────────

    private async Task ResponderAsync(string fromWaId, string numero, string texto, string? lineaId)
    {
        var sid = await _meta.SendTextAsync(fromWaId, texto, lineaPhoneId: lineaId);
        await RegistrarAsync(numero, texto, sid, lineaId);
    }

    private async Task RegistrarAsync(string numero, string cuerpo, string? sid, string? lineaId)
    {
        _db.WhatsAppTwilioMensajes.Add(new WhatsAppTwilioMensaje
        {
            Direccion = "OUTGOING", Numero = numero, Cuerpo = cuerpo, LineaPhoneId = lineaId,
            TwilioMessageSid = sid, Canal = "CLOUD", Procesado = true, CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }

    private static string Recortar(string s, int max) => string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..(max - 1)] + "…");

    private static string Money(decimal v)
        => "$" + v.ToString(v % 1 == 0 ? "#,##0" : "#,##0.00", CultureInfo.InvariantCulture).Replace(",", "#").Replace(".", ",").Replace("#", ".");
}
