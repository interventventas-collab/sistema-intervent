using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Servicio dedicado a REACTIVAR publicaciones MeLi que fueron pausadas erróneamente
/// (típicamente por nuestro push automático que mandó stock=0).
///
/// CONTEXTO 2026-05-23: descubrimos que el background push estaba pusheando stock=0
/// para productos OTROS con StockUnidades=0, lo que causa que MeLi auto-pause la publicación.
/// Se detectaron 142 MLAs en prod en ese estado. Este servicio identifica candidatas y las
/// reactiva con un PUT status=active + available_quantity razonable.
///
/// IMPORTANTE: solo reactiva si:
/// 1. La publicación está marcada como paused en nuestra última sincro.
/// 2. La publicación está vinculada (via MeliItemComponentes) a un producto que pusheamos
///    con stock=0 hoy (LastPushedToMeli >= today AND StockUnidades = 0 AND Categoria = OTROS).
/// Esto evita reactivar publicaciones que el usuario pausó manualmente por otros motivos.
/// </summary>
public class MeliReactivacionService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MeliAccountService _accountService;
    private readonly ILogger<MeliReactivacionService> _logger;

    public MeliReactivacionService(AppDbContext db, IHttpClientFactory httpFactory,
        MeliAccountService accountService, ILogger<MeliReactivacionService> logger)
    {
        _db = db;
        _httpFactory = httpFactory;
        _accountService = accountService;
        _logger = logger;
    }

    public record Candidato(string MeliItemId, string Title, string Sku, int AccountId, string Nickname,
        int CafeProductoId, string CafeProductoSku, string CafeProductoNombre, DateTime LastPushedToMeli);

    public record ReactivacionResult(int Procesadas, int Reactivadas, int YaActivas, int Errores, List<string> Detalles);

    /// <summary>Identifica los MLAs candidatos a reactivar. No modifica nada.</summary>
    public async Task<List<Candidato>> ListarCandidatosAsync(CancellationToken ct = default)
    {
        // Productos OTROS pusheados HOY con stock=0
        var today = DateTime.UtcNow.Date;
        var productosPusheadosEnCero = await _db.CafeProductos
            .Where(p => p.LastPushedToMeli != null
                && p.LastPushedToMeli >= today
                && p.Categoria == "OTROS"
                && p.StockUnidades == 0)
            .Select(p => new { p.Id, p.Sku, p.Nombre, p.LastPushedToMeli })
            .ToListAsync(ct);

        if (productosPusheadosEnCero.Count == 0) return new List<Candidato>();

        var prodIds = productosPusheadosEnCero.Select(p => p.Id).ToList();
        var prodLookup = productosPusheadosEnCero.ToDictionary(p => p.Id);

        // MLAs vinculados a esos productos via componentes
        var meliItemIdsLinkados = await _db.MeliItemComponentes
            .Where(mc => prodIds.Contains(mc.CafeProductoId))
            .Select(mc => new { mc.MeliItemId, mc.CafeProductoId })
            .Distinct()
            .ToListAsync(ct);

        // Filtrar solo los que están "paused" en nuestra última sincro
        var meliIds = meliItemIdsLinkados.Select(x => x.MeliItemId).Distinct().ToList();
        var pausadas = await _db.MeliItems
            .Where(mi => meliIds.Contains(mi.MeliItemId) && mi.Status == "paused")
            .Include(mi => mi.MeliAccount)
            .GroupBy(mi => mi.MeliItemId)
            .Select(g => g.First())
            .ToListAsync(ct);

        var pausadasSet = pausadas.ToDictionary(mi => mi.MeliItemId);

        var resultado = new List<Candidato>();
        foreach (var link in meliItemIdsLinkados)
        {
            if (!pausadasSet.TryGetValue(link.MeliItemId, out var mi)) continue;
            if (!prodLookup.TryGetValue(link.CafeProductoId, out var prod)) continue;

            resultado.Add(new Candidato(
                MeliItemId: mi.MeliItemId,
                Title: mi.Title ?? "",
                Sku: mi.Sku ?? "",
                AccountId: mi.MeliAccountId,
                Nickname: mi.MeliAccount?.Nickname ?? $"Cuenta {mi.MeliAccountId}",
                CafeProductoId: prod.Id,
                CafeProductoSku: prod.Sku ?? "",
                CafeProductoNombre: prod.Nombre ?? "",
                LastPushedToMeli: prod.LastPushedToMeli!.Value
            ));
        }

        return resultado.DistinctBy(c => c.MeliItemId).ToList();
    }

    /// <summary>Reactiva las publicaciones candidatas en MeLi.
    /// Para cada una: PUT status=active + available_quantity=stockSafe.
    /// stockSafe = el available_quantity actual de MeLi (que sigue siendo lo que MeLi muestra
    /// aunque esté pausada) o un default de 1 si no podemos leerlo.
    /// </summary>
    public async Task<ReactivacionResult> ReactivarAsync(List<string>? soloEstosMLAs = null,
        int stockSafeDefault = 1, CancellationToken ct = default)
    {
        var candidatos = await ListarCandidatosAsync(ct);
        if (soloEstosMLAs is not null && soloEstosMLAs.Count > 0)
        {
            var set = new HashSet<string>(soloEstosMLAs);
            candidatos = candidatos.Where(c => set.Contains(c.MeliItemId)).ToList();
        }

        var detalles = new List<string>();
        int ok = 0, yaActiva = 0, err = 0;

        // Cachear tokens por cuenta
        var tokens = new Dictionary<int, string?>();

        foreach (var cand in candidatos)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                if (!tokens.TryGetValue(cand.AccountId, out var token))
                {
                    var acc = await _db.MeliAccounts.FindAsync(new object[] { cand.AccountId }, ct);
                    if (acc is null) { err++; detalles.Add($"{cand.MeliItemId}: cuenta {cand.AccountId} no encontrada"); continue; }
                    token = await _accountService.GetValidTokenAsync(acc);
                    tokens[cand.AccountId] = token;
                }
                if (token is null) { err++; detalles.Add($"{cand.MeliItemId}: token inválido"); continue; }

                using var http = _httpFactory.CreateClient();
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                http.Timeout = TimeSpan.FromSeconds(20);

                // GET para chequear estado actual y leer available_quantity
                var getResp = await http.GetAsync($"https://api.mercadolibre.com/items/{cand.MeliItemId}", ct);
                if (!getResp.IsSuccessStatusCode)
                {
                    err++;
                    detalles.Add($"{cand.MeliItemId}: GET fallido {(int)getResp.StatusCode}");
                    continue;
                }
                var doc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync()).RootElement;
                var statusActual = doc.TryGetProperty("status", out var s) ? s.GetString() : null;

                if (statusActual == "active")
                {
                    yaActiva++;
                    detalles.Add($"{cand.MeliItemId}: ya está active");
                    // Actualizar nuestra DB
                    var miRows = await _db.MeliItems.Where(x => x.MeliItemId == cand.MeliItemId).ToListAsync(ct);
                    foreach (var r in miRows) r.Status = "active";
                    await _db.SaveChangesAsync(ct);
                    continue;
                }

                if (statusActual != "paused")
                {
                    detalles.Add($"{cand.MeliItemId}: estado={statusActual ?? "?"} — saltar");
                    continue;
                }

                // Calcular stockSafe: usar available_quantity actual si > 0, sino el default
                int availActual = 0;
                if (doc.TryGetProperty("available_quantity", out var aq) && aq.ValueKind == JsonValueKind.Number)
                    availActual = aq.GetInt32();
                var stockSafe = availActual > 0 ? availActual : stockSafeDefault;

                // Si tiene variations, hay que mandar la lista de variations también
                Dictionary<string, object> payload;
                if (doc.TryGetProperty("variations", out var variations)
                    && variations.ValueKind == JsonValueKind.Array
                    && variations.GetArrayLength() > 0)
                {
                    var varEntries = new List<object>();
                    foreach (var v in variations.EnumerateArray())
                    {
                        var vid = v.GetProperty("id").GetInt64();
                        int vAq = v.TryGetProperty("available_quantity", out var vaq) && vaq.ValueKind == JsonValueKind.Number
                            ? vaq.GetInt32() : 0;
                        varEntries.Add(new { id = vid, available_quantity = vAq > 0 ? vAq : stockSafeDefault });
                    }
                    payload = new Dictionary<string, object>
                    {
                        ["status"] = "active",
                        ["variations"] = varEntries
                    };
                }
                else
                {
                    payload = new Dictionary<string, object>
                    {
                        ["status"] = "active",
                        ["available_quantity"] = stockSafe
                    };
                }

                var putBody = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var putResp = await http.PutAsync($"https://api.mercadolibre.com/items/{cand.MeliItemId}", putBody, ct);

                if (putResp.IsSuccessStatusCode)
                {
                    ok++;
                    detalles.Add($"✅ {cand.MeliItemId} ({cand.Sku}) reactivada");
                    // Actualizar nuestra DB
                    var miRows = await _db.MeliItems.Where(x => x.MeliItemId == cand.MeliItemId).ToListAsync(ct);
                    foreach (var r in miRows) r.Status = "active";
                    await _db.SaveChangesAsync(ct);
                }
                else
                {
                    err++;
                    var body = await putResp.Content.ReadAsStringAsync();
                    detalles.Add($"❌ {cand.MeliItemId}: PUT {(int)putResp.StatusCode} — {body.Substring(0, Math.Min(120, body.Length))}");
                }
            }
            catch (Exception ex)
            {
                err++;
                detalles.Add($"❌ {cand.MeliItemId}: {ex.Message}");
                _logger.LogError(ex, "Error reactivando {MeliItemId}", cand.MeliItemId);
            }
        }

        return new ReactivacionResult(candidatos.Count, ok, yaActiva, err, detalles);
    }
}
