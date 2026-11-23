using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// Everything entered for one product, opened from the gallery: big photo, name, scientific name, code, specialty, manufacturer, unit, price,
// expiry day (red when expired, orange when within 90 days), stock, availability and notes. Read only.
public sealed class ProductDetailPage : ContentPage
{
    public ProductDetailPage(ProductItem product)
    {
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.products.details");
        var today = DateOnly.FromDateTime(DateTime.Today);
        var head = new VerticalStackLayout { Spacing = 4, Padding = new Thickness(16, 14, 16, 4) };
        head.Children.Add(new Label { Text = product.DisplayName(Texts.Language), FontSize = 22, FontAttributes = FontAttributes.Bold });
        if (!string.IsNullOrWhiteSpace(product.ScientificName))
            head.Children.Add(new Label { Text = product.ScientificName, FontSize = 15, FontAttributes = FontAttributes.Italic, TextColor = Colors.Gray });
        if (!product.PricesHidden)
            head.Children.Add(new Label { Text = $"{ProductsPage.Money(product.BasePrice)} {product.Currency} / {product.Unit}", FontSize = 20, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#E52B50") });
        if (!product.Available)
            head.Children.Add(new Label { Text = Texts.Get("app.products.unavailable"), FontAttributes = FontAttributes.Bold, TextColor = Colors.OrangeRed });

        var rows = new VerticalStackLayout { Spacing = 0, Padding = new Thickness(16, 8, 16, 24) };
        Row(rows, "web.catalog.code", product.Code);
        if (!string.IsNullOrWhiteSpace(product.Specialty)) Row(rows, "web.catalog.specialty", Texts.Get("specialty." + product.Specialty));
        Row(rows, "web.catalog.manufacturer", product.Manufacturer);
        Row(rows, "web.catalog.unit", product.Unit);
        if (product.ExpiryDate is { } expiry)
        {
            var status = product.ExpiryStatusOn(today);
            var text = ProductsPage.Day(expiry) + status switch { ExpiryStatus.Expired => " · " + Texts.Get("app.products.expired"), ExpiryStatus.Soon => " · " + Texts.Get("app.products.expiresSoon"), _ => "" };
            Row(rows, "web.catalog.expiry", text, status switch { ExpiryStatus.Expired => Colors.Red, ExpiryStatus.Soon => Colors.DarkOrange, _ => null });
        }
        if (product.StockQty is { } qty)
            Row(rows, "app.products.stockQtyLabel", product.InStock ? ProductsPage.Money(qty) : Texts.Get("app.products.outOfStock"), product.InStock ? null : Colors.Gray);
        Row(rows, "web.catalog.notes", product.Notes);

        Content = new ScrollView { Content = new VerticalStackLayout { Spacing = 0, Children = { ProductsPage.Photo(product, 260), head, rows } } };
    }

    // One "label: value" line with a thin divider; empty values are left out.
    private static void Row(VerticalStackLayout host, string labelKey, string? value, Color? color = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var row = new Grid { ColumnDefinitions = { new ColumnDefinition(new GridLength(1, GridUnitType.Star)), new ColumnDefinition(new GridLength(2, GridUnitType.Star)) }, Padding = new Thickness(0, 10), ColumnSpacing = 10 };
        row.Add(new Label { Text = Texts.Get(labelKey), TextColor = Colors.Gray, FontSize = 14 }, 0);
        row.Add(new Label { Text = value, FontSize = 15, TextColor = color ?? Application.Current?.RequestedTheme switch { AppTheme.Dark => Colors.White, _ => Color.FromArgb("#0F172A") } }, 1);
        host.Children.Add(row);
        host.Children.Add(new BoxView { HeightRequest = 1, Color = Color.FromArgb("#E2E8F0") });
    }
}
