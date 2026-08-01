-- 2026-08-01: nombre + imagen personalizados por LÍNEA (WhatsApp o Instagram).
-- Idempotente. Correr en dev y prod:
--   sqlcmd -S localhost -U sa -P "$SQL_SA_PASSWORD" -C -d AIml -i db/whatsapp_lineas_config.sql
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'WhatsApp_LineasConfig')
BEGIN
    CREATE TABLE WhatsApp_LineasConfig (
        LineaId       NVARCHAR(60)  NOT NULL PRIMARY KEY,
        Nombre        NVARCHAR(120) NULL,
        ImagenDataUrl NVARCHAR(MAX) NULL,
        UpdatedAt     DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
    );
    PRINT 'WhatsApp_LineasConfig creada';
END
ELSE PRINT 'WhatsApp_LineasConfig ya existe';
