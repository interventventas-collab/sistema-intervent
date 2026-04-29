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
