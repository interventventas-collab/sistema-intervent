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

-- ArcaAccounts table — cuentas de ARCA (ex AFIP) para login automatizado por scraping.
-- Cada fila representa una combinación CUIT + CUIT Login (algunos usuarios loguean
-- con un CUIT y luego representan a otro). Si CuitLogin es NULL, loguea con el
-- mismo Cuit. La password se guarda en texto porque el scraper la usa en runtime.
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='ArcaAccounts' AND xtype='U')
BEGIN
    CREATE TABLE ArcaAccounts (
        Id INT PRIMARY KEY IDENTITY(1,1),
        Cuit NVARCHAR(11) NOT NULL,
        CuitLogin NVARCHAR(11) NULL,
        Alias NVARCHAR(100) NULL,
        Password NVARCHAR(MAX) NOT NULL,
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL
    );
    CREATE INDEX IX_ArcaAccounts_Cuit ON ArcaAccounts (Cuit);
END
GO

-- ArcaWebserviceAccounts table — certificados .pfx para autenticarse contra
-- los webservices de ARCA. Cada CUIT puede tener varios certificados (distintos
-- alias/ambientes). El archivo .pfx vive en disco bajo "Certificados ARCA/<cuit>/".
-- Aca solo guardamos el path relativo + metadata. Environment = production | homologation.
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='ArcaWebserviceAccounts' AND xtype='U')
BEGIN
    CREATE TABLE ArcaWebserviceAccounts (
        Id INT PRIMARY KEY IDENTITY(1,1),
        Cuit NVARCHAR(11) NOT NULL,
        Alias NVARCHAR(100) NULL,
        FileName NVARCHAR(255) NOT NULL,
        FilePath NVARCHAR(500) NOT NULL,
        Password NVARCHAR(500) NULL,
        Environment NVARCHAR(20) NOT NULL DEFAULT 'production',
        ExpiresAt DATETIME2 NULL,
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL,
        CONSTRAINT UQ_ArcaWS_CuitFile UNIQUE (Cuit, FileName)
    );
    CREATE INDEX IX_ArcaWS_Cuit ON ArcaWebserviceAccounts (Cuit);
END
GO

-- Migracion: si la tabla ArcaWebserviceAccounts ya existe pero le falta la
-- columna Environment (instalaciones que probaron una version intermedia),
-- agregarla con default 'production'.
IF EXISTS (SELECT * FROM sysobjects WHERE name='ArcaWebserviceAccounts' AND xtype='U')
   AND NOT EXISTS (SELECT * FROM sys.columns WHERE Name='Environment' AND Object_ID=OBJECT_ID('ArcaWebserviceAccounts'))
BEGIN
    ALTER TABLE ArcaWebserviceAccounts ADD Environment NVARCHAR(20) NOT NULL DEFAULT 'production';
END
GO

-- ArcaCsrRequests — pedidos de CSR temporales del wizard de generacion de
-- certificado. Cuando el usuario arranca el wizard se crea una clave privada
-- RSA + CSR aca; cuando vuelve con el .crt de ARCA, combinamos cert + key,
-- generamos el .pfx y BORRAMOS esta fila.
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='ArcaCsrRequests' AND xtype='U')
BEGIN
    CREATE TABLE ArcaCsrRequests (
        Id INT PRIMARY KEY IDENTITY(1,1),
        Cuit NVARCHAR(11) NOT NULL,
        Alias NVARCHAR(100) NOT NULL,
        PrivateKeyPem NVARCHAR(MAX) NOT NULL,
        CsrPem NVARCHAR(MAX) NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE()
    );
    CREATE INDEX IX_ArcaCsr_Cuit ON ArcaCsrRequests (Cuit);
END
GO

-- ArcaEmisores — datos legales del emisor (Razón social, IIBB, Domicilio, etc.)
-- que van en el header del PDF. Una fila por CUIT (UNIQUE Cuit).
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='ArcaEmisores' AND xtype='U')
BEGIN
    CREATE TABLE ArcaEmisores (
        Id INT PRIMARY KEY IDENTITY(1,1),
        Cuit NVARCHAR(11) NOT NULL,
        RazonSocial NVARCHAR(200) NULL,
        CondicionIva NVARCHAR(50) NOT NULL DEFAULT 'Responsable Inscripto',
        Domicilio NVARCHAR(300) NULL,
        IIBBTipo NVARCHAR(20) NULL,
        IIBBNumero NVARCHAR(30) NULL,
        InicioActividades DATETIME2 NULL,
        LogoPath NVARCHAR(500) NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL,
        CONSTRAINT UQ_ArcaEmisores_Cuit UNIQUE (Cuit)
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
        ComisionPorKg DECIMAL(18,2) NOT NULL DEFAULT 0,
        BonoFijo DECIMAL(18,2) NOT NULL DEFAULT 0,
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL
    );
    CREATE INDEX IX_NomEmpleados_Nombre ON Nom_Empleados (Nombre);
END
GO

-- Migracion: agregar ComisionPorKg a Nom_Empleados (instalaciones existentes)
IF EXISTS (SELECT * FROM sysobjects WHERE name='Nom_Empleados' AND xtype='U')
   AND NOT EXISTS (SELECT * FROM sys.columns WHERE Name='ComisionPorKg' AND Object_ID=OBJECT_ID('Nom_Empleados'))
BEGIN
    ALTER TABLE Nom_Empleados ADD ComisionPorKg DECIMAL(18,2) NOT NULL DEFAULT 0;
END
GO

-- Migracion: agregar BonoFijo a Nom_Empleados (instalaciones existentes)
IF EXISTS (SELECT * FROM sysobjects WHERE name='Nom_Empleados' AND xtype='U')
   AND NOT EXISTS (SELECT * FROM sys.columns WHERE Name='BonoFijo' AND Object_ID=OBJECT_ID('Nom_Empleados'))
BEGIN
    ALTER TABLE Nom_Empleados ADD BonoFijo DECIMAL(18,2) NOT NULL DEFAULT 0;
END
GO

-- Migracion: agregar KgCafe a Nom_Liquidaciones (instalaciones existentes)
IF EXISTS (SELECT * FROM sysobjects WHERE name='Nom_Liquidaciones' AND xtype='U')
   AND NOT EXISTS (SELECT * FROM sys.columns WHERE Name='KgCafe' AND Object_ID=OBJECT_ID('Nom_Liquidaciones'))
BEGIN
    ALTER TABLE Nom_Liquidaciones ADD KgCafe DECIMAL(18,2) NOT NULL DEFAULT 0;
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
        RecargoHsExtraPct DECIMAL(8,2) NOT NULL DEFAULT 0,
        DiasAusencia DECIMAL(5,2) NOT NULL DEFAULT 0,
        DiasVacaciones DECIMAL(5,2) NOT NULL DEFAULT 0,
        SueldoBase DECIMAL(18,2) NOT NULL DEFAULT 0,
        MontoHsExtra DECIMAL(18,2) NOT NULL DEFAULT 0,
        KgCafe DECIMAL(18,2) NOT NULL DEFAULT 0,
        Comision DECIMAL(18,2) NOT NULL DEFAULT 0,
        Bonos DECIMAL(18,2) NOT NULL DEFAULT 0,
        Aguinaldo DECIMAL(18,2) NOT NULL DEFAULT 0,
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

-- Migracion: si la tabla ya existia (instalacion vieja), agregar la columna
-- Aguinaldo. Default 0 asi liquidaciones existentes no se rompen.
IF EXISTS (SELECT * FROM sysobjects WHERE name='Nom_Liquidaciones' AND xtype='U')
   AND NOT EXISTS (SELECT * FROM sys.columns WHERE Name='Aguinaldo' AND Object_ID=OBJECT_ID('Nom_Liquidaciones'))
