using Microsoft.Data.SqlClient;
using PayNex.Cloud.Api.Data;
using PayNex.Cloud.Api.Models;
using System.Data;
using System.Globalization;
using System.Text;

namespace PayNex.Cloud.Api.Services;

public sealed class PlatformSubscriptionService
{
    private readonly ConnectionFactory _db;
    private readonly SubscriptionAccessService _subscriptionAccess;

    public PlatformSubscriptionService(ConnectionFactory db, SubscriptionAccessService subscriptionAccess)
    {
        _db = db;
        _subscriptionAccess = subscriptionAccess;
    }

    public async Task EnsureSchemaAsync(SqlConnection? existing = null)
    {
        async Task RunAsync(SqlConnection con)
        {
            async Task ExecAsync(string sql)
            {
                await using var cmd = con.CreateCommand();
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync();
            }

            await ExecAsync(@"
IF OBJECT_ID('SubscriptionPlans') IS NULL
BEGIN
CREATE TABLE SubscriptionPlans(
    SubscriptionPlanId INT IDENTITY(1,1) PRIMARY KEY,
    PlanCode NVARCHAR(30) NOT NULL UNIQUE,
    PlanName NVARCHAR(100) NOT NULL,
    DurationType NVARCHAR(20) NOT NULL CONSTRAINT DF_SubscriptionPlans_DurationType DEFAULT 'Month',
    DurationValue INT NOT NULL CONSTRAINT DF_SubscriptionPlans_DurationValue DEFAULT 1,
    DefaultAmount DECIMAL(18,2) NULL,
    IsActive BIT NOT NULL CONSTRAINT DF_SubscriptionPlans_IsActive DEFAULT 1,
    CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_SubscriptionPlans_CreatedAt DEFAULT SYSUTCDATETIME()
);
END");
            await ExecAsync(@"
IF NOT EXISTS (SELECT 1 FROM SubscriptionPlans WHERE PlanCode = 'MONTHLY')
    INSERT INTO SubscriptionPlans(PlanCode, PlanName, DurationType, DurationValue, DefaultAmount, IsActive)
    VALUES ('MONTHLY', 'Monthly', 'Month', 1, NULL, 1);
IF NOT EXISTS (SELECT 1 FROM SubscriptionPlans WHERE PlanCode = 'QUARTERLY')
    INSERT INTO SubscriptionPlans(PlanCode, PlanName, DurationType, DurationValue, DefaultAmount, IsActive)
    VALUES ('QUARTERLY', 'Quarterly', 'Month', 3, NULL, 1);
IF NOT EXISTS (SELECT 1 FROM SubscriptionPlans WHERE PlanCode = 'HALFYEARLY')
    INSERT INTO SubscriptionPlans(PlanCode, PlanName, DurationType, DurationValue, DefaultAmount, IsActive)
    VALUES ('HALFYEARLY', 'Half Yearly', 'Month', 6, NULL, 1);
IF NOT EXISTS (SELECT 1 FROM SubscriptionPlans WHERE PlanCode = 'ANNUAL')
    INSERT INTO SubscriptionPlans(PlanCode, PlanName, DurationType, DurationValue, DefaultAmount, IsActive)
    VALUES ('ANNUAL', 'Annual', 'Month', 12, NULL, 1);");
            await ExecAsync(@"
IF OBJECT_ID('ClientSubscriptions') IS NULL
BEGIN
CREATE TABLE ClientSubscriptions(
    SubscriptionId INT IDENTITY(1,1) PRIMARY KEY,
    SubscriptionNo NVARCHAR(30) NOT NULL UNIQUE,
    TenantId UNIQUEIDENTIFIER NOT NULL,
    CompanyCode NVARCHAR(40) NOT NULL,
    SubscriptionPlanId INT NOT NULL,
    StartDate DATE NOT NULL,
    ExpiryDate DATE NOT NULL,
    Amount DECIMAL(18,2) NOT NULL CONSTRAINT DF_ClientSubscriptions_Amount DEFAULT 0,
    Currency NVARCHAR(10) NOT NULL CONSTRAINT DF_ClientSubscriptions_Currency DEFAULT 'PKR',
    PaymentStatus NVARCHAR(20) NOT NULL CONSTRAINT DF_ClientSubscriptions_PaymentStatus DEFAULT 'Pending',
    SubscriptionStatus NVARCHAR(20) NOT NULL CONSTRAINT DF_ClientSubscriptions_Status DEFAULT 'Active',
    CancellationDate DATE NULL,
    CancellationReason NVARCHAR(250) NULL,
    CreatedBy NVARCHAR(100) NULL,
    CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_ClientSubscriptions_CreatedAt DEFAULT SYSUTCDATETIME(),
    UpdatedAt DATETIME2 NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ClientSubscriptions_CompanyCode' AND object_id=OBJECT_ID('ClientSubscriptions'))
    CREATE INDEX IX_ClientSubscriptions_CompanyCode ON ClientSubscriptions(CompanyCode, SubscriptionStatus);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ClientSubscriptions_ExpiryDate' AND object_id=OBJECT_ID('ClientSubscriptions'))
    CREATE INDEX IX_ClientSubscriptions_ExpiryDate ON ClientSubscriptions(ExpiryDate, SubscriptionStatus);
END");
            await ExecAsync(@"
IF OBJECT_ID('SubscriptionHistory') IS NULL
BEGIN
CREATE TABLE SubscriptionHistory(
    HistoryId BIGINT IDENTITY(1,1) PRIMARY KEY,
    SubscriptionId INT NOT NULL,
    TenantId UNIQUEIDENTIFIER NOT NULL,
    CompanyCode NVARCHAR(40) NOT NULL,
    ActionType NVARCHAR(40) NOT NULL,
    PreviousExpiryDate DATE NULL,
    NewExpiryDate DATE NULL,
    Amount DECIMAL(18,2) NOT NULL CONSTRAINT DF_SubscriptionHistory_Amount DEFAULT 0,
    PlanId INT NULL,
    Notes NVARCHAR(500) NULL,
    CreatedBy NVARCHAR(100) NULL,
    CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_SubscriptionHistory_CreatedAt DEFAULT SYSUTCDATETIME()
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_SubscriptionHistory_SubscriptionId' AND object_id=OBJECT_ID('SubscriptionHistory'))
    CREATE INDEX IX_SubscriptionHistory_SubscriptionId ON SubscriptionHistory(SubscriptionId, HistoryId DESC);
END");
            await ExecAsync(@"
IF OBJECT_ID('SubscriptionPayments') IS NULL
BEGIN
CREATE TABLE SubscriptionPayments(
    PaymentId BIGINT IDENTITY(1,1) PRIMARY KEY,
    SubscriptionId INT NOT NULL,
    HistoryId BIGINT NULL,
    PaymentDate DATE NOT NULL,
    Amount DECIMAL(18,2) NOT NULL CONSTRAINT DF_SubscriptionPayments_Amount DEFAULT 0,
    PaymentMethod NVARCHAR(40) NOT NULL CONSTRAINT DF_SubscriptionPayments_Method DEFAULT 'Cash',
    ReferenceNo NVARCHAR(80) NULL,
    Status NVARCHAR(20) NOT NULL CONSTRAINT DF_SubscriptionPayments_Status DEFAULT 'Paid',
    CreatedBy NVARCHAR(100) NULL,
    CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_SubscriptionPayments_CreatedAt DEFAULT SYSUTCDATETIME()
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_SubscriptionPayments_SubscriptionId' AND object_id=OBJECT_ID('SubscriptionPayments'))
    CREATE INDEX IX_SubscriptionPayments_SubscriptionId ON SubscriptionPayments(SubscriptionId, PaymentDate DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_SubscriptionPayments_PaymentDate' AND object_id=OBJECT_ID('SubscriptionPayments'))
    CREATE INDEX IX_SubscriptionPayments_PaymentDate ON SubscriptionPayments(PaymentDate, Status);
END");
            await ExecAsync(@"
IF COL_LENGTH('Tenants','LastSubscriptionAmount') IS NULL
    ALTER TABLE Tenants ADD LastSubscriptionAmount DECIMAL(18,2) NULL;");
            await ExecAsync(@"
UPDATE ClientSubscriptions
SET SubscriptionStatus='Expired', UpdatedAt=SYSUTCDATETIME()
WHERE SubscriptionStatus='Active' AND ExpiryDate < CAST(SYSUTCDATETIME() AS DATE);");
            await _subscriptionAccess.SyncAllExpiredTenantsAsync(con);
        }

        if (existing != null)
        {
            await RunAsync(existing);
            return;
        }

        await using var con = await _db.OpenMasterAsync();
        await RunAsync(con);
    }

    public async Task<List<Dictionary<string, object?>>> ListPlansAsync()
    {
        await using var con = await _db.OpenMasterAsync();
        await EnsureSchemaAsync(con);
        return await SqlList.ReadAsync(con, @"
SELECT SubscriptionPlanId,PlanCode,PlanName,DurationType,DurationValue,DefaultAmount,IsActive,CreatedAt
FROM SubscriptionPlans
WHERE IsActive=1
ORDER BY DurationValue, PlanName");
    }

    public async Task<List<Dictionary<string, object?>>> ListAsync(
        string? term = null,
        string? status = null,
        int? planId = null,
        int? expiringWithinDays = null)
    {
        await using var con = await _db.OpenMasterAsync();
        await EnsureSchemaAsync(con);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT s.SubscriptionId,s.SubscriptionNo,s.TenantId,s.CompanyCode,t.CompanyName,
       s.SubscriptionPlanId,p.PlanCode,p.PlanName,p.DurationType,p.DurationValue,
       s.StartDate,s.ExpiryDate,s.Amount,s.Currency,s.PaymentStatus,
       CASE
         WHEN s.SubscriptionStatus='Active' AND s.ExpiryDate < CAST(SYSUTCDATETIME() AS DATE) THEN 'Expired'
         ELSE s.SubscriptionStatus
       END SubscriptionStatus,
       s.CancellationDate,s.CancellationReason,s.CreatedBy,s.CreatedAt,s.UpdatedAt,
       DATEDIFF(DAY, CAST(SYSUTCDATETIME() AS DATE), s.ExpiryDate) DaysRemaining,
       ISNULL(t.LastSubscriptionAmount,s.Amount) LastSubscriptionAmount
FROM ClientSubscriptions s
INNER JOIN Tenants t ON t.TenantId=s.TenantId
INNER JOIN SubscriptionPlans p ON p.SubscriptionPlanId=s.SubscriptionPlanId
WHERE (@Term='' OR s.CompanyCode LIKE @Like OR t.CompanyName LIKE @Like OR s.SubscriptionNo LIKE @Like OR p.PlanName LIKE @Like)
  AND (@Status='' OR (
        CASE
          WHEN s.SubscriptionStatus='Active' AND s.ExpiryDate < CAST(SYSUTCDATETIME() AS DATE) THEN 'Expired'
          ELSE s.SubscriptionStatus
        END)=@Status)
  AND (@PlanId=0 OR s.SubscriptionPlanId=@PlanId)
  AND (@ExpiringDays=0 OR (
        s.SubscriptionStatus IN ('Active','Expired')
        AND s.ExpiryDate >= CAST(SYSUTCDATETIME() AS DATE)
        AND s.ExpiryDate <= DATEADD(DAY,@ExpiringDays,CAST(SYSUTCDATETIME() AS DATE))))
ORDER BY s.ExpiryDate ASC, s.SubscriptionNo DESC";
        var cleanTerm = (term ?? "").Trim();
        cmd.Parameters.AddWithValue("@Term", cleanTerm);
        cmd.Parameters.AddWithValue("@Like", $"%{cleanTerm}%");
        cmd.Parameters.AddWithValue("@Status", (status ?? "").Trim());
        cmd.Parameters.AddWithValue("@PlanId", planId ?? 0);
        cmd.Parameters.AddWithValue("@ExpiringDays", expiringWithinDays ?? 0);
        return await SqlList.ReadAsync(cmd);
    }

    public async Task<Dictionary<string, object?>> GetDashboardAsync()
    {
        await using var con = await _db.OpenMasterAsync();
        await EnsureSchemaAsync(con);
        var today = DateTime.UtcNow.Date;

        await using var kpiCmd = con.CreateCommand();
        kpiCmd.CommandText = @"
DECLARE @Today DATE = CAST(SYSUTCDATETIME() AS DATE);
;WITH Live AS (
  SELECT
    CASE WHEN SubscriptionStatus='Active' AND ExpiryDate < @Today THEN 'Expired' ELSE SubscriptionStatus END Status,
    ExpiryDate, PaymentStatus, Amount, CompanyCode
  FROM ClientSubscriptions
)
SELECT
  (SELECT COUNT(*) FROM Tenants) TotalClients,
  (SELECT COUNT(*) FROM Live WHERE Status='Active') ActiveSubscriptions,
  (SELECT COUNT(*) FROM Live WHERE Status='Expired') ExpiredSubscriptions,
  (SELECT COUNT(*) FROM Live WHERE Status='Cancelled') CancelledSubscriptions,
  (SELECT COUNT(*) FROM Live WHERE Status='Suspended') SuspendedSubscriptions,
  (SELECT COUNT(*) FROM Live WHERE Status='Active' AND ExpiryDate BETWEEN @Today AND DATEADD(DAY,30,@Today)) ExpiringSoon,
  (SELECT COUNT(*) FROM Live WHERE PaymentStatus IN ('Pending','Partial')) PendingPayments,
  (SELECT ISNULL(SUM(Amount),0) FROM SubscriptionPayments WHERE Status='Paid' AND PaymentDate>=DATEFROMPARTS(YEAR(@Today),MONTH(@Today),1) AND PaymentDate<=@Today) MonthlyRevenue,
  (SELECT ISNULL(SUM(Amount),0) FROM SubscriptionPayments WHERE Status='Paid' AND PaymentDate>=DATEFROMPARTS(YEAR(@Today),1,1) AND PaymentDate<=@Today) AnnualRevenue,
  (SELECT COUNT(*) FROM Live WHERE Status='Active' AND ExpiryDate BETWEEN @Today AND DATEADD(DAY,30,@Today)) RenewalsNext30,
  (SELECT COUNT(*) FROM Live WHERE Status='Active' AND ExpiryDate BETWEEN @Today AND DATEADD(DAY,60,@Today)) RenewalsNext60,
  (SELECT COUNT(*) FROM Live WHERE Status='Active' AND ExpiryDate BETWEEN @Today AND DATEADD(DAY,90,@Today)) RenewalsNext90;";
        var kpi = await SqlList.ReadSingleAsync(kpiCmd);

        var monthly = await SqlList.ReadAsync(con, @"
DECLARE @Start DATE = DATEADD(MONTH,-11,DATEFROMPARTS(YEAR(SYSUTCDATETIME()),MONTH(SYSUTCDATETIME()),1));
SELECT FORMAT(PaymentDate,'yyyy-MM') PeriodLabel,
       DATENAME(MONTH, MIN(PaymentDate)) MonthName,
       YEAR(MIN(PaymentDate)) YearNo,
       SUM(Amount) TotalAmount
FROM SubscriptionPayments
WHERE Status='Paid' AND PaymentDate>=@Start
GROUP BY FORMAT(PaymentDate,'yyyy-MM')
ORDER BY PeriodLabel;");

        var statusChart = await SqlList.ReadAsync(con, @"
DECLARE @Today DATE = CAST(SYSUTCDATETIME() AS DATE);
SELECT Status, COUNT(*) TotalCount FROM (
  SELECT CASE WHEN SubscriptionStatus='Active' AND ExpiryDate < @Today THEN 'Expired' ELSE SubscriptionStatus END Status
  FROM ClientSubscriptions
) x
GROUP BY Status
ORDER BY Status;");

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["kpis"] = kpi,
            ["monthlyRevenue"] = monthly,
            ["statusBreakdown"] = statusChart,
            ["asOf"] = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
    }

    public async Task<Dictionary<string, object?>> GetAsync(int subscriptionId)
    {
        await using var con = await _db.OpenMasterAsync();
        await EnsureSchemaAsync(con);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT s.SubscriptionId,s.SubscriptionNo,s.TenantId,s.CompanyCode,t.CompanyName,
       s.SubscriptionPlanId,p.PlanCode,p.PlanName,p.DurationType,p.DurationValue,
       s.StartDate,s.ExpiryDate,s.Amount,s.Currency,s.PaymentStatus,
       CASE WHEN s.SubscriptionStatus='Active' AND s.ExpiryDate < CAST(SYSUTCDATETIME() AS DATE) THEN 'Expired' ELSE s.SubscriptionStatus END SubscriptionStatus,
       s.CancellationDate,s.CancellationReason,s.CreatedBy,s.CreatedAt,s.UpdatedAt,
       DATEDIFF(DAY, CAST(SYSUTCDATETIME() AS DATE), s.ExpiryDate) DaysRemaining,
       ISNULL(t.LastSubscriptionAmount,s.Amount) LastSubscriptionAmount,
       t.LicenseStatus,t.SubscriptionPlan TenantPlan,t.ExpiryDate TenantExpiryDate,t.RenewalDate
FROM ClientSubscriptions s
INNER JOIN Tenants t ON t.TenantId=s.TenantId
INNER JOIN SubscriptionPlans p ON p.SubscriptionPlanId=s.SubscriptionPlanId
WHERE s.SubscriptionId=@Id";
        cmd.Parameters.AddWithValue("@Id", subscriptionId);
        var header = await SqlList.ReadSingleAsync(cmd);
        if (header.Count == 0)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["subscription"] = null,
                ["history"] = Array.Empty<object>(),
                ["payments"] = Array.Empty<object>(),
            };
        }

        await using var histCmd = con.CreateCommand();
        histCmd.CommandText = @"
SELECT h.HistoryId,h.SubscriptionId,h.CompanyCode,h.ActionType,h.PreviousExpiryDate,h.NewExpiryDate,
       h.Amount,h.PlanId,ISNULL(p.PlanName,'') PlanName,h.Notes,h.CreatedBy,h.CreatedAt
FROM SubscriptionHistory h
LEFT JOIN SubscriptionPlans p ON p.SubscriptionPlanId=h.PlanId
WHERE h.SubscriptionId=@Id
ORDER BY h.HistoryId DESC";
        histCmd.Parameters.AddWithValue("@Id", subscriptionId);
        var history = await SqlList.ReadAsync(histCmd);

        await using var payCmd = con.CreateCommand();
        payCmd.CommandText = @"
SELECT PaymentId,SubscriptionId,HistoryId,PaymentDate,Amount,PaymentMethod,ReferenceNo,Status,CreatedBy,CreatedAt
FROM SubscriptionPayments
WHERE SubscriptionId=@Id
ORDER BY PaymentDate DESC, PaymentId DESC";
        payCmd.Parameters.AddWithValue("@Id", subscriptionId);
        var payments = await SqlList.ReadAsync(payCmd);

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["subscription"] = header,
            ["history"] = history,
            ["payments"] = payments,
        };
    }

    public async Task<Dictionary<string, object?>> GetByCompanyAsync(string companyCode)
    {
        await using var con = await _db.OpenMasterAsync();
        await EnsureSchemaAsync(con);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT TOP 1 SubscriptionId
FROM ClientSubscriptions
WHERE CompanyCode=@Code
ORDER BY CASE WHEN SubscriptionStatus IN ('Active','Expired') THEN 0 ELSE 1 END, ExpiryDate DESC, SubscriptionId DESC";
        cmd.Parameters.AddWithValue("@Code", companyCode.Trim().ToUpperInvariant());
        var idObj = await cmd.ExecuteScalarAsync();
        if (idObj == null || idObj == DBNull.Value)
        {
            await using var tenantCmd = con.CreateCommand();
            tenantCmd.CommandText = @"
SELECT TenantId,CompanyCode,CompanyName,SubscriptionPlan,LicenseStatus,ExpiryDate,LicenseExpiryDate,RenewalDate,LastSubscriptionAmount
FROM Tenants WHERE CompanyCode=@Code";
            tenantCmd.Parameters.AddWithValue("@Code", companyCode.Trim().ToUpperInvariant());
            var tenant = await SqlList.ReadSingleAsync(tenantCmd);
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["subscription"] = null,
                ["tenant"] = tenant.Count == 0 ? null : tenant,
                ["history"] = Array.Empty<object>(),
                ["payments"] = Array.Empty<object>(),
            };
        }

        return await GetAsync(Convert.ToInt32(idObj));
    }

    public async Task<object> PostNewAsync(UserSession user, SubscriptionPostRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CompanyCode))
            throw new InvalidOperationException("Company is required.");
        if (request.SubscriptionPlanId <= 0)
            throw new InvalidOperationException("Subscription plan is required.");
        if (request.Amount <= 0)
            throw new InvalidOperationException("Subscription amount must be greater than zero.");

        await using var con = await _db.OpenMasterAsync();
        await EnsureSchemaAsync(con);
        await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
        try
        {
            var companyCode = request.CompanyCode.Trim().ToUpperInvariant();
            var tenant = await GetTenantAsync(con, tran, companyCode)
                ?? throw new InvalidOperationException("Client company was not found.");
            var plan = await GetPlanAsync(con, tran, request.SubscriptionPlanId)
                ?? throw new InvalidOperationException("Subscription plan was not found.");

            // New subscription: keep the Start/Expiry the owner entered on the card.
            // Do not rewrite from a previous live expiry (that is Renew's job).
            var startDate = ToDateOnly(request.StartDate) ?? DateTime.Today;
            var previousExpiry = await GetCurrentExpiryAsync(con, tran, companyCode);
            DateTime expiry;
            var requestedExpiry = ToDateOnly(request.ExpiryDate);
            if (requestedExpiry.HasValue)
            {
                expiry = requestedExpiry.Value;
                if (expiry < startDate)
                    throw new InvalidOperationException("Expiry date cannot be before start date.");
            }
            else
            {
                expiry = AddDuration(startDate, plan.DurationType, plan.DurationValue);
            }

            var paymentStatus = NormalizePaymentStatus(request.PaymentStatus);
            var currency = string.IsNullOrWhiteSpace(request.Currency) ? "PKR" : request.Currency.Trim().ToUpperInvariant();
            var createdBy = user.DisplayName ?? user.UserName ?? "Owner";
            var subscriptionNo = await NextSubscriptionNoAsync(con, tran);

            // Close any prior active rows for this company.
            await using (var close = new SqlCommand(@"
UPDATE ClientSubscriptions
SET SubscriptionStatus=CASE WHEN ExpiryDate < CAST(SYSUTCDATETIME() AS DATE) THEN 'Expired' ELSE 'Suspended' END,
    UpdatedAt=SYSUTCDATETIME()
WHERE CompanyCode=@Code AND SubscriptionStatus='Active'", con, tran))
            {
                close.Parameters.AddWithValue("@Code", companyCode);
                await close.ExecuteNonQueryAsync();
            }

            int subscriptionId;
            await using (var insert = new SqlCommand(@"
INSERT INTO ClientSubscriptions(SubscriptionNo,TenantId,CompanyCode,SubscriptionPlanId,StartDate,ExpiryDate,Amount,Currency,PaymentStatus,SubscriptionStatus,CreatedBy)
OUTPUT INSERTED.SubscriptionId
VALUES(@No,@TenantId,@Code,@PlanId,@Start,@Expiry,@Amount,@Currency,@PayStatus,'Active',@CreatedBy)", con, tran))
            {
                insert.Parameters.AddWithValue("@No", subscriptionNo);
                insert.Parameters.AddWithValue("@TenantId", tenant.TenantId);
                insert.Parameters.AddWithValue("@Code", companyCode);
                insert.Parameters.AddWithValue("@PlanId", plan.SubscriptionPlanId);
                insert.Parameters.AddWithValue("@Start", startDate);
                insert.Parameters.AddWithValue("@Expiry", expiry);
                insert.Parameters.AddWithValue("@Amount", request.Amount);
                insert.Parameters.AddWithValue("@Currency", currency);
                insert.Parameters.AddWithValue("@PayStatus", paymentStatus);
                insert.Parameters.AddWithValue("@CreatedBy", createdBy);
                subscriptionId = Convert.ToInt32(await insert.ExecuteScalarAsync());
            }

            var historyId = await InsertHistoryAsync(
                con, tran, subscriptionId, tenant.TenantId, companyCode,
                "NewSubscription", previousExpiry, expiry, request.Amount, plan.SubscriptionPlanId,
                request.Notes, createdBy);

            if (!string.Equals(paymentStatus, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                await InsertPaymentAsync(
                    con, tran, subscriptionId, historyId, DateTime.UtcNow.Date, request.Amount,
                    request.PaymentMethod, request.ReferenceNo, paymentStatus, createdBy);
            }

            await SyncTenantLicenseAsync(con, tran, companyCode, plan.PlanName, "Active", expiry, request.Amount);
            await WriteAuditAsync(con, tran, tenant.TenantId, companyCode, "SubscriptionPosted",
                $"Posted {subscriptionNo} {plan.PlanName} until {expiry:yyyy-MM-dd} amount {request.Amount:0.##}");

            await tran.CommitAsync();
            return new
            {
                subscriptionId,
                subscriptionNo,
                companyCode,
                startDate = startDate.ToString("yyyy-MM-dd"),
                expiryDate = expiry.ToString("yyyy-MM-dd"),
                message = "Subscription posted."
            };
        }
        catch
        {
            await tran.RollbackAsync();
            throw;
        }
    }

    public async Task<object> RenewAsync(UserSession user, int subscriptionId, SubscriptionRenewRequest request)
    {
        if (request.Amount <= 0)
            throw new InvalidOperationException("Renewal amount must be greater than zero.");

        await using var con = await _db.OpenMasterAsync();
        await EnsureSchemaAsync(con);
        await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
        try
        {
            var current = await GetSubscriptionRowAsync(con, tran, subscriptionId)
                ?? throw new InvalidOperationException("Subscription was not found.");
            if (string.Equals(current.SubscriptionStatus, "Cancelled", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cancelled subscriptions cannot be renewed. Post a new subscription.");

            var planId = request.SubscriptionPlanId is > 0 ? request.SubscriptionPlanId.Value : current.SubscriptionPlanId;
            var plan = await GetPlanAsync(con, tran, planId)
                ?? throw new InvalidOperationException("Subscription plan was not found.");

            var months = request.DurationMonths is > 0
                ? request.DurationMonths.Value
                : DurationMonths(plan.DurationType, plan.DurationValue);

            var previousExpiry = current.ExpiryDate.Date;
            var baseDate = previousExpiry >= DateTime.UtcNow.Date ? previousExpiry : DateTime.UtcNow.Date;
            var newExpiry = baseDate.AddMonths(months);
            var paymentStatus = NormalizePaymentStatus(request.PaymentStatus);
            var currency = string.IsNullOrWhiteSpace(request.Currency) ? current.Currency : request.Currency.Trim().ToUpperInvariant();
            var createdBy = user.DisplayName ?? user.UserName ?? "Owner";

            await using (var update = new SqlCommand(@"
UPDATE ClientSubscriptions
SET SubscriptionPlanId=@PlanId, ExpiryDate=@Expiry, Amount=@Amount, Currency=@Currency,
    PaymentStatus=@PayStatus, SubscriptionStatus='Active', CancellationDate=NULL, CancellationReason=NULL,
    UpdatedAt=SYSUTCDATETIME()
WHERE SubscriptionId=@Id", con, tran))
            {
                update.Parameters.AddWithValue("@PlanId", plan.SubscriptionPlanId);
                update.Parameters.AddWithValue("@Expiry", newExpiry);
                update.Parameters.AddWithValue("@Amount", request.Amount);
                update.Parameters.AddWithValue("@Currency", currency);
                update.Parameters.AddWithValue("@PayStatus", paymentStatus);
                update.Parameters.AddWithValue("@Id", subscriptionId);
                await update.ExecuteNonQueryAsync();
            }

            var historyId = await InsertHistoryAsync(
                con, tran, subscriptionId, current.TenantId, current.CompanyCode,
                "Renewal", previousExpiry, newExpiry, request.Amount, plan.SubscriptionPlanId,
                request.Notes, createdBy);

            if (!string.Equals(paymentStatus, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                await InsertPaymentAsync(
                    con, tran, subscriptionId, historyId, DateTime.UtcNow.Date, request.Amount,
                    request.PaymentMethod, request.ReferenceNo, paymentStatus, createdBy);
            }

            await SyncTenantLicenseAsync(con, tran, current.CompanyCode, plan.PlanName, "Active", newExpiry, request.Amount);
            await WriteAuditAsync(con, tran, current.TenantId, current.CompanyCode, "SubscriptionRenewed",
                $"Renewed {current.SubscriptionNo} to {newExpiry:yyyy-MM-dd} amount {request.Amount:0.##}");

            await tran.CommitAsync();
            return new
            {
                subscriptionId,
                subscriptionNo = current.SubscriptionNo,
                companyCode = current.CompanyCode,
                previousExpiryDate = previousExpiry.ToString("yyyy-MM-dd"),
                expiryDate = newExpiry.ToString("yyyy-MM-dd"),
                message = "Subscription renewed."
            };
        }
        catch
        {
            await tran.RollbackAsync();
            throw;
        }
    }

    public async Task<object> CancelAsync(UserSession user, int subscriptionId, SubscriptionCancelRequest request)
    {
        await using var con = await _db.OpenMasterAsync();
        await EnsureSchemaAsync(con);
        await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
        try
        {
            var current = await GetSubscriptionRowAsync(con, tran, subscriptionId)
                ?? throw new InvalidOperationException("Subscription was not found.");
            if (string.Equals(current.SubscriptionStatus, "Cancelled", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Subscription is already cancelled.");

            var cancelDate = (request.CancellationDate ?? DateTime.UtcNow).Date;
            var reason = (request.Reason ?? "").Trim();
            if (string.IsNullOrWhiteSpace(reason))
                throw new InvalidOperationException("Cancellation reason is required.");
            var createdBy = user.DisplayName ?? user.UserName ?? "Owner";

            await using (var update = new SqlCommand(@"
UPDATE ClientSubscriptions
SET SubscriptionStatus='Cancelled', CancellationDate=@CancelDate, CancellationReason=@Reason, UpdatedAt=SYSUTCDATETIME()
WHERE SubscriptionId=@Id", con, tran))
            {
                update.Parameters.AddWithValue("@CancelDate", cancelDate);
                update.Parameters.AddWithValue("@Reason", reason);
                update.Parameters.AddWithValue("@Id", subscriptionId);
                await update.ExecuteNonQueryAsync();
            }

            await InsertHistoryAsync(
                con, tran, subscriptionId, current.TenantId, current.CompanyCode,
                "Cancellation", current.ExpiryDate, current.ExpiryDate, 0, current.SubscriptionPlanId,
                reason, createdBy);

            await SyncTenantLicenseAsync(con, tran, current.CompanyCode, current.PlanName, "Suspended", current.ExpiryDate, null);
            await WriteAuditAsync(con, tran, current.TenantId, current.CompanyCode, "SubscriptionCancelled",
                $"Cancelled {current.SubscriptionNo}: {reason}");

            await tran.CommitAsync();
            return new
            {
                subscriptionId,
                subscriptionNo = current.SubscriptionNo,
                companyCode = current.CompanyCode,
                message = "Subscription cancelled."
            };
        }
        catch
        {
            await tran.RollbackAsync();
            throw;
        }
    }

    public async Task<Dictionary<string, object?>> GetReportAsync(string? reportType = null, string? status = null)
    {
        var type = string.IsNullOrWhiteSpace(reportType) ? "register" : reportType.Trim().ToLowerInvariant();
        await using var con = await _db.OpenMasterAsync();
        await EnsureSchemaAsync(con);

        if (type == "revenue")
        {
            var rows = await SqlList.ReadAsync(con, @"
SELECT FORMAT(PaymentDate,'yyyy-MM') PeriodLabel,
       DATENAME(MONTH, MIN(PaymentDate)) MonthName,
       YEAR(MIN(PaymentDate)) YearNo,
       COUNT(*) PaymentCount,
       SUM(Amount) TotalAmount
FROM SubscriptionPayments
WHERE Status='Paid'
GROUP BY FORMAT(PaymentDate,'yyyy-MM')
ORDER BY PeriodLabel DESC");
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["reportType"] = "revenue",
                ["rows"] = rows,
            };
        }

        if (type == "expiry")
        {
            await using var cmd = con.CreateCommand();
            cmd.CommandText = @"
DECLARE @Today DATE = CAST(SYSUTCDATETIME() AS DATE);
SELECT s.SubscriptionNo,s.CompanyCode,t.CompanyName,p.PlanName,s.Amount,s.StartDate,s.ExpiryDate,
       CASE WHEN s.SubscriptionStatus='Active' AND s.ExpiryDate < @Today THEN 'Expired' ELSE s.SubscriptionStatus END SubscriptionStatus,
       DATEDIFF(DAY,@Today,s.ExpiryDate) DaysRemaining,
       CASE
         WHEN DATEDIFF(DAY,@Today,s.ExpiryDate) BETWEEN 0 AND 30 THEN '0-30'
         WHEN DATEDIFF(DAY,@Today,s.ExpiryDate) BETWEEN 31 AND 60 THEN '30-60'
         WHEN DATEDIFF(DAY,@Today,s.ExpiryDate) BETWEEN 61 AND 90 THEN '60-90'
         ELSE 'Other'
       END ExpiryBucket
FROM ClientSubscriptions s
INNER JOIN Tenants t ON t.TenantId=s.TenantId
INNER JOIN SubscriptionPlans p ON p.SubscriptionPlanId=s.SubscriptionPlanId
WHERE s.SubscriptionStatus IN ('Active','Expired')
  AND s.ExpiryDate BETWEEN @Today AND DATEADD(DAY,90,@Today)
ORDER BY s.ExpiryDate, s.CompanyCode";
            var rows = await SqlList.ReadAsync(cmd);
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["reportType"] = "expiry",
                ["rows"] = rows,
            };
        }

        {
            await using var cmd = con.CreateCommand();
            cmd.CommandText = @"
DECLARE @Today DATE = CAST(SYSUTCDATETIME() AS DATE);
SELECT s.SubscriptionNo,s.CompanyCode,t.CompanyName,p.PlanName,s.Amount,s.Currency,s.StartDate,s.ExpiryDate,
       s.PaymentStatus,
       CASE WHEN s.SubscriptionStatus='Active' AND s.ExpiryDate < @Today THEN 'Expired' ELSE s.SubscriptionStatus END SubscriptionStatus
FROM ClientSubscriptions s
INNER JOIN Tenants t ON t.TenantId=s.TenantId
INNER JOIN SubscriptionPlans p ON p.SubscriptionPlanId=s.SubscriptionPlanId
WHERE (@Status='' OR (
        CASE WHEN s.SubscriptionStatus='Active' AND s.ExpiryDate < @Today THEN 'Expired' ELSE s.SubscriptionStatus END)=@Status)
ORDER BY s.ExpiryDate DESC, s.SubscriptionNo DESC";
            cmd.Parameters.AddWithValue("@Status", (status ?? "").Trim());
            var rows = await SqlList.ReadAsync(cmd);
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["reportType"] = "register",
                ["rows"] = rows,
            };
        }
    }

    public async Task<string> GetReportHtmlAsync(string? reportType = null, string? status = null)
    {
        var report = await GetReportAsync(reportType, status);
        var type = Convert.ToString(report.GetValueOrDefault("reportType")) ?? "register";
        var rows = report.GetValueOrDefault("rows") as List<Dictionary<string, object?>> ?? [];
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset='utf-8'><title>Subscription Report</title>");
        sb.Append("<style>body{font-family:Segoe UI,Arial,sans-serif;padding:24px;color:#111}h1{margin:0 0 8px}table{border-collapse:collapse;width:100%;margin-top:16px}th,td{border:1px solid #ccc;padding:8px;text-align:left;font-size:13px}th{background:#f3f4f6}.muted{color:#666}.number{text-align:right}</style></head><body>");
        sb.Append($"<h1>Client Subscription {(type switch { "expiry" => "Expiry", "revenue" => "Revenue", _ => "Register" })} Report</h1>");
        sb.Append($"<p class='muted'>Generated {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC · {rows.Count} row(s)</p><table><thead><tr>");

        if (type == "revenue")
        {
            sb.Append("<th>Period</th><th>Month</th><th class='number'>Payments</th><th class='number'>Amount</th></tr></thead><tbody>");
            foreach (var row in rows)
            {
                sb.Append("<tr>");
                sb.Append($"<td>{Esc(row.GetValueOrDefault("PeriodLabel"))}</td>");
                sb.Append($"<td>{Esc(row.GetValueOrDefault("MonthName"))} {Esc(row.GetValueOrDefault("YearNo"))}</td>");
                sb.Append($"<td class='number'>{Esc(row.GetValueOrDefault("PaymentCount"))}</td>");
                sb.Append($"<td class='number'>{FormatMoney(row.GetValueOrDefault("TotalAmount"))}</td>");
                sb.Append("</tr>");
            }
        }
        else if (type == "expiry")
        {
            sb.Append("<th>Subscription</th><th>Client</th><th>Plan</th><th class='number'>Amount</th><th>Expiry</th><th>Days</th><th>Bucket</th><th>Status</th></tr></thead><tbody>");
            foreach (var row in rows)
            {
                sb.Append("<tr>");
                sb.Append($"<td>{Esc(row.GetValueOrDefault("SubscriptionNo"))}</td>");
                sb.Append($"<td>{Esc(row.GetValueOrDefault("CompanyCode"))} · {Esc(row.GetValueOrDefault("CompanyName"))}</td>");
                sb.Append($"<td>{Esc(row.GetValueOrDefault("PlanName"))}</td>");
                sb.Append($"<td class='number'>{FormatMoney(row.GetValueOrDefault("Amount"))}</td>");
                sb.Append($"<td>{DateOnly(row.GetValueOrDefault("ExpiryDate"))}</td>");
                sb.Append($"<td class='number'>{Esc(row.GetValueOrDefault("DaysRemaining"))}</td>");
                sb.Append($"<td>{Esc(row.GetValueOrDefault("ExpiryBucket"))}</td>");
                sb.Append($"<td>{Esc(row.GetValueOrDefault("SubscriptionStatus"))}</td>");
                sb.Append("</tr>");
            }
        }
        else
        {
            sb.Append("<th>Subscription</th><th>Client</th><th>Plan</th><th class='number'>Amount</th><th>Start</th><th>Expiry</th><th>Payment</th><th>Status</th></tr></thead><tbody>");
            foreach (var row in rows)
            {
                sb.Append("<tr>");
                sb.Append($"<td>{Esc(row.GetValueOrDefault("SubscriptionNo"))}</td>");
                sb.Append($"<td>{Esc(row.GetValueOrDefault("CompanyCode"))} · {Esc(row.GetValueOrDefault("CompanyName"))}</td>");
                sb.Append($"<td>{Esc(row.GetValueOrDefault("PlanName"))}</td>");
                sb.Append($"<td class='number'>{FormatMoney(row.GetValueOrDefault("Amount"))}</td>");
                sb.Append($"<td>{DateOnly(row.GetValueOrDefault("StartDate"))}</td>");
                sb.Append($"<td>{DateOnly(row.GetValueOrDefault("ExpiryDate"))}</td>");
                sb.Append($"<td>{Esc(row.GetValueOrDefault("PaymentStatus"))}</td>");
                sb.Append($"<td>{Esc(row.GetValueOrDefault("SubscriptionStatus"))}</td>");
                sb.Append("</tr>");
            }
        }

        if (rows.Count == 0) sb.Append("<tr><td colspan='8'>No records.</td></tr>");
        sb.Append("</tbody></table></body></html>");
        return sb.ToString();
    }

    private static string Esc(object? value) =>
        System.Net.WebUtility.HtmlEncode(Convert.ToString(value) ?? "");

    private static string DateOnly(object? value)
    {
        if (value is DateTime dt) return dt.ToString("yyyy-MM-dd");
        var text = Convert.ToString(value);
        return string.IsNullOrWhiteSpace(text) ? "—" : text.Length >= 10 ? text[..10] : text;
    }

    private static string FormatMoney(object? value)
    {
        if (value == null) return "0.00";
        return Convert.ToDecimal(value).ToString("N2", CultureInfo.InvariantCulture);
    }

    private static string NormalizePaymentStatus(string? status)
    {
        var value = (status ?? "Paid").Trim();
        if (value.Equals("Pending", StringComparison.OrdinalIgnoreCase)) return "Pending";
        if (value.Equals("Partial", StringComparison.OrdinalIgnoreCase)) return "Partial";
        return "Paid";
    }

    private static int DurationMonths(string durationType, int durationValue)
    {
        if (durationType.Equals("Year", StringComparison.OrdinalIgnoreCase))
            return Math.Max(1, durationValue) * 12;
        return Math.Max(1, durationValue);
    }

    private static DateTime AddDuration(DateTime baseDate, string durationType, int durationValue) =>
        baseDate.AddMonths(DurationMonths(durationType, durationValue));

    /// <summary>
    /// Prefer calendar Y/M/D from the payload so UTC midnight does not shift the day.
    /// </summary>
    private static DateTime? ToDateOnly(DateTime? value)
    {
        if (!value.HasValue) return null;
        var d = value.Value;
        if (d.Kind == DateTimeKind.Utc)
            return new DateTime(d.Year, d.Month, d.Day, 0, 0, 0, DateTimeKind.Unspecified);
        return d.Date;
    }

    private async Task<string> NextSubscriptionNoAsync(SqlConnection con, SqlTransaction tran)
    {
        await using var cmd = new SqlCommand("SELECT ISNULL(MAX(SubscriptionId),0)+1 FROM ClientSubscriptions", con, tran);
        var next = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        return $"SUB-{next:00000}";
    }

    private async Task<TenantLite?> GetTenantAsync(SqlConnection con, SqlTransaction tran, string companyCode)
    {
        await using var cmd = new SqlCommand("SELECT TOP 1 TenantId,CompanyCode,CompanyName FROM Tenants WHERE CompanyCode=@Code", con, tran);
        cmd.Parameters.AddWithValue("@Code", companyCode);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new TenantLite(r.GetGuid(0), r.GetString(1), r.GetString(2));
    }

    private async Task<PlanLite?> GetPlanAsync(SqlConnection con, SqlTransaction tran, int planId)
    {
        await using var cmd = new SqlCommand(@"
SELECT TOP 1 SubscriptionPlanId,PlanCode,PlanName,DurationType,DurationValue
FROM SubscriptionPlans WHERE SubscriptionPlanId=@Id AND IsActive=1", con, tran);
        cmd.Parameters.AddWithValue("@Id", planId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new PlanLite(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4));
    }

    private async Task<DateTime?> GetCurrentExpiryAsync(SqlConnection con, SqlTransaction tran, string companyCode)
    {
        await using var cmd = new SqlCommand(@"
SELECT TOP 1 ExpiryDate
FROM ClientSubscriptions
WHERE CompanyCode=@Code AND SubscriptionStatus IN ('Active','Expired')
ORDER BY ExpiryDate DESC, SubscriptionId DESC", con, tran);
        cmd.Parameters.AddWithValue("@Code", companyCode);
        var result = await cmd.ExecuteScalarAsync();
        if (result == null || result == DBNull.Value)
        {
            await using var tenantCmd = new SqlCommand(@"
SELECT TOP 1 ISNULL(LicenseExpiryDate, ExpiryDate) FROM Tenants WHERE CompanyCode=@Code", con, tran);
            tenantCmd.Parameters.AddWithValue("@Code", companyCode);
            result = await tenantCmd.ExecuteScalarAsync();
        }
        return result == null || result == DBNull.Value ? null : Convert.ToDateTime(result).Date;
    }

    private async Task<SubscriptionLite?> GetSubscriptionRowAsync(SqlConnection con, SqlTransaction tran, int id)
    {
        await using var cmd = new SqlCommand(@"
SELECT s.SubscriptionId,s.SubscriptionNo,s.TenantId,s.CompanyCode,s.SubscriptionPlanId,p.PlanName,
       s.StartDate,s.ExpiryDate,s.Amount,s.Currency,s.SubscriptionStatus
FROM ClientSubscriptions s
INNER JOIN SubscriptionPlans p ON p.SubscriptionPlanId=s.SubscriptionPlanId
WHERE s.SubscriptionId=@Id", con, tran);
        cmd.Parameters.AddWithValue("@Id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new SubscriptionLite(
            r.GetInt32(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetInt32(4), r.GetString(5),
            r.GetDateTime(6), r.GetDateTime(7), r.GetDecimal(8), r.GetString(9), r.GetString(10));
    }

    private async Task<long> InsertHistoryAsync(
        SqlConnection con, SqlTransaction tran, int subscriptionId, Guid tenantId, string companyCode,
        string actionType, DateTime? previousExpiry, DateTime? newExpiry, decimal amount, int? planId,
        string? notes, string createdBy)
    {
        await using var cmd = new SqlCommand(@"
INSERT INTO SubscriptionHistory(SubscriptionId,TenantId,CompanyCode,ActionType,PreviousExpiryDate,NewExpiryDate,Amount,PlanId,Notes,CreatedBy)
OUTPUT INSERTED.HistoryId
VALUES(@SubscriptionId,@TenantId,@Code,@Action,@Prev,@New,@Amount,@PlanId,@Notes,@CreatedBy)", con, tran);
        cmd.Parameters.AddWithValue("@SubscriptionId", subscriptionId);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@Code", companyCode);
        cmd.Parameters.AddWithValue("@Action", actionType);
        cmd.Parameters.AddWithValue("@Prev", (object?)previousExpiry?.Date ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@New", (object?)newExpiry?.Date ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Amount", amount);
        cmd.Parameters.AddWithValue("@PlanId", (object?)planId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Notes", (object?)notes?.Trim() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedBy", createdBy);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task InsertPaymentAsync(
        SqlConnection con, SqlTransaction tran, int subscriptionId, long historyId, DateTime paymentDate,
        decimal amount, string? method, string? reference, string status, string createdBy)
    {
        await using var cmd = new SqlCommand(@"
INSERT INTO SubscriptionPayments(SubscriptionId,HistoryId,PaymentDate,Amount,PaymentMethod,ReferenceNo,Status,CreatedBy)
VALUES(@SubscriptionId,@HistoryId,@PaymentDate,@Amount,@Method,@Ref,@Status,@CreatedBy)", con, tran);
        cmd.Parameters.AddWithValue("@SubscriptionId", subscriptionId);
        cmd.Parameters.AddWithValue("@HistoryId", historyId);
        cmd.Parameters.AddWithValue("@PaymentDate", paymentDate);
        cmd.Parameters.AddWithValue("@Amount", amount);
        cmd.Parameters.AddWithValue("@Method", string.IsNullOrWhiteSpace(method) ? "Cash" : method.Trim());
        cmd.Parameters.AddWithValue("@Ref", (object?)reference?.Trim() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Status", status);
        cmd.Parameters.AddWithValue("@CreatedBy", createdBy);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SyncTenantLicenseAsync(
        SqlConnection con, SqlTransaction tran, string companyCode, string planName, string licenseStatus,
        DateTime expiry, decimal? lastAmount)
    {
        await using var cmd = new SqlCommand(@"
UPDATE Tenants
SET SubscriptionPlan=@Plan,
    LicenseStatus=@LicenseStatus,
    ExpiryDate=@Expiry,
    LicenseExpiryDate=@Expiry,
    RenewalDate=@Expiry,
    LastSubscriptionAmount=CASE WHEN @Amount IS NULL THEN LastSubscriptionAmount ELSE @Amount END,
    UpdatedAt=SYSUTCDATETIME()
WHERE CompanyCode=@Code", con, tran);
        cmd.Parameters.AddWithValue("@Plan", planName);
        cmd.Parameters.AddWithValue("@LicenseStatus", licenseStatus);
        cmd.Parameters.AddWithValue("@Expiry", expiry.Date);
        cmd.Parameters.AddWithValue("@Amount", (object?)lastAmount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Code", companyCode);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task WriteAuditAsync(
        SqlConnection con, SqlTransaction tran, Guid tenantId, string companyCode, string action, string description)
    {
        await using var cmd = new SqlCommand(@"
IF OBJECT_ID('TenantAuditLog') IS NOT NULL
INSERT INTO TenantAuditLog(TenantId,CompanyCode,ActionName,Description)
VALUES(@TenantId,@Code,@Action,@Description)", con, tran);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@Code", companyCode);
        cmd.Parameters.AddWithValue("@Action", action);
        cmd.Parameters.AddWithValue("@Description", description);
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed record TenantLite(Guid TenantId, string CompanyCode, string CompanyName);
    private sealed record PlanLite(int SubscriptionPlanId, string PlanCode, string PlanName, string DurationType, int DurationValue);
    private sealed record SubscriptionLite(
        int SubscriptionId, string SubscriptionNo, Guid TenantId, string CompanyCode, int SubscriptionPlanId,
        string PlanName, DateTime StartDate, DateTime ExpiryDate, decimal Amount, string Currency, string SubscriptionStatus);
}
