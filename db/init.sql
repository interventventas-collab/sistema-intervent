USE master;
GO

IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = 'AIml')
BEGIN
    CREATE DATABASE AIml;
END
GO

USE AIml;
GO

-- Roles table
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Roles' AND xtype='U')
BEGIN
    CREATE TABLE Roles (
        Id INT PRIMARY KEY IDENTITY(1,1),
        Name NVARCHAR(50) NOT NULL UNIQUE,
        Description NVARCHAR(255) NULL,
        CreatedAt DATETIME2 DEFAULT GETDATE()
    );
END
GO

-- Seed default roles
IF NOT EXISTS (SELECT * FROM Roles WHERE Name = 'admin')
BEGIN
    INSERT INTO Roles (Name, Description) VALUES ('admin', 'Administrador con acceso total');
END
GO

IF NOT EXISTS (SELECT * FROM Roles WHERE Name = 'usuario')
BEGIN
    INSERT INTO Roles (Name, Description) VALUES ('usuario', 'Usuario con acceso basico');
END
GO

-- Users table
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Users' AND xtype='U')
BEGIN
    CREATE TABLE Users (
        Id INT PRIMARY KEY IDENTITY(1,1),
        Username NVARCHAR(100) NOT NULL UNIQUE,
        Email NVARCHAR(255) NOT NULL UNIQUE,
        PasswordHash NVARCHAR(255) NOT NULL,
        FirstName NVARCHAR(100) NULL,
        LastName NVARCHAR(100) NULL,
        Phone NVARCHAR(50) NULL,
        Role NVARCHAR(50) NOT NULL DEFAULT 'usuario',
        RoleId INT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 DEFAULT GETDATE(),
        IsActive BIT DEFAULT 1,
        CONSTRAINT FK_Users_Roles FOREIGN KEY (RoleId) REFERENCES Roles(Id)
    );
END
GO

-- Add new columns if table already exists but columns don't
IF EXISTS (SELECT * FROM sysobjects WHERE name='Users' AND xtype='U')
   AND NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'FirstName')
BEGIN
    ALTER TABLE Users ADD FirstName NVARCHAR(100) NULL;
    ALTER TABLE Users ADD LastName NVARCHAR(100) NULL;
    ALTER TABLE Users ADD Phone NVARCHAR(50) NULL;
END
GO

-- Add RoleId column if it doesn't exist (step 1: add column)
IF EXISTS (SELECT * FROM sysobjects WHERE name='Users' AND xtype='U')
   AND NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'RoleId')
BEGIN
    ALTER TABLE Users ADD RoleId INT NULL;
END
GO

-- Add RoleId column (step 2: populate data - must be separate batch)
IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'RoleId')
   AND EXISTS (SELECT * FROM Users WHERE RoleId IS NULL)
BEGIN
    UPDATE Users SET RoleId = 1 WHERE Role = 'admin';
    UPDATE Users SET RoleId = 2 WHERE Role != 'admin' OR RoleId IS NULL;
END
GO

-- Add RoleId column (step 3: add constraints)
IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'RoleId')
   AND NOT EXISTS (SELECT * FROM sys.default_constraints WHERE name = 'DF_Users_RoleId')
BEGIN
    -- Make it not null with default
    IF NOT EXISTS (SELECT * FROM Users WHERE RoleId IS NULL)
    BEGIN
        ALTER TABLE Users ALTER COLUMN RoleId INT NOT NULL;
    END
    ALTER TABLE Users ADD CONSTRAINT DF_Users_RoleId DEFAULT 2 FOR RoleId;
END
GO

IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'RoleId')
   AND NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE name = 'FK_Users_Roles')
BEGIN
    ALTER TABLE Users ADD CONSTRAINT FK_Users_Roles FOREIGN KEY (RoleId) REFERENCES Roles(Id);
END
GO

-- Integrations table
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Integrations' AND xtype='U')
BEGIN
    CREATE TABLE Integrations (
        Id INT PRIMARY KEY IDENTITY(1,1),
        Provider NVARCHAR(50) NOT NULL UNIQUE,
        AppId NVARCHAR(255) NULL,
        AppSecret NVARCHAR(255) NULL,
        RedirectUrl NVARCHAR(500) NULL,
    Settings NVARCHAR(MAX) NULL,
        IsActive BIT DEFAULT 0,
        CreatedAt DATETIME2 DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL
    );
END
GO

-- MeliAccounts table
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='MeliAccounts' AND xtype='U')
BEGIN
    CREATE TABLE MeliAccounts (
        Id INT PRIMARY KEY IDENTITY(1,1),
        MeliUserId BIGINT NOT NULL UNIQUE,
        Nickname NVARCHAR(255) NOT NULL,
        Email NVARCHAR(255) NULL,
        AccessToken NVARCHAR(MAX) NOT NULL,
        RefreshToken NVARCHAR(MAX) NULL,
        TokenExpiresAt DATETIME2 NOT NULL,
        CreatedAt DATETIME2 DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL
    );
END
GO

-- MeliOrders table
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='MeliOrders' AND xtype='U')
BEGIN
    CREATE TABLE MeliOrders (
        Id INT PRIMARY KEY IDENTITY(1,1),
        MeliOrderId BIGINT NOT NULL,
        MeliAccountId INT NOT NULL,
        Status NVARCHAR(50) NOT NULL,
        DateCreated DATETIME2 NOT NULL,
        DateClosed DATETIME2 NULL,
        TotalAmount DECIMAL(18,2) NOT NULL,
        CurrencyId NVARCHAR(10) NOT NULL,
        BuyerId BIGINT NOT NULL,
        BuyerNickname NVARCHAR(255) NOT NULL,
        ItemId NVARCHAR(50) NOT NULL,
        ItemTitle NVARCHAR(500) NOT NULL,
        Quantity INT NOT NULL,
        UnitPrice DECIMAL(18,2) NOT NULL,
        FullUnitPrice DECIMAL(18,2) NULL,
        ShippingId BIGINT NULL,
        PackId BIGINT NULL,
        ShippingStatus NVARCHAR(50) NULL,
        ShippingSubstatus NVARCHAR(100) NULL,
        CreatedAt DATETIME2 DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL,
        CONSTRAINT FK_MeliOrders_MeliAccounts FOREIGN KEY (MeliAccountId) REFERENCES MeliAccounts(Id)
    );
    CREATE UNIQUE INDEX IX_MeliOrders_MeliOrderId_ItemId ON MeliOrders (MeliOrderId, ItemId);
    CREATE INDEX IX_MeliOrders_MeliAccountId ON MeliOrders (MeliAccountId);
    CREATE INDEX IX_MeliOrders_DateCreated ON MeliOrders (DateCreated);
    CREATE INDEX IX_MeliOrders_PackId ON MeliOrders (PackId);
END
GO

-- Add PackId column if table already exists
IF EXISTS (SELECT * FROM sysobjects WHERE name='MeliOrders' AND xtype='U')
   AND NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('MeliOrders') AND name = 'PackId')
BEGIN
    ALTER TABLE MeliOrders ADD PackId BIGINT NULL;
    CREATE INDEX IX_MeliOrders_PackId ON MeliOrders (PackId);
END
GO

-- Add ShippingStatus column if table already exists
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('MeliOrders') AND name = 'ShippingStatus')
BEGIN
    ALTER TABLE MeliOrders ADD ShippingStatus NVARCHAR(50) NULL;
END
GO

-- Add ShippingSubstatus column if table already exists
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('MeliOrders') AND name = 'ShippingSubstatus')
BEGIN
    ALTER TABLE MeliOrders ADD ShippingSubstatus NVARCHAR(100) NULL;
END
GO

-- Add FullUnitPrice column if table already exists
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('MeliOrders') AND name = 'FullUnitPrice')
BEGIN
    ALTER TABLE MeliOrders ADD FullUnitPrice DECIMAL(18,2) NULL;
END
GO

-- MeliItems table
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='MeliItems' AND xtype='U')
BEGIN
    CREATE TABLE MeliItems (
        Id INT PRIMARY KEY IDENTITY(1,1),
        MeliItemId NVARCHAR(50) NOT NULL,
        MeliAccountId INT NOT NULL,
        Title NVARCHAR(500) NOT NULL,
        CategoryId NVARCHAR(50) NULL,
        Price DECIMAL(18,2) NOT NULL DEFAULT 0,
        OriginalPrice DECIMAL(18,2) NULL,
        CurrencyId NVARCHAR(10) NOT NULL DEFAULT 'ARS',
        AvailableQuantity INT NOT NULL DEFAULT 0,
        SoldQuantity INT NOT NULL DEFAULT 0,
        Status NVARCHAR(50) NOT NULL DEFAULT 'active',
        Condition NVARCHAR(20) NULL,
        ListingTypeId NVARCHAR(50) NULL,
        Thumbnail NVARCHAR(500) NULL,
        Permalink NVARCHAR(1000) NULL,
        Sku NVARCHAR(255) NULL,
        UserProductId NVARCHAR(100) NULL,
        FamilyId NVARCHAR(100) NULL,
        FamilyName NVARCHAR(500) NULL,
        DateCreated DATETIME2 NULL,
        LastUpdated DATETIME2 NULL,
        CreatedAt DATETIME2 DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL,
        CONSTRAINT FK_MeliItems_MeliAccounts FOREIGN KEY (MeliAccountId) REFERENCES MeliAccounts(Id) ON DELETE CASCADE
    );
    CREATE UNIQUE INDEX IX_MeliItems_MeliItemId ON MeliItems (MeliItemId);
    CREATE INDEX IX_MeliItems_MeliAccountId ON MeliItems (MeliAccountId);
    CREATE INDEX IX_MeliItems_Status ON MeliItems (Status);
    CREATE INDEX IX_MeliItems_UserProductId ON MeliItems (UserProductId);
    CREATE INDEX IX_MeliItems_FamilyId ON MeliItems (FamilyId);
END
GO

-- Migracion: vincular publicaciones de MeLi tambien a Combos (no solo a Productos)
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'ComboId' AND Object_ID = Object_ID(N'MeliItems'))
BEGIN
    ALTER TABLE MeliItems ADD ComboId INT NULL;
    -- FK se agrega solo si la tabla Combos ya existe (puede correrse antes que Combos en init fresco).
    IF EXISTS (SELECT * FROM sysobjects WHERE name='Combos' AND xtype='U')
        EXEC('ALTER TABLE MeliItems ADD CONSTRAINT FK_MeliItems_Combo FOREIGN KEY (ComboId) REFERENCES Combos(Id) ON DELETE SET NULL');
    CREATE INDEX IX_MeliItems_ComboId ON MeliItems(ComboId);
