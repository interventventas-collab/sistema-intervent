using System.Globalization;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-08-13 (pedido del usuario): CARGAR UN PAGO ESCRIBIENDO "PAGO" POR WHATSAPP.
///
/// Un número AUTORIZADO le escribe "PAGO" al WhatsApp de la empresa. El bot lo lleva paso a paso:
///   1) ¿A un empleado o a un proveedor? (dos botones)
///   2) ¿A quién? (lista de empleados / de proveedores con deuda; también se puede escribir el nombre)
///   3) Empleado → concepto (Sueldo/Adelanto/Otro) y monto. Proveedor → qué factura y cuánto.
///   4) ¿Cómo se pagó? (efectivo / transferencia / Mercado Pago / cheque)
/// Al terminar deja el pago en estado PENDIENTE en PagosMovil_Pendientes (la MISMA bandeja que la
/// pantalla "Pagos desde el móvil"). NO impacta saldos hasta que alguien lo confirma desde la PC
/// en Tesorería → Pagos pendientes.
///
/// Como es PLATA, solo funciona desde números habilitados en PagosMovil_WaAutorizados.
/// </summary>
public class WhatsAppPagoBotService
{
    private readonly AppDbContext _db;
    private readonly MetaWhatsAppService _meta;
    private readonly ILogger<WhatsAppPagoBotService> _log;

    public WhatsAppPagoBotService(AppDbContext db, MetaWhatsAppService meta, ILogger<WhatsAppPagoBotService> log)
    {
        _db = db; _meta = meta; _log = log;
    }

    /// <summary>Intenta atender el mensaje como parte del asistente de PAGO. Devuelve true si lo
    /// manejó (el webhook NO debe seguir con empleado/pedido/bienvenida). false = no era para acá.</summary>
    public async Task<bool> TryHandleAsync(string fromWaId, string numero, string? tipo,
        string? idInteractivo, string? cuerpo, string? lineaId)
    {
        try
        {
            // 1) ¿Tocó un botón/opción del asistente? (id "pago:...")
            if (!string.IsNullOrEmpty(idInteractivo) && idInteractivo.StartsWith("pago:", StringComparison.Ordinal))
                return await ManejarBotonAsync(fromWaId, numero, idInteractivo, lineaId);

            if (tipo != "text" || string.IsNullOrWhiteSpace(cuerpo)) return false;
            var texto = cuerpo.Trim();

            // 2) ¿Escribió "PAGO"? → arrancar (solo números autorizados).
            if (texto.Equals("PAGO", StringComparison.OrdinalIgnoreCase))
            {
                var aut = await _db.PagosMovilWaAutorizados.AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Activo && a.Numero == numero);
                if (aut is null)
                {
                    _log.LogInformation("[BotPago] '{Num}' escribió PAGO pero no está autorizado", numero);
                    return false; // que caiga al flujo normal como un mensaje cualquiera
                }
                await IniciarAsync(fromWaId, numero, aut.Nombre, lineaId);
                return true;
            }

            // 3) ¿Hay un asistente en curso para este número? → interpretar el texto según el paso.
            var st = await _db.PagosMovilWaEstados.FirstOrDefaultAsync(x => x.Numero == numero);
            if (st is null || string.IsNullOrEmpty(st.Paso)) return false;
            if (st.ExpiraAt < DateTime.UtcNow) { await LimpiarAsync(numero); return false; }

            if (EsCancelar(texto))
            {
                await LimpiarAsync(numero);
                await ResponderAsync(fromWaId, numero, "👍 Listo, cancelé la carga del pago. Cuando quieras, escribí *PAGO* para empezar de nuevo.", lineaId);
                return true;
            }

            return await ManejarTextoAsync(fromWaId, numero, texto, st, lineaId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[BotPago] error atendiendo a {Num}", numero);
            try { await ResponderAsync(fromWaId, numero, "Uf, algo falló cargando el pago. Escribí *PAGO* para empezar de nuevo.", lineaId); } catch { }
            return true;
        }
    }

    // ─────────────── Arranque ───────────────

