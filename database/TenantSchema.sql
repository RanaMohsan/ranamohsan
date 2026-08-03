GO
GO


IF OBJECT_ID('CompanyInformation') IS NULL
BEGIN
CREATE TABLE CompanyInformation(
    CompanyInformationId INT IDENTITY(1,1) PRIMARY KEY,
    CompanyName NVARCHAR(150) NOT NULL,
    AddressLine NVARCHAR(250) NULL,
    PhoneNo NVARCHAR(50) NULL,
    Email NVARCHAR(100) NULL,
    Website NVARCHAR(100) NULL,
    TaxRegistrationNo NVARCHAR(50) NULL,
    LogoPath NVARCHAR(500) NULL,
    LogoImage VARBINARY(MAX) NULL,
    UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
END;

IF NOT EXISTS (SELECT 1 FROM CompanyInformation)
BEGIN
    INSERT INTO CompanyInformation(CompanyName, AddressLine, PhoneNo, Email, Website, TaxRegistrationNo, LogoPath)
    VALUES('PayNex_POS_B1', 'Main Branch', '', '', '', '', '');
END;

IF OBJECT_ID('Currencies') IS NULL
BEGIN
CREATE TABLE Currencies(
    CurrencyId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CurrencyCode NVARCHAR(10) NOT NULL,
    CurrencyName NVARCHAR(80) NOT NULL,
    Symbol NVARCHAR(12) NOT NULL,
    DecimalPlaces TINYINT NOT NULL CONSTRAINT DF_Currencies_DecimalPlaces DEFAULT 2,
    ExchangeRate DECIMAL(18,6) NOT NULL CONSTRAINT DF_Currencies_ExchangeRate DEFAULT 1,
    IsBase BIT NOT NULL CONSTRAINT DF_Currencies_IsBase DEFAULT 0,
    IsActive BIT NOT NULL CONSTRAINT DF_Currencies_IsActive DEFAULT 1,
    CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_Currencies_CreatedAt DEFAULT SYSUTCDATETIME(),
    UpdatedAt DATETIME2 NULL,
    CONSTRAINT UQ_Currencies_Code UNIQUE(CurrencyCode)
);
CREATE UNIQUE INDEX UX_Currencies_Base ON Currencies(IsBase) WHERE IsBase=1;
END;

IF NOT EXISTS(SELECT 1 FROM Currencies)
    INSERT INTO Currencies(CurrencyCode,CurrencyName,Symbol,DecimalPlaces,ExchangeRate,IsBase,IsActive)
    VALUES('PKR','Pakistani Rupee','Rs.',2,1,1,1);

IF OBJECT_ID('Roles') IS NULL
BEGIN
CREATE TABLE Roles(
    RoleId INT IDENTITY(1,1) PRIMARY KEY,
    RoleName NVARCHAR(50) NOT NULL UNIQUE
);
END;

IF OBJECT_ID('Stores') IS NULL
BEGIN
CREATE TABLE Stores(
    StoreId INT IDENTITY(1,1) PRIMARY KEY,
    StoreCode NVARCHAR(30) NOT NULL UNIQUE,
    StoreName NVARCHAR(100) NOT NULL,
    BranchCode NVARCHAR(30) NULL,
    BranchName NVARCHAR(100) NULL,
    AddressLine NVARCHAR(250) NULL,
    IsMainBranch BIT NOT NULL DEFAULT 0,
    IsActive BIT NOT NULL DEFAULT 1
);
END;

IF OBJECT_ID('Terminals') IS NULL
BEGIN
CREATE TABLE Terminals(
    TerminalId INT IDENTITY(1,1) PRIMARY KEY,
    StoreId INT NOT NULL FOREIGN KEY REFERENCES Stores(StoreId),
    TerminalCode NVARCHAR(30) NOT NULL,
    TerminalName NVARCHAR(100) NOT NULL,
    IsActive BIT NOT NULL DEFAULT 1,
    CONSTRAINT UQ_Terminal UNIQUE(StoreId, TerminalCode)
);
END;

IF OBJECT_ID('Users') IS NULL
BEGIN
CREATE TABLE Users(
    UserId INT IDENTITY(1,1) PRIMARY KEY,
    UserName NVARCHAR(50) NOT NULL UNIQUE,
    DisplayName NVARCHAR(100) NOT NULL,
    Email NVARCHAR(180) NULL,
    PhoneNumber NVARCHAR(40) NULL,
    EmailVerified BIT NOT NULL DEFAULT 0,
    IsCompanySuperAdmin BIT NOT NULL DEFAULT 0,
    PasswordHash NVARCHAR(500) NOT NULL,
    RoleId INT NOT NULL FOREIGN KEY REFERENCES Roles(RoleId),
    StoreId INT NOT NULL FOREIGN KEY REFERENCES Stores(StoreId),
    IsActive BIT NOT NULL DEFAULT 1,
    ProfileImage VARBINARY(MAX) NULL,
    ProfileImageContentType NVARCHAR(80) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
END;


IF COL_LENGTH('Users','Email') IS NULL ALTER TABLE Users ADD Email NVARCHAR(180) NULL;
IF COL_LENGTH('Users','PhoneNumber') IS NULL ALTER TABLE Users ADD PhoneNumber NVARCHAR(40) NULL;
IF COL_LENGTH('Users','EmailVerified') IS NULL ALTER TABLE Users ADD EmailVerified BIT NOT NULL CONSTRAINT DF_Users_EmailVerified DEFAULT 0;
IF COL_LENGTH('Users','IsCompanySuperAdmin') IS NULL ALTER TABLE Users ADD IsCompanySuperAdmin BIT NOT NULL CONSTRAINT DF_Users_IsCompanySuperAdmin DEFAULT 0;
IF COL_LENGTH('Users','UpdatedAt') IS NULL ALTER TABLE Users ADD UpdatedAt DATETIME2 NULL;
IF COL_LENGTH('Users','ProfileImage') IS NULL ALTER TABLE Users ADD ProfileImage VARBINARY(MAX) NULL;
IF COL_LENGTH('Users','ProfileImageContentType') IS NULL ALTER TABLE Users ADD ProfileImageContentType NVARCHAR(80) NULL;


IF OBJECT_ID('UserPermissions') IS NULL
BEGIN
CREATE TABLE UserPermissions(
    UserId INT NOT NULL,
    PermissionKey NVARCHAR(120) NOT NULL,
    IsAllowed BIT NOT NULL DEFAULT 0,
    UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_UserPermissions PRIMARY KEY(UserId, PermissionKey)
);
END;

IF OBJECT_ID('UserBranchAssignments') IS NULL
BEGIN
CREATE TABLE UserBranchAssignments(
    UserId INT NOT NULL,
    StoreId INT NOT NULL,
    IsDefault BIT NOT NULL DEFAULT 0,
    CONSTRAINT PK_UserBranchAssignments PRIMARY KEY(UserId, StoreId)
);
END;

IF NOT EXISTS(SELECT 1 FROM Roles WHERE RoleName='Company Super Admin') INSERT INTO Roles(RoleName) VALUES('Company Super Admin');
IF NOT EXISTS(SELECT 1 FROM Roles WHERE RoleName='Manager') INSERT INTO Roles(RoleName) VALUES('Manager');
IF NOT EXISTS(SELECT 1 FROM Roles WHERE RoleName='Cashier') INSERT INTO Roles(RoleName) VALUES('Cashier');
GO

IF OBJECT_ID('Customers') IS NULL
BEGIN
CREATE TABLE Customers(
    CustomerId INT IDENTITY(1,1) PRIMARY KEY,
    CustomerCode NVARCHAR(30) NOT NULL UNIQUE,
    CustomerName NVARCHAR(150) NOT NULL,
    Mobile NVARCHAR(30) NULL,
    Email NVARCHAR(100) NULL,
    AddressLine NVARCHAR(250) NULL,
    CreditLimit DECIMAL(18,2) NOT NULL DEFAULT 0,
    LoyaltyPoints DECIMAL(18,2) NOT NULL DEFAULT 0,
    IsActive BIT NOT NULL DEFAULT 1,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
END;

IF COL_LENGTH('Customers','OpeningBalance') IS NULL
BEGIN
    ALTER TABLE Customers ADD OpeningBalance DECIMAL(18,2) NOT NULL CONSTRAINT DF_Customers_OpeningBalance DEFAULT 0;
END;

IF COL_LENGTH('Customers','CurrentBalance') IS NULL
BEGIN
    ALTER TABLE Customers ADD CurrentBalance DECIMAL(18,2) NOT NULL CONSTRAINT DF_Customers_CurrentBalance DEFAULT 0;
END;

IF OBJECT_ID('Suppliers') IS NULL
BEGIN
CREATE TABLE Suppliers(
    SupplierId INT IDENTITY(1,1) PRIMARY KEY,
    SupplierCode NVARCHAR(30) NOT NULL UNIQUE,
    SupplierName NVARCHAR(150) NOT NULL,
    ContactPerson NVARCHAR(150) NULL,
    Mobile NVARCHAR(30) NULL,
    Email NVARCHAR(100) NULL,
    AddressLine NVARCHAR(250) NULL,
    IsActive BIT NOT NULL DEFAULT 1
);
END;

IF OBJECT_ID('Categories') IS NULL
BEGIN
CREATE TABLE Categories(
    CategoryId INT IDENTITY(1,1) PRIMARY KEY,
    CategoryName NVARCHAR(100) NOT NULL UNIQUE
);
END;

IF OBJECT_ID('Brands') IS NULL
BEGIN
CREATE TABLE Brands(
    BrandId INT IDENTITY(1,1) PRIMARY KEY,
    BrandName NVARCHAR(100) NOT NULL UNIQUE
);
END;

IF OBJECT_ID('TaxGroups') IS NULL
BEGIN
CREATE TABLE TaxGroups(
    TaxGroupId INT IDENTITY(1,1) PRIMARY KEY,
    TaxGroupName NVARCHAR(100) NOT NULL,
    TaxPercent DECIMAL(9,2) NOT NULL DEFAULT 0,
    IsInclusive BIT NOT NULL DEFAULT 0
);
END;

IF OBJECT_ID('Products') IS NULL
BEGIN
CREATE TABLE Products(
    ProductId INT IDENTITY(1,1) PRIMARY KEY,
    ProductCode NVARCHAR(30) NOT NULL UNIQUE,
    Barcode NVARCHAR(50) NOT NULL UNIQUE,
    ProductName NVARCHAR(200) NOT NULL,
    CategoryId INT NULL FOREIGN KEY REFERENCES Categories(CategoryId),
    BrandId INT NULL FOREIGN KEY REFERENCES Brands(BrandId),
    UnitOfMeasure NVARCHAR(20) NOT NULL DEFAULT 'PCS',
    PurchasePrice DECIMAL(18,2) NOT NULL DEFAULT 0,
    SalePrice DECIMAL(18,2) NOT NULL DEFAULT 0,
    RetailPrice DECIMAL(18,2) NOT NULL DEFAULT 0,
    TaxGroupId INT NULL FOREIGN KEY REFERENCES TaxGroups(TaxGroupId),
    DiscountAllowed BIT NOT NULL DEFAULT 1,
    MinStockLevel DECIMAL(18,3) NOT NULL DEFAULT 0,
    ReorderLevel DECIMAL(18,3) NOT NULL DEFAULT 0,
    StockOnHand DECIMAL(18,3) NOT NULL DEFAULT 0,
    ImagePath NVARCHAR(500) NULL,
    ProductImage VARBINARY(MAX) NULL,
    IsActive BIT NOT NULL DEFAULT 1,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
END;


IF COL_LENGTH('Products','ProductDiscountPercent') IS NULL
BEGIN
    ALTER TABLE Products ADD ProductDiscountPercent DECIMAL(9,2) NOT NULL CONSTRAINT DF_Products_ProductDiscountPercent DEFAULT 0;
END;

IF OBJECT_ID('PaymentMethods') IS NULL
BEGIN
CREATE TABLE PaymentMethods(
    PaymentMethodId INT IDENTITY(1,1) PRIMARY KEY,
    PaymentMethodName NVARCHAR(50) NOT NULL UNIQUE,
    RequiresReference BIT NOT NULL DEFAULT 0,
    IsActive BIT NOT NULL DEFAULT 1
);
END;

IF OBJECT_ID('Shifts') IS NULL
BEGIN
CREATE TABLE Shifts(
    ShiftId INT IDENTITY(1,1) PRIMARY KEY,
    StoreId INT NOT NULL FOREIGN KEY REFERENCES Stores(StoreId),
    TerminalId INT NOT NULL FOREIGN KEY REFERENCES Terminals(TerminalId),
    UserId INT NOT NULL FOREIGN KEY REFERENCES Users(UserId),
    OpeningCash DECIMAL(18,2) NOT NULL DEFAULT 0,
    ClosingCash DECIMAL(18,2) NULL,
    ExpectedCash DECIMAL(18,2) NULL,
    DifferenceAmount DECIMAL(18,2) NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT 'Open',
    OpenedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    ClosedAt DATETIME2 NULL,
    ClosingRemarks NVARCHAR(250) NULL
);
END;

IF OBJECT_ID('SalesHeader') IS NULL
BEGIN
CREATE TABLE SalesHeader(
    SaleId INT IDENTITY(1,1) PRIMARY KEY,
    InvoiceNo NVARCHAR(30) NOT NULL UNIQUE,
    StoreId INT NOT NULL FOREIGN KEY REFERENCES Stores(StoreId),
    TerminalId INT NOT NULL FOREIGN KEY REFERENCES Terminals(TerminalId),
    ShiftId INT NOT NULL FOREIGN KEY REFERENCES Shifts(ShiftId),
    UserId INT NOT NULL FOREIGN KEY REFERENCES Users(UserId),
    CustomerId INT NOT NULL FOREIGN KEY REFERENCES Customers(CustomerId),
    SaleDate DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    SubTotal DECIMAL(18,2) NOT NULL,
    DiscountAmount DECIMAL(18,2) NOT NULL,
    TaxAmount DECIMAL(18,2) NOT NULL,
    GrandTotal DECIMAL(18,2) NOT NULL,
    PaidAmount DECIMAL(18,2) NOT NULL,
    ChangeAmount DECIMAL(18,2) NOT NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT 'Posted',
    Remarks NVARCHAR(250) NULL
);
END;

IF OBJECT_ID('SalesLines') IS NULL
BEGIN
CREATE TABLE SalesLines(
    SaleLineId INT IDENTITY(1,1) PRIMARY KEY,
    SaleId INT NOT NULL FOREIGN KEY REFERENCES SalesHeader(SaleId),
    ProductId INT NOT NULL FOREIGN KEY REFERENCES Products(ProductId),
    ProductName NVARCHAR(200) NOT NULL,
    Quantity DECIMAL(18,3) NOT NULL,
    UnitPrice DECIMAL(18,2) NOT NULL,
    DiscountPercent DECIMAL(9,2) NOT NULL DEFAULT 0,
    DiscountAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
    TaxPercent DECIMAL(9,2) NOT NULL DEFAULT 0,
    TaxAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
    LineTotal DECIMAL(18,2) NOT NULL,
    UnitCost DECIMAL(18,2) NOT NULL DEFAULT 0
);
END;

IF OBJECT_ID('PaymentLines') IS NULL
BEGIN
CREATE TABLE PaymentLines(
    PaymentLineId INT IDENTITY(1,1) PRIMARY KEY,
    SaleId INT NOT NULL FOREIGN KEY REFERENCES SalesHeader(SaleId),
    PaymentMethodId INT NOT NULL FOREIGN KEY REFERENCES PaymentMethods(PaymentMethodId),
    Amount DECIMAL(18,2) NOT NULL,
    ReferenceNo NVARCHAR(100) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
END;



IF OBJECT_ID('ReturnHeader') IS NULL
BEGIN
CREATE TABLE ReturnHeader(
    ReturnId INT IDENTITY(1,1) PRIMARY KEY,
    ReturnNo NVARCHAR(30) NOT NULL UNIQUE,
    OriginalInvoiceNo NVARCHAR(30) NOT NULL,
    StoreId INT NOT NULL FOREIGN KEY REFERENCES Stores(StoreId),
    TerminalId INT NOT NULL FOREIGN KEY REFERENCES Terminals(TerminalId),
    ShiftId INT NOT NULL FOREIGN KEY REFERENCES Shifts(ShiftId),
    UserId INT NOT NULL FOREIGN KEY REFERENCES Users(UserId),
    ReturnDate DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    RefundAmount DECIMAL(18,2) NOT NULL,
    Reason NVARCHAR(250) NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT 'Posted'
);
END;

IF OBJECT_ID('ReturnLines') IS NULL
BEGIN
CREATE TABLE ReturnLines(
    ReturnLineId INT IDENTITY(1,1) PRIMARY KEY,
    ReturnId INT NOT NULL FOREIGN KEY REFERENCES ReturnHeader(ReturnId),
    SaleLineId INT NOT NULL FOREIGN KEY REFERENCES SalesLines(SaleLineId),
    ProductId INT NOT NULL FOREIGN KEY REFERENCES Products(ProductId),
    ProductName NVARCHAR(200) NOT NULL,
    ReturnQuantity DECIMAL(18,3) NOT NULL,
    UnitPrice DECIMAL(18,2) NOT NULL,
    DiscountAmount DECIMAL(18,2) NOT NULL,
    TaxAmount DECIMAL(18,2) NOT NULL,
    RefundAmount DECIMAL(18,2) NOT NULL,
    UnitCost DECIMAL(18,2) NOT NULL DEFAULT 0
);
END;

IF OBJECT_ID('InventoryLedger') IS NULL
BEGIN
CREATE TABLE InventoryLedger(
    LedgerId INT IDENTITY(1,1) PRIMARY KEY,
    StoreId INT NOT NULL FOREIGN KEY REFERENCES Stores(StoreId),
    ProductId INT NOT NULL FOREIGN KEY REFERENCES Products(ProductId),
    MovementType NVARCHAR(30) NOT NULL,
    SourceDocumentNo NVARCHAR(50) NOT NULL,
    QuantityIn DECIMAL(18,3) NOT NULL DEFAULT 0,
    QuantityOut DECIMAL(18,3) NOT NULL DEFAULT 0,
    UnitCost DECIMAL(18,2) NOT NULL DEFAULT 0,
    Remarks NVARCHAR(250) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CreatedBy INT NULL FOREIGN KEY REFERENCES Users(UserId)
);
END;

IF OBJECT_ID('HoldSalesHeader') IS NULL
BEGIN
CREATE TABLE HoldSalesHeader(
    HoldId INT IDENTITY(1,1) PRIMARY KEY,
    HoldNo NVARCHAR(30) NOT NULL UNIQUE,
    StoreId INT NOT NULL,
    TerminalId INT NOT NULL,
    ShiftId INT NOT NULL,
    UserId INT NOT NULL,
    CustomerId INT NOT NULL,
    SubTotal DECIMAL(18,2) NOT NULL,
    DiscountAmount DECIMAL(18,2) NOT NULL,
    TaxAmount DECIMAL(18,2) NOT NULL,
    GrandTotal DECIMAL(18,2) NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
END;

IF OBJECT_ID('HoldSalesLines') IS NULL
BEGIN
CREATE TABLE HoldSalesLines(
    HoldLineId INT IDENTITY(1,1) PRIMARY KEY,
    HoldId INT NOT NULL FOREIGN KEY REFERENCES HoldSalesHeader(HoldId),
    ProductId INT NOT NULL,
    ProductName NVARCHAR(200) NOT NULL,
    Barcode NVARCHAR(50) NOT NULL,
    Quantity DECIMAL(18,3) NOT NULL,
    UnitPrice DECIMAL(18,2) NOT NULL,
    DiscountPercent DECIMAL(9,2) NOT NULL,
    TaxPercent DECIMAL(9,2) NOT NULL,
    UnitCost DECIMAL(18,2) NOT NULL
);
END;

IF OBJECT_ID('CashDrawerLedger') IS NULL
BEGIN
CREATE TABLE CashDrawerLedger(
    CashLedgerId INT IDENTITY(1,1) PRIMARY KEY,
    ShiftId INT NOT NULL FOREIGN KEY REFERENCES Shifts(ShiftId),
    EntryType NVARCHAR(30) NOT NULL,
    Amount DECIMAL(18,2) NOT NULL,
    ReferenceNo NVARCHAR(50) NULL,
    Remarks NVARCHAR(250) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CreatedBy INT NOT NULL FOREIGN KEY REFERENCES Users(UserId)
);
END;

IF OBJECT_ID('AuditLog') IS NULL
BEGIN
CREATE TABLE AuditLog(
    AuditId INT IDENTITY(1,1) PRIMARY KEY,
    UserId INT NULL,
    ActionName NVARCHAR(100) NOT NULL,
    EntityName NVARCHAR(100) NOT NULL,
    EntityKey NVARCHAR(100) NULL,
    Description NVARCHAR(500) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
END;

IF OBJECT_ID('Vendors') IS NULL
BEGIN
CREATE TABLE Vendors(
    VendorId INT IDENTITY(1,1) PRIMARY KEY,
    VendorCode NVARCHAR(30) NOT NULL UNIQUE,
    VendorName NVARCHAR(150) NOT NULL,
    ContactPerson NVARCHAR(150) NULL,
    Mobile NVARCHAR(30) NULL,
    Email NVARCHAR(100) NULL,
    AddressLine NVARCHAR(250) NULL,
    PaymentTerms NVARCHAR(50) NULL,
    OpeningBalance DECIMAL(18,2) NOT NULL DEFAULT 0,
    CurrentBalance DECIMAL(18,2) NOT NULL DEFAULT 0,
    IsActive BIT NOT NULL DEFAULT 1,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
END;

IF OBJECT_ID('ChartOfAccounts') IS NULL
BEGIN
CREATE TABLE ChartOfAccounts(
    AccountId INT IDENTITY(1,1) PRIMARY KEY,
    AccountNo NVARCHAR(30) NOT NULL UNIQUE,
    AccountName NVARCHAR(150) NOT NULL,
    AccountType NVARCHAR(50) NOT NULL,
    NormalBalance NVARCHAR(10) NOT NULL,
    IsSystem BIT NOT NULL DEFAULT 0,
    IsActive BIT NOT NULL DEFAULT 1,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
END;

IF OBJECT_ID('PurchaseInvoiceHeader') IS NULL
BEGIN
CREATE TABLE PurchaseInvoiceHeader(
    PurchaseInvoiceId INT IDENTITY(1,1) PRIMARY KEY,
    InvoiceNo NVARCHAR(30) NOT NULL UNIQUE,
    VendorId INT NULL FOREIGN KEY REFERENCES Vendors(VendorId),
    InvoiceDate DATE NOT NULL,
    VendorInvoiceNo NVARCHAR(50) NULL,
    StoreId INT NOT NULL FOREIGN KEY REFERENCES Stores(StoreId),
    UserId INT NOT NULL FOREIGN KEY REFERENCES Users(UserId),
    SubTotal DECIMAL(18,2) NOT NULL DEFAULT 0,
    DiscountAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
    TaxAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
    GrandTotal DECIMAL(18,2) NOT NULL DEFAULT 0,
    PaidAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
    BalanceAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
    Status NVARCHAR(20) NOT NULL DEFAULT 'Posted',
    Remarks NVARCHAR(250) NULL,
    PostedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
END;

IF OBJECT_ID('PurchaseInvoiceHeader') IS NOT NULL
   AND EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('PurchaseInvoiceHeader') AND name='VendorId' AND is_nullable=0)
BEGIN
    ALTER TABLE PurchaseInvoiceHeader ALTER COLUMN VendorId INT NULL;
END;

IF OBJECT_ID('PurchaseInvoiceLines') IS NULL
BEGIN
CREATE TABLE PurchaseInvoiceLines(
    PurchaseInvoiceLineId INT IDENTITY(1,1) PRIMARY KEY,
    PurchaseInvoiceId INT NOT NULL FOREIGN KEY REFERENCES PurchaseInvoiceHeader(PurchaseInvoiceId),
    ProductId INT NOT NULL FOREIGN KEY REFERENCES Products(ProductId),
    ProductName NVARCHAR(200) NOT NULL,
    Quantity DECIMAL(18,3) NOT NULL,
    UnitCost DECIMAL(18,2) NOT NULL,
    TaxPercent DECIMAL(9,2) NOT NULL DEFAULT 0,
    TaxAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
    LineTotal DECIMAL(18,2) NOT NULL DEFAULT 0
);
END;

IF OBJECT_ID('VendorLedgerEntries') IS NULL
BEGIN
CREATE TABLE VendorLedgerEntries(
    VendorLedgerEntryId INT IDENTITY(1,1) PRIMARY KEY,
    VendorId INT NOT NULL FOREIGN KEY REFERENCES Vendors(VendorId),
    PostingDate DATE NOT NULL,
    DocumentType NVARCHAR(50) NOT NULL,
    DocumentNo NVARCHAR(50) NOT NULL,
    DebitAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
    CreditAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
    BalanceAfter DECIMAL(18,2) NOT NULL DEFAULT 0,
    Description NVARCHAR(250) NULL,
    SourceId INT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
END;

IF OBJECT_ID('GLEntries') IS NULL
BEGIN
CREATE TABLE GLEntries(
    GLEntryId INT IDENTITY(1,1) PRIMARY KEY,
    AccountId INT NOT NULL FOREIGN KEY REFERENCES ChartOfAccounts(AccountId),
    PostingDate DATE NOT NULL,
    DocumentType NVARCHAR(50) NOT NULL,
    DocumentNo NVARCHAR(50) NOT NULL,
    DebitAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
    CreditAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
    Description NVARCHAR(250) NULL,
    SourceId INT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
END;

IF OBJECT_ID('VendorPayments') IS NULL
BEGIN
CREATE TABLE VendorPayments(
    PaymentId INT IDENTITY(1,1) PRIMARY KEY,
    PaymentNo NVARCHAR(30) NOT NULL UNIQUE,
    VendorId INT NOT NULL FOREIGN KEY REFERENCES Vendors(VendorId),
    PaymentDate DATE NOT NULL,
    Amount DECIMAL(18,2) NOT NULL,
    PaymentMethod NVARCHAR(50) NOT NULL,
    ReferenceNo NVARCHAR(100) NULL,
    Remarks NVARCHAR(250) NULL,
    CreatedBy INT NOT NULL FOREIGN KEY REFERENCES Users(UserId),
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
END;


IF OBJECT_ID('SalesInvoiceHeader') IS NULL
BEGIN
CREATE TABLE SalesInvoiceHeader(
    SalesInvoiceId INT IDENTITY(1,1) PRIMARY KEY,
    InvoiceNo NVARCHAR(30) NOT NULL UNIQUE,
    CustomerId INT NOT NULL FOREIGN KEY REFERENCES Customers(CustomerId),
    InvoiceDate DATE NOT NULL,
    StoreId INT NOT NULL FOREIGN KEY REFERENCES Stores(StoreId),
    UserId INT NOT NULL FOREIGN KEY REFERENCES Users(UserId),
    SubTotal DECIMAL(18,2) NOT NULL DEFAULT 0,
    DiscountAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
    TaxAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
    GrandTotal DECIMAL(18,2) NOT NULL DEFAULT 0,
    PaidAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
    BalanceAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
    Status NVARCHAR(20) NOT NULL DEFAULT 'Posted',
    Remarks NVARCHAR(250) NULL,
    PostedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
END;

IF OBJECT_ID('SalesInvoiceLines') IS NULL
BEGIN
CREATE TABLE SalesInvoiceLines(
    SalesInvoiceLineId INT IDENTITY(1,1) PRIMARY KEY,
    SalesInvoiceId INT NOT NULL FOREIGN KEY REFERENCES SalesInvoiceHeader(SalesInvoiceId),
    ProductId INT NOT NULL FOREIGN KEY REFERENCES Products(ProductId),
    ProductName NVARCHAR(200) NOT NULL,
    Quantity DECIMAL(18,3) NOT NULL,
    UnitPrice DECIMAL(18,2) NOT NULL,
    DiscountPercent DECIMAL(9,2) NOT NULL DEFAULT 0,
    DiscountAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
    TaxPercent DECIMAL(9,2) NOT NULL DEFAULT 0,
    TaxAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
    LineTotal DECIMAL(18,2) NOT NULL DEFAULT 0,
    UnitCost DECIMAL(18,2) NOT NULL DEFAULT 0
);
END;

IF OBJECT_ID('CustomerLedgerEntries') IS NULL
BEGIN
CREATE TABLE CustomerLedgerEntries(
    CustomerLedgerEntryId INT IDENTITY(1,1) PRIMARY KEY,
    CustomerId INT NOT NULL FOREIGN KEY REFERENCES Customers(CustomerId),
    PostingDate DATE NOT NULL,
    DocumentType NVARCHAR(50) NOT NULL,
    DocumentNo NVARCHAR(50) NOT NULL,
    DebitAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
    CreditAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
    BalanceAfter DECIMAL(18,2) NOT NULL DEFAULT 0,
    Description NVARCHAR(250) NULL,
    SourceId INT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
END;

IF OBJECT_ID('CustomerPayments') IS NULL
BEGIN
CREATE TABLE CustomerPayments(
    PaymentId INT IDENTITY(1,1) PRIMARY KEY,
    PaymentNo NVARCHAR(30) NOT NULL UNIQUE,
    CustomerId INT NOT NULL FOREIGN KEY REFERENCES Customers(CustomerId),
    PaymentDate DATE NOT NULL,
    Amount DECIMAL(18,2) NOT NULL,
    PaymentMethod NVARCHAR(50) NOT NULL,
    ReferenceNo NVARCHAR(100) NULL,
    Remarks NVARCHAR(250) NULL,
    CreatedBy INT NOT NULL FOREIGN KEY REFERENCES Users(UserId),
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
END;

GO
-- Cloud tenant upgrade schema from original WPF app
IF OBJECT_ID('PostingSetup') IS NULL
BEGIN
    CREATE TABLE PostingSetup(
        SetupId INT NOT NULL PRIMARY KEY CHECK (SetupId = 1),
        CashAccount NVARCHAR(30) NOT NULL,
        BankAccount NVARCHAR(30) NOT NULL,
        ReceivableAccount NVARCHAR(30) NOT NULL,
        InventoryAccount NVARCHAR(30) NOT NULL,
        InputTaxAccount NVARCHAR(30) NOT NULL,
        PayableAccount NVARCHAR(30) NOT NULL,
        OutputTaxAccount NVARCHAR(30) NOT NULL,
        OpeningBalanceAccount NVARCHAR(30) NOT NULL,
        SalesAccount NVARCHAR(30) NOT NULL,
        SalesReturnAccount NVARCHAR(30) NOT NULL,
        CogsAccount NVARCHAR(30) NOT NULL,
        StockAdjustmentAccount NVARCHAR(30) NOT NULL,
        CashierDiscountLimit DECIMAL(9,2) NOT NULL DEFAULT 5,
        BlockNegativeStock BIT NOT NULL DEFAULT 1,
        CostingMethod NVARCHAR(20) NOT NULL DEFAULT 'Average',
        ModifiedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;

IF OBJECT_ID('NumberSeries') IS NULL
BEGIN
    CREATE TABLE NumberSeries(
        SeriesCode NVARCHAR(30) NOT NULL PRIMARY KEY,
        Prefix NVARCHAR(20) NOT NULL,
        LastNumber BIGINT NOT NULL DEFAULT 0,
        NumberLength INT NOT NULL DEFAULT 6,
        IncludeDate BIT NOT NULL DEFAULT 1
    );
END;

IF OBJECT_ID('AccountingPeriods') IS NULL
BEGIN
    CREATE TABLE AccountingPeriods(
        PeriodId INT IDENTITY(1,1) PRIMARY KEY,
        PeriodName NVARCHAR(50) NOT NULL,
        StartDate DATE NOT NULL,
        EndDate DATE NOT NULL,
        IsClosed BIT NOT NULL DEFAULT 0,
        ClosedAt DATETIME2 NULL,
        ClosedBy INT NULL
    );
END;

IF OBJECT_ID('PostingBatches') IS NULL
BEGIN
    CREATE TABLE PostingBatches(
        PostingBatchId BIGINT IDENTITY(1,1) PRIMARY KEY,
        DocumentType NVARCHAR(50) NOT NULL,
        DocumentNo NVARCHAR(50) NOT NULL,
        PostingDate DATE NOT NULL,
        SourceId INT NULL,
        TotalDebit DECIMAL(18,2) NOT NULL,
        TotalCredit DECIMAL(18,2) NOT NULL,
        ReversedBatchId BIGINT NULL,
        CreatedBy INT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_PostingBatches_Document UNIQUE(DocumentType, DocumentNo)
    );
END;

IF COL_LENGTH('GLEntries','PostingBatchId') IS NULL
    ALTER TABLE GLEntries ADD PostingBatchId BIGINT NULL;
IF COL_LENGTH('SalesLines','TaxInclusive') IS NULL
    ALTER TABLE SalesLines ADD TaxInclusive BIT NOT NULL DEFAULT 0;
IF COL_LENGTH('SalesInvoiceLines','TaxInclusive') IS NULL
    ALTER TABLE SalesInvoiceLines ADD TaxInclusive BIT NOT NULL DEFAULT 0;
IF COL_LENGTH('PurchaseInvoiceLines','TaxInclusive') IS NULL
    ALTER TABLE PurchaseInvoiceLines ADD TaxInclusive BIT NOT NULL DEFAULT 0;
IF COL_LENGTH('HoldSalesLines','TaxInclusive') IS NULL
    ALTER TABLE HoldSalesLines ADD TaxInclusive BIT NOT NULL DEFAULT 0;
IF COL_LENGTH('AuditLog','OldValues') IS NULL
    ALTER TABLE AuditLog ADD OldValues NVARCHAR(MAX) NULL;
IF COL_LENGTH('AuditLog','NewValues') IS NULL
    ALTER TABLE AuditLog ADD NewValues NVARCHAR(MAX) NULL;

IF OBJECT_ID('StockByStore') IS NULL
BEGIN
    CREATE TABLE StockByStore(
        StoreId INT NOT NULL,
        ProductId INT NOT NULL,
        Quantity DECIMAL(18,3) NOT NULL DEFAULT 0,
        AverageCost DECIMAL(18,4) NOT NULL DEFAULT 0,
        PRIMARY KEY(StoreId, ProductId)
    );
END;

IF OBJECT_ID('ItemBatches') IS NULL
BEGIN
    CREATE TABLE ItemBatches(
        ItemBatchId BIGINT IDENTITY(1,1) PRIMARY KEY,
        StoreId INT NOT NULL,
        ProductId INT NOT NULL,
        BatchNo NVARCHAR(50) NOT NULL,
        SerialNo NVARCHAR(100) NULL,
        ExpiryDate DATE NULL,
        Quantity DECIMAL(18,3) NOT NULL DEFAULT 0,
        UnitCost DECIMAL(18,4) NOT NULL DEFAULT 0,
        Status NVARCHAR(20) NOT NULL DEFAULT 'Available',
        CONSTRAINT UQ_ItemBatch UNIQUE(StoreId, ProductId, BatchNo, SerialNo)
    );
END;

IF OBJECT_ID('StockTransfers') IS NULL
BEGIN
    CREATE TABLE StockTransfers(
        TransferId BIGINT IDENTITY(1,1) PRIMARY KEY,
        TransferNo NVARCHAR(40) NOT NULL UNIQUE,
        FromStoreId INT NOT NULL,
        ToStoreId INT NOT NULL,
        TransferDate DATE NOT NULL,
        Status NVARCHAR(20) NOT NULL DEFAULT 'Posted',
        CreatedBy INT NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
    CREATE TABLE StockTransferLines(
        TransferLineId BIGINT IDENTITY(1,1) PRIMARY KEY,
        TransferId BIGINT NOT NULL,
        ProductId INT NOT NULL,
        Quantity DECIMAL(18,3) NOT NULL,
        UnitCost DECIMAL(18,4) NOT NULL
    );
END;

IF OBJECT_ID('StockAdjustments') IS NULL
BEGIN
    CREATE TABLE StockAdjustments(
        AdjustmentId BIGINT IDENTITY(1,1) PRIMARY KEY,
        AdjustmentNo NVARCHAR(40) NOT NULL UNIQUE,
        StoreId INT NOT NULL,
        PostingDate DATE NOT NULL,
        Reason NVARCHAR(250) NULL,
        Status NVARCHAR(20) NOT NULL DEFAULT 'Posted',
        CreatedBy INT NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
    CREATE TABLE StockAdjustmentLines(
        AdjustmentLineId BIGINT IDENTITY(1,1) PRIMARY KEY,
        AdjustmentId BIGINT NOT NULL,
        ProductId INT NOT NULL,
        SystemQuantity DECIMAL(18,3) NOT NULL,
        CountedQuantity DECIMAL(18,3) NOT NULL,
        UnitCost DECIMAL(18,4) NOT NULL,
        Disposition NVARCHAR(30) NOT NULL DEFAULT 'Normal'
    );
END;

IF OBJECT_ID('SalesQuotes') IS NULL
BEGIN
    CREATE TABLE SalesQuotes(
        QuoteId BIGINT IDENTITY(1,1) PRIMARY KEY,
        QuoteNo NVARCHAR(40) NOT NULL UNIQUE,
        CustomerId INT NOT NULL,
        QuoteDate DATE NOT NULL,
        ValidUntil DATE NULL,
        TotalAmount DECIMAL(18,2) NOT NULL,
        Status NVARCHAR(20) NOT NULL DEFAULT 'Open',
        Payload NVARCHAR(MAX) NOT NULL,
        CreatedBy INT NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;

IF OBJECT_ID('SalesOrders') IS NULL
BEGIN
    CREATE TABLE SalesOrders(
        OrderId BIGINT IDENTITY(1,1) PRIMARY KEY,
        OrderNo NVARCHAR(40) NOT NULL UNIQUE,
        CustomerId INT NOT NULL,
        OrderDate DATE NOT NULL,
        TotalAmount DECIMAL(18,2) NOT NULL,
        Status NVARCHAR(20) NOT NULL DEFAULT 'Open',
        Payload NVARCHAR(MAX) NOT NULL,
        CreatedBy INT NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;

IF OBJECT_ID('ApprovalRequests') IS NULL
BEGIN
    CREATE TABLE ApprovalRequests(
        ApprovalRequestId BIGINT IDENTITY(1,1) PRIMARY KEY,
        RequestType NVARCHAR(40) NOT NULL,
        DocumentNo NVARCHAR(50) NOT NULL,
        RequestedBy INT NOT NULL,
        RequestedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        ApprovedBy INT NULL,
        ApprovedAt DATETIME2 NULL,
        Status NVARCHAR(20) NOT NULL DEFAULT 'Pending',
        Remarks NVARCHAR(250) NULL
    );
END;

IF OBJECT_ID('SyncOutbox') IS NULL
BEGIN
    CREATE TABLE SyncOutbox(
        SyncOutboxId BIGINT IDENTITY(1,1) PRIMARY KEY,
        EntityName NVARCHAR(80) NOT NULL,
        EntityKey NVARCHAR(100) NOT NULL,
        Operation NVARCHAR(20) NOT NULL,
        Payload NVARCHAR(MAX) NOT NULL,
        Status NVARCHAR(20) NOT NULL DEFAULT 'Pending',
        RetryCount INT NOT NULL DEFAULT 0,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        SyncedAt DATETIME2 NULL
    );
END;

IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_GLEntries_PostingDate_AccountId')
    CREATE INDEX IX_GLEntries_PostingDate_AccountId ON GLEntries(PostingDate, AccountId);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_InventoryLedger_Product_Store')
    CREATE INDEX IX_InventoryLedger_Product_Store ON InventoryLedger(ProductId, StoreId, CreatedAt);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_CustomerLedger_Aging')
    CREATE INDEX IX_CustomerLedger_Aging ON CustomerLedgerEntries(CustomerId, PostingDate);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_VendorLedger_Aging')
    CREATE INDEX IX_VendorLedger_Aging ON VendorLedgerEntries(VendorId, PostingDate);
GO

GO
-- Final requirement upgrade additions
IF COL_LENGTH('TaxGroups','IsActive') IS NULL
    ALTER TABLE TaxGroups ADD IsActive BIT NOT NULL CONSTRAINT DF_TaxGroups_IsActive DEFAULT 1;
IF COL_LENGTH('StockAdjustmentLines','DifferenceQuantity') IS NULL
    ALTER TABLE StockAdjustmentLines ADD DifferenceQuantity DECIMAL(18,3) NOT NULL CONSTRAINT DF_StockAdjustmentLines_DifferenceQuantity DEFAULT 0;
IF COL_LENGTH('StockAdjustmentLines','BatchNo') IS NULL
    ALTER TABLE StockAdjustmentLines ADD BatchNo NVARCHAR(50) NULL;
IF COL_LENGTH('StockAdjustmentLines','SerialNo') IS NULL
    ALTER TABLE StockAdjustmentLines ADD SerialNo NVARCHAR(100) NULL;
IF COL_LENGTH('StockAdjustmentLines','ExpiryDate') IS NULL
    ALTER TABLE StockAdjustmentLines ADD ExpiryDate DATE NULL;
IF COL_LENGTH('SalesInvoiceHeader','DocumentStatus') IS NULL
    ALTER TABLE SalesInvoiceHeader ADD DocumentStatus NVARCHAR(30) NOT NULL CONSTRAINT DF_SalesInvoiceHeader_DocumentStatus DEFAULT 'Posted';
IF COL_LENGTH('PurchaseInvoiceHeader','DocumentStatus') IS NULL
    ALTER TABLE PurchaseInvoiceHeader ADD DocumentStatus NVARCHAR(30) NOT NULL CONSTRAINT DF_PurchaseInvoiceHeader_DocumentStatus DEFAULT 'Posted';
IF OBJECT_ID('DocumentApplications') IS NULL
BEGIN
    CREATE TABLE DocumentApplications(
        ApplicationId BIGINT IDENTITY(1,1) PRIMARY KEY,
        PartyType NVARCHAR(20) NOT NULL,
        PartyId INT NOT NULL,
        PaymentDocumentNo NVARCHAR(50) NOT NULL,
        InvoiceDocumentNo NVARCHAR(50) NOT NULL,
        AppliedAmount DECIMAL(18,2) NOT NULL,
        AppliedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;
IF OBJECT_ID('SystemSettings') IS NULL
BEGIN
    CREATE TABLE SystemSettings(
        SettingKey NVARCHAR(80) NOT NULL PRIMARY KEY,
        SettingValue NVARCHAR(MAX) NULL,
        UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;
MERGE NumberSeries AS t USING (VALUES('SALES_QUOTE','SQ'),('SALES_ORDER','SO'),('BACKUP','BKP')) s(SeriesCode,Prefix)
ON t.SeriesCode=s.SeriesCode
WHEN NOT MATCHED THEN INSERT(SeriesCode,Prefix,LastNumber,NumberLength,IncludeDate) VALUES(s.SeriesCode,s.Prefix,0,6,1);
GO


-- Professional operations hardening: backup/restore history and richer approvals.
IF OBJECT_ID('DatabaseBackupHistory') IS NULL
BEGIN
CREATE TABLE DatabaseBackupHistory(
    BackupHistoryId BIGINT IDENTITY(1,1) PRIMARY KEY,
    BackupReference NVARCHAR(60) NOT NULL UNIQUE,
    DatabaseName NVARCHAR(128) NOT NULL,
    BackupFilePath NVARCHAR(500) NOT NULL,
    RequestedBy INT NOT NULL,
    RequestedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CompletedAt DATETIME2 NULL,
    Status NVARCHAR(30) NOT NULL DEFAULT 'Requested',
    Remarks NVARCHAR(500) NULL
);
END;

IF OBJECT_ID('DatabaseRestoreHistory') IS NULL
BEGIN
CREATE TABLE DatabaseRestoreHistory(
    RestoreHistoryId BIGINT IDENTITY(1,1) PRIMARY KEY,
    BackupReference NVARCHAR(60) NOT NULL,
    DatabaseName NVARCHAR(128) NOT NULL,
    RequestedBy INT NOT NULL,
    RequestedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CompletedAt DATETIME2 NULL,
    Status NVARCHAR(30) NOT NULL DEFAULT 'Requested',
    Remarks NVARCHAR(500) NULL
);
END;

IF COL_LENGTH('ApprovalRequests','ApprovedAt') IS NULL ALTER TABLE ApprovalRequests ADD ApprovedAt DATETIME2 NULL;
IF COL_LENGTH('ApprovalRequests','ApprovedBy') IS NULL ALTER TABLE ApprovalRequests ADD ApprovedBy INT NULL;
IF COL_LENGTH('ApprovalRequests','Remarks') IS NULL ALTER TABLE ApprovalRequests ADD Remarks NVARCHAR(250) NULL;
GO

GO
-- Multi-branch / branch-context upgrade.
-- Stores are used as branches so inventory, users, shifts and documents keep one consistent branch dimension.
IF OBJECT_ID('Stores') IS NOT NULL AND COL_LENGTH('Stores','BranchCode') IS NULL ALTER TABLE Stores ADD BranchCode NVARCHAR(30) NULL;
IF OBJECT_ID('Stores') IS NOT NULL AND COL_LENGTH('Stores','BranchName') IS NULL ALTER TABLE Stores ADD BranchName NVARCHAR(100) NULL;
IF OBJECT_ID('Stores') IS NOT NULL AND COL_LENGTH('Stores','IsMainBranch') IS NULL ALTER TABLE Stores ADD IsMainBranch BIT NOT NULL CONSTRAINT DF_Stores_IsMainBranch DEFAULT 0;
IF OBJECT_ID('Shifts') IS NOT NULL AND COL_LENGTH('Shifts','BranchCode') IS NULL ALTER TABLE Shifts ADD BranchCode NVARCHAR(30) NULL;
IF OBJECT_ID('SalesHeader') IS NOT NULL AND COL_LENGTH('SalesHeader','BranchCode') IS NULL ALTER TABLE SalesHeader ADD BranchCode NVARCHAR(30) NULL;
IF OBJECT_ID('SalesInvoiceHeader') IS NOT NULL AND COL_LENGTH('SalesInvoiceHeader','BranchCode') IS NULL ALTER TABLE SalesInvoiceHeader ADD BranchCode NVARCHAR(30) NULL;
IF OBJECT_ID('PurchaseInvoiceHeader') IS NOT NULL AND COL_LENGTH('PurchaseInvoiceHeader','BranchCode') IS NULL ALTER TABLE PurchaseInvoiceHeader ADD BranchCode NVARCHAR(30) NULL;
IF OBJECT_ID('InventoryLedger') IS NOT NULL AND COL_LENGTH('InventoryLedger','BranchCode') IS NULL ALTER TABLE InventoryLedger ADD BranchCode NVARCHAR(30) NULL;
IF OBJECT_ID('CustomerPayments') IS NOT NULL AND COL_LENGTH('CustomerPayments','BranchCode') IS NULL ALTER TABLE CustomerPayments ADD BranchCode NVARCHAR(30) NULL;
IF OBJECT_ID('VendorPayments') IS NOT NULL AND COL_LENGTH('VendorPayments','BranchCode') IS NULL ALTER TABLE VendorPayments ADD BranchCode NVARCHAR(30) NULL;
IF OBJECT_ID('GLEntries') IS NOT NULL AND COL_LENGTH('GLEntries','BranchCode') IS NULL ALTER TABLE GLEntries ADD BranchCode NVARCHAR(30) NULL;
IF OBJECT_ID('StockAdjustments') IS NOT NULL AND COL_LENGTH('StockAdjustments','BranchCode') IS NULL ALTER TABLE StockAdjustments ADD BranchCode NVARCHAR(30) NULL;
IF OBJECT_ID('StockTransfers') IS NOT NULL AND COL_LENGTH('StockTransfers','FromBranchCode') IS NULL ALTER TABLE StockTransfers ADD FromBranchCode NVARCHAR(30) NULL;
IF OBJECT_ID('StockTransfers') IS NOT NULL AND COL_LENGTH('StockTransfers','ToBranchCode') IS NULL ALTER TABLE StockTransfers ADD ToBranchCode NVARCHAR(30) NULL;
IF OBJECT_ID('HoldSalesHeader') IS NOT NULL AND COL_LENGTH('HoldSalesHeader','BranchCode') IS NULL ALTER TABLE HoldSalesHeader ADD BranchCode NVARCHAR(30) NULL;
IF OBJECT_ID('ReturnHeader') IS NOT NULL AND COL_LENGTH('ReturnHeader','BranchCode') IS NULL ALTER TABLE ReturnHeader ADD BranchCode NVARCHAR(30) NULL;
GO

IF OBJECT_ID('Stores') IS NOT NULL EXEC('UPDATE Stores SET BranchCode=StoreCode WHERE BranchCode IS NULL OR BranchCode=''''');
IF OBJECT_ID('Stores') IS NOT NULL EXEC('UPDATE Stores SET BranchName=StoreName WHERE BranchName IS NULL OR BranchName=''''');
IF OBJECT_ID('Stores') IS NOT NULL EXEC('UPDATE Stores SET IsMainBranch=1 WHERE StoreCode=''MAIN'' OR StoreId=(SELECT MIN(StoreId) FROM Stores)');
IF OBJECT_ID('SalesHeader') IS NOT NULL EXEC('UPDATE h SET BranchCode=s.StoreCode FROM SalesHeader h INNER JOIN Stores s ON s.StoreId=h.StoreId WHERE h.BranchCode IS NULL');
IF OBJECT_ID('SalesInvoiceHeader') IS NOT NULL EXEC('UPDATE h SET BranchCode=s.StoreCode FROM SalesInvoiceHeader h INNER JOIN Stores s ON s.StoreId=h.StoreId WHERE h.BranchCode IS NULL');
IF OBJECT_ID('PurchaseInvoiceHeader') IS NOT NULL EXEC('UPDATE h SET BranchCode=s.StoreCode FROM PurchaseInvoiceHeader h INNER JOIN Stores s ON s.StoreId=h.StoreId WHERE h.BranchCode IS NULL');
IF OBJECT_ID('InventoryLedger') IS NOT NULL EXEC('UPDATE l SET BranchCode=s.StoreCode FROM InventoryLedger l INNER JOIN Stores s ON s.StoreId=l.StoreId WHERE l.BranchCode IS NULL');
IF OBJECT_ID('Shifts') IS NOT NULL EXEC('UPDATE sh SET BranchCode=s.StoreCode FROM Shifts sh INNER JOIN Stores s ON s.StoreId=sh.StoreId WHERE sh.BranchCode IS NULL');
IF OBJECT_ID('ReturnHeader') IS NOT NULL EXEC('UPDATE rh SET BranchCode=s.StoreCode FROM ReturnHeader rh INNER JOIN Stores s ON s.StoreId=rh.StoreId WHERE rh.BranchCode IS NULL');
IF OBJECT_ID('StockAdjustments') IS NOT NULL EXEC('UPDATE a SET BranchCode=s.StoreCode FROM StockAdjustments a INNER JOIN Stores s ON s.StoreId=a.StoreId WHERE a.BranchCode IS NULL');
IF OBJECT_ID('StockTransfers') IS NOT NULL EXEC('UPDATE t SET FromBranchCode=fs.StoreCode, ToBranchCode=ts.StoreCode FROM StockTransfers t INNER JOIN Stores fs ON fs.StoreId=t.FromStoreId INNER JOIN Stores ts ON ts.StoreId=t.ToStoreId WHERE t.FromBranchCode IS NULL OR t.ToBranchCode IS NULL');
GO

-- Package Configuration: Business Central-style master-data migration packages.
IF OBJECT_ID('ConfigurationPackages') IS NULL
BEGIN
CREATE TABLE ConfigurationPackages(
    PackageId BIGINT IDENTITY(1,1) PRIMARY KEY,
    PackageCode NVARCHAR(30) NOT NULL UNIQUE,
    PackageName NVARCHAR(120) NOT NULL,
    Description NVARCHAR(500) NULL,
    IncludeItems BIT NOT NULL DEFAULT 1,
    IncludeCustomers BIT NOT NULL DEFAULT 1,
    IncludeVendors BIT NOT NULL DEFAULT 1,
    IncludeBalances BIT NOT NULL DEFAULT 1,
    IsActive BIT NOT NULL DEFAULT 1,
    CreatedBy INT NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    ModifiedBy INT NULL,
    ModifiedAt DATETIME2 NULL
);
END;

IF OBJECT_ID('ConfigurationPackageImports') IS NULL
BEGIN
CREATE TABLE ConfigurationPackageImports(
    ImportId BIGINT IDENTITY(1,1) PRIMARY KEY,
    PackageId BIGINT NOT NULL,
    FileName NVARCHAR(260) NOT NULL,
    WorkbookJson NVARCHAR(MAX) NOT NULL,
    ValidationIssuesJson NVARCHAR(MAX) NULL,
    ItemRows INT NOT NULL DEFAULT 0,
    CustomerRows INT NOT NULL DEFAULT 0,
    VendorRows INT NOT NULL DEFAULT 0,
    ValidRows INT NOT NULL DEFAULT 0,
    ErrorRows INT NOT NULL DEFAULT 0,
    Status NVARCHAR(30) NOT NULL DEFAULT 'Validated',
    ImportedBy INT NOT NULL,
    ImportedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    AppliedBy INT NULL,
    AppliedAt DATETIME2 NULL,
    ResultJson NVARCHAR(MAX) NULL
);
CREATE INDEX IX_ConfigurationPackageImports_PackageId ON ConfigurationPackageImports(PackageId, ImportId DESC);
END;
GO

-- Lightweight Expense Management module.
IF OBJECT_ID('ExpenseCategories') IS NULL
BEGIN
CREATE TABLE ExpenseCategories(
    ExpenseCategoryId INT IDENTITY(1,1) PRIMARY KEY,
    CategoryCode NVARCHAR(20) NOT NULL UNIQUE,
    CategoryName NVARCHAR(100) NOT NULL,
    IsActive BIT NOT NULL DEFAULT 1,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt DATETIME2 NULL
);
END;

IF OBJECT_ID('Expenses') IS NULL
BEGIN
CREATE TABLE Expenses(
    ExpenseId BIGINT IDENTITY(1,1) PRIMARY KEY,
    ExpenseNo NVARCHAR(40) NOT NULL UNIQUE,
    ExpenseDate DATE NOT NULL,
    ExpenseCategoryId INT NOT NULL,
    Description NVARCHAR(250) NOT NULL,
    Amount DECIMAL(18,2) NOT NULL,
    StoreId INT NOT NULL,
    BranchCode NVARCHAR(30) NULL,
    CreatedBy INT NOT NULL,
    Remarks NVARCHAR(500) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedBy INT NULL,
    UpdatedAt DATETIME2 NULL,
    IsDeleted BIT NOT NULL DEFAULT 0,
    DeletedBy INT NULL,
    DeletedAt DATETIME2 NULL
);
CREATE INDEX IX_Expenses_Date_Branch_Category ON Expenses(ExpenseDate,StoreId,ExpenseCategoryId) INCLUDE(Amount,IsDeleted);
END;

MERGE NumberSeries AS t USING(VALUES('EXPENSE','EXP')) s(SeriesCode,Prefix)
ON t.SeriesCode=s.SeriesCode
WHEN NOT MATCHED THEN INSERT(SeriesCode,Prefix,LastNumber,NumberLength,IncludeDate) VALUES(s.SeriesCode,s.Prefix,0,6,1);

MERGE ExpenseCategories AS t USING(VALUES
 ('GENERAL','General Expense'),('TRAVEL','Travel & Conveyance'),('UTILITIES','Utilities'),('RENT','Rent'),
 ('MARKETING','Marketing'),('MAINTENANCE','Repairs & Maintenance'),('OFFICE','Office Supplies')
) s(CategoryCode,CategoryName) ON t.CategoryCode=s.CategoryCode
WHEN NOT MATCHED THEN INSERT(CategoryCode,CategoryName,IsActive) VALUES(s.CategoryCode,s.CategoryName,1);
GO

-- Day Closing: one summary row per branch per business date (no duplicate invoice storage).
IF OBJECT_ID('DayClosings') IS NULL
BEGIN
CREATE TABLE DayClosings(
    DayClosingId BIGINT IDENTITY(1,1) PRIMARY KEY,
    StoreId INT NOT NULL,
    BranchCode NVARCHAR(30) NULL,
    BusinessDate DATE NOT NULL,
    ClosedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    ClosedByUserId INT NOT NULL,
    ClosedByName NVARCHAR(150) NULL,
    Remarks NVARCHAR(500) NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT 'Closed',
    ShiftCount INT NOT NULL DEFAULT 0,
    InvoiceCount INT NOT NULL DEFAULT 0,
    ReturnCount INT NOT NULL DEFAULT 0,
    TotalSales DECIMAL(18,2) NOT NULL DEFAULT 0,
    TotalTax DECIMAL(18,2) NOT NULL DEFAULT 0,
    TotalDiscount DECIMAL(18,2) NOT NULL DEFAULT 0,
    TotalCost DECIMAL(18,2) NOT NULL DEFAULT 0,
    TotalProfit DECIMAL(18,2) NOT NULL DEFAULT 0,
    TotalRefunds DECIMAL(18,2) NOT NULL DEFAULT 0,
    CashSales DECIMAL(18,2) NOT NULL DEFAULT 0,
    NonCashSales DECIMAL(18,2) NOT NULL DEFAULT 0,
    CONSTRAINT UQ_DayClosings_StoreDate UNIQUE(StoreId, BusinessDate)
);
END;
GO