END
GO

-- AuditLogs table
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='AuditLogs' AND xtype='U')
BEGIN
    CREATE TABLE AuditLogs (
        Id INT PRIMARY KEY IDENTITY(1,1),
        EntityType NVARCHAR(100) NOT NULL,
        EntityId NVARCHAR(100) NOT NULL,
        Action NVARCHAR(50) NOT NULL,
        Changes NVARCHAR(MAX) NULL,
        UserName NVARCHAR(100) NULL,
        CreatedAt DATETIME2 DEFAULT GETDATE()
    );
    CREATE INDEX IX_AuditLogs_EntityType_EntityId ON AuditLogs (EntityType, EntityId);
    CREATE INDEX IX_AuditLogs_CreatedAt ON AuditLogs (CreatedAt DESC);
END
GO

-- Seed admin user (password will be set by API on startup)
IF NOT EXISTS (SELECT * FROM Users WHERE Username = 'admin')
BEGIN
    INSERT INTO Users (Username, Email, PasswordHash, FirstName, LastName, Role, RoleId)
    VALUES ('admin', 'admin@template.local', 'placeholder', 'Admin', 'Sistema', 'admin', 1);
END
GO

PRINT 'Database initialized successfully';
GO

-- Add CategoryPath column to MeliItems
IF EXISTS (SELECT * FROM sysobjects WHERE name='MeliItems' AND xtype='U')
   AND NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('MeliItems') AND name = 'CategoryPath')
BEGIN
    ALTER TABLE MeliItems ADD CategoryPath NVARCHAR(500) NULL;
END
GO

-- Add InstallmentTag column to MeliItems
IF EXISTS (SELECT * FROM sysobjects WHERE name='MeliItems' AND xtype='U')
   AND NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('MeliItems') AND name = 'InstallmentTag')
BEGIN
    ALTER TABLE MeliItems ADD InstallmentTag NVARCHAR(50) NULL;
END
GO

-- Add FreeShipping column to MeliItems
IF EXISTS (SELECT * FROM sysobjects WHERE name='MeliItems' AND xtype='U')
   AND NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('MeliItems') AND name = 'FreeShipping')
BEGIN
    ALTER TABLE MeliItems ADD FreeShipping BIT NOT NULL DEFAULT 0;
END
GO

-- ScheduledProcesses table
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='ScheduledProcesses' AND xtype='U')
BEGIN
    CREATE TABLE ScheduledProcesses (
        Id INT PRIMARY KEY IDENTITY(1,1),
        Code NVARCHAR(100) NOT NULL UNIQUE,
        Name NVARCHAR(255) NOT NULL,
        Description NVARCHAR(500) NULL,
        TriggerType NVARCHAR(20) NOT NULL DEFAULT 'Interval',
        IntervalMinutes INT NULL,
        DailyAtTime NVARCHAR(5) NULL,
        CronExpression NVARCHAR(100) NULL,
        IsEnabled BIT NOT NULL DEFAULT 0,
        LastRunAt DATETIME2 NULL,
        LastRunStatus NVARCHAR(20) NULL,
        LastRunDurationMs INT NULL,
        NextRunAt DATETIME2 NULL,
        CreatedAt DATETIME2 DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL
    );
END
GO

-- ProcessExecutionLogs table
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='ProcessExecutionLogs' AND xtype='U')
BEGIN
    CREATE TABLE ProcessExecutionLogs (
        Id INT PRIMARY KEY IDENTITY(1,1),
        ProcessCode NVARCHAR(100) NOT NULL,
        StartedAt DATETIME2 NOT NULL,
        FinishedAt DATETIME2 NULL,
        Status NVARCHAR(20) NOT NULL,
        DurationMs INT NULL,
        ResultSummary NVARCHAR(MAX) NULL,
        ErrorMessage NVARCHAR(MAX) NULL,
        CONSTRAINT FK_ProcessLogs_Process FOREIGN KEY (ProcessCode) REFERENCES ScheduledProcesses(Code) ON DELETE CASCADE
    );
    CREATE INDEX IX_ProcessLogs_ProcessCode ON ProcessExecutionLogs (ProcessCode);
    CREATE INDEX IX_ProcessLogs_StartedAt ON ProcessExecutionLogs (StartedAt DESC);
END
GO

-- Seed scheduled processes
IF NOT EXISTS (SELECT * FROM ScheduledProcesses WHERE Code = 'SyncMeliOrders')
BEGIN
    INSERT INTO ScheduledProcesses (Code, Name, Description, TriggerType, IntervalMinutes, IsEnabled)
    VALUES ('SyncMeliOrders', 'Sincronizar Ordenes', 'Sincroniza las ordenes de MercadoLibre de los ultimos 7 dias', 'Interval', 360, 0);
END
GO

IF NOT EXISTS (SELECT * FROM ScheduledProcesses WHERE Code = 'SyncMeliItems')
BEGIN
    INSERT INTO ScheduledProcesses (Code, Name, Description, TriggerType, IntervalMinutes, IsEnabled)
    VALUES ('SyncMeliItems', 'Sincronizar Publicaciones', 'Sincroniza las publicaciones activas de MercadoLibre', 'Interval', 360, 0);
END
GO

-- Products table
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Products' AND xtype='U')
BEGIN
    CREATE TABLE Products (
        Id INT PRIMARY KEY IDENTITY(1,1),
        Title NVARCHAR(200) NOT NULL,
        Description NVARCHAR(MAX) NULL,
        Brand NVARCHAR(100) NULL,
        Model NVARCHAR(100) NULL,
        Photo1 NVARCHAR(MAX) NULL,
        Photo2 NVARCHAR(MAX) NULL,
        Photo3 NVARCHAR(MAX) NULL,
        CostPrice DECIMAL(18,2) NOT NULL DEFAULT 0,
        RetailPrice DECIMAL(18,2) NOT NULL DEFAULT 0,
        Stock INT NOT NULL DEFAULT 0,
        CriticalStock INT DEFAULT 0,
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL
    );
END
GO

-- Add ProductId FK to MeliItems (link publications to products)
IF EXISTS (SELECT * FROM sysobjects WHERE name='MeliItems' AND xtype='U')
   AND NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('MeliItems') AND name = 'ProductId')
BEGIN
    ALTER TABLE MeliItems ADD ProductId INT NULL;
    ALTER TABLE MeliItems ADD CONSTRAINT FK_MeliItems_Products
        FOREIGN KEY (ProductId) REFERENCES Products(Id) ON DELETE SET NULL;
    CREATE INDEX IX_MeliItems_ProductId ON MeliItems (ProductId);
END
GO


-- Add StockDiscounted to MeliOrders
IF EXISTS (SELECT * FROM sysobjects WHERE name='MeliOrders' AND xtype='U')
   AND NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('MeliOrders') AND name = 'StockDiscounted')
BEGIN
    ALTER TABLE MeliOrders ADD StockDiscounted BIT NOT NULL DEFAULT 0;
END
GO

-- Seed ProcessOrderStock scheduled process
IF NOT EXISTS (SELECT * FROM ScheduledProcesses WHERE Code = 'ProcessOrderStock')
BEGIN
    INSERT INTO ScheduledProcesses (Code, Name, Description, TriggerType, IntervalMinutes, IsEnabled)
    VALUES ('ProcessOrderStock', 'Descontar Stock por Ordenes', 'Descuenta el stock de los productos vinculados cuando entran ordenes nuevas y propaga el cambio a todas las cuentas', 'Interval', 360, 0);
END
GO

-- Add SKU to Products
IF EXISTS (SELECT * FROM sysobjects WHERE name='Products' AND xtype='U')
   AND NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Products') AND name = 'Sku')
BEGIN
    ALTER TABLE Products ADD Sku NVARCHAR(100) NULL;
END
GO

-- Add CriticalStock to Products
IF EXISTS (SELECT * FROM sysobjects WHERE name='Products' AND xtype='U')
   AND NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Products') AND name = 'CriticalStock')
BEGIN
    ALTER TABLE Products ADD CriticalStock INT DEFAULT 0;
END
GO

-- RolePermissions table
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='RolePermissions' AND xtype='U')
BEGIN
    CREATE TABLE RolePermissions (
        Id INT PRIMARY KEY IDENTITY(1,1),
        RoleId INT NOT NULL,
        MenuKey NVARCHAR(50) NOT NULL,
        CONSTRAINT FK_RolePermissions_Roles FOREIGN KEY (RoleId) REFERENCES Roles(Id) ON DELETE CASCADE,
        CONSTRAINT UQ_RolePermissions UNIQUE (RoleId, MenuKey)
    );
    CREATE INDEX IX_RolePermissions_RoleId ON RolePermissions (RoleId);
END
GO

-- Seed admin permissions (all menus)
IF NOT EXISTS (SELECT * FROM RolePermissions WHERE RoleId = 1)
BEGIN
    INSERT INTO RolePermissions (RoleId, MenuKey) VALUES
    (1, 'dashboard'), (1, 'publicaciones'), (1, 'ordenes'),
    (1, 'productos'), (1, 'usuarios'), (1, 'roles'),
    (1, 'integraciones'), (1, 'procesos'), (1, 'auditoria'), (1, 'config');
END
GO

-- Seed usuario permissions (basic menus)
IF NOT EXISTS (SELECT * FROM RolePermissions WHERE RoleId = 2)
BEGIN
    INSERT INTO RolePermissions (RoleId, MenuKey) VALUES
    (2, 'dashboard'), (2, 'publicaciones'), (2, 'ordenes'),
    (2, 'productos'), (2, 'config');
END
GO

-- App Settings table
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='AppSettings' AND xtype='U')
BEGIN
    CREATE TABLE AppSettings (
        [Key] NVARCHAR(100) PRIMARY KEY,
        [Value] NVARCHAR(MAX) NOT NULL,
        UpdatedAt DATETIME2 DEFAULT GETDATE()
    );
END
GO

-- Seed default brand name
IF NOT EXISTS (SELECT * FROM AppSettings WHERE [Key] = 'BrandName')
BEGIN
    INSERT INTO AppSettings ([Key], [Value]) VALUES ('BrandName', 'Tu Marca');
END
GO

IF NOT EXISTS (SELECT * FROM AppSettings WHERE [Key] = 'BrandIcon')
BEGIN
    INSERT INTO AppSettings ([Key], [Value]) VALUES ('BrandIcon', 'Brand');
END
GO

