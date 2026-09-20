IF OBJECT_ID('dbo.CompanyInformation', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CompanyInformation(
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
GO

IF NOT EXISTS (SELECT 1 FROM dbo.CompanyInformation)
BEGIN
    INSERT INTO dbo.CompanyInformation(CompanyName, AddressLine, PhoneNo, Email, Website, TaxRegistrationNo, LogoPath)
    VALUES('PayNex_POS_B1', 'Main Store / Branch Address', '', '', '', '', '');
END;
GO

-- Optional: update company info manually from SQL Server Management Studio.
-- UPDATE dbo.CompanyInformation
-- SET CompanyName='Your Company Name',
--     AddressLine='Your full company address',
--     PhoneNo='0300-0000000',
--     Email='info@company.com',
--     Website='www.company.com',
--     TaxRegistrationNo='NTN / VAT No',
--     LogoPath='C:\PayNex\Logo.png';

-- Optional: save logo picture into SQL varbinary.
-- UPDATE dbo.CompanyInformation
-- SET LogoImage = (SELECT BulkColumn FROM OPENROWSET(BULK N'C:\PayNex\Logo.png', SINGLE_BLOB) AS LogoFile);
