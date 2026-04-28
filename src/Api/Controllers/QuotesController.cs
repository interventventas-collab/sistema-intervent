using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/quotes")]
[Authorize]
public class QuotesController : ControllerBase
{
    private readonly QuotesService _quotes;

    public QuotesController(QuotesService quotes)
    {
        _quotes = quotes;
    }

    [HttpGet("dolar-bna")]
    public async Task<IActionResult> DolarBna() => Ok(await _quotes.GetDolarBnaAsync());
}
