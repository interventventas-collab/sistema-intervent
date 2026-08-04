-- 2026-08-04: estado + responsable por conversación de WhatsApp/Instagram.
-- Permite "pasar" una charla de un operador a otro (OSMAR/GERMAN/GABRIEL/DEPOSITO) y ponerle
-- estado (nueva / en_curso / esperando / finalizada). Una conversación = (Numero + LineaPhoneId).
-- Idempotente: se puede correr varias veces sin romper nada.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name='WhatsApp_Conversaciones')
CREATE TABLE WhatsApp_Conversaciones (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    Numero NVARCHAR(30) NOT NULL,
    LineaPhoneId NVARCHAR(30) NULL,
    Estado NVARCHAR(20) NOT NULL DEFAULT 'nueva',   -- nueva | en_curso | esperando | finalizada
    AsignadoOperador NVARCHAR(40) NULL,             -- OSMAR/GERMAN/GABRIEL/DEPOSITO (firma del operador)
    AsignadoPor NVARCHAR(40) NULL,                  -- quién la pasó (para el aviso "te la pasó X")
    AsignadoNota NVARCHAR(300) NULL,
    AsignadoAt DATETIME2 NULL,
    AsignadoVisto BIT NOT NULL DEFAULT 0,           -- el que la recibió ya la abrió
    UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
-- Una sola fila por (Numero, LineaPhoneId). En SQL Server el índice UNIQUE trata los NULL como
-- iguales, así que también hay a lo sumo una fila para la conversación de "línea sin registrar".
IF EXISTS (SELECT 1 FROM sys.tables WHERE name='WhatsApp_Conversaciones')
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_WaConv_NumeroLinea')
    CREATE UNIQUE INDEX UX_WaConv_NumeroLinea ON WhatsApp_Conversaciones (Numero, LineaPhoneId);
GO
