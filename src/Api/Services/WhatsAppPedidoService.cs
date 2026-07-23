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

    /// <summary>Parsea un texto crudo de WhatsApp y devuelve los productos identificados contra el catálogo.
    /// Usa Claude (Anthropic) como motor IA. Si no está configurado, intenta OpenAI como fallback.</summary>
    public async Task<ParseResult> ParseTextoAsync(string textoCrudo, CancellationToken ct = default)
    {
        var result = new ParseResult();
        // Priorizar Claude (Anthropic), fallback a OpenAI
        var anthropicKey = await _integration.GetSecretAsync("anthropic");
        var openaiKey = string.IsNullOrEmpty(anthropicKey) ? await _integration.GetSecretAsync("openai") : null;
        if (string.IsNullOrEmpty(anthropicKey) && string.IsNullOrEmpty(openaiKey))
        {
            result.Error = "Sin IA configurada. Cargá la API key de Claude (Anthropic) o OpenAI en /integraciones";
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
0. El texto puede empezar con un código de control: ""##"", ""#NUMERO"", ""XC"", ""XP"" o ""XF"" (mayúscula o minúscula), a veces seguido de un número que es el CÓDIGO INTERNO del cliente. Ese encabezado NO es un producto ni una cantidad: ignoralo por completo al detectar productos.
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

            string content;
            if (!string.IsNullOrEmpty(anthropicKey))
            {
                // === CLAUDE (Anthropic) ===
                http.DefaultRequestHeaders.Add("x-api-key", anthropicKey);
                http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

                var body = new
                {
                    model = "claude-haiku-4-5-20251001",
                    max_tokens = 4000,
                    system = "Sos un asistente que parsea pedidos de WhatsApp para una distribuidora. Respondés EXCLUSIVAMENTE con JSON valido sin texto adicional ni markdown.",
                    messages = new[] { new { role = "user", content = prompt } }
                };
                var json = JsonSerializer.Serialize(body);
                var resp = await http.PostAsync("https://api.anthropic.com/v1/messages",
                    new StringContent(json, Encoding.UTF8, "application/json"), ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var rb = await resp.Content.ReadAsStringAsync();
                    result.Error = $"Anthropic error {(int)resp.StatusCode}: {rb.Substring(0, Math.Min(300, rb.Length))}";
                    return result;
                }
                var rj = await resp.Content.ReadAsStringAsync();
                using var rdoc = JsonDocument.Parse(rj);
                // Claude devuelve content como array: [{ type: "text", text: "..." }]
                var contentArr = rdoc.RootElement.GetProperty("content");
                content = "";
                foreach (var block in contentArr.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var tp) && tp.GetString() == "text"
                        && block.TryGetProperty("text", out var tx)) { content = tx.GetString() ?? ""; break; }
                }
            }
            else
            {
                // === OpenAI (fallback) ===
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", openaiKey);
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
                    var rb = await resp.Content.ReadAsStringAsync();
                    result.Error = $"OpenAI error {(int)resp.StatusCode}: {rb.Substring(0, Math.Min(300, rb.Length))}";
                    return result;
                }
                var rj = await resp.Content.ReadAsStringAsync();
                using var rdoc = JsonDocument.Parse(rj);
                content = rdoc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}";
            }

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

    /// <summary>Crea un registro WhatsAppPedidoRecibido en estado NUEVO con SOLO el texto crudo.
    /// MODO SIMPLE 2026-05-23: no intenta parsear con IA. El usuario lee el texto y carga la venta a mano.
    /// (El parseo IA queda disponible en ParseTextoAsync para uso futuro cuando configure Claude/OpenAI.)
    ///
    /// DETECCION DE CLIENTE 2026-05-24: si el texto contiene un patron #NUMERO (ej "#PED #134"),
    /// busca CafeCliente con ese CodigoInterno y lo asocia automaticamente al pedido.</summary>
    public async Task<WhatsAppPedidoRecibido> RecibirPedidoAsync(string telefono, string textoCrudo, string source = "manual", CancellationToken ct = default, int? clienteIdVinculado = null)
    {
        var pedido = new WhatsAppPedidoRecibido
        {
            Telefono = telefono ?? "",
            TextoCrudo = textoCrudo,
            Source = source,
            Estado = "NUEVO",
            RecibidoAt = DateTime.UtcNow,
            // 2026-07-23: XC/XP/XF marcan que documento quiere el que escribio
            TipoSolicitado = DetectarTipoSolicitado(textoCrudo)
        };

        // Detectar codigo de cliente: buscar #NUMERO en cualquier lugar del texto.
        // Excluir el #PED del trigger para no matchear con eso.
        var (clienteId, clienteNombre) = await TryDetectarClienteAsync(textoCrudo, ct);
        if (clienteId.HasValue)
        {
            pedido.ClienteId = clienteId;
            pedido.ClienteNombre = clienteNombre;
        }
        // 2026-07-23: si el que llama ya sabe el cliente (ej. contacto del chat vinculado a un
        // cliente), usarlo como respaldo cuando el texto no trae codigo.
        else if (clienteIdVinculado.HasValue && clienteIdVinculado.Value > 0)
        {
            var cli = await _db.CafeClientes.AsNoTracking()
                .Where(c => c.Id == clienteIdVinculado.Value && c.IsActive)
                .Select(c => new { c.Id, c.Nombre })
                .FirstOrDefaultAsync(ct);
            if (cli is not null) { pedido.ClienteId = cli.Id; pedido.ClienteNombre = cli.Nombre; }
        }

        _db.WhatsAppPedidosRecibidos.Add(pedido);
        await _db.SaveChangesAsync(ct);

        // 2026-07-23 (unificación): la IA lee el pedido apenas entra — antes solo corría con el
        // botón "re-parsear" y en la práctica nunca se usaba. Respetá el interruptor de la
        // pantalla (whatsapp.pedidos.ia_enabled). Si falla, el pedido queda NUEVO igual.
        try { await ParsearYGuardarAsync(pedido, ct); }
        catch (Exception ex) { _log.LogError(ex, "Error parseando pedido {Id} con IA", pedido.Id); }

        return pedido;
    }

    /// <summary>Interruptor de la IA lectora de pedidos (lo maneja el usuario desde la pantalla
    /// Pedidos WhatsApp). Default: prendida.</summary>
    public async Task<bool> IaEnabledAsync(CancellationToken ct = default)
    {
        var s = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == "whatsapp.pedidos.ia_enabled", ct);
        return s is null || string.Equals(s.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Corre la IA sobre un pedido ya guardado y persiste el resultado (misma lógica que
    /// el botón re-parsear). Si el texto traía código de cliente, ese cliente MANDA: no se pisa
    /// con lo que adivine la IA. Con el interruptor apagado no hace nada.</summary>
    public async Task ParsearYGuardarAsync(WhatsAppPedidoRecibido pedido, CancellationToken ct = default)
    {
        if (!await IaEnabledAsync(ct)) return;

        var parsed = await ParseTextoAsync(pedido.TextoCrudo, ct);
        if (!pedido.ClienteId.HasValue)
        {
            pedido.ClienteId = parsed.ClienteId;
            pedido.ClienteNombre = parsed.ClienteNombre;
        }
        pedido.ProductosParseados = System.Text.Json.JsonSerializer.Serialize(parsed);
        pedido.ParseadoAt = DateTime.UtcNow;
        pedido.Estado = string.IsNullOrEmpty(parsed.Error) ? "PARSEADO" : "ERROR";
        pedido.ParseError = parsed.Error;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Intenta detectar el cliente del pedido buscando #NUMERO en el texto, donde NUMERO
    /// es el CodigoInterno de un CafeCliente activo. Devuelve (Id, Nombre) si encuentra match.
    /// IMPORTANTE: si el mensaje empieza con `##` se considera "venta ciega" y NO se detecta cliente.</summary>
    private async Task<(int? Id, string? Nombre)> TryDetectarClienteAsync(string textoCrudo, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(textoCrudo)) return (null, null);
        var trimmed = textoCrudo.TrimStart();
        // Si empieza con `##` es venta ciega (sin cliente)
        if (trimmed.StartsWith("##")) return (null, null);

        // 2026-07-23: con los triggers XC/XP/XF, el numero pegado a las letras (o separado por
        // espacio) es el codigo interno del cliente. Ej: "XF 105 2 bultos..." o "xc105 ...".
        var mx = System.Text.RegularExpressions.Regex.Match(trimmed, @"^[xX][cCpPfF]\s*(\d+)\b");
        if (mx.Success && int.TryParse(mx.Groups[1].Value, out var codigoX))
        {
            var cliX = await _db.CafeClientes.AsNoTracking()
                .Where(c => c.IsActive && c.CodigoInterno == codigoX)
                .Select(c => new { c.Id, c.Nombre })
                .FirstOrDefaultAsync(ct);
            if (cliX is not null) return (cliX.Id, cliX.Nombre);
        }

        // Regex: # seguido SOLO de digitos (ignorar # seguido de letras como #PED y `##` que ya filtramos arriba)
        var matches = System.Text.RegularExpressions.Regex.Matches(textoCrudo, @"#(\d+)\b");
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            if (!int.TryParse(m.Groups[1].Value, out var codigo)) continue;
            var cliente = await _db.CafeClientes.AsNoTracking()
                .Where(c => c.IsActive && c.CodigoInterno == codigo)
                .Select(c => new { c.Id, c.Nombre })
                .FirstOrDefaultAsync(ct);
            if (cliente is not null) return (cliente.Id, cliente.Nombre);
        }
        return (null, null);
    }

    /// <summary>Verifica si un texto es un pedido valido. Triggers aceptados al inicio del mensaje:
    /// `##` o `#NUMERO` (pedido comun), y 2026-07-23 pedido Osmar: `XC` (cotizacion),
    /// `XP` (presupuesto PRO) y `XF` (factura), en mayuscula o minuscula, opcionalmente
    /// seguidos del codigo interno del cliente (ej: "xf 105 2 bultos cafe...").
    /// Se exige separador (espacio o fin) despues de las 2 letras para no confundir con
    /// palabras que empiecen igual.</summary>
    public static bool EsTriggerValido(string? textoCrudo)
    {
        if (string.IsNullOrWhiteSpace(textoCrudo)) return false;
        var t = textoCrudo.TrimStart();
        if (t.StartsWith("##")) return true;
        // `#` seguido inmediatamente de digito
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^#\d+\b")) return true;
        // XC / XP / XF seguidos de espacio, numero o fin de texto
        return System.Text.RegularExpressions.Regex.IsMatch(t, @"^[xX][cCpPfF](\s|\d|$)");
    }

    /// <summary>Que tipo de documento pidio el que escribio, segun el trigger del mensaje:
    /// XC = COTIZACION (comprobante X) · XP = PRESUPUESTO (PRO) · XF = FACTURA (ARCA con OK humano)
    /// · ##/#NUMERO = PEDIDO comun (como siempre).</summary>
    public static string DetectarTipoSolicitado(string? textoCrudo)
    {
        if (string.IsNullOrWhiteSpace(textoCrudo)) return "PEDIDO";
        var t = textoCrudo.TrimStart();
        var m = System.Text.RegularExpressions.Regex.Match(t, @"^[xX]([cCpPfF])(\s|\d|$)");
        if (!m.Success) return "PEDIDO";
        return char.ToUpperInvariant(m.Groups[1].Value[0]) switch
        {
            'C' => "COTIZACION",
            'P' => "PRESUPUESTO",
            'F' => "FACTURA",
            _ => "PEDIDO"
        };
    }

    public Task<int> CountUnseenAsync(CancellationToken ct = default)
        => _db.WhatsAppPedidosRecibidos.AsNoTracking()
            .Where(p => p.SeenAt == null && (p.Estado == "NUEVO" || p.Estado == "PARSEADO"))
            .CountAsync(ct);
}
