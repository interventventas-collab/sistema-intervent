using Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-10-06: resumen liviano de las máquinas de café que están en los clientes, para que se
/// VEAN siempre (¿Quién me debe?, listado de Clientes y Nueva Venta) sin una llamada por cliente.
/// Solo lectura: la ficha y las cuotas se manejan en CafeComodatosController (no se toca acá).
/// Trae solo lo que está en el cliente: comodatos EN_CLIENTE y financiadas EN_CLIENTE con falta > 0.
/// El saldo es Cafe_Comodatos.SaldoFinanciamiento tal cual (lo mantiene al día la cobranza).
/// </summary>
[ApiController]
[Route("api/cafe/maquinas-resumen")]
[Authorize]
public class CafeMaquinasResumenController : ControllerBase
{
    private readonly AppDbContext _db;
    public CafeMaquinasResumenController(AppDbContext db) => _db = db;

    public record MaquinaResumenDto(int Id, int ClienteId, string? ClienteNombre, string Modalidad,
        string Descripcion, string Moneda, decimal? SaldoFinanciamiento);

    /// <summary>GET api/cafe/maquinas-resumen?clienteId= (sin parámetro: todas).</summary>
    [HttpGet]
    public async Task<ActionResult<List<MaquinaResumenDto>>> Get([FromQuery] int? clienteId)
    {
        var q = _db.CafeComodatos.AsNoTracking()
            .Where(c => c.Estado == "EN_CLIENTE")
            .Where(c => c.Modalidad == "COMODATO"
                     || (c.Modalidad == "FINANCIADA" && c.SaldoFinanciamiento > 0));
        if (clienteId.HasValue) q = q.Where(c => c.ClienteId == clienteId.Value);

        var filas = await q
            .OrderBy(c => c.ClienteId).ThenBy(c => c.Modalidad).ThenBy(c => c.Id)
            .Select(c => new
            {
                c.Id, c.ClienteId, ClienteNombre = c.Cliente != null ? c.Cliente.Nombre : null,
                c.Modalidad, c.Marca, c.Modelo, c.Moneda, c.SaldoFinanciamiento
            })
            .ToListAsync();

        return filas.Select(c => new MaquinaResumenDto(
            c.Id, c.ClienteId, c.ClienteNombre, c.Modalidad,
            Descripcion(c.Marca, c.Modelo),
            string.IsNullOrWhiteSpace(c.Moneda) ? "ARS" : c.Moneda.Trim().ToUpperInvariant(),
            c.Modalidad == "FINANCIADA" ? c.SaldoFinanciamiento : null)).ToList();
    }

    /// <summary>"Marca Modelo" prolijo: sin espacios de más y sin repetir la marca si el modelo
    /// ya la trae (ej. Marca "Gaggia", Modelo "Gaggia Classic" → "Gaggia Classic").</summary>
    private static string Descripcion(string? marca, string? modelo)
    {
        static string Limpio(string? s) => string.Join(' ', (s ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var ma = Limpio(marca);
        var mo = Limpio(modelo);
        if (ma.Length == 0 && mo.Length == 0) return "máquina de café";
        if (ma.Length == 0) return mo;
        if (mo.Length == 0) return ma;
        if (mo.StartsWith(ma, StringComparison.OrdinalIgnoreCase)) return mo;
        return $"{ma} {mo}";
    }
}
