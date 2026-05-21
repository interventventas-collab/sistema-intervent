using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Importa productos sueltos y stock inicial desde Contabilium.
///
/// Reglas (decision usuario 2026-05-21):
/// - Solo importa Tipo="Producto" (NO combos).
/// - Solo importa productos que sean: vendidos directo en MeLi, O componentes de algun combo en MeLi.
/// - Los cafes (F0-F49) ya estan en sistema, NO los pisa.
/// - Si el producto ya existe (mismo SKU), actualiza el stock pero deja los precios intactos.
/// - Si no existe, crea con Categoria="OTROS" + datos basicos.
///
/// Tambien pobla MeliItemComponentes: por cada MeliItem, calcula que productos sueltos
/// componen esa publicacion (1:1 si es producto suelto, 1:N si es combo Contabilium).
/// </summary>
public class ContabiliumImportService
{
    private readonly AppDbContext _db;
    private readonly ContabiliumService _contab;
    private readonly ILogger<ContabiliumImportService> _logger;

    public ContabiliumImportService(AppDbContext db, ContabiliumService contab, ILogger<ContabiliumImportService> logger)
    {
        _db = db; _contab = contab; _logger = logger;
    }

    public record ImportResult(
        int ProductosCreados, int ProductosActualizados,
        int ComponentesLinkeados, int ItemsConCombo, int ItemsDirectos,
        int ItemsSinMatch, List<string> Warnings);

