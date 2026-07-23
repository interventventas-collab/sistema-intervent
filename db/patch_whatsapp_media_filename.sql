-- 2026-07-23: mostrar nombre y tipo del archivo adjunto en el chat de WhatsApp (pedido Osmar).
-- Agrega WhatsApp_TwilioMensajes.MediaFilename (nombre original del adjunto, ej "Lista Take Away.pdf").
-- Ademas backfillea los mensajes viejos cruzando con WhatsApp_TwilioUploads por el token de la URL.
-- Idempotente: se puede correr las veces que haga falta.
--
-- Como correrlo:
--   DEV : sudo docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SQL_SA_PASSWORD" -C -d AIml -i /dev/stdin < db/patch_whatsapp_media_filename.sql
--   PROD: sudo docker compose -f docker-compose.prod.yml exec -T sqlserver-prod /opt/mssql-tools18/bin/sqlcmd ... (idem)

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.WhatsApp_TwilioMensajes') AND name = 'MediaFilename'
)
BEGIN
    ALTER TABLE dbo.WhatsApp_TwilioMensajes ADD MediaFilename nvarchar(300) NULL;
    PRINT 'Columna MediaFilename agregada.';
END
ELSE
    PRINT 'Columna MediaFilename ya existia.';
GO

-- Backfill de los mensajes que ya tienen adjunto: sacar el nombre original del upload
UPDATE m SET m.MediaFilename = u.OriginalFilename
FROM dbo.WhatsApp_TwilioMensajes m
JOIN dbo.WhatsApp_TwilioUploads u ON m.MediaUrl LIKE '%/files/' + u.Token + '%'
WHERE m.MediaFilename IS NULL AND m.MediaUrl IS NOT NULL;
PRINT 'Backfill de nombres hecho.';
