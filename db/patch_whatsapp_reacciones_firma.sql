-- 2026-08-28: quien puso cada reaccion de WhatsApp.
-- Agrega WhatsApp_TwilioReacciones.Firma (NVARCHAR(20)): la abreviatura del que reacciono
-- (oficina: os/ger/ga del PIN; deposito: alex/walter/... de la barrita de firma). NULL = como antes.
-- Sirve para que en el chat de Deposito se vea "un emoji por persona" sin que se pisen entre ellos:
-- el toggle ahora es por (mensaje + emoji + firma), asi nadie borra la reaccion del otro.
-- Idempotente: se puede correr las veces que haga falta.
--
-- Como correrlo:
--   DEV : sudo docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SQL_SA_PASSWORD" -C -d AIml -i /dev/stdin < db/patch_whatsapp_reacciones_firma.sql
--   PROD: idem con -f docker-compose.prod.yml y el servicio sqlserver-prod (y la DB de prod).

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.WhatsApp_TwilioReacciones') AND name = 'Firma'
)
BEGIN
    ALTER TABLE dbo.WhatsApp_TwilioReacciones ADD Firma NVARCHAR(20) NULL;
    PRINT 'Columna Firma agregada.';
END
ELSE
    PRINT 'Columna Firma ya existia.';
GO