    /// <summary>Ejecuta el import completo: baja conceptos, baja combos, importa productos sueltos y popula MeliItemComponentes.</summary>
    public async Task<ImportResult> RunFullImportAsync(CancellationToken ct = default)
    {
        var warnings = new List<string>();

        // 1) Bajar todos los conceptos Contabilium paginado (50 por pagina hasta agotar).
        _logger.LogInformation("[ContabImport] Bajando conceptos Contabilium...");
        var todosConceptos = new List<ContabiliumService.ConceptoDto>();
        int page = 1;
        while (true)
        {
            var pg = await _contab.ListConceptosAsync(page, 50);
            if (pg is null) { warnings.Add($"No se pudo bajar pagina {page}"); break; }
            todosConceptos.AddRange(pg.Items);
            if (pg.Items.Count < 50) break;
            page++;
            if (page > 200) break; // safety
            await Task.Delay(100, ct);
        }
        _logger.LogInformation("[ContabImport] Conceptos bajados: {N}", todosConceptos.Count);

        // Index por codigo (case-insensitive)
        var contabBySku = todosConceptos
            .Where(c => !string.IsNullOrWhiteSpace(c.Codigo))
            .GroupBy(c => c.Codigo!.Trim().ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.First());

        // 2) Items MeLi en DB
        var meliItems = await _db.MeliItems
            .Where(mi => mi.Sku != null && mi.Sku != "")
            .ToListAsync(ct);
        var meliSkusUnique = meliItems.Select(mi => (mi.Sku ?? "").Trim().ToUpperInvariant())
            .Where(s => !string.IsNullOrEmpty(s)).Distinct().ToHashSet();

        // 3) Identificar combos Contabilium que aparecen en MeLi
        var combosToFetch = todosConceptos
            .Where(c => string.Equals(c.Tipo, "Combo", StringComparison.OrdinalIgnoreCase))
            .Where(c => meliSkusUnique.Contains((c.Codigo ?? "").Trim().ToUpperInvariant()))
            .ToList();
        _logger.LogInformation("[ContabImport] Combos a expandir: {N}", combosToFetch.Count);

        // 4) Bajar detalle de cada combo (con sus Items componentes)
        var comboComponents = new Dictionary<string, List<(string sku, decimal cant)>>(StringComparer.OrdinalIgnoreCase);
        int comboFetchCount = 0;
        foreach (var c in combosToFetch)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var detail = await _contab.GetConceptoAsync(c.Id);
                if (detail?.Items is not null)
                {
                    var list = detail.Items
                        .Where(it => !string.IsNullOrWhiteSpace(it.Codigo))
                        .Select(it => (sku: it.Codigo!.Trim().ToUpperInvariant(), cant: it.Cantidad))
                        .ToList();
                    if (list.Count > 0) comboComponents[c.Codigo!.Trim().ToUpperInvariant()] = list;
                }
                comboFetchCount++;
                if (comboFetchCount % 200 == 0)
                {
                    _logger.LogInformation("[ContabImport] {N} combos descargados...", comboFetchCount);
                }
                await Task.Delay(60, ct);
            }
            catch (Exception ex)
            {
                warnings.Add($"Combo {c.Codigo} (id {c.Id}): {ex.Message}");
            }
        }
        _logger.LogInformation("[ContabImport] Combos con componentes: {N}", comboComponents.Count);

        // 5) Set de SKUs sueltos a importar: directos en MeLi + componentes de combos en MeLi.
        var skusAImportar = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Patron café (F[R]?N(.2|.4)?) — esos ya están en sistema, NO los importamos.
        var esCafe = new Regex(@"^F[R]?\d+(\.\d+)?$", RegexOptions.IgnoreCase);

        foreach (var sku in meliSkusUnique)
        {
            if (esCafe.IsMatch(sku)) continue; // café ya en sistema
            if (contabBySku.TryGetValue(sku, out var con) && string.Equals(con.Tipo, "Producto", StringComparison.OrdinalIgnoreCase))
            {
                skusAImportar.Add(sku);
            }
        }
        foreach (var comp in comboComponents.Values.SelectMany(l => l))
        {
            if (esCafe.IsMatch(comp.sku)) continue;
            if (contabBySku.TryGetValue(comp.sku, out var con) && string.Equals(con.Tipo, "Producto", StringComparison.OrdinalIgnoreCase))
                skusAImportar.Add(comp.sku);
        }
        _logger.LogInformation("[ContabImport] Productos sueltos a importar (excluyendo cafés): {N}", skusAImportar.Count);

        // 6) Importar productos al sistema
        var existing = await _db.CafeProductos
            .Where(p => p.Sku != null)
            .ToDictionaryAsync(p => p.Sku!.Trim().ToUpperInvariant(), ct);
        int creados = 0, actualizados = 0;
        foreach (var sku in skusAImportar)
        {
            var con = contabBySku[sku];
            if (existing.TryGetValue(sku, out var prod))
            {
                // Actualizar SOLO stock (no pisar precios ni nombre).
                prod.StockUnidades = (int)Math.Round(con.Stock ?? 0m);
                if (prod.UpdatedAt is null || prod.UpdatedAt < DateTime.UtcNow.AddMinutes(-1))
                    prod.UpdatedAt = DateTime.UtcNow;
                actualizados++;
            }
            else
            {
                var nuevo = new CafeProducto
                {
                    Sku = sku,
                    Nombre = (con.Nombre ?? sku).Trim(),
                    Categoria = "OTROS",
                    Costo = con.CostoInterno ?? 0m,
                    StockUnidades = (int)Math.Round(con.Stock ?? 0m),
                    IsActive = string.Equals(con.Estado, "Activo", StringComparison.OrdinalIgnoreCase),
                    CreatedAt = DateTime.UtcNow
                };
                // Precio: si el PrecioFinal de Contabilium > 0, lo cargamos como PrecioOtro (referencia, no se modifica despues).
                if (con.PrecioFinal.HasValue && con.PrecioFinal.Value > 0)
                {
                    nuevo.Pvp2 = con.PrecioFinal.Value;
                    nuevo.PrecioOtro = con.PrecioFinal.Value;
                }
                _db.CafeProductos.Add(nuevo);
                existing[sku] = nuevo;
                creados++;
            }
        }
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("[ContabImport] Productos creados {C}, actualizados {U}", creados, actualizados);

        // 7) Poblar MeliItemComponentes (limpiar y re-armar).
        // Cuidado: NO borramos componentes de cafés que ya fueron linkeados a mano antes
        // (esos están en MeliItem.CafeProductoId + CafeFormato — el SyncService los maneja como fallback legacy).
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM MeliItemComponentes WHERE Source IS NULL OR Source <> 'manual'", ct);

        int compsLinkeados = 0, itemsConCombo = 0, itemsDirectos = 0, itemsSinMatch = 0;

        foreach (var mi in meliItems)
        {
            var sku = (mi.Sku ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(sku)) continue;
            if (esCafe.IsMatch(sku))
            {
                // Cafés: el linkeo viejo en MeliItem.CafeProductoId/CafeFormato sigue siendo el ofical.
                continue;
            }
            // Es combo en Contabilium?
            if (comboComponents.TryGetValue(sku, out var comps))
            {
                foreach (var (csku, ccant) in comps)
                {
                    if (existing.TryGetValue(csku, out var prod))
                    {
                        _db.MeliItemComponentes.Add(new MeliItemComponente
                        {
                            MeliItemId = mi.MeliItemId,
                            CafeProductoId = prod.Id,
                            Cantidad = ccant,
                            Source = "combo-contabilium",
                            CreatedAt = DateTime.UtcNow
                        });
                        compsLinkeados++;
                    }
                }
                itemsConCombo++;
            }
            else if (existing.TryGetValue(sku, out var prodD))
            {
                // Producto suelto directo
                _db.MeliItemComponentes.Add(new MeliItemComponente
                {
                    MeliItemId = mi.MeliItemId,
                    CafeProductoId = prodD.Id,
                    Cantidad = 1m,
                    Source = "meli-sku-directo",
                    CreatedAt = DateTime.UtcNow
                });
                compsLinkeados++;
                itemsDirectos++;
            }
            else
            {
                itemsSinMatch++;
            }
        }
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("[ContabImport] Componentes linkeados: {N} (combo:{C}, directo:{D}, sin match:{S})",
            compsLinkeados, itemsConCombo, itemsDirectos, itemsSinMatch);

        return new ImportResult(creados, actualizados, compsLinkeados, itemsConCombo, itemsDirectos, itemsSinMatch, warnings);
    }
}
