using Api.Data;
using Microsoft.EntityFrameworkCore;
using QRCoder;

namespace Api.Services;

/// <summary>
/// Genera el QR que va en los comprobantes (cotizacion + factura ARCA) y que el repartidor
/// escanea para ir a /repartidor/{token} y cargar la cobranza desde su celu. Pedido 2026-05-19.
///
/// La URL base sale del AppSetting "mapeo.public_base_url" (ej "https://app.palanica.com.ar").
/// Si no esta configurada, devuelve null y el PDF no muestra QR.
/// </summary>
public class QrRepartidorService
{
    private readonly AppDbContext _db;
    public QrRepartidorService(AppDbContext db) { _db = db; }

    public Task<byte[]?> GenerarQrAsync(string? publicToken) => GenerarParaRutaAsync("repartidor", publicToken);

    /// <summary>QR para el comprobante de una reserva de alquiler: lleva a /alquiler/{token}.</summary>
    public Task<byte[]?> GenerarQrAlquilerAsync(string? publicToken) => GenerarParaRutaAsync("alquiler", publicToken);

    private async Task<byte[]?> GenerarParaRutaAsync(string ruta, string? publicToken)
    {
        if (string.IsNullOrWhiteSpace(publicToken)) return null;
        var baseUrl = (await _db.AppSettings.FindAsync("mapeo.public_base_url"))?.Value;
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        baseUrl = baseUrl.TrimEnd('/');
        var url = $"{baseUrl}/{ruta}/{publicToken}";
        using var qrGen = new QRCodeGenerator();
        using var qrData = qrGen.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(qrData);
        return png.GetGraphic(6);
    }
}