    private async Task IniciarAsync(string fromWaId, string numero, string nombre, string? lineaId)
    {
        await GuardarEstadoAsync(numero, st =>
        {
            st.Paso = "tipo"; st.Tipo = null; st.EmpleadoId = null; st.ProveedorId = null;
            st.CompraId = null; st.CompraSaldo = null; st.Concepto = null; st.Monto = null;
        });
        var botones = new List<(string, string)>
        {
            ("pago:tipo:empleado", "👷 Empleado"),
            ("pago:tipo:proveedor", "🚚 Proveedor"),
        };
        var sid = await _meta.SendButtonsAsync(fromWaId, $"💵 Cargar un PAGO\nHola {nombre} 👋 ¿A quién le pagaste?", botones, lineaPhoneId: lineaId);
        await RegistrarSalienteAsync(numero, "💵 Cargar un PAGO — ¿empleado o proveedor?", sid, lineaId);
    }

    // ─────────────── Botones / opciones de lista ───────────────

    private async Task<bool> ManejarBotonAsync(string fromWaId, string numero, string id, string? lineaId)
    {
        var st = await _db.PagosMovilWaEstados.FirstOrDefaultAsync(x => x.Numero == numero);
        if (st is null || st.ExpiraAt < DateTime.UtcNow)
        {
            await ResponderAsync(fromWaId, numero, "Se venció la carga anterior. Escribí *PAGO* para empezar de nuevo.", lineaId);
            return true;
        }

        var partes = id.Split(':'); // pago : que : valor
        if (partes.Length < 3) return true;
        var que = partes[1];
        var valor = partes[2];

        switch (que)
        {
            case "tipo":
                if (valor == "empleado") return await MostrarEmpleadosAsync(fromWaId, numero, st, lineaId);
                if (valor == "proveedor") return await MostrarProveedoresAsync(fromWaId, numero, st, lineaId);
                return true;

            case "emp": // eligió un empleado de la lista
                if (int.TryParse(valor, out var empId)) return await ElegirEmpleadoAsync(fromWaId, numero, st, empId, lineaId);
                return true;

            case "conc": // concepto del pago a empleado
                return await ElegirConceptoAsync(fromWaId, numero, st, valor, lineaId);

            case "prov": // eligió un proveedor de la lista
                if (int.TryParse(valor, out var provId)) return await ElegirProveedorAsync(fromWaId, numero, st, provId, lineaId);
                return true;

            case "fact": // eligió una factura del proveedor
                if (int.TryParse(valor, out var compraId)) return await ElegirFacturaAsync(fromWaId, numero, st, compraId, lineaId);
                return true;

            case "medio": // medio de pago → cerrar y crear el pendiente
                return await ElegirMedioAsync(fromWaId, numero, st, valor, lineaId);
        }
        return true;
    }

    // ─────────────── Texto libre según el paso ───────────────

    private async Task<bool> ManejarTextoAsync(string fromWaId, string numero, string texto, PagosMovilWaEstado st, string? lineaId)
    {
        switch (st.Paso)
        {
            case "emp_elegir":
                return await BuscarEmpleadoPorNombreAsync(fromWaId, numero, st, texto, lineaId);

            case "prov_elegir":
                return await BuscarProveedorPorNombreAsync(fromWaId, numero, st, texto, lineaId);

            case "emp_concepto_texto":
                st.Concepto = texto.Length > 55 ? texto[..55] : texto;
                await GuardarEstadoAsync(numero, s => { s.Concepto = st.Concepto; s.Paso = "emp_monto"; });
                await PedirMontoAsync(fromWaId, numero, null, lineaId);
                return true;

            case "emp_monto":
                if (!TryParseMonto(texto, out var montoE) || montoE <= 0)
                {
                    await ResponderAsync(fromWaId, numero, "No entendí el monto. Escribí solo el número, por ejemplo *15000*.", lineaId);
                    return true;
                }
                await GuardarEstadoAsync(numero, s => { s.Monto = montoE; s.Paso = "emp_medio"; });
                await MostrarMediosAsync(fromWaId, numero, lineaId);
                return true;

            case "prov_monto":
                decimal montoP;
                if (texto.Equals("TODO", StringComparison.OrdinalIgnoreCase) || texto.Equals("TODOS", StringComparison.OrdinalIgnoreCase))
                    montoP = st.CompraSaldo ?? 0m;
                else if (!TryParseMonto(texto, out montoP) || montoP <= 0)
                {
                    await ResponderAsync(fromWaId, numero, "No entendí el monto. Escribí solo el número, o respondé *TODO* para pagar el saldo completo.", lineaId);
                    return true;
                }
                if (st.CompraSaldo.HasValue && montoP > st.CompraSaldo.Value + 0.01m)
                {
                    await ResponderAsync(fromWaId, numero, $"Ese monto es mayor al saldo de la factura ({Money(st.CompraSaldo.Value)}). Escribí un monto menor o igual, o *TODO*.", lineaId);
                    return true;
                }
                await GuardarEstadoAsync(numero, s => { s.Monto = montoP; s.Paso = "prov_medio"; });
                await MostrarMediosAsync(fromWaId, numero, lineaId);
                return true;
        }
        // En pasos de botón (tipo/concepto/medio) esperamos que TOQUE, no que escriba.
        await ResponderAsync(fromWaId, numero, "👆 Tocá una de las opciones de arriba, o escribí *cancelar* para salir.", lineaId);
        return true;
    }

