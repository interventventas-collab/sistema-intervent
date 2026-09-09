using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-09-09 — El aviso que le tapa la pantalla a Depósito.
///
/// Pedido de Osmar: *"si Gaby escribe @ojo que les aparezca en la pantalla y les suene, que lo
/// tengan que ver sí o sí"*, más un botón para mandarles un zumbido desde la oficina.
///
/// Se dispara sólo desde los chats que Depósito ve (`whatsapp.deposito.chats`): un @ojo en el chat
/// de un cliente cualquiera no tiene por qué tapar nada.
///
/// ⚠ La palabra clave lleva arroba a propósito. "ojo" solo no sirve: Gabriel lo escribe en frases
/// normales todo el tiempo ("ojo que llego tarde") y el cartel saltaría varias veces por día —
/// en dos semanas lo tocarían sin leer y el aviso dejaría de servir.
/// </summary>
public class AvisoDepositoService
{
    public const string KeyPalabras = "whatsapp.deposito.aviso.palabras";
    public const string KeyDepositoChats = "whatsapp.deposito.chats";

    /// <summary>Las que eligió él. Se pueden cambiar desde AppSettings sin tocar el programa.</summary>
    public static readonly string[] PalabrasPorDefecto = { "@ojo", "🔴" };

    private readonly AppDbContext _db;
    private readonly WaLiveNotifier _live;
    private readonly ILogger<AvisoDepositoService> _log;

    public AvisoDepositoService(AppDbContext db, WaLiveNotifier live, ILogger<AvisoDepositoService> log)
    {
        _db = db; _live = live; _log = log;
    }

    public async Task<string[]> PalabrasAsync()
    {
        var fila = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == KeyPalabras);
        if (fila is null || string.IsNullOrWhiteSpace(fila.Value)) return PalabrasPorDefecto;
        var lista = fila.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lista.Length == 0 ? PalabrasPorDefecto : lista;
    }

    private static string SoloDigitos(string? s)
        => new(( s ?? "").Where(char.IsDigit).ToArray());

    /// <summary>¿Este chat es uno de los que ve Depósito?</summary>
    private async Task<bool> EsChatDeDepositoAsync(string? numero, string? linea)
    {
        var fila = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == KeyDepositoChats);
        // 2026-09-09: si la clave no está guardada, el resto del sistema usa la lista histórica
        // (Gabriel / FIJO TRANSRADIO). Sin esta misma caída, el aviso no saltaría NUNCA en una base
        // donde nadie tocó la pantalla de asignación — y nadie se enteraría de por qué.
        if (fila is null || string.IsNullOrWhiteSpace(fila.Value))
            return SoloDigitos(numero) == "5491158464160";
        try
        {
            using var doc = JsonDocument.Parse(fila.Value);
            var num = SoloDigitos(numero);
            var lin = SoloDigitos(linea);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var n = SoloDigitos(el.TryGetProperty("Numero", out var pn) ? pn.GetString()
                    : el.TryGetProperty("numero", out var pn2) ? pn2.GetString() : null);
                var l = SoloDigitos(el.TryGetProperty("Linea", out var pl) ? pl.GetString()
                    : el.TryGetProperty("linea", out var pl2) ? pl2.GetString() : null);
                if (n.Length > 0 && n == num && (l.Length == 0 || l == lin)) return true;
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "[AvisoDeposito] lista de chats ilegible"); }
        return false;
    }

    /// <summary>Saca la palabra clave del texto: el cartel ya dice que es importante, repetirla es ruido.</summary>
    private static string LimpiarTexto(string cuerpo, string[] palabras)
    {
        var t = cuerpo;
        foreach (var p in palabras)
        {
            var i = t.IndexOf(p, StringComparison.OrdinalIgnoreCase);
            while (i >= 0)
            {
                t = t.Remove(i, p.Length);
                i = t.IndexOf(p, StringComparison.OrdinalIgnoreCase);
            }
        }
        return t.Trim().TrimStart('-', '·', ',', ':').Trim();
    }

    /// <summary>
    /// Lo llama el webhook con cada mensaje ENTRANTE. Si no corresponde, no hace nada.
    /// Nunca tira excepción: un aviso que falla no puede romper la entrada de un mensaje.
    /// </summary>
    public async Task RevisarMensajeAsync(string? numero, string? linea, string? cuerpo, string? nombrePerfil)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cuerpo)) return;
            var palabras = await PalabrasAsync();
            if (!palabras.Any(p => cuerpo.Contains(p, StringComparison.OrdinalIgnoreCase))) return;
            if (!await EsChatDeDepositoAsync(numero, linea)) return;

            var quien = string.IsNullOrWhiteSpace(nombrePerfil) ? "Gabriel" : nombrePerfil!.Trim();
            var texto = LimpiarTexto(cuerpo, palabras);
            if (texto.Length == 0) texto = "(mandó la marca sin texto)";
            if (texto.Length > 1000) texto = texto[..1000];

            await CrearAsync(numero, linea, $"Mensaje importante de {quien}", texto,
                WhatsAppAvisoDeposito.OrigenMensaje, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[AvisoDeposito] no se pudo crear el aviso de {Numero}", numero);
        }
    }

    /// <summary>Crea el aviso y lo empuja a las pantallas abiertas en el momento.</summary>
    public async Task<WhatsAppAvisoDeposito> CrearAsync(
        string? numero, string? linea, string titulo, string texto, string origen, string? creadoPor)
    {
        var aviso = new WhatsAppAvisoDeposito
        {
            Numero = numero,
            LineaPhoneId = linea,
            Titulo = titulo.Length > 120 ? titulo[..120] : titulo,
            Texto = texto.Length > 1000 ? texto[..1000] : texto,
            Origen = origen,
            CreadoPor = creadoPor
        };
        _db.WhatsAppAvisosDeposito.Add(aviso);
        await _db.SaveChangesAsync();
        await _live.AvisarDepositoAsync(aviso.Id);
        return aviso;
    }
}
