using Api.Data;
using Api.Models;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;

namespace Api.Controllers;

/// <summary>2026-08-06: candado de 4 dígitos para la pantalla de WhatsApp del celu (/whatsapp-movil).
/// Solo entran Osmar/Germán/Gabriel con su año de nacimiento. Identifica quién entró.
/// Los códigos se guardan en AppSettings (key "wamovil.codigos", JSON) y son editables; si no
/// existe todavía, usa los valores por defecto. Es una barrera liviana ADEMÁS del login del sistema
/// (la pantalla sigue siendo [Authorize]); la huella se sumará como método principal más adelante.</summary>
[ApiController]
[Route("api/wa-movil")]
[Authorize]
public class WaMovilAccesoController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IFido2 _fido2;
    private readonly IMemoryCache _cache;
    public WaMovilAccesoController(AppDbContext db, IFido2 fido2, IMemoryCache cache) { _db = db; _fido2 = fido2; _cache = cache; }

    private const string SettingKey = "wamovil.codigos";

    public record AccesoDto(string Nombre, string Codigo);
    public record VerificarRequest(string Codigo);

    // Valores por defecto (si todavía no se editaron desde la config).
    private static List<AccesoDto> Defaults() => new()
    {
        new("Osmar", "1988"),
        new("Germán", "1990"),
        new("Gabriel", "1983"),
    };

    private async Task<List<AccesoDto>> LeerAsync()
    {
        var s = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == SettingKey);
        if (s == null || string.IsNullOrWhiteSpace(s.Value)) return Defaults();
        try
        {
            var list = JsonSerializer.Deserialize<List<AccesoDto>>(s.Value);
            return (list != null && list.Count > 0) ? list : Defaults();
        }
        catch { return Defaults(); }
    }

    /// <summary>Verifica el código de 4 dígitos. Devuelve { ok, nombre } si coincide con alguien.</summary>
    [HttpPost("verificar")]
    public async Task<IActionResult> Verificar([FromBody] VerificarRequest req)
    {
        var codigo = (req?.Codigo ?? "").Trim();
        if (string.IsNullOrWhiteSpace(codigo)) return Ok(new { ok = false });
        var lista = await LeerAsync();
        var match = lista.FirstOrDefault(a => a.Codigo == codigo);
        if (match == null) return Ok(new { ok = false });
        return Ok(new { ok = true, nombre = match.Nombre });
    }

    /// <summary>Lista los accesos (para una pantalla de edición). Solo admin.</summary>
    [HttpGet("codigos")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Codigos() => Ok(await LeerAsync());

    /// <summary>Guarda/edita los accesos (nombre + código de 4 dígitos). Solo admin.</summary>
    [HttpPut("codigos")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> GuardarCodigos([FromBody] List<AccesoDto> accesos)
    {
        accesos ??= new();
        // Normalizamos: solo los que tengan nombre y código de 4 dígitos numéricos.
        var limpios = accesos
            .Where(a => !string.IsNullOrWhiteSpace(a.Nombre)
                        && !string.IsNullOrWhiteSpace(a.Codigo)
                        && a.Codigo.Trim().Length == 4
                        && a.Codigo.Trim().All(char.IsDigit))
            .Select(a => new AccesoDto(a.Nombre.Trim(), a.Codigo.Trim()))
            .ToList();

        var json = JsonSerializer.Serialize(limpios);
        var s = await _db.AppSettings.FirstOrDefaultAsync(x => x.Key == SettingKey);
        if (s == null)
        {
            s = new AppSetting { Key = SettingKey, Value = json, UpdatedAt = DateTime.UtcNow };
            _db.AppSettings.Add(s);
        }
        else
        {
            s.Value = json;
            s.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, count = limpios.Count });
    }

    // ===================== HUELLA (WebAuthn) — 2026-08-07 =====================
    // Cada persona (Osmar/Germán/Gabriel) registra su huella con su código de 4 dígitos;
    // después entra tocando la huella. Reusa el motor Fido2. Tabla WaMovil_WebAuthnCredentials.
    // OJO: la huella anda en el dominio configurado como RP id (app.palanica.com.ar). En otro
    // dominio (frikaf) el browser la rechaza → ahí entran con el código.

    public record HuellaRegBeginReq(string Codigo, string? DeviceName);
    public record HuellaRegBeginResult(bool Ok, string? Mensaje, object? Options, string? SessionId);

    [HttpPost("huella/registro/begin")]
    public async Task<IActionResult> HuellaRegBegin([FromBody] HuellaRegBeginReq req)
    {
        var lista = await LeerAsync();
        var persona = lista.FirstOrDefault(a => a.Codigo == (req?.Codigo ?? "").Trim())?.Nombre;
        if (string.IsNullOrEmpty(persona))
            return Ok(new HuellaRegBeginResult(false, "Ingresá tu código de 4 dígitos y volvé a tocar Registrar huella.", null, null));

        var userHandle = System.Text.Encoding.UTF8.GetBytes($"wamovil-{persona}");
        var user = new Fido2User { Id = userHandle, Name = $"wamovil-{persona}", DisplayName = persona };
        var existentes = await _db.WaMovilWebAuthnCredentials.Where(c => c.Persona == persona).ToListAsync();
        var excludeList = existentes.Select(c => new PublicKeyCredentialDescriptor(Convert.FromBase64String(c.CredentialId))).ToList();
        var authSelection = new AuthenticatorSelection
        {
            UserVerification = UserVerificationRequirement.Required,
            AuthenticatorAttachment = AuthenticatorAttachment.Platform
        };
        var options = _fido2.RequestNewCredential(user, excludeList, authSelection, AttestationConveyancePreference.None);
        var sessionId = Guid.NewGuid().ToString("N").Substring(0, 16);
        _cache.Set($"wamovil:reg:{sessionId}", options.ToJson(), TimeSpan.FromMinutes(5));
        _cache.Set($"wamovil:reg:{sessionId}:persona", persona, TimeSpan.FromMinutes(5));
        _cache.Set($"wamovil:reg:{sessionId}:device", req?.DeviceName ?? "Celular", TimeSpan.FromMinutes(5));
        return Ok(new HuellaRegBeginResult(true, null, options, sessionId));
    }

    public class HuellaRegCompleteReq
    {
        public string SessionId { get; set; } = "";
        public AuthenticatorAttestationRawResponse AttestationResponse { get; set; } = null!;
    }
    public record HuellaRegCompleteResult(bool Ok, string? Mensaje);

    [HttpPost("huella/registro/complete")]
    public async Task<IActionResult> HuellaRegComplete([FromBody] HuellaRegCompleteReq req)
    {
        if (!_cache.TryGetValue<string>($"wamovil:reg:{req.SessionId}", out var oj) || oj is null)
            return Ok(new HuellaRegCompleteResult(false, "Sesión expirada, volvé a intentar."));
        var persona = _cache.Get<string>($"wamovil:reg:{req.SessionId}:persona") ?? "";
        var device = _cache.Get<string>($"wamovil:reg:{req.SessionId}:device") ?? "Celular";
        var options = CredentialCreateOptions.FromJson(oj);

        IsCredentialIdUniqueToUserAsyncDelegate cb = async (args, ct) =>
        {
            var b64 = Convert.ToBase64String(args.CredentialId);
            return !await _db.WaMovilWebAuthnCredentials.AnyAsync(c => c.CredentialId == b64, ct);
        };
        try
        {
            var success = await _fido2.MakeNewCredentialAsync(req.AttestationResponse, options, cb);
            if (success.Result is null) return Ok(new HuellaRegCompleteResult(false, "No se pudo registrar la huella."));
            _db.WaMovilWebAuthnCredentials.Add(new WaMovilWebAuthnCredential
            {
                Persona = persona,
                CredentialId = Convert.ToBase64String(success.Result.CredentialId),
                PublicKey = Convert.ToBase64String(success.Result.PublicKey),
                UserHandle = Convert.ToBase64String(success.Result.User.Id),
                AaGuid = success.Result.Aaguid.ToString(),
                SignatureCounter = success.Result.Counter,
                DeviceName = device,
                CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
            _cache.Remove($"wamovil:reg:{req.SessionId}");
            _cache.Remove($"wamovil:reg:{req.SessionId}:persona");
            _cache.Remove($"wamovil:reg:{req.SessionId}:device");
            return Ok(new HuellaRegCompleteResult(true, "Huella registrada"));
        }
        catch (Fido2VerificationException ex)
        {
            return Ok(new HuellaRegCompleteResult(false, "Error de verificación: " + ex.Message));
        }
    }

    public record HuellaLoginBeginResult(bool Ok, string? Mensaje, object? Options, string? SessionId);

    [HttpPost("huella/login/begin")]
    public async Task<IActionResult> HuellaLoginBegin()
    {
        var creds = await _db.WaMovilWebAuthnCredentials.ToListAsync();
        var allowed = creds.Select(c => new PublicKeyCredentialDescriptor(Convert.FromBase64String(c.CredentialId))).ToList();
        if (allowed.Count == 0) return Ok(new HuellaLoginBeginResult(false, "Todavía no hay huellas registradas.", null, null));
        var options = _fido2.GetAssertionOptions(allowed, UserVerificationRequirement.Required);
        var sessionId = Guid.NewGuid().ToString("N").Substring(0, 16);
        _cache.Set($"wamovil:auth:{sessionId}", options.ToJson(), TimeSpan.FromMinutes(5));
        return Ok(new HuellaLoginBeginResult(true, null, options, sessionId));
    }

    public class HuellaLoginCompleteReq
    {
        public string SessionId { get; set; } = "";
        public AuthenticatorAssertionRawResponse AssertionResponse { get; set; } = null!;
    }
    public record HuellaLoginCompleteResult(bool Ok, string? Mensaje, string? Nombre);

    [HttpPost("huella/login/complete")]
    public async Task<IActionResult> HuellaLoginComplete([FromBody] HuellaLoginCompleteReq req)
    {
        if (!_cache.TryGetValue<string>($"wamovil:auth:{req.SessionId}", out var oj) || oj is null)
            return Ok(new HuellaLoginCompleteResult(false, "Sesión expirada, volvé a intentar.", null));
        var options = AssertionOptions.FromJson(oj);
        var credIdB64 = Convert.ToBase64String(req.AssertionResponse.Id);
        var cred = await _db.WaMovilWebAuthnCredentials.FirstOrDefaultAsync(c => c.CredentialId == credIdB64);
        if (cred is null) return Ok(new HuellaLoginCompleteResult(false, "Huella no reconocida.", null));

        IsUserHandleOwnerOfCredentialIdAsync cb = async (args, ct) =>
        {
            var b64 = Convert.ToBase64String(args.CredentialId);
            return await _db.WaMovilWebAuthnCredentials.AnyAsync(c => c.CredentialId == b64
                && c.UserHandle == Convert.ToBase64String(args.UserHandle), ct);
        };
        AssertionVerificationResult vr;
        try
        {
            vr = await _fido2.MakeAssertionAsync(req.AssertionResponse, options,
                Convert.FromBase64String(cred.PublicKey), cred.SignatureCounter, cb);
        }
        catch (Fido2VerificationException ex)
        {
            return Ok(new HuellaLoginCompleteResult(false, "Error verificando huella: " + ex.Message, null));
        }
        cred.SignatureCounter = vr.Counter;
        cred.LastUsedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        _cache.Remove($"wamovil:auth:{req.SessionId}");
        return Ok(new HuellaLoginCompleteResult(true, null, cred.Persona));
    }
}
