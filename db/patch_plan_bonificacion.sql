-- ─────────────────────────────────────────────────────────────────────────────
-- 29/09/2026 — PLAN DE BONIFICACION POR CLIENTE (Nucleo).
-- Cada 5 kg de F1 pagados en una venta -> 1 caja D401 bonificada, y a fin de mes
-- el 10% de los kg de F1 del mes en kg de F1 (redondeado para arriba, editable).
-- Lo bonificado (renglon con 100% de descuento) NO suma kg.
-- Cafe_BonificacionesMes guarda solo los meses que se cambiaron a mano.
-- ─────────────────────────────────────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name='Cafe_PlanesBonificacion')
BEGIN
    CREATE TABLE Cafe_PlanesBonificacion (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        ClienteId INT NOT NULL,
        Activo BIT NOT NULL DEFAULT 1,
        ProductoKgId INT NOT NULL,
        KgPorRegalo DECIMAL(18,3) NOT NULL DEFAULT 5,
        ProductoRegaloId INT NULL,
        CantidadRegalo INT NOT NULL DEFAULT 1,
        PctMensual DECIMAL(5,2) NOT NULL DEFAULT 10,
        Desde DATETIME2 NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedAt DATETIME2 NULL,
        UpdatedBy NVARCHAR(100) NULL,
        CONSTRAINT FK_CafePlanesBonif_Cliente FOREIGN KEY (ClienteId) REFERENCES Cafe_Clientes(Id) ON DELETE CASCADE
    );
    CREATE UNIQUE INDEX UX_CafePlanesBonif_Cliente ON Cafe_PlanesBonificacion (ClienteId);
END
GO
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name='Cafe_BonificacionesMes')
BEGIN
    CREATE TABLE Cafe_BonificacionesMes (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        ClienteId INT NOT NULL,
        Anio INT NOT NULL,
        Mes INT NOT NULL,
        KgOtorgado DECIMAL(18,3) NOT NULL,
        Nota NVARCHAR(300) NULL,
        UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedBy NVARCHAR(100) NULL,
        CONSTRAINT FK_CafeBonifMes_Cliente FOREIGN KEY (ClienteId) REFERENCES Cafe_Clientes(Id) ON DELETE CASCADE
    );
    CREATE UNIQUE INDEX UX_CafeBonifMes_Cli_Mes ON Cafe_BonificacionesMes (ClienteId, Anio, Mes);
END
GO
-- 29/09/2026 (2) — AVISOS POR WHATSAPP A LOS INTERNOS (Gabriel):
--   * en cada venta de un cliente con plan de bonificacion: lo que lleva + la bonificacion.
--   * lo que debe un cliente, programado desde la ficha (una vez / semanal / mensual).
--   A quien: Auto_Destinatarios ("bonif-venta:{clienteId}" / "deuda-cli:{avisoId}").
--   WhatsApp_MensajesProgramados.EsperarVentana: si la persona no escribio en 24 hs, el
--   mensaje queda esperando en vez de fallar.
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'AvisarEnCadaVenta' AND Object_ID = Object_ID('Cafe_PlanesBonificacion'))
    ALTER TABLE Cafe_PlanesBonificacion ADD AvisarEnCadaVenta BIT NOT NULL CONSTRAINT DF_CafePlanesBonif_Avisar DEFAULT 0;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'EsperarVentana' AND Object_ID = Object_ID('WhatsApp_MensajesProgramados'))
    ALTER TABLE WhatsApp_MensajesProgramados ADD EsperarVentana BIT NOT NULL CONSTRAINT DF_WaProg_EsperarVentana DEFAULT 0;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'Origen' AND Object_ID = Object_ID('WhatsApp_MensajesProgramados'))
    ALTER TABLE WhatsApp_MensajesProgramados ADD Origen NVARCHAR(60) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name='Cafe_AvisosDeuda')
BEGIN
    CREATE TABLE Cafe_AvisosDeuda (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        ClienteId INT NOT NULL,
        Frecuencia NVARCHAR(10) NOT NULL,
        Fecha DATETIME2 NULL,
        DiaSemana INT NULL,
        DiaMes INT NULL,
        HoraMin INT NOT NULL DEFAULT 600,
        ProximoEnvio DATETIME2 NULL,
        Activo BIT NOT NULL DEFAULT 1,
        UltimoEnvioAt DATETIME2 NULL,
        UltimoResultado NVARCHAR(400) NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(100) NULL,
        CONSTRAINT FK_CafeAvisosDeuda_Cliente FOREIGN KEY (ClienteId) REFERENCES Cafe_Clientes(Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_CafeAvisosDeuda_Cliente ON Cafe_AvisosDeuda (ClienteId);
END
GO