IF NOT EXISTS (SELECT * FROM AppSettings WHERE [Key] = 'PrimaryColor')
BEGIN
    INSERT INTO AppSettings ([Key], [Value]) VALUES ('PrimaryColor', '#10b981');
END
GO

IF NOT EXISTS (SELECT * FROM AppSettings WHERE [Key] = 'SidebarBgColor')
BEGIN
    INSERT INTO AppSettings ([Key], [Value]) VALUES ('SidebarBgColor', '#1a1a2e');
END
GO

-- ============================================================
-- Backups
-- ============================================================

-- Tabla de archivos de backup
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='BackupFiles' AND xtype='U')
BEGIN
    CREATE TABLE BackupFiles (
        Id INT PRIMARY KEY IDENTITY(1,1),
        FileName NVARCHAR(255) NOT NULL UNIQUE,
        SizeBytes BIGINT NOT NULL DEFAULT 0,
        BackupType NVARCHAR(20) NOT NULL DEFAULT 'Manual', -- Manual | Programado | Subido
        Status NVARCHAR(20) NOT NULL DEFAULT 'Completed', -- InProgress | Completed | Failed
        ErrorMessage NVARCHAR(MAX) NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedByUserId INT NULL
    );
    CREATE INDEX IX_BackupFiles_CreatedAt ON BackupFiles (CreatedAt DESC);
END
GO

-- Configuracion de retencion de backups (una sola fila)
IF NOT EXISTS (SELECT * FROM AppSettings WHERE [Key] = 'BackupRetentionDays')
BEGIN
    INSERT INTO AppSettings ([Key], [Value]) VALUES ('BackupRetentionDays', '7');
END
GO

-- Seed proceso programado de backup automatico
IF NOT EXISTS (SELECT * FROM ScheduledProcesses WHERE Code = 'BackupDatabase')
BEGIN
    INSERT INTO ScheduledProcesses (Code, Name, Description, TriggerType, IntervalMinutes, IsEnabled)
    VALUES ('BackupDatabase', 'Backup de Base de Datos', 'Genera un backup .bak y elimina los mas antiguos segun la retencion configurada', 'Interval', 1440, 1);
END
GO

-- Agregar permiso del menu backups al admin
IF NOT EXISTS (SELECT * FROM RolePermissions WHERE RoleId = 1 AND MenuKey = 'backups')
BEGIN
    INSERT INTO RolePermissions (RoleId, MenuKey) VALUES (1, 'backups');
END
GO

-- Agregar permiso del menu combos al admin
IF NOT EXISTS (SELECT * FROM RolePermissions WHERE RoleId = 1 AND MenuKey = 'combos')
BEGIN
    INSERT INTO RolePermissions (RoleId, MenuKey) VALUES (1, 'combos');
END
GO

-- Agregar columna BaseProductId a Products
-- (un producto puede basarse en otro: hereda costo y PVP del producto base)
IF NOT EXISTS (
    SELECT * FROM sys.columns
    WHERE Name = N'BaseProductId' AND Object_ID = Object_ID(N'Products')
)
BEGIN
    ALTER TABLE Products ADD BaseProductId INT NULL;
END
GO

IF NOT EXISTS (
    SELECT * FROM sys.foreign_keys WHERE name = 'FK_Products_BaseProduct'
)
BEGIN
    ALTER TABLE Products
        ADD CONSTRAINT FK_Products_BaseProduct
        FOREIGN KEY (BaseProductId) REFERENCES Products(Id) ON DELETE NO ACTION;
END
GO

IF NOT EXISTS (
    SELECT * FROM sys.indexes WHERE name = 'IX_Products_BaseProductId' AND object_id = Object_ID(N'Products')
)
BEGIN
    CREATE INDEX IX_Products_BaseProductId ON Products(BaseProductId);
END
GO

-- Suppliers (proveedores)
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Suppliers' AND xtype='U')
BEGIN
    CREATE TABLE Suppliers (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Code NVARCHAR(30) NOT NULL,
        Name NVARCHAR(200) NOT NULL,
        Cuit NVARCHAR(20) NULL,
        Phone NVARCHAR(50) NULL,
        Email NVARCHAR(255) NULL,
        Address NVARCHAR(500) NULL,
        ContactName NVARCHAR(150) NULL,
        Notes NVARCHAR(MAX) NULL,
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL,
        CONSTRAINT UQ_Suppliers_Code UNIQUE (Code)
    );
    CREATE INDEX IX_Suppliers_Name ON Suppliers (Name);
END
GO

-- Migracion: agregar columna Code a Suppliers si no existe (backfill incluido)
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'Code' AND Object_ID = Object_ID(N'Suppliers'))
BEGIN
    ALTER TABLE Suppliers ADD Code NVARCHAR(30) NULL;
    EXEC('UPDATE Suppliers SET Code = ''PROV-'' + RIGHT(''000'' + CAST(Id AS VARCHAR), 3) WHERE Code IS NULL');
    EXEC('ALTER TABLE Suppliers ALTER COLUMN Code NVARCHAR(30) NOT NULL');
    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UQ_Suppliers_Code')
        EXEC('ALTER TABLE Suppliers ADD CONSTRAINT UQ_Suppliers_Code UNIQUE (Code)');
END
GO

-- Brands (marcas)
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Brands' AND xtype='U')
BEGIN
    CREATE TABLE Brands (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Code NVARCHAR(30) NOT NULL,
        Name NVARCHAR(150) NOT NULL,
        Description NVARCHAR(500) NULL,
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL,
        CONSTRAINT UQ_Brands_Name UNIQUE (Name),
        CONSTRAINT UQ_Brands_Code UNIQUE (Code)
    );
END
GO

-- Migracion: agregar columna Code a Brands
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'Code' AND Object_ID = Object_ID(N'Brands'))
BEGIN
    ALTER TABLE Brands ADD Code NVARCHAR(30) NULL;
    EXEC('UPDATE Brands SET Code = ''MAR-'' + RIGHT(''000'' + CAST(Id AS VARCHAR), 3) WHERE Code IS NULL');
    EXEC('ALTER TABLE Brands ALTER COLUMN Code NVARCHAR(30) NOT NULL');
    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UQ_Brands_Code')
        EXEC('ALTER TABLE Brands ADD CONSTRAINT UQ_Brands_Code UNIQUE (Code)');
END
GO

-- Marcas con productos perecederos (manejan stock por lotes con fecha de vencimiento)
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'HasExpiry' AND Object_ID = Object_ID(N'Brands'))
BEGIN
    ALTER TABLE Brands ADD HasExpiry BIT NOT NULL DEFAULT 0;
END
GO

-- Empresas (CSV) en las que esta marca se muestra. NULL o '' = visible para todas las empresas.
-- Valores posibles separados por coma: INTERVENT, INTEREVENTOS, FRIKAF, PALANICA
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'Companies' AND Object_ID = Object_ID(N'Brands'))
BEGIN
    ALTER TABLE Brands ADD Companies NVARCHAR(200) NULL;
END
GO

-- Campos extendidos en Products (nombre para mostrar, codigos, IVA, cuentas contables)
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'DisplayName' AND Object_ID = Object_ID(N'Products'))
BEGIN
    ALTER TABLE Products ADD DisplayName NVARCHAR(200) NULL;
END
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'Barcode' AND Object_ID = Object_ID(N'Products'))
BEGIN
    ALTER TABLE Products ADD Barcode NVARCHAR(50) NULL;
END
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'OemCode' AND Object_ID = Object_ID(N'Products'))
BEGIN
    ALTER TABLE Products ADD OemCode NVARCHAR(100) NULL;
END
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'ImageUrl' AND Object_ID = Object_ID(N'Products'))
BEGIN
    ALTER TABLE Products ADD ImageUrl NVARCHAR(1000) NULL;
END
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'VatRate' AND Object_ID = Object_ID(N'Products'))
BEGIN
    ALTER TABLE Products ADD VatRate DECIMAL(5,2) NULL;
END
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'PurchaseAccount' AND Object_ID = Object_ID(N'Products'))
BEGIN
    ALTER TABLE Products ADD PurchaseAccount NVARCHAR(100) NULL;
END
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'SaleAccount' AND Object_ID = Object_ID(N'Products'))
BEGIN
    ALTER TABLE Products ADD SaleAccount NVARCHAR(100) NULL;
END
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'InventoryAccount' AND Object_ID = Object_ID(N'Products'))
BEGIN
    ALTER TABLE Products ADD InventoryAccount NVARCHAR(100) NULL;
END
GO

-- Flag explicito de "producto base" (independiente de tener derivados)
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'IsBase' AND Object_ID = Object_ID(N'Products'))
BEGIN
    ALTER TABLE Products ADD IsBase BIT NOT NULL CONSTRAINT DF_Products_IsBase DEFAULT 0;
END
GO

-- Backfill UNA SOLA VEZ: marcar como base a los productos sin padre que existian antes del flag.
IF NOT EXISTS (SELECT 1 FROM AppSettings WHERE [Key] = 'products_isbase_backfilled')
BEGIN
    UPDATE Products SET IsBase = 1 WHERE BaseProductId IS NULL;
    INSERT INTO AppSettings ([Key], [Value]) VALUES ('products_isbase_backfilled', '1');
END
GO

-- ===== VENTAS (comprobantes) =====
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Sales' AND xtype='U')
BEGIN
    CREATE TABLE Sales (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Number NVARCHAR(50) NOT NULL,
        Date DATETIME2 NOT NULL,
        DueDate DATETIME2 NULL,
        PeriodFrom DATETIME2 NULL,
        PeriodTo DATETIME2 NULL,
        ClientId INT NULL,
        ClientNameSnapshot NVARCHAR(200) NULL,
        ClientAddressSnapshot NVARCHAR(500) NULL,
        ClientCityLocationSnapshot NVARCHAR(200) NULL,
        ClientCuitSnapshot NVARCHAR(20) NULL,
        PaymentCondition NVARCHAR(50) NULL,
        IvaCondition NVARCHAR(50) NULL,
        Subtotal DECIMAL(18,2) NOT NULL,
        Discount DECIMAL(18,2) NOT NULL DEFAULT 0,
        Total DECIMAL(18,2) NOT NULL,
        AmountInWords NVARCHAR(500) NULL,
        Notes NVARCHAR(MAX) NULL,
        IsCancelled BIT NOT NULL DEFAULT 0,
        CancelledAt DATETIME2 NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL,
        CONSTRAINT FK_Sales_Client FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE SET NULL,
        CONSTRAINT UQ_Sales_Number UNIQUE (Number)
    );
    CREATE INDEX IX_Sales_Date ON Sales (Date);
    CREATE INDEX IX_Sales_ClientId ON Sales (ClientId);
