using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PayNex.Cloud.Api.Models;
using System.Data;

namespace PayNex.Cloud.Api.Data;

public sealed class ConnectionFactory
{
    private readonly PayNexOptions _options;
    public ConnectionFactory(IOptions<PayNexOptions> options) => _options = options.Value;
    public PayNexOptions Options => _options;

    public async Task<SqlConnection> OpenMasterServerAsync()
    {
        var con = new SqlConnection(_options.MasterServerConnection);
        await con.OpenAsync();
        return con;
    }

    public async Task<SqlConnection> OpenMasterAsync()
    {
        var con = new SqlConnection(_options.MasterDbConnection);
        await con.OpenAsync();
        return con;
    }

    public async Task<SqlConnection> OpenTenantAsync(string databaseName)
    {
        if (!IsSafeDatabaseName(databaseName)) throw new InvalidOperationException("Unsafe tenant database name.");
        var con = new SqlConnection(_options.TenantConnectionTemplate.Replace("{database}", databaseName));
        await con.OpenAsync();
        return con;
    }

    public async Task<TenantInfo?> GetTenantByCodeAsync(string companyCode)
    {
        await using var con = await OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT TOP 1 TenantId,CompanyCode,CompanyName,Slug,DatabaseName,Status,SubscriptionPlan,ExpiryDate,
       LicenseStatus,DatabaseCreationStatus,ProvisioningStatus,
       ISNULL(OwnerName,'') OwnerName,ISNULL(OwnerEmail,'') OwnerEmail,ISNULL(OwnerMobile,'') OwnerMobile,
       TrialStartDate,TrialEndDate,RenewalDate,CreatedAt,CompanyStartDate,LicenseExpiryDate,
       ISNULL(AllowSandbox,0) AllowSandbox,ISNULL(ProductionDatabaseName,DatabaseName) ProductionDatabaseName,
       ISNULL(SandboxDatabaseName,'') SandboxDatabaseName,SandboxCreatedAt,ISNULL(ActiveEnvironment,'Production') ActiveEnvironment,ISNULL(AllowMultipleBranches,0) AllowMultipleBranches,ISNULL(MaxBranches,1) MaxBranches,ISNULL(MaxCounters,2) MaxCounters
FROM Tenants WHERE CompanyCode=@Code";
        cmd.Parameters.AddWithValue("@Code", NormalizeCode(companyCode));
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new TenantInfo(r.GetGuid(r.GetOrdinal("TenantId")), SqlRead.String(r,"CompanyCode"), SqlRead.String(r,"CompanyName"), SqlRead.String(r,"Slug"), SqlRead.String(r,"DatabaseName"), SqlRead.String(r,"Status"), SqlRead.String(r,"SubscriptionPlan"), SqlRead.NullableDateTime(r,"ExpiryDate"), SqlRead.String(r,"LicenseStatus"), SqlRead.String(r,"DatabaseCreationStatus"), SqlRead.String(r,"ProvisioningStatus"), SqlRead.String(r,"OwnerName"), SqlRead.String(r,"OwnerEmail"), SqlRead.String(r,"OwnerMobile"), SqlRead.NullableDateTime(r,"TrialStartDate"), SqlRead.NullableDateTime(r,"TrialEndDate"), SqlRead.NullableDateTime(r,"RenewalDate"), SqlRead.NullableDateTime(r,"CreatedAt"), SqlRead.NullableDateTime(r,"CompanyStartDate"), SqlRead.NullableDateTime(r,"LicenseExpiryDate"), SqlRead.Bool(r,"AllowSandbox"), SqlRead.String(r,"ProductionDatabaseName"), SqlRead.String(r,"SandboxDatabaseName"), SqlRead.NullableDateTime(r,"SandboxCreatedAt"), SqlRead.String(r,"ActiveEnvironment"), SqlRead.Bool(r,"AllowMultipleBranches"), SqlRead.Int(r,"MaxBranches"), SqlRead.Int(r,"MaxCounters"));
    }

    public static string NormalizeCode(string value)
    {
        var s = new string((value ?? string.Empty).Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(s)) throw new InvalidOperationException("Company code is required.");
        if (s.Length > 40) s = s[..40];
        return s;
    }

    public static bool IsSafeDatabaseName(string dbName) => !string.IsNullOrWhiteSpace(dbName) && dbName.Length <= 128 && dbName.All(c => char.IsLetterOrDigit(c) || c == '_');
}

public static class SqlRead
{
    public static string String(IDataRecord r, string name) => r[name] == DBNull.Value ? string.Empty : Convert.ToString(r[name]) ?? string.Empty;
    public static int Int(IDataRecord r, string name) => r[name] == DBNull.Value ? 0 : Convert.ToInt32(r[name]);
    public static decimal Decimal(IDataRecord r, string name) => r[name] == DBNull.Value ? 0m : Convert.ToDecimal(r[name]);
    public static bool Bool(IDataRecord r, string name) => r[name] != DBNull.Value && Convert.ToBoolean(r[name]);
    public static DateTime? NullableDateTime(IDataRecord r, string name) => r[name] == DBNull.Value ? null : Convert.ToDateTime(r[name]);
}

public static class SqlList
{
    public static async Task<List<Dictionary<string, object?>>> ReadAsync(SqlConnection con, string sql)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        return await ReadAsync(cmd);
    }

    public static async Task<Dictionary<string, object?>> ReadSingleAsync(SqlConnection con, string sql)
    {
        var list = await ReadAsync(con, sql);
        return list.FirstOrDefault() ?? new Dictionary<string, object?>();
    }

    public static async Task<Dictionary<string, object?>> ReadSingleAsync(SqlCommand cmd)
    {
        var list = await ReadAsync(cmd);
        return list.FirstOrDefault() ?? new Dictionary<string, object?>();
    }

    public static async Task<List<Dictionary<string, object?>>> ReadAsync(SqlCommand cmd)
    {
        var list = new List<Dictionary<string, object?>>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < r.FieldCount; i++)
            {
                var val = r.GetValue(i);
                row[r.GetName(i)] = val == DBNull.Value ? null : val;
            }
            list.Add(row);
        }
        return list;
    }
}
