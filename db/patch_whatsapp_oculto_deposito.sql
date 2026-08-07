-- 2026-08-07: ocultar mensajes puntuales al usuario DEPÓSITO.
-- Agrega WhatsApp_TwilioMensajes.OcultoDeposito (BIT): si está en 1, ese mensaje NO se le muestra
-- a los usuarios de Depósito (ven un cartelito "Mensaje ocultado" en su lugar). Lo marca admin/oficina.
-- Caso típico: se pasa un pedido, se modifica y se remanda; el viejo se oculta para que Depósito arme el nuevo.
-- Idempotente: se puede correr las veces que haga falta.
--
-- Como correrlo:
--   DEV : sudo docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SQL_SA_PASSWORD" -C -d AIml -i /dev/stdin < db/patch_whatsapp_oculto_deposito.sql
--   PROD: idem con -f docker-compose.prod.yml y el servicio sqlserver-prod (y la DB de prod).

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.WhatsApp_TwilioMensajes') AND name = 'OcultoDeposito'
)
BEGIN
    ALTER TABLE dbo.WhatsApp_TwilioMensajes ADD OcultoDeposito BIT NOT NULL CONSTRAINT DF_WaTwMsg_OcultoDeposito DEFAULT 0;
    PRINT 'Columna OcultoDeposito agregada.';
END
ELSE
    PRINT 'Columna OcultoDeposito ya existia.';
GO
