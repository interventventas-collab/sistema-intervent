using System.Globalization;
using System.Text.RegularExpressions;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 18/11/2026: "#gasto" por WhatsApp. Cuando un número autorizado escribe "#gasto lo que sea" (con o sin
/// foto del ticket) se guarda en Gastos_WhatsApp como PENDIENTE. NO se contesta nada por WhatsApp
/// (desde oct/2026 Meta cobra las respuestas). Si manda la foto en un mensaje aparte enseguida después,
/// se pega al último gasto pendiente de ese número (hasta 10 minutos).
///
/// Quién puede: la lista "gasto.numeros" de AppSettings (se edita en Compras → Gastos por WhatsApp).
/// Si está vacía, valen los números de las personas activas de Alertas (Auto_Personas).
/// </summary>
public static class GastoWhatsAppService
{
    public const string KeyNumeros = "gasto.numeros";
    private static readonly Regex Disparador = new(@"^\s*#\s*gastos?\b[\s:,.-]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Últimos 10 dígitos: así "+54 9 11 2081-2933", "541120812933" y "1120812933" son el mismo.</summary>
    public static string Clave(string? numero)
    {
        var d = new string((numero ?? "").Where(char.IsDigit).ToArray());
        return d.Length > 10 ? d[^10..] : d;
    }

    public static async Task<List<string>> NumerosConfiguradosAsync(AppDbContext db)
    {
        var raw = (await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == KeyNumeros))?.Value;
        return (raw ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => Clave(x).Length >= 8).ToList();
    }

    /// <summary>Números habilitados (claves de 10 dígitos) + de dónde salen.</summary>
    public static async Task<(HashSet<string> Claves, bool DeAlertas)> HabilitadosAsync(AppDbContext db)
    {
        var conf = await NumerosConfiguradosAsync(db);
        if (conf.Count > 0) return (conf.Select(Clave).ToHashSet(), false);
        var personas = await db.AutoPersonas.AsNoTracking()
            .Where(p => p.Activo && p.WhatsAppNumero != null && p.WhatsAppNumero != "")
            .Select(p => p.WhatsAppNumero!).ToListAsync();
        return (personas.Select(Clave).Where(c => c.Length >= 8).ToHashSet(), true);
    }

    public static bool EsGasto(string? cuerpo) => !string.IsNullOrWhiteSpace(cuerpo) && Disparador.IsMatch(cuerpo);

    /// <summary>Se llama con CADA mensaje entrante (ya guardado). Devuelve true si lo tomó como gasto
    /// (o como foto de un gasto); en ese caso no hay que correr ningún otro robot.</summary>
    public static async Task<bool> TryRegistrarAsync(AppDbContext db, ILogger log, WhatsAppTwilioMensaje msg)
    {
        var esGasto = EsGasto(msg.Cuerpo);
        var esSoloAdjunto = !esGasto && !string.IsNullOrEmpty(msg.MediaUrl);
        if (!esGasto && !esSoloAdjunto) return false;

        var (habilitados, _) = await HabilitadosAsync(db);
        var clave = Clave(msg.Numero);
        if (!habilitados.Contains(clave))
        {
            if (esGasto) log.LogInformation("[#gasto] {Numero} no está habilitado: se ignora", msg.Numero);
            return false;
        }

        if (esSoloAdjunto)
        {
            // Foto/PDF suelto: se pega al último #gasto pendiente sin adjunto del mismo número (10 min).
            var desde = DateTime.UtcNow.AddMinutes(-10);
            var cands = await db.GastosWhatsApp
                .Where(g => g.Estado == "PENDIENTE" && g.MediaUrl == null && g.CreatedAt >= desde)
                .OrderByDescending(g => g.Id).ToListAsync();
            var g = cands.FirstOrDefault(x => Clave(x.Numero) == clave);
            if (g is null) return false;
            g.MediaUrl = msg.MediaUrl;
            g.MediaFilename = msg.MediaFilename;
            await db.SaveChangesAsync();
            log.LogInformation("[#gasto] adjunto pegado al gasto {Id}", g.Id);
            return true;
        }

        var texto = Disparador.Replace(msg.Cuerpo!, "", 1).Trim();
        if (texto.Length > 1000) texto = texto[..1000];
        var gasto = new GastoWhatsApp
        {
            MensajeId = msg.Id,
            Numero = msg.Numero,
            NombrePerfil = msg.NombrePerfil,
            Texto = texto,
            MediaUrl = msg.MediaUrl,
            MediaFilename = msg.MediaFilename,
            Importe = LeerImporte(texto),
            Medio = LeerMedio(texto),
            Estado = "PENDIENTE",
            CreatedAt = DateTime.UtcNow,
        };
        db.GastosWhatsApp.Add(gasto);
        await db.SaveChangesAsync();
        log.LogInformation("[#gasto] nuevo gasto {Id} de {Numero}: {Texto}", gasto.Id, msg.Numero, texto);
        return true;
    }

    // "25000", "25.000", "$25.000,50", "25000.5", "25 mil", "25k"
    private static readonly Regex Numero = new(@"\$?\s*(\d{1,3}(?:\.\d{3})+(?:,\d{1,2})?|\d+(?:[.,]\d{1,2})?)\s*(mil\b|k\b|lucas?\b)?", RegexOptions.IgnoreCase);

    /// <summary>El número más grande del texto (los gastos suelen tener uno solo; si hay "2 cafés 3500",
    /// el importe es el grande). Menos de $10 no se toma (cantidades).</summary>
    public static decimal? LeerImporte(string texto)
    {
        decimal? mejor = null;
        foreach (Match m in Numero.Matches(texto))
        {
            var s = m.Groups[1].Value;
            decimal v;
            if (s.Contains('.') && Regex.IsMatch(s, @"^\d{1,3}(\.\d{3})+(,\d{1,2})?$"))
                v = decimal.Parse(s.Replace(".", "").Replace(",", "."), CultureInfo.InvariantCulture);
            else
                v = decimal.Parse(s.Replace(",", "."), CultureInfo.InvariantCulture);
            if (m.Groups[2].Success) v *= 1000;
            if (v < 10) continue;
            if (mejor is null || v > mejor) mejor = v;
        }
        return mejor;
    }

    public static string? LeerMedio(string texto)
    {
        var t = texto.ToLowerInvariant();
        if (Regex.IsMatch(t, @"\b(efectivo|efvo|efect|cash|billete)")) return "EFECTIVO";
        if (Regex.IsMatch(t, @"\b(mp|mercado\s*pago|mercadopago)\b")) return "MERCADOPAGO";
        if (Regex.IsMatch(t, @"\b(transf|transferencia|galicia|banco|cbu|alias)")) return "TRANSFERENCIA";
        return null;
    }
}
