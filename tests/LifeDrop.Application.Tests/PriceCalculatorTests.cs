using LifeDrop.Application.Catalog;
using Xunit;

namespace LifeDrop.Application.Tests;

// Discounts of a customer (plan step 2.1): oldest first, each on the price left; an amount belongs to one product; never below 0.
public class PriceCalculatorTests
{
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static DiscountRow D(string kind, decimal value, int minute, Guid? product = null) =>
        new(Guid.NewGuid(), product, kind, value, Start.AddMinutes(minute));

    [Fact]
    public void NoDiscountKeepsTheListPrice() =>
        Assert.Equal((1500m, 0), (PriceCalculator.Apply(1500m, Product, Array.Empty<DiscountRow>()).Price, PriceCalculator.Apply(1500m, Product, Array.Empty<DiscountRow>()).Applied.Count));

    [Fact]
    public void DiscountsRunOldestFirstEachOnThePriceLeft()
    {
        var all10 = D("percent", 10, 0);
        var minus100 = D("amount", 100, 1, Product);
        var five = D("percent", 5, 2, Product);
        // Given out of order on purpose
        var (price, applied) = PriceCalculator.Apply(1800m, Product, new[] { five, all10, minus100 });
        Assert.Equal(1444m, price);   // 1800 - 10 % = 1620; - 100 = 1520; - 5 % = 1444
        Assert.Equal(new[] { all10.Id, minus100.Id, five.Id }, applied.Select(a => a.Id).ToArray());
        // The other order gives another price: that is why the order is fixed (5 % on 1800 = 1710; - 100 = 1610; - 10 % = 1449)
        Assert.Equal(1449m, PriceCalculator.Apply(1800m, Product, new[] { D("percent", 5, 0, Product), D("amount", 100, 1, Product), D("percent", 10, 2) }).Price);
    }

    [Fact]
    public void AnAmountOfAnotherProductAndAnAmountWithoutAProductAreIgnored()
    {
        var (price, applied) = PriceCalculator.Apply(10m, Product, new[] { D("amount", 3, 0, Guid.NewGuid()), D("amount", 3, 1, null) });
        Assert.Equal(10m, price);
        Assert.Empty(applied);
    }

    [Fact]
    public void ThePriceNeverGoesBelowZeroAndKeepsThreeDecimals()
    {
        Assert.Equal(0m, PriceCalculator.Apply(1500m, Product, new[] { D("amount", 5000, 0, Product) }).Price);
        Assert.Equal(0m, PriceCalculator.Apply(1500m, Product, new[] { D("percent", 100, 0) }).Price);
        Assert.Equal(3.333m, PriceCalculator.Apply(5m, Product, new[] { D("percent", 33.34m, 0) }).Price);   // 5 - 1.667 = 3.333
    }
}