END
GO

IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='SaleItems' AND xtype='U')
BEGIN
    CREATE TABLE SaleItems (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        SaleId INT NOT NULL,
        ProductId INT NULL,
        Code NVARCHAR(100) NULL,
        Description NVARCHAR(500) NOT NULL,
        Quantity DECIMAL(18,2) NOT NULL,
        UnitPrice DECIMAL(18,2) NOT NULL,
        VatRate DECIMAL(5,2) NULL,
        BonifPercent DECIMAL(5,2) NOT NULL DEFAULT 0,
        LineTotal DECIMAL(18,2) NOT NULL,
        CONSTRAINT FK_SaleItems_Sale FOREIGN KEY (SaleId) REFERENCES Sales(Id) ON DELETE CASCADE,
        CONSTRAINT FK_SaleItems_Product FOREIGN KEY (ProductId) REFERENCES Products(Id) ON DELETE NO ACTION
    );
    CREATE INDEX IX_SaleItems_SaleId ON SaleItems (SaleId);
END
GO

-- Settings de la empresa (para el comprobante)
IF NOT EXISTS (SELECT * FROM AppSettings WHERE [Key] = 'company.name')
    INSERT INTO AppSettings ([Key], [Value]) VALUES ('company.name', 'FRIKAF');
IF NOT EXISTS (SELECT * FROM AppSettings WHERE [Key] = 'company.cuit')
    INSERT INTO AppSettings ([Key], [Value]) VALUES ('company.cuit', '');
IF NOT EXISTS (SELECT * FROM AppSettings WHERE [Key] = 'company.address')
    INSERT INTO AppSettings ([Key], [Value]) VALUES ('company.address', '');
IF NOT EXISTS (SELECT * FROM AppSettings WHERE [Key] = 'company.phone')
    INSERT INTO AppSettings ([Key], [Value]) VALUES ('company.phone', '');
IF NOT EXISTS (SELECT * FROM AppSettings WHERE [Key] = 'company.email')
    INSERT INTO AppSettings ([Key], [Value]) VALUES ('company.email', '');
IF NOT EXISTS (SELECT * FROM AppSettings WHERE [Key] = 'company.web')
    INSERT INTO AppSettings ([Key], [Value]) VALUES ('company.web', '');
IF NOT EXISTS (SELECT * FROM AppSettings WHERE [Key] = 'company.iva_condition')
    INSERT INTO AppSettings ([Key], [Value]) VALUES ('company.iva_condition', 'Responsable Inscripto');
IF NOT EXISTS (SELECT * FROM AppSettings WHERE [Key] = 'company.iibb')
    INSERT INTO AppSettings ([Key], [Value]) VALUES ('company.iibb', '');
IF NOT EXISTS (SELECT * FROM AppSettings WHERE [Key] = 'company.activity_start')
    INSERT INTO AppSettings ([Key], [Value]) VALUES ('company.activity_start', '');
IF NOT EXISTS (SELECT * FROM AppSettings WHERE [Key] = 'sales.point_of_sale')
    INSERT INTO AppSettings ([Key], [Value]) VALUES ('sales.point_of_sale', '0001');
IF NOT EXISTS (SELECT * FROM AppSettings WHERE [Key] = 'sales.delete_password')
    INSERT INTO AppSettings ([Key], [Value]) VALUES ('sales.delete_password', 'Y2535007F');
IF NOT EXISTS (SELECT * FROM AppSettings WHERE [Key] = 'sales.delete_password_hint')
    INSERT INTO AppSettings ([Key], [Value]) VALUES ('sales.delete_password_hint', 'NIE');
IF NOT EXISTS (SELECT * FROM AppSettings WHERE [Key] = 'sales.delete_allowed_operator')
    INSERT INTO AppSettings ([Key], [Value]) VALUES ('sales.delete_allowed_operator', 'OSMAR');
GO

-- Permiso ventas
IF NOT EXISTS (SELECT * FROM RolePermissions WHERE RoleId = 1 AND MenuKey = 'ventas')
    INSERT INTO RolePermissions (RoleId, MenuKey) VALUES (1, 'ventas');
GO

-- Sales: dias de la semana visibles en el comprobante + flag pagado
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'WeekDays' AND Object_ID = Object_ID(N'Sales'))
BEGIN
    ALTER TABLE Sales ADD WeekDays NVARCHAR(40) NULL;
END
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'IsPaid' AND Object_ID = Object_ID(N'Sales'))
BEGIN
    ALTER TABLE Sales ADD IsPaid BIT NOT NULL CONSTRAINT DF_Sales_IsPaid DEFAULT 0;
END
GO
-- Sales: snapshot del nombre/marca de la empresa que aparece arriba del comprobante.
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'CompanyNameSnapshot' AND Object_ID = Object_ID(N'Sales'))
BEGIN
    ALTER TABLE Sales ADD CompanyNameSnapshot NVARCHAR(100) NULL;
END
GO

-- Sales: nombre del operador que anulo el comprobante (auditoria visible)
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'CancelledByOperator' AND Object_ID = Object_ID(N'Sales'))
BEGIN
    ALTER TABLE Sales ADD CancelledByOperator NVARCHAR(50) NULL;
END
GO

-- Sales: flag para evitar descontar stock dos veces (idempotencia ante reintentos).
-- Se setea en true al crear la venta y se vuelve a false al anular.
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'StockDiscounted' AND Object_ID = Object_ID(N'Sales'))
BEGIN
    ALTER TABLE Sales ADD StockDiscounted BIT NOT NULL CONSTRAINT DF_Sales_StockDiscounted DEFAULT 0;
END
GO

-- SaleItems: snapshot del precio base (sin lista de precios aplicada) y del % de
-- ajuste de la lista al momento de la venta. Sirve para mostrar el descuento
-- correctamente en el comprobante impreso aunque despues cambie el precio del producto.
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'BasePrice' AND Object_ID = Object_ID(N'SaleItems'))
BEGIN
    ALTER TABLE SaleItems ADD BasePrice DECIMAL(18,2) NOT NULL CONSTRAINT DF_SaleItems_BasePrice DEFAULT 0;
END
GO

-- Sales: tipo de comprobante. 'X' es la cotizacion/remito interno (no fiscal, sin IVA).
-- A futuro entran 'FACTURA_A', 'FACTURA_B', 'FACTURA_C' cuando se enlace ARCA.
-- Cada tipo lleva su propio punto de venta y numeracion independiente.
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'ComprobanteType' AND Object_ID = Object_ID(N'Sales'))
BEGIN
    ALTER TABLE Sales ADD ComprobanteType NVARCHAR(20) NOT NULL CONSTRAINT DF_Sales_ComprobanteType DEFAULT 'X';
END
GO

-- Sales: vendedor que emitio el comprobante (snapshot del usuario logueado al guardar).
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'VendedorName' AND Object_ID = Object_ID(N'Sales'))
BEGIN
    ALTER TABLE Sales ADD VendedorName NVARCHAR(150) NULL;
END
GO

-- Punto de venta por tipo de comprobante. Inicialmente: X = '0009' (cotizaciones/remitos),
-- factura A/B/C = '0001' por defecto (se va a configurar bien cuando ARCA este integrado).
IF NOT EXISTS (SELECT * FROM AppSettings WHERE [Key] = 'sales.pv.X')
    INSERT INTO AppSettings ([Key], [Value]) VALUES ('sales.pv.X', '0009');
IF NOT EXISTS (SELECT * FROM AppSettings WHERE [Key] = 'sales.pv.FACTURA_A')
    INSERT INTO AppSettings ([Key], [Value]) VALUES ('sales.pv.FACTURA_A', '0001');
IF NOT EXISTS (SELECT * FROM AppSettings WHERE [Key] = 'sales.pv.FACTURA_B')
    INSERT INTO AppSettings ([Key], [Value]) VALUES ('sales.pv.FACTURA_B', '0001');
IF NOT EXISTS (SELECT * FROM AppSettings WHERE [Key] = 'sales.pv.FACTURA_C')
    INSERT INTO AppSettings ([Key], [Value]) VALUES ('sales.pv.FACTURA_C', '0001');
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'TierAdjustmentPercent' AND Object_ID = Object_ID(N'SaleItems'))
BEGIN
    ALTER TABLE SaleItems ADD TierAdjustmentPercent DECIMAL(6,2) NOT NULL CONSTRAINT DF_SaleItems_TierAdjustmentPercent DEFAULT 0;
END
GO
-- Para items existentes (cargados antes de esta migracion), backfill: BasePrice = UnitPrice.
UPDATE SaleItems SET BasePrice = UnitPrice WHERE BasePrice = 0 AND UnitPrice > 0;
GO

-- ============================================================
-- TESORERIA (cuentas y movimientos)
-- ============================================================
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='TreasuryAccounts' AND xtype='U')
BEGIN
    CREATE TABLE TreasuryAccounts (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Code NVARCHAR(30) NOT NULL,
        Name NVARCHAR(150) NOT NULL,
        AccountType NVARCHAR(30) NOT NULL DEFAULT 'caja',
        Currency NVARCHAR(10) NOT NULL DEFAULT 'ARS',
        InitialBalance DECIMAL(18,2) NOT NULL DEFAULT 0,
        Notes NVARCHAR(500) NULL,
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL,
        CONSTRAINT UQ_TreasuryAccounts_Code UNIQUE (Code)
    );
END
GO

IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='TreasuryMovements' AND xtype='U')
BEGIN
    CREATE TABLE TreasuryMovements (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        AccountId INT NOT NULL,
        Date DATETIME2 NOT NULL,
        MovementType NVARCHAR(20) NOT NULL,
        Concept NVARCHAR(100) NULL,
        Description NVARCHAR(500) NULL,
        Amount DECIMAL(18,2) NOT NULL,
        RelatedAccountId INT NULL,
        RelatedSaleId INT NULL,
        RelatedEmployeeId INT NULL,
        TransferGroupId UNIQUEIDENTIFIER NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CONSTRAINT FK_TreasuryMovements_Account FOREIGN KEY (AccountId) REFERENCES TreasuryAccounts(Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_TreasuryMovements_AccountId ON TreasuryMovements (AccountId);
    CREATE INDEX IX_TreasuryMovements_Date ON TreasuryMovements (Date);
END
GO

-- ============================================================
-- EMPLEADOS y LIQUIDACIONES DE SUELDO
-- ============================================================
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Employees' AND xtype='U')
BEGIN
    CREATE TABLE Employees (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Code NVARCHAR(30) NOT NULL,
        FirstName NVARCHAR(100) NOT NULL,
        LastName NVARCHAR(100) NOT NULL,
        Dni NVARCHAR(20) NULL,
        Cuil NVARCHAR(20) NULL,
        Position NVARCHAR(100) NULL,
        HireDate DATE NULL,
        BaseSalary DECIMAL(18,2) NOT NULL DEFAULT 0,
        Bank NVARCHAR(100) NULL,
        Cbu NVARCHAR(30) NULL,
        Phone NVARCHAR(50) NULL,
        Email NVARCHAR(255) NULL,
        Address NVARCHAR(500) NULL,
        Notes NVARCHAR(MAX) NULL,
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL,
        CONSTRAINT UQ_Employees_Code UNIQUE (Code)
    );
END
GO

IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Payrolls' AND xtype='U')
BEGIN
    CREATE TABLE Payrolls (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        EmployeeId INT NOT NULL,
        Year INT NOT NULL,
        Month INT NOT NULL,
        BaseSalary DECIMAL(18,2) NOT NULL,
        Bonuses DECIMAL(18,2) NOT NULL DEFAULT 0,
        Deductions DECIMAL(18,2) NOT NULL DEFAULT 0,
        GrossTotal DECIMAL(18,2) NOT NULL,
        NetTotal DECIMAL(18,2) NOT NULL,
        Notes NVARCHAR(500) NULL,
        IsPaid BIT NOT NULL DEFAULT 0,
        PaidAt DATETIME2 NULL,
        PaidFromAccountId INT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL,
        CONSTRAINT FK_Payrolls_Employee FOREIGN KEY (EmployeeId) REFERENCES Employees(Id) ON DELETE CASCADE,
        CONSTRAINT FK_Payrolls_Account FOREIGN KEY (PaidFromAccountId) REFERENCES TreasuryAccounts(Id) ON DELETE SET NULL,
        CONSTRAINT UQ_Payrolls UNIQUE (EmployeeId, Year, Month)
    );
END
GO

-- Permisos para admin
IF NOT EXISTS (SELECT * FROM RolePermissions WHERE RoleId = 1 AND MenuKey = 'tesoreria')
    INSERT INTO RolePermissions (RoleId, MenuKey) VALUES (1, 'tesoreria');
IF NOT EXISTS (SELECT * FROM RolePermissions WHERE RoleId = 1 AND MenuKey = 'empleados')
    INSERT INTO RolePermissions (RoleId, MenuKey) VALUES (1, 'empleados');
IF NOT EXISTS (SELECT * FROM RolePermissions WHERE RoleId = 1 AND MenuKey = 'sueldos')
    INSERT INTO RolePermissions (RoleId, MenuKey) VALUES (1, 'sueldos');
GO

-- Servicios: productos con IsService=true, no manejan stock (infinitamente disponibles)
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'IsService' AND Object_ID = Object_ID(N'Products'))
BEGIN
    ALTER TABLE Products ADD IsService BIT NOT NULL DEFAULT 0;
END
GO
IF NOT EXISTS (SELECT * FROM RolePermissions WHERE RoleId = 1 AND MenuKey = 'servicios')
    INSERT INTO RolePermissions (RoleId, MenuKey) VALUES (1, 'servicios');
GO

-- UxB (unidades por bulto): informativo para armar pedidos a proveedor
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'UnitsPerPack' AND Object_ID = Object_ID(N'Products'))
BEGIN
    ALTER TABLE Products ADD UnitsPerPack INT NULL;
END
GO

-- Fraccionamiento padre/hijo: cada hijo representa una fraccion del padre + un recargo fijo.
-- Formula: precio_hijo = precio_padre * Fraction + MarkupAmount
-- Ej (cafe): hijo "1/2 kg" tiene Fraction=0.5 y MarkupAmount=1000 (recargo por fraccionar y envasar).
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'Fraction' AND Object_ID = Object_ID(N'Products'))
BEGIN
    ALTER TABLE Products ADD Fraction DECIMAL(10,4) NOT NULL CONSTRAINT DF_Products_Fraction DEFAULT 1.0;
END
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'MarkupAmount' AND Object_ID = Object_ID(N'Products'))
BEGIN
    ALTER TABLE Products ADD MarkupAmount DECIMAL(18,2) NOT NULL CONSTRAINT DF_Products_MarkupAmount DEFAULT 0;
END
GO

-- Stock decimal: cambiamos Products.Stock a DECIMAL(18,3) para soportar kg con fracciones (0.5 kg, 0.25 kg, etc).
-- Tambien agregamos StockUnit para distinguir productos vendidos por unidad vs por kg.
-- Al pasar a 'kg', el padre lleva el total del bulk y los hijos NO tienen stock propio:
-- al vender un hijo (1/2 kg), se descuentan kg del padre = quantity * Fraction.
IF EXISTS (SELECT * FROM sys.columns WHERE Name = N'Stock' AND Object_ID = Object_ID(N'Products') AND system_type_id = TYPE_ID('int'))
BEGIN
    -- Necesitamos dropear el default constraint antes de cambiar el tipo
    DECLARE @df_name NVARCHAR(200);
    SELECT @df_name = name FROM sys.default_constraints
        WHERE parent_object_id = OBJECT_ID('Products')
        AND parent_column_id = (SELECT column_id FROM sys.columns WHERE Name = 'Stock' AND Object_ID = Object_ID('Products'));
    IF @df_name IS NOT NULL
        EXEC('ALTER TABLE Products DROP CONSTRAINT ' + @df_name);
    ALTER TABLE Products ALTER COLUMN Stock DECIMAL(18,3) NOT NULL;
    ALTER TABLE Products ADD CONSTRAINT DF_Products_Stock DEFAULT 0 FOR Stock;
END
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'StockUnit' AND Object_ID = Object_ID(N'Products'))
BEGIN
    ALTER TABLE Products ADD StockUnit NVARCHAR(10) NOT NULL CONSTRAINT DF_Products_StockUnit DEFAULT 'unidad';
END
GO

-- Pagos parciales de sueldos (adelantos + pagos finales)
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='PayrollPayments' AND xtype='U')
BEGIN
    CREATE TABLE PayrollPayments (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        PayrollId INT NOT NULL,
        Date DATETIME2 NOT NULL,
        Amount DECIMAL(18,2) NOT NULL,
        AccountId INT NULL,
        PaymentMethod NVARCHAR(30) NOT NULL DEFAULT 'efectivo',
        Concept NVARCHAR(100) NULL,
        Notes NVARCHAR(500) NULL,
        TreasuryMovementId INT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CONSTRAINT FK_PayrollPayments_Payroll FOREIGN KEY (PayrollId) REFERENCES Payrolls(Id) ON DELETE CASCADE,
        CONSTRAINT FK_PayrollPayments_Account FOREIGN KEY (AccountId) REFERENCES TreasuryAccounts(Id) ON DELETE SET NULL
    );
    CREATE INDEX IX_PayrollPayments_PayrollId ON PayrollPayments (PayrollId);
END
GO

-- Asegurar que el nombre default tenga el simbolo de marca registrada
IF EXISTS (SELECT 1 FROM AppSettings WHERE [Key] = 'company.name' AND ([Value] = '' OR [Value] = 'FRIKAF'))
    UPDATE AppSettings SET [Value] = 'FRIKAF' + NCHAR(174), UpdatedAt = GETDATE() WHERE [Key] = 'company.name';
GO

