using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-11-10: la "clave del día" del celu del depósito (inicio /depo y sus pantallas).
/// La clave (6 números) y el pase que recibe el celu al escribirla salen de firmar la fecha
/// argentina con el secreto de la app: no se guarda nada y a la medianoche dejan de servir.
/// El pase viaja en el encabezado X-Pase-Celu. Las rutas que lo usan van bajo /api/public/
/// (ya permitida en WaMovilScopeMiddleware para los celus con huella).
/// </summary>
public class ClaveDiaCeluService
{
    public const string ENCABEZADO = "X-Pase-Celu";
    private readonly IConfiguration _config;

    public ClaveDiaCeluService(IConfiguration config) => _config = config;

    public static string HoyAr() => DateTime.UtcNow.AddHours(-3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private byte[] Firma(string para, string dia)
    {
        var secreto = _config["Jwt:Secret"];
        if (string.IsNullOrEmpty(secreto)) throw new InvalidOperationException("Falta Jwt:Secret");
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secreto), Encoding.UTF8.GetBytes($"stock-ideal-celu|{para}|{dia}"));
    }

    /// <summary>La clave de hoy, para mostrarla adentro del sistema.</summary>
    public string ClaveDeHoy() => Clave(HoyAr());

    private string Clave(string dia)
        => (BitConverter.ToUInt32(Firma("clave", dia), 0) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    private string Pase(string dia) => Convert.ToHexString(Firma("pase", dia));

    private static bool Iguales(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    /// <summary>Si la clave es la de hoy devuelve el pase; si no, null.</summary>
    public string? Entrar(string? clave)
    {
        var dia = HoyAr();
        return Iguales((clave ?? "").Trim(), Clave(dia)) ? Pase(dia) : null;
    }

    public bool PaseValido(HttpRequest request)
        => request.Headers.TryGetValue(ENCABEZADO, out var pase) && Iguales(pase.ToString(), Pase(HoyAr()));

    /// <summary>El celu del depósito habilitado (el mismo del picking y del escáner): hay uno solo activo.</summary>
    public static async Task<bool> EsCeluHabilitadoAsync(AppDbContext db, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        var activo = await db.CafePickingDispositivos.Where(x => x.Activo).OrderBy(x => x.Id)
            .Select(x => x.DeviceId).FirstOrDefaultAsync();
        return activo is not null && activo == deviceId.Trim();
    }
}