BEGIN
    ALTER TABLE Nom_Liquidaciones ADD Aguinaldo DECIMAL(18,2) NOT NULL DEFAULT 0;
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
        Concepto NVARCHAR(30) NOT NULL DEFAULT 'sueldo',
        Detalle NVARCHAR(500) NULL,
        Notas NVARCHAR(500) NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CONSTRAINT FK_NomPago_Liq FOREIGN KEY (LiquidacionId) REFERENCES Nom_Liquidaciones(Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_NomPago_Liq ON Nom_Pagos (LiquidacionId);
    CREATE INDEX IX_NomPago_Fecha ON Nom_Pagos (FechaPago);
END
GO

-- Migracion: si la tabla Nom_Pagos ya existe (instalaciones viejas), agregamos
-- las columnas Concepto y Detalle. Concepto NOT NULL con default 'sueldo' asi
-- los pagos previos quedan etiquetados como sueldo (el usuario puede borrarlos
-- y recrearlos con el concepto correcto si necesita).
IF EXISTS (SELECT * FROM sysobjects WHERE name='Nom_Pagos' AND xtype='U')
   AND NOT EXISTS (SELECT * FROM sys.columns WHERE Name='Concepto' AND Object_ID=OBJECT_ID('Nom_Pagos'))
BEGIN
    ALTER TABLE Nom_Pagos ADD Concepto NVARCHAR(30) NOT NULL DEFAULT 'sueldo';
END
GO

IF EXISTS (SELECT * FROM sysobjects WHERE name='Nom_Pagos' AND xtype='U')
   AND NOT EXISTS (SELECT * FROM sys.columns WHERE Name='Detalle' AND Object_ID=OBJECT_ID('Nom_Pagos'))
BEGIN
    ALTER TABLE Nom_Pagos ADD Detalle NVARCHAR(500) NULL;
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

-- Cafe: ventas y items
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Cafe_Ventas' AND xtype='U')
BEGIN
    CREATE TABLE Cafe_Ventas (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Numero NVARCHAR(20) NOT NULL,
        Fecha DATE NOT NULL,
        ClienteId INT NULL,
        ClienteNombreSnapshot NVARCHAR(200) NULL,
        ClienteTipoSnapshot NVARCHAR(20) NULL,
        Subtotal DECIMAL(18,2) NOT NULL DEFAULT 0,
        Descuento DECIMAL(18,2) NOT NULL DEFAULT 0,
        Total DECIMAL(18,2) NOT NULL DEFAULT 0,
        CostoTotal DECIMAL(18,2) NOT NULL DEFAULT 0,
        Margen DECIMAL(18,2) NOT NULL DEFAULT 0,
        Observaciones NVARCHAR(500) NULL,
        Estado NVARCHAR(20) NOT NULL DEFAULT 'emitido',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL,
        CONSTRAINT FK_CafeVentas_Cliente FOREIGN KEY (ClienteId) REFERENCES Cafe_Clientes(Id),
        CONSTRAINT UQ_CafeVentas_Numero UNIQUE (Numero)
    );
    CREATE INDEX IX_CafeVentas_Fecha ON Cafe_Ventas (Fecha);
    CREATE INDEX IX_CafeVentas_Cliente ON Cafe_Ventas (ClienteId);
    CREATE INDEX IX_CafeVentas_Estado ON Cafe_Ventas (Estado);
END
GO

-- Migracion: columnas ARCA en Cafe_Ventas (factura electronica emitida desde el modulo Cafe)
IF EXISTS (SELECT * FROM sysobjects WHERE name='Cafe_Ventas' AND xtype='U')
   AND NOT EXISTS (SELECT * FROM sys.columns WHERE Name='ArcaEstado' AND Object_ID=OBJECT_ID('Cafe_Ventas'))
BEGIN
    ALTER TABLE Cafe_Ventas ADD ArcaEstado NVARCHAR(20) NOT NULL DEFAULT 'no_aplica';
    ALTER TABLE Cafe_Ventas ADD ArcaCae NVARCHAR(20) NULL;
    ALTER TABLE Cafe_Ventas ADD ArcaCaeVto DATETIME2 NULL;
    ALTER TABLE Cafe_Ventas ADD ArcaPtoVta INT NULL;
    ALTER TABLE Cafe_Ventas ADD ArcaCbteNro INT NULL;
    ALTER TABLE Cafe_Ventas ADD ArcaCbteTipoNum INT NULL;
    ALTER TABLE Cafe_Ventas ADD ArcaError NVARCHAR(1000) NULL;
END
GO

-- Migracion: trazabilidad Proforma → Factura (vinculos entre ventas)
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='OrigenVentaId' AND Object_ID=OBJECT_ID('Cafe_Ventas'))
    ALTER TABLE Cafe_Ventas ADD OrigenVentaId INT NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='FacturadaComoVentaId' AND Object_ID=OBJECT_ID('Cafe_Ventas'))
    ALTER TABLE Cafe_Ventas ADD FacturadaComoVentaId INT NULL;
GO

IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Cafe_VentaItems' AND xtype='U')
BEGIN
    CREATE TABLE Cafe_VentaItems (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        VentaId INT NOT NULL,
        ProductoId INT NULL,   -- nullable para soportar items de "concepto libre" sin producto del catalogo
        EsConceptoLibre BIT NOT NULL DEFAULT 0,
        ProductoNombreSnapshot NVARCHAR(200) NOT NULL,
        Categoria NVARCHAR(20) NOT NULL,
        Formato NVARCHAR(20) NOT NULL,
        Cantidad INT NOT NULL,
        PrecioUnitario DECIMAL(18,2) NOT NULL,
        CostoUnitario DECIMAL(18,2) NOT NULL,
        Subtotal DECIMAL(18,2) NOT NULL,
        GramosDescontados DECIMAL(18,3) NOT NULL DEFAULT 0,
        CONSTRAINT FK_CafeVentaItems_Venta FOREIGN KEY (VentaId) REFERENCES Cafe_Ventas(Id) ON DELETE CASCADE,
        CONSTRAINT FK_CafeVentaItems_Producto FOREIGN KEY (ProductoId) REFERENCES Cafe_Productos(Id)
    );
    CREATE INDEX IX_CafeVentaItems_Venta ON Cafe_VentaItems (VentaId);
    CREATE INDEX IX_CafeVentaItems_Producto ON Cafe_VentaItems (ProductoId);
END
GO

-- Cafe_Ventas: agregar WeekDays e IsPaid
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'WeekDays' AND Object_ID = Object_ID('Cafe_Ventas'))
    ALTER TABLE Cafe_Ventas ADD WeekDays NVARCHAR(50) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'IsPaid' AND Object_ID = Object_ID('Cafe_Ventas'))
    ALTER TABLE Cafe_Ventas ADD IsPaid BIT NOT NULL CONSTRAINT DF_CafeVentas_IsPaid DEFAULT 0;
GO

-- Cafe_VentaItems: agregar Molienda y EsDoyPack
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'Molienda' AND Object_ID = Object_ID('Cafe_VentaItems'))
    ALTER TABLE Cafe_VentaItems ADD Molienda NVARCHAR(30) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'EsDoyPack' AND Object_ID = Object_ID('Cafe_VentaItems'))
    ALTER TABLE Cafe_VentaItems ADD EsDoyPack BIT NOT NULL CONSTRAINT DF_CafeVentaItems_EsDoyPack DEFAULT 0;
GO

-- Cafe_VentaItems: descuento porcentual por linea
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'DescuentoPct' AND Object_ID = Object_ID('Cafe_VentaItems'))
    ALTER TABLE Cafe_VentaItems ADD DescuentoPct DECIMAL(5,2) NOT NULL CONSTRAINT DF_CafeVentaItems_DescuentoPct DEFAULT 0;
GO

-- Cafe_VentaItems: soporte de "concepto libre" (items que no corresponden a un producto
-- del catalogo — servicios, otros conceptos manuales). Hacemos ProductoId nullable y
-- agregamos un flag EsConceptoLibre. Los items existentes quedan intactos (ProductoId no null).
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'EsConceptoLibre' AND Object_ID = Object_ID('Cafe_VentaItems'))
    ALTER TABLE Cafe_VentaItems ADD EsConceptoLibre BIT NOT NULL CONSTRAINT DF_CafeVentaItems_EsConceptoLibre DEFAULT 0;
GO
-- Hacer ProductoId nullable. Primero hay que dropear el FK y el index, modificar, recrear.
IF COL_LENGTH('Cafe_VentaItems', 'ProductoId') IS NOT NULL
    AND COLUMNPROPERTY(OBJECT_ID('Cafe_VentaItems'), 'ProductoId', 'AllowsNull') = 0
BEGIN
    -- Dropear FK + index para poder modificar la columna
    IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_CafeVentaItems_Producto')
        ALTER TABLE Cafe_VentaItems DROP CONSTRAINT FK_CafeVentaItems_Producto;
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CafeVentaItems_Producto' AND object_id = OBJECT_ID('Cafe_VentaItems'))
        DROP INDEX IX_CafeVentaItems_Producto ON Cafe_VentaItems;
    -- Modificar a nullable
    ALTER TABLE Cafe_VentaItems ALTER COLUMN ProductoId INT NULL;
    -- Recrear FK + index (con OnDelete = NoAction para no cascadear borrados raros)
    ALTER TABLE Cafe_VentaItems ADD CONSTRAINT FK_CafeVentaItems_Producto
        FOREIGN KEY (ProductoId) REFERENCES Cafe_Productos(Id);
    CREATE INDEX IX_CafeVentaItems_Producto ON Cafe_VentaItems (ProductoId);
END
GO

-- Cafe_Settings: agregar template del mensaje de WhatsApp
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'WhatsappMensajeTemplate' AND Object_ID = Object_ID('Cafe_Settings'))
    ALTER TABLE Cafe_Settings ADD WhatsappMensajeTemplate NVARCHAR(500) NULL;
