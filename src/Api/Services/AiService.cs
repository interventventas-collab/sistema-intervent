using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Api.DTOs;

namespace Api.Services;

public class AiService
{
    private readonly IntegrationService _integrationService;
    private readonly IHttpClientFactory _httpFactory;

    public AiService(IntegrationService integrationService, IHttpClientFactory httpFactory)
    {
        _integrationService = integrationService;
        _httpFactory = httpFactory;
    }

    public async Task<List<SuggestedAttributeDto>> SuggestAttributesAsync(SuggestAttributesRequest request)
    {
        var apiKey = await _integrationService.GetSecretAsync("openai");
        if (string.IsNullOrEmpty(apiKey))
            return new List<SuggestedAttributeDto>();

        try
        {
            var attrDescriptions = new StringBuilder();
            foreach (var attr in request.Attributes)
            {
                attrDescriptions.AppendLine($"- {attr.Id}: {attr.Name} (tipo: {attr.ValueType}, obligatorio: {attr.Required})");
                if (attr.Values.Any())
                {
                    var options = string.Join(", ", attr.Values.Take(20).Select(v => $"{v.Id}={v.Name}"));
                    attrDescriptions.AppendLine($"  Opciones: {options}");
                }
            }

            var descriptionText = request.Description ?? "No disponible";
            if (descriptionText.Length > 500) descriptionText = descriptionText[..500];

            var brandText = request.Brand ?? "No especificada";
            var modelText = request.Model ?? "No especificado";

            var prompt = $@"Eres un experto en productos de MercadoLibre Argentina.
Dado este producto:
- Titulo: {request.Title}
- Marca: {brandText}
- Modelo: {modelText}
- Descripcion: {descriptionText}
- Categoria MeLi: {request.CategoryName} ({request.CategoryId})

Sugiere valores para los siguientes atributos de la ficha tecnica:
{attrDescriptions}

IMPORTANTE:
- Si el atributo tiene opciones predefinidas, usa EXACTAMENTE el ID y nombre de una de las opciones.
- Si no tiene opciones y es texto libre, sugiere un valor razonable.
- Si no puedes determinar el valor, omite ese atributo.
- Responde SOLO con un JSON array, sin texto adicional.

Formato de respuesta (JSON array):
[
  {{ ""attributeId"": ""BRAND"", ""valueId"": ""12345"", ""valueName"": ""Samsung"" }},
  {{ ""attributeId"": ""COLOR"", ""valueId"": null, ""valueName"": ""Negro"" }}
]";

            var http = _httpFactory.CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var body = new
            {
                model = "gpt-4o-mini",
                messages = new[]
                {
                    new { role = "system", content = "Eres un asistente que sugiere atributos de productos para MercadoLibre. Responde SOLO con JSON valido." },
                    new { role = "user", content = prompt }
                },
                temperature = 0.3,
                max_tokens = 2000
            };

            var json = JsonSerializer.Serialize(body);
            var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await http.PostAsync("https://api.openai.com/v1/chat/completions", httpContent);

            if (!response.IsSuccessStatusCode)
                return new List<SuggestedAttributeDto>();

            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);
            var messageContent = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "[]";

            // Clean markdown code blocks if present
            messageContent = messageContent.Trim();
            if (messageContent.StartsWith("```json"))
                messageContent = messageContent[7..];
            else if (messageContent.StartsWith("```"))
                messageContent = messageContent[3..];
            if (messageContent.EndsWith("```"))
                messageContent = messageContent[..^3];
            messageContent = messageContent.Trim();

            var suggestions = JsonSerializer.Deserialize<List<SuggestedAttributeDto>>(messageContent, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return suggestions ?? new List<SuggestedAttributeDto>();
        }
        catch
        {
            return new List<SuggestedAttributeDto>();
        }
    }
}
