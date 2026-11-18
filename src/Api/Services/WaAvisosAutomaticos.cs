using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-11-18: interruptor GENERAL "📱 Avisos automáticos por WhatsApp" (pantalla Mis alertas).
/// Apagado = ningún robot manda WhatsApp por su cuenta (resúmenes diarios, alertas, avisos del
/// repartidor, Drive caído, cierre de ventana, avisos a internos en cola). La campanita, Telegram
/// y mail siguen igual. Lo que manda una persona a mano (chat, botones "enviar por WhatsApp",
/// "probar", "mandalo ahora") y las respuestas de los bots a un mensaje que entró NO se tocan.
///
/// Vive en AppSettings, clave "wa.avisos_automaticos": "1" prendido, "0" apagado.
/// Si la fila no existe se toma como PRENDIDO (comportamiento de siempre).
/// </summary>
public static class WaAvisosAutomaticos
{
    public const string Key = "wa.avisos_automaticos";

    public static async Task<bool> EstanPrendidosAsync(AppDbContext db)
    {
        var v = await db.AppSettings.AsNoTracking()
            .Where(x => x.Key == Key).Select(x => x.Value).FirstOrDefaultAsync();
        return v != "0";
    }

    public static async Task SetAsync(AppDbContext db, bool prendidos)
    {
        var s = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == Key);
        if (s is null) db.AppSettings.Add(s = new AppSetting { Key = Key });
        s.Value = prendidos ? "1" : "0";
        s.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }
}
