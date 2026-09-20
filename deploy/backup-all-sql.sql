-- PayNex production backup. Run in SSMS as a SQL login that can BACKUP DATABASE.
-- Stores files on D:\SqlBackups — never inside C:\PayNex\app.

IF NOT EXISTS (SELECT 1 FROM sys.master_files WHERE physical_name LIKE 'D:\SqlBackups\%')
BEGIN
    EXEC xp_create_subdir 'D:\SqlBackups';
END
GO

DECLARE @stamp nvarchar(20) = CONVERT(varchar(8), GETDATE(), 112) + '_' + REPLACE(CONVERT(varchar(8), GETDATE(), 108), ':', '');
DECLARE @name sysname, @sql nvarchar(max);

DECLARE dbs CURSOR LOCAL FAST_FORWARD FOR
SELECT name
FROM sys.databases
WHERE name = 'PayNex_MasterDB'
   OR name LIKE 'PayNex\_%\_DB' ESCAPE '\'
   OR name LIKE 'PayNex\_%\_SBX_DB' ESCAPE '\';

OPEN dbs;
FETCH NEXT FROM dbs INTO @name;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'BACKUP DATABASE ' + QUOTENAME(@name) +
        N' TO DISK = N''D:\SqlBackups\' + @name + N'_' + @stamp + N'.bak'' WITH INIT, CHECKSUM, COPY_ONLY;';
    EXEC sp_executesql @sql;
    FETCH NEXT FROM dbs INTO @name;
END
CLOSE dbs;
DEALLOCATE dbs;
GO
