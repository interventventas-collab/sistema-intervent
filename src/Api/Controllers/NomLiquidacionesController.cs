using Api.Data;
using Api.DTOs;
using Api.Models;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/nominas")]
[Authorize]
public class NomLiquidacionesController : ControllerBase
{
    private readonly AppDbContext _db;
    private static readonly string[] EstadosValidos = { "pendiente", "pagado", "anulada" };

    public NomLiquidacionesController(AppDbContext db) { _db = db; }

    // ============================================================
    // LIQUIDACIONES
    // ============================================================

    [HttpGet("liquidaciones")]
    public async Task<IActionResult> GetAll([FromQuery] int? anio = null, [FromQuery] int? mes = null, [FromQuery] string? estado = null)
    {
        var q = _db.NomLiquidaciones
            .Include(l => l.EmpleadoNav)
            .Include(l => l.Pagos)
            .AsQueryable();
        if (anio.HasValue) q = q.Where(l => l.Anio == anio.Value);
        if (mes.HasValue) q = q.Where(l => l.Mes == mes.Value);
        if (!string.IsNullOrWhiteSpace(estado)) { var e = estado.Trim().ToLowerInvariant(); q = q.Where(l => l.Estado == e); }
        var list = await q.OrderByDescending(l => l.Anio).ThenByDescending(l => l.Mes).ThenBy(l => l.EmpleadoNav!.Nombre).ToListAsync();
        // 2026-07-01: contar archivos adjuntos por liquidación (sin traer el contenido binario).
        var ids = list.Select(l => l.Id).ToList();
        var counts = await _db.NomNominaArchivos
            .Where(a => ids.Contains(a.LiquidacionId))
            .GroupBy(a => a.LiquidacionId)
            .Select(g => new { LiquidacionId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.LiquidacionId, x => x.Count);
        return Ok(list.Select(l => Map(l, counts.TryGetValue(l.Id, out var c) ? c : 0)).ToList());
    }

    [HttpGet("liquidaciones/{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var l = await _db.NomLiquidaciones
            .Include(l => l.EmpleadoNav)
            .Include(l => l.Pagos)
            .FirstOrDefaultAsync(l => l.Id == id);
        if (l is null) return NotFound(new { error = "Liquidacion no encontrada" });
        return Ok(Map(l));
    }

    // ============================================================
    //  2026-07-01: ARCHIVOS ADJUNTOS (recibos / nóminas) por liquidación.
    //  Varios por liquidación. Se guardan en la DB (varbinary) para que entren en los backups.
    // ============================================================
    private const long MaxArchivoBytes = 10 * 1024 * 1024; // 10 MB por archivo

    [HttpGet("liquidaciones/{id:int}/archivos")]
    public async Task<IActionResult> GetArchivos(int id)
    {
        if (!await _db.NomLiquidaciones.AnyAsync(l => l.Id == id))
            return NotFound(new { error = "Liquidacion no encontrada" });
        var archivos = await _db.NomNominaArchivos
            .Where(a => a.LiquidacionId == id)
            .OrderByDescending(a => a.UploadedAt)
            .Select(a => new NomNominaArchivoDto(a.Id, a.LiquidacionId, a.FileName, a.ContentType, a.FileSize, a.UploadedAt, a.UploadedBy))
            .ToListAsync();
        return Ok(archivos);
    }

    [HttpPost("liquidaciones/{id:int}/archivos")]
    public async Task<IActionResult> UploadArchivo(int id, [FromBody] UploadNominaArchivoRequest req)
    {
        var liq = await _db.NomLiquidaciones.FindAsync(id);
        if (liq is null) return NotFound(new { error = "Liquidacion no encontrada" });
        if (string.IsNullOrWhiteSpace(req.Base64)) return BadRequest(new { error = "Archivo vacío" });

        byte[] bytes;
        try { bytes = Convert.FromBase64String(req.Base64); }
        catch { return BadRequest(new { error = "Archivo inválido" }); }
        if (bytes.Length == 0) return BadRequest(new { error = "Archivo vacío" });
        if (bytes.Length > MaxArchivoBytes) return BadRequest(new { error = "El archivo es muy grande (máximo 10 MB)" });

        var ct = (req.ContentType ?? "").Trim().ToLowerInvariant();
        var name = string.IsNullOrWhiteSpace(req.FileName) ? "archivo" : System.IO.Path.GetFileName(req.FileName.Trim());
        var ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
        var okType = ct is "application/pdf" or "image/jpeg" or "image/png" or "image/webp"
                  || ext is ".pdf" or ".jpg" or ".jpeg" or ".png" or ".webp";
        if (!okType) return BadRequest(new { error = "Solo se permiten PDF o imágenes (JPG, PNG)" });

        var archivo = new NomNominaArchivo
        {
            LiquidacionId = id,
            FileName = name.Length > 255 ? name.Substring(0, 255) : name,
            ContentType = string.IsNullOrWhiteSpace(ct) ? "application/octet-stream" : ct,
            FileSize = bytes.Length,
            Contenido = bytes,
            UploadedAt = DateTime.UtcNow,
            UploadedBy = User?.Identity?.Name
        };
        _db.NomNominaArchivos.Add(archivo);
        await _db.SaveChangesAsync();
        return Ok(new NomNominaArchivoDto(archivo.Id, archivo.LiquidacionId, archivo.FileName, archivo.ContentType, archivo.FileSize, archivo.UploadedAt, archivo.UploadedBy));
    }

    [HttpGet("liquidaciones/{id:int}/archivos/{archivoId:int}/download")]
    public async Task<IActionResult> DownloadArchivo(int id, int archivoId)
    {
        var a = await _db.NomNominaArchivos.FirstOrDefaultAsync(x => x.Id == archivoId && x.LiquidacionId == id);
        if (a is null) return NotFound(new { error = "Archivo no encontrado" });
        return File(a.Contenido, string.IsNullOrWhiteSpace(a.ContentType) ? "application/octet-stream" : a.ContentType, a.FileName);
    }

    [HttpDelete("liquidaciones/{id:int}/archivos/{archivoId:int}")]
    public async Task<IActionResult> DeleteArchivo(int id, int archivoId)
    {
        var a = await _db.NomNominaArchivos.FirstOrDefaultAsync(x => x.Id == archivoId && x.LiquidacionId == id);
        if (a is null) return NotFound(new { error = "Archivo no encontrado" });
        _db.NomNominaArchivos.Remove(a);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    [HttpPost("liquidaciones")]
    public async Task<IActionResult> Create([FromBody] CreateNomLiquidacionRequest req)
    {
        var emp = await _db.NomEmpleados.FindAsync(req.EmpleadoId);
        if (emp is null) return BadRequest(new { error = "Empleado no encontrado" });
        if (req.Anio < 2000 || req.Anio > 2100) return BadRequest(new { error = "Año invalido" });
        if (req.Mes < 1 || req.Mes > 12) return BadRequest(new { error = "Mes invalido (1-12)" });

        // Una sola liquidacion por (empleado, año, mes)
        var existe = await _db.NomLiquidaciones.AnyAsync(l => l.EmpleadoId == req.EmpleadoId && l.Anio == req.Anio && l.Mes == req.Mes);
        if (existe) return BadRequest(new { error = $"Ya existe una liquidacion de {emp.Nombre} para {req.Mes:00}/{req.Anio}" });

        var liq = new NomLiquidacion
        {
            EmpleadoId = req.EmpleadoId,
            Anio = req.Anio,
            Mes = req.Mes,
            HorasTrabajadas = Math.Max(0m, req.HorasTrabajadas),
            HorasExtra = Math.Max(0m, req.HorasExtra),
            RecargoHsExtraPct = req.RecargoHsExtraPct ?? 0m,
            DiasAusencia = Math.Max(0m, req.DiasAusencia),
            DiasVacaciones = Math.Max(0m, req.DiasVacaciones),
            KgCafe = Math.Max(0m, req.KgCafe),
            DiasTrabajados = Math.Max(0m, req.DiasTrabajados),  // 2026-06-08
            Bonos = Math.Max(0m, req.Bonos),
            Aguinaldo = Math.Max(0m, req.Aguinaldo),
            Adelantos = Math.Max(0m, req.Adelantos),
            OtrosDescuentos = Math.Max(0m, req.OtrosDescuentos),
            Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim(),
            Estado = "pendiente",
            CreatedAt = DateTime.UtcNow
        };
        Calcular(liq, emp, esCreacion: true);
        _db.NomLiquidaciones.Add(liq);
        await _db.SaveChangesAsync();

        var saved = await _db.NomLiquidaciones
            .Include(l => l.EmpleadoNav)
            .Include(l => l.Pagos)
            .FirstAsync(l => l.Id == liq.Id);
        return Ok(Map(saved));
    }

    [HttpPut("liquidaciones/{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateNomLiquidacionRequest req)
    {
        var liq = await _db.NomLiquidaciones.Include(l => l.Pagos).FirstOrDefaultAsync(l => l.Id == id);
        if (liq is null) return NotFound(new { error = "Liquidacion no encontrada" });
        var emp = await _db.NomEmpleados.FindAsync(liq.EmpleadoId);
        if (emp is null) return BadRequest(new { error = "Empleado no encontrado" });

        if (req.HorasTrabajadas.HasValue) liq.HorasTrabajadas = Math.Max(0m, req.HorasTrabajadas.Value);
        if (req.HorasExtra.HasValue) liq.HorasExtra = Math.Max(0m, req.HorasExtra.Value);
        if (req.RecargoHsExtraPct.HasValue) liq.RecargoHsExtraPct = Math.Max(0m, req.RecargoHsExtraPct.Value);
        if (req.DiasAusencia.HasValue) liq.DiasAusencia = Math.Max(0m, req.DiasAusencia.Value);
        if (req.DiasVacaciones.HasValue) liq.DiasVacaciones = Math.Max(0m, req.DiasVacaciones.Value);
        if (req.KgCafe.HasValue) liq.KgCafe = Math.Max(0m, req.KgCafe.Value);
        if (req.DiasTrabajados.HasValue) liq.DiasTrabajados = Math.Max(0m, req.DiasTrabajados.Value);  // 2026-06-08
        if (req.Bonos.HasValue) liq.Bonos = Math.Max(0m, req.Bonos.Value);
        if (req.Aguinaldo.HasValue) liq.Aguinaldo = Math.Max(0m, req.Aguinaldo.Value);
        if (req.Adelantos.HasValue) liq.Adelantos = Math.Max(0m, req.Adelantos.Value);
        if (req.OtrosDescuentos.HasValue) liq.OtrosDescuentos = Math.Max(0m, req.OtrosDescuentos.Value);
        // 2026-07-01: el sueldo base de un empleado MENSUAL se puede editar por liquidación (queda
        // congelado ahí). Para diarios no aplica (surge de días × jornal).
        var empEsDiario = string.Equals(emp.ModalidadSueldo, "diario", StringComparison.OrdinalIgnoreCase);
        if (req.SueldoBase.HasValue && !empEsDiario) liq.SueldoBase = Math.Max(0m, req.SueldoBase.Value);
        if (req.Notas is not null) liq.Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim();
        if (req.Estado is not null)
        {
            var ne = req.Estado.Trim().ToLowerInvariant();
            if (!EstadosValidos.Contains(ne)) return BadRequest(new { error = $"Estado invalido. Validos: {string.Join(", ", EstadosValidos)}" });
            liq.Estado = ne;
        }

        Calcular(liq, emp, esCreacion: false);
        liq.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var saved = await _db.NomLiquidaciones
            .Include(l => l.EmpleadoNav)
            .Include(l => l.Pagos)
            .FirstAsync(l => l.Id == liq.Id);
        return Ok(Map(saved));
    }

    [HttpDelete("liquidaciones/{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var liq = await _db.NomLiquidaciones.Include(l => l.Pagos).FirstOrDefaultAsync(l => l.Id == id);
        if (liq is null) return NotFound(new { error = "Liquidacion no encontrada" });
        if (liq.Pagos.Any())
            return BadRequest(new { error = "No se puede eliminar: ya tiene pagos asociados. Borrá los pagos primero o cambiala a 'anulada'." });
        _db.NomLiquidaciones.Remove(liq);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    // ============================================================
    // PAGOS
    // ============================================================

    [HttpPost("pagos")]
    public async Task<IActionResult> CreatePago([FromBody] CreateNomPagoRequest req)
    {
        var liq = await _db.NomLiquidaciones.Include(l => l.Pagos).FirstOrDefaultAsync(l => l.Id == req.LiquidacionId);
        if (liq is null) return BadRequest(new { error = "Liquidacion no encontrada" });
        if (liq.Estado == "anulada") return BadRequest(new { error = "No se puede pagar una liquidacion anulada" });
        if (req.Monto <= 0) return BadRequest(new { error = "El monto debe ser mayor a 0" });
        if (string.IsNullOrWhiteSpace(req.Metodo)) return BadRequest(new { error = "Indicá un metodo de pago" });

        var pagado = liq.Pagos.Sum(p => p.Monto);
        var saldo = liq.NetoAPagar - pagado;
        if (req.Monto > saldo + 0.01m)
            return BadRequest(new { error = $"El monto excede el saldo pendiente (${saldo:N2})" });

        // Concepto: validamos contra la lista permitida; si viene vacio o invalido, default a "sueldo".
        var conceptosValidos = new HashSet<string> { "sueldo", "comision_cafe", "horas_extra", "bono", "adelanto", "aguinaldo", "otro" };
        var concepto = (req.Concepto ?? "sueldo").Trim().ToLowerInvariant();
        if (!conceptosValidos.Contains(concepto)) concepto = "otro";

        var pago = new NomPago
        {
            LiquidacionId = req.LiquidacionId,
            FechaPago = (req.FechaPago ?? DateTime.Today).Date,
            Metodo = req.Metodo.Trim().ToLowerInvariant(),
            Monto = req.Monto,
            Concepto = concepto,
            Detalle = string.IsNullOrWhiteSpace(req.Detalle) ? null : req.Detalle.Trim(),
            Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim(),
            CreatedAt = DateTime.UtcNow
        };
        _db.NomPagos.Add(pago);

        // Si con este pago se cancela la liquidacion → estado pagado
        if (pagado + req.Monto >= liq.NetoAPagar - 0.01m)
        {
            liq.Estado = "pagado";
            liq.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();

        var saved = await _db.NomLiquidaciones
            .Include(l => l.EmpleadoNav)
            .Include(l => l.Pagos)
            .FirstAsync(l => l.Id == liq.Id);
        return Ok(Map(saved));
    }

    /// <summary>Edita un pago existente. Requiere clave de seguridad (la misma global que
    /// se usa para borrar comprobantes: sales.delete_password con sales.delete_allowed_operator).
    /// Si cambia el monto y eso re-completa la liquidacion, el estado se ajusta a 'pagado';
    /// si la deja con saldo, vuelve a 'pendiente'.</summary>
    [HttpPut("pagos/{id:int}")]
    public async Task<IActionResult> UpdatePago(int id, [FromBody] UpdateNomPagoRequest req)
    {
        if (req is null) return BadRequest(new { error = "Body vacio" });

        // ── Validar clave (reuso el helper de Cafe Ventas para mantener la misma password global) ──
        var allowedOp = (await _db.AppSettings.FindAsync("sales.delete_allowed_operator"))?.Value ?? "OSMAR";
        var expectedPassword = (await _db.AppSettings.FindAsync("sales.delete_password"))?.Value ?? "";
        if (!string.Equals(req.Operator ?? "", allowedOp, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = $"Solo {allowedOp} puede editar pagos." });
        if (string.IsNullOrEmpty(expectedPassword) || req.Password != expectedPassword)
            return BadRequest(new { error = "Clave incorrecta." });

        var pago = await _db.NomPagos.FindAsync(id);
        if (pago is null) return NotFound(new { error = "Pago no encontrado" });
        var liq = await _db.NomLiquidaciones.Include(l => l.Pagos).FirstOrDefaultAsync(l => l.Id == pago.LiquidacionId);
        if (liq is null) return BadRequest(new { error = "Liquidacion del pago no encontrada" });
        if (liq.Estado == "anulada") return BadRequest(new { error = "No se puede editar un pago de una liquidacion anulada" });

        // ── Aplicar cambios (solo los campos que vienen seteados) ──
        if (req.FechaPago.HasValue) pago.FechaPago = req.FechaPago.Value.Date;
        if (!string.IsNullOrWhiteSpace(req.Metodo)) pago.Metodo = req.Metodo.Trim().ToLowerInvariant();
        if (req.Monto.HasValue)
        {
            if (req.Monto.Value <= 0) return BadRequest(new { error = "El monto debe ser mayor a 0" });
            // Validar que el nuevo monto no exceda el total a pagar. Suma de todos los otros pagos + este nuevo monto.
            var otrosPagos = liq.Pagos.Where(p => p.Id != pago.Id).Sum(p => p.Monto);
            if (otrosPagos + req.Monto.Value > liq.NetoAPagar + 0.01m)
                return BadRequest(new { error = $"El monto excede el total a pagar (${liq.NetoAPagar - otrosPagos:N2} restante)" });
            pago.Monto = req.Monto.Value;
        }
        if (!string.IsNullOrWhiteSpace(req.Concepto))
        {
            var conceptosValidos = new HashSet<string> { "sueldo", "comision_cafe", "horas_extra", "bono", "adelanto", "aguinaldo", "otro" };
            var concepto = req.Concepto.Trim().ToLowerInvariant();
            pago.Concepto = conceptosValidos.Contains(concepto) ? concepto : "otro";
        }
        if (req.Detalle is not null) pago.Detalle = string.IsNullOrWhiteSpace(req.Detalle) ? null : req.Detalle.Trim();
        if (req.Notas is not null) pago.Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim();

        // ── Recalcular estado de la liquidacion segun el total pagado ──
        var totalPagado = liq.Pagos.Sum(p => p.Monto);
        if (totalPagado >= liq.NetoAPagar - 0.01m && liq.Estado != "anulada")
        {
            liq.Estado = "pagado";
        }
        else if (liq.Estado == "pagado")
        {
            liq.Estado = "pendiente";
        }
        liq.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var saved = await _db.NomLiquidaciones
            .Include(l => l.EmpleadoNav)
            .Include(l => l.Pagos)
            .FirstAsync(l => l.Id == liq.Id);
        return Ok(Map(saved));
    }

    [HttpDelete("pagos/{id:int}")]
    public async Task<IActionResult> DeletePago(int id)
    {
        var pago = await _db.NomPagos.FindAsync(id);
        if (pago is null) return NotFound(new { error = "Pago no encontrado" });
        var liq = await _db.NomLiquidaciones.FindAsync(pago.LiquidacionId);
        _db.NomPagos.Remove(pago);
        // Si estaba pagada y ahora queda saldo → vuelve a pendiente
        if (liq is not null && liq.Estado == "pagado")
        {
            liq.Estado = "pendiente";
            liq.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    // ============================================================
    // REPORTES
    // ============================================================

    [HttpGet("resumen")]
    public async Task<IActionResult> Resumen([FromQuery] int anio, [FromQuery] int mes)
    {
        if (anio < 2000 || mes < 1 || mes > 12) return BadRequest(new { error = "Año/mes invalido" });
        var liqs = await _db.NomLiquidaciones
            .Include(l => l.Pagos)
            .Where(l => l.Anio == anio && l.Mes == mes && l.Estado != "anulada")
            .ToListAsync();
        var totalPagado = liqs.Sum(l => l.Pagos.Sum(p => p.Monto));
        return Ok(new NomResumenMensualDto(
            anio, mes,
            liqs.Count,
            liqs.Sum(l => l.TotalGanado),
            liqs.Sum(l => l.TotalDescuentos),
            liqs.Sum(l => l.NetoAPagar),
            totalPagado,
            liqs.Sum(l => l.NetoAPagar) - totalPagado));
    }

    // ============================================================
    // CALCULO
    // ============================================================

    /// <summary>
    /// Calcula los totales de la liquidacion segun los insumos cargados y los datos del empleado.
    /// Reglas:
    ///  - Sueldo base = empleado.SueldoBase
    ///  - Hora extra con recargo = ValorHora * (1 + RecargoHsExtraPct/100)
    ///  - Monto hs extra = HorasExtra * HoraExtraConRecargo
    ///  - Descuento por dia de ausencia = DiasAusencia * (SueldoBase / 30)
    ///  - TOTAL GANADO = Base + MontoHsExtra + Comision + Bonos + Aguinaldo
    ///  - TOTAL DESCUENTOS = DescuentoFaltas + Adelantos + OtrosDescuentos
    ///  - NETO = Ganado - Descuentos
    /// </summary>
    /// <summary>2026-07-01: 'esCreacion' congela el sueldo. En la CREACIÓN se toma el sueldo de la
    /// ficha (snapshot). En un UPDATE NO se vuelve a leer la ficha — el SueldoBase queda congelado
    /// (o el que el usuario editó en esa liquidación). Así, cambiar el sueldo en la ficha NO afecta
    /// meses ya cargados: cada liquidación conserva el importe de cuando se cargó.</summary>
    private static void Calcular(NomLiquidacion liq, NomEmpleado emp, bool esCreacion)
    {
        // 2026-06-08: si el empleado es modalidad "diario", el sueldo base se calcula
        // como DiasTrabajados × JornalDiario (en vez de tomar el SueldoBase mensual fijo).
        var esDiario = string.Equals(emp.ModalidadSueldo, "diario", StringComparison.OrdinalIgnoreCase);
        if (esDiario)
        {
            // Diario: siempre días × jornal (el importe surge de los días trabajados de ese mes).
            liq.SueldoBase = Math.Round(liq.DiasTrabajados * emp.JornalDiario, 2, MidpointRounding.AwayFromZero);
        }
        else if (esCreacion)
        {
            // Mensual, primera carga: snapshot del sueldo de la ficha en ese momento.
            liq.SueldoBase = emp.SueldoBase;
        }
        // Mensual + UPDATE: NO se toca liq.SueldoBase — queda congelado / lo que el usuario editó.
        var hsExtraConRecargo = emp.ValorHora * (1m + liq.RecargoHsExtraPct / 100m);
        liq.MontoHsExtra = Math.Round(liq.HorasExtra * hsExtraConRecargo, 2, MidpointRounding.AwayFromZero);
        // Comision auto-calc: kg de café del mes × tarifa por kg del empleado.
        liq.Comision = Math.Round(liq.KgCafe * emp.ComisionPorKg, 2, MidpointRounding.AwayFromZero);
        // Para empleados diarios, el descuento por ausencia ya está implícito (si faltó un día,
        // no se cuenta en DiasTrabajados). Solo se aplica DescuentoFaltas a mensuales.
        // Usa el SueldoBase CONGELADO de la liquidación (no el de la ficha) para no descuadrar meses viejos.
        var diaProporcional = esDiario ? 0m : liq.SueldoBase / 30m;
        liq.DescuentoFaltas = Math.Round(liq.DiasAusencia * diaProporcional, 2, MidpointRounding.AwayFromZero);
        liq.TotalGanado = liq.SueldoBase + liq.MontoHsExtra + liq.Comision + liq.Bonos + liq.Aguinaldo;
        liq.TotalDescuentos = liq.DescuentoFaltas + liq.Adelantos + liq.OtrosDescuentos;
        liq.NetoAPagar = Math.Round(liq.TotalGanado - liq.TotalDescuentos, 2, MidpointRounding.AwayFromZero);
    }

    private static NomLiquidacionDto Map(NomLiquidacion l, int archivosCount = 0)
    {
        var totalPagado = l.Pagos.Sum(p => p.Monto);
        // 2026-06-08: incluir DiasTrabajados + datos del empleado (modalidad / jornal)
        // para que el frontend muestre la UI correcta (Sueldo base vs Días trabajados).
        return new NomLiquidacionDto(
            l.Id, l.EmpleadoId, l.EmpleadoNav?.Nombre ?? "—", l.EmpleadoNav?.Puesto,
            l.Anio, l.Mes,
            l.HorasTrabajadas, l.HorasExtra, l.RecargoHsExtraPct,
            l.DiasAusencia, l.DiasVacaciones,
            l.KgCafe, l.DiasTrabajados,
            l.SueldoBase, l.MontoHsExtra, l.Comision, l.Bonos,
            l.Aguinaldo,
            l.DescuentoFaltas, l.Adelantos, l.OtrosDescuentos,
            l.TotalGanado, l.TotalDescuentos, l.NetoAPagar,
            l.Estado, l.Notas,
            totalPagado, l.NetoAPagar - totalPagado,
            l.EmpleadoNav?.ModalidadSueldo ?? "mensual", l.EmpleadoNav?.JornalDiario ?? 0m,
            l.CreatedAt, l.UpdatedAt,
            l.Pagos.OrderByDescending(p => p.FechaPago).Select(p => new NomPagoDto(
                p.Id, p.LiquidacionId, p.FechaPago, p.Metodo, p.Monto,
                p.Concepto, p.Detalle,
                p.Notas, p.CreatedAt)).ToList(),
            archivosCount);
    }

    // ============================================================
    //  PANEL DEUDAS — vista simplificada para operador no tecnico
    //  Muestra empleados con saldo pendiente, agrupado por concepto.
    //  Pedido del usuario 2026-05-19: 'un enlace bien intuitivo que un
    //  niño pueda manejar' para ver a quien le debo + pagar con clave.
    // ============================================================

    public class DashboardPagarRequest
    {
        public int LiquidacionId { get; set; }
        public string Concepto { get; set; } = "sueldo";
        public decimal Monto { get; set; }
        public string Metodo { get; set; } = "efectivo";
        public DateTime? FechaPago { get; set; }
        public string? Detalle { get; set; }
        public string? Notas { get; set; }
        public string? Operator { get; set; }
        public string? Password { get; set; }
    }

    /// <summary>Registrar un pago desde el panel de deudas. Decisión del usuario 2026-05-20:
    /// NO pide clave ni operador específico — cualquier usuario logueado puede registrar el pago,
    /// igual que desde /nominas/liquidaciones. Si más adelante hay que volver a poner clave, se
    /// agrega acá la validación.</summary>
    [HttpPost("dashboard/pagar")]
    public async Task<IActionResult> DashboardPagar([FromBody] DashboardPagarRequest req)
    {
        if (req is null) return BadRequest(new { error = "Body vacio" });

        // Reusa la logica del CreatePago existente
        var liq = await _db.NomLiquidaciones.Include(l => l.Pagos).FirstOrDefaultAsync(l => l.Id == req.LiquidacionId);
        if (liq is null) return BadRequest(new { error = "Liquidacion no encontrada" });
        if (liq.Estado == "anulada") return BadRequest(new { error = "No se puede pagar una liquidacion anulada" });
        if (req.Monto <= 0) return BadRequest(new { error = "El monto debe ser mayor a 0" });
        if (string.IsNullOrWhiteSpace(req.Metodo)) return BadRequest(new { error = "Indicá un metodo de pago" });

        var pagado = liq.Pagos.Sum(p => p.Monto);
        var saldo = liq.NetoAPagar - pagado;
        if (req.Monto > saldo + 0.01m)
            return BadRequest(new { error = $"El monto excede el saldo pendiente (${saldo:N2})" });

        var conceptosValidos = new HashSet<string> { "sueldo", "comision_cafe", "horas_extra", "bono", "adelanto", "aguinaldo", "otro" };
        var concepto = (req.Concepto ?? "sueldo").Trim().ToLowerInvariant();
        if (!conceptosValidos.Contains(concepto)) concepto = "otro";

        var pago = new NomPago
        {
            LiquidacionId = req.LiquidacionId,
            FechaPago = (req.FechaPago ?? DateTime.UtcNow.AddHours(-3)).Date,
            Metodo = req.Metodo.Trim().ToLowerInvariant(),
            Monto = req.Monto,
            Concepto = concepto,
            Detalle = string.IsNullOrWhiteSpace(req.Detalle) ? null : req.Detalle.Trim(),
            Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim(),
            CreatedAt = DateTime.UtcNow
        };
        _db.NomPagos.Add(pago);
        if (pagado + req.Monto >= liq.NetoAPagar - 0.01m)
        {
            liq.Estado = "pagado";
            liq.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();

        return Ok(new { ok = true });
    }

    public record DashboardConceptoDto(string Concepto, decimal Presupuestado, decimal Pagado, decimal Pendiente);
    public record DashboardLiquidacionDto(
        int LiquidacionId, int Anio, int Mes,
        decimal NetoAPagar, decimal TotalPagado, decimal Saldo,
        DateTime FechaVencimiento, int DiasParaVencer,
        List<DashboardConceptoDto> Conceptos);
    public record DashboardEmpleadoDto(
        int EmpleadoId, string Nombre,
        decimal TotalDebe, bool TieneVencido, int DiasParaVencerMasUrgente,
        List<DashboardLiquidacionDto> Liquidaciones);
    public record DashboardDeudasDto(
        decimal TotalAPagar, decimal TotalVencido,
        int CantidadConDeuda, int CantidadVencidos,
        List<DashboardEmpleadoDto> Empleados);

    // ──── Token publico del panel ────
    // Guardado en AppSettings con key 'nominas.panel.public_token'. Si no existe se genera al
    // pedirlo. Sirve para que el usuario comparta una URL /panel-pagos/{token} que cualquiera
    // con el link puede abrir sin loguearse (pero igual necesita la clave global para pagar).
    private const string PanelTokenSettingKey = "nominas.panel.public_token";

    private async Task<string> GetOrCreatePanelTokenAsync()
    {
        var existing = await _db.AppSettings.FindAsync(PanelTokenSettingKey);
        if (existing is not null && !string.IsNullOrEmpty(existing.Value)) return existing.Value;
        var token = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .Replace("/", "_").Replace("+", "-").TrimEnd('=');
        if (existing is null)
        {
            _db.AppSettings.Add(new AppSetting { Key = PanelTokenSettingKey, Value = token });
        }
        else
        {
            existing.Value = token;
        }
        await _db.SaveChangesAsync();
        return token;
    }

    /// <summary>Devuelve el token publico actual (no genera uno nuevo). El frontend lo usa
    /// para armar la URL compartible. Si el token no existe, lo crea.</summary>
    [HttpGet("dashboard/public-token")]
    public async Task<IActionResult> GetPanelPublicToken()
    {
        var token = await GetOrCreatePanelTokenAsync();
        return Ok(new { token });
    }

    /// <summary>Regenera el token publico (invalida el anterior). Por si el operador
    /// sospecha que el link anterior se filtro y quiere generar uno nuevo.</summary>
    [HttpPost("dashboard/public-token/regenerate")]
    public async Task<IActionResult> RegeneratePanelPublicToken()
    {
        var existing = await _db.AppSettings.FindAsync(PanelTokenSettingKey);
        var nuevo = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .Replace("/", "_").Replace("+", "-").TrimEnd('=');
        if (existing is null)
            _db.AppSettings.Add(new AppSetting { Key = PanelTokenSettingKey, Value = nuevo });
        else
            existing.Value = nuevo;
        await _db.SaveChangesAsync();
        return Ok(new { token = nuevo });
    }

    /// <summary>Variante publica de GetDashboardDeudas — accesible sin login pero requiere
    /// matchear el token en la URL contra el guardado en AppSettings.</summary>
    [HttpGet("dashboard/publica/{token}/deudas")]
    [AllowAnonymous]
    public async Task<IActionResult> GetDashboardDeudasPublic(string token)
    {
        if (!await ValidatePanelTokenAsync(token)) return NotFound();
        return await GetDashboardDeudas();
    }

    /// <summary>Variante publica de DashboardPagar — accesible sin login + token en URL,
    /// pero igual valida operador + clave en el body (sigue siendo necesaria la clave
    /// global para pagar — el token solo da acceso a la VISTA).</summary>
    [HttpPost("dashboard/publica/{token}/pagar")]
    [AllowAnonymous]
    public async Task<IActionResult> DashboardPagarPublic(string token, [FromBody] DashboardPagarRequest req)
    {
        if (!await ValidatePanelTokenAsync(token)) return NotFound();
        return await DashboardPagar(req);
    }

    private async Task<bool> ValidatePanelTokenAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var saved = await _db.AppSettings.FindAsync(PanelTokenSettingKey);
        return saved is not null && !string.IsNullOrEmpty(saved.Value) && saved.Value == token;
    }

    [HttpGet("dashboard/deudas")]
    public async Task<IActionResult> GetDashboardDeudas()
    {
        // Fecha "hoy" en hora Argentina (UTC-3) para calcular vencimientos.
        var hoyArg = DateTime.UtcNow.AddHours(-3).Date;

        // Solo liquidaciones activas (no anuladas) con saldo > 0
        var liqs = await _db.NomLiquidaciones
            .Include(l => l.EmpleadoNav)
            .Include(l => l.Pagos)
            .Where(l => l.Estado != "anulada")
            .ToListAsync();

        // Helper: presupuesto por concepto en la liquidacion (mismo mapping que el frontend).
        static decimal PresupuestoConcepto(string concepto, NomLiquidacion l) => concepto switch
        {
            "sueldo" => l.SueldoBase,
            "horas_extra" => l.MontoHsExtra,
            "comision_cafe" => l.Comision,
            "bono" => l.Bonos,
            "aguinaldo" => l.Aguinaldo,
            _ => 0m
        };

        // Solo los 5 conceptos "positivos" (los que se le pagan al empleado).
        // Adelantos y OtrosDescuentos no van porque son descuentos (no se le paga eso).
        var conceptosPositivos = new[] { "sueldo", "horas_extra", "comision_cafe", "bono", "aguinaldo" };

        var empleadosDict = new Dictionary<int, DashboardEmpleadoDto>();
        var liqsConSaldo = new List<DashboardLiquidacionDto>();

        foreach (var l in liqs)
        {
            var pagado = l.Pagos.Sum(p => p.Monto);
            var saldo = l.NetoAPagar - pagado;
            if (saldo <= 0.01m) continue;

            // Vencimiento = ultimo dia del mes de la liquidacion
            var fechaVenc = new DateTime(l.Anio, l.Mes, DateTime.DaysInMonth(l.Anio, l.Mes));
            var diasParaVencer = (fechaVenc - hoyArg).Days;

            // Conceptos pendientes — solo los que tienen pendiente > 0
            var conceptos = new List<DashboardConceptoDto>();
            foreach (var c in conceptosPositivos)
            {
                var presup = PresupuestoConcepto(c, l);
                if (presup <= 0m) continue;
                var pagadoConcepto = l.Pagos.Where(p => p.Concepto == c).Sum(p => p.Monto);
                var pendiente = presup - pagadoConcepto;
                if (pendiente > 0.01m)
                    conceptos.Add(new DashboardConceptoDto(c, presup, pagadoConcepto, pendiente));
            }

            var liqDto = new DashboardLiquidacionDto(
                l.Id, l.Anio, l.Mes,
                l.NetoAPagar, pagado, saldo,
                fechaVenc, diasParaVencer,
                conceptos);

            if (!empleadosDict.TryGetValue(l.EmpleadoId, out var emp))
            {
                emp = new DashboardEmpleadoDto(
                    l.EmpleadoId,
                    l.EmpleadoNav?.Nombre ?? $"Empleado {l.EmpleadoId}",
                    0m, false, int.MaxValue,
                    new List<DashboardLiquidacionDto>());
                empleadosDict[l.EmpleadoId] = emp;
            }
            emp.Liquidaciones.Add(liqDto);
            // Actualizar agregados — uso un record con propiedades inmutables, asi que lo reconstruyo
            empleadosDict[l.EmpleadoId] = emp with
            {
                TotalDebe = emp.TotalDebe + saldo,
                TieneVencido = emp.TieneVencido || diasParaVencer < 0,
                DiasParaVencerMasUrgente = Math.Min(emp.DiasParaVencerMasUrgente, diasParaVencer)
            };
            liqsConSaldo.Add(liqDto);
        }

        // Ordenar liquidaciones de cada empleado por fecha (mas urgentes primero)
        // y ordenar empleados (vencidos primero, despues por mas urgente)
        foreach (var key in empleadosDict.Keys.ToList())
        {
            var e = empleadosDict[key];
            var ordenadas = e.Liquidaciones.OrderBy(x => x.DiasParaVencer).ToList();
            empleadosDict[key] = e with { Liquidaciones = ordenadas };
        }

        var empleadosOrdenados = empleadosDict.Values
            .OrderBy(e => e.TieneVencido ? 0 : 1)
            .ThenBy(e => e.DiasParaVencerMasUrgente)
            .ThenBy(e => e.Nombre)
            .ToList();

        var result = new DashboardDeudasDto(
            TotalAPagar: liqsConSaldo.Sum(l => l.Saldo),
            TotalVencido: liqsConSaldo.Where(l => l.DiasParaVencer < 0).Sum(l => l.Saldo),
            CantidadConDeuda: empleadosOrdenados.Count,
            CantidadVencidos: empleadosOrdenados.Count(e => e.TieneVencido),
            Empleados: empleadosOrdenados);
        return Ok(result);
    }

    // ============================================================
    //  EXPORT EXCEL — varias hojas (Resumen / Pendientes / Pagos /
    //  Por empleado / Por mes). Filtros por rango de meses + empleado.
    // ============================================================

    public class ExportLiquidacionesRequest
    {
        /// <summary>Año-Mes inicial inclusive (formato YYYYMM, ej 202604). Null = sin limite inferior.</summary>
        public int? DesdeYYYYMM { get; set; }
        /// <summary>Año-Mes final inclusive (formato YYYYMM, ej 202605). Null = sin limite superior.</summary>
        public int? HastaYYYYMM { get; set; }
        /// <summary>Lista de ids de empleados. Vacia o null = todos.</summary>
        public List<int>? EmpleadoIds { get; set; }
        /// <summary>Si true, solo incluye liquidaciones con saldo pendiente > 0. Anuladas se excluyen siempre.</summary>
        public bool SoloPendientes { get; set; }
    }

    [HttpPost("liquidaciones/export")]
    public async Task<IActionResult> ExportExcel([FromBody] ExportLiquidacionesRequest req)
    {
        req ??= new ExportLiquidacionesRequest();

        // 1) Armar query con filtros
        var q = _db.NomLiquidaciones
            .Include(l => l.EmpleadoNav)
            .Include(l => l.Pagos)
            .Where(l => l.Estado != "anulada")
            .AsQueryable();

        if (req.DesdeYYYYMM.HasValue)
        {
            var d = req.DesdeYYYYMM.Value;
            int dAnio = d / 100, dMes = d % 100;
            q = q.Where(l => l.Anio > dAnio || (l.Anio == dAnio && l.Mes >= dMes));
        }
        if (req.HastaYYYYMM.HasValue)
        {
            var h = req.HastaYYYYMM.Value;
            int hAnio = h / 100, hMes = h % 100;
            q = q.Where(l => l.Anio < hAnio || (l.Anio == hAnio && l.Mes <= hMes));
        }
        if (req.EmpleadoIds is not null && req.EmpleadoIds.Count > 0)
            q = q.Where(l => req.EmpleadoIds.Contains(l.EmpleadoId));

        var liqs = await q.OrderBy(l => l.Anio).ThenBy(l => l.Mes)
            .ThenBy(l => l.EmpleadoNav!.Nombre).ToListAsync();

        if (req.SoloPendientes)
        {
            liqs = liqs.Where(l => l.NetoAPagar - l.Pagos.Sum(p => p.Monto) > 0.01m).ToList();
        }

        // 2) Generar XLSX con 5 hojas
        using var wb = new XLWorkbook();
        var culture = new System.Globalization.CultureInfo("es-AR");

        // ── Hoja 1: RESUMEN (1 fila por liquidacion, todos los conceptos) ──
        var ws1 = wb.Worksheets.Add("Resumen");
        var headers1 = new[] {
            "Empleado", "Año", "Mes", "Sueldo Base", "Hs Extra (monto)", "Comisión", "Bonos",
            "Aguinaldo", "Adelantos", "Desc. Faltas", "Otros Desc.",
            "Total Ganado", "Total Descuentos", "Neto a Pagar", "Total Pagado", "Saldo", "Estado"
        };
        for (int i = 0; i < headers1.Length; i++)
        {
            var c = ws1.Cell(1, i + 1);
            c.Value = headers1[i];
            c.Style.Font.Bold = true;
            c.Style.Fill.BackgroundColor = XLColor.LightGray;
        }
        int row1 = 2;
        decimal totGanado = 0, totPagado = 0, totSaldo = 0;
        foreach (var l in liqs)
        {
            var pagado = l.Pagos.Sum(p => p.Monto);
            var saldo = l.NetoAPagar - pagado;
            ws1.Cell(row1, 1).Value = l.EmpleadoNav?.Nombre ?? $"Empleado {l.EmpleadoId}";
            ws1.Cell(row1, 2).Value = l.Anio;
            ws1.Cell(row1, 3).Value = l.Mes;
            ws1.Cell(row1, 4).Value = l.SueldoBase;
            ws1.Cell(row1, 5).Value = l.MontoHsExtra;
            ws1.Cell(row1, 6).Value = l.Comision;
            ws1.Cell(row1, 7).Value = l.Bonos;
            ws1.Cell(row1, 8).Value = l.Aguinaldo;
            ws1.Cell(row1, 9).Value = l.Adelantos;
            ws1.Cell(row1, 10).Value = l.DescuentoFaltas;
            ws1.Cell(row1, 11).Value = l.OtrosDescuentos;
            ws1.Cell(row1, 12).Value = l.TotalGanado;
            ws1.Cell(row1, 13).Value = l.TotalDescuentos;
            ws1.Cell(row1, 14).Value = l.NetoAPagar;
            ws1.Cell(row1, 15).Value = pagado;
            ws1.Cell(row1, 16).Value = saldo;
            ws1.Cell(row1, 17).Value = l.Estado;
            for (int col = 4; col <= 16; col++)
                ws1.Cell(row1, col).Style.NumberFormat.Format = "#,##0.00";
            totGanado += l.TotalGanado;
            totPagado += pagado;
            totSaldo += saldo;
            row1++;
        }
        // Fila de totales en amarillo
        if (liqs.Count > 0)
        {
            ws1.Cell(row1, 1).Value = "TOTAL";
            ws1.Cell(row1, 12).Value = totGanado;
            ws1.Cell(row1, 14).Value = liqs.Sum(l => l.NetoAPagar);
            ws1.Cell(row1, 15).Value = totPagado;
            ws1.Cell(row1, 16).Value = totSaldo;
            for (int col = 1; col <= 17; col++)
            {
                ws1.Cell(row1, col).Style.Font.Bold = true;
                ws1.Cell(row1, col).Style.Fill.BackgroundColor = XLColor.LightYellow;
                if (col >= 4 && col <= 16) ws1.Cell(row1, col).Style.NumberFormat.Format = "#,##0.00";
            }
        }
        ws1.Columns().AdjustToContents();

        // ── Hoja 2: PENDIENTES (solo las que tienen saldo) ──
        var ws2 = wb.Worksheets.Add("Pendientes");
        var headers2 = new[] { "Empleado", "Año", "Mes", "Neto a Pagar", "Total Pagado", "Saldo Pendiente", "Estado" };
        for (int i = 0; i < headers2.Length; i++)
        {
            var c = ws2.Cell(1, i + 1);
            c.Value = headers2[i];
            c.Style.Font.Bold = true;
            c.Style.Fill.BackgroundColor = XLColor.LightGray;
        }
        int row2 = 2;
        decimal totalSaldoPendiente = 0;
        foreach (var l in liqs)
        {
            var pagado = l.Pagos.Sum(p => p.Monto);
            var saldo = l.NetoAPagar - pagado;
            if (saldo <= 0.01m) continue;
            ws2.Cell(row2, 1).Value = l.EmpleadoNav?.Nombre ?? $"Empleado {l.EmpleadoId}";
            ws2.Cell(row2, 2).Value = l.Anio;
            ws2.Cell(row2, 3).Value = l.Mes;
            ws2.Cell(row2, 4).Value = l.NetoAPagar;
            ws2.Cell(row2, 5).Value = pagado;
            ws2.Cell(row2, 6).Value = saldo;
            ws2.Cell(row2, 7).Value = l.Estado;
            for (int col = 4; col <= 6; col++) ws2.Cell(row2, col).Style.NumberFormat.Format = "#,##0.00";
            totalSaldoPendiente += saldo;
            row2++;
        }
        if (row2 > 2)
        {
            ws2.Cell(row2, 1).Value = "TOTAL PENDIENTE";
            ws2.Cell(row2, 6).Value = totalSaldoPendiente;
            for (int col = 1; col <= 7; col++)
            {
                ws2.Cell(row2, col).Style.Font.Bold = true;
                ws2.Cell(row2, col).Style.Fill.BackgroundColor = XLColor.LightYellow;
            }
            ws2.Cell(row2, 6).Style.NumberFormat.Format = "#,##0.00";
        }
        ws2.Columns().AdjustToContents();

        // ── Hoja 3: PAGOS detallados (1 fila por pago) ──
        var ws3 = wb.Worksheets.Add("Pagos detallados");
        var headers3 = new[] { "Empleado", "Año Liq.", "Mes Liq.", "Fecha pago", "Concepto", "Método", "Monto", "Detalle", "Notas" };
        for (int i = 0; i < headers3.Length; i++)
        {
            var c = ws3.Cell(1, i + 1);
            c.Value = headers3[i];
            c.Style.Font.Bold = true;
            c.Style.Fill.BackgroundColor = XLColor.LightGray;
        }
        int row3 = 2;
        decimal totPagosDetalle = 0;
        var pagosOrdenados = liqs.SelectMany(l => l.Pagos.Select(p => new { Liq = l, Pago = p }))
            .OrderBy(x => x.Pago.FechaPago).ThenBy(x => x.Liq.EmpleadoNav?.Nombre);
        foreach (var x in pagosOrdenados)
        {
            ws3.Cell(row3, 1).Value = x.Liq.EmpleadoNav?.Nombre ?? $"Empleado {x.Liq.EmpleadoId}";
            ws3.Cell(row3, 2).Value = x.Liq.Anio;
            ws3.Cell(row3, 3).Value = x.Liq.Mes;
            ws3.Cell(row3, 4).Value = x.Pago.FechaPago.ToString("dd/MM/yyyy");
            ws3.Cell(row3, 5).Value = x.Pago.Concepto;
            ws3.Cell(row3, 6).Value = x.Pago.Metodo;
            ws3.Cell(row3, 7).Value = x.Pago.Monto;
            ws3.Cell(row3, 7).Style.NumberFormat.Format = "#,##0.00";
            ws3.Cell(row3, 8).Value = x.Pago.Detalle ?? "";
            ws3.Cell(row3, 9).Value = x.Pago.Notas ?? "";
            totPagosDetalle += x.Pago.Monto;
            row3++;
        }
        if (row3 > 2)
        {
            ws3.Cell(row3, 1).Value = "TOTAL PAGADO";
            ws3.Cell(row3, 7).Value = totPagosDetalle;
            for (int col = 1; col <= 9; col++)
            {
                ws3.Cell(row3, col).Style.Font.Bold = true;
                ws3.Cell(row3, col).Style.Fill.BackgroundColor = XLColor.LightYellow;
            }
            ws3.Cell(row3, 7).Style.NumberFormat.Format = "#,##0.00";
        }
        ws3.Columns().AdjustToContents();

        // ── Hoja 4: POR EMPLEADO (totalizado a través de los meses filtrados) ──
        var ws4 = wb.Worksheets.Add("Por empleado");
        var headers4 = new[] { "Empleado", "Cantidad liq.", "Total Ganado", "Total Pagado", "Saldo Pendiente" };
        for (int i = 0; i < headers4.Length; i++)
        {
            var c = ws4.Cell(1, i + 1);
            c.Value = headers4[i];
            c.Style.Font.Bold = true;
            c.Style.Fill.BackgroundColor = XLColor.LightGray;
        }
        int row4 = 2;
        var porEmpleado = liqs.GroupBy(l => new { l.EmpleadoId, Nombre = l.EmpleadoNav?.Nombre ?? $"Empleado {l.EmpleadoId}" })
            .Select(g => new
            {
                Nombre = g.Key.Nombre,
                CantLiq = g.Count(),
                Ganado = g.Sum(l => l.NetoAPagar),
                Pagado = g.Sum(l => l.Pagos.Sum(p => p.Monto)),
                Saldo = g.Sum(l => l.NetoAPagar - l.Pagos.Sum(p => p.Monto))
            })
            .OrderBy(x => x.Nombre)
            .ToList();
        foreach (var pe in porEmpleado)
        {
            ws4.Cell(row4, 1).Value = pe.Nombre;
            ws4.Cell(row4, 2).Value = pe.CantLiq;
            ws4.Cell(row4, 3).Value = pe.Ganado;
            ws4.Cell(row4, 4).Value = pe.Pagado;
            ws4.Cell(row4, 5).Value = pe.Saldo;
            for (int col = 3; col <= 5; col++) ws4.Cell(row4, col).Style.NumberFormat.Format = "#,##0.00";
            row4++;
        }
        if (porEmpleado.Count > 0)
        {
            ws4.Cell(row4, 1).Value = "TOTAL";
            ws4.Cell(row4, 2).Value = porEmpleado.Sum(x => x.CantLiq);
            ws4.Cell(row4, 3).Value = porEmpleado.Sum(x => x.Ganado);
            ws4.Cell(row4, 4).Value = porEmpleado.Sum(x => x.Pagado);
            ws4.Cell(row4, 5).Value = porEmpleado.Sum(x => x.Saldo);
            for (int col = 1; col <= 5; col++)
            {
                ws4.Cell(row4, col).Style.Font.Bold = true;
                ws4.Cell(row4, col).Style.Fill.BackgroundColor = XLColor.LightYellow;
                if (col >= 3 && col <= 5) ws4.Cell(row4, col).Style.NumberFormat.Format = "#,##0.00";
            }
        }
        ws4.Columns().AdjustToContents();

        // ── Hoja 5: POR MES (totalizado por año-mes) ──
        var ws5 = wb.Worksheets.Add("Por mes");
        var headers5 = new[] { "Año", "Mes", "Cant. empleados", "Total Ganado", "Total Pagado", "Saldo Pendiente" };
        for (int i = 0; i < headers5.Length; i++)
        {
            var c = ws5.Cell(1, i + 1);
            c.Value = headers5[i];
            c.Style.Font.Bold = true;
            c.Style.Fill.BackgroundColor = XLColor.LightGray;
        }
        int row5 = 2;
        var porMes = liqs.GroupBy(l => new { l.Anio, l.Mes })
            .Select(g => new
            {
                g.Key.Anio,
                g.Key.Mes,
                CantEmp = g.Select(l => l.EmpleadoId).Distinct().Count(),
                Ganado = g.Sum(l => l.NetoAPagar),
                Pagado = g.Sum(l => l.Pagos.Sum(p => p.Monto)),
                Saldo = g.Sum(l => l.NetoAPagar - l.Pagos.Sum(p => p.Monto))
            })
            .OrderBy(x => x.Anio).ThenBy(x => x.Mes)
            .ToList();
        foreach (var pm in porMes)
        {
            ws5.Cell(row5, 1).Value = pm.Anio;
            ws5.Cell(row5, 2).Value = pm.Mes;
            ws5.Cell(row5, 3).Value = pm.CantEmp;
            ws5.Cell(row5, 4).Value = pm.Ganado;
            ws5.Cell(row5, 5).Value = pm.Pagado;
            ws5.Cell(row5, 6).Value = pm.Saldo;
            for (int col = 4; col <= 6; col++) ws5.Cell(row5, col).Style.NumberFormat.Format = "#,##0.00";
            row5++;
        }
        if (porMes.Count > 0)
        {
            ws5.Cell(row5, 1).Value = "TOTAL";
            ws5.Cell(row5, 4).Value = porMes.Sum(x => x.Ganado);
            ws5.Cell(row5, 5).Value = porMes.Sum(x => x.Pagado);
            ws5.Cell(row5, 6).Value = porMes.Sum(x => x.Saldo);
            for (int col = 1; col <= 6; col++)
            {
                ws5.Cell(row5, col).Style.Font.Bold = true;
                ws5.Cell(row5, col).Style.Fill.BackgroundColor = XLColor.LightYellow;
                if (col >= 4 && col <= 6) ws5.Cell(row5, col).Style.NumberFormat.Format = "#,##0.00";
            }
        }
        ws5.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var bytes = ms.ToArray();
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"liquidaciones-{DateTime.Now:yyyyMMdd-HHmm}.xlsx");
    }
}