GO
UPDATE Cafe_Settings
   SET WhatsappMensajeTemplate = N'Hola! Te escribo por el comprobante {numero} (total ${total}). Gracias!'
 WHERE Id = 1 AND (WhatsappMensajeTemplate IS NULL OR LTRIM(RTRIM(WhatsappMensajeTemplate)) = '');
GO

-- Cafe_Ventas: snapshot del telefono del cliente al momento de emitir
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'ClienteTelefonoSnapshot' AND Object_ID = Object_ID('Cafe_Ventas'))
    ALTER TABLE Cafe_Ventas ADD ClienteTelefonoSnapshot NVARCHAR(50) NULL;
GO

-- Cafe_Settings: template del mensaje DESDE el negocio AL cliente (uso: repartidor)
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'WhatsappMensajeClienteTemplate' AND Object_ID = Object_ID('Cafe_Settings'))
    ALTER TABLE Cafe_Settings ADD WhatsappMensajeClienteTemplate NVARCHAR(500) NULL;
GO
UPDATE Cafe_Settings
   SET WhatsappMensajeClienteTemplate = N'Hola {cliente}! Te escribo del negocio por el comprobante {numero}.'
 WHERE Id = 1 AND (WhatsappMensajeClienteTemplate IS NULL OR LTRIM(RTRIM(WhatsappMensajeClienteTemplate)) = '');
GO

-- Postits: agregar Scope para permitir multiples boards (dashboard, nominas, ...)
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'Scope' AND Object_ID = Object_ID('Postits'))
    ALTER TABLE Postits ADD Scope NVARCHAR(50) NOT NULL CONSTRAINT DF_Postits_Scope DEFAULT N'dashboard';
GO

-- Cafe_Clientes: agregar Cuit y Email
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'Cuit' AND Object_ID = Object_ID('Cafe_Clientes'))
    ALTER TABLE Cafe_Clientes ADD Cuit NVARCHAR(20) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'Email' AND Object_ID = Object_ID('Cafe_Clientes'))
    ALTER TABLE Cafe_Clientes ADD Email NVARCHAR(255) NULL;
GO

-- Cafe_Productos: agregar Marca
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'Marca' AND Object_ID = Object_ID('Cafe_Productos'))
    ALTER TABLE Cafe_Productos ADD Marca NVARCHAR(100) NULL;
GO

-- Cafe_Clientes: codigo automatico (4 digitos)
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'Codigo' AND Object_ID = Object_ID('Cafe_Clientes'))
    ALTER TABLE Cafe_Clientes ADD Codigo NVARCHAR(20) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_CafeClientes_Codigo' AND object_id = OBJECT_ID('Cafe_Clientes'))
BEGIN
    -- Backfill antes de crear el indice unico
    ;WITH ranked AS (
        SELECT Id, ROW_NUMBER() OVER (ORDER BY CreatedAt, Id) AS rn
        FROM Cafe_Clientes WHERE Codigo IS NULL OR Codigo = N''
    )
    UPDATE c SET Codigo = RIGHT(N'0000' + CAST(r.rn AS NVARCHAR(10)), 4)
    FROM Cafe_Clientes c JOIN ranked r ON r.Id = c.Id;
    CREATE UNIQUE INDEX IX_CafeClientes_Codigo ON Cafe_Clientes(Codigo);
END
GO

-- Cafe_Productos: agregar Sku y Barcode
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'Sku' AND Object_ID = Object_ID('Cafe_Productos'))
    ALTER TABLE Cafe_Productos ADD Sku NVARCHAR(50) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'Barcode' AND Object_ID = Object_ID('Cafe_Productos'))
    ALTER TABLE Cafe_Productos ADD Barcode NVARCHAR(100) NULL;
GO

-- Cafe_Ventas: tipo de comprobante, cond. IVA del cliente, cond. de pago
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'TipoComprobante' AND Object_ID = Object_ID('Cafe_Ventas'))
    ALTER TABLE Cafe_Ventas ADD TipoComprobante NVARCHAR(10) NOT NULL CONSTRAINT DF_CafeVentas_TipoComprobante DEFAULT 'X';
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'CondicionIva' AND Object_ID = Object_ID('Cafe_Ventas'))
    ALTER TABLE Cafe_Ventas ADD CondicionIva NVARCHAR(20) NOT NULL CONSTRAINT DF_CafeVentas_CondicionIva DEFAULT 'CF';
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'CondicionPago' AND Object_ID = Object_ID('Cafe_Ventas'))
    ALTER TABLE Cafe_Ventas ADD CondicionPago NVARCHAR(20) NOT NULL CONSTRAINT DF_CafeVentas_CondicionPago DEFAULT 'EFECTIVO';
GO

-- Cafe_Settings: branding extra para listas de precios (Email, Web, Logo)
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'NegocioEmail' AND Object_ID = Object_ID('Cafe_Settings'))
    ALTER TABLE Cafe_Settings ADD NegocioEmail NVARCHAR(200) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'NegocioWeb' AND Object_ID = Object_ID('Cafe_Settings'))
    ALTER TABLE Cafe_Settings ADD NegocioWeb NVARCHAR(200) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'NegocioLogoUrl' AND Object_ID = Object_ID('Cafe_Settings'))
    ALTER TABLE Cafe_Settings ADD NegocioLogoUrl NVARCHAR(500) NULL;
GO

-- Cafe_Combos: bundles de productos. El precio NO se guarda; se calcula en vivo
-- al cotizar (suma de PVP*cantidad de cada item). Cuando se selecciona en una
-- venta, se "expande" en N items de Cafe_VentaItems con la logica normal.
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Cafe_Combos' AND xtype='U')
BEGIN
    CREATE TABLE Cafe_Combos (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Nombre NVARCHAR(200) NOT NULL,
        Descripcion NVARCHAR(500) NULL,
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        UpdatedAt DATETIME2 NULL
    );
    CREATE INDEX IX_CafeCombos_Nombre ON Cafe_Combos (Nombre);
    CREATE INDEX IX_CafeCombos_IsActive ON Cafe_Combos (IsActive);
END
GO

IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Cafe_ComboItems' AND xtype='U')
BEGIN
    CREATE TABLE Cafe_ComboItems (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        ComboId INT NOT NULL,
        ProductoId INT NOT NULL,
        Formato NVARCHAR(20) NOT NULL DEFAULT '1KG',  -- 1KG | MEDIO | CUARTO | UNIT
        Cantidad INT NOT NULL DEFAULT 1,
        Molienda NVARCHAR(30) NULL,                    -- solo CAFE
        EsDoyPack BIT NOT NULL DEFAULT 0,              -- solo CAFE
        SortOrder INT NOT NULL DEFAULT 0,
        CONSTRAINT FK_CafeComboItems_Combo FOREIGN KEY (ComboId) REFERENCES Cafe_Combos(Id) ON DELETE CASCADE,
        CONSTRAINT FK_CafeComboItems_Producto FOREIGN KEY (ProductoId) REFERENCES Cafe_Productos(Id)
    );
    CREATE INDEX IX_CafeComboItems_Combo ON Cafe_ComboItems (ComboId);
    CREATE INDEX IX_CafeComboItems_Producto ON Cafe_ComboItems (ProductoId);
END
GO

-- Cafe_Productos: PVP por producto + UxB (paso 1 OEM)
-- Para OTROS: Pvp2 = PVP fijo manual (lo paga OTRO), BarPctSobreCosto = % sobre costo (lo paga BAR si no es null).
-- Para CAFE: sin cambios, sigue usando Pvp1 (BAR) y Pvp2 (OTRO) como precio/kg.
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'BarPctSobreCosto' AND Object_ID = Object_ID('Cafe_Productos'))
    ALTER TABLE Cafe_Productos ADD BarPctSobreCosto DECIMAL(7,2) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'UxB' AND Object_ID = Object_ID('Cafe_Productos'))
    ALTER TABLE Cafe_Productos ADD UxB INT NULL;
GO

-- Migracion one-shot para OTROS:
--  - Si Pvp2 (OTRO) viene NULL, lo seteo en costo*(1+MargenOtrosNoBarPct/100) para preservar el precio que el motor venia calculando.
--  - BarPctSobreCosto lo seteo en MargenOtrosBarPct para preservar el precio BAR.
--  - Solo OTROS, solo si todavia no se aplico esta migracion.
IF NOT EXISTS (SELECT 1 FROM AppSettings WHERE [Key] = 'cafe_productos_pvp_per_product_migrated')
BEGIN
    DECLARE @MargenOtroPct DECIMAL(7,2) = ISNULL((SELECT TOP 1 MargenOtrosNoBarPct FROM Cafe_Settings WHERE Id = 1), 0);
    DECLARE @MargenBarPct DECIMAL(7,2) = ISNULL((SELECT TOP 1 MargenOtrosBarPct FROM Cafe_Settings WHERE Id = 1), 0);

    -- Pvp2 = costo * (1 + margenOtro/100), solo si Pvp2 esta vacio
    UPDATE Cafe_Productos
    SET Pvp2 = ROUND(Costo * (1 + (@MargenOtroPct / 100.0)), 2)
    WHERE Categoria = 'OTROS' AND Pvp2 IS NULL;

    -- BarPctSobreCosto = margenBar (siempre, es campo nuevo sin data previa)
    UPDATE Cafe_Productos
    SET BarPctSobreCosto = @MargenBarPct
    WHERE Categoria = 'OTROS' AND BarPctSobreCosto IS NULL;

    INSERT INTO AppSettings ([Key], [Value]) VALUES ('cafe_productos_pvp_per_product_migrated', '1');
