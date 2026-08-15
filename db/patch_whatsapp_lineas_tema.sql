-- 2026-08-15: TEMA (claro/oscuro) por línea de WhatsApp. Agrega la columna Tema a WhatsApp_LineasConfig.
-- Idempotente. Correr en dev y prod:
--   sqlcmd -S localhost -U sa -P "$SQL_SA_PASSWORD" -C -d AIml -i db/patch_whatsapp_lineas_tema.sql
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('WhatsApp_LineasConfig') AND name = 'Tema')
BEGIN
    ALTER TABLE WhatsApp_LineasConfig ADD Tema NVARCHAR(10) NULL;
    PRINT 'Columna Tema agregada';
END
ELSE PRINT 'Columna Tema ya existe';
