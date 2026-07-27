using Microsoft.Data.SqlClient;
using PayNex_POS_B1.Data;
using PayNex_POS_B1.Models;

namespace PayNex_POS_B1.Repositories;

public class CompanyInformationRepository
{
    public CompanyInformation Get()
    {
        EnsureTable();
        using var con = Db.Open();
        using var cmd = new SqlCommand(@"
SELECT TOP 1 CompanyInformationId, CompanyName, ISNULL(AddressLine,'') AddressLine, ISNULL(PhoneNo,'') PhoneNo,
       ISNULL(Email,'') Email, ISNULL(Website,'') Website, ISNULL(TaxRegistrationNo,'') TaxRegistrationNo,
       ISNULL(LogoPath,'') LogoPath, LogoImage, UpdatedAt
FROM CompanyInformation
ORDER BY CompanyInformationId", con);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            return new CompanyInformation { CompanyName = AppConfig.CompanyName, AddressLine = AppConfig.StoreName };
        }

        return new CompanyInformation
        {
            CompanyInformationId = SqlMap.Int(r, "CompanyInformationId"),
            CompanyName = SqlMap.String(r, "CompanyName"),
            AddressLine = SqlMap.String(r, "AddressLine"),
            PhoneNo = SqlMap.String(r, "PhoneNo"),
            Email = SqlMap.String(r, "Email"),
            Website = SqlMap.String(r, "Website"),
            TaxRegistrationNo = SqlMap.String(r, "TaxRegistrationNo"),
            LogoPath = SqlMap.String(r, "LogoPath"),
            LogoImage = r["LogoImage"] == DBNull.Value ? null : (byte[])r["LogoImage"],
            UpdatedAt = SqlMap.DateTime(r, "UpdatedAt")
        };
    }

    public void Save(CompanyInformation info)
    {
        EnsureTable();
        using var con = Db.Open();
        using var cmd = new SqlCommand(@"
IF EXISTS (SELECT 1 FROM CompanyInformation)
BEGIN
    UPDATE CompanyInformation
    SET CompanyName=@CompanyName,
        AddressLine=@AddressLine,
        PhoneNo=@PhoneNo,
        Email=@Email,
        Website=@Website,
        TaxRegistrationNo=@TaxRegistrationNo,
        LogoPath=@LogoPath,
        LogoImage=@LogoImage,
        UpdatedAt=SYSUTCDATETIME()
    WHERE CompanyInformationId = (SELECT TOP 1 CompanyInformationId FROM CompanyInformation ORDER BY CompanyInformationId);
END
ELSE
BEGIN
    INSERT INTO CompanyInformation(CompanyName, AddressLine, PhoneNo, Email, Website, TaxRegistrationNo, LogoPath, LogoImage)
    VALUES(@CompanyName,@AddressLine,@PhoneNo,@Email,@Website,@TaxRegistrationNo,@LogoPath,@LogoImage);
END", con);

        cmd.Parameters.AddWithValue("@CompanyName", Clean(info.CompanyName, AppConfig.CompanyName));
        cmd.Parameters.AddWithValue("@AddressLine", Clean(info.AddressLine));
        cmd.Parameters.AddWithValue("@PhoneNo", Clean(info.PhoneNo));
        cmd.Parameters.AddWithValue("@Email", Clean(info.Email));
        cmd.Parameters.AddWithValue("@Website", Clean(info.Website));
        cmd.Parameters.AddWithValue("@TaxRegistrationNo", Clean(info.TaxRegistrationNo));
        cmd.Parameters.AddWithValue("@LogoPath", Clean(info.LogoPath));
        var logoParam = new SqlParameter("@LogoImage", System.Data.SqlDbType.VarBinary, -1)
        {
            Value = info.LogoImage == null || info.LogoImage.Length == 0 ? DBNull.Value : info.LogoImage
        };
        cmd.Parameters.Add(logoParam);
        cmd.ExecuteNonQuery();
    }

    private static string Clean(string? value, string fallback = "") => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static void EnsureTable()
    {
        using var con = Db.Open();
        using var cmd = new SqlCommand(@"
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
END", con);
        cmd.ExecuteNonQuery();
    }
}
