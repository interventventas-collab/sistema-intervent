using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Saldos pendientes migrados del sistema viejo (Contabilium).
/// El usuario sube un Excel, los saldos entran como "pendiente" y los
/// asocia uno por uno con clientes de la base. Asociar crea una venta
/// tipo "X" no pagada con el monto del saldo como saldo de migracion.
/// </summary>
[ApiController]
[Route("api/cafe/saldos-migracion")]
[Authorize]
public class CafeSaldosMigracionController : ControllerBase
{
    private readonly AppDbContext _db;
    public CafeSaldosMigracionController(AppDbContext db) { _db = db; }

    public record SaldoDto(int Id, string RazonSocialOriginal, string? Tags, string? TipoDocumento,
        string? NroDocumento, string? CondicionIva, decimal Saldo, string Moneda, string Estado,
        int? ClienteId, string? ClienteNombre, int? VentaId, string? VentaNumero,
        string? Notas, DateTime FechaImport, DateTime CreatedAt);

    private SaldoDto Map(CafeSaldoMigracion s, CafeCliente? c = null, CafeVenta? v = null) =>
        new(s.Id, s.RazonSocialOriginal, s.Tags, s.TipoDocumento, s.NroDocumento, s.CondicionIva,
            s.Saldo, s.Moneda, s.Estado, s.ClienteId,
            c?.Nombre ?? s.Cliente?.Nombre,
            s.VentaId,
            v?.Numero ?? s.Venta?.Numero,
            s.Notas, s.FechaImport, s.CreatedAt);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? estado = null, [FromQuery] string? q = null)
    {
        var qry = _db.CafeSaldosMigracion
            .Include(s => s.Cliente)
            .Include(s => s.Venta)
            .AsQueryable();
        if (!string.IsNullOrWhiteSpace(estado) && estado != "todos")
            qry = qry.Where(s => s.Estado == estado);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim();
            qry = qry.Where(s => s.RazonSocialOriginal.Contains(t)
                || (s.NroDocumento != null && s.NroDocumento.Contains(t))
                || (s.Tags != null && s.Tags.Contains(t)));
        }
        var list = await qry.OrderByDescending(s => s.Saldo).ThenBy(s => s.Id).ToListAsync();
        return Ok(list.Select(s => Map(s)));
    }

    [HttpGet("stats")]
    public async Task<IActionResult> Stats()
    {
        var todos = await _db.CafeSaldosMigracion.ToListAsync();
        return Ok(new
        {
            total = todos.Count,
            pendientes = todos.Count(s => s.Estado == "pendiente"),
            asociados = todos.Count(s => s.Estado == "asociado"),
            ignorados = todos.Count(s => s.Estado == "ignorado"),
            saldoPendiente = todos.Where(s => s.Estado == "pendiente").Sum(s => s.Saldo),
            saldoAsociado = todos.Where(s => s.Estado == "asociado").Sum(s => s.Saldo),
            saldoTotal = todos.Sum(s => s.Saldo)
        });
    }

    public record ImportItem(string RazonSocialOriginal, string? Tags, string? TipoDocumento,
        string? NroDocumento, string? CondicionIva, decimal Saldo, string? Moneda);
    public record ImportRequest(List<ImportItem> Items, bool ReemplazarPendientes);

    /// <summary>
    /// Carga masiva de saldos. El frontend parsea el Excel y manda los items.
    /// Si ReemplazarPendientes=true, primero borra todos los pendientes anteriores.
    /// Nunca toca los asociados/ignorados (esos quedan como historial).
    /// </summary>
    [HttpPost("import")]
    public async Task<IActionResult> Import([FromBody] ImportRequest req)
    {
        if (req?.Items is null || req.Items.Count == 0)
            return BadRequest(new { error = "No hay items para importar" });

        if (req.ReemplazarPendientes)
        {
            var viejos = await _db.CafeSaldosMigracion.Where(s => s.Estado == "pendiente").ToListAsync();
            if (viejos.Count > 0) _db.CafeSaldosMigracion.RemoveRange(viejos);
        }

        int agregados = 0;
        foreach (var it in req.Items)
        {
            if (string.IsNullOrWhiteSpace(it.RazonSocialOriginal)) continue;
            _db.CafeSaldosMigracion.Add(new CafeSaldoMigracion
            {
                RazonSocialOriginal = it.RazonSocialOriginal.Trim(),
                Tags = it.Tags?.Trim(),
                TipoDocumento = it.TipoDocumento?.Trim().ToUpperInvariant(),
                NroDocumento = string.IsNullOrWhiteSpace(it.NroDocumento) ? null : it.NroDocumento.Trim(),
                CondicionIva = it.CondicionIva?.Trim().ToUpperInvariant(),
                Saldo = it.Saldo,
                Moneda = string.IsNullOrWhiteSpace(it.Moneda) ? "$" : it.Moneda.Trim(),
                Estado = "pendiente",
                FechaImport = DateTime.UtcNow.AddHours(-3).Date,
                CreatedAt = DateTime.UtcNow
            });
            agregados++;
        }
        await _db.SaveChangesAsync();
        return Ok(new { agregados });
    }

    public record AsociarRequest(int ClienteId, DateTime? FechaVenta, string? NotasInternas);

    /// <summary>
    /// Asocia el saldo con un cliente y CREA una venta tipo "X" no pagada con el monto
    /// como saldo de migracion. La venta queda enlazada y se ve en el dashboard de
    /// saldos del cliente y en su cuenta corriente.
    /// </summary>
    [HttpPost("{id:int}/asociar")]
    public async Task<IActionResult> Asociar(int id, [FromBody] AsociarRequest req)
    {
        var s = await _db.CafeSaldosMigracion.FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return NotFound(new { error = "Saldo no encontrado" });
        if (s.Estado == "asociado") return BadRequest(new { error = "El saldo ya está asociado" });

        var cli = await _db.CafeClientes.FirstOrDefaultAsync(c => c.Id == req.ClienteId);
        if (cli is null) return BadRequest(new { error = "Cliente no encontrado" });

        // Crear la venta tipo X (comprobante interno) con un único item de concepto libre.
        var fechaVenta = (req.FechaVenta ?? DateTime.UtcNow.AddHours(-3).Date).Date;
        var venta = new CafeVenta
        {
            Numero = await GenerarNumeroVentaAsync(),
            Fecha = fechaVenta,
            ClienteId = cli.Id,
            ClienteNombreSnapshot = cli.Nombre,
            ClienteTipoSnapshot = cli.Tipo,
            ClienteTelefonoSnapshot = cli.Telefono,
            ClienteRazonSocialSnapshot = cli.RazonSocial,
            ClienteDomicilioEntregaSnapshot = cli.DomicilioEntrega,
            ClienteComentariosComprobante = cli.ComentariosComprobante,
            ClienteCuitSnapshot = cli.Cuit,
            ClienteDireccionSnapshot = cli.Direccion,
            ClienteLocalidadSnapshot = cli.Localidad,
            ClienteCiudadSnapshot = cli.Ciudad,
            ClienteCpSnapshot = cli.Cp,
            Subtotal = s.Saldo,
            Descuento = 0m,
            Total = s.Saldo,
            CostoTotal = 0m,
            Margen = s.Saldo,
            Observaciones = $"Saldo migración del sistema anterior. Razón social original: {s.RazonSocialOriginal}"
                            + (string.IsNullOrWhiteSpace(req.NotasInternas) ? "" : $" · {req.NotasInternas}"),
            Estado = "emitido",
            IsPaid = false,
            TipoComprobante = "X",
            CondicionIva = s.CondicionIva ?? cli.CondicionIvaDefault ?? "CF",
            CondicionPago = "CTA_CORRIENTE",
            CreatedAt = DateTime.UtcNow
        };
        venta.Items.Add(new CafeVentaItem
        {
            ProductoId = null,
            EsConceptoLibre = true,
            ProductoNombreSnapshot = $"Saldo anterior al cambio de sistema ({s.FechaImport:dd/MM/yyyy})",
            Categoria = "LIBRE",
            Formato = "UNIT",
            Cantidad = 1,
            PrecioUnitario = s.Saldo,
            CostoUnitario = 0m,
            Subtotal = s.Saldo,
            GramosDescontados = 0m,
            EsDoyPack = false,
            DescuentoPct = 0m
        });
        _db.CafeVentas.Add(venta);
        await _db.SaveChangesAsync();

        s.Estado = "asociado";
        s.ClienteId = cli.Id;
        s.VentaId = venta.Id;
        if (!string.IsNullOrWhiteSpace(req.NotasInternas)) s.Notas = req.NotasInternas.Trim();
        s.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { ventaId = venta.Id, ventaNumero = venta.Numero, clienteId = cli.Id });
    }

    /// <summary>Marca un saldo como ignorado (descartado, no se va a cargar).</summary>
    [HttpPost("{id:int}/ignorar")]
    public async Task<IActionResult> Ignorar(int id, [FromBody] NotaSimple? req)
    {
        var s = await _db.CafeSaldosMigracion.FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return NotFound();
        s.Estado = "ignorado";
        if (req?.Notas is not null) s.Notas = req.Notas;
        s.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Vuelve un saldo a estado pendiente. Si estaba asociado, NO borra la venta creada.</summary>
    [HttpPost("{id:int}/reactivar")]
    public async Task<IActionResult> Reactivar(int id)
    {
        var s = await _db.CafeSaldosMigracion.FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return NotFound();
        s.Estado = "pendiente";
        s.ClienteId = null;
        s.VentaId = null;
        s.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    public record NotaSimple(string? Notas);

    /// <summary>Sugerencias de matching: clientes que comparten CUIT exacto o nombre similar.</summary>
    [HttpGet("{id:int}/sugerencias")]
    public async Task<IActionResult> Sugerencias(int id)
    {
        var s = await _db.CafeSaldosMigracion.FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return NotFound();

        var sugerencias = new List<object>();

        // 1) Match exacto por CUIT
        if (!string.IsNullOrWhiteSpace(s.NroDocumento))
        {
            var porCuit = await _db.CafeClientes
                .Where(c => c.IsActive && c.Cuit == s.NroDocumento)
                .Select(c => new { c.Id, c.Nombre, c.RazonSocial, c.Cuit, c.CodigoInterno, motivo = "cuit_exacto" })
                .ToListAsync();
            sugerencias.AddRange(porCuit);
        }

        // 2) Match por texto del nombre (palabras claves del original)
        if (!string.IsNullOrWhiteSpace(s.RazonSocialOriginal))
        {
            var palabras = s.RazonSocialOriginal
                .Split(new[] { ' ', ',', '/', '.', '-' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(p => p.Length >= 4)
                .Take(3)
                .ToList();
            foreach (var pal in palabras)
            {
                var ya = sugerencias.Select(o => ((dynamic)o).Id).ToHashSet();
                var matches = await _db.CafeClientes
                    .Where(c => c.IsActive && (c.Nombre.Contains(pal) || (c.RazonSocial != null && c.RazonSocial.Contains(pal))))
                    .Take(10)
                    .Select(c => new { c.Id, c.Nombre, c.RazonSocial, c.Cuit, c.CodigoInterno, motivo = $"nombre:{pal}" })
                    .ToListAsync();
                foreach (var m in matches)
                {
                    if (!ya.Contains(m.Id)) sugerencias.Add(m);
                }
                if (sugerencias.Count >= 10) break;
            }
        }
        return Ok(sugerencias.Take(15));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var s = await _db.CafeSaldosMigracion.FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return NotFound();
        _db.CafeSaldosMigracion.Remove(s);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    private async Task<string> GenerarNumeroVentaAsync()
    {
        var year = DateTime.UtcNow.Year;
        var prefix = $"CAFE-{year}-";
        var existing = await _db.CafeVentas
            .Where(v => v.Numero.StartsWith(prefix))
            .Select(v => v.Numero)
            .ToListAsync();
        int max = 0;
        foreach (var s in existing)
            if (int.TryParse(s.Substring(prefix.Length), out var n) && n > max) max = n;
        return $"{prefix}{(max + 1):D4}";
    }
}
