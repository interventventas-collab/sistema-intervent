-- ============================================================================
-- Tablas de la bandeja de WhatsApp (chat + contactos + respuestas rapidas +
-- reacciones + adjuntos). Las usan tanto Twilio como la API oficial de Meta
-- (Cloud API) — se distinguen por la columna [Canal].
--
-- Fecha: 2026-07-19
--
-- IDEMPOTENTE: crea cada tabla solo si no existe. Se puede correr las veces
-- que haga falta, en dev y en prod, sin romper nada ni borrar datos.
--
-- NOTA: estas tablas NO estaban en init.sql (se habian creado a mano solo en
-- prod). Este script las deja reproducibles en cualquier entorno.
--
-- Como correrlo:
--   DEV:  docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd \
--           -S localhost -U sa -P "$SQL_SA_PASSWORD" -C -d AIml -i whatsapp_tablas.sql
--   PROD: idem con -f docker-compose.prod.yml y el container sqlserver-prod.
--         (En prod, si las tablas ya existen, correr ademas patch_whatsapp_meta_canal.sql
--          para agregar la columna [Canal] y agrandar [TwilioMessageSid].)
-- ============================================================================

-- 1) Mensajes (la bandeja del chat) -----------------------------------------
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'WhatsApp_TwilioMensajes')
BEGIN
    CREATE TABLE [WhatsApp_TwilioMensajes] (
        [Id]                INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [Direccion]         NVARCHAR(10)   NOT NULL,             -- INCOMING / OUTGOING
        [Numero]            NVARCHAR(30)   NOT NULL,             -- formato "whatsapp:+E164"
        [NombrePerfil]      NVARCHAR(120)  NULL,
        [Cuerpo]            NVARCHAR(MAX)  NULL,
        [MediaUrl]          NVARCHAR(500)  NULL,
        [NumMedia]          INT            NULL,
        [TwilioMessageSid]  NVARCHAR(200)  NULL,                 -- SID de Twilio o wamid.* de Meta
        [Canal]             NVARCHAR(10)   NOT NULL CONSTRAINT DF_WATwMsg_Canal DEFAULT 'TWILIO',
        [Procesado]         BIT            NOT NULL CONSTRAINT DF_WATwMsg_Proc  DEFAULT 0,
        [PedidoTrigger]     NVARCHAR(10)   NULL,
        [VentaIdGenerada]   INT            NULL,
        [RespuestaEnviada]  NVARCHAR(MAX)  NULL,
        [CreatedAt]         DATETIME2      NOT NULL CONSTRAINT DF_WATwMsg_Created DEFAULT SYSUTCDATETIME()
    );
    CREATE INDEX IX_WATwMsg_Numero_Created ON [WhatsApp_TwilioMensajes]([Numero], [CreatedAt] DESC);
    CREATE INDEX IX_WATwMsg_Sid            ON [WhatsApp_TwilioMensajes]([TwilioMessageSid]);
    PRINT 'Tabla WhatsApp_TwilioMensajes creada.';
END
ELSE PRINT 'WhatsApp_TwilioMensajes ya existia (no se toca).';
GO

-- 2) Contactos (quien es cada numero) ---------------------------------------
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'WhatsApp_TwilioContactos')
BEGIN
    CREATE TABLE [WhatsApp_TwilioContactos] (
        [Id]         INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [Numero]     NVARCHAR(30)  NOT NULL,
        [Nombre]     NVARCHAR(120) NOT NULL,
        [Rol]        NVARCHAR(20)  NOT NULL CONSTRAINT DF_WATwCont_Rol DEFAULT 'otro',  -- cliente/proveedor/otro
        [Notas]      NVARCHAR(MAX) NULL,
        [Activo]     BIT           NOT NULL CONSTRAINT DF_WATwCont_Activo DEFAULT 1,
        [ClienteId]  INT           NULL,                          -- FK logica a Cafe_Clientes
        [CreatedAt]  DATETIME2     NOT NULL CONSTRAINT DF_WATwCont_Created DEFAULT SYSUTCDATETIME()
    );
    CREATE INDEX IX_WATwCont_Numero ON [WhatsApp_TwilioContactos]([Numero]);
    PRINT 'Tabla WhatsApp_TwilioContactos creada.';
END
ELSE PRINT 'WhatsApp_TwilioContactos ya existia (no se toca).';
GO

-- 3) Respuestas rapidas (plantillas internas del operador) -------------------
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'WhatsApp_TwilioRespuestasRapidas')
BEGIN
    CREATE TABLE [WhatsApp_TwilioRespuestasRapidas] (
        [Id]         INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [Nombre]     NVARCHAR(80)  NOT NULL,
        [Texto]      NVARCHAR(MAX) NOT NULL,
        [Orden]      INT           NOT NULL CONSTRAINT DF_WATwRR_Orden  DEFAULT 0,
        [Activo]     BIT           NOT NULL CONSTRAINT DF_WATwRR_Activo DEFAULT 1,
        [CreatedAt]  DATETIME2     NOT NULL CONSTRAINT DF_WATwRR_Created DEFAULT SYSUTCDATETIME()
    );
    PRINT 'Tabla WhatsApp_TwilioRespuestasRapidas creada.';
END
ELSE PRINT 'WhatsApp_TwilioRespuestasRapidas ya existia (no se toca).';
GO

-- 4) Reacciones (emojis sobre mensajes) -------------------------------------
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'WhatsApp_TwilioReacciones')
BEGIN
    CREATE TABLE [WhatsApp_TwilioReacciones] (
        [Id]         INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [MensajeId]  INT          NOT NULL,
        [Emoji]      NVARCHAR(20) NOT NULL,
        [UsuarioId]  INT          NULL,
        [CreatedAt]  DATETIME2    NOT NULL CONSTRAINT DF_WATwReac_Created DEFAULT SYSUTCDATETIME()
    );
    CREATE INDEX IX_WATwReac_Mensaje ON [WhatsApp_TwilioReacciones]([MensajeId]);
    PRINT 'Tabla WhatsApp_TwilioReacciones creada.';
END
ELSE PRINT 'WhatsApp_TwilioReacciones ya existia (no se toca).';
GO

-- 5) Adjuntos subidos (se sirven por URL publica con token) ------------------
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'WhatsApp_TwilioUploads')
BEGIN
    CREATE TABLE [WhatsApp_TwilioUploads] (
        [Id]                INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [Token]             NVARCHAR(64)  NOT NULL,
        [OriginalFilename]  NVARCHAR(255) NOT NULL,
        [StoredFilename]    NVARCHAR(255) NOT NULL,
        [ContentType]       NVARCHAR(120) NOT NULL,
        [SizeBytes]         BIGINT        NOT NULL,
        [UploadedByUserId]  INT           NULL,
        [NumeroDestino]     NVARCHAR(30)  NULL,
        [CreatedAt]         DATETIME2     NOT NULL CONSTRAINT DF_WATwUp_Created DEFAULT SYSUTCDATETIME(),
        [ExpiresAt]         DATETIME2     NOT NULL,
        [DownloadedAt]      DATETIME2     NULL
    );
    CREATE UNIQUE INDEX IX_WATwUp_Token ON [WhatsApp_TwilioUploads]([Token]);
    PRINT 'Tabla WhatsApp_TwilioUploads creada.';
END
ELSE PRINT 'WhatsApp_TwilioUploads ya existia (no se toca).';
GO