END
GO

-- Cafe_Oems: lista de codigos del proveedor con costo + pvp sugerido.
-- No tiene stock, no se vende. Sirve para alimentar precio a las variantes (Cafe_Productos)
-- via FK opcional Cafe_Productos.OemId. Ver paso 3 OEM.
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Cafe_Oems' AND xtype='U')
BEGIN
    CREATE TABLE Cafe_Oems (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Codigo NVARCHAR(50) NOT NULL,
        Descripcion NVARCHAR(500) NULL,
        Marca NVARCHAR(100) NULL,
        Costo DECIMAL(18,2) NOT NULL DEFAULT 0,
        PvpConIva DECIMAL(18,2) NULL,
        IvaPct DECIMAL(5,2) NULL,
        Barcode NVARCHAR(100) NULL,
        Proveedor NVARCHAR(100) NULL,    -- ej "COLOMBRARO"
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        UpdatedAt DATETIME2 NULL,
        LastImportAt DATETIME2 NULL       -- timestamp de la ultima importacion que toco esta fila
    );
    CREATE UNIQUE INDEX IX_CafeOems_Codigo ON Cafe_Oems(Codigo);
    CREATE INDEX IX_CafeOems_Marca ON Cafe_Oems(Marca);
    CREATE INDEX IX_CafeOems_Proveedor ON Cafe_Oems(Proveedor);
END
GO

-- Cafe_Oems: UxB (unidades por bulto) — informativo, se autocompleta a la variante al vincular.
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'UxB' AND Object_ID = Object_ID('Cafe_Oems'))
    ALTER TABLE Cafe_Oems ADD UxB INT NULL;
GO

-- ═══════════════ MODULO COMPRAS ═══════════════
-- Proveedores: entidad simple con datos de contacto.
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Cafe_Proveedores' AND xtype='U')
BEGIN
    CREATE TABLE Cafe_Proveedores (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Nombre NVARCHAR(200) NOT NULL,
        Contacto NVARCHAR(200) NULL,
        Telefono NVARCHAR(50) NULL,
        Email NVARCHAR(200) NULL,
        Notas NVARCHAR(500) NULL,
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        UpdatedAt DATETIME2 NULL
    );
    CREATE INDEX IX_CafeProveedores_Nombre ON Cafe_Proveedores(Nombre);
    CREATE INDEX IX_CafeProveedores_IsActive ON Cafe_Proveedores(IsActive);
END
GO

-- Cafe_Proveedores: campos de identidad fiscal y direccion (importacion desde Contabilium).
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'Cuit' AND Object_ID = Object_ID('Cafe_Proveedores'))
    ALTER TABLE Cafe_Proveedores ADD Cuit NVARCHAR(20) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'CategoriaImpositiva' AND Object_ID = Object_ID('Cafe_Proveedores'))
    ALTER TABLE Cafe_Proveedores ADD CategoriaImpositiva NVARCHAR(20) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'Direccion' AND Object_ID = Object_ID('Cafe_Proveedores'))
    ALTER TABLE Cafe_Proveedores ADD Direccion NVARCHAR(300) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'CodigoPostal' AND Object_ID = Object_ID('Cafe_Proveedores'))
    ALTER TABLE Cafe_Proveedores ADD CodigoPostal NVARCHAR(20) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'Provincia' AND Object_ID = Object_ID('Cafe_Proveedores'))
    ALTER TABLE Cafe_Proveedores ADD Provincia NVARCHAR(100) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'Ciudad' AND Object_ID = Object_ID('Cafe_Proveedores'))
    ALTER TABLE Cafe_Proveedores ADD Ciudad NVARCHAR(100) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'Web' AND Object_ID = Object_ID('Cafe_Proveedores'))
    ALTER TABLE Cafe_Proveedores ADD Web NVARCHAR(200) NULL;
GO
SET QUOTED_IDENTIFIER ON;
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_CafeProveedores_Cuit' AND object_id = OBJECT_ID('Cafe_Proveedores'))
    CREATE UNIQUE INDEX IX_CafeProveedores_Cuit ON Cafe_Proveedores(Cuit) WHERE Cuit IS NOT NULL;
GO

-- Compras (cabecera): Estados BORRADOR, CONFIRMADA, PAGADA, ANULADA.
-- Al confirmar incrementa stock y pisa el costo del producto.
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Cafe_Compras' AND xtype='U')
BEGIN
    CREATE TABLE Cafe_Compras (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Numero NVARCHAR(20) NOT NULL,                  -- COMPRA-AAAA-NNNN
        ProveedorId INT NULL,
        ProveedorNombreSnapshot NVARCHAR(200) NULL,
        Fecha DATETIME2 NOT NULL,
        NumeroComprobante NVARCHAR(50) NULL,           -- factura/remito del proveedor
        Estado NVARCHAR(20) NOT NULL DEFAULT 'BORRADOR',
        Total DECIMAL(18,2) NOT NULL DEFAULT 0,
        Observaciones NVARCHAR(500) NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        UpdatedAt DATETIME2 NULL,
        ConfirmadaAt DATETIME2 NULL,
        PagadaAt DATETIME2 NULL,
        AnuladaAt DATETIME2 NULL,
        CONSTRAINT FK_CafeCompras_Proveedor FOREIGN KEY (ProveedorId) REFERENCES Cafe_Proveedores(Id)
    );
    CREATE UNIQUE INDEX IX_CafeCompras_Numero ON Cafe_Compras(Numero);
    CREATE INDEX IX_CafeCompras_Fecha ON Cafe_Compras(Fecha);
    CREATE INDEX IX_CafeCompras_Estado ON Cafe_Compras(Estado);
    CREATE INDEX IX_CafeCompras_Proveedor ON Cafe_Compras(ProveedorId);
END
GO

-- Items de compra: Cantidad en kg para CAFE, en unidades para OTROS.
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Cafe_CompraItems' AND xtype='U')
BEGIN
    CREATE TABLE Cafe_CompraItems (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        CompraId INT NOT NULL,
        ProductoId INT NOT NULL,
        ProductoNombreSnapshot NVARCHAR(200) NOT NULL,
        Categoria NVARCHAR(20) NOT NULL,
        Cantidad DECIMAL(18,3) NOT NULL,
        CostoUnitario DECIMAL(18,2) NOT NULL,
        Subtotal DECIMAL(18,2) NOT NULL,
        CONSTRAINT FK_CafeCompraItems_Compra FOREIGN KEY (CompraId) REFERENCES Cafe_Compras(Id) ON DELETE CASCADE,
        CONSTRAINT FK_CafeCompraItems_Producto FOREIGN KEY (ProductoId) REFERENCES Cafe_Productos(Id)
    );
    CREATE INDEX IX_CafeCompraItems_Compra ON Cafe_CompraItems(CompraId);
    CREATE INDEX IX_CafeCompraItems_Producto ON Cafe_CompraItems(ProductoId);
END
GO

-- ═══════════════ MARCAS (entidad propia con FK a proveedor) ═══════════════
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Cafe_Marcas' AND xtype='U')
BEGIN
    CREATE TABLE Cafe_Marcas (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Nombre NVARCHAR(100) NOT NULL,
        ProveedorId INT NULL,
        Notas NVARCHAR(500) NULL,
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        UpdatedAt DATETIME2 NULL,
        CONSTRAINT FK_CafeMarcas_Proveedor FOREIGN KEY (ProveedorId) REFERENCES Cafe_Proveedores(Id)
    );
    CREATE UNIQUE INDEX IX_CafeMarcas_Nombre ON Cafe_Marcas(Nombre);
    CREATE INDEX IX_CafeMarcas_Proveedor ON Cafe_Marcas(ProveedorId);
END
GO

-- Cafe_Productos.MarcaId (FK a Cafe_Marcas, reemplaza Marca texto)
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='MarcaId' AND Object_ID=Object_ID('Cafe_Productos'))
    ALTER TABLE Cafe_Productos ADD MarcaId INT NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE name='FK_CafeProductos_Marca')
    ALTER TABLE Cafe_Productos ADD CONSTRAINT FK_CafeProductos_Marca FOREIGN KEY (MarcaId) REFERENCES Cafe_Marcas(Id);
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name='IX_CafeProductos_MarcaId' AND object_id=OBJECT_ID('Cafe_Productos'))
    CREATE INDEX IX_CafeProductos_MarcaId ON Cafe_Productos(MarcaId);
