using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Data;
using Api.DTOs;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Asistente conversacional que responde preguntas sobre los datos del ERP.
/// Usa Claude Haiku 4.5 con tool-use: el modelo elige qué herramienta llamar,
/// el servidor la ejecuta sobre la DB y le devuelve el resultado, hasta que el
/// modelo da una respuesta final en lenguaje natural.
///
/// Las herramientas son SOLO LECTURA — el asistente no puede crear, modificar
/// ni borrar datos en esta versión.
/// </summary>
public class AssistantService
{
    private readonly IntegrationService _integrations;
    private readonly IHttpClientFactory _httpFactory;
    private readonly AppDbContext _db;
    private readonly ILogger<AssistantService> _logger;

    private const string MODEL = "claude-haiku-4-5-20251001";
    private const string API_URL = "https://api.anthropic.com/v1/messages";
    private const int MAX_TOOL_LOOPS = 8;

    public AssistantService(
        IntegrationService integrations,
        IHttpClientFactory httpFactory,
        AppDbContext db,
        ILogger<AssistantService> logger)
    {
        _integrations = integrations;
        _httpFactory = httpFactory;
        _db = db;
        _logger = logger;
    }

    public async Task<AssistantChatResponse> ChatAsync(List<AssistantChatMessage> history)
    {
        // 1. API key
        var apiKey = await _integrations.GetSecretAsync("anthropic");
        if (string.IsNullOrEmpty(apiKey))
        {
            apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        }
        if (string.IsNullOrEmpty(apiKey))
        {
            return new AssistantChatResponse
            {
                Configured = false,
                Reply = "El asistente no está configurado todavía. Cargá la API Key de Anthropic en Administración → Integraciones."
            };
        }

        if (history is null || history.Count == 0)
        {
            return new AssistantChatResponse { Reply = "Hola! Preguntame lo que necesites." };
        }

        // 2. Armar el array de messages para Anthropic.
        // El último mensaje del usuario es el que querés que conteste.
        // Los anteriores van como contexto.
        var messages = new JsonArray();
        foreach (var m in history)
        {
            var role = m.Role == "assistant" ? "assistant" : "user";
            messages.Add(new JsonObject
            {
                ["role"] = role,
                ["content"] = m.Content ?? ""
            });
        }

        // 3. Tool loop.
        var trace = new List<AssistantToolCallTrace>();
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        for (int loop = 0; loop < MAX_TOOL_LOOPS; loop++)
        {
            var requestBody = new JsonObject
            {
                ["model"] = MODEL,
                ["max_tokens"] = 1024,
                ["system"] = SystemPrompt(),
                ["tools"] = ToolDefinitions(),
                // Clonamos messages: en la 2da vuelta (tool-use) no se puede reasignar
                // el MISMO nodo como hijo de otro requestBody (JsonNode solo admite un padre).
                ["messages"] = JsonNode.Parse(messages.ToJsonString())!
            };

            using var content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(API_URL, content);
            var respBody = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Anthropic respondió {Status}: {Body}", resp.StatusCode, respBody);
                return new AssistantChatResponse
                {
                    Reply = $"Error consultando al asistente ({(int)resp.StatusCode}). Reintentá en un rato.",
                    Error = respBody.Length > 500 ? respBody.Substring(0, 500) : respBody,
                    ToolCalls = trace
                };
            }

            var doc = JsonNode.Parse(respBody)!.AsObject();
            var stopReason = doc["stop_reason"]?.GetValue<string>();
            var contentBlocks = doc["content"]?.AsArray() ?? new JsonArray();

            // Si el modelo NO pidió tools, devolvemos el texto.
            if (stopReason != "tool_use")
            {
                var text = ExtractText(contentBlocks);
                return new AssistantChatResponse { Reply = text, ToolCalls = trace };
            }

            // Hay tool_use → ejecutar cada una y agregar el assistant turn + un user turn con resultados.
            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = JsonNode.Parse(contentBlocks.ToJsonString())!
            });

            var toolResults = new JsonArray();
            foreach (var block in contentBlocks)
            {
                var b = block!.AsObject();
                if (b["type"]?.GetValue<string>() != "tool_use") continue;
                var toolName = b["name"]?.GetValue<string>() ?? "";
                var toolUseId = b["id"]?.GetValue<string>() ?? "";
                var input = b["input"]?.AsObject() ?? new JsonObject();
                string result;
                try
                {
                    result = await ExecuteToolAsync(toolName, input);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error ejecutando tool {Tool}", toolName);
                    result = JsonSerializer.Serialize(new { error = ex.Message });
                }
                trace.Add(new AssistantToolCallTrace
                {
                    Tool = toolName,
                    Args = input.ToJsonString(),
                    Result = result.Length > 1500 ? result.Substring(0, 1500) + "…" : result
                });
                toolResults.Add(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = toolUseId,
                    ["content"] = result
                });
            }

            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = toolResults
            });
        }

        return new AssistantChatResponse
        {
            Reply = "Disculpá, la consulta requirió demasiados pasos y la corté para no demorar más. Probá hacer una pregunta más específica.",
            ToolCalls = trace
        };
    }

    private static string ExtractText(JsonArray contentBlocks)
    {
        var sb = new StringBuilder();
        foreach (var block in contentBlocks)
        {
            var b = block!.AsObject();
            if (b["type"]?.GetValue<string>() == "text")
            {
                sb.Append(b["text"]?.GetValue<string>() ?? "");
            }
        }
        return sb.ToString().Trim();
    }

    private static string SystemPrompt() => @"Sos el asistente de Cafe Frikaf, integrado en su sistema. Respondés en castellano rioplatense, con buena onda, claro y al punto.

