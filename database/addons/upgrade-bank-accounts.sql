IF OBJECT_ID('BankAccounts') IS NULL
BEGIN
    RETURN;
END;

IF OBJECT_ID('BankAccounts') IS NOT NULL AND COL_LENGTH('BankAccounts','AccountNumber') IS NULL
    ALTER TABLE BankAccounts ADD AccountNumber NVARCHAR(50) NULL;
IF COL_LENGTH('BankAccounts','IBAN') IS NULL
    ALTER TABLE BankAccounts ADD IBAN NVARCHAR(50) NULL;
IF COL_LENGTH('BankAccounts','BranchName') IS NULL
    ALTER TABLE BankAccounts ADD BranchName NVARCHAR(150) NULL;
IF COL_LENGTH('BankAccounts','Currency') IS NULL
    ALTER TABLE BankAccounts ADD Currency NVARCHAR(10) NOT NULL CONSTRAINT DF_BankAccounts_Currency DEFAULT 'PKR';
IF COL_LENGTH('BankAccounts','BankType') IS NULL
    ALTER TABLE BankAccounts ADD BankType NVARCHAR(20) NOT NULL CONSTRAINT DF_BankAccounts_BankType DEFAULT 'Bank';
IF COL_LENGTH('BankAccounts','GLAccountId') IS NULL
    ALTER TABLE BankAccounts ADD GLAccountId INT NULL;
IF COL_LENGTH('BankAccounts','CreatedAt') IS NULL
    ALTER TABLE BankAccounts ADD CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_BankAccounts_CreatedAt DEFAULT SYSUTCDATETIME();
IF COL_LENGTH('BankAccounts','UpdatedAt') IS NULL
    ALTER TABLE BankAccounts ADD UpdatedAt DATETIME2 NULL;
GO
IF OBJECT_ID('BankAccounts') IS NOT NULL AND COL_LENGTH('BankAccounts','GLAccountId') IS NOT NULL AND COL_LENGTH('BankAccounts','AccountNo') IS NOT NULL
    EXEC('UPDATE b SET b.GLAccountId=a.AccountId FROM BankAccounts b INNER JOIN ChartOfAccounts a ON a.AccountNo=b.AccountNo WHERE b.GLAccountId IS NULL');
IF OBJECT_ID('BankAccounts') IS NOT NULL AND COL_LENGTH('BankAccounts','GLAccountId') IS NOT NULL
    EXEC('UPDATE BankAccounts SET GLAccountId=(SELECT TOP 1 AccountId FROM ChartOfAccounts WHERE IsActive=1 AND (AccountNo=''1010'' OR AccountName LIKE ''%Bank%'') ORDER BY CASE WHEN AccountNo=''1010'' THEN 0 ELSE 1 END, AccountId) WHERE GLAccountId IS NULL');
IF OBJECT_ID('BankAccounts') IS NOT NULL AND COL_LENGTH('BankAccounts','GLAccountId') IS NOT NULL
    EXEC('UPDATE BankAccounts SET GLAccountId=(SELECT TOP 1 AccountId FROM ChartOfAccounts ORDER BY AccountId) WHERE GLAccountId IS NULL');
IF OBJECT_ID('BankAccounts') IS NOT NULL AND COL_LENGTH('BankAccounts','AccountNumber') IS NOT NULL AND COL_LENGTH('BankAccounts','AccountNo') IS NOT NULL
    EXEC('UPDATE b SET AccountNumber=b.AccountNo FROM BankAccounts b WHERE ISNULL(b.AccountNumber,'''')='''' AND NOT EXISTS (SELECT 1 FROM ChartOfAccounts a WHERE a.AccountNo=b.AccountNo)');
IF OBJECT_ID('BankAccounts') IS NOT NULL AND COL_LENGTH('BankAccounts','GLAccountId') IS NOT NULL
AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('BankAccounts') AND name='GLAccountId' AND is_nullable=1)
AND NOT EXISTS (SELECT 1 FROM BankAccounts WHERE GLAccountId IS NULL)
    ALTER TABLE BankAccounts ALTER COLUMN GLAccountId INT NOT NULL;
IF OBJECT_ID('BankAccounts') IS NOT NULL AND COL_LENGTH('BankAccounts','GLAccountId') IS NOT NULL
AND NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys fk
    INNER JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id=fk.object_id
    INNER JOIN sys.columns c ON c.object_id=fkc.parent_object_id AND c.column_id=fkc.parent_column_id
    WHERE fk.parent_object_id=OBJECT_ID('BankAccounts') AND c.name='GLAccountId')
    ALTER TABLE BankAccounts ADD CONSTRAINT FK_BankAccounts_ChartOfAccounts FOREIGN KEY (GLAccountId) REFERENCES ChartOfAccounts(AccountId);
IF OBJECT_ID('BankAccounts') IS NOT NULL AND COL_LENGTH('BankAccounts','AccountNo') IS NOT NULL
AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('BankAccounts') AND name='AccountNo' AND is_nullable=0)
    ALTER TABLE BankAccounts ALTER COLUMN AccountNo NVARCHAR(50) NULL;
IF OBJECT_ID('BankAccounts') IS NOT NULL AND COL_LENGTH('BankAccounts','AccountNo') IS NOT NULL
AND NOT EXISTS (SELECT 1 FROM sys.default_constraints dc INNER JOIN sys.columns c ON c.default_object_id=dc.object_id WHERE dc.parent_object_id=OBJECT_ID('BankAccounts') AND c.name='AccountNo')
    ALTER TABLE BankAccounts ADD CONSTRAINT DF_BankAccounts_AccountNo DEFAULT '' FOR AccountNo;
GO