GO

-- Cafe_Oems.MarcaId
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='MarcaId' AND Object_ID=Object_ID('Cafe_Oems'))
    ALTER TABLE Cafe_Oems ADD MarcaId INT NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE name='FK_CafeOems_Marca')
    ALTER TABLE Cafe_Oems ADD CONSTRAINT FK_CafeOems_Marca FOREIGN KEY (MarcaId) REFERENCES Cafe_Marcas(Id);
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name='IX_CafeOems_MarcaId' AND object_id=OBJECT_ID('Cafe_Oems'))
    CREATE INDEX IX_CafeOems_MarcaId ON Cafe_Oems(MarcaId);
GO

-- Migracion one-shot: poblar Cafe_Marcas con DISTINCT de Productos+OEMs y linkear FKs.
IF NOT EXISTS (SELECT 1 FROM AppSettings WHERE [Key] = 'cafe_marcas_migrated')
BEGIN
    INSERT INTO Cafe_Marcas (Nombre, IsActive, CreatedAt)
    SELECT DISTINCT LTRIM(RTRIM(Marca)), 1, GETUTCDATE()
    FROM (
        SELECT Marca FROM Cafe_Productos WHERE Marca IS NOT NULL AND LTRIM(RTRIM(Marca)) <> ''
        UNION
        SELECT Marca FROM Cafe_Oems WHERE Marca IS NOT NULL AND LTRIM(RTRIM(Marca)) <> ''
    ) t
    WHERE LTRIM(RTRIM(Marca)) NOT IN (SELECT Nombre FROM Cafe_Marcas);

    UPDATE p SET p.MarcaId = m.Id
    FROM Cafe_Productos p
    INNER JOIN Cafe_Marcas m ON m.Nombre = LTRIM(RTRIM(p.Marca))
    WHERE p.Marca IS NOT NULL AND p.MarcaId IS NULL;

    UPDATE o SET o.MarcaId = m.Id
    FROM Cafe_Oems o
    INNER JOIN Cafe_Marcas m ON m.Nombre = LTRIM(RTRIM(o.Marca))
    WHERE o.Marca IS NOT NULL AND o.MarcaId IS NULL;

    INSERT INTO AppSettings ([Key], [Value]) VALUES ('cafe_marcas_migrated', '1');
END
GO

-- Cafe_Productos: vinculo opcional al OEM origen.
-- 1 OEM puede alimentar a N variantes (ej OEM 8733 -> C8733BL, C8733NEG, etc).
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'OemId' AND Object_ID = Object_ID('Cafe_Productos'))
    ALTER TABLE Cafe_Productos ADD OemId INT NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE name = 'FK_CafeProductos_Oem')
    ALTER TABLE Cafe_Productos
        ADD CONSTRAINT FK_CafeProductos_Oem FOREIGN KEY (OemId) REFERENCES Cafe_Oems(Id);
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_CafeProductos_OemId' AND object_id = OBJECT_ID('Cafe_Productos'))
    CREATE INDEX IX_CafeProductos_OemId ON Cafe_Productos(OemId);
GO

-- Cafe_Productos: IVA % por producto. Default 21%, opcional 10.5% para alimentos.
-- Convencion: Pvp1 y Pvp2 se guardan SIN IVA. El precio con IVA se calcula al mostrar.
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'IvaPct' AND Object_ID = Object_ID('Cafe_Productos'))
    ALTER TABLE Cafe_Productos ADD IvaPct DECIMAL(5,2) NOT NULL DEFAULT 21;
GO

-- Cafe_Marcas: bloqueo de descuento. Marcas propias (Frikaf) nunca dan descuento.
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'BloqueaDescuento' AND Object_ID = Object_ID('Cafe_Marcas'))
    ALTER TABLE Cafe_Marcas ADD BloqueaDescuento BIT NOT NULL DEFAULT 0;
GO
-- Cafe_Marcas: margen sobre costo (% para PVP automatico de productos OTROS).
-- Default 100% (PVP = costo × 2). Editable por marca.
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'MargenPctSobreCosto' AND Object_ID = Object_ID('Cafe_Marcas'))
    ALTER TABLE Cafe_Marcas ADD MargenPctSobreCosto DECIMAL(7,2) NOT NULL DEFAULT 100;
GO

-- Reglas de precios: matriz tipo cliente x categoria (CAFE/OTROS) -> % descuento.
-- Opcionalmente con MarcaId para override puntual por marca.
-- Aplica sobre Pvp1 (lista 100%) del producto. Reemplaza Cafe_DescuentosCliente y MargenPctSobreCosto.
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Cafe_ReglasPrecios' AND xtype='U')
BEGIN
    CREATE TABLE Cafe_ReglasPrecios (
        Id INT PRIMARY KEY IDENTITY(1,1),
        TipoCliente NVARCHAR(20) NOT NULL,
        Categoria NVARCHAR(20) NOT NULL,
        MarcaId INT NULL,
        DescuentoPct DECIMAL(7,2) NOT NULL DEFAULT 0,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL,
        CONSTRAINT FK_ReglasPrecios_Marca FOREIGN KEY (MarcaId) REFERENCES Cafe_Marcas(Id) ON DELETE CASCADE
    );
END
GO
SET QUOTED_IDENTIFIER ON;
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name='UX_ReglasPrecios_General' AND object_id=OBJECT_ID('Cafe_ReglasPrecios'))
    CREATE UNIQUE INDEX UX_ReglasPrecios_General ON Cafe_ReglasPrecios(TipoCliente, Categoria) WHERE MarcaId IS NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name='UX_ReglasPrecios_Override' AND object_id=OBJECT_ID('Cafe_ReglasPrecios'))
    CREATE UNIQUE INDEX UX_ReglasPrecios_Override ON Cafe_ReglasPrecios(TipoCliente, Categoria, MarcaId) WHERE MarcaId IS NOT NULL;
GO

-- Seed default de reglas: BAR/Comercial x CAFE/OTROS.
IF NOT EXISTS (SELECT 1 FROM Cafe_ReglasPrecios)
BEGIN
    INSERT INTO Cafe_ReglasPrecios (TipoCliente, Categoria, DescuentoPct) VALUES ('BAR',  'CAFE',  50);
    INSERT INTO Cafe_ReglasPrecios (TipoCliente, Categoria, DescuentoPct) VALUES ('BAR',  'OTROS', 50);
    INSERT INTO Cafe_ReglasPrecios (TipoCliente, Categoria, DescuentoPct) VALUES ('OTRO', 'CAFE',  40);
    INSERT INTO Cafe_ReglasPrecios (TipoCliente, Categoria, DescuentoPct) VALUES ('OTRO', 'OTROS', 50);
END
GO

-- =============================================================================
-- Kits del modulo Cafe (productos compuestos con BOM/Bill of Materials).
-- Distinto de Cafe_Combos que son "promos" de cafe fraccionado (1kg/medio/cuarto).
-- Cafe_Kits: SKU compuesto (ej: 1925 = cesto + tapa). Stock virtual = MIN(componente / cantidad).
-- Cafe_KitItems: lista de productos simples que conforman el kit, con cantidad cada uno.
-- =============================================================================
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Cafe_Kits' AND xtype='U')
BEGIN
    CREATE TABLE Cafe_Kits (
        Id INT PRIMARY KEY IDENTITY(1,1),
        Sku NVARCHAR(100) NOT NULL,
        Nombre NVARCHAR(500) NOT NULL,
        Descripcion NVARCHAR(MAX) NULL,
        Categoria NVARCHAR(20) NOT NULL DEFAULT 'OTROS',
        Marca NVARCHAR(100) NULL,
        MarcaId INT NULL,
        Pvp1 DECIMAL(18,2) NULL,
        Pvp2 DECIMAL(18,2) NULL,
        IvaPct DECIMAL(5,2) NOT NULL DEFAULT 21,
        Notas NVARCHAR(500) NULL,
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL,
        CONSTRAINT FK_CafeKits_Marca FOREIGN KEY (MarcaId) REFERENCES Cafe_Marcas(Id) ON DELETE SET NULL
    );
    CREATE UNIQUE INDEX UX_CafeKits_Sku ON Cafe_Kits(Sku);
    CREATE INDEX IX_CafeKits_MarcaId ON Cafe_Kits(MarcaId);
