using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Procesa pedidos que llegan vía WhatsApp (manual paste por ahora, auto-scraping en Fase 2).
/// Parsea texto crudo del vendedor con IA (OpenAI) contra el catálogo de Cafe_Productos
/// y devuelve productos identificados con cantidad/formato.
/// </summary>
public class WhatsAppPedidoService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IntegrationService _integration;
    private readonly ILogger<WhatsAppPedidoService> _log;

    public WhatsAppPedidoService(AppDbContext db, IHttpClientFactory httpFactory, IntegrationService integration, ILogger<WhatsAppPedidoService> log)
    {
        _db = db; _httpFactory = httpFactory; _integration = integration; _log = log;
    }

    public class ProductoDetectado
    {
        public string? Sku { get; set; }
        public string? Nombre { get; set; }
        public int Cantidad { get; set; }
        public string? Formato { get; set; }
        public decimal? PrecioOverride { get; set; }
        public string? Notas { get; set; }
        public double? Confianza { get; set; }
    }

    public class ParseResult
    {
        public string? ClienteNombre { get; set; }
        public int? ClienteId { get; set; }
        public List<ProductoDetectado> Productos { get; set; } = new();
        public string? Comprobante { get; set; }
        public string? FormaEnvio { get; set; }
        public string? FormaPago { get; set; }
        public string? Notas { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>Parsea un texto crudo de WhatsApp y devuelve los productos identificados contra el catálogo.</summary>
    public async Task<ParseResult> ParseTextoAsync(string textoCrudo, CancellationToken ct = default)
    {
        var result = new ParseResult();
        var apiKey = await _integration.GetSecretAsync("openai");
        if (string.IsNullOrEmpty(apiKey))
        {
            result.Error = "OpenAI no configurado. Configurá la API key en /integraciones/openai";
            return result;
        }

        // Cargar catálogo (solo activos + visibles en ventas) — limitamos a campos relevantes
        var catalogo = await _db.CafeProductos
            .Where(p => p.IsActive)
            .Select(p => new { p.Sku, p.Nombre, p.Categoria, p.UxB })
            .OrderBy(p => p.Sku)
            .ToListAsync(ct);

        // Cargar clientes activos (para matchear el nombre del cliente)
        var clientes = await _db.CafeClientes
            .Where(c => c.IsActive)
            .Select(c => new { c.Id, c.Nombre })
            .OrderBy(c => c.Nombre)
            .Take(2000)
            .ToListAsync(ct);

        // Resumir catálogo en un formato compacto para el prompt
        var sbCat = new StringBuilder();
        foreach (var p in catalogo)
        {
            sbCat.AppendLine($"{p.Sku}|{p.Nombre}|{p.Categoria}|UxB={p.UxB?.ToString() ?? "-"}");
        }

        var sbClientes = new StringBuilder();
        foreach (var c in clientes.Take(800)) sbClientes.AppendLine($"{c.Id}|{c.Nombre}");

        var prompt = $@"Sos un experto en pedidos de Palanica Hermanos (distribuidora de café y descartables).
Analizá este pedido recibido por WhatsApp del vendedor y devolvé JSON con los productos identificados.

TEXTO DEL PEDIDO:
````
{textoCrudo.Trim()}
````

CATÁLOGO DE PRODUCTOS (SKU|Nombre|Categoria|UxB):
{sbCat}

CLIENTES (Id|Nombre):
{sbClientes}

REGLAS:
1. Identificá el cliente. Si el pedido empieza con ""#PEDIDO NOMBRE_CLIENTE"" usá ese nombre.
2. Para cada línea de producto:
   - Detectá la cantidad numérica
   - Identificá el SKU del catálogo por similitud al texto del producto
   - Detectá el formato (1KG, MEDIO, CUARTO, UNIT, BULTO, o ""PACK_N"" si dice ""pack de N"")
   - Si dice ""sin cargo"" / ""gratis"" / ""0"" / ""bonificado"", poné PrecioOverride: 0
3. Si el texto menciona ""factura A / B / C / X"" o ""remito"", reportalo en ""comprobante"".
4. Si menciona ""flete"" / ""logístico"" / ""retira"" / ""envío"", reportalo en ""formaEnvio"".
5. Si menciona ""ctacte"" / ""cuenta corriente"" / ""efectivo"" / ""transferencia"" / ""contraentrega"", reportalo en ""formaPago"".
6. Si NO podés identificar un producto, igual incluilo con Sku=null y la mejor descripción que tengas (vamos a revisar manualmente).
7. Asigná una confianza 0.0-1.0 por producto (1.0 = match perfecto, 0.5 = duda).

Respondé ESTRICTAMENTE este JSON (sin markdown, sin texto adicional):
{{
  ""clienteNombre"": ""..."",
  ""clienteId"": 123 o null,
  ""productos"": [
    {{ ""sku"": ""F2"", ""nombre"": ""Café Brasil Premium 1kg"", ""cantidad"": 25, ""formato"": ""1KG"", ""precioOverride"": null, ""notas"": null, ""confianza"": 0.95 }},
    {{ ""sku"": ""CULKRE100"", ""nombre"": ""Cuchara Frikaf"", ""cantidad"": 5, ""formato"": ""UNIT"", ""precioOverride"": 0, ""notas"": ""sin cargo"", ""confianza"": 0.9 }}
  ],
  ""comprobante"": ""FA"" o ""FB"" o ""FC"" o ""FX"" o ""REM"" o null,
  ""formaEnvio"": ""flete"" o ""retira"" o null,
  ""formaPago"": ""ctacte"" o ""efectivo"" o ""transferencia"" o null,
  ""notas"": ""...""
}}";

        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(60);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var body = new
            {
                model = "gpt-4o-mini",
                messages = new[]
                {
                    new { role = "system", content = "Sos un asistente que parsea pedidos. Respondés SOLO JSON válido sin texto adicional." },
                    new { role = "user", content = prompt }
                },
                temperature = 0.1,
                max_tokens = 4000
            };
            var json = JsonSerializer.Serialize(body);
            var resp = await http.PostAsync("https://api.openai.com/v1/chat/completions",
                new StringContent(json, Encoding.UTF8, "application/json"), ct);
            if (!resp.IsSuccessStatusCode)
            {
                result.Error = $"OpenAI error {(int)resp.StatusCode}: {(await resp.Content.ReadAsStringAsync()).Substring(0, Math.Min(200, 200))}";
                return result;
            }
            var rj = await resp.Content.ReadAsStringAsync();
            using var rdoc = JsonDocument.Parse(rj);
            var content = rdoc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}";
            content = content.Trim();
            if (content.StartsWith("```")) content = content.Substring(content.IndexOf('\n') + 1);
            if (content.EndsWith("```")) content = content[..content.LastIndexOf("```")];

            using var parsed = JsonDocument.Parse(content);
            var root = parsed.RootElement;
            if (root.TryGetProperty("clienteNombre", out var cn) && cn.ValueKind == JsonValueKind.String)
                result.ClienteNombre = cn.GetString();
            if (root.TryGetProperty("clienteId", out var ci) && ci.ValueKind == JsonValueKind.Number)
                result.ClienteId = ci.GetInt32();
            if (root.TryGetProperty("comprobante", out var c) && c.ValueKind == JsonValueKind.String)
                result.Comprobante = c.GetString();
            if (root.TryGetProperty("formaEnvio", out var fe) && fe.ValueKind == JsonValueKind.String)
                result.FormaEnvio = fe.GetString();
            if (root.TryGetProperty("formaPago", out var fp) && fp.ValueKind == JsonValueKind.String)
                result.FormaPago = fp.GetString();
            if (root.TryGetProperty("notas", out var n) && n.ValueKind == JsonValueKind.String)
                result.Notas = n.GetString();
            if (root.TryGetProperty("productos", out var prods) && prods.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in prods.EnumerateArray())
                {
                    var prod = new ProductoDetectado
                    {
                        Sku = p.TryGetProperty("sku", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null,
                        Nombre = p.TryGetProperty("nombre", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : null,
                        Cantidad = p.TryGetProperty("cantidad", out var cant) && cant.ValueKind == JsonValueKind.Number ? cant.GetInt32() : 1,
                        Formato = p.TryGetProperty("formato", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null,
                        PrecioOverride = p.TryGetProperty("precioOverride", out var po) && po.ValueKind == JsonValueKind.Number ? po.GetDecimal() : (decimal?)null,
                        Notas = p.TryGetProperty("notas", out var pn) && pn.ValueKind == JsonValueKind.String ? pn.GetString() : null,
                        Confianza = p.TryGetProperty("confianza", out var co) && co.ValueKind == JsonValueKind.Number ? co.GetDouble() : (double?)null,
                    };
                    result.Productos.Add(prod);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error parseando pedido WhatsApp");
            result.Error = "Error procesando con IA: " + ex.Message;
        }
        return result;
    }

    /// <summary>Crea un registro WhatsAppPedidoRecibido en NUEVO y dispara el parseo.</summary>
    public async Task<WhatsAppPedidoRecibido> RecibirPedidoAsync(string telefono, string textoCrudo, string source = "manual", CancellationToken ct = default)
    {
        var pedido = new WhatsAppPedidoRecibido
        {
            Telefono = telefono ?? "",
            TextoCrudo = textoCrudo,
            Source = source,
            Estado = "NUEVO",
            RecibidoAt = DateTime.UtcNow
        };
        _db.WhatsAppPedidosRecibidos.Add(pedido);
        await _db.SaveChangesAsync(ct);

        // Parsear inmediato
        var parsed = await ParseTextoAsync(textoCrudo, ct);
        pedido.ClienteNombre = parsed.ClienteNombre;
        pedido.ClienteId = parsed.ClienteId;
        pedido.ProductosParseados = JsonSerializer.Serialize(parsed);
        pedido.ParseadoAt = DateTime.UtcNow;
        pedido.Estado = string.IsNullOrEmpty(parsed.Error) ? "PARSEADO" : "ERROR";
        pedido.ParseError = parsed.Error;
        await _db.SaveChangesAsync(ct);

        return pedido;
    }

    public Task<int> CountUnseenAsync(CancellationToken ct = default)
        => _db.WhatsAppPedidosRecibidos.AsNoTracking()
            .Where(p => p.SeenAt == null && (p.Estado == "NUEVO" || p.Estado == "PARSEADO"))
            .CountAsync(ct);
}
