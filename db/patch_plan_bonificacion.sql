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
