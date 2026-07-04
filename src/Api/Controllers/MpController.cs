using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

/// <summary>
/// Cuenta de Mercado Pago (API oficial) + lectura del saldo. Etapa 1: solo saldo.
/// El Access Token se carga desde la pantalla /integraciones/mercadopago y la tarjeta
/// del dashboard muestra el saldo. Pedido de Osmar 2026-07-04.
/// </summary>
[ApiController]
[Route("api/mercadopago")]
[Authorize]
public class MpController : ControllerBase
{
    private readonly MpAccountService _service;
    private readonly MpSyncService _syncService;

    public MpController(MpAccountService service, MpSyncService syncService)
    {
        _service = service;
        _syncService = syncService;
    }

    /// <summary>Devuelve la cuenta cargada (sin el token), o null si no hay.</summary>
    [HttpGet("account")]
    public async Task<IActionResult> GetAccount() => Ok(await _service.GetAsync());

    /// <summary>Crea o actualiza el Access Token / alias / horarios.</summary>
    [HttpPut("account")]
    public async Task<IActionResult> SaveAccount([FromBody] MpAccountService.SaveMpAccountRequest req)
    {
        var (ok, error, dto) = await _service.SaveAsync(req);
        if (!ok) return BadRequest(new { error });
        return Ok(dto);
    }

    public record SincronizarMpResultDto(bool Ok, decimal? Disponible, decimal? Total, string? Error);

    /// <summary>Lee el saldo ahora contra la API de Mercado Pago. Rapido (llamada HTTP directa).</summary>
    [HttpPost("sincronizar")]
    public async Task<IActionResult> Sincronizar()
    {
        var r = await _syncService.SincronizarAsync();
        return Ok(new SincronizarMpResultDto(r.Ok, r.Disponible, r.Total, r.Error));
    }
}
