-- 2026-08-01: backfill de LineaPhoneId en mensajes SALIENTES.
-- Antes los OUTGOING no guardaban la línea (quedaba NULL). Con el nuevo agrupamiento por
-- (Número + Línea), esos salientes null se separarían del hilo. Este patch les asigna la
-- línea del ÚLTIMO entrante de ese mismo número (histórico = 1 sola línea, así que es correcto).
-- Idempotente: solo toca los que están en NULL. Twilio queda en NULL (no tiene línea).
-- Correr en dev y en prod (una vez).
--   sqlcmd -S localhost -U sa -P "$SQL_SA_PASSWORD" -C -d AIml -i db/patch_whatsapp_backfill_linea_outgoing.sql

UPDATE m
SET m.LineaPhoneId = x.Linea
FROM WhatsApp_TwilioMensajes m
CROSS APPLY (
    SELECT TOP 1 i.LineaPhoneId AS Linea
    FROM WhatsApp_TwilioMensajes i
    WHERE i.Numero = m.Numero AND i.Direccion = 'INCOMING' AND i.LineaPhoneId IS NOT NULL
    ORDER BY i.CreatedAt DESC
) x
WHERE m.Direccion = 'OUTGOING' AND m.LineaPhoneId IS NULL AND m.Canal IN ('CLOUD','INSTAGRAM');

PRINT 'Backfill LineaPhoneId salientes: OK';
