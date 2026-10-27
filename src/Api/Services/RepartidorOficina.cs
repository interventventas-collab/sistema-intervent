using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-10-20: sección "Oficina" del link del repartidor (/mis-pedidos). Pedido del dueño: que él y
/// sus hermanos (Gabriel, Germán, Osmar) vean desde el celu lo mismo que en la compu —cobranzas,
/// cheques a pagar, listas de precios, stock, alquileres— y que quede "encaminado para ir agregando
/// más cosas". Cada cosa es un tilde en la ficha del repartidor (Administración → Repartidores),
/// guardados juntos en <see cref="CafeRepartidor.Oficina"/> separados por coma. Una cosa nueva =
/// una clave nueva acá + su cuadradito en el celu + su tilde en la ficha, sin columnas nuevas.
///
/// Decisión del dueño: todo SIN clave (lo que ve cada uno lo deciden los tildes), salvo el saldo
/// del BANCO, que pide la clave de 4 números de Osmar, Germán o Gabriel (la del PIN de operador).
/// "Comprobantes" sigue siendo su propio tilde (<see cref="CafeRepartidor.VerComprobantes"/>, de antes).
/// El saldo de Shell Flota lo ven TODOS los repartidores, sin tilde.
///
/// Los datos salen de los MISMOS endpoints que usa la compu: cada controlador tiene un
/// "repartidor/{token}" [AllowAnonymous] que chequea el tilde acá y llama a su método de siempre.
/// </summary>
public static class RepartidorOficina
{
    public const string Cobranzas = "cobranzas";
    public const string Cheques = "cheques";
    public const string Listas = "listas";
    public const string Stock = "stock";
    public const string Alquileres = "alquileres";
    public const string Banco = "banco";
    // 21/10/2026: "Por dónde van" (los camioncitos del mapa) y el fichaje del día.
    public const string Ubicaciones = "ubicaciones";
    public const string Fichaje = "fichaje";
    // 27/10/2026: la carpeta de Documentación importante (constancias, certificados, etc.), solo ver y mandar.
    public const string Documentacion = "documentacion";

    /// <summary>En el orden en que aparecen los cuadraditos y los tildes.</summary>
    public static readonly string[] Todas = { Cobranzas, Cheques, Listas, Stock, Alquileres, Ubicaciones, Fichaje, Documentacion, Banco };

    /// <summary>Los que pueden abrir el saldo del banco con SU clave (PIN de operador).</summary>
    public static readonly string[] ClaveBanco = { "OSMAR", "GERMAN", "GABRIEL" };

    public static HashSet<string> Parse(string? oficina) =>
        (oficina ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant()).Where(Todas.Contains).ToHashSet();

    /// <summary>Normaliza lo que manda la ficha: solo claves conocidas, sin repetir, en orden.</summary>
    public static string? Normalizar(IEnumerable<string>? claves)
    {
        var set = (claves ?? Enumerable.Empty<string>()).Select(x => (x ?? "").Trim().ToLowerInvariant()).ToHashSet();
        var ok = Todas.Where(set.Contains).ToList();
        return ok.Count == 0 ? null : string.Join(",", ok);
    }

    /// <summary>El repartidor activo de ese link, solo si tiene el tilde pedido. Null = no puede.</summary>
    public static async Task<CafeRepartidor?> ConPermisoAsync(AppDbContext db, string? token, string permiso)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var r = await db.CafeRepartidores.AsNoTracking()
            .FirstOrDefaultAsync(x => x.PublicToken == token && x.IsActive);
        return r is not null && Parse(r.Oficina).Contains(permiso) ? r : null;
    }
}
