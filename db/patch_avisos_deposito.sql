-- ─────────────────────────────────────────────────────────────────────────────
-- 09/09/2026 — AVISO IMPORTANTE PARA DEPOSITO.
-- Osmar: "si Gaby escribe @ojo que les aparezca en la pantalla y les suene, que
-- lo tengan que ver si o si. Y yo, desde mi sesion, un boton que les de un zumbido".
--
--   Origen: 'mensaje' (lo disparo una palabra clave de Gaby) | 'boton' (lo mando la oficina).
--   VistoPor / VistoAt: quien lo vio y cuando. Es la vuelta que hoy no existe:
--   sin esto no hay forma de saber si el mensaje se leyo o quedo enterrado.
--
-- ⚠ Correr A MANO en PROD antes de publicar. Idempotente y NO corta nada.
-- ─────────────────────────────────────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name='WhatsApp_AvisosDeposito')
BEGIN
    CREATE TABLE WhatsApp_AvisosDeposito (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Numero NVARCHAR(60) NULL,
        LineaPhoneId NVARCHAR(60) NULL,
        Titulo NVARCHAR(120) NOT NULL,
        Texto NVARCHAR(1000) NOT NULL,
        Origen NVARCHAR(20) NOT NULL,
        CreadoPor NVARCHAR(100) NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        VistoPor NVARCHAR(100) NULL,
        VistoAt DATETIME2 NULL
    );
    CREATE INDEX IX_WaAvisosDeposito_Pendientes ON WhatsApp_AvisosDeposito (CreatedAt) WHERE VistoAt IS NULL;
END
GO
