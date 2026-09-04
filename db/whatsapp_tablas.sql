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
        [ReplyToSid]        NVARCHAR(200)  NULL,                 -- 2026-08-05: wamid del mensaje CITADO (responder citando)
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
        -- 2026-08-28: abreviatura de QUIEN reacciono (os/ger/ga, alex/walter...). NULL = sin firma.
        [Firma]      NVARCHAR(20) NULL,
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

-- 6) Datos bancarios / CBUs (2026-07-29) ------------------------------------
--    Cuentas guardadas para pasarle al cliente desde el chat (boton 🏦).
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'WhatsApp_TwilioDatosBancarios')
BEGIN
    CREATE TABLE [WhatsApp_TwilioDatosBancarios] (
        [Id]          INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [Nombre]      NVARCHAR(80)  NOT NULL,
        [Banco]       NVARCHAR(120) NOT NULL CONSTRAINT DF_WATwCbu_Banco   DEFAULT '',
        [TipoCuenta]  NVARCHAR(40)  NOT NULL CONSTRAINT DF_WATwCbu_Tipo    DEFAULT '',
        [Titular]     NVARCHAR(160) NOT NULL CONSTRAINT DF_WATwCbu_Titular DEFAULT '',
        [Cuit]        NVARCHAR(20)  NOT NULL CONSTRAINT DF_WATwCbu_Cuit    DEFAULT '',
        [Cbu]         NVARCHAR(30)  NOT NULL CONSTRAINT DF_WATwCbu_Cbu     DEFAULT '',
        [Alias]       NVARCHAR(60)  NOT NULL CONSTRAINT DF_WATwCbu_Alias   DEFAULT '',
        [Mail]        NVARCHAR(120) NOT NULL CONSTRAINT DF_WATwCbu_Mail    DEFAULT '',
        [Orden]       INT           NOT NULL CONSTRAINT DF_WATwCbu_Orden   DEFAULT 0,
        [Activo]      BIT           NOT NULL CONSTRAINT DF_WATwCbu_Activo  DEFAULT 1,
        [CreatedAt]   DATETIME2     NOT NULL CONSTRAINT DF_WATwCbu_Created DEFAULT SYSUTCDATETIME()
    );
    PRINT 'Tabla WhatsApp_TwilioDatosBancarios creada.';
END
ELSE PRINT 'WhatsApp_TwilioDatosBancarios ya existia (no se toca).';
GO

-- 7) Catalogos permanentes (2026-08-04) -------------------------------------
--    Archivos (PDF/documentos/imagenes) que quedan guardados para siempre y se
--    pueden mandar por el chat desde la pestana "Catalogos". No expiran a las 24h
--    como "Mis subidos"; solo se borran cuando el operador toca el boton borrar.
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

-- 8) Color del boton de cada respuesta rapida (2026-09-04) -------------------
--    Para que el operador distinga las respuestas de un vistazo en el cajon del
--    chat. NULL = el celeste de siempre. Se guarda la clave del color
--    ("rojo", "verde", ...), NO un codigo hexadecimal: asi el dia que cambie la
--    paleta no hay que tocar la base.
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
               WHERE TABLE_NAME = 'WhatsApp_TwilioRespuestasRapidas' AND COLUMN_NAME = 'Color')
BEGIN
    ALTER TABLE [WhatsApp_TwilioRespuestasRapidas] ADD [Color] NVARCHAR(20) NULL;
    PRINT 'Columna Color agregada a WhatsApp_TwilioRespuestasRapidas.';
END
ELSE PRINT 'Columna Color ya existia (no se toca).';
GO
