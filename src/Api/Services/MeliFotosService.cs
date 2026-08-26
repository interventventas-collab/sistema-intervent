using System.Net.Http.Headers;
using System.Text.Json;
using Api.Data;
using Api.DTOs;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-08-26 — Fotos de una publicación, para la pantalla nueva (etapa 3).
///
/// Pedido de Osmar: "si se pincha en la foto, que se desplieguen todas las fotos así las veo y
/// las puedo modificar". Y sobre todo: poder COPIARLAS a las publicaciones hermanas — el mismo
/// producto está publicado varias veces y hoy hay que dejar linda cada una por separado.
///
/// Reusa lo que ya existía: MeliItemService.UpdateItemPicturesAsync (PUT a MeLi con la lista
/// ordenada — la PRIMERA foto es la portada). Acá se agrega leer en vivo, ver las hermanas con
/// cuántas fotos tiene cada una, y copiar de una a las otras.
///
/// Dos avisos importantes:
///  • Reordenar, poner portada y borrar son la MISMA operación: se manda la lista final ordenada.
///  • Las publicaciones de CATÁLOGO tienen las fotos bloqueadas por MeLi. Se avisa y no se deja tocar.
/// </summary>
public class MeliFotosService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MeliAccountService _accountService;
    private readonly MeliItemService _itemService;
    private readonly ILogger<MeliFotosService> _logger;

    public MeliFotosService(AppDbContext db, IHttpClientFactory httpFactory,
        MeliAccountService accountService, MeliItemService itemService,
        ILogger<MeliFotosService> logger)
    {
        _db = db;
        _httpFactory = httpFactory;
        _accountService = accountService;
        _itemService = itemService;
        _logger = logger;
    }

    public record FotoDto(string Id, string Url);
    public record HermanaDto(string MeliItemId, string? Titulo, int CantidadFotos, string? Estado,
        string? Thumbnail, bool DeCatalogo);
    public record FotosDto(string MeliItemId, string? Titulo, string? Sku, bool DeCatalogo,
        string? Permalink, List<FotoDto> Fotos, List<HermanaDto> Hermanas, string? Aviso);

    public record GuardarRequest(List<PictureSpec> Fotos);
    public record CopiarRequest(List<string> Destinos);
    public record ResultadoDto(bool Ok, string Mensaje, FotosDto? Fotos);

    /// <summary>Lee las fotos en vivo de MeLi, más las hermanas (mismo SKU) con su cantidad de fotos.</summary>
    public async Task<FotosDto?> LeerAsync(string meliItemId, CancellationToken ct = default)
    {
        var item = await _db.MeliItems.AsNoTracking().Include(i => i.MeliAccount)
            .FirstOrDefaultAsync(i => i.MeliItemId == meliItemId && i.VariationId == null, ct);
        if (item?.MeliAccount is null) return null;

        var token = await _accountService.GetValidTokenAsync(item.MeliAccount);
        if (string.IsNullOrWhiteSpace(token))
            return new FotosDto(meliItemId, item.Title, item.Sku, false, item.Permalink,
                new(), new(), "No hay token de MercadoLibre: reconectá la cuenta en Integraciones.");

        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.Timeout = TimeSpan.FromSeconds(30);

        var fotos = new List<FotoDto>();
        bool deCatalogo = item.CatalogListing;
        string? aviso = null;

        try
        {
            var resp = await http.GetAsync($"https://api.mercadolibre.com/items/{meliItemId}", ct);
            if (!resp.IsSuccessStatusCode)
                return new FotosDto(meliItemId, item.Title, item.Sku, deCatalogo, item.Permalink,
                    new(), new(), $"MercadoLibre no devolvió las fotos (error {(int)resp.StatusCode}).");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            if (root.TryGetProperty("catalog_listing", out var cl) && cl.ValueKind == JsonValueKind.True)
                deCatalogo = true;

            if (root.TryGetProperty("pictures", out var pics) && pics.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in pics.EnumerateArray())
                {
                    var id = p.TryGetProperty("id", out var pid) ? pid.GetString() : null;
                    var url = p.TryGetProperty("secure_url", out var su) ? su.GetString()
                            : p.TryGetProperty("url", out var u) ? u.GetString() : null;
                    if (id is null || url is null) continue;
                    fotos.Add(new FotoDto(id, url));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Fotos] {Mla}: no se pudo leer de MeLi", meliItemId);
            return new FotosDto(meliItemId, item.Title, item.Sku, deCatalogo, item.Permalink,
                new(), new(), "No se pudo consultar a MercadoLibre en este momento.");
        }

        if (deCatalogo)
            aviso = "Es una publicación de catálogo: las fotos las pone MercadoLibre y no se pueden cambiar.";
        else if (fotos.Count == 0)
            aviso = "Esta publicación no tiene fotos.";

        var hermanas = await LeerHermanasAsync(http, item.Sku, meliItemId, ct);
        return new FotosDto(meliItemId, item.Title, item.Sku, deCatalogo, item.Permalink, fotos, hermanas, aviso);
    }

    /// <summary>Las otras publicaciones del mismo SKU, con cuántas fotos tiene cada una.
    /// Se pregunta de a 20 por vez (multiget de MeLi) para no hacer una llamada por publicación.</summary>
    private async Task<List<HermanaDto>> LeerHermanasAsync(HttpClient http, string? sku,
        string meliItemId, CancellationToken ct)
    {
        var hermanas = new List<HermanaDto>();
        if (string.IsNullOrWhiteSpace(sku)) return hermanas;

        var candidatas = await _db.MeliItems.AsNoTracking()
            .Where(m => m.Sku == sku && m.MeliItemId != meliItemId
                        && m.VariationId == null && m.Status != "closed" && m.Status != "deleted")
            .OrderBy(m => m.Title)
            .Select(m => new { m.MeliItemId, m.Title, m.Status, m.Thumbnail, m.CatalogListing })
            .Take(40)
            .ToListAsync(ct);
        if (candidatas.Count == 0) return hermanas;

        // cantidad de fotos + si es de catálogo, en vivo
        var conteo = new Dictionary<string, (int Fotos, bool Catalogo)>();
        foreach (var tanda in candidatas.Chunk(20))
        {
            try
            {
                var ids = string.Join(",", tanda.Select(c => c.MeliItemId));
                var resp = await http.GetAsync(
                    $"https://api.mercadolibre.com/items?ids={ids}&attributes=id,pictures,catalog_listing", ct);
                if (!resp.IsSuccessStatusCode) continue;

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    if (!e.TryGetProperty("body", out var body)) continue;
                    var id = body.TryGetProperty("id", out var bid) ? bid.GetString() : null;
                    if (id is null) continue;
                    int n = body.TryGetProperty("pictures", out var ps) && ps.ValueKind == JsonValueKind.Array
                        ? ps.GetArrayLength() : 0;
                    bool cat = body.TryGetProperty("catalog_listing", out var c) && c.ValueKind == JsonValueKind.True;
                    conteo[id] = (n, cat);
                }
            }
            catch (Exception ex)
            {
                // Si falla el conteo mostramos igual las hermanas: se ven con "?" fotos.
                _logger.LogWarning(ex, "[Fotos] No se pudo contar las fotos de las hermanas de {Sku}", sku);
            }
        }

        foreach (var c in candidatas)
        {
            conteo.TryGetValue(c.MeliItemId, out var info);
            hermanas.Add(new HermanaDto(c.MeliItemId, c.Title,
                conteo.ContainsKey(c.MeliItemId) ? info.Fotos : -1,
                c.Status, c.Thumbnail, c.CatalogListing || info.Catalogo));
        }
        return hermanas;
    }

    /// <summary>Guarda la lista final ordenada (la primera es la portada). Las que no vengan en la
    /// lista quedan borradas: reordenar, cambiar portada y borrar son la misma operación en MeLi.
    /// Las fotos nuevas llegan como DataUri (base64) y las sube MeliItemService.</summary>
    public async Task<ResultadoDto> GuardarAsync(string meliItemId, List<PictureSpec> fotos,
        CancellationToken ct = default)
    {
        if (fotos is null || fotos.Count == 0)
            return new ResultadoDto(false, "Una publicación no puede quedarse sin fotos.", null);

        var deCatalogo = await _db.MeliItems.AsNoTracking()
            .Where(m => m.MeliItemId == meliItemId && m.VariationId == null)
            .Select(m => (bool?)m.CatalogListing).FirstOrDefaultAsync(ct);
        if (deCatalogo == true)
            return new ResultadoDto(false,
                "Es una publicación de catálogo: MercadoLibre no deja cambiarle las fotos.", null);

        try
        {
            await _itemService.UpdateItemPicturesAsync(meliItemId,
                new UpdateItemPicturesRequest { Pictures = fotos });
            _logger.LogWarning("[Fotos] {Mla}: {N} fotos guardadas", meliItemId, fotos.Count);
            var actualizadas = await LeerAsync(meliItemId, ct);
            return new ResultadoDto(true, $"Listo: quedaron {fotos.Count} fotos.", actualizadas);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Fotos] {Mla}: MeLi rechazó el cambio", meliItemId);
            return new ResultadoDto(false, "MercadoLibre rechazó el cambio: " + Resumir(ex.Message), null);
        }
    }

    /// <summary>Copia las fotos de esta publicación a las hermanas elegidas. Se mandan por id:
    /// como ya están subidas a la misma cuenta, MeLi las reconoce y no hay que re-subirlas.</summary>
    public async Task<ResultadoDto> CopiarAsync(string origenMla, List<string> destinos,
        CancellationToken ct = default)
    {
        if (destinos is null || destinos.Count == 0)
            return new ResultadoDto(false, "No elegiste a qué publicaciones copiarlas.", null);

        var origen = await LeerAsync(origenMla, ct);
        if (origen is null || origen.Fotos.Count == 0)
            return new ResultadoDto(false, "Esta publicación no tiene fotos para copiar.", null);

        var lista = destinos.Distinct().Where(d => !string.IsNullOrWhiteSpace(d) && d != origenMla).ToList();
        var bloqueadas = origen.Hermanas.Where(h => h.DeCatalogo).Select(h => h.MeliItemId).ToHashSet();

        var ids = origen.Fotos.Select(f => new PictureSpec { Id = f.Id }).ToList();
        int ok = 0, err = 0, salteadas = 0;
        var errores = new List<string>();

        foreach (var destino in lista)
        {
            if (ct.IsCancellationRequested) break;
            if (bloqueadas.Contains(destino)) { salteadas++; continue; }
            try
            {
                await _itemService.UpdateItemPicturesAsync(destino,
                    new UpdateItemPicturesRequest { Pictures = ids });
                ok++;
            }
            catch (Exception ex)
            {
                err++;
                errores.Add($"{destino}: {Resumir(ex.Message)}");
                _logger.LogWarning(ex, "[Fotos] No se pudo copiar de {Origen} a {Destino}", origenMla, destino);
            }
            try { await Task.Delay(400, ct); } catch (OperationCanceledException) { break; }
        }

        var texto = $"{origen.Fotos.Count} fotos copiadas a {ok} publicación/es";
        if (salteadas > 0) texto += $" · {salteadas} de catálogo no se pueden tocar";
        if (err > 0) texto += $" · fallaron {err}: {string.Join(" · ", errores.Take(3))}";

        _logger.LogWarning("[Fotos] Copiar desde {Origen}: {Ok} ok, {Err} error, {Skip} salteadas",
            origenMla, ok, err, salteadas);
        return new ResultadoDto(err == 0, texto, await LeerAsync(origenMla, ct));
    }

    /// <summary>Los errores de MeLi vienen con el JSON entero. Cortamos para que se lea.</summary>
    private static string Resumir(string mensaje)
        => mensaje.Length <= 220 ? mensaje : mensaje[..220] + "…";
}
