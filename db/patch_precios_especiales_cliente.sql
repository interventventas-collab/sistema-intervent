-- ─────────────────────────────────────────────────────────────────────────────
-- 08/09/2026 — Precios PACTADOS por cliente (Cafe_PreciosEspecialesCliente).
-- Correr A MANO en PRODUCCION antes de publicar: los CREATE/ALTER de init.sql no
-- se aplican solos sobre una base que ya existe.
--
--   sudo docker compose -f docker-compose.prod.yml exec -T sqlserver-prod \
--     /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SQL_SA_PASSWORD" -C -I \
--     -d AIml -i /dev/stdin < db/patch_precios_especiales_cliente.sql
--
-- Es idempotente: si la tabla ya existe, no hace nada.
-- ─────────────────────────────────────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name='Cafe_PreciosEspecialesCliente')
BEGIN
    CREATE TABLE Cafe_PreciosEspecialesCliente (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        ClienteId INT NOT NULL,
        ProductoId INT NOT NULL,
        Formato NVARCHAR(20) NOT NULL,
        Precio DECIMAL(18,2) NOT NULL,
        Notas NVARCHAR(300) NULL,
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedAt DATETIME2 NULL,
        CreatedBy NVARCHAR(100) NULL,
        CONSTRAINT FK_CafePreciosEsp_Cliente FOREIGN KEY (ClienteId) REFERENCES Cafe_Clientes(Id) ON DELETE CASCADE,
        CONSTRAINT FK_CafePreciosEsp_Producto FOREIGN KEY (ProductoId) REFERENCES Cafe_Productos(Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_CafePreciosEsp_Cliente ON Cafe_PreciosEspecialesCliente (ClienteId) WHERE IsActive = 1;
    CREATE UNIQUE INDEX UX_CafePreciosEsp_Cli_Prod_Fmt
        ON Cafe_PreciosEspecialesCliente (ClienteId, ProductoId, Formato) WHERE IsActive = 1;
    PRINT 'Cafe_PreciosEspecialesCliente creada.';
END
ELSE
    PRINT 'Cafe_PreciosEspecialesCliente ya existia, no se toco nada.';
GO
