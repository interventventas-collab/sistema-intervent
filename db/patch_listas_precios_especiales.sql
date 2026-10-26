-- ─────────────────────────────────────────────────────────────────────────────
-- 26/10/2026 — LISTAS de precios especiales (pantalla Ventas → Precios especiales).
-- Una lista (ej: "Lista clientes especiales", vasos y tapas) tiene productos con su
-- precio pactado y se aplica a varios clientes. Lo que cobra la venta sigue saliendo
-- de Cafe_PreciosEspecialesCliente: cada cambio en la lista se copia a las filas de
-- sus clientes, marcadas con ListaId. Las filas con ListaId NULL son pactos sueltos
-- de un solo cliente (los de la ficha).
-- ⚠ En PROD correr a mano (con -I):
--   sudo docker compose -f docker-compose.prod.yml exec -T sqlserver-prod \
--     /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SQL_SA_PASSWORD" -C -I \
--     -d AIml -i /dev/stdin < db/patch_listas_precios_especiales.sql
-- Es idempotente.
-- ─────────────────────────────────────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name='Cafe_ListasEspeciales')
BEGIN
    CREATE TABLE Cafe_ListasEspeciales (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Nombre NVARCHAR(100) NOT NULL,
        Notas NVARCHAR(300) NULL,
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedAt DATETIME2 NULL,
        CreatedBy NVARCHAR(100) NULL
    );
END
GO
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name='Cafe_ListasEspecialesItems')
BEGIN
    CREATE TABLE Cafe_ListasEspecialesItems (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        ListaId INT NOT NULL,
        ProductoId INT NOT NULL,
        Formato NVARCHAR(20) NOT NULL,
        Precio DECIMAL(18,2) NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedAt DATETIME2 NULL,
        CONSTRAINT FK_CafeListasEspItems_Lista FOREIGN KEY (ListaId) REFERENCES Cafe_ListasEspeciales(Id) ON DELETE CASCADE,
        CONSTRAINT FK_CafeListasEspItems_Producto FOREIGN KEY (ProductoId) REFERENCES Cafe_Productos(Id) ON DELETE CASCADE
    );
    CREATE UNIQUE INDEX UX_CafeListasEspItems ON Cafe_ListasEspecialesItems (ListaId, ProductoId, Formato);
END
GO
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name='Cafe_ListasEspecialesClientes')
BEGIN
    CREATE TABLE Cafe_ListasEspecialesClientes (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        ListaId INT NOT NULL,
        ClienteId INT NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_CafeListasEspCli_Lista FOREIGN KEY (ListaId) REFERENCES Cafe_ListasEspeciales(Id) ON DELETE CASCADE,
        CONSTRAINT FK_CafeListasEspCli_Cliente FOREIGN KEY (ClienteId) REFERENCES Cafe_Clientes(Id) ON DELETE CASCADE
    );
    CREATE UNIQUE INDEX UX_CafeListasEspCli ON Cafe_ListasEspecialesClientes (ListaId, ClienteId);
END
GO
IF COL_LENGTH('Cafe_PreciosEspecialesCliente','ListaId') IS NULL
    ALTER TABLE Cafe_PreciosEspecialesCliente ADD ListaId INT NULL;
GO
-- La lista de vasos y tapas que se le cargó a Núcleo el 21/09 (Notas = 'Lista clientes
-- especiales 21/09') pasa a ser la primera lista. Solo si todavía no existe.
IF NOT EXISTS (SELECT 1 FROM Cafe_ListasEspeciales)
   AND EXISTS (SELECT 1 FROM Cafe_PreciosEspecialesCliente WHERE IsActive = 1 AND Notas LIKE 'Lista clientes especiales%')
BEGIN
    DECLARE @lista INT;
    INSERT INTO Cafe_ListasEspeciales (Nombre, Notas, CreatedBy)
    VALUES ('Lista clientes especiales', 'vasos y tapas', 'migracion 26/10');
    SET @lista = SCOPE_IDENTITY();

    INSERT INTO Cafe_ListasEspecialesItems (ListaId, ProductoId, Formato, Precio)
    SELECT @lista, ProductoId, Formato, Precio FROM (
        SELECT ProductoId, Formato, Precio,
               ROW_NUMBER() OVER (PARTITION BY ProductoId, Formato ORDER BY ClienteId) rn
        FROM Cafe_PreciosEspecialesCliente
        WHERE IsActive = 1 AND Notas LIKE 'Lista clientes especiales%') x
    WHERE rn = 1;

    INSERT INTO Cafe_ListasEspecialesClientes (ListaId, ClienteId)
    SELECT DISTINCT @lista, ClienteId FROM Cafe_PreciosEspecialesCliente
    WHERE IsActive = 1 AND Notas LIKE 'Lista clientes especiales%';

    UPDATE p SET ListaId = @lista
    FROM Cafe_PreciosEspecialesCliente p
    JOIN Cafe_ListasEspecialesItems i ON i.ListaId = @lista AND i.ProductoId = p.ProductoId
                                     AND i.Formato = p.Formato AND i.Precio = p.Precio
    WHERE p.IsActive = 1 AND p.Notas LIKE 'Lista clientes especiales%';
END
GO
