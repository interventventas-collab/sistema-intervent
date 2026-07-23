-- 2026-07-23: Centro de Automatizaciones (pedido Osmar) — pantalla única para gestionar
-- los robots del sistema: interruptor, días/horario, canales (campanita/Telegram/WhatsApp/correo)
-- y destinatarios por automatización. Idempotente.
--
-- Tablas:
--   Auto_Personas      : la "libretita" — cada persona con sus 3 direcciones (Telegram/WhatsApp/mail)
--   Auto_Config        : configuración por automatización programada (días, hora, canales, última corrida)
--   Auto_Destinatarios : qué personas reciben cada automatización
--
-- Como correrlo: mismo sqlcmd de siempre (ver patch_whatsapp_meta_canal.sql)

IF OBJECT_ID('dbo.Auto_Personas') IS NULL
BEGIN
    CREATE TABLE dbo.Auto_Personas (
        Id int IDENTITY(1,1) PRIMARY KEY,
        Nombre nvarchar(80) NOT NULL,
        TelegramChatId bigint NULL,
        WhatsAppNumero nvarchar(30) NULL,
        Email nvarchar(150) NULL,
        Activo bit NOT NULL DEFAULT 1,
        CreatedAt datetime2 NOT NULL DEFAULT GETUTCDATE()
    );
    PRINT 'Auto_Personas creada';
END

IF OBJECT_ID('dbo.Auto_Config') IS NULL
BEGIN
    CREATE TABLE dbo.Auto_Config (
        AutoKey nvarchar(50) PRIMARY KEY,
        Enabled bit NOT NULL DEFAULT 1,
        Dias nvarchar(20) NOT NULL DEFAULT '1,2,3,4,5,6,7',  -- 1=lunes ... 7=domingo
        Hora int NOT NULL DEFAULT 8,
        CanalCampanita bit NOT NULL DEFAULT 0,
        CanalTelegram bit NOT NULL DEFAULT 1,
        CanalWhatsApp bit NOT NULL DEFAULT 0,
        CanalEmail bit NOT NULL DEFAULT 0,
        LastRunAt datetime2 NULL,
        LastRunOk bit NULL,
        LastRunDetalle nvarchar(300) NULL,
        UpdatedAt datetime2 NOT NULL DEFAULT GETUTCDATE()
    );
    PRINT 'Auto_Config creada';
END

IF OBJECT_ID('dbo.Auto_Destinatarios') IS NULL
BEGIN
    CREATE TABLE dbo.Auto_Destinatarios (
        Id int IDENTITY(1,1) PRIMARY KEY,
        AutoKey nvarchar(50) NOT NULL,
        PersonaId int NOT NULL
    );
    PRINT 'Auto_Destinatarios creada';
END

-- Semillas (solo si están vacías)
IF NOT EXISTS (SELECT 1 FROM dbo.Auto_Config)
BEGIN
    INSERT INTO dbo.Auto_Config (AutoKey, Enabled, Dias, Hora, CanalCampanita, CanalTelegram, CanalWhatsApp, CanalEmail)
    VALUES ('resumen-financiero', 1, '1,2,3,4,5,6,7', 8, 0, 1, 1, 0),
           ('deudas-diario',      1, '1,2,3,4,5,6,7', 8, 0, 1, 0, 0);
    PRINT 'Auto_Config sembrada';
END
