using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Web.Services;

/// <summary>
/// 2026-10-28 — Buscador de localidades para las fichas de cliente.
/// Lista OFICIAL (Georef, datos.gob.ar; CABA por barrio) + los CP de cps-ar.json, armada por
/// scripts/armar-localidades-ar.py en /data/localidades-ar.json. Encima se suman las localidades
/// que ya usan los clientes (endpoint localidades-usadas): salen primero y le "enseñan" los CP
/// que la lista oficial no trae. Sólo SUGIERE: nunca cambia nada que no se elija.
/// </summary>
public class LocalidadesService
{
    public const string Caba = "Ciudad Autónoma de Buenos Aires";

    public class Opcion
    {
        public string Nombre { get; init; } = "";
        public string Provincia { get; init; } = "";
        public string Partido { get; init; } = "";
        public List<string> Cps { get; init; } = new();
        public int Clientes { get; set; }
        internal string Clave { get; init; } = "";

        /// <summary>"Palermo · CABA", "General Pacheco · Tigre, Buenos Aires", "Tigre · Buenos Aires".</summary>
        public string Detalle
        {
            get
            {
                var prov = Provincia == Caba ? "CABA" : Provincia;
                if (Nombre == "CABA") return "Capital, sin barrio";
                if (!string.IsNullOrEmpty(Partido) && Norm(Partido) != Clave) return $"{Partido}, {prov}";
                return prov;
            }
        }
        public string? CpUnico => Cps.Count == 1 ? Cps[0] : null;
    }

    private readonly HttpClient _http;
    private readonly ApiClient _api;
    private List<Opcion>? _todas;
    private Task? _carga;

    public LocalidadesService(HttpClient http, ApiClient api) { _http = http; _api = api; }

    public Task EnsureAsync() => _todas is not null ? Task.CompletedTask : (_carga ??= CargarAsync());

    private async Task CargarAsync()
    {
        var lista = new List<Opcion>();
        try
        {
            var filas = await _http.GetFromJsonAsync<List<List<string>>>("data/localidades-ar.json") ?? new();
            foreach (var f in filas)
            {
                if (f.Count < 4) continue;
                lista.Add(new Opcion
                {
                    Nombre = f[0], Provincia = f[1], Partido = f[2], Clave = Norm(f[0]),
                    Cps = f[3].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
                });
            }
        }
        catch { }
        // Capital a secas, para cuando no se sabe el barrio.
        lista.Add(new Opcion { Nombre = "CABA", Provincia = Caba, Clave = "caba" });

        try
        {
            var usadas = await _api.GetLocalidadesUsadasAsync<List<Usada>>() ?? new();
            var provincias = lista.Select(o => o.Provincia).Distinct().ToDictionary(Norm, p => p);
            var porClave = lista.GroupBy(o => o.Clave).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var u in usadas)
            {
                var clave = Alias(Norm(u.Localidad));
                var prov = provincias.GetValueOrDefault(Norm(u.Provincia)) ?? (Norm(u.Provincia) is "caba" or "capital federal" ? Caba : null);
                var cp = (u.Cp ?? "").Trim();
                var cpOk = cp.Length == 4 && cp.All(char.IsDigit);
                if (porClave.TryGetValue(clave, out var iguales))
                {
                    // Con el mismo nombre en varias provincias: la de su provincia; si no, BA/CABA.
                    var o = iguales.FirstOrDefault(x => x.Provincia == prov)
                            ?? iguales.FirstOrDefault(x => x.Provincia is "Buenos Aires" or Caba)
                            ?? iguales[0];
                    o.Clientes += u.Clientes;
                    if (cpOk && o.Cps.Count == 0 && (prov is null || prov == o.Provincia)) o.Cps.Add(cp);
                }
                else if (u.Clientes >= 2 && u.Localidad.Length <= 40 && !u.Localidad.Any(c => char.IsDigit(c) || c is ',' or '.' or '('))
                {
                    // No está en la oficial pero la usan varios clientes (ej. Nordelta): se suma.
                    var nueva = new Opcion
                    {
                        Nombre = Titulo(u.Localidad.Trim()), Provincia = prov ?? "", Clave = clave,
                        Cps = cpOk ? new() { cp } : new(), Clientes = u.Clientes,
                    };
                    lista.Add(nueva);
                    porClave[clave] = new() { nueva };
                }
            }
        }
        catch { }
        _todas = lista;
    }

    private class Usada
    {
        public string Localidad { get; set; } = "";
        public string? Provincia { get; set; }
        public string? Cp { get; set; }
        public int Clientes { get; set; }
    }

    /// <summary>Las que coinciden con lo escrito: primero las que ya usan tus clientes, después las
    /// que empiezan igual (BA y CABA adelante), después las que lo contienen.</summary>
    public List<Opcion> Buscar(string? texto, int max = 8)
    {
        var t = Alias(Norm(texto));
        if (_todas is null || t.Length < 2) return new();
        return _todas
            .Select(o => new { o, pos = o.Clave.IndexOf(t, StringComparison.Ordinal) })
            .Select(x => new { x.o, x.pos, tipo = x.pos == 0 ? 0 : x.pos > 0 && !char.IsLetterOrDigit(x.o.Clave[x.pos - 1]) ? 1 : 2 })
            .Where(x => x.pos >= 0 && (x.tipo < 2 || t.Length >= 4))
            .OrderByDescending(x => x.o.Clientes > 0)
            .ThenByDescending(x => x.o.Clientes)
            .ThenBy(x => x.tipo == 2 ? 1 : 0)
            .ThenBy(x => x.o.Provincia is "Buenos Aires" or Caba ? 0 : 1)
            .ThenBy(x => x.tipo)
            .ThenBy(x => x.o.Nombre.Length)
            .Take(max)
            .Select(x => x.o)
            .ToList();
    }

    /// <summary>¿Ese CP es de alguna localidad que no sea esta? (para no dejar el CP de la anterior).</summary>
    public bool CpEsDeOtra(string? cp, Opcion o) => PorCp(cp).Any(x => x != o);

    /// <summary>Las localidades de un CP de 4 números (puede haber varias).</summary>
    public List<Opcion> PorCp(string? cp)
    {
        cp = (cp ?? "").Trim();
        if (_todas is null || cp.Length != 4) return new();
        return _todas.Where(o => o.Cps.Contains(cp))
            .OrderByDescending(o => o.Clientes).ThenBy(o => o.Nombre).Take(8).ToList();
    }

    /// <summary>La opción que corresponde a un texto ya cargado (para mostrar su detalle).</summary>
    public Opcion? Exacta(string? nombre, string? provincia = null)
    {
        var c = Alias(Norm(nombre));
        if (_todas is null || c == "") return null;
        var iguales = _todas.Where(o => o.Clave == c).ToList();
        return iguales.FirstOrDefault(o => Norm(o.Provincia) == Norm(provincia)) ?? (iguales.Count == 1 ? iguales[0] : null);
    }

    private static string Alias(string c) => c is "capital federal" or "ciudad autonoma de buenos aires" or "capital" ? "caba" : c;

    public static string Norm(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder();
        foreach (var ch in s.Trim().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(ch));
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Titulo(string s) => s == s.ToUpperInvariant()
        ? CultureInfo.GetCultureInfo("es-AR").TextInfo.ToTitleCase(s.ToLowerInvariant()) : s;
}