END
GO
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Cafe_KitItems' AND xtype='U')
BEGIN
    CREATE TABLE Cafe_KitItems (
        Id INT PRIMARY KEY IDENTITY(1,1),
        KitId INT NOT NULL,
        ProductoId INT NOT NULL,
        Cantidad DECIMAL(18,3) NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CONSTRAINT FK_KitItems_Kit FOREIGN KEY (KitId) REFERENCES Cafe_Kits(Id) ON DELETE CASCADE,
        CONSTRAINT FK_KitItems_Producto FOREIGN KEY (ProductoId) REFERENCES Cafe_Productos(Id)
    );
    CREATE UNIQUE INDEX UX_CafeKitItems_KitProd ON Cafe_KitItems(KitId, ProductoId);
    CREATE INDEX IX_CafeKitItems_Producto ON Cafe_KitItems(ProductoId);
END
GO
-- MeliItems.CafeKitId: vinculacion publicacion MeLi -> Kit
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'CafeKitId' AND Object_ID = Object_ID('MeliItems'))
    ALTER TABLE MeliItems ADD CafeKitId INT NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE name = 'FK_MeliItems_CafeKit')
   AND EXISTS (SELECT * FROM sysobjects WHERE name='Cafe_Kits' AND xtype='U')
    ALTER TABLE MeliItems ADD CONSTRAINT FK_MeliItems_CafeKit FOREIGN KEY (CafeKitId) REFERENCES Cafe_Kits(Id) ON DELETE SET NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_MeliItems_CafeKitId' AND object_id = OBJECT_ID('MeliItems'))
    CREATE INDEX IX_MeliItems_CafeKitId ON MeliItems(CafeKitId);
GO

-- Historial de cambios de precio por producto Cafe.
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Cafe_HistorialPrecios' AND xtype='U')
BEGIN
    CREATE TABLE Cafe_HistorialPrecios (
        Id INT PRIMARY KEY IDENTITY(1,1),
        ProductoId INT NOT NULL,
        Pvp1Anterior DECIMAL(18,2) NULL,
        Pvp2Anterior DECIMAL(18,2) NULL,
        CostoAnterior DECIMAL(18,2) NULL,
        IvaPctAnterior DECIMAL(5,2) NULL,
        Pvp1Nuevo DECIMAL(18,2) NULL,
        Pvp2Nuevo DECIMAL(18,2) NULL,
        CostoNuevo DECIMAL(18,2) NULL,
        IvaPctNuevo DECIMAL(5,2) NULL,
        ChangedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        ChangedBy NVARCHAR(100) NULL,
        Motivo NVARCHAR(500) NULL,
        CONSTRAINT FK_HistPrecios_Producto FOREIGN KEY (ProductoId) REFERENCES Cafe_Productos(Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_HistPrecios_ProductoId ON Cafe_HistorialPrecios(ProductoId);
    CREATE INDEX IX_HistPrecios_ChangedAt ON Cafe_HistorialPrecios(ChangedAt DESC);
END
GO

-- Tabla de descuentos por tipo de cliente (BAR / OTRO) y opcionalmente por marca.
-- MarcaId NULL = descuento general para ese tipo (aplica a todas las marcas que no tengan override).
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Cafe_DescuentosCliente' AND xtype='U')
BEGIN
    CREATE TABLE Cafe_DescuentosCliente (
        Id INT PRIMARY KEY IDENTITY(1,1),
        TipoCliente NVARCHAR(20) NOT NULL,
        MarcaId INT NULL,
        DescuentoPct DECIMAL(7,2) NOT NULL DEFAULT 0,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        UpdatedAt DATETIME2 NULL,
        CONSTRAINT FK_DescCliente_Marca FOREIGN KEY (MarcaId) REFERENCES Cafe_Marcas(Id) ON DELETE CASCADE
    );
END
GO
SET QUOTED_IDENTIFIER ON;
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UX_DescCliente_TipoMarca' AND object_id = OBJECT_ID('Cafe_DescuentosCliente'))
    CREATE UNIQUE INDEX UX_DescCliente_TipoMarca ON Cafe_DescuentosCliente(TipoCliente, MarcaId) WHERE MarcaId IS NOT NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UX_DescCliente_TipoDefault' AND object_id = OBJECT_ID('Cafe_DescuentosCliente'))
    CREATE UNIQUE INDEX UX_DescCliente_TipoDefault ON Cafe_DescuentosCliente(TipoCliente) WHERE MarcaId IS NULL;
GO

-- Seed inicial: 25% para BAR y OTRO. Solo se carga si la tabla esta vacia.
IF NOT EXISTS (SELECT 1 FROM Cafe_DescuentosCliente)
BEGIN
    INSERT INTO Cafe_DescuentosCliente (TipoCliente, MarcaId, DescuentoPct) VALUES ('BAR', NULL, 25);
    INSERT INTO Cafe_DescuentosCliente (TipoCliente, MarcaId, DescuentoPct) VALUES ('OTRO', NULL, 25);
END
GO

-- =============================================================================
-- Contabilium: tablas de staging para cotejo SKU MeLi <-> Contabilium.
-- Solo guardan lo descargado de los Excels. Las tablas reales (Products, Combos)
-- se siguen creando aparte cuando el usuario decide vincular.
-- =============================================================================
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Contab_Productos' AND xtype='U')
BEGIN
    CREATE TABLE Contab_Productos (
        Id INT PRIMARY KEY IDENTITY(1,1),
        Sku NVARCHAR(100) NOT NULL,
        SkuPadre NVARCHAR(100) NULL,
        Tipo NVARCHAR(50) NULL,
        Nombre NVARCHAR(500) NULL,
        Atributo1 NVARCHAR(100) NULL,
        VarianteAtributo1 NVARCHAR(200) NULL,
        Atributo2 NVARCHAR(100) NULL,
        VarianteAtributo2 NVARCHAR(200) NULL,
        CodigoBarras NVARCHAR(100) NULL,
        CodigoOem NVARCHAR(100) NULL,
        Estado NVARCHAR(20) NULL,
        CostoInterno DECIMAL(18,4) NULL,
        Precio DECIMAL(18,4) NULL,
        Iva DECIMAL(18,4) NULL,
        PrecioFinal DECIMAL(18,4) NULL,
        Stock DECIMAL(18,4) NULL,
        Rubro NVARCHAR(200) NULL,
        SubRubro NVARCHAR(200) NULL,
        Proveedor NVARCHAR(200) NULL,
        Descripcion NVARCHAR(MAX) NULL,
        ImportedAt DATETIME2 NOT NULL DEFAULT GETDATE()
    );
    CREATE UNIQUE INDEX UX_ContabProductos_Sku ON Contab_Productos(Sku);
    CREATE INDEX IX_ContabProductos_SkuPadre ON Contab_Productos(SkuPadre);
END
GO

IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Contab_Combos' AND xtype='U')
BEGIN
    CREATE TABLE Contab_Combos (
        Id INT PRIMARY KEY IDENTITY(1,1),
        SkuCombo NVARCHAR(100) NOT NULL,
        Nombre NVARCHAR(500) NULL,
        Descripcion NVARCHAR(MAX) NULL,
        Estado NVARCHAR(20) NULL,
        CostoInterno DECIMAL(18,4) NULL,
        Rentabilidad DECIMAL(18,4) NULL,
        PrecioUnitario DECIMAL(18,4) NULL,
        Iva DECIMAL(18,4) NULL,
        PrecioFinal DECIMAL(18,4) NULL,
        PrecioAutomatico BIT NULL,
        ImportedAt DATETIME2 NOT NULL DEFAULT GETDATE()
    );
    CREATE UNIQUE INDEX UX_ContabCombos_Sku ON Contab_Combos(SkuCombo);
END
GO

IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Contab_ComboItems' AND xtype='U')
BEGIN
    CREATE TABLE Contab_ComboItems (
        Id INT PRIMARY KEY IDENTITY(1,1),
        SkuCombo NVARCHAR(100) NOT NULL,
        SkuComponente NVARCHAR(100) NOT NULL, -- corresponde a la columna "Codigo" de Contabilium
        NombreComponente NVARCHAR(500) NULL,
        Cantidad DECIMAL(18,4) NOT NULL DEFAULT 1,
        CostoInternoComponente DECIMAL(18,4) NULL,
        PrecioComponente DECIMAL(18,4) NULL,
        ImportedAt DATETIME2 NOT NULL DEFAULT GETDATE()
    );
    CREATE INDEX IX_ContabComboItems_Combo ON Contab_ComboItems(SkuCombo);
    CREATE INDEX IX_ContabComboItems_Comp ON Contab_ComboItems(SkuComponente);
END
GO

-- MeliItems: soporte de variantes.
-- Cuando una publicacion tiene variations en MeLi, cada variante se almacena como una
-- fila independiente combinando MeliItemId (padre) + VariationId. La fila padre (sin
-- variantes) o las publicaciones simples mantienen VariationId NULL.
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'VariationId' AND Object_ID = Object_ID('MeliItems'))
    ALTER TABLE MeliItems ADD VariationId NVARCHAR(50) NULL;
GO
-- Atributos visibles de la variante (ej: "Negro", "Blanco / Talle XL").
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'VariationAttributes' AND Object_ID = Object_ID('MeliItems'))
    ALTER TABLE MeliItems ADD VariationAttributes NVARCHAR(500) NULL;
