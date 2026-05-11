namespace Web.Models;

public class IntegrationDto
{
    public int Id { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string? AppId { get; set; }
    public bool HasSecret { get; set; }
    public string? RedirectUrl { get; set; }
    public string? Settings { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class SaveIntegrationRequest
{
    public string Provider { get; set; } = string.Empty;
    public string? AppId { get; set; }
    public string? AppSecret { get; set; }
    public string? RedirectUrl { get; set; }
    public string? Settings { get; set; }
    public bool IsActive { get; set; }
}

public class MeliAccountDto
{
    public int Id { get; set; }
    public long MeliUserId { get; set; }
    public string Nickname { get; set; } = string.Empty;
    public string? Email { get; set; }
    public bool TokenValid { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class MeliAuthUrlResponse
{
    public string AuthUrl { get; set; } = string.Empty;
}

public class MeliCallbackRequest
{
    public string Code { get; set; } = string.Empty;
}

public class OpenAiModelDto
{
    public string Id { get; set; } = string.Empty;
}

public class ClaudeModelDto
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

// ===== ARCA (scraping) =====
public class ArcaAccountDto
{
    public int Id { get; set; }
    public string Cuit { get; set; } = string.Empty;
    public string? CuitLogin { get; set; }
    public string? Alias { get; set; }
    public bool HasPassword { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class CreateArcaAccountRequest
{
    public string Cuit { get; set; } = string.Empty;
    public string? CuitLogin { get; set; }
    public string? Alias { get; set; }
    public string Password { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public class UpdateArcaAccountRequest
{
    public string? Cuit { get; set; }
    public string? CuitLogin { get; set; }
    public string? Alias { get; set; }
    /// <summary>Si null o vacío, no se cambia la contraseña.</summary>
    public string? Password { get; set; }
    public bool? IsActive { get; set; }
}

// ===== ARCA test (login + scraping) =====
public class ArcaTestStatusDto
{
    public bool Running { get; set; }
    public string Step { get; set; } = "";
    public ArcaTestResultDto? Result { get; set; }
}

public class ArcaTestResultDto
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Titular { get; set; }
    public List<ArcaDomicilioDto>? Domicilios { get; set; }
    public List<ArcaActividadDto>? Actividades { get; set; }
    // Comprobantes
    public List<ArcaComprobanteDto>? Emitidos { get; set; }
    public List<ArcaComprobanteDto>? Recibidos { get; set; }
    public string? RangoDesde { get; set; }
    public string? RangoHasta { get; set; }
}

public class ArcaDomicilioDto
{
    public string Tipo { get; set; } = "";
    public string Direccion { get; set; } = "";
    public string Jurisdiccion { get; set; } = "";
}

public class ArcaActividadDto
{
    public string Descripcion { get; set; } = "";
    public string FechaInicio { get; set; } = "";
}

public class ArcaComprobanteDto
{
    public string Fecha { get; set; } = "";
    public string NroDoc { get; set; } = "";
    public string Denominacion { get; set; } = "";
    public decimal? ImpNeto { get; set; }
    public decimal? TotalIva { get; set; }
    public decimal? ImpTotal { get; set; }
}

public class ArcaRangoFechasRequest
{
    public string Tipo { get; set; } = "30dias";
    public string? Desde { get; set; }
    public string? Hasta { get; set; }
}

// ===== ARCA Webservice (.pfx) =====
public class ArcaWebserviceAccountDto
{
    public int Id { get; set; }
    public string Cuit { get; set; } = "";
    public string? Alias { get; set; }
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string? Password { get; set; }
    public string Environment { get; set; } = "production";
    public DateTime? ExpiresAt { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class UpdateArcaWebserviceAccountRequest
{
    public string? Alias { get; set; }
    /// <summary>Si null no toca la pass; si "" la blanquea.</summary>
    public string? Password { get; set; }
    public string? Environment { get; set; }
    public bool? IsActive { get; set; }
}

public class GenerateArcaCsrRequest
{
    public string Cuit { get; set; } = "";
    public string Alias { get; set; } = "";
}

public class GenerateArcaCsrResponseDto
{
    public int Id { get; set; }
    public string FileName { get; set; } = "";
    public string CsrPem { get; set; } = "";
    public string Subject { get; set; } = "";
}

// Test de certificado contra WSAA + WSFEv1
public class TestCertificateResultDto
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public bool IsHomologation { get; set; }
    public List<PuntoVentaInfoDto> Puntos { get; set; } = new();
}

public class PuntoVentaInfoDto
{
    public int Nro { get; set; }
    public string EmisionTipo { get; set; } = "";
    public bool Bloqueado { get; set; }
    public string? FchBaja { get; set; }
    public string? UltimoCbteTipo { get; set; }
    public int UltimoCbteTipoNro { get; set; }
    public int UltimoCbteNro { get; set; }
    public string? UltimaFecha { get; set; }
}

public class UltimosComprobantesRequest
{
    public int PtoVta { get; set; }
    public int CbteTipo { get; set; }
    public int? UltimoNro { get; set; }
    public int Cantidad { get; set; } = 5;
}

public class UltimosComprobantesResultDto
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public List<ComprobanteDetalleDto> Comprobantes { get; set; } = new();
}

public class ComprobanteDetalleDto
{
    public int CbteTipoNro { get; set; }
    public string CbteTipo { get; set; } = "";
    public int CbteNro { get; set; }
    public string Fecha { get; set; } = "";
    public string DocNro { get; set; } = "";
    public decimal? ImpNeto { get; set; }
    public decimal? ImpIVA { get; set; }
    public decimal? ImpTrib { get; set; }
    public decimal? ImpTotal { get; set; }
    public string MonId { get; set; } = "PES";
    public string Cae { get; set; } = "";
    public string CaeVto { get; set; } = "";
    public string Resultado { get; set; } = "";
}
