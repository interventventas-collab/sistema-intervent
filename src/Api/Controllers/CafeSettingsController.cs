using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/cafe/settings")]
[Authorize]
public class CafeSettingsController : ControllerBase
{
    private readonly AppDbContext _db;

    public CafeSettingsController(AppDbContext db) { _db = db; }

    private static CafeSettingDto Map(CafeSetting s) => new(
        s.CostoFraccionamiento, s.RedondeoMultiplo,
        s.MargenOtrosBarPct, s.MargenOtrosNoBarPct,
        s.NegocioNombre, s.NegocioTelefono, s.NegocioWhatsappNumero,
        s.NegocioDireccion, s.NegocioCuit,
        s.NegocioEmail, s.NegocioWeb, s.NegocioLogoUrl,
        s.WhatsappMensajeTemplate, s.WhatsappMensajeClienteTemplate,
        s.NegocioRazonSocial, s.NegocioCondicionIva,
        s.NegocioIngresosBrutos, s.NegocioInicioActividad,
        s.NegocioLocalidad, s.NegocioCp,
        s.UpdatedAt);

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var s = await EnsureAsync();
        return Ok(Map(s));
    }

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] UpdateCafeSettingRequest req)
    {
        var s = await EnsureAsync();
        if (req.CostoFraccionamiento.HasValue) s.CostoFraccionamiento = Math.Max(0m, req.CostoFraccionamiento.Value);
        if (req.RedondeoMultiplo.HasValue)
        {
            if (req.RedondeoMultiplo.Value < 1m) return BadRequest(new { error = "El redondeo debe ser >= 1" });
            s.RedondeoMultiplo = req.RedondeoMultiplo.Value;
        }
        if (req.MargenOtrosBarPct.HasValue) s.MargenOtrosBarPct = Math.Max(0m, req.MargenOtrosBarPct.Value);
        if (req.MargenOtrosNoBarPct.HasValue) s.MargenOtrosNoBarPct = Math.Max(0m, req.MargenOtrosNoBarPct.Value);
        if (req.NegocioNombre is not null) s.NegocioNombre = Norm(req.NegocioNombre);
        if (req.NegocioTelefono is not null) s.NegocioTelefono = Norm(req.NegocioTelefono);
        if (req.NegocioWhatsappNumero is not null) s.NegocioWhatsappNumero = Norm(req.NegocioWhatsappNumero);
        if (req.NegocioDireccion is not null) s.NegocioDireccion = Norm(req.NegocioDireccion);
        if (req.NegocioCuit is not null) s.NegocioCuit = Norm(req.NegocioCuit);
        if (req.NegocioEmail is not null) s.NegocioEmail = Norm(req.NegocioEmail);
        if (req.NegocioWeb is not null) s.NegocioWeb = Norm(req.NegocioWeb);
        if (req.NegocioLogoUrl is not null) s.NegocioLogoUrl = Norm(req.NegocioLogoUrl);
        if (req.WhatsappMensajeTemplate is not null) s.WhatsappMensajeTemplate = Norm(req.WhatsappMensajeTemplate);
        if (req.WhatsappMensajeClienteTemplate is not null) s.WhatsappMensajeClienteTemplate = Norm(req.WhatsappMensajeClienteTemplate);
        if (req.NegocioRazonSocial is not null) s.NegocioRazonSocial = Norm(req.NegocioRazonSocial);
        if (req.NegocioCondicionIva is not null) s.NegocioCondicionIva = Norm(req.NegocioCondicionIva);
        if (req.NegocioIngresosBrutos is not null) s.NegocioIngresosBrutos = Norm(req.NegocioIngresosBrutos);
        if (req.NegocioInicioActividad.HasValue) s.NegocioInicioActividad = req.NegocioInicioActividad;
        if (req.NegocioLocalidad is not null) s.NegocioLocalidad = Norm(req.NegocioLocalidad);
        if (req.NegocioCp is not null) s.NegocioCp = Norm(req.NegocioCp);
        s.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(s));
    }

    private async Task<CafeSetting> EnsureAsync()
    {
        var s = await _db.CafeSettings.FindAsync(1);
        if (s is null)
        {
            s = new CafeSetting { Id = 1 };
            _db.CafeSettings.Add(s);
            await _db.SaveChangesAsync();
        }
        return s;
    }

    private static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