-- Lotes de stock (cantidad + fecha de vencimiento por producto)
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='ProductStockBatches' AND xtype='U')
BEGIN
    CREATE TABLE ProductStockBatches (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        ProductId INT NOT NULL,
        Quantity INT NOT NULL,
        ExpiryDate DATE NOT NULL,
        Notes NVARCHAR(500) NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL,
        CONSTRAINT FK_ProductStockBatches_Product FOREIGN KEY (ProductId) REFERENCES Products(Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_ProductStockBatches_ProductId ON ProductStockBatches (ProductId);
    CREATE INDEX IX_ProductStockBatches_ExpiryDate ON ProductStockBatches (ExpiryDate);
END
GO

-- ============================================================
-- INVENTARIOS: Depositos + Movimientos de stock
-- ============================================================
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Warehouses' AND xtype='U')
BEGIN
    CREATE TABLE Warehouses (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Code NVARCHAR(30) NOT NULL,
        Name NVARCHAR(150) NOT NULL,
        Address NVARCHAR(300) NULL,
        Notes NVARCHAR(500) NULL,
        IsDefault BIT NOT NULL DEFAULT 0,
        IsActive BIT NOT NULL DEFAULT 1,
        SortOrder INT NOT NULL DEFAULT 0,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL,
        CONSTRAINT UQ_Warehouses_Code UNIQUE (Code)
    );
    CREATE INDEX IX_Warehouses_IsDefault ON Warehouses (IsDefault);
END
GO

-- Seed de depositos pedidos por el usuario: '9 de abril' (default) y 'Full'.
IF NOT EXISTS (SELECT * FROM Warehouses WHERE Code = '9DEABRIL')
    INSERT INTO Warehouses (Code, Name, IsDefault, IsActive, SortOrder, Notes)
    VALUES ('9DEABRIL', N'9 de abril', 1, 1, 1, N'Deposito principal. Por default toda la mercaderia se ubica aca.');
IF NOT EXISTS (SELECT * FROM Warehouses WHERE Code = 'FULL')
    INSERT INTO Warehouses (Code, Name, IsDefault, IsActive, SortOrder, Notes)
    VALUES ('FULL', N'Full', 0, 1, 2, N'Deposito Full (MercadoLibre / fulfillment).');
GO

-- Movimientos de stock: log de cada ajuste manual hecho desde "Modificacion de stock".
-- Tipo: 'ajuste' (cambia el stock al valor indicado), 'ingreso' (suma), 'egreso' (resta),
-- 'venta' (descuento por venta), 'devolucion', 'rotura', 'merma', etc. — texto libre por ahora.
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='StockMovements' AND xtype='U')
BEGIN
    CREATE TABLE StockMovements (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        ProductId INT NOT NULL,
        WarehouseId INT NOT NULL,
        MovementType NVARCHAR(30) NOT NULL,    -- 'ingreso', 'egreso', 'ajuste', 'rotura', 'merma', 'devolucion', 'conteo', 'otro'
        DeltaQuantity DECIMAL(18,3) NOT NULL,   -- decimal para soportar kg con fracciones
        StockBefore DECIMAL(18,3) NOT NULL,
        StockAfter DECIMAL(18,3) NOT NULL,
        Reason NVARCHAR(150) NULL,              -- motivo corto (texto)
        Notes NVARCHAR(500) NULL,               -- detalle opcional
        OperatorName NVARCHAR(100) NULL,        -- quien lo hizo (operador del sistema)
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CONSTRAINT FK_StockMovements_Product FOREIGN KEY (ProductId) REFERENCES Products(Id) ON DELETE CASCADE,
        CONSTRAINT FK_StockMovements_Warehouse FOREIGN KEY (WarehouseId) REFERENCES Warehouses(Id) ON DELETE NO ACTION
    );
    CREATE INDEX IX_StockMovements_ProductId ON StockMovements (ProductId);
    CREATE INDEX IX_StockMovements_WarehouseId ON StockMovements (WarehouseId);
    CREATE INDEX IX_StockMovements_CreatedAt ON StockMovements (CreatedAt);
END
GO

-- Permiso para inventarios (admin lo recibe automatico via MenuDefinition).
-- Pre-cargamos por las dudas para roles existentes.
IF NOT EXISTS (SELECT * FROM RolePermissions WHERE RoleId = 1 AND MenuKey = 'modificacion-stock')
    INSERT INTO RolePermissions (RoleId, MenuKey) VALUES (1, 'modificacion-stock');
IF NOT EXISTS (SELECT * FROM RolePermissions WHERE RoleId = 1 AND MenuKey = 'depositos')
    INSERT INTO RolePermissions (RoleId, MenuKey) VALUES (1, 'depositos');
IF NOT EXISTS (SELECT * FROM RolePermissions WHERE RoleId = 1 AND MenuKey = 'movimientos-depositos')
    INSERT INTO RolePermissions (RoleId, MenuKey) VALUES (1, 'movimientos-depositos');
IF NOT EXISTS (SELECT * FROM RolePermissions WHERE RoleId = 1 AND MenuKey = 'import-productos')
    INSERT INTO RolePermissions (RoleId, MenuKey) VALUES (1, 'import-productos');
IF NOT EXISTS (SELECT * FROM RolePermissions WHERE RoleId = 1 AND MenuKey = 'actualizacion-stock')
    INSERT INTO RolePermissions (RoleId, MenuKey) VALUES (1, 'actualizacion-stock');
GO

-- Clients (clientes)
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Clients' AND xtype='U')
BEGIN
    CREATE TABLE Clients (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Code NVARCHAR(30) NOT NULL,
        Name NVARCHAR(200) NOT NULL,
        Cuit NVARCHAR(20) NULL,
        Phone NVARCHAR(50) NULL,
        Email NVARCHAR(255) NULL,
        Address NVARCHAR(500) NULL,
        ContactName NVARCHAR(150) NULL,
        Notes NVARCHAR(MAX) NULL,
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL,
        CONSTRAINT UQ_Clients_Code UNIQUE (Code)
    );
    CREATE INDEX IX_Clients_Name ON Clients (Name);
END
GO

-- Permiso de clientes para admin
IF NOT EXISTS (SELECT * FROM RolePermissions WHERE RoleId = 1 AND MenuKey = 'clientes')
BEGIN
    INSERT INTO RolePermissions (RoleId, MenuKey) VALUES (1, 'clientes');
END
GO

-- ============================================================
-- LISTAS DE PRECIOS POR TIPO DE CLIENTE
-- Cada cliente apunta a una lista. Cada lista tiene un % de ajuste
-- sobre el PVP base del producto. Por defecto: Bares -10%, Ventas 0%, MeLi +15%.
-- ============================================================
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='CustomerTiers' AND xtype='U')
BEGIN
    CREATE TABLE CustomerTiers (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Name NVARCHAR(60) NOT NULL,
        Code NVARCHAR(20) NOT NULL,           -- 'bares', 'ventas', 'meli', 'mayorista', etc.
        AdjustmentPercent DECIMAL(6,2) NOT NULL DEFAULT 0, -- ej: -10.00 / 0.00 / 15.00
        IsDefault BIT NOT NULL DEFAULT 0,
        IsActive BIT NOT NULL DEFAULT 1,
        SortOrder INT NOT NULL DEFAULT 0,
        Notes NVARCHAR(500) NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL,
        CONSTRAINT UQ_CustomerTiers_Code UNIQUE (Code)
    );
    CREATE INDEX IX_CustomerTiers_IsDefault ON CustomerTiers (IsDefault);
END
GO

-- Seed inicial de las 3 listas pedidas por el usuario
IF NOT EXISTS (SELECT * FROM CustomerTiers WHERE Code = 'bares')
BEGIN
    INSERT INTO CustomerTiers (Name, Code, AdjustmentPercent, IsDefault, IsActive, SortOrder, Notes)
    VALUES (N'Bares', 'bares', -10.00, 0, 1, 1, N'Precio para bares y locales gastronomicos. Ajuste por compra al por mayor.');
END
IF NOT EXISTS (SELECT * FROM CustomerTiers WHERE Code = 'ventas')
BEGIN
    INSERT INTO CustomerTiers (Name, Code, AdjustmentPercent, IsDefault, IsActive, SortOrder, Notes)
    VALUES (N'Ventas', 'ventas', 0.00, 1, 1, 2, N'Lista por defecto. Precio de venta al publico general.');
END
IF NOT EXISTS (SELECT * FROM CustomerTiers WHERE Code = 'meli')
BEGIN
    INSERT INTO CustomerTiers (Name, Code, AdjustmentPercent, IsDefault, IsActive, SortOrder, Notes)
    VALUES (N'MercadoLibre', 'meli', 15.00, 0, 1, 3, N'Precio que se sube a publicaciones de MeLi. Cubre comisiones y financiacion.');
END
GO

-- Vincular cliente con su lista de precios
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'CustomerTierId' AND Object_ID = Object_ID(N'Clients'))
BEGIN
    ALTER TABLE Clients ADD CustomerTierId INT NULL;
    EXEC('ALTER TABLE Clients ADD CONSTRAINT FK_Clients_CustomerTier FOREIGN KEY (CustomerTierId) REFERENCES CustomerTiers(Id) ON DELETE SET NULL');
    CREATE INDEX IX_Clients_CustomerTierId ON Clients (CustomerTierId);
END
GO

-- Companies en las que se muestra cada lista. CSV (ej "FRIKAF,PALANICA").
-- Empty/null = visible en todas las empresas. Mismo patron que Brands.Companies.
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'Companies' AND Object_ID = Object_ID(N'CustomerTiers'))
BEGIN
    ALTER TABLE CustomerTiers ADD Companies NVARCHAR(200) NULL;
END
GO

-- Asignar la lista por defecto a clientes que no tengan una asignada
UPDATE Clients
SET CustomerTierId = (SELECT TOP 1 Id FROM CustomerTiers WHERE IsDefault = 1)
WHERE CustomerTierId IS NULL;
GO

-- Tabla de precios especiales (override) por producto + lista
-- Si existe una fila aca, gana sobre el calculo automatico (RetailPrice * (1 + Adjustment%)).
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='ProductPriceOverrides' AND xtype='U')
BEGIN
    CREATE TABLE ProductPriceOverrides (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        ProductId INT NOT NULL,
        CustomerTierId INT NOT NULL,
        Price DECIMAL(18,2) NOT NULL,         -- Precio sin IVA, mismo criterio que Products.RetailPrice
        Notes NVARCHAR(300) NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL,
        CONSTRAINT FK_PPO_Product FOREIGN KEY (ProductId) REFERENCES Products(Id) ON DELETE CASCADE,
        CONSTRAINT FK_PPO_Tier FOREIGN KEY (CustomerTierId) REFERENCES CustomerTiers(Id) ON DELETE CASCADE,
        CONSTRAINT UQ_PPO_ProductTier UNIQUE (ProductId, CustomerTierId)
    );
    CREATE INDEX IX_PPO_ProductId ON ProductPriceOverrides (ProductId);
    CREATE INDEX IX_PPO_CustomerTierId ON ProductPriceOverrides (CustomerTierId);
END
GO

-- Permiso para listas de precios (admin)
IF NOT EXISTS (SELECT * FROM RolePermissions WHERE RoleId = 1 AND MenuKey = 'listas-precios')
BEGIN
    INSERT INTO RolePermissions (RoleId, MenuKey) VALUES (1, 'listas-precios');
END
GO

-- Vincular productos con marca (FK opcional)
IF NOT EXISTS (
    SELECT * FROM sys.columns WHERE Name = N'BrandId' AND Object_ID = Object_ID(N'Products')
)
BEGIN
    ALTER TABLE Products ADD BrandId INT NULL;
END
GO

IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE name = 'FK_Products_Brand')
BEGIN
    ALTER TABLE Products
        ADD CONSTRAINT FK_Products_Brand
        FOREIGN KEY (BrandId) REFERENCES Brands(Id) ON DELETE SET NULL;
END
GO

IF NOT EXISTS (
    SELECT * FROM sys.indexes WHERE name = 'IX_Products_BrandId' AND object_id = Object_ID(N'Products')
)
BEGIN
    CREATE INDEX IX_Products_BrandId ON Products(BrandId);
END
GO

-- Permisos de proveedores y marcas para admin
IF NOT EXISTS (SELECT * FROM RolePermissions WHERE RoleId = 1 AND MenuKey = 'proveedores')
BEGIN
    INSERT INTO RolePermissions (RoleId, MenuKey) VALUES (1, 'proveedores');
END
GO

IF NOT EXISTS (SELECT * FROM RolePermissions WHERE RoleId = 1 AND MenuKey = 'marcas')
BEGIN
    INSERT INTO RolePermissions (RoleId, MenuKey) VALUES (1, 'marcas');
END
GO

-- Combos (agrupacion de productos vendida como una unidad)
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Combos' AND xtype='U')
BEGIN
    CREATE TABLE Combos (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Name NVARCHAR(200) NOT NULL,
        Sku NVARCHAR(100) NULL,
        Description NVARCHAR(MAX) NULL,
        Photo NVARCHAR(MAX) NULL,
        PriceMode NVARCHAR(10) NOT NULL DEFAULT 'auto',
        ManualPrice DECIMAL(18,2) NULL,
        PercentAdjustment DECIMAL(8,2) NULL,
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL
    );
    CREATE INDEX IX_Combos_Name ON Combos (Name);
END
GO

IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='ComboItems' AND xtype='U')
BEGIN
    CREATE TABLE ComboItems (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        ComboId INT NOT NULL,
        ProductId INT NOT NULL,
        Quantity INT NOT NULL DEFAULT 1,
        CONSTRAINT FK_ComboItems_Combo FOREIGN KEY (ComboId) REFERENCES Combos(Id) ON DELETE CASCADE,
        CONSTRAINT FK_ComboItems_Product FOREIGN KEY (ProductId) REFERENCES Products(Id) ON DELETE NO ACTION
    );
    CREATE INDEX IX_ComboItems_ComboId ON ComboItems (ComboId);
    CREATE INDEX IX_ComboItems_ProductId ON ComboItems (ProductId);
END
GO

-- Domicilio de entrega del cliente (opcional, distinto del fiscal/facturacion)
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'DeliveryAddress' AND Object_ID = Object_ID(N'Clients'))
BEGIN
    ALTER TABLE Clients ADD DeliveryAddress NVARCHAR(500) NULL;
END
GO

-- Snapshot del domicilio de entrega en cada venta (para que el comprobante quede inmutable)
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'ClientDeliveryAddressSnapshot' AND Object_ID = Object_ID(N'Sales'))
BEGIN
    ALTER TABLE Sales ADD ClientDeliveryAddressSnapshot NVARCHAR(500) NULL;
END
GO

-- ============================================================
-- ARQUITECTURA DE PRECIOS POR EMPRESA (Opcion B)
-- ============================================================

-- Tabla maestra de empresas (normaliza los strings hardcoded)
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Companies' AND xtype='U')
BEGIN
    CREATE TABLE Companies (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Code NVARCHAR(30) NOT NULL UNIQUE,
        Name NVARCHAR(100) NOT NULL,
        CanSell BIT NOT NULL DEFAULT 1,
        SortOrder INT NOT NULL DEFAULT 0,
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL
    );
    INSERT INTO Companies (Code, Name, CanSell, SortOrder) VALUES
        ('INTERVENT', N'INTERVENT', 0, 1),
        ('INTEREVENTOS', N'INTEREVENTOS', 1, 2),
        ('FRIKAF', N'FRIKAF', 1, 3),
        ('PALANICA', N'PALANICA', 1, 4);
END
GO

-- Override de precio por producto+empresa (nivel 1: el mas especifico)
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='ProductCompanyPrices' AND xtype='U')
BEGIN
    CREATE TABLE ProductCompanyPrices (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        ProductId INT NOT NULL,
        CompanyId INT NOT NULL,
        RetailPrice DECIMAL(18,2) NOT NULL,
        UpdatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CONSTRAINT FK_ProductCompanyPrices_Product FOREIGN KEY (ProductId) REFERENCES Products(Id) ON DELETE CASCADE,
        CONSTRAINT FK_ProductCompanyPrices_Company FOREIGN KEY (CompanyId) REFERENCES Companies(Id),
        CONSTRAINT UQ_ProductCompanyPrices_ProductCompany UNIQUE (ProductId, CompanyId)
    );
    CREATE INDEX IX_ProductCompanyPrices_Company ON ProductCompanyPrices (CompanyId);
END
GO

-- Markup por marca+empresa (nivel 2: si no hay override de producto, calcula con cost*markup)
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='BrandCompanyMarkups' AND xtype='U')
BEGIN
    CREATE TABLE BrandCompanyMarkups (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        BrandId INT NOT NULL,
        CompanyId INT NOT NULL,
        MarkupPercent DECIMAL(8,2) NOT NULL,
        UpdatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CONSTRAINT FK_BrandCompanyMarkups_Brand FOREIGN KEY (BrandId) REFERENCES Brands(Id) ON DELETE CASCADE,
        CONSTRAINT FK_BrandCompanyMarkups_Company FOREIGN KEY (CompanyId) REFERENCES Companies(Id),
        CONSTRAINT UQ_BrandCompanyMarkups_BrandCompany UNIQUE (BrandId, CompanyId)
    );
    CREATE INDEX IX_BrandCompanyMarkups_Company ON BrandCompanyMarkups (CompanyId);
END
GO

-- FK explicito de Sales -> Company. Hasta hoy era solo CompanyNameSnapshot (texto).
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'CompanyId' AND Object_ID = Object_ID(N'Sales'))
BEGIN
    ALTER TABLE Sales ADD CompanyId INT NULL;
    EXEC('ALTER TABLE Sales ADD CONSTRAINT FK_Sales_Company FOREIGN KEY (CompanyId) REFERENCES Companies(Id)');
    CREATE INDEX IX_Sales_CompanyId ON Sales (CompanyId);
END
GO

