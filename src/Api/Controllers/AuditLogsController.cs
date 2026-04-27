using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/audit-logs")]
[Authorize]
public class AuditLogsController : ControllerBase
{
    private readonly AuditLogService _service;

    public AuditLogsController(AuditLogService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> GetLogs(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? entityType,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var dateFrom = from ?? DateTime.UtcNow.AddDays(-30);
        var dateTo = to ?? DateTime.UtcNow.AddDays(1);
        var result = await _service.GetLogsAsync(dateFrom, dateTo, entityType, page, pageSize);
        return Ok(result);
    }
}
