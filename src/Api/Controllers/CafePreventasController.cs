using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Preventas (notas de pedido) — vendedor en la calle (Gaby) carga desde un link publico
/// en el celular: cliente + productos + foto + notas. NO descuenta stock NI factura.
/// Osmar (admin) las ve en su panel y las convierte a venta real desde ahi.
/// </summary>
[ApiController]
[Route("api/preventas")]
public class CafePreventasController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;
    public CafePreventasController(AppDbContext db, IWebHostEnvironment env) { _db = db; _env = env; }

    // ============================================================
    // DTOs
    // ============================================================

    public record PubItemDto(int Id, int? ProductoId, string? ProductoNombre, string? DescripcionLibre,
        decimal Cantidad, decimal? PrecioSugerido, string? Observaciones);
    public record PubPreventaDto(int Id, string Numero, DateTime Fecha,
        int? ClienteId, string? ClienteNombreLibre, string? ClienteNombreCatalogo, string? ClienteTelefono,
        string? Notas, string? FotoUrl, string Estado, DateTime CreatedAt,
        List<PubItemDto> Items, int TotalItems);

    public record PubClienteDto(int Id, string Nombre, string? Tipo, string? Telefono, string? Direccion, string? Localidad);
    public record PubProductoDto(int Id, string? Sku, string Nombre, string Categoria, string? Marca, decimal? PrecioOtro, decimal? PrecioBar);
    public record PubInitDto(int VendedorId, string Nombre);

    // ============================================================
    // ENDPOINTS PUBLICOS (sin auth, token del vendedor)
    // ============================================================

    private async Task<CafePreventaVendedor?> ResolverVendedorAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        return await _db.CafePreventaVendedores.FirstOrDefaultAsync(v => v.Token == token && v.IsActive);
    }

    [HttpGet("publica/{token}/init")]
    [AllowAnonymous]
    public async Task<IActionResult> GetInit(string token)
    {
        var vend = await ResolverVendedorAsync(token);
        if (vend is null) return NotFound(new { error = "Link inválido o vendedor inactivo" });
        return Ok(new PubInitDto(vend.Id, vend.Nombre));
    }

    /// <summary>Busca clientes por nombre / razón social / teléfono / dirección. Devuelve hasta 25.</summary>
    [HttpGet("publica/{token}/clientes")]
    [AllowAnonymous]
    public async Task<IActionResult> BuscarClientes(string token, [FromQuery] string q = "")
    {
        var vend = await ResolverVendedorAsync(token);
        if (vend is null) return NotFound();
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2) return Ok(new List<PubClienteDto>());
        var term = q.Trim();
        var clientes = await _db.CafeClientes
            .Where(c => c.IsActive)
            .Where(c => c.Nombre!.Contains(term)
                     || (c.RazonSocial != null && c.RazonSocial.Contains(term))
                     || (c.Telefono != null && c.Telefono.Contains(term))
                     || (c.Direccion != null && c.Direccion.Contains(term))
                     || (c.Localidad != null && c.Localidad.Contains(term)))
            .OrderBy(c => c.CodigoInterno.HasValue ? 0 : 1)
            .ThenBy(c => c.Nombre)
            .Take(25)
            .Select(c => new PubClienteDto(c.Id, c.Nombre ?? "?", c.Tipo, c.Telefono, c.Direccion, c.Localidad))
            .ToListAsync();
        return Ok(clientes);
    }

    /// <summary>Busca productos por nombre / SKU / marca. Devuelve hasta 25.</summary>
    [HttpGet("publica/{token}/productos")]
    [AllowAnonymous]
    public async Task<IActionResult> BuscarProductos(string token, [FromQuery] string q = "")
    {
        var vend = await ResolverVendedorAsync(token);
        if (vend is null) return NotFound();
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2) return Ok(new List<PubProductoDto>());
        var term = q.Trim().ToUpperInvariant();
        var prods = await _db.CafeProductos
            .Where(p => p.IsActive)
            .Where(p =>
                (p.Sku != null && p.Sku.ToUpper().Contains(term))
                || p.Nombre.ToUpper().Contains(term)
                || (p.Marca != null && p.Marca.ToUpper().Contains(term))
                || (p.Barcode != null && p.Barcode.Contains(term)))
            .OrderBy(p => p.Sku)
            .Take(25)
            .Select(p => new PubProductoDto(p.Id, p.Sku, p.Nombre, p.Categoria, p.Marca, p.PrecioOtro, p.PrecioBar))
            .ToListAsync();
        return Ok(prods);
    }

    /// <summary>Top productos que el cliente compró históricamente — para sugerir al vendedor.</summary>
    [HttpGet("publica/{token}/cliente/{clienteId:int}/top-productos")]
    [AllowAnonymous]
    public async Task<IActionResult> TopProductosCliente(string token, int clienteId, [FromQuery] int count = 10)
    {
        var vend = await ResolverVendedorAsync(token);
        if (vend is null) return NotFound();
        count = Math.Clamp(count, 1, 30);

        // Reutilizamos la misma lógica que CafeVentas: items de ventas NO anuladas del cliente
        var grouped = await _db.CafeVentaItems
            .Where(i => i.VentaNav != null
                     && i.VentaNav.ClienteId == clienteId
                     && i.VentaNav.Estado != "anulado"
                     && i.ProductoId != null)
            .GroupBy(i => i.ProductoId!.Value)
            .Select(g => new
            {
                ProductoId = g.Key,
                Veces = g.Select(x => x.VentaId).Distinct().Count(),
                CantTotal = g.Sum(x => x.Cantidad)
            })
            .OrderByDescending(x => x.Veces)
            .ThenByDescending(x => x.CantTotal)
            .Take(count)
            .ToListAsync();

        if (grouped.Count == 0) return Ok(new List<PubProductoDto>());

        var ids = grouped.Select(x => x.ProductoId).ToList();
        var prods = await _db.CafeProductos
            .Where(p => ids.Contains(p.Id) && p.IsActive)
            .ToListAsync();

        // Preservar el orden de "más comprados primero"
        var result = grouped
            .Select(g => prods.FirstOrDefault(p => p.Id == g.ProductoId))
            .Where(p => p is not null)
            .Select(p => new PubProductoDto(p!.Id, p.Sku, p.Nombre, p.Categoria, p.Marca, p.PrecioOtro, p.PrecioBar))
            .ToList();
        return Ok(result);
    }

    public class CrearItemRequest
    {
        public int? ProductoId { get; set; }
        public string? DescripcionLibre { get; set; }
        public decimal Cantidad { get; set; } = 1m;
        public decimal? PrecioSugerido { get; set; }
        public string? Observaciones { get; set; }
    }

    public class CrearPreventaRequest
    {
        public int? ClienteId { get; set; }
        public string? ClienteNombreLibre { get; set; }
        public string? ClienteTelefono { get; set; }
        public string? Notas { get; set; }
        public List<CrearItemRequest> Items { get; set; } = new();
    }

    [HttpPost("publica/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> Crear(string token, [FromBody] CrearPreventaRequest req)
    {
        var vend = await ResolverVendedorAsync(token);
        if (vend is null) return NotFound(new { error = "Link inválido" });

        // Validaciones básicas
        var tieneCliente = req.ClienteId.HasValue || !string.IsNullOrWhiteSpace(req.ClienteNombreLibre);
        if (!tieneCliente) return BadRequest(new { error = "Tenés que cargar un cliente (escrito o elegido)" });
        if (req.Items is null || req.Items.Count == 0) return BadRequest(new { error = "Tenés que cargar al menos 1 producto" });

        // Resolver cliente del catálogo si vino id
        CafeCliente? cli = null;
        if (req.ClienteId.HasValue && req.ClienteId.Value > 0)
            cli = await _db.CafeClientes.FindAsync(req.ClienteId.Value);

        var hoy = DateTime.UtcNow.AddHours(-3).Date;
        var pv = new CafePreventa
        {
            Numero = await ProximoNumeroAsync(),
            Fecha = hoy,
            VendedorId = vend.Id,
            VendedorNombreSnap = vend.Nombre,
            ClienteId = cli?.Id,
            ClienteNombreLibre = cli is null ? (req.ClienteNombreLibre?.Trim()) : null,
            ClienteTelefono = string.IsNullOrWhiteSpace(req.ClienteTelefono) ? null : req.ClienteTelefono.Trim(),
            Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim(),
            Estado = "pendiente",
            CreatedAt = DateTime.UtcNow
        };

        int orden = 0;
        foreach (var it in req.Items)
        {
            // Resolver producto si vino id
            CafeProducto? prod = null;
            if (it.ProductoId.HasValue && it.ProductoId.Value > 0)
                prod = await _db.CafeProductos.FindAsync(it.ProductoId.Value);

            var desc = string.IsNullOrWhiteSpace(it.DescripcionLibre) ? null : it.DescripcionLibre.Trim();
            if (prod is null && string.IsNullOrEmpty(desc)) continue; // item vacío, skip

            pv.Items.Add(new CafePreventaItem
            {
                ProductoId = prod?.Id,
                ProductoNombreSnap = prod?.Nombre,
                DescripcionLibre = desc,
                Cantidad = it.Cantidad > 0 ? it.Cantidad : 1m,
                PrecioSugerido = it.PrecioSugerido,
                Observaciones = string.IsNullOrWhiteSpace(it.Observaciones) ? null : it.Observaciones.Trim(),
                Orden = orden++
            });
        }

        if (pv.Items.Count == 0) return BadRequest(new { error = "Ningún item válido" });

        _db.CafePreventas.Add(pv);
        await _db.SaveChangesAsync();
        return Ok(new { id = pv.Id, numero = pv.Numero });
    }

    /// <summary>Genera "PRE-YYYY-NNNN" buscando el último número del año actual.</summary>
    private async Task<string> ProximoNumeroAsync()
    {
        var year = DateTime.UtcNow.AddHours(-3).Year;
        var prefix = $"PRE-{year}-";
        var existentes = await _db.CafePreventas
            .Where(p => p.Numero.StartsWith(prefix))
            .Select(p => p.Numero)
            .ToListAsync();
        int max = 0;
        foreach (var s in existentes)
        {
            if (int.TryParse(s.Substring(prefix.Length), out var n) && n > max) max = n;
        }
        return $"{prefix}{(max + 1):D4}";
    }

    /// <summary>Sube una foto y la asocia a una preventa. Solo permite imagen y máximo 8 MB.</summary>
    [HttpPost("publica/{token}/{id:int}/foto")]
    [AllowAnonymous]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<IActionResult> SubirFoto(string token, int id, IFormFile file)
    {
        var vend = await ResolverVendedorAsync(token);
        if (vend is null) return NotFound();
        var pv = await _db.CafePreventas.FirstOrDefaultAsync(p => p.Id == id && p.VendedorId == vend.Id);
        if (pv is null) return NotFound(new { error = "Preventa no encontrada" });
        if (file is null || file.Length == 0) return BadRequest(new { error = "No se recibió archivo" });
        if (file.Length > 8 * 1024 * 1024) return BadRequest(new { error = "Imagen demasiado grande (máx 8 MB)" });
        if (!file.ContentType.StartsWith("image/")) return BadRequest(new { error = "Solo se aceptan imágenes" });

        var dir = Path.Combine("/data", "preventas-fotos");
        Directory.CreateDirectory(dir);
        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrEmpty(ext) || ext.Length > 6) ext = ".jpg";
        var filename = $"pv-{pv.Id}-{Guid.NewGuid():N}{ext}";
        var fullPath = Path.Combine(dir, filename);
        using (var fs = new FileStream(fullPath, FileMode.Create))
            await file.CopyToAsync(fs);

        pv.FotoPath = filename;
        pv.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, foto = filename });
    }

    /// <summary>Devuelve la imagen de una preventa (público, requiere token del vendedor o admin).</summary>
    [HttpGet("foto/{filename}")]
    [AllowAnonymous]
    public IActionResult GetFoto(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename) || filename.Contains("..") || filename.Contains("/"))
            return NotFound();
        var path = Path.Combine("/data", "preventas-fotos", filename);
        if (!System.IO.File.Exists(path)) return NotFound();
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        var contentType = ext switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };
        return PhysicalFile(path, contentType);
    }

    [HttpGet("publica/{token}/mis-pedidos")]
    [AllowAnonymous]
    public async Task<IActionResult> MisPedidos(string token, [FromQuery] int limit = 30)
    {
        var vend = await ResolverVendedorAsync(token);
        if (vend is null) return NotFound();
        limit = Math.Clamp(limit, 1, 100);

        var pvs = await _db.CafePreventas
            .Include(p => p.Items)
            .Include(p => p.ClienteNav)
            .Where(p => p.VendedorId == vend.Id)
            .OrderByDescending(p => p.CreatedAt)
            .Take(limit)
            .ToListAsync();

        var result = pvs.Select(p => MapPub(p)).ToList();
        return Ok(result);
    }

    private static PubPreventaDto MapPub(CafePreventa p) => new(
        p.Id, p.Numero, p.Fecha,
        p.ClienteId, p.ClienteNombreLibre, p.ClienteNav?.Nombre, p.ClienteTelefono,
        p.Notas,
        string.IsNullOrEmpty(p.FotoPath) ? null : $"/api/preventas/foto/{p.FotoPath}",
        p.Estado, p.CreatedAt,
        p.Items.OrderBy(i => i.Orden).Select(i => new PubItemDto(
            i.Id, i.ProductoId, i.ProductoNombreSnap, i.DescripcionLibre,
            i.Cantidad, i.PrecioSugerido, i.Observaciones)).ToList(),
        p.Items.Count);

    // ============================================================
    // ENDPOINTS ADMIN
    // ============================================================

    public record AdminPreventaListDto(int Id, string Numero, DateTime Fecha,
        string VendedorNombre, string ClienteNombre, int TotalItems,
        string? Notas, string? FotoUrl, string Estado, int? VentaIdFinal, DateTime CreatedAt);

    [HttpGet("admin")]
    [Authorize]
    public async Task<IActionResult> ListAdmin([FromQuery] string estado = "pendiente", [FromQuery] int limit = 200)
    {
        limit = Math.Clamp(limit, 1, 1000);
        var q = _db.CafePreventas
            .Include(p => p.Items)
            .Include(p => p.ClienteNav)
            .Include(p => p.VendedorNav)
            .AsQueryable();
        if (estado != "TODOS" && !string.IsNullOrWhiteSpace(estado))
            q = q.Where(p => p.Estado == estado);
        var pvs = await q.OrderByDescending(p => p.CreatedAt).Take(limit).ToListAsync();
        var result = pvs.Select(p => new AdminPreventaListDto(
            p.Id, p.Numero, p.Fecha,
            p.VendedorNombreSnap ?? p.VendedorNav?.Nombre ?? "?",
            p.ClienteNav?.Nombre ?? p.ClienteNombreLibre ?? "?",
            p.Items.Count, p.Notas,
            string.IsNullOrEmpty(p.FotoPath) ? null : $"/api/preventas/foto/{p.FotoPath}",
            p.Estado, p.VentaIdFinal, p.CreatedAt
        )).ToList();
        return Ok(result);
    }

    [HttpGet("admin/{id:int}")]
    [Authorize]
    public async Task<IActionResult> GetAdmin(int id)
    {
        var p = await _db.CafePreventas
            .Include(x => x.Items).ThenInclude(i => i.ProductoNav)
            .Include(x => x.ClienteNav)
            .Include(x => x.VendedorNav)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        return Ok(MapPub(p));
    }

    [HttpGet("admin/pendientes-count")]
    [Authorize]
    public async Task<IActionResult> PendientesCount()
    {
        var c = await _db.CafePreventas.CountAsync(p => p.Estado == "pendiente");
        return Ok(new { count = c });
    }

    [HttpPost("admin/{id:int}/marcar-procesada")]
    [Authorize]
    public async Task<IActionResult> MarcarProcesada(int id)
    {
        var p = await _db.CafePreventas.FindAsync(id);
        if (p is null) return NotFound();
        p.Estado = "procesada";
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    [HttpPost("admin/{id:int}/cancelar")]
    [Authorize]
    public async Task<IActionResult> Cancelar(int id)
    {
        var p = await _db.CafePreventas.FindAsync(id);
        if (p is null) return NotFound();
        p.Estado = "cancelada";
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    [HttpPost("admin/{id:int}/reabrir")]
    [Authorize]
    public async Task<IActionResult> Reabrir(int id)
    {
        var p = await _db.CafePreventas.FindAsync(id);
        if (p is null) return NotFound();
        p.Estado = "pendiente";
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Vincula esta preventa a una venta ya creada (cuando Osmar convirtió desde el frontend).</summary>
    public class VincularRequest { public int VentaId { get; set; } }

    [HttpPost("admin/{id:int}/vincular-venta")]
    [Authorize]
    public async Task<IActionResult> VincularVenta(int id, [FromBody] VincularRequest req)
    {
        var p = await _db.CafePreventas.FindAsync(id);
        if (p is null) return NotFound();
        p.VentaIdFinal = req.VentaId;
        p.Estado = "procesada";
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // === Admin: vendedores ===
    public record VendedorDto(int Id, string Nombre, string Token, bool IsActive, DateTime CreatedAt);

    [HttpGet("admin/vendedores")]
    [Authorize]
    public async Task<IActionResult> ListVendedores()
    {
        var list = await _db.CafePreventaVendedores.OrderBy(v => v.Nombre).ToListAsync();
        return Ok(list.Select(v => new VendedorDto(v.Id, v.Nombre, v.Token, v.IsActive, v.CreatedAt)).ToList());
    }

    public class CrearVendedorRequest { public string Nombre { get; set; } = ""; }

    [HttpPost("admin/vendedores")]
    [Authorize]
    public async Task<IActionResult> CrearVendedor([FromBody] CrearVendedorRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "Nombre obligatorio" });
        var v = new CafePreventaVendedor
        {
            Nombre = req.Nombre.Trim(),
            Token = Guid.NewGuid().ToString("N"),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.CafePreventaVendedores.Add(v);
        await _db.SaveChangesAsync();
        return Ok(v);
    }

    public class UpdateVendedorRequest
    {
        public string? Nombre { get; set; }
        public bool? IsActive { get; set; }
        public bool RegenerarToken { get; set; }
    }

    [HttpPut("admin/vendedores/{id:int}")]
    [Authorize]
    public async Task<IActionResult> UpdateVendedor(int id, [FromBody] UpdateVendedorRequest req)
    {
        var v = await _db.CafePreventaVendedores.FindAsync(id);
        if (v is null) return NotFound();
        if (req.Nombre is not null && !string.IsNullOrWhiteSpace(req.Nombre)) v.Nombre = req.Nombre.Trim();
        if (req.IsActive.HasValue) v.IsActive = req.IsActive.Value;
        if (req.RegenerarToken) v.Token = Guid.NewGuid().ToString("N");
        v.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(v);
    }
}