    // ─────────────── Empleados ───────────────

    private async Task<bool> MostrarEmpleadosAsync(string fromWaId, string numero, PagosMovilWaEstado st, string? lineaId)
    {
        await GuardarEstadoAsync(numero, s => { s.Tipo = "empleado"; s.Paso = "emp_elegir"; });
        var emps = await _db.NomEmpleados.AsNoTracking()
            .Where(e => e.IsActive).OrderBy(e => e.Nombre).Take(10).ToListAsync();
        if (emps.Count == 0)
        {
            await LimpiarAsync(numero);
            await ResponderAsync(fromWaId, numero, "No tenés empleados activos cargados en el sistema.", lineaId);
            return true;
        }
        var filas = emps.Select(e => ($"pago:emp:{e.Id}", Recortar(e.Nombre, 24), (string?)e.Puesto)).ToList();
        var totalActivos = await _db.NomEmpleados.CountAsync(e => e.IsActive);
        var cuerpo = "👷 ¿A qué empleado le pagaste?" + (totalActivos > 10 ? "\n(si no está en la lista, escribí su nombre)" : "");
        var sid = await _meta.SendListAsync(fromWaId, cuerpo, "Ver empleados", filas, lineaPhoneId: lineaId);
        await RegistrarSalienteAsync(numero, cuerpo + " [lista empleados]", sid, lineaId);
        return true;
    }

    private async Task<bool> BuscarEmpleadoPorNombreAsync(string fromWaId, string numero, PagosMovilWaEstado st, string q, string? lineaId)
    {
        if (q.Length < 2) { await ResponderAsync(fromWaId, numero, "Escribí al menos 2 letras del nombre del empleado.", lineaId); return true; }
        var emps = await _db.NomEmpleados.AsNoTracking()
            .Where(e => e.IsActive && e.Nombre.Contains(q)).OrderBy(e => e.Nombre).Take(10).ToListAsync();
        if (emps.Count == 0) { await ResponderAsync(fromWaId, numero, $"No encontré ningún empleado con «{q}». Probá con otro nombre.", lineaId); return true; }
        if (emps.Count == 1) return await ElegirEmpleadoAsync(fromWaId, numero, st, emps[0].Id, lineaId);
        var filas = emps.Select(e => ($"pago:emp:{e.Id}", Recortar(e.Nombre, 24), (string?)e.Puesto)).ToList();
        var sid = await _meta.SendListAsync(fromWaId, $"Encontré varios con «{q}». Elegí:", "Ver empleados", filas, lineaPhoneId: lineaId);
        await RegistrarSalienteAsync(numero, "Varios empleados coinciden [lista]", sid, lineaId);
        return true;
    }

    private async Task<bool> ElegirEmpleadoAsync(string fromWaId, string numero, PagosMovilWaEstado st, int empId, string? lineaId)
    {
        var emp = await _db.NomEmpleados.AsNoTracking().FirstOrDefaultAsync(e => e.Id == empId && e.IsActive);
        if (emp is null) { await ResponderAsync(fromWaId, numero, "No encontré ese empleado. Escribí el nombre de nuevo.", lineaId); return true; }
        await GuardarEstadoAsync(numero, s => { s.EmpleadoId = empId; s.Paso = "emp_concepto"; });
        var botones = new List<(string, string)>
        {
            ("pago:conc:sueldo", "Sueldo"),
            ("pago:conc:adelanto", "Adelanto"),
            ("pago:conc:otro", "Otro"),
        };
        var sid = await _meta.SendButtonsAsync(fromWaId, $"👷 {emp.Nombre}\n¿Por qué concepto es el pago?", botones, lineaPhoneId: lineaId);
        await RegistrarSalienteAsync(numero, $"Empleado {emp.Nombre} — ¿concepto?", sid, lineaId);
        return true;
    }