GO

-- El indice unico viejo (solo MeliItemId) debe pasar a (MeliItemId, VariationId).
IF EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_MeliItems_MeliItemId' AND object_id = OBJECT_ID('MeliItems'))
    DROP INDEX IX_MeliItems_MeliItemId ON MeliItems;
GO
-- Filtered indexes requieren QUOTED_IDENTIFIER ON.
SET QUOTED_IDENTIFIER ON;
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_MeliItems_MeliItemId_VariationId' AND object_id = OBJECT_ID('MeliItems'))
    CREATE UNIQUE INDEX IX_MeliItems_MeliItemId_VariationId
        ON MeliItems (MeliItemId, VariationId)
        WHERE VariationId IS NOT NULL;
GO
-- Indice unico para filas sin variante (publicacion simple o registro padre).
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_MeliItems_MeliItemId_NoVariation' AND object_id = OBJECT_ID('MeliItems'))
    CREATE UNIQUE INDEX IX_MeliItems_MeliItemId_NoVariation
        ON MeliItems (MeliItemId)
        WHERE VariationId IS NULL;
GO

-- MeliItems: vinculo nuevo a Cafe_Productos / Cafe_Combos (la base "nueva" del modulo Cafe).
-- Coexiste con ProductId/ComboId (que apuntan al sistema generico legacy).
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'CafeProductoId' AND Object_ID = Object_ID('MeliItems'))
    ALTER TABLE MeliItems ADD CafeProductoId INT NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = 'CafeComboId' AND Object_ID = Object_ID('MeliItems'))
    ALTER TABLE MeliItems ADD CafeComboId INT NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE name = 'FK_MeliItems_CafeProducto')
   AND EXISTS (SELECT * FROM sysobjects WHERE name='Cafe_Productos' AND xtype='U')
    ALTER TABLE MeliItems ADD CONSTRAINT FK_MeliItems_CafeProducto FOREIGN KEY (CafeProductoId) REFERENCES Cafe_Productos(Id) ON DELETE SET NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_MeliItems_CafeProductoId' AND object_id = OBJECT_ID('MeliItems'))
    CREATE INDEX IX_MeliItems_CafeProductoId ON MeliItems(CafeProductoId);
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_MeliItems_CafeComboId' AND object_id = OBJECT_ID('MeliItems'))
    CREATE INDEX IX_MeliItems_CafeComboId ON MeliItems(CafeComboId);
GO

-- Cafe_Settings: campos fiscales del negocio para emitir comprobantes/facturas.
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='NegocioRazonSocial' AND Object_ID=Object_ID('Cafe_Settings'))
    ALTER TABLE Cafe_Settings ADD NegocioRazonSocial NVARCHAR(200) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='NegocioCondicionIva' AND Object_ID=Object_ID('Cafe_Settings'))
    ALTER TABLE Cafe_Settings ADD NegocioCondicionIva NVARCHAR(50) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='NegocioIngresosBrutos' AND Object_ID=Object_ID('Cafe_Settings'))
    ALTER TABLE Cafe_Settings ADD NegocioIngresosBrutos NVARCHAR(50) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='NegocioInicioActividad' AND Object_ID=Object_ID('Cafe_Settings'))
    ALTER TABLE Cafe_Settings ADD NegocioInicioActividad DATE NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='NegocioLocalidad' AND Object_ID=Object_ID('Cafe_Settings'))
    ALTER TABLE Cafe_Settings ADD NegocioLocalidad NVARCHAR(150) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='NegocioCp' AND Object_ID=Object_ID('Cafe_Settings'))
    ALTER TABLE Cafe_Settings ADD NegocioCp NVARCHAR(20) NULL;
GO

-- Cafe_Clientes: campos extra para facturacion y comprobantes.
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='RazonSocial' AND Object_ID=Object_ID('Cafe_Clientes'))
    ALTER TABLE Cafe_Clientes ADD RazonSocial NVARCHAR(200) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='Localidad' AND Object_ID=Object_ID('Cafe_Clientes'))
    ALTER TABLE Cafe_Clientes ADD Localidad NVARCHAR(150) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='Ciudad' AND Object_ID=Object_ID('Cafe_Clientes'))
    ALTER TABLE Cafe_Clientes ADD Ciudad NVARCHAR(150) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='Cp' AND Object_ID=Object_ID('Cafe_Clientes'))
    ALTER TABLE Cafe_Clientes ADD Cp NVARCHAR(20) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='CondicionIvaDefault' AND Object_ID=Object_ID('Cafe_Clientes'))
    ALTER TABLE Cafe_Clientes ADD CondicionIvaDefault NVARCHAR(20) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='DomicilioEntrega' AND Object_ID=Object_ID('Cafe_Clientes'))
    ALTER TABLE Cafe_Clientes ADD DomicilioEntrega NVARCHAR(500) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='ComentariosComprobante' AND Object_ID=Object_ID('Cafe_Clientes'))
    ALTER TABLE Cafe_Clientes ADD ComentariosComprobante NVARCHAR(MAX) NULL;
GO

-- Cafe_Ventas: snapshots de los nuevos campos del cliente (para que el comprobante
-- histórico no cambie si después editas el cliente).
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='ClienteRazonSocialSnapshot' AND Object_ID=Object_ID('Cafe_Ventas'))
    ALTER TABLE Cafe_Ventas ADD ClienteRazonSocialSnapshot NVARCHAR(200) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='ClienteDomicilioEntregaSnapshot' AND Object_ID=Object_ID('Cafe_Ventas'))
    ALTER TABLE Cafe_Ventas ADD ClienteDomicilioEntregaSnapshot NVARCHAR(500) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='ClienteComentariosComprobante' AND Object_ID=Object_ID('Cafe_Ventas'))
    ALTER TABLE Cafe_Ventas ADD ClienteComentariosComprobante NVARCHAR(MAX) NULL;
GO


-- Cafe_Ventas: snapshots fiscales del cliente al emitir (para comprobante completo).
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='ClienteCuitSnapshot' AND Object_ID=Object_ID('Cafe_Ventas'))
    ALTER TABLE Cafe_Ventas ADD ClienteCuitSnapshot NVARCHAR(50) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='ClienteDireccionSnapshot' AND Object_ID=Object_ID('Cafe_Ventas'))
    ALTER TABLE Cafe_Ventas ADD ClienteDireccionSnapshot NVARCHAR(300) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='ClienteLocalidadSnapshot' AND Object_ID=Object_ID('Cafe_Ventas'))
    ALTER TABLE Cafe_Ventas ADD ClienteLocalidadSnapshot NVARCHAR(150) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='ClienteCiudadSnapshot' AND Object_ID=Object_ID('Cafe_Ventas'))
    ALTER TABLE Cafe_Ventas ADD ClienteCiudadSnapshot NVARCHAR(150) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='ClienteCpSnapshot' AND Object_ID=Object_ID('Cafe_Ventas'))
    ALTER TABLE Cafe_Ventas ADD ClienteCpSnapshot NVARCHAR(20) NULL;
GO

-- ===== MeliQuestions: preguntas de MercadoLibre con notificacion + respuesta desde la app =====
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name='MeliQuestions')
BEGIN
    CREATE TABLE MeliQuestions (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        MeliQuestionId BIGINT NOT NULL,
        MeliAccountId INT NOT NULL,
        ItemId NVARCHAR(50) NOT NULL,
        ItemTitle NVARCHAR(500) NULL,
        ItemThumbnail NVARCHAR(500) NULL,
        FromUserId BIGINT NOT NULL,
        FromNickname NVARCHAR(100) NULL,
        Text NVARCHAR(MAX) NOT NULL,
        AnswerText NVARCHAR(MAX) NULL,
        Status NVARCHAR(30) NOT NULL CONSTRAINT DF_MeliQuestions_Status DEFAULT 'UNANSWERED',
        DateCreated DATETIME2 NOT NULL,
        DateAnswered DATETIME2 NULL,
        SeenAt DATETIME2 NULL,
        LastSyncedAt DATETIME2 NOT NULL CONSTRAINT DF_MeliQuestions_LastSync DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_MeliQuestions_QuestionId UNIQUE (MeliQuestionId),
        CONSTRAINT FK_MeliQuestions_MeliAccount FOREIGN KEY (MeliAccountId) REFERENCES MeliAccounts(Id)
    );
    CREATE INDEX IX_MeliQuestions_Status ON MeliQuestions(Status);
    CREATE INDEX IX_MeliQuestions_DateCreated ON MeliQuestions(DateCreated DESC);
    CREATE INDEX IX_MeliQuestions_AccountId ON MeliQuestions(MeliAccountId);
END
GO

