using System.ComponentModel.DataAnnotations;

namespace Api.DTOs;

public record IntegrationDto(
    int Id,
    string Provider,
    string? AppId,
    bool HasSecret,
    string? RedirectUrl,
    string? Settings,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

public record SaveIntegrationRequest(
    [Required][MaxLength(50)] string Provider,
    [MaxLength(255)] string? AppId,
    [MaxLength(255)] string? AppSecret,
    [MaxLength(500)] string? RedirectUrl,
    string? Settings,
    bool IsActive
);

// ===== ARCA (scraping) =====
// La contraseña NUNCA se devuelve al frontend — solo HasPassword indica si hay una guardada.
public record ArcaAccountDto(
    int Id,
    string Cuit,
    string? CuitLogin,
    string? Alias,
    bool HasPassword,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

public class CreateArcaAccountRequest
{
    [Required, MaxLength(20)]
    public string Cuit { get; set; } = string.Empty;
    [MaxLength(20)]
    public string? CuitLogin { get; set; }
    [MaxLength(100)]
    public string? Alias { get; set; }
    [Required]
    public string Password { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public class UpdateArcaAccountRequest
{
    [MaxLength(20)]
    public string? Cuit { get; set; }
    [MaxLength(20)]
    public string? CuitLogin { get; set; }
    [MaxLength(100)]
    public string? Alias { get; set; }
    /// <summary>Si viene vacío/null, NO se cambia la contraseña existente.</summary>
    public string? Password { get; set; }
    public bool? IsActive { get; set; }
}

// ===== ARCA (webservice) — certificados .pfx =====
// Atención: Password sí se devuelve al DTO porque el modal de edición debe poder
// mostrarla con el ojito 👁. Si en algún momento querés que sea read-only, sacala.
public record ArcaWebserviceAccountDto(
    int Id,
    string Cuit,
    string? Alias,
    string FileName,
    string FilePath,
    string? Password,
    string Environment,
    DateTime? ExpiresAt,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

/// <summary>Body de update — solo los campos editables (no FileName ni Cuit ni FilePath).</summary>
public class UpdateArcaWebserviceAccountRequest
{
    [MaxLength(100)]
    public string? Alias { get; set; }
    /// <summary>Si viene null, NO se toca la contraseña actual. Si viene "" se setea como vacía.</summary>
    public string? Password { get; set; }
    [MaxLength(20)]
    public string? Environment { get; set; }
    public bool? IsActive { get; set; }
}

// ===== Wizard de generación de certificado =====
public class GenerateCsrRequest
{
    [Required, MaxLength(20)]
    public string Cuit { get; set; } = string.Empty;
    [Required, MaxLength(100)]
    public string Alias { get; set; } = string.Empty;
}

public record GenerateCsrResponseDto(int Id, string FileName, string CsrPem, string Subject);

// ===== Test de certificado (WSAA + WSFEv1) =====
public class TestCertificateResultDto
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    /// <summary>true si la cuenta es de homologación — UI muestra form manual en lugar de PtoVta.</summary>
    public bool IsHomologation { get; set; }
    public List<PuntoVentaInfoDto> Puntos { get; set; } = new();
}

public class PuntoVentaInfoDto
{
    public int Nro { get; set; }
    public string EmisionTipo { get; set; } = "";
    public bool Bloqueado { get; set; }
    public string? FchBaja { get; set; }
    /// <summary>Tipo del último comprobante encontrado (texto legible, ej "Factura A").</summary>
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

// ============================================================
// Emisión de comprobante (FECAESolicitar)
// ============================================================

public class EmitirComprobanteItemDto
{
    public string Descripcion { get; set; } = "";
    public decimal Cantidad { get; set; }
    public decimal PrecioUnitario { get; set; }
    /// <summary>3=0%, 4=10.5%, 5=21%, 6=27%, 8=5%, 9=2.5%</summary>
    public int AlicIvaId { get; set; } = 5;
}

public class EmitirComprobanteRequest
{
    public int PtoVta { get; set; }
    /// <summary>1=Factura A, 6=B, 11=C, 2=ND A, 7=ND B, 12=ND C, 3=NC A, 8=NC B, 13=NC C</summary>
    public int CbteTipo { get; set; }
    /// <summary>1=Productos, 2=Servicios, 3=Productos y Servicios</summary>
    public int Concepto { get; set; } = 1;
    /// <summary>80=CUIT, 96=DNI, 99=Consumidor Final</summary>
    public int DocTipo { get; set; } = 99;
    public string DocNro { get; set; } = "0";
    public string ReceptorNombre { get; set; } = "Consumidor Final";
    public string? ReceptorDomicilio { get; set; }
    /// <summary>Obligatorio desde RG 5616. Ver tabla en el form del frontend.</summary>
    public int CondicionIVAReceptorId { get; set; } = 5; // 5=Consumidor Final por default
    public List<EmitirComprobanteItemDto> Items { get; set; } = new();
}

public class ComprobanteEmitidoDto
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Observaciones { get; set; }
    public int CbteTipo { get; set; }
    public string CbteTipoNombre { get; set; } = "";
    public int PtoVta { get; set; }
    public int CbteNro { get; set; }
    public string? Cae { get; set; }
    public string? CaeVto { get; set; }
    public string? Resultado { get; set; }
    public decimal ImpNeto { get; set; }
    public decimal ImpIVA { get; set; }
    public decimal ImpTotal { get; set; }
    public string Fecha { get; set; } = "";
    /// <summary>Path relativo del PDF guardado en disco.</summary>
    public string? PdfPath { get; set; }
    public string? PdfDownloadUrl { get; set; }
}