    private async Task<bool> ElegirConceptoAsync(string fromWaId, string numero, PagosMovilWaEstado st, string valor, string? lineaId)
    {
        if (valor == "otro")
        {
            await GuardarEstadoAsync(numero, s => s.Paso = "emp_concepto_texto");
            await ResponderAsync(fromWaId, numero, "✍️ Escribí el concepto del pago (ej: aguinaldo, bono, vacaciones).", lineaId);
            return true;
        }
        var concepto = valor == "adelanto" ? "adelanto" : "sueldo";
        await GuardarEstadoAsync(numero, s => { s.Concepto = concepto; s.Paso = "emp_monto"; });
        await PedirMontoAsync(fromWaId, numero, null, lineaId);
        return true;
    }

    // ─────────────── Proveedores ───────────────

    private async Task<bool> MostrarProveedoresAsync(string fromWaId, string numero, PagosMovilWaEstado st, string? lineaId)
    {
        await GuardarEstadoAsync(numero, s => { s.Tipo = "proveedor"; s.Paso = "prov_elegir"; });
        var conDeuda = await ProveedoresConDeudaAsync();
        if (conDeuda.Count == 0)
        {
            await LimpiarAsync(numero);
            await ResponderAsync(fromWaId, numero, "No hay proveedores con facturas pendientes de pago en el sistema.", lineaId);
            return true;
        }
        var top = conDeuda.OrderByDescending(x => x.Deuda).Take(10).ToList();
        var filas = top.Select(p => ($"pago:prov:{p.Id}", Recortar(p.Nombre, 24), (string?)$"Debe {Money(p.Deuda)}")).ToList();
        var cuerpo = "🚚 ¿A qué proveedor le pagaste?" + (conDeuda.Count > 10 ? "\n(si no está en la lista, escribí su nombre)" : "");
        var sid = await _meta.SendListAsync(fromWaId, cuerpo, "Ver proveedores", filas, lineaPhoneId: lineaId);
        await RegistrarSalienteAsync(numero, cuerpo + " [lista proveedores]", sid, lineaId);
        return true;
    }

