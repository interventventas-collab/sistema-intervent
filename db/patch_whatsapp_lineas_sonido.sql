-- 2026-08-01: sonido de aviso por línea. Agrega la columna Sonido a WhatsApp_LineasConfig.
-- Idempotente. Correr en dev y prod:
--   sqlcmd -S localhost -U sa -P "$SQL_SA_PASSWORD" -C -d AIml -i db/patch_whatsapp_lineas_sonido.sql
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('WhatsApp_LineasConfig') AND name = 'Sonido')
BEGIN
    ALTER TABLE WhatsApp_LineasConfig ADD Sonido NVARCHAR(30) NULL;
    PRINT 'Columna Sonido agregada';
END
ELSE PRINT 'Columna Sonido ya existe';
