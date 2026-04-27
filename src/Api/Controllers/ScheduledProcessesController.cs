using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/scheduled-processes")]
[Authorize]
public class ScheduledProcessesController : ControllerBase
{
    private readonly ScheduledProcessService _service;

    public ScheduledProcessesController(ScheduledProcessService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<List<ScheduledProcessDto>>> GetAll()
    {
        var processes = await _service.GetAllAsync();
        return Ok(processes);
    }

    [HttpGet("{code}")]
    public async Task<ActionResult<ScheduledProcessDto>> GetByCode(string code)
    {
        var process = await _service.GetByCodeAsync(code);
        if (process is null) return NotFound();
        return Ok(process);
    }

    [HttpPut("{code}/schedule")]
    public async Task<ActionResult<ScheduledProcessDto>> UpdateSchedule(string code, [FromBody] UpdateScheduleRequest request)
    {
        var result = await _service.UpdateScheduleAsync(code, request);
        if (result is null) return NotFound();
        return Ok(result);
    }

    [HttpPost("{code}/run")]
    public async Task<ActionResult<RunProcessResponse>> RunNow(string code)
    {
        var result = await _service.RunNowAsync(code);
        if (!result.Started) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("{code}/logs")]
    public async Task<ActionResult<ProcessLogListResponse>> GetLogs(string code, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await _service.GetLogsAsync(code, page, pageSize);
        return Ok(result);
    }

    [HttpGet("logs")]
    public async Task<ActionResult<ProcessLogListResponse>> GetAllLogs([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await _service.GetLogsAsync(null, page, pageSize);
        return Ok(result);
    }
}
