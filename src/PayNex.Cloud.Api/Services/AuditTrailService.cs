using Microsoft.Data.SqlClient;
using PayNex.Cloud.Api.Data;
using PayNex.Cloud.Api.Models;
using System.Text.Json;

namespace PayNex.Cloud.Api.Services;

public sealed class AuditTrailService
{
    private readonly ConnectionFactory _db;
    public AuditTrailService(ConnectionFactory db) => _db = db;

    public async Task WriteAsync(UserSession user, string actionName, string entityName, string? entityKey, string? description, object? newValues = null)
    {
        await using var con = await _db.OpenTenantAsync(user.DatabaseName);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
INSERT INTO AuditLog(UserId,ActionName,EntityName,EntityKey,Description,NewValues)
VALUES(@UserId,@ActionName,@EntityName,@EntityKey,@Description,@NewValues)";
        cmd.Parameters.AddWithValue("@UserId", user.UserId);
        cmd.Parameters.AddWithValue("@ActionName", actionName);
        cmd.Parameters.AddWithValue("@EntityName", entityName);
        cmd.Parameters.AddWithValue("@EntityKey", (object?)entityKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Description", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@NewValues", newValues == null ? DBNull.Value : JsonSerializer.Serialize(newValues));
        await cmd.ExecuteNonQueryAsync();
    }
}
