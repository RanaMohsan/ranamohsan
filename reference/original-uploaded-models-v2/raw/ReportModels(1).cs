using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PayNex_POS_B1.Models;

public class Product
{
    public int ProductId { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string Barcode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public string BrandName { get; set; } = string.Empty;
    public string UnitOfMeasure { get; set; } = "PCS";
    public decimal PurchasePrice { get; set; }
    public decimal SalePrice { get; set; }
    public decimal RetailPrice { get; set; }
    public decimal TaxPercent { get; set; }
    public bool TaxInclusive { get; set; }
    public bool DiscountAllowed { get; set; }
    public decimal MinStockLevel { get; set; }
    public decimal ReorderLevel { get; set; }
    public decimal StockOnHand { get; set; }
    public string ImagePath { get; set; } = string.Empty;
    public byte[]? ProductImage { get; set; }
    public bool IsActive { get; set; } = true;

    public bool HasImage => (ProductImage != null && ProductImage.Length > 0) || !string.IsNullOrWhiteSpace(ImagePath);

    public ImageSource? ProductImageSource
    {
        get
        {
            try
            {
                if (ProductImage != null && ProductImage.Length > 0)
                {
                    var bitmap = new BitmapImage();
                    using var ms = new MemoryStream(ProductImage);
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = ms;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }

                if (!string.IsNullOrWhiteSpace(ImagePath) && File.Exists(ImagePath))
                {
                    var bitmap = new BitmapImage(new Uri(ImagePath, UriKind.Absolute));
                    bitmap.Freeze();
                    return bitmap;
                }
            }
            catch
            {
                // ignore image load errors
            }

            return null;
        }
    }
}
