using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/nominas/empleados")]
[Authorize]
public class NomEmpleadosController : ControllerBase
{
    private readonly AppDbContext _db;

    public NomEmpleadosController(AppDbContext db) { _db = db; }

    private static NomEmpleadoDto Map(NomEmpleado e, int archivosCount = 0) => new(
        e.Id, e.Nombre, e.Documento, e.Puesto, e.FechaIngreso,
        e.SueldoBase, e.ValorHora, e.ComisionPorcentaje,
        e.ComisionPorKg, e.BonoFijo,
        e.ModalidadSueldo, e.JornalDiario,
        e.IsActive,
        e.FechaAlta, e.Banco, e.Cbu, e.Alias,
        e.Domicilio, e.TelefonoContacto, e.TelefonoFamiliar, e.Email,
        archivosCount,
        e.CreatedAt, e.UpdatedAt);

    private const long MaxArchivoBytes = 10 * 1024 * 1024; // 10 MB por archivo

    // 2026-06-08: normaliza la modalidad — solo permite "mensual" o "diario"
    private static string NormalizarModalidad(string? m)
    {
        var v = (m ?? "mensual").Trim().ToLowerInvariant();
        return v == "diario" ? "diario" : "mensual";
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        // 2026-06-25: sumamos los "apodos" — cómo aparece el mismo empleado en el kiosko de
        // fichaje y en el módulo de repartidores. Sirve para que el usuario vea que p.ej.
        // "Walter" es el mismo "NACHO" del kiosko, y no se confunda.
        var rows = await (
            from e in _db.NomEmpleados
            join f in _db.HorasExtrasEmpleados on e.Id equals f.NomEmpleadoId into fj
            from f in fj.DefaultIfEmpty()
            join r in _db.CafeRepartidores on e.Id equals r.NomEmpleadoId into rj
            from r in rj.DefaultIfEmpty()
            orderby e.Nombre
            select new { e, ApodoKiosko = f != null ? f.Nombre : null, ApodoRepartidor = r != null ? r.Nombre : null }
        ).ToListAsync();
        // 2026-07-31: contar los documentos personales adjuntos por empleado (sin traer el binario).
        var counts = await _db.NomEmpleadoArchivos
            .GroupBy(a => a.EmpleadoId)
            .Select(g => new { EmpleadoId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.EmpleadoId, x => x.Count);
        return Ok(rows.Select(x => Map(x.e, counts.TryGetValue(x.e.Id, out var c) ? c : 0)
            with { ApodoKiosko = x.ApodoKiosko, ApodoRepartidor = x.ApodoRepartidor }).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var e = await _db.NomEmpleados.FindAsync(id);
        if (e is null) return NotFound(new { error = "Empleado no encontrado" });
        var count = await _db.NomEmpleadoArchivos.CountAsync(a => a.EmpleadoId == id);
        return Ok(Map(e, count));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateNomEmpleadoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre))
            return BadRequest(new { error = "El nombre es obligatorio" });
        if (req.SueldoBase < 0 || req.ValorHora < 0)
            return BadRequest(new { error = "Sueldo base y valor hora no pueden ser negativos" });

        var e = new NomEmpleado
        {
            Nombre = req.Nombre.Trim(),
            Documento = string.IsNullOrWhiteSpace(req.Documento) ? null : req.Documento.Trim(),
            Puesto = string.IsNullOrWhiteSpace(req.Puesto) ? null : req.Puesto.Trim(),
            FechaIngreso = (req.FechaIngreso ?? DateTime.Today).Date,
            SueldoBase = req.SueldoBase,
            ValorHora = req.ValorHora,
            ComisionPorcentaje = req.ComisionPorcentaje,
            ComisionPorKg = Math.Max(0m, req.ComisionPorKg),
            BonoFijo = Math.Max(0m, req.BonoFijo),
            // 2026-06-08: modalidad de pago (mensual / diario) + jornal
            ModalidadSueldo = NormalizarModalidad(req.ModalidadSueldo),
            JornalDiario = Math.Max(0m, req.JornalDiario),
            IsActive = true,
            // 2026-07-31: datos personales / administrativos
            FechaAlta = req.FechaAlta?.Date,
            Banco = Limpiar(req.Banco),
            Cbu = Limpiar(req.Cbu),
            Alias = Limpiar(req.Alias),
            Domicilio = Limpiar(req.Domicilio),
            TelefonoContacto = Limpiar(req.TelefonoContacto),
            TelefonoFamiliar = Limpiar(req.TelefonoFamiliar),
            Email = Limpiar(req.Email),
            CreatedAt = DateTime.UtcNow
        };
        _db.NomEmpleados.Add(e);
        await _db.SaveChangesAsync();
        return Ok(Map(e));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateNomEmpleadoRequest req)
    {
        var e = await _db.NomEmpleados.FindAsync(id);
        if (e is null) return NotFound(new { error = "Empleado no encontrado" });

        if (req.Nombre is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "El nombre no puede ser vacio" });
            e.Nombre = req.Nombre.Trim();
        }
        if (req.Documento is not null) e.Documento = string.IsNullOrWhiteSpace(req.Documento) ? null : req.Documento.Trim();
        if (req.Puesto is not null) e.Puesto = string.IsNullOrWhiteSpace(req.Puesto) ? null : req.Puesto.Trim();
        if (req.FechaIngreso.HasValue) e.FechaIngreso = req.FechaIngreso.Value.Date;
        if (req.SueldoBase.HasValue)
        {
            if (req.SueldoBase.Value < 0) return BadRequest(new { error = "Sueldo base no puede ser negativo" });
            e.SueldoBase = req.SueldoBase.Value;
        }
        if (req.ValorHora.HasValue)
        {
            if (req.ValorHora.Value < 0) return BadRequest(new { error = "Valor hora no puede ser negativo" });
            e.ValorHora = req.ValorHora.Value;
        }
        if (req.ComisionPorcentaje.HasValue) e.ComisionPorcentaje = req.ComisionPorcentaje.Value;
        if (req.ComisionPorKg.HasValue) e.ComisionPorKg = Math.Max(0m, req.ComisionPorKg.Value);
        if (req.BonoFijo.HasValue) e.BonoFijo = Math.Max(0m, req.BonoFijo.Value);
        // 2026-06-08: modalidad + jornal
        if (req.ModalidadSueldo is not null) e.ModalidadSueldo = NormalizarModalidad(req.ModalidadSueldo);
        if (req.JornalDiario.HasValue) e.JornalDiario = Math.Max(0m, req.JornalDiario.Value);
        if (req.IsActive.HasValue) e.IsActive = req.IsActive.Value;
        // 2026-07-31: datos personales / administrativos. null = no tocar; texto vacío = borrar.
        if (req.FechaAlta.HasValue) e.FechaAlta = req.FechaAlta.Value.Date;
        if (req.Banco is not null) e.Banco = Limpiar(req.Banco);
        if (req.Cbu is not null) e.Cbu = Limpiar(req.Cbu);
        if (req.Alias is not null) e.Alias = Limpiar(req.Alias);
        if (req.Domicilio is not null) e.Domicilio = Limpiar(req.Domicilio);
        if (req.TelefonoContacto is not null) e.TelefonoContacto = Limpiar(req.TelefonoContacto);
        if (req.TelefonoFamiliar is not null) e.TelefonoFamiliar = Limpiar(req.TelefonoFamiliar);
        if (req.Email is not null) e.Email = Limpiar(req.Email);
        e.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        var count = await _db.NomEmpleadoArchivos.CountAsync(a => a.EmpleadoId == e.Id);
        return Ok(Map(e, count));
    }

    // Normaliza un texto opcional: recorta espacios y devuelve null si queda vacío.
    private static string? Limpiar(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ============================================================
    //  2026-07-31: DOCUMENTACIÓN PERSONAL adjunta por empleado.
    //  Varios por empleado (DNI, contrato, certificados...). Se guardan en la DB
    //  (varbinary) para que entren en los backups. Mismo patrón que los recibos de liquidación.
    // ============================================================

    [HttpGet("{id:int}/archivos")]
    public async Task<IActionResult> GetArchivos(int id)
    {
        if (!await _db.NomEmpleados.AnyAsync(e => e.Id == id))
            return NotFound(new { error = "Empleado no encontrado" });
        var archivos = await _db.NomEmpleadoArchivos
            .Where(a => a.EmpleadoId == id)
            .OrderByDescending(a => a.UploadedAt)
            .Select(a => new NomEmpleadoArchivoDto(a.Id, a.EmpleadoId, a.FileName, a.ContentType, a.FileSize, a.UploadedAt, a.UploadedBy))
            .ToListAsync();
        return Ok(archivos);
    }

    [HttpPost("{id:int}/archivos")]
    public async Task<IActionResult> UploadArchivo(int id, [FromBody] UploadNominaArchivoRequest req)
    {
        var emp = await _db.NomEmpleados.FindAsync(id);
        if (emp is null) return NotFound(new { error = "Empleado no encontrado" });
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

        var archivo = new NomEmpleadoArchivo
        {
            EmpleadoId = id,
            FileName = name.Length > 255 ? name.Substring(0, 255) : name,
            ContentType = string.IsNullOrWhiteSpace(ct) ? "application/octet-stream" : ct,
            FileSize = bytes.Length,
            Contenido = bytes,
            UploadedAt = DateTime.UtcNow,
            UploadedBy = User?.Identity?.Name
        };
        _db.NomEmpleadoArchivos.Add(archivo);
        await _db.SaveChangesAsync();
        return Ok(new NomEmpleadoArchivoDto(archivo.Id, archivo.EmpleadoId, archivo.FileName, archivo.ContentType, archivo.FileSize, archivo.UploadedAt, archivo.UploadedBy));
    }

    [HttpGet("{id:int}/archivos/{archivoId:int}/download")]
    public async Task<IActionResult> DownloadArchivo(int id, int archivoId)
    {
        var a = await _db.NomEmpleadoArchivos.FirstOrDefaultAsync(x => x.Id == archivoId && x.EmpleadoId == id);
        if (a is null) return NotFound(new { error = "Archivo no encontrado" });
        return File(a.Contenido, string.IsNullOrWhiteSpace(a.ContentType) ? "application/octet-stream" : a.ContentType, a.FileName);
    }

    [HttpDelete("{id:int}/archivos/{archivoId:int}")]
    public async Task<IActionResult> DeleteArchivo(int id, int archivoId)
    {
        var a = await _db.NomEmpleadoArchivos.FirstOrDefaultAsync(x => x.Id == archivoId && x.EmpleadoId == id);
        if (a is null) return NotFound(new { error = "Archivo no encontrado" });
        _db.NomEmpleadoArchivos.Remove(a);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var e = await _db.NomEmpleados.FindAsync(id);
        if (e is null) return NotFound(new { error = "Empleado no encontrado" });
        var tieneLiq = await _db.NomLiquidaciones.AnyAsync(l => l.EmpleadoId == id);
        if (tieneLiq)
            return BadRequest(new { error = "No se puede eliminar: el empleado tiene liquidaciones cargadas. Marcalo como inactivo en su lugar." });
        _db.NomEmpleados.Remove(e);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }
}
