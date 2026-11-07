using System.Text.Json;
using System.Text.RegularExpressions;

namespace Api.Services;

/// <summary>
/// 2026-11-06: pedido del dueño — en el listado de ventas de MeLi se ve UNA sola nota por venta, así que
/// cuando el sistema dejaba varias (ganancia, vencimiento, teléfono, Maps) solo veía una u otra.
/// Ahora el sistema deja UNA sola nota con todo junto: "💲 ganancia ‖ 📅 VENCE … ‖ Tel cliente: …".
/// Cada vez que se agrega una parte, se leen las notas del sistema que ya tiene la venta, se arma la nota
/// nueva con todas las partes, se postea, y recién después se borran las viejas del sistema.
/// Las coordenadas ("Maps: …") ya no se ponen y se descartan al juntar. Las notas que escribe el dueño a
/// mano en MeLi NO se tocan (solo se reconocen como del sistema las que empiezan con 💲, 📅, Tel cliente: o Maps:).
/// </summary>
public static class MeliNotaUnica
{
    public const string Ganancia = "GAN";
    public const string Vence = "VENC";
    public const string Telefono = "TEL";
    private const string Maps = "MAPS";
    public const string Separador = " ‖ ";
    private const int Max = 300;   // límite de MeLi para una nota
    private static readonly string[] Orden = { Ganancia, Vence, Telefono };

    // El sistema escribe notas desde distintos lugares (robot de notas, sync de envíos, botón "traer teléfono"):
    // de a una por vez para que dos no armen la nota al mismo tiempo y se pisen.
    private static readonly SemaphoreSlim Candado = new(1, 1);

    private static string? TipoDe(string parte)
    {
        var p = parte.TrimStart();
        if (p.StartsWith("💲")) return Ganancia;
        if (p.StartsWith("📅")) return Vence;
        if (p.StartsWith("Tel cliente:")) return Telefono;
        if (p.StartsWith("Maps:")) return Maps;
        return null;
    }

    /// <summary>Pone (o reemplaza) una parte de la nota única de la orden. Devuelve false si MeLi no contestó bien.</summary>
    public static async Task<bool> PonerAsync(HttpClient http, long orderId, string tipo, string texto, string tag, CancellationToken ct = default)
    {
        await Candado.WaitAsync(ct);
        try
        {
            var (ok, notas) = await LeerAsync(http, orderId, ct);
            if (!ok) { Console.WriteLine($"[{tag}] order {orderId}: no se pudieron leer las notas"); return false; }

            var partes = new Dictionary<string, string>();
            var delSistema = new List<string>();   // ids de notas del sistema a borrar después
            foreach (var (id, nota) in notas)       // vienen de la más vieja a la más nueva: la más nueva gana
            {
                var trozos = nota.Split(Separador.Trim(), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var tipos = trozos.Select(TipoDe).ToList();
                if (trozos.Length == 0 || tipos.Any(t => t is null)) continue;   // nota escrita a mano: no se toca
                delSistema.Add(id);
                for (var i = 0; i < trozos.Length; i++)
                    if (tipos[i] != Maps) partes[tipos[i]!] = trozos[i];
            }
            partes[tipo] = texto.Trim();

            var final = Armar(partes);
            // Si ya está exactamente esa nota y es la única del sistema, no hay nada que hacer.
            if (delSistema.Count == 1 && notas.First(n => n.Id == delSistema[0]).Nota.Trim() == final) return true;

            if (!await PostAsync(http, orderId, final, tag, ct)) return false;
            foreach (var id in delSistema)
            {
                try
                {
                    using var r = await http.DeleteAsync($"https://api.mercadolibre.com/orders/{orderId}/notes/{id}", ct);
                    if (!r.IsSuccessStatusCode) Console.WriteLine($"[{tag}] order {orderId} no se pudo borrar nota vieja {id}: {(int)r.StatusCode}");
                }
                catch (Exception ex) { Console.WriteLine($"[{tag}] order {orderId} borrar nota {id}: {ex.Message}"); }
            }
            return true;
        }
        finally { Candado.Release(); }
    }

    /// <summary>Junta las partes en el orden ganancia · vencimiento · teléfono, achicando la ganancia si no entra en 300.</summary>
    public static string Armar(Dictionary<string, string> partes)
    {
        string Unir() => string.Join(Separador, Orden.Where(partes.ContainsKey).Select(t => partes[t]));
        var txt = Unir();
        if (txt.Length <= Max || !partes.ContainsKey(Ganancia)) return txt.Length <= Max ? txt : txt[..Max];
        // 1º se saca el sugerido de fábrica, 2º el detalle del envío Flex, 3º se corta la ganancia
        partes[Ganancia] = Regex.Replace(partes[Ganancia], @" · sug\. fábrica \$[\d\.]+", "");
        txt = Unir();
        if (txt.Length <= Max) return txt;
        partes[Ganancia] = Regex.Replace(partes[Ganancia], @" \((lo pagó el cliente|bonifica MeLi|cliente [^)]*)\)", "");
        txt = Unir();
        if (txt.Length <= Max) return txt;
        var resto = txt.Length - partes[Ganancia].Length;
        var lugar = Math.Max(20, Max - resto - 1);
        partes[Ganancia] = partes[Ganancia].Length > lugar ? partes[Ganancia][..lugar] + "…" : partes[Ganancia];
        txt = Unir();
        return txt.Length <= Max ? txt : txt[..Max];
    }

    private static async Task<(bool Ok, List<(string Id, string Nota)> Notas)> LeerAsync(HttpClient http, long orderId, CancellationToken ct)
    {
        var res = new List<(string Id, string Nota, DateTimeOffset Fecha)>();
        try
        {
            using var resp = await http.GetAsync($"https://api.mercadolibre.com/orders/{orderId}/notes", ct);
            if (!resp.IsSuccessStatusCode) return (false, new());
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var bloques = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : new List<JsonElement> { root };
            foreach (var b in bloques)
            {
                if (!b.TryGetProperty("results", out var rs) || rs.ValueKind != JsonValueKind.Array) continue;
                foreach (var n in rs.EnumerateArray())
                {
                    var texto = n.TryGetProperty("note", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    if (string.IsNullOrWhiteSpace(texto) || !n.TryGetProperty("id", out var idEl)) continue;
                    var id = idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : idEl.GetRawText();
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    var fecha = n.TryGetProperty("date_created", out var dc) && dc.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(dc.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var dto) ? dto : DateTimeOffset.MinValue;
                    res.Add((id!, texto!, fecha));
                }
            }
            return (true, res.OrderBy(x => x.Fecha).Select(x => (x.Id, x.Nota)).ToList());
        }
        catch { return (false, new()); }
    }

    private static async Task<bool> PostAsync(HttpClient http, long orderId, string nota, string tag, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { note = nota });
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var resp = await http.PostAsync($"https://api.mercadolibre.com/orders/{orderId}/notes", content, ct);
        if (resp.IsSuccessStatusCode) return true;
        var err = await resp.Content.ReadAsStringAsync(ct);
        Console.WriteLine($"[{tag}] order {orderId} {(int)resp.StatusCode}: {err[..Math.Min(err.Length, 200)]}");
        return false;
    }
}