-- Backfill: matchear ventas viejas con su empresa por el snapshot (ignorando ® y mayusculas)
IF EXISTS (SELECT 1 FROM Sales WHERE CompanyId IS NULL AND CompanyNameSnapshot IS NOT NULL)
BEGIN
    UPDATE s SET s.CompanyId = c.Id
      FROM Sales s
      JOIN Companies c
        ON UPPER(REPLACE(REPLACE(LTRIM(RTRIM(s.CompanyNameSnapshot)), N'®', ''), '''', '')) = UPPER(c.Code)
     WHERE s.CompanyId IS NULL AND s.CompanyNameSnapshot IS NOT NULL;
END
GO

-- BrandCompanyMarkups: agregar modo (PERCENT por default, o PVP para usar el RetailPrice del producto)
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'PriceMode' AND Object_ID = Object_ID('BrandCompanyMarkups'))
BEGIN
    ALTER TABLE BrandCompanyMarkups ADD PriceMode NVARCHAR(20) NOT NULL CONSTRAINT DF_BrandCompanyMarkups_PriceMode DEFAULT 'PERCENT';
END
GO

-- Products: PVP 2 (precio alternativo) y PVP 3 (% sobre costo)
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'RetailPrice2' AND Object_ID = Object_ID('Products'))
BEGIN
    ALTER TABLE Products ADD RetailPrice2 DECIMAL(18,2) NULL;
END
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'Pvp3MarkupPercent' AND Object_ID = Object_ID('Products'))
BEGIN
    ALTER TABLE Products ADD Pvp3MarkupPercent DECIMAL(8,2) NULL;
END
GO

-- ============================================================
-- MODULO ALQUILERES (independiente del resto)
-- Tablas prefijadas con Alq_ para no mezclar con el ERP principal.
-- ============================================================

IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Alq_Equipos' AND xtype='U')
BEGIN
    CREATE TABLE Alq_Equipos (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Sku NVARCHAR(50) NOT NULL,
        Nombre NVARCHAR(200) NOT NULL,
        Categoria NVARCHAR(80) NULL,
        Descripcion NVARCHAR(500) NULL,
        StockTotal INT NOT NULL DEFAULT 0,
        PrecioDiario DECIMAL(18,2) NOT NULL DEFAULT 0,
        PrecioReposicion DECIMAL(18,2) NULL,
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL,
        CONSTRAINT UQ_AlqEquipos_Sku UNIQUE (Sku)
    );
    CREATE INDEX IX_AlqEquipos_Categoria ON Alq_Equipos (Categoria);
END
GO

IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Alq_Clientes' AND xtype='U')
BEGIN
    CREATE TABLE Alq_Clientes (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Nombre NVARCHAR(200) NOT NULL,
        Empresa NVARCHAR(200) NULL,
        Telefono NVARCHAR(50) NULL,
        Email NVARCHAR(200) NULL,
        DireccionDefault NVARCHAR(300) NULL,
        Notas NVARCHAR(1000) NULL,
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL
    );
    CREATE INDEX IX_AlqClientes_Nombre ON Alq_Clientes (Nombre);
END
GO

IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Alq_Reservas' AND xtype='U')
BEGIN
    CREATE TABLE Alq_Reservas (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Numero NVARCHAR(20) NOT NULL,
        ClienteId INT NOT NULL,
        FechaEntrega DATE NOT NULL,
        FechaRetiro DATE NOT NULL,
        DireccionEvento NVARCHAR(300) NULL,
        LatitudEvento DECIMAL(10,7) NULL,
        LongitudEvento DECIMAL(10,7) NULL,
        MontoTotal DECIMAL(18,2) NOT NULL DEFAULT 0,
        Sena DECIMAL(18,2) NOT NULL DEFAULT 0,
        Estado NVARCHAR(30) NOT NULL DEFAULT 'reservado',
        Notas NVARCHAR(1000) NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL,
        CONSTRAINT FK_AlqReservas_Cliente FOREIGN KEY (ClienteId) REFERENCES Alq_Clientes(Id),
        CONSTRAINT UQ_AlqReservas_Numero UNIQUE (Numero)
    );
    CREATE INDEX IX_AlqReservas_Fechas ON Alq_Reservas (FechaEntrega, FechaRetiro);
    CREATE INDEX IX_AlqReservas_Estado ON Alq_Reservas (Estado);
END
GO

IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Alq_ReservaItems' AND xtype='U')
BEGIN
    CREATE TABLE Alq_ReservaItems (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        ReservaId INT NOT NULL,
        EquipoId INT NOT NULL,
        Cantidad INT NOT NULL,
        PrecioUnitario DECIMAL(18,2) NOT NULL DEFAULT 0,
        CONSTRAINT FK_AlqReservaItems_Reserva FOREIGN KEY (ReservaId) REFERENCES Alq_Reservas(Id) ON DELETE CASCADE,
        CONSTRAINT FK_AlqReservaItems_Equipo FOREIGN KEY (EquipoId) REFERENCES Alq_Equipos(Id)
    );
    CREATE INDEX IX_AlqReservaItems_Reserva ON Alq_ReservaItems (ReservaId);
    CREATE INDEX IX_AlqReservaItems_Equipo ON Alq_ReservaItems (EquipoId);
END
GO

-- Permiso del modulo (admin)
IF NOT EXISTS (SELECT * FROM RolePermissions WHERE RoleId = 1 AND MenuKey = 'alquileres')
BEGIN
    INSERT INTO RolePermissions (RoleId, MenuKey) VALUES (1, 'alquileres');
END
GO

-- Alq_Clientes: campos adicionales (DNI/CUIT, telefono 2, piso, depto, barrio, entre calles)
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'DniCuit' AND Object_ID = Object_ID('Alq_Clientes'))
    ALTER TABLE Alq_Clientes ADD DniCuit NVARCHAR(50) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'Telefono2' AND Object_ID = Object_ID('Alq_Clientes'))
    ALTER TABLE Alq_Clientes ADD Telefono2 NVARCHAR(50) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'Piso' AND Object_ID = Object_ID('Alq_Clientes'))
    ALTER TABLE Alq_Clientes ADD Piso NVARCHAR(20) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'Depto' AND Object_ID = Object_ID('Alq_Clientes'))
    ALTER TABLE Alq_Clientes ADD Depto NVARCHAR(20) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'Barrio' AND Object_ID = Object_ID('Alq_Clientes'))
    ALTER TABLE Alq_Clientes ADD Barrio NVARCHAR(100) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'EntreCalles' AND Object_ID = Object_ID('Alq_Clientes'))
    ALTER TABLE Alq_Clientes ADD EntreCalles NVARCHAR(200) NULL;
GO

-- Alq_Reservas: horarios del evento + descuento
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'HoraInicio' AND Object_ID = Object_ID('Alq_Reservas'))
    ALTER TABLE Alq_Reservas ADD HoraInicio NVARCHAR(8) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'HoraFin' AND Object_ID = Object_ID('Alq_Reservas'))
    ALTER TABLE Alq_Reservas ADD HoraFin NVARCHAR(8) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'Descuento' AND Object_ID = Object_ID('Alq_Reservas'))
    ALTER TABLE Alq_Reservas ADD Descuento DECIMAL(18,2) NOT NULL CONSTRAINT DF_AlqReservas_Descuento DEFAULT 0;
GO

-- Condiciones de servicio (texto editable, va al pie del PDF de la reserva)
IF NOT EXISTS (SELECT * FROM AppSettings WHERE [Key] = 'alq.condiciones')
BEGIN
    INSERT INTO AppSettings ([Key], Value, UpdatedAt)
    VALUES ('alq.condiciones',
'Las entregas y retiros son en puerta / planta baja / ascensor. No subimos escaleras. El servicio NO incluye acomodamiento.

Su reserva es confirmada una vez que le enviamos el archivo PDF. Es FUNDAMENTAL revisar que la fecha, cantidades e importe sean correctos.',
        GETDATE());
END
GO

-- ============================================================
-- MODULO NOMINAS (independiente del resto)
-- Tablas prefijadas con Nom_ para no mezclar con Empleados/Sueldos del ERP principal.
-- ============================================================

IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Nom_Empleados' AND xtype='U')
BEGIN
    CREATE TABLE Nom_Empleados (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Nombre NVARCHAR(200) NOT NULL,
        Documento NVARCHAR(50) NULL,
        Puesto NVARCHAR(100) NULL,
        FechaIngreso DATE NOT NULL,
        SueldoBase DECIMAL(18,2) NOT NULL DEFAULT 0,
        ValorHora DECIMAL(18,2) NOT NULL DEFAULT 0,
        ComisionPorcentaje DECIMAL(8,2) NULL,
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL
    );
    CREATE INDEX IX_NomEmpleados_Nombre ON Nom_Empleados (Nombre);
END
GO

IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Nom_Liquidaciones' AND xtype='U')
BEGIN
    CREATE TABLE Nom_Liquidaciones (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        EmpleadoId INT NOT NULL,
        Anio INT NOT NULL,
        Mes INT NOT NULL,
        HorasTrabajadas DECIMAL(8,2) NOT NULL DEFAULT 0,
        HorasExtra DECIMAL(8,2) NOT NULL DEFAULT 0,
        RecargoHsExtraPct DECIMAL(8,2) NOT NULL DEFAULT 50,
        DiasAusencia DECIMAL(5,2) NOT NULL DEFAULT 0,
        DiasVacaciones DECIMAL(5,2) NOT NULL DEFAULT 0,
        SueldoBase DECIMAL(18,2) NOT NULL DEFAULT 0,
        MontoHsExtra DECIMAL(18,2) NOT NULL DEFAULT 0,
        Comision DECIMAL(18,2) NOT NULL DEFAULT 0,
        Bonos DECIMAL(18,2) NOT NULL DEFAULT 0,
        DescuentoFaltas DECIMAL(18,2) NOT NULL DEFAULT 0,
        Adelantos DECIMAL(18,2) NOT NULL DEFAULT 0,
        OtrosDescuentos DECIMAL(18,2) NOT NULL DEFAULT 0,
        TotalGanado DECIMAL(18,2) NOT NULL DEFAULT 0,
        TotalDescuentos DECIMAL(18,2) NOT NULL DEFAULT 0,
        NetoAPagar DECIMAL(18,2) NOT NULL DEFAULT 0,
        Estado NVARCHAR(20) NOT NULL DEFAULT 'pendiente',
        Notas NVARCHAR(1000) NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL,
        CONSTRAINT FK_NomLiq_Empleado FOREIGN KEY (EmpleadoId) REFERENCES Nom_Empleados(Id),
        CONSTRAINT UQ_NomLiq_EmpleadoMes UNIQUE (EmpleadoId, Anio, Mes)
    );
    CREATE INDEX IX_NomLiq_AnioMes ON Nom_Liquidaciones (Anio, Mes);
    CREATE INDEX IX_NomLiq_Estado ON Nom_Liquidaciones (Estado);
END
GO

IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Nom_Pagos' AND xtype='U')
BEGIN
    CREATE TABLE Nom_Pagos (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        LiquidacionId INT NOT NULL,
        FechaPago DATE NOT NULL,
        Metodo NVARCHAR(50) NOT NULL,
        Monto DECIMAL(18,2) NOT NULL,
        Notas NVARCHAR(500) NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CONSTRAINT FK_NomPago_Liq FOREIGN KEY (LiquidacionId) REFERENCES Nom_Liquidaciones(Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_NomPago_Liq ON Nom_Pagos (LiquidacionId);
    CREATE INDEX IX_NomPago_Fecha ON Nom_Pagos (FechaPago);
END
GO

IF NOT EXISTS (SELECT * FROM RolePermissions WHERE RoleId = 1 AND MenuKey = 'nominas')
BEGIN
    INSERT INTO RolePermissions (RoleId, MenuKey) VALUES (1, 'nominas');
END
GO

-- ============================================================
-- BOVEDA DE CONTRASEÑAS (modulo independiente, una bóveda compartida)
-- ============================================================

IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Vault_Settings' AND xtype='U')
BEGIN
    CREATE TABLE Vault_Settings (
        Id INT NOT NULL PRIMARY KEY,
        MasterPasswordHash NVARCHAR(255) NOT NULL,
        KdfSalt NVARCHAR(64) NOT NULL,
        AutoLockMinutes INT NOT NULL DEFAULT 5,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL
    );
END
GO

IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Vault_Entries' AND xtype='U')
BEGIN
    CREATE TABLE Vault_Entries (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Servicio NVARCHAR(200) NOT NULL,
        UsuarioEnc NVARCHAR(MAX) NOT NULL,
        PasswordEnc NVARCHAR(MAX) NOT NULL,
        NotasEnc NVARCHAR(MAX) NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL
    );
    CREATE INDEX IX_VaultEntries_Servicio ON Vault_Entries (Servicio);
END
GO

IF NOT EXISTS (SELECT * FROM RolePermissions WHERE RoleId = 1 AND MenuKey = 'vault')
BEGIN
    INSERT INTO RolePermissions (RoleId, MenuKey) VALUES (1, 'vault');
END
GO

-- Postits del dashboard (block de notas compartido)
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Postits' AND xtype='U')
BEGIN
    CREATE TABLE Postits (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Texto NVARCHAR(MAX) NOT NULL,
        Color NVARCHAR(20) NOT NULL DEFAULT 'amarillo',
        CreadoPor NVARCHAR(100) NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL
    );
    CREATE INDEX IX_Postits_CreatedAt ON Postits (CreatedAt DESC);
END
GO

-- ============================================================
-- MODULO CAFE (independiente del resto)
-- Negocio de venta de cafe e insumos. Tablas prefijadas con Cafe_.
-- ============================================================

IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Cafe_Clientes' AND xtype='U')
BEGIN
    CREATE TABLE Cafe_Clientes (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Nombre NVARCHAR(200) NOT NULL,
        Tipo NVARCHAR(20) NOT NULL DEFAULT 'OTRO',
        Telefono NVARCHAR(50) NULL,
        Direccion NVARCHAR(300) NULL,
        Notas NVARCHAR(500) NULL,
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL
    );
    CREATE INDEX IX_CafeClientes_Tipo ON Cafe_Clientes (Tipo);
    CREATE INDEX IX_CafeClientes_Nombre ON Cafe_Clientes (Nombre);
END
GO

IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Cafe_Productos' AND xtype='U')
BEGIN
    CREATE TABLE Cafe_Productos (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Nombre NVARCHAR(200) NOT NULL,
        Categoria NVARCHAR(20) NOT NULL DEFAULT 'CAFE',
        Costo DECIMAL(18,2) NOT NULL DEFAULT 0,
        PrecioPorKg DECIMAL(18,2) NULL,
        Pvp1 DECIMAL(18,2) NULL,
        Pvp2 DECIMAL(18,2) NULL,
        StockGramos DECIMAL(18,3) NOT NULL DEFAULT 0,
        StockUnidades INT NOT NULL DEFAULT 0,
        Notas NVARCHAR(500) NULL,
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL
    );
    CREATE INDEX IX_CafeProductos_Categoria ON Cafe_Productos (Categoria);
    CREATE INDEX IX_CafeProductos_Nombre ON Cafe_Productos (Nombre);
END
GO

IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Cafe_Settings' AND xtype='U')
BEGIN
    CREATE TABLE Cafe_Settings (
        Id INT NOT NULL PRIMARY KEY,
        CostoFraccionamiento DECIMAL(18,2) NOT NULL DEFAULT 1000,
        RedondeoMultiplo DECIMAL(18,2) NOT NULL DEFAULT 1000,
        MargenOtrosBarPct DECIMAL(8,2) NOT NULL DEFAULT 40,
        MargenOtrosNoBarPct DECIMAL(8,2) NOT NULL DEFAULT 60,
        NegocioNombre NVARCHAR(200) NULL,
        NegocioTelefono NVARCHAR(50) NULL,
        NegocioWhatsappNumero NVARCHAR(50) NULL,
        NegocioDireccion NVARCHAR(300) NULL,
        NegocioCuit NVARCHAR(50) NULL,
        UpdatedAt DATETIME2 NULL
    );
    INSERT INTO Cafe_Settings (Id) VALUES (1);
END
GO

IF NOT EXISTS (SELECT * FROM RolePermissions WHERE RoleId = 1 AND MenuKey = 'cafe')
BEGIN
    INSERT INTO RolePermissions (RoleId, MenuKey) VALUES (1, 'cafe');
END
GO
