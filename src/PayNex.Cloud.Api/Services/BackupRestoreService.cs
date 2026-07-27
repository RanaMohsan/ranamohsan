using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PayNex.Cloud.Api.Data;
using PayNex.Cloud.Api.Models;

namespace PayNex.Cloud.Api.Services;

public sealed class BackupRestoreService
{
    private readonly ConnectionFactory _db;
    private readonly PayNexOptions _options;

    public BackupRestoreService(ConnectionFactory db, IOptions<PayNexOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    public async Task<object> BackupTenantDatabaseAsync(UserSession user, string? remarks)
    {
        var reference = $"BKP-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var folder = Path.IsPathRooted(_options.BackupFolder)
            ? _options.BackupFolder
            : Path.Combine(AppContext.BaseDirectory, _options.BackupFolder);
        Directory.CreateDirectory(folder);
        var filePath = Path.Combine(folder, $"{user.DatabaseName}_{reference}.bak");

        await using var con = await _db.OpenTenantAsync(user.DatabaseName);
        await EnsureOperationalTablesAsync(con);
        await using (var hist = con.CreateCommand())
        {
            hist.CommandText = @"
INSERT INTO DatabaseBackupHistory(BackupReference,DatabaseName,BackupFilePath,RequestedBy,Status,Remarks)
VALUES(@Reference,@DatabaseName,@FilePath,@RequestedBy,'Running',@Remarks)";
            hist.Parameters.AddWithValue("@Reference", reference);
            hist.Parameters.AddWithValue("@DatabaseName", user.DatabaseName);
            hist.Parameters.AddWithValue("@FilePath", filePath);
            hist.Parameters.AddWithValue("@RequestedBy", user.UserId);
            hist.Parameters.AddWithValue("@Remarks", remarks ?? string.Empty);
            await hist.ExecuteNonQueryAsync();
        }

        if (_options.EnablePhysicalSqlBackup)
        {
            await using var server = await _db.OpenMasterServerAsync();
            await using var cmd = server.CreateCommand();
            cmd.CommandTimeout = 600;
            cmd.CommandText = $"BACKUP DATABASE [{user.DatabaseName.Replace("]", "]]")}] TO DISK=@FilePath WITH INIT, COPY_ONLY, CHECKSUM";
            cmd.Parameters.AddWithValue("@FilePath", filePath);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var done = con.CreateCommand())
        {
            done.CommandText = "UPDATE DatabaseBackupHistory SET Status='Completed',CompletedAt=SYSUTCDATETIME() WHERE BackupReference=@Reference";
            done.Parameters.AddWithValue("@Reference", reference);
            await done.ExecuteNonQueryAsync();
        }

        return new { backupReference = reference, databaseName = user.DatabaseName, backupFilePath = filePath, physicalBackupExecuted = _options.EnablePhysicalSqlBackup, message = "Database backup completed." };
    }

    public async Task<object> RestoreTenantDatabaseAsync(UserSession user, string backupReference, string? remarks)
    {
        if (string.IsNullOrWhiteSpace(backupReference)) throw new InvalidOperationException("Backup reference is required.");
        await using var tenantCon = await _db.OpenTenantAsync(user.DatabaseName);
        await EnsureOperationalTablesAsync(tenantCon);
        string? backupPath;
        await using (var lookup = tenantCon.CreateCommand())
        {
            lookup.CommandText = "SELECT TOP 1 BackupFilePath FROM DatabaseBackupHistory WHERE BackupReference=@Reference AND Status='Completed' ORDER BY BackupHistoryId DESC";
            lookup.Parameters.AddWithValue("@Reference", backupReference.Trim());
            backupPath = Convert.ToString(await lookup.ExecuteScalarAsync());
        }
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath)) throw new InvalidOperationException("Backup file was not found on server storage.");

        await using (var hist = tenantCon.CreateCommand())
        {
            hist.CommandText = @"
INSERT INTO DatabaseRestoreHistory(BackupReference,DatabaseName,RequestedBy,Status,Remarks)
VALUES(@Reference,@DatabaseName,@RequestedBy,'Running',@Remarks)";
            hist.Parameters.AddWithValue("@Reference", backupReference.Trim());
            hist.Parameters.AddWithValue("@DatabaseName", user.DatabaseName);
            hist.Parameters.AddWithValue("@RequestedBy", user.UserId);
            hist.Parameters.AddWithValue("@Remarks", remarks ?? string.Empty);
            await hist.ExecuteNonQueryAsync();
        }

        await tenantCon.CloseAsync();
        await using var server = await _db.OpenMasterServerAsync();
        await using var cmd = server.CreateCommand();
        cmd.CommandTimeout = 900;
        var dbName = user.DatabaseName.Replace("]", "]]");
        cmd.CommandText = $@"
ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
RESTORE DATABASE [{dbName}] FROM DISK=@FilePath WITH REPLACE, RECOVERY;
ALTER DATABASE [{dbName}] SET MULTI_USER;";
        cmd.Parameters.AddWithValue("@FilePath", backupPath);
        await cmd.ExecuteNonQueryAsync();

        await using var con2 = await _db.OpenTenantAsync(user.DatabaseName);
        await using var done = con2.CreateCommand();
        done.CommandText = "UPDATE DatabaseRestoreHistory SET Status='Completed',CompletedAt=SYSUTCDATETIME() WHERE BackupReference=@Reference AND RequestedBy=@UserId";
        done.Parameters.AddWithValue("@Reference", backupReference.Trim());
        done.Parameters.AddWithValue("@UserId", user.UserId);
        await done.ExecuteNonQueryAsync();

        return new { backupReference = backupReference.Trim(), databaseName = user.DatabaseName, message = "Database restored successfully." };
    }
    private static async Task EnsureOperationalTablesAsync(SqlConnection con)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
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
END;";
        await cmd.ExecuteNonQueryAsync();
    }

}
