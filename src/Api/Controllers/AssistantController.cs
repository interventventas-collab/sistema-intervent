using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/assistant")]
[Authorize]
public class AssistantController : ControllerBase
{
    private readonly AssistantService _assistant;

    public AssistantController(AssistantService assistant) { _assistant = assistant; }

    [HttpPost("chat")]
    public async Task<IActionResult> Chat([FromBody] AssistantChatRequest request)
    {
        try
        {
            var resp = await _assistant.ChatAsync(request.Messages ?? new());
            return Ok(resp);
        }
        catch (Exception ex)
        {
            return Ok(new AssistantChatResponse
            {
                Reply = "Hubo un error procesando tu pregunta. Probá de nuevo en un rato.",
                Error = ex.Message
            });
        }
    }
}