Ayudás con DOS tipos de cosas:

1) DATOS DEL SISTEMA — deudas de clientes, stock de productos, comprobantes/ventas, sueldos de empleados. Para esto SIEMPRE usá las herramientas: NUNCA inventes números, nombres ni IDs. Si no hay una herramienta para ese dato puntual, decilo con sinceridad.

2) CUALQUIER OTRA COSA — redactar un WhatsApp o un mail, traducir, corregir o mejorar un texto, resumir, dar ideas, explicar algo, hacer una cuenta, o simplemente charlar. Para esto respondé como un asistente general útil (tipo ChatGPT), sin herramientas.

REGLAS:
- Elegí bien: si la pregunta es sobre datos concretos del negocio, herramientas sí o sí. Si es general, respondé directo sin herramientas.
- El sistema gestiona: catálogo de productos (incluye café por kg), ventas/comprobantes, clientes (cuentas corrientes), alquileres de equipos para eventos, nóminas/sueldos de empleados.
- Formateá montos en pesos argentinos con punto de miles y coma decimal: $1.234.567,89.
- Sólo lectura sobre el sistema: no podés crear, modificar ni borrar datos. Si te piden cambiar algo del sistema, decí que no podés y dales el camino manual (ej: 'andá a Ventas → tal').
- Si una búsqueda de datos devuelve varios matches, mencionalos brevemente y pedí que elija el correcto.
- Sé conciso y amable. No expliques lo que vas a hacer; hacelo y respondé.
- No reveles detalles internos del sistema (tablas, ids exactos, JSON). Hablá en lenguaje humano.";

    private static JsonArray ToolDefinitions()
    {
        return new JsonArray
        {
            ToolDef("buscar_cliente",
                "Busca clientes del ERP por nombre o parte del nombre. Devuelve hasta 5 que matcheen.",
                new JsonObject { ["nombre"] = ObjStr("Texto a buscar en el nombre del cliente.") },
                new JsonArray { "nombre" }),

            ToolDef("ver_deuda_cliente",
                "Suma todos los comprobantes pendientes de pago de un cliente y devuelve el total + el detalle de los pendientes.",
                new JsonObject { ["clienteId"] = ObjInt("ID del cliente (lo obtenés con buscar_cliente).") },
                new JsonArray { "clienteId" }),

            ToolDef("ver_ultimo_comprobante_cliente",
                "Devuelve los datos del último comprobante (venta) emitido a un cliente: número, fecha, total, estado.",
                new JsonObject { ["clienteId"] = ObjInt("ID del cliente.") },
                new JsonArray { "clienteId" }),

            ToolDef("buscar_producto",
                "Busca productos por SKU, código de barras o parte del nombre. Devuelve hasta 5 con stock y precio.",
                new JsonObject { ["busqueda"] = ObjStr("SKU, código o nombre.") },
                new JsonArray { "busqueda" }),

            ToolDef("buscar_empleado_nominas",
                "Busca empleados del módulo de Nóminas por nombre.",
                new JsonObject { ["nombre"] = ObjStr("Nombre o parte del nombre del empleado.") },
                new JsonArray { "nombre" }),

            ToolDef("ver_sueldo_pendiente_empleado",
                "Suma los saldos pendientes de las liquidaciones del empleado y devuelve el total + detalle por liquidación.",
                new JsonObject { ["empleadoId"] = ObjInt("ID del empleado de Nóminas.") },
                new JsonArray { "empleadoId" })
        };
    }

    private static JsonObject ToolDef(string name, string desc, JsonObject props, JsonArray required)
        => new()
        {
            ["name"] = name,
            ["description"] = desc,
            ["input_schema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = props,
                ["required"] = required
            }
        };
    private static JsonObject ObjStr(string desc) => new() { ["type"] = "string", ["description"] = desc };
    private static JsonObject ObjInt(string desc) => new() { ["type"] = "integer", ["description"] = desc };

    // ============================================================
    // EJECUCION DE HERRAMIENTAS (todas read-only)
    // ============================================================
    private async Task<string> ExecuteToolAsync(string name, JsonObject input)
    {
        switch (name)
        {
            case "buscar_cliente":
                return await BuscarClienteAsync(input["nombre"]?.GetValue<string>() ?? "");
            case "ver_deuda_cliente":
                return await VerDeudaClienteAsync(input["clienteId"]?.GetValue<int>() ?? 0);
            case "ver_ultimo_comprobante_cliente":
                return await VerUltimoComprobanteAsync(input["clienteId"]?.GetValue<int>() ?? 0);
            case "buscar_producto":
                return await BuscarProductoAsync(input["busqueda"]?.GetValue<string>() ?? "");
            case "buscar_empleado_nominas":
                return await BuscarEmpleadoAsync(input["nombre"]?.GetValue<string>() ?? "");
            case "ver_sueldo_pendiente_empleado":
                return await VerSueldoPendienteAsync(input["empleadoId"]?.GetValue<int>() ?? 0);
            default:
                return JsonSerializer.Serialize(new { error = $"Herramienta desconocida: {name}" });
        }
    }

    private async Task<string> BuscarClienteAsync(string q)
    {
        if (string.IsNullOrWhiteSpace(q)) return "[]";
        var like = $"%{q.Trim()}%";
        var results = await _db.Clients
            .Where(c => EF.Functions.Like(c.Name, like) || (c.Code != null && EF.Functions.Like(c.Code, like)))
            .OrderBy(c => c.Name)
            .Take(5)
            .Select(c => new { id = c.Id, nombre = c.Name, codigo = c.Code, telefono = c.Phone })
            .ToListAsync();
        return JsonSerializer.Serialize(results);
    }

    private async Task<string> VerDeudaClienteAsync(int clienteId)
    {
        if (clienteId <= 0) return JsonSerializer.Serialize(new { error = "Falta clienteId" });
        var cliente = await _db.Clients.FindAsync(clienteId);
        if (cliente is null) return JsonSerializer.Serialize(new { error = "Cliente no encontrado" });

        var pendientes = await _db.Sales
            .Where(s => s.ClientId == clienteId && !s.IsPaid && !s.IsCancelled)
            .OrderByDescending(s => s.Date)
            .Take(20)
            .Select(s => new { numero = s.Number, fecha = s.Date, total = s.Total })
            .ToListAsync();
        var totalDeuda = pendientes.Sum(p => p.total);
        return JsonSerializer.Serialize(new
        {
            cliente = cliente.Name,
            totalDeuda,
            cantidadPendientes = pendientes.Count,
            comprobantes = pendientes
        });
    }

    private async Task<string> VerUltimoComprobanteAsync(int clienteId)
    {
        if (clienteId <= 0) return JsonSerializer.Serialize(new { error = "Falta clienteId" });
        var s = await _db.Sales
            .Where(x => x.ClientId == clienteId && !x.IsCancelled)
            .OrderByDescending(x => x.Date).ThenByDescending(x => x.Id)
            .Select(x => new { numero = x.Number, fecha = x.Date, total = x.Total, pagado = x.IsPaid, condicion = x.PaymentCondition })
            .FirstOrDefaultAsync();
        if (s is null) return JsonSerializer.Serialize(new { mensaje = "El cliente no tiene comprobantes." });
        return JsonSerializer.Serialize(s);
    }

    private async Task<string> BuscarProductoAsync(string q)
    {
        if (string.IsNullOrWhiteSpace(q)) return "[]";
        var like = $"%{q.Trim()}%";
        var results = await _db.Products
            .Where(p => p.IsActive && (
                (p.Sku != null && EF.Functions.Like(p.Sku, like)) ||
                EF.Functions.Like(p.Title, like) ||
                (p.Barcode != null && EF.Functions.Like(p.Barcode, like))))
            .OrderBy(p => p.Title)
            .Take(5)
            .Select(p => new
            {
                id = p.Id,
                sku = p.Sku,
                titulo = p.Title,
                stock = p.Stock,
                stockUnit = p.StockUnit,
                precio = p.RetailPrice,
                costo = p.CostPrice
            })
            .ToListAsync();
        return JsonSerializer.Serialize(results);
    }

    private async Task<string> BuscarEmpleadoAsync(string q)
    {
        if (string.IsNullOrWhiteSpace(q)) return "[]";
        var like = $"%{q.Trim()}%";
        var results = await _db.NomEmpleados
            .Where(e => e.IsActive && EF.Functions.Like(e.Nombre, like))
            .OrderBy(e => e.Nombre)
            .Take(5)
            .Select(e => new { id = e.Id, nombre = e.Nombre, puesto = e.Puesto, sueldoBase = e.SueldoBase })
            .ToListAsync();
        return JsonSerializer.Serialize(results);
    }

    private async Task<string> VerSueldoPendienteAsync(int empleadoId)
    {
        if (empleadoId <= 0) return JsonSerializer.Serialize(new { error = "Falta empleadoId" });
        var emp = await _db.NomEmpleados.FindAsync(empleadoId);
        if (emp is null) return JsonSerializer.Serialize(new { error = "Empleado no encontrado" });

        var liqs = await _db.NomLiquidaciones
            .Where(l => l.EmpleadoId == empleadoId && l.Estado != "anulada")
            .Include(l => l.Pagos)
            .OrderByDescending(l => l.Anio).ThenByDescending(l => l.Mes)
            .ToListAsync();

        var detalle = liqs.Select(l =>
        {
            var pagado = l.Pagos.Sum(p => p.Monto);
            var saldo = l.NetoAPagar - pagado;
            return new
            {
                anio = l.Anio,
                mes = l.Mes,
                neto = l.NetoAPagar,
                pagado,
                saldo,
                estado = l.Estado,
                pagos = l.Pagos.OrderByDescending(p => p.FechaPago)
                          .Select(p => new { fecha = p.FechaPago, metodo = p.Metodo, monto = p.Monto })
                          .ToList()
            };
        }).Where(d => d.saldo > 0.01m).ToList();

        var total = detalle.Sum(d => d.saldo);
        return JsonSerializer.Serialize(new
        {
            empleado = emp.Nombre,
            totalPendiente = total,
            cantidadLiquidaciones = detalle.Count,
            liquidaciones = detalle
        });
    }
}
