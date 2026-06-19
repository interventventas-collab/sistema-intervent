using Api.Data;
using Api.DTOs;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/cafe/combos")]
[Authorize]
public class CafeCombosController : ControllerBase
{
    private readonly AppDbContext _db;
    private static readonly string[] FormatosValidos = { "1KG", "MEDIO", "CUARTO", "UNIT" };
    // 2026-06-08: corregido "MOLIDO ESPRESS" → "MOLIDO EXPRESS" + agregado "MOLIDO CAFETERA ITALIANA"
    private static readonly string[] MoliendasValidas = { "EN GRANOS", "MOLIDO FILTRO", "MOLIDO EXPRESS", "MOLIDO MOKA", "MOLIDO BODUM", "MOLIDO PRENSA FRANCESA", "MOLIDO CAFETERA ITALIANA", "MOLIDO A LA TURCA", "MINI EXPRESS" };

    public CafeCombosController(AppDbContext db) { _db = db; }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] bool? activos = null)
    {
        var settings = await _db.CafeSettings.FindAsync(1) ?? new CafeSetting { Id = 1 };
        var q = _db.CafeCombos.Include(c => c.Items).ThenInclude(i => i.ProductoNav).Include(c => c.OemNav).AsQueryable();
        if (activos == true) q = q.Where(c => c.IsActive);

        var combos = await q.OrderBy(c => c.Nombre).ToListAsync();
        return Ok(combos.Select(c => Map(c, settings)).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var settings = await _db.CafeSettings.FindAsync(1) ?? new CafeSetting { Id = 1 };
        var c = await _db.CafeCombos.Include(x => x.Items).ThenInclude(i => i.ProductoNav).Include(x => x.OemNav)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound(new { error = "Combo no encontrado" });
        return Ok(Map(c, settings));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCafeComboRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre))
            return BadRequest(new { error = "El nombre es obligatorio" });
        if (req.Items is null || req.Items.Count == 0)
            return BadRequest(new { error = "El combo debe tener al menos 1 item" });

        var combo = new CafeCombo
        {
            Nombre = req.Nombre.Trim(),
            Descripcion = string.IsNullOrWhiteSpace(req.Descripcion) ? null : req.Descripcion.Trim(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            EsCompuesto = req.EsCompuesto ?? false,
            OemId = req.OemId,
            MultiplicadorOem = req.MultiplicadorOem
        };

        var validacion = await ValidarYAgregarItemsAsync(combo, req.Items);
        if (validacion is not null) return BadRequest(new { error = validacion });

        _db.CafeCombos.Add(combo);
        await _db.SaveChangesAsync();

        var settings = await _db.CafeSettings.FindAsync(1) ?? new CafeSetting { Id = 1 };
        return Ok(Map(await ReloadAsync(combo.Id), settings));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCafeComboRequest req)
    {
        var combo = await _db.CafeCombos.Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (combo is null) return NotFound(new { error = "Combo no encontrado" });

        if (req.Nombre is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Nombre))
                return BadRequest(new { error = "El nombre no puede estar vacio" });
            combo.Nombre = req.Nombre.Trim();
        }
        if (req.Descripcion is not null)
            combo.Descripcion = string.IsNullOrWhiteSpace(req.Descripcion) ? null : req.Descripcion.Trim();
        if (req.IsActive.HasValue) combo.IsActive = req.IsActive.Value;
        if (req.EsCompuesto.HasValue) combo.EsCompuesto = req.EsCompuesto.Value;
        // 2026-06-18: OEM. ClearOem=true desvincula (vuelve a precio por componentes).
        // Si OemId viene con valor, se setea. Si no se manda nada, queda como estaba.
        if (req.ClearOem == true)
        {
            combo.OemId = null;
            combo.MultiplicadorOem = null;
        }
        else if (req.OemId.HasValue)
        {
            combo.OemId = req.OemId.Value;
            combo.MultiplicadorOem = req.MultiplicadorOem ?? 1m;
        }
        else if (req.MultiplicadorOem.HasValue && combo.OemId.HasValue)
        {
            // Permitir cambiar solo el multiplicador manteniendo el OEM
            combo.MultiplicadorOem = req.MultiplicadorOem.Value;
        }

        if (req.Items is not null)
        {
            if (req.Items.Count == 0)
                return BadRequest(new { error = "El combo debe tener al menos 1 item" });

            // Reemplazar todos los items
            _db.CafeComboItems.RemoveRange(combo.Items);
            combo.Items.Clear();

            var validacion = await ValidarYAgregarItemsAsync(combo, req.Items);
            if (validacion is not null) return BadRequest(new { error = validacion });
        }

        combo.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var settings = await _db.CafeSettings.FindAsync(1) ?? new CafeSetting { Id = 1 };
        return Ok(Map(await ReloadAsync(combo.Id), settings));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var combo = await _db.CafeCombos.Include(c => c.Items).FirstOrDefaultAsync(c => c.Id == id);
        if (combo is null) return NotFound(new { error = "Combo no encontrado" });
        _db.CafeCombos.Remove(combo);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    /// <summary>2026-06-01: toggle dedicado para EsCompuesto. Permite editarlo sin tener
    /// que reenviar todos los items del combo (Update completo).</summary>
    public record SetEsCompuestoRequest(bool EsCompuesto);

    [HttpPatch("{id:int}/es-compuesto")]
    public async Task<IActionResult> SetEsCompuesto(int id, [FromBody] SetEsCompuestoRequest req)
    {
        var combo = await _db.CafeCombos.FirstOrDefaultAsync(c => c.Id == id);
        if (combo is null) return NotFound(new { error = "Combo no encontrado" });
        combo.EsCompuesto = req.EsCompuesto;
        combo.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { id, esCompuesto = combo.EsCompuesto });
    }

    /// <summary>2026-06-18 — Reclasifica TODOS los combos EsCompuesto=1 segun la regla refinada
    /// definida con el usuario. Es Compuesto solo si:
    ///   (a) Todos los componentes tienen cantidad = 1
    ///   (b) Ningun componente es a su vez un combo (su SKU existe en Cafe_Combos)
    ///   (c) Los nombres normalizados de los componentes NO son todos iguales (no es combo de variantes)
    /// Los que no cumplen pasan a EsCompuesto=0 (combo MeLi). Devuelve el resumen con cantidades y
    /// la lista de SKUs degradados para auditoría.</summary>
    public record ReclasificarResult(int Total, int QuedaronCompuestos, int PasaronACombo, List<string> SkusDegradados);

    [HttpPost("reclasificar-automatico")]
    public async Task<IActionResult> ReclasificarAutomatico()
    {
        // 1. Cargar todos los combos EsCompuesto=1 con sus items y productos
        var compuestosActuales = await _db.CafeCombos
            .Include(c => c.Items).ThenInclude(i => i.ProductoNav)
            .Where(c => c.EsCompuesto)
            .ToListAsync();

        // 2. Pre-calcular: set de SKUs que existen como combo (para condicion b)
        var skusQueSonCombos = (await _db.CafeCombos
            .Where(c => c.Sku != null)
            .Select(c => c.Sku!)
            .ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var degradados = new List<string>();
        int quedan = 0;

        foreach (var c in compuestosActuales)
        {
            var esCompuestoReal = EsCompuestoSegunRegla(c, skusQueSonCombos);
            if (!esCompuestoReal)
            {
                c.EsCompuesto = false;
                c.UpdatedAt = DateTime.UtcNow;
                degradados.Add(c.Sku ?? $"#{c.Id}");
            }
            else
            {
                quedan++;
            }
        }
        await _db.SaveChangesAsync();
        return Ok(new ReclasificarResult(
            Total: compuestosActuales.Count,
            QuedaronCompuestos: quedan,
            PasaronACombo: degradados.Count,
            SkusDegradados: degradados));
    }

    /// <summary>2026-06-18 — Sugiere OEMs para los compuestos sin OEM cargado, en lote.
    /// Algoritmo: para cada compuesto, extraer la "raíz" del SKU (quitar prefijo C, sufijos de color
    /// y multiplicadores XN) y buscar OEM con código exactamente igual a esa raíz.</summary>
    public record SugerenciaOemDto(int ComboId, string ComboSku, string ComboNombre, int OemId, string OemCodigo, string? OemDescripcion, decimal? OemPvpConIva);
    public record SugerenciaResult(int Total, int ConMatch, int SinMatch, List<SugerenciaOemDto> Sugerencias);

    [HttpGet("sugerir-oems")]
    public async Task<IActionResult> SugerirOems()
    {
        // 1. compuestos sin OEM
        var sinOem = await _db.CafeCombos
            .Where(c => c.EsCompuesto && c.OemId == null && c.Sku != null)
            .Select(c => new { c.Id, c.Sku, c.Nombre })
            .ToListAsync();

        // 2. todos los OEMs indexados por código (uppercase trim)
        var oems = await _db.CafeOems
            .Where(o => o.Codigo != null)
            .Select(o => new { o.Id, o.Codigo, o.Descripcion, o.PvpConIva })
            .ToListAsync();
        var oemPorCodigo = oems
            .GroupBy(o => o.Codigo!.Trim().ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.First());

        var sugerencias = new List<SugerenciaOemDto>();
        int sinMatch = 0;
        foreach (var c in sinOem)
        {
            var raiz = ExtraerRaizOemDelSku(c.Sku!);
            if (string.IsNullOrEmpty(raiz)) { sinMatch++; continue; }
            if (oemPorCodigo.TryGetValue(raiz, out var oem))
            {
                sugerencias.Add(new SugerenciaOemDto(c.Id, c.Sku!, c.Nombre, oem.Id, oem.Codigo!, oem.Descripcion, oem.PvpConIva));
            }
            else
            {
                sinMatch++;
            }
        }
        return Ok(new SugerenciaResult(sinOem.Count, sugerencias.Count, sinMatch, sugerencias));
    }

    /// <summary>2026-06-18 — Aplica en lote las sugerencias confirmadas por el usuario.</summary>
    public record AplicarSugerenciaItem(int ComboId, int OemId, decimal Multiplicador);
    public record AplicarSugerenciasRequest(List<AplicarSugerenciaItem> Items);
    public record AplicarSugerenciasResult(int Aplicadas, int Fallidas);

    [HttpPost("aplicar-sugerencias-oem")]
    public async Task<IActionResult> AplicarSugerenciasOem([FromBody] AplicarSugerenciasRequest req)
    {
        if (req.Items is null || req.Items.Count == 0)
            return BadRequest(new { error = "Lista vacia" });

        int aplicadas = 0, fallidas = 0;
        var ids = req.Items.Select(x => x.ComboId).ToList();
        var combos = await _db.CafeCombos.Where(c => ids.Contains(c.Id)).ToListAsync();
        var combosById = combos.ToDictionary(c => c.Id);

        foreach (var item in req.Items)
        {
            if (!combosById.TryGetValue(item.ComboId, out var combo)) { fallidas++; continue; }
            combo.OemId = item.OemId;
            combo.MultiplicadorOem = item.Multiplicador > 0m ? item.Multiplicador : 1m;
            combo.EsCompuesto = true;
            combo.UpdatedAt = DateTime.UtcNow;
            aplicadas++;
        }
        await _db.SaveChangesAsync();
        return Ok(new AplicarSugerenciasResult(aplicadas, fallidas));
    }

    /// <summary>2026-06-18 — Extrae la "raíz" de codigo OEM del SKU del compuesto.
    /// Estrategia: ir probando candidatos de mas largo a mas corto y devolver el primero que sea util.
    /// Ej: C926NEG -> 926; C4300AZX10 -> 4300; X146X10 -> X146; 10KDOR -> 10KDOR (no se trimea sufijo si queda < 2 chars).
    /// El backend luego matchea contra OEM.Codigo exacto (uppercase). Si no hay match, queda como "sin sugerencia".</summary>
    private static string ExtraerRaizOemDelSku(string sku)
    {
        var s = sku.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(s)) return "";

        // Sufijos de color/material/tipo que aparecen al final del SKU del compuesto
        var sufijosColor = new[] {
            "NEGRO","BLANCO","ROJO","AZUL","VERDE","AMARILLO","FUCSIA","CELESTE","VIOLETA","NARANJA","BEIGE","GRIS","ROSA","MARRON","TURQUESA","LILA","MENTA","BORDO","DORADO","PLATEADO","TRANSPARENTE","CRISTAL","NATURAL","SURTIDO","CLARO","OSCURO",
            "NEG","BLA","ROJ","AZU","VER","AMA","FUC","CEL","VIO","NAR","BEI","GRI","ROS","MAR","TUR","LIL","MEN","BOR","DOR","PLA","TRA","TR","BL","GR","AZ","VP","FU","BE","NA","AM","RO","CL","OS","TN","VM","TX","NE","RJ","CE","GS","AS"
        };
        // Sufijos de multiplicador (X2, X4, X10, X100, etc.)
        var rxMult = new System.Text.RegularExpressions.Regex(@"X\d{1,3}$");

        // Quitar multiplicadores al final (puede haber mas de uno, ej C4300AZX10 = AZ + X10)
        while (rxMult.IsMatch(s))
        {
            s = rxMult.Replace(s, "");
        }

        // Quitar sufijos de color al final (probando de mas largo a mas corto)
        bool cambio = true;
        while (cambio && s.Length > 2)
        {
            cambio = false;
            foreach (var suf in sufijosColor.OrderByDescending(x => x.Length))
            {
                if (s.EndsWith(suf, StringComparison.OrdinalIgnoreCase) && s.Length - suf.Length >= 2)
                {
                    s = s.Substring(0, s.Length - suf.Length);
                    cambio = true;
                    break;
                }
            }
        }

        // Quitar prefijo C si lo que queda son solo digitos (ej C926 -> 926, pero C9419TT no toca)
        if (s.Length > 1 && s[0] == 'C' && s.Substring(1).All(char.IsDigit))
        {
            s = s.Substring(1);
        }

        return s;
    }

    /// <summary>2026-06-18 — Regla refinada para detectar producto compuesto.</summary>
    private static bool EsCompuestoSegunRegla(CafeCombo c, HashSet<string> skusQueSonCombos)
    {
        if (c.Items.Count == 0) return false;
        // (a) Todos cantidad = 1
        if (c.Items.Any(i => i.Cantidad > 1)) return false;
        // (b) Ningun componente es otro combo
        foreach (var i in c.Items)
        {
            var sku = i.ProductoNav?.Sku;
            if (!string.IsNullOrEmpty(sku) && skusQueSonCombos.Contains(sku)) return false;
        }
        // (c) Los nombres base de los componentes NO son todos iguales
        if (c.Items.Count >= 2)
        {
            var nombresBase = c.Items
                .Select(i => NormalizarNombreBase(i.ProductoNav?.Nombre))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (nombresBase.Count == 1) return false;  // todos comparten el mismo nombre base → combo de variantes
        }
        return true;
    }

    private static string NormalizarNombreBase(string? nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre)) return "";
        var n = nombre.Trim().ToUpperInvariant();
        return n.Length > 20 ? n.Substring(0, 20) : n;
    }

    // ============================================================
    // Helpers
    // ============================================================

    private async Task<CafeCombo> ReloadAsync(int id)
    {
        return (await _db.CafeCombos.Include(c => c.Items).ThenInclude(i => i.ProductoNav).Include(c => c.OemNav)
            .FirstAsync(c => c.Id == id));
    }

    /// <summary>Valida + carga los items en el combo. Devuelve null si todo bien, mensaje de error si no.</summary>
    private async Task<string?> ValidarYAgregarItemsAsync(CafeCombo combo, List<CafeComboItemRequest> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.Cantidad <= 0) return $"Item {i + 1}: la cantidad debe ser mayor a 0";
            if (!FormatosValidos.Contains(it.Formato)) return $"Item {i + 1}: formato invalido '{it.Formato}'";

            var prod = await _db.CafeProductos.FindAsync(it.ProductoId);
            if (prod is null) return $"Item {i + 1}: producto {it.ProductoId} no encontrado";

            // Coherencia: formato unitario solo para OTROS, formatos kg solo para CAFE
            var esCafe = prod.Categoria == "CAFE";
            var esFormatoCafe = it.Formato is "1KG" or "MEDIO" or "CUARTO";
            if (esCafe != esFormatoCafe)
                return $"Item {i + 1} ({prod.Nombre}): " +
                    (esCafe ? "para cafe usa 1 kg / 1/2 kg / 1/4 kg" : "para otros productos usa 'unidad'");

            string? molienda = null;
            if (esCafe && !string.IsNullOrWhiteSpace(it.Molienda))
            {
                var m = it.Molienda.Trim().ToUpperInvariant();
                if (MoliendasValidas.Contains(m)) molienda = m;
            }

            combo.Items.Add(new CafeComboItem
            {
                ProductoId = prod.Id,
                Formato = it.Formato,
                Cantidad = it.Cantidad,
                Molienda = molienda,
                EsDoyPack = it.EsDoyPack && esCafe,
                EsEnvasePlateado = it.EsEnvasePlateado && esCafe && !it.EsDoyPack,
                SortOrder = it.SortOrder == 0 ? i : it.SortOrder
            });
        }
        return null;
    }

    private static CafeComboDto Map(CafeCombo c, CafeSetting settings)
    {
        decimal precioBar = 0m, precioOtro = 0m, costoTotal = 0m;
        int? stockMinDisponible = null;
        foreach (var it in c.Items)
        {
            var prod = it.ProductoNav;
            if (prod is null) continue;
            precioBar += CafePricingService.CalcularPrecioUnitario(prod, it.Formato, "BAR", settings) * it.Cantidad;
            precioOtro += CafePricingService.CalcularPrecioUnitario(prod, it.Formato, "OTRO", settings) * it.Cantidad;
            // 2026-06-18: costo total = suma de (Costo del componente × cantidad)
            costoTotal += prod.Costo * it.Cantidad;
            // 2026-06-18: stock disponible = min(stock_componente / cantidad). Cuantos compuestos puedo armar.
            if (it.Cantidad > 0)
            {
                var disponible = prod.StockUnidades / it.Cantidad;
                stockMinDisponible = stockMinDisponible.HasValue ? Math.Min(stockMinDisponible.Value, disponible) : disponible;
            }
        }

        // 2026-06-18: si el compuesto tiene OEM cargado, usar el PVP del OEM × multiplicador
        // en lugar de la suma de componentes. Logica espejo de CafeProductos.
        if (c.EsCompuesto && c.OemNav is not null && (c.OemNav.PvpConIva ?? 0m) > 0m)
        {
            var mult = c.MultiplicadorOem ?? 1m;
            if (mult <= 0m) mult = 1m;
            var oemConIva = Math.Round((c.OemNav.PvpConIva ?? 0m) * mult, 2);
            // OEM trae el PvpConIva. Mostramos el mismo precio para BAR y OTRO porque el OEM
            // representa el precio mayorista unico del armado de fabrica.
            precioBar = oemConIva;
            precioOtro = oemConIva;
        }

        return new CafeComboDto(
            c.Id, c.Nombre, c.Descripcion,
            c.IsActive, c.CreatedAt, c.UpdatedAt,
            c.Items.Count,
            precioBar, precioOtro,
            c.Items.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => new CafeComboItemDto(
                x.Id, x.ProductoId,
                x.ProductoNav?.Nombre ?? "?",
                x.ProductoNav?.Categoria ?? "CAFE",
                x.ProductoNav?.Marca,
                x.ProductoNav?.Sku,
                x.ProductoNav?.Pvp1,
                x.ProductoNav?.Pvp2,
                x.Formato, x.Cantidad,
                x.Molienda, x.EsDoyPack,
                x.SortOrder,
                x.EsEnvasePlateado)).ToList(),
            Sku: c.Sku,
            EsCompuesto: c.EsCompuesto,
            OemId: c.OemId,
            OemCodigo: c.OemNav?.Codigo,
            OemPvpConIva: c.OemNav?.PvpConIva,
            OemIvaPct: c.OemNav?.IvaPct,
            MultiplicadorOem: c.MultiplicadorOem,
            CostoSumaComponentes: Math.Round(costoTotal, 2),
            StockDisponible: stockMinDisponible ?? 0
        );
    }
}
