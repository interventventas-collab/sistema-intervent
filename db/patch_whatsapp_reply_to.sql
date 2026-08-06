-- 2026-08-05: "responder citando" un mensaje (reply/quote de WhatsApp).
-- Agrega WhatsApp_TwilioMensajes.ReplyToSid: guarda el wamid del mensaje CITADO.
--   - Entrante: cuando el cliente responde a un mensaje puntual nuestro, Meta manda
--     messages[].context.id con el wamid citado; lo guardamos acá.
--   - Saliente: cuando NOSOTROS respondemos citando, guardamos el wamid del mensaje al
--     que contestamos (para mostrar la burbuja citada en la pantalla, igual que WhatsApp).
-- La pantalla resuelve ReplyToSid contra TwilioMessageSid del mensaje original.
-- Idempotente: se puede correr las veces que haga falta.
--
-- Como correrlo:
--   DEV : sudo docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SQL_SA_PASSWORD" -C -d AIml -i /dev/stdin < db/patch_whatsapp_reply_to.sql
--   PROD: sudo docker compose -f docker-compose.prod.yml exec -T sqlserver-prod /opt/mssql-tools18/bin/sqlcmd ... (idem con -d AImlProd si aplica)

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.WhatsApp_TwilioMensajes') AND name = 'ReplyToSid'
)
BEGIN
    ALTER TABLE dbo.WhatsApp_TwilioMensajes ADD ReplyToSid nvarchar(200) NULL;
    PRINT 'Columna ReplyToSid agregada.';
END
ELSE
    PRINT 'Columna ReplyToSid ya existia.';
GO
