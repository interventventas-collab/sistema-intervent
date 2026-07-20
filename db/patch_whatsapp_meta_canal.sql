-- ============================================================================
-- Patch: soporte de WhatsApp Cloud API (Meta) en la bandeja compartida.
-- Fecha: 2026-07-18
--
-- Agrega la columna [Canal] a WhatsApp_TwilioMensajes (para distinguir mensajes
-- que vienen por Twilio de los que vienen por la API oficial de Meta) y agranda
-- [TwilioMessageSid] a 200 chars (el wamid.* de Meta es mas largo que un SID de Twilio).
--
-- IDEMPOTENTE: se puede correr varias veces sin romper nada. Solo actua si la
-- tabla existe (en DEV, si todavia no existe la suite WhatsApp_Twilio*, no hace nada).
--
-- Como correrlo en produccion:
--   docker compose -f docker-compose.prod.yml exec -T sqlserver-prod \
--     /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SQL_SA_PASSWORD" -C -d AIml \
--     -i /path/al/patch_whatsapp_meta_canal.sql
-- (o pegarlo directo). NOTA: init.sql NO crea estas tablas; se administran a mano.
-- ============================================================================

IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'WhatsApp_TwilioMensajes')
BEGIN
    -- 1) Columna Canal (default 'TWILIO' para las filas existentes).
    IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                   WHERE TABLE_NAME = 'WhatsApp_TwilioMensajes' AND COLUMN_NAME = 'Canal')
    BEGIN
        ALTER TABLE [WhatsApp_TwilioMensajes]
            ADD [Canal] NVARCHAR(10) NOT NULL CONSTRAINT DF_WATwMsg_Canal DEFAULT 'TWILIO';
        PRINT 'Columna [Canal] agregada a WhatsApp_TwilioMensajes.';
    END
    ELSE
        PRINT 'Columna [Canal] ya existia, no se toca.';

    -- 2) Agrandar TwilioMessageSid a 200 (wamid de Meta es largo).
    IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
               WHERE TABLE_NAME = 'WhatsApp_TwilioMensajes' AND COLUMN_NAME = 'TwilioMessageSid'
                     AND CHARACTER_MAXIMUM_LENGTH < 200)
    BEGIN
        ALTER TABLE [WhatsApp_TwilioMensajes]
            ALTER COLUMN [TwilioMessageSid] NVARCHAR(200) NULL;
        PRINT 'Columna [TwilioMessageSid] agrandada a 200.';
    END
    ELSE
        PRINT 'Columna [TwilioMessageSid] ya tenia tamano suficiente.';
END
ELSE
    PRINT 'Tabla WhatsApp_TwilioMensajes no existe en esta base (nada que hacer).';
GO
