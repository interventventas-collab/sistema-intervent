-- 2026-08-04: tabla de Catalogos permanentes para el chat de WhatsApp.
-- Archivos (PDF/documentos/imagenes) que NO expiran (a diferencia de "Mis subidos").
-- Se aplica manualmente contra la DB que ya existe (init.sql solo corre en DB nueva).
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'WhatsApp_Catalogos')
BEGIN
    CREATE TABLE [WhatsApp_Catalogos] (
        [Id]                INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [Token]             NVARCHAR(64)  NOT NULL,
        [OriginalFilename]  NVARCHAR(255) NOT NULL,
        [StoredFilename]    NVARCHAR(255) NOT NULL,
        [ContentType]       NVARCHAR(120) NOT NULL,
        [SizeBytes]         BIGINT        NOT NULL,
        [UploadedByUserId]  INT           NULL,
        [CreatedAt]         DATETIME2     NOT NULL CONSTRAINT DF_WACat_Created DEFAULT SYSUTCDATETIME()
    );
    CREATE UNIQUE INDEX IX_WACat_Token ON [WhatsApp_Catalogos]([Token]);
    PRINT 'Tabla WhatsApp_Catalogos creada.';
END
ELSE PRINT 'WhatsApp_Catalogos ya existia (no se toca).';
GO
