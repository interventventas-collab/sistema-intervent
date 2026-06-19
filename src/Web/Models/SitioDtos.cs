namespace Web.Models;

public record SitioDto(int Id, string Nombre, string Slug, string? Dominios,
    string? LogoUrl, string? Eyebrow, string? Frase,
    string? WhatsApp, string? WhatsApp2, string? Instagram, string? Facebook,
    string? ColorPrimario, string? ColorAcento, bool IsActive);

public record SitioUpsertRequest(string Nombre, string Slug, string? Dominios,
    string? LogoUrl, string? Eyebrow, string? Frase,
    string? WhatsApp, string? WhatsApp2, string? Instagram, string? Facebook,
    string? ColorPrimario, string? ColorAcento, bool IsActive);

public record SitioUploadDto(string Url, string Filename, long SizeKb, DateTime Created);

public record SitioUploadResponse(string Url, string Filename);