    private async Task<bool> BuscarProveedorPorNombreAsync(string fromWaId, string numero, PagosMovilWaEstado st, string q, string? lineaId)
    {
        if (q.Length < 2) { await ResponderAsync(fromWaId, numero, "Escribí al menos 2 letras del nombre del proveedor.", lineaId); return true; }
        var conDeuda = await ProveedoresConDeudaAsync();
        var match = conDeuda.Where(p => p.Nombre.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderByDescending(p => p.Deuda).Take(10).ToList();
        if (match.Count == 0) { await ResponderAsync(fromWaId, numero, $"No encontré ningún proveedor con deuda que coincida con «{q}».", lineaId); return true; }
        if (match.Count == 1) return await ElegirProveedorAsync(fromWaId, numero, st, match[0].Id, lineaId);
        var filas = match.Select(p => ($"pago:prov:{p.Id}", Recortar(p.Nombre, 24), (string?)$"Debe {Money(p.Deuda)}")).ToList();
        var sid = await _meta.SendListAsync(fromWaId, $"Encontré varios con «{q}». Elegí:", "Ver proveedores", filas, lineaPhoneId: lineaId);
        await RegistrarSalienteAsync(numero, "Varios proveedores coinciden [lista]", sid, lineaId);
        return true;
    }

    private async Task<bool> ElegirProveedorAsync(string fromWaId, string numero, PagosMovilWaEstado st, int provId, string? lineaId)
    {
        var prov = await _db.CafeProveedores.AsNoTracking().FirstOrDefaultAsync(p => p.Id == provId);
        if (prov is null) { await ResponderAsync(fromWaId, numero, "No encontré ese proveedor. Escribí el nombre de nuevo.", lineaId); return true; }

        var facturas = await ComprasPendientesAsync(provId);
        if (facturas.Count == 0)
        {
            await LimpiarAsync(numero);
            await ResponderAsync(fromWaId, numero, $"🚚 {prov.Nombre} no tiene facturas pendientes de pago cargadas. No puedo cargar el pago desde acá.", lineaId);
            return true;
        }
        await GuardarEstadoAsync(numero, s => { s.ProveedorId = provId; s.Paso = "prov_factura"; });
        var top = facturas.OrderBy(f => f.Fecha).Take(10).ToList();
        var filas = top.Select(f =>
        {
            var titulo = !string.IsNullOrWhiteSpace(f.NumeroComprobante) ? f.NumeroComprobante! : f.Numero;
            return ($"pago:fact:{f.Id}", Recortar(titulo, 24), (string?)$"{f.Fecha:dd/MM/yy} · saldo {Money(f.Saldo)}");
        }).ToList();
        var cuerpo = $"🧾 {prov.Nombre}\n¿Qué factura pagaste?";
        var sid = await _meta.SendListAsync(fromWaId, cuerpo, "Ver facturas", filas, lineaPhoneId: lineaId);
        await RegistrarSalienteAsync(numero, cuerpo + " [lista facturas]", sid, lineaId);
        return true;
    }

    private async Task<bool> ElegirFacturaAsync(string fromWaId, string numero, PagosMovilWaEstado st, int compraId, string? lineaId)
    {
        if (st.ProveedorId is null) { await ResponderAsync(fromWaId, numero, "Se perdió el proveedor. Escribí *PAGO* para empezar de nuevo.", lineaId); return true; }
        var facturas = await ComprasPendientesAsync(st.ProveedorId.Value);
        var f = facturas.FirstOrDefault(x => x.Id == compraId);
        if (f is null) { await ResponderAsync(fromWaId, numero, "No encontré esa factura. Elegí de nuevo.", lineaId); return true; }
        await GuardarEstadoAsync(numero, s => { s.CompraId = compraId; s.CompraSaldo = f.Saldo; s.Paso = "prov_monto"; });
        var titulo = !string.IsNullOrWhiteSpace(f.NumeroComprobante) ? f.NumeroComprobante! : f.Numero;
        await ResponderAsync(fromWaId, numero, $"🧾 Factura {titulo} — saldo {Money(f.Saldo)}\n¿Cuánto pagaste? Escribí el monto, o respondé *TODO* para pagar todo el saldo.", lineaId);
        return true;
    }

    // ─────────────── Medio de pago + cierre ───────────────

    private async Task MostrarMediosAsync(string fromWaId, string numero, string? lineaId)
    {
        var filas = new List<(string, string, string?)>
        {
            ("pago:medio:efectivo",      "💵 Efectivo",       null),
            ("pago:medio:transferencia", "🏦 Transferencia",  null),
            ("pago:medio:mp",            "📲 Mercado Pago",   null),
            ("pago:medio:cheque",        "🧾 Cheque",         null),
        };
        var sid = await _meta.SendListAsync(fromWaId, "¿Cómo se pagó?", "Ver formas de pago", filas, lineaPhoneId: lineaId);
        await RegistrarSalienteAsync(numero, "¿Cómo se pagó? [lista medios]", sid, lineaId);
    }

    private async Task<bool> ElegirMedioAsync(string fromWaId, string numero, PagosMovilWaEstado st, string valor, string? lineaId)
    {
        var medio = valor switch
        {
            "efectivo" => "efectivo",
            "transferencia" => "transferencia",
            "mp" => "mp",
            "cheque" => "cheque",
            _ => valor
        };

        var aut = await _db.PagosMovilWaAutorizados.AsNoTracking().FirstOrDefaultAsync(a => a.Activo && a.Numero == numero);
        if (aut is null) { await LimpiarAsync(numero); await ResponderAsync(fromWaId, numero, "Tu número ya no está autorizado para cargar pagos.", lineaId); return true; }

        if (st.Tipo == "empleado")
        {
            if (st.EmpleadoId is null || st.Monto is null || string.IsNullOrWhiteSpace(st.Concepto))
            { await ResponderAsync(fromWaId, numero, "Faltan datos del pago. Escribí *PAGO* para empezar de nuevo.", lineaId); await LimpiarAsync(numero); return true; }

            var emp = await _db.NomEmpleados.AsNoTracking().FirstOrDefaultAsync(e => e.Id == st.EmpleadoId);
            var pend = new PagosMovilPendiente
            {
                Tipo = "empleado",
                EmpleadoId = st.EmpleadoId,
                Concepto = st.Concepto!.Trim(),
                Monto = st.Monto!.Value,
                MedioPago = medio,
                Notas = $"Cargado por WhatsApp ({aut.Nombre})",
                Estado = "PENDIENTE",
                CreadoPorUsuarioId = aut.UserId,
                CreatedAt = DateTime.UtcNow
            };
            _db.PagosMovilPendientes.Add(pend);
            await _db.SaveChangesAsync();
            await LimpiarAsync(numero);
            await ResponderAsync(fromWaId, numero,
                $"✅ Pago cargado como PENDIENTE:\n👷 {emp?.Nombre}\n🏷️ {st.Concepto}\n💲 {Money(st.Monto.Value)}\n💳 {NombreMedio(medio)}\n\n📋 Falta que lo confirmen desde *Tesorería → Pagos pendientes* para que impacte.", lineaId);
            _log.LogInformation("[BotPago] pendiente EMPLEADO #{Id} cargado por {Num}", pend.Id, numero);
            return true;
        }

        if (st.Tipo == "proveedor")
        {
            if (st.ProveedorId is null || st.CompraId is null || st.Monto is null)
            { await ResponderAsync(fromWaId, numero, "Faltan datos del pago. Escribí *PAGO* para empezar de nuevo.", lineaId); await LimpiarAsync(numero); return true; }

            var prov = await _db.CafeProveedores.AsNoTracking().FirstOrDefaultAsync(p => p.Id == st.ProveedorId);
            var pend = new PagosMovilPendiente
            {
                Tipo = "proveedor",
                ProveedorId = st.ProveedorId,
                Concepto = "Pago facturas",
                Monto = st.Monto!.Value,
                MedioPago = medio,
                Notas = $"Cargado por WhatsApp ({aut.Nombre})",
                Estado = "PENDIENTE",
                CreadoPorUsuarioId = aut.UserId,
                CreatedAt = DateTime.UtcNow,
                Comprobantes = new List<PagosMovilPendienteComprobante>
                {
                    new() { CompraId = st.CompraId.Value, Importe = st.Monto!.Value }
                }
            };
            _db.PagosMovilPendientes.Add(pend);
            await _db.SaveChangesAsync();
            await LimpiarAsync(numero);
            await ResponderAsync(fromWaId, numero,
                $"✅ Pago cargado como PENDIENTE:\n🚚 {prov?.Nombre}\n💲 {Money(st.Monto.Value)}\n💳 {NombreMedio(medio)}\n\n📋 Falta que lo confirmen desde *Tesorería → Pagos pendientes* para que impacte.", lineaId);
            _log.LogInformation("[BotPago] pendiente PROVEEDOR #{Id} cargado por {Num}", pend.Id, numero);
            return true;
        }

        await ResponderAsync(fromWaId, numero, "Se perdió el tipo de pago. Escribí *PAGO* para empezar de nuevo.", lineaId);
        await LimpiarAsync(numero);
        return true;
    }

    private async Task PedirMontoAsync(string fromWaId, string numero, string? extra, string? lineaId)
        => await ResponderAsync(fromWaId, numero, (extra ?? "") + "💲 ¿Cuánto? Escribí solo el número (ej: 15000).", lineaId);

    // ─────────────── Cálculo de deuda de proveedores (mismo criterio que PagosMovilController) ───────────────

    private record ProvDeuda(int Id, string Nombre, decimal Deuda);
    private record FacturaPend(int Id, string Numero, string? NumeroComprobante, DateTime Fecha, decimal Saldo);

    private async Task<List<ProvDeuda>> ProveedoresConDeudaAsync()
    {
        var compras = await _db.CafeCompras
            .Where(c => c.Estado != "ANULADA" && c.ProveedorId != null)
            .Select(c => new
            {
                c.Id,
                ProveedorId = c.ProveedorId!.Value,
                c.Total,
                ProveedorNombre = c.ProveedorNav != null ? c.ProveedorNav.Nombre : (c.ProveedorNombreSnapshot ?? "—")
            })
            .ToListAsync();
        if (compras.Count == 0) return new();

        var ids = compras.Select(c => c.Id).ToList();
        var pagado = await _db.CafePagosProveedorComprobantes
            .Where(c => c.CompraId != null && ids.Contains(c.CompraId!.Value) && c.Pago!.Estado == "VIGENTE")
            .GroupBy(c => c.CompraId!.Value)
            .Select(g => new { CompraId = g.Key, Total = g.Sum(x => x.Importe) })
            .ToListAsync();
        var dict = pagado.ToDictionary(p => p.CompraId, p => p.Total);

        return compras
            .Select(c => new { c.ProveedorId, c.ProveedorNombre, Saldo = c.Total - (dict.TryGetValue(c.Id, out var p) ? p : 0m) })
            .Where(x => x.Saldo > 0.01m)
            .GroupBy(x => new { x.ProveedorId, x.ProveedorNombre })
            .Select(g => new ProvDeuda(g.Key.ProveedorId, g.Key.ProveedorNombre, g.Sum(x => x.Saldo)))
            .ToList();
    }

    private async Task<List<FacturaPend>> ComprasPendientesAsync(int proveedorId)
    {
        var compras = await _db.CafeCompras
            .Where(c => c.ProveedorId == proveedorId && c.Estado != "ANULADA")
            .Select(c => new { c.Id, c.Numero, c.Fecha, c.Total, c.NumeroComprobante })
            .ToListAsync();
        if (compras.Count == 0) return new();

        var ids = compras.Select(c => c.Id).ToList();
        var pagado = await _db.CafePagosProveedorComprobantes
            .Where(c => c.CompraId != null && ids.Contains(c.CompraId!.Value) && c.Pago!.Estado == "VIGENTE")
            .GroupBy(c => c.CompraId!.Value)
            .Select(g => new { CompraId = g.Key, Total = g.Sum(x => x.Importe) })
            .ToListAsync();
        var dict = pagado.ToDictionary(p => p.CompraId, p => p.Total);

        return compras
            .Select(c => new FacturaPend(c.Id, c.Numero, c.NumeroComprobante, c.Fecha,
                c.Total - (dict.TryGetValue(c.Id, out var p) ? p : 0m)))
            .Where(x => x.Saldo > 0.01m)
            .ToList();
    }

    // ─────────────── Estado (memoria corta) ───────────────

    private async Task GuardarEstadoAsync(string numero, Action<PagosMovilWaEstado> mutar)
    {
        var st = await _db.PagosMovilWaEstados.FirstOrDefaultAsync(x => x.Numero == numero);
        if (st is null) { st = new PagosMovilWaEstado { Numero = numero }; _db.PagosMovilWaEstados.Add(st); }
        mutar(st);
        st.ExpiraAt = DateTime.UtcNow.AddMinutes(15);
        st.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    private async Task LimpiarAsync(string numero)
    {
        var st = await _db.PagosMovilWaEstados.FirstOrDefaultAsync(x => x.Numero == numero);
        if (st is not null) { _db.PagosMovilWaEstados.Remove(st); await _db.SaveChangesAsync(); }
    }

    private static readonly HashSet<string> Cancelar = new(StringComparer.OrdinalIgnoreCase)
    { "cancelar", "cancela", "salir", "chau", "chao", "no", "basta", "fin", "terminar" };
    private static bool EsCancelar(string t) => Cancelar.Contains((t ?? "").Trim());

    // ─────────────── Envío / registro ───────────────

    private async Task ResponderAsync(string fromWaId, string numero, string texto, string? lineaId)
    {
        var sid = await _meta.SendTextAsync(fromWaId, texto, lineaPhoneId: lineaId);
        await RegistrarSalienteAsync(numero, texto, sid, lineaId);
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

    // ─────────────── Utilidades ───────────────

    private static bool TryParseMonto(string t, out decimal monto)
    {
        monto = 0m;
        if (string.IsNullOrWhiteSpace(t)) return false;
        // Quitar $, espacios y separadores de miles; aceptar coma o punto decimal.
        var limpio = t.Replace("$", "").Replace(" ", "").Trim();
        limpio = limpio.Replace(".", "").Replace(",", ".");
        return decimal.TryParse(limpio, NumberStyles.Number, CultureInfo.InvariantCulture, out monto);
    }

    private static string NombreMedio(string m) => m switch
    {
        "efectivo" => "Efectivo",
        "transferencia" => "Transferencia",
        "mp" => "Mercado Pago",
        "cheque" => "Cheque",
        _ => m
    };

    private static string Recortar(string s, int max) => string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..(max - 1)] + "…");

    private static string Money(decimal v)
        => "$" + v.ToString("#,##0", CultureInfo.InvariantCulture).Replace(",", ".");
}
