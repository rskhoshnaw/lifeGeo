using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

public class ButtonAccessTests
{
    private static readonly string[] SalesRep = ["customers.read", "customers.write", "orders.create", "visits.plan"];
    private static readonly string[] WarehouseStaff = ["warehouse.work", "orders.read"];

    [Fact]
    public void AVisitorDoesNotGetTheWarehouseOrDeliveryButtons()
    {
        var stored = ButtonAccess.Store(SalesRep);
        Assert.False(ButtonAccess.Allows(stored, "warehouse.work"));
        Assert.False(ButtonAccess.Allows(stored, "delivery.work"));
        Assert.True(ButtonAccess.Allows(stored, "orders.create"));
    }

    [Fact]
    public void WarehouseStaffGetTheWarehouseButtonOnly()
    {
        var stored = ButtonAccess.Store(WarehouseStaff);
        Assert.True(ButtonAccess.Allows(stored, "warehouse.work"));
        Assert.False(ButtonAccess.Allows(stored, "delivery.work"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AListThatWasNeverFetchedShowsEverything(string? stored) => Assert.True(ButtonAccess.Allows(stored, "warehouse.work"));

    [Fact]
    public void ThePermissionMustMatchWholly() => Assert.False(ButtonAccess.Allows("warehouse.workflow,orders.read", "warehouse.work"));
}
