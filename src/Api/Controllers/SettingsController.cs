using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SettingsController : ControllerBase
{
    private readonly AppDbContext _db;

    public SettingsController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll()
    {
        var settings = await _db.AppSettings.ToListAsync();
        return Ok(settings.ToDictionary(s => s.Key, s => s.Value));
    }

    [HttpGet("{key}")]
    public async Task<IActionResult> Get(string key)
    {
        var setting = await _db.AppSettings.FindAsync(key);
        if (setting is null) return NotFound();
        return Ok(new { setting.Key, setting.Value });
    }

    [HttpPost("logo")]
    public async Task<IActionResult> UploadLogo(IFormFile file)
    {
        if (file is null || file.Length == 0)
            return BadRequest("No file provided");

        // Validate file type
        var allowedTypes = new[] { "image/png", "image/jpeg", "image/svg+xml", "image/webp" };
        if (!allowedTypes.Contains(file.ContentType))
            return BadRequest("Only PNG, JPG, SVG and WebP files are allowed");

        // Max 500KB
        if (file.Length > 512000)
            return BadRequest("File too large. Maximum 500KB");

        // Convert to base64 data URI
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var base64 = Convert.ToBase64String(ms.ToArray());
        var dataUri = $"data:{file.ContentType};base64,{base64}";

        // Save as setting
        var setting = await _db.AppSettings.FindAsync("BrandLogo");
        if (setting is null)
        {
            setting = new AppSetting { Key = "BrandLogo", Value = dataUri };
            _db.AppSettings.Add(setting);
        }
        else
        {
            setting.Value = dataUri;
            setting.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();

        return Ok(new { Key = "BrandLogo", Value = dataUri });
    }

    [HttpDelete("logo")]
    public async Task<IActionResult> DeleteLogo()
    {
        var setting = await _db.AppSettings.FindAsync("BrandLogo");
        if (setting is not null)
        {
            _db.AppSettings.Remove(setting);
            await _db.SaveChangesAsync();
        }
        return Ok();
    }

    [HttpPut("{key}")]
    public async Task<IActionResult> Update(string key, [FromBody] SettingUpdateDto dto)
    {
        var setting = await _db.AppSettings.FindAsync(key);
        if (setting is null)
        {
            setting = new AppSetting { Key = key, Value = dto.Value };
            _db.AppSettings.Add(setting);
        }
        else
        {
            setting.Value = dto.Value;
            setting.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return Ok(new { setting.Key, setting.Value });
    }
}

public class SettingUpdateDto
{
    public string Value { get; set; } = string.Empty;
}
