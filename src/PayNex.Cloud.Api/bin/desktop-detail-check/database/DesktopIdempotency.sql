-- Safe additive migration for PayNex tenant databases.
-- Enables exactly-once desktop posting via ExternalClientDocumentId.

IF COL_LENGTH('dbo.SalesHeader','ExternalClientDocumentId') IS NULL
    ALTER TABLE dbo.SalesHeader ADD ExternalClientDocumentId NVARCHAR(80) NULL;

IF COL_LENGTH('dbo.SalesInvoiceHeader','ExternalClientDocumentId') IS NULL
    ALTER TABLE dbo.SalesInvoiceHeader ADD ExternalClientDocumentId NVARCHAR(80) NULL;

IF COL_LENGTH('dbo.SalesHeader','ExternalClientDocumentId') IS NOT NULL
AND NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'UX_SalesHeader_ExternalClientDocumentId'
      AND object_id = OBJECT_ID('dbo.SalesHeader'))
    CREATE UNIQUE INDEX UX_SalesHeader_ExternalClientDocumentId
        ON dbo.SalesHeader(ExternalClientDocumentId)
        WHERE ExternalClientDocumentId IS NOT NULL;

IF COL_LENGTH('dbo.SalesInvoiceHeader','ExternalClientDocumentId') IS NOT NULL
AND NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'UX_SalesInvoiceHeader_ExternalClientDocumentId'
      AND object_id = OBJECT_ID('dbo.SalesInvoiceHeader'))
    CREATE UNIQUE INDEX UX_SalesInvoiceHeader_ExternalClientDocumentId
        ON dbo.SalesInvoiceHeader(ExternalClientDocumentId)
        WHERE ExternalClientDocumentId IS NOT NULL;
