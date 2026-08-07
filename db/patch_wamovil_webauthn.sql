-- 2026-08-07: huella (WebAuthn) para desbloquear /whatsapp-movil.
-- Crea la tabla WaMovil_WebAuthnCredentials (huellas de Osmar/Germán/Gabriel para el WhatsApp del celu).
-- Independiente de la fichada. Idempotente.
--
-- Como correrlo:
--   DEV : sudo docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SQL_SA_PASSWORD" -C -d AIml -i /dev/stdin < db/patch_wamovil_webauthn.sql
--   PROD: idem con -f docker-compose.prod.yml y el servicio sqlserver-prod (misma DB AIml en su container).

IF OBJECT_ID('dbo.WaMovil_WebAuthnCredentials', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.WaMovil_WebAuthnCredentials (
        Id               INT IDENTITY(1,1) PRIMARY KEY,
        Persona          NVARCHAR(60)  NOT NULL DEFAULT '',
        CredentialId     NVARCHAR(400) NOT NULL DEFAULT '',
        PublicKey        NVARCHAR(2000) NOT NULL DEFAULT '',
        UserHandle       NVARCHAR(200) NOT NULL DEFAULT '',
        AaGuid           NVARCHAR(64)  NULL,
        SignatureCounter BIGINT        NOT NULL DEFAULT 0,
        DeviceName       NVARCHAR(120) NULL,
        CreatedAt        DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        LastUsedAt       DATETIME2     NULL
    );
    PRINT 'Tabla WaMovil_WebAuthnCredentials creada.';
END
ELSE
    PRINT 'Tabla WaMovil_WebAuthnCredentials ya existia.';
GO
