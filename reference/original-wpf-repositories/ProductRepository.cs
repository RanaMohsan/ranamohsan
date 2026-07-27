using Microsoft.Data.SqlClient;
using PayNex_POS_B1.Data;
using PayNex_POS_B1.Models;

namespace PayNex_POS_B1.Repositories;

public class ProductRepository
{
    public List<Product> Search(string term = "")
    {
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT TOP 500 p.ProductId, p.ProductCode, p.Barcode, p.ProductName,
       ISNULL(c.CategoryName,'') CategoryName, ISNULL(b.BrandName,'') BrandName,
       p.UnitOfMeasure, p.PurchasePrice, p.SalePrice, p.RetailPrice,
       ISNULL(t.TaxPercent,0) TaxPercent, ISNULL(t.IsInclusive,0) TaxInclusive, p.DiscountAllowed, p.MinStockLevel,
       p.ReorderLevel, p.StockOnHand, ISNULL(p.ImagePath,'') ImagePath,
       p.ProductImage, p.IsActive
FROM Products p
LEFT JOIN Categories c ON c.CategoryId = p.CategoryId
LEFT JOIN Brands b ON b.BrandId = p.BrandId
LEFT JOIN TaxGroups t ON t.TaxGroupId = p.TaxGroupId
WHERE p.IsActive = 1 AND (@Term = '' OR p.ProductName LIKE @Like OR p.Barcode LIKE @Like OR p.ProductCode LIKE @Like OR c.CategoryName LIKE @Like OR b.BrandName LIKE @Like)
ORDER BY p.ProductName";
        cmd.Parameters.AddWithValue("@Term", term.Trim());
        cmd.Parameters.AddWithValue("@Like", $"%{term.Trim()}%");
        return ReadProducts(cmd);
    }

    public Product? GetByBarcodeOrCode(string code)
    {
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT TOP 1 p.ProductId, p.ProductCode, p.Barcode, p.ProductName,
       ISNULL(c.CategoryName,'') CategoryName, ISNULL(b.BrandName,'') BrandName,
       p.UnitOfMeasure, p.PurchasePrice, p.SalePrice, p.RetailPrice,
       ISNULL(t.TaxPercent,0) TaxPercent, ISNULL(t.IsInclusive,0) TaxInclusive, p.DiscountAllowed, p.MinStockLevel,
       p.ReorderLevel, p.StockOnHand, ISNULL(p.ImagePath,'') ImagePath,
       p.ProductImage, p.IsActive
FROM Products p
LEFT JOIN Categories c ON c.CategoryId = p.CategoryId
LEFT JOIN Brands b ON b.BrandId = p.BrandId
LEFT JOIN TaxGroups t ON t.TaxGroupId = p.TaxGroupId
WHERE p.IsActive = 1 AND (p.Barcode = @Code OR p.ProductCode = @Code)";
        cmd.Parameters.AddWithValue("@Code", code.Trim());
        return ReadProducts(cmd).FirstOrDefault();
    }

    public void Upsert(Product p)
    {
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
IF EXISTS(SELECT 1 FROM Products WHERE ProductId = @ProductId)
BEGIN
    UPDATE Products SET ProductCode=@ProductCode, Barcode=@Barcode, ProductName=@ProductName,
        UnitOfMeasure=@UnitOfMeasure, PurchasePrice=@PurchasePrice, SalePrice=@SalePrice,
        RetailPrice=@RetailPrice, StockOnHand=@StockOnHand, DiscountAllowed=@DiscountAllowed,
        MinStockLevel=@MinStockLevel, ReorderLevel=@ReorderLevel, ImagePath=@ImagePath,
        ProductImage=@ProductImage, IsActive=@IsActive
    WHERE ProductId=@ProductId;
END
ELSE
BEGIN
    INSERT INTO Products(ProductCode, Barcode, ProductName, UnitOfMeasure, PurchasePrice, SalePrice, RetailPrice, TaxGroupId, DiscountAllowed, MinStockLevel, ReorderLevel, StockOnHand, ImagePath, ProductImage, IsActive)
    VALUES(@ProductCode, @Barcode, @ProductName, @UnitOfMeasure, @PurchasePrice, @SalePrice, @RetailPrice, 1, @DiscountAllowed, @MinStockLevel, @ReorderLevel, @StockOnHand, @ImagePath, @ProductImage, @IsActive);
END";
        cmd.Parameters.AddWithValue("@ProductId", p.ProductId);
        cmd.Parameters.AddWithValue("@ProductCode", p.ProductCode);
        cmd.Parameters.AddWithValue("@Barcode", p.Barcode);
        cmd.Parameters.AddWithValue("@ProductName", p.ProductName);
        cmd.Parameters.AddWithValue("@UnitOfMeasure", p.UnitOfMeasure);
        cmd.Parameters.AddWithValue("@PurchasePrice", p.PurchasePrice);
        cmd.Parameters.AddWithValue("@SalePrice", p.SalePrice);
        cmd.Parameters.AddWithValue("@RetailPrice", p.RetailPrice);
        cmd.Parameters.AddWithValue("@StockOnHand", p.StockOnHand);
        cmd.Parameters.AddWithValue("@DiscountAllowed", p.DiscountAllowed);
        cmd.Parameters.AddWithValue("@MinStockLevel", p.MinStockLevel);
        cmd.Parameters.AddWithValue("@ReorderLevel", p.ReorderLevel);
        cmd.Parameters.AddWithValue("@ImagePath", string.IsNullOrWhiteSpace(p.ImagePath) ? (object)DBNull.Value : p.ImagePath);
        cmd.Parameters.Add("@ProductImage", System.Data.SqlDbType.VarBinary, -1).Value = p.ProductImage == null || p.ProductImage.Length == 0 ? (object)DBNull.Value : p.ProductImage;
        cmd.Parameters.AddWithValue("@IsActive", p.IsActive);
        cmd.ExecuteNonQuery();
    }

    private static List<Product> ReadProducts(SqlCommand cmd)
    {
        var list = new List<Product>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Product
            {
                ProductId = SqlMap.Int(r, "ProductId"),
                ProductCode = SqlMap.String(r, "ProductCode"),
                Barcode = SqlMap.String(r, "Barcode"),
                ProductName = SqlMap.String(r, "ProductName"),
                CategoryName = SqlMap.String(r, "CategoryName"),
                BrandName = SqlMap.String(r, "BrandName"),
                UnitOfMeasure = SqlMap.String(r, "UnitOfMeasure"),
                PurchasePrice = SqlMap.Decimal(r, "PurchasePrice"),
                SalePrice = SqlMap.Decimal(r, "SalePrice"),
                RetailPrice = SqlMap.Decimal(r, "RetailPrice"),
                TaxPercent = SqlMap.Decimal(r, "TaxPercent"),
                TaxInclusive = SqlMap.Bool(r, "TaxInclusive"),
                DiscountAllowed = SqlMap.Bool(r, "DiscountAllowed"),
                MinStockLevel = SqlMap.Decimal(r, "MinStockLevel"),
                ReorderLevel = SqlMap.Decimal(r, "ReorderLevel"),
                StockOnHand = SqlMap.Decimal(r, "StockOnHand"),
                ImagePath = SqlMap.String(r, "ImagePath"),
                ProductImage = r["ProductImage"] == DBNull.Value ? null : (byte[])r["ProductImage"],
                IsActive = SqlMap.Bool(r, "IsActive")
            });
        }
        return list;
    }
}