-- Job: sincronizar preguntas de MeLi (UNANSWERED) cada 1 minuto
IF NOT EXISTS (SELECT * FROM ScheduledProcesses WHERE Code = 'SyncMeliQuestions')
BEGIN
    INSERT INTO ScheduledProcesses (Code, Name, Description, TriggerType, IntervalMinutes, IsEnabled, NextRunAt)
    VALUES ('SyncMeliQuestions', 'Sincronizar Preguntas MeLi', 'Polea cada 1 minuto las preguntas sin responder de todas las cuentas conectadas, para que la campanita y el sonido avisen al usuario', 'Interval', 1, 1, SYSUTCDATETIME());
END
GO

-- MeliItems: formato del cafe (1KG | MEDIO | CUARTO) que representa cada publicacion vinculada
-- a un CafeProducto. Sirve para el push de stock + precio desde el modulo cafe.
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='CafeFormato' AND Object_ID=Object_ID('MeliItems'))
    ALTER TABLE MeliItems ADD CafeFormato NVARCHAR(10) NULL;
GO

-- MeliItems: tipo de logistica (fulfillment=Full / drop_off / cross_docking / etc.)
-- Sirve para no pushear stock a publicaciones Full (la API estandar no lo permite).
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='LogisticType' AND Object_ID=Object_ID('MeliItems'))
    ALTER TABLE MeliItems ADD LogisticType NVARCHAR(30) NULL;
GO

-- ===== MeliShipments: snapshot de envios para el mapa de rutas =====
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name='MeliShipments')
BEGIN
    CREATE TABLE MeliShipments (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        MeliShipmentId BIGINT NOT NULL,
        MeliAccountId INT NOT NULL,
        MeliOrderId BIGINT NULL,
        Status NVARCHAR(40) NULL,
        Substatus NVARCHAR(40) NULL,
        LogisticType NVARCHAR(30) NULL,
        TrackingNumber NVARCHAR(60) NULL,
        ReceiverName NVARCHAR(200) NULL,
        ReceiverPhone NVARCHAR(50) NULL,
        AddressLine NVARCHAR(300) NULL,
        StreetName NVARCHAR(200) NULL,
        StreetNumber NVARCHAR(20) NULL,
        Neighborhood NVARCHAR(150) NULL,
        City NVARCHAR(150) NULL,
        State NVARCHAR(150) NULL,
        ZipCode NVARCHAR(20) NULL,
        Latitude DECIMAL(10,7) NULL,
        Longitude DECIMAL(10,7) NULL,
        GeolocationType NVARCHAR(50) NULL,
        Comment NVARCHAR(500) NULL,
        ItemsSummary NVARCHAR(500) NULL,
        OrderTotal DECIMAL(18,2) NULL,
        DateCreated DATETIME2 NULL,
        DateReadyToShip DATETIME2 NULL,
        DateShipped DATETIME2 NULL,
        DateDelivered DATETIME2 NULL,
        EstimatedDeliveryFinal DATETIME2 NULL,
        EstimatedDeliveryLimit DATETIME2 NULL,
        InternalStatus NVARCHAR(30) NOT NULL CONSTRAINT DF_MeliShipments_InternalStatus DEFAULT 'pending',
        Notes NVARCHAR(MAX) NULL,
        LastSyncedAt DATETIME2 NOT NULL CONSTRAINT DF_MeliShipments_LastSync DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_MeliShipments_ShipId UNIQUE (MeliShipmentId),
        CONSTRAINT FK_MeliShipments_Account FOREIGN KEY (MeliAccountId) REFERENCES MeliAccounts(Id)
    );
    CREATE INDEX IX_MeliShipments_LogisticType ON MeliShipments(LogisticType);
    CREATE INDEX IX_MeliShipments_Status ON MeliShipments(Status);
    CREATE INDEX IX_MeliShipments_DateCreated ON MeliShipments(DateCreated DESC);
END
GO

-- Permiso de menu para el modulo de mapeo (rutas Flex)
IF NOT EXISTS (SELECT * FROM RolePermissions WHERE RoleId=1 AND MenuKey='mapeo')
    INSERT INTO RolePermissions (RoleId, MenuKey) VALUES (1, 'mapeo');
GO

-- MeliShipments: nickname del comprador (para cotejar con el panel de MeLi)
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='BuyerNickname' AND Object_ID=Object_ID('MeliShipments'))
    ALTER TABLE MeliShipments ADD BuyerNickname NVARCHAR(100) NULL;
GO

-- ===== Mapeo: Repartidores y libreta de direcciones favoritas =====
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name='MapeoDrivers')
BEGIN
    CREATE TABLE MapeoDrivers (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Nombre NVARCHAR(100) NOT NULL,
        Telefono NVARCHAR(50) NULL,
        Color NVARCHAR(10) NOT NULL CONSTRAINT DF_MapeoDrivers_Color DEFAULT '#1d4ed8',
        IsActive BIT NOT NULL CONSTRAINT DF_MapeoDrivers_Active DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_MapeoDrivers_Created DEFAULT SYSUTCDATETIME(),
        UpdatedAt DATETIME2 NULL
    );
END
GO
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name='MapeoFavoritos')
BEGIN
    CREATE TABLE MapeoFavoritos (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Alias NVARCHAR(100) NOT NULL,
        Direccion NVARCHAR(300) NOT NULL,
        Latitude DECIMAL(10,7) NOT NULL,
        Longitude DECIMAL(10,7) NOT NULL,
        ContactName NVARCHAR(150) NULL,
        Telefono NVARCHAR(50) NULL,
        Notas NVARCHAR(500) NULL,
        IsActive BIT NOT NULL CONSTRAINT DF_MapeoFavoritos_Active DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_MapeoFavoritos_Created DEFAULT SYSUTCDATETIME(),
        UpdatedAt DATETIME2 NULL
    );
    CREATE INDEX IX_MapeoFavoritos_Alias ON MapeoFavoritos(Alias);
END
GO

-- ===== Mapeo: paradas (puede venir de Flex, favorito, venta cafe o manual) =====
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name='MapeoStops')
BEGIN
    CREATE TABLE MapeoStops (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        -- Origen: 'flex' (referencia a MeliShipmentId), 'favorito', 'venta_cafe', 'manual'.
        Origin NVARCHAR(20) NOT NULL,
        OriginRefId NVARCHAR(50) NULL,
        Alias NVARCHAR(100) NULL,
        Direccion NVARCHAR(300) NOT NULL,
        Latitude DECIMAL(10,7) NOT NULL,
        Longitude DECIMAL(10,7) NOT NULL,
        ContactName NVARCHAR(150) NULL,
        Telefono NVARCHAR(50) NULL,
        Notas NVARCHAR(500) NULL,
        InternalStatus NVARCHAR(30) NOT NULL CONSTRAINT DF_MapeoStops_Status DEFAULT 'pending',
        AssignedDriverId INT NULL,
        OrderInRoute INT NULL,
        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_MapeoStops_Created DEFAULT SYSUTCDATETIME(),
        UpdatedAt DATETIME2 NULL,
        CONSTRAINT FK_MapeoStops_Driver FOREIGN KEY (AssignedDriverId) REFERENCES MapeoDrivers(Id) ON DELETE SET NULL
    );
    CREATE INDEX IX_MapeoStops_Origin ON MapeoStops(Origin, OriginRefId);
    CREATE INDEX IX_MapeoStops_Driver ON MapeoStops(AssignedDriverId);
END
GO

-- MapeoStops: slot del vehículo del día (asignación visual ad-hoc, separa el "vehículo" del "chofer").
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='AssignedVehicleSlot' AND Object_ID=Object_ID('MapeoStops'))
    ALTER TABLE MapeoStops ADD AssignedVehicleSlot INT NULL;
GO

-- MapeoDrivers: token compartible para que el chofer acceda a su ruta sin login
IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name='ShareToken' AND Object_ID=Object_ID('MapeoDrivers'))
    ALTER TABLE MapeoDrivers ADD ShareToken NVARCHAR(64) NULL;
GO
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name='UX_MapeoDrivers_ShareToken')
    CREATE UNIQUE INDEX UX_MapeoDrivers_ShareToken ON MapeoDrivers(ShareToken) WHERE ShareToken IS NOT NULL;
GO

-- ===== Mapeo: snapshots de rutas armadas (historial) =====
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name='MapeoRouteSnapshots')
BEGIN
    CREATE TABLE MapeoRouteSnapshots (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Title NVARCHAR(200) NOT NULL,
        StopsCount INT NOT NULL,
        VehiclesCount INT NOT NULL,
        DriversCount INT NOT NULL,
        StopsJson NVARCHAR(MAX) NOT NULL,
        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_MapeoSnap_Created DEFAULT SYSUTCDATETIME(),
        CreatedByUsername NVARCHAR(100) NULL,
        Notes NVARCHAR(500) NULL
    );
    CREATE INDEX IX_MapeoSnap_Created ON MapeoRouteSnapshots(CreatedAt DESC);
END
GO
