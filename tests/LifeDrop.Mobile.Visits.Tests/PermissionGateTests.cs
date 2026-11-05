using LifeDrop.Mobile.Tracking;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

public class PermissionGateTests
{
    private static ShiftHealth Android(string permission = "always", bool? precise = true, bool? services = true, bool? notifications = true, bool? battery = true) =>
        new("1.0", "android", "14", "Pixel", permission, precise, services, notifications, battery, null, 50, false);

    private static ShiftHealth Ios(string permission = "always", string? refresh = "available", bool? notifications = true) =>
        new("1.0", "ios", "18", "iPhone", permission, true, true, notifications, null, refresh, 50, false);

    [Fact]
    public void EverythingAllowedMissesNothing()
    {
        Assert.Empty(PermissionGate.Missing(Android()));
        Assert.Empty(PermissionGate.Missing(Ios()));
    }

    [Theory]
    [InlineData("when_in_use")]
    [InlineData("denied")]
    [InlineData("unknown")]
    public void OnlyAllTheTimeCounts(string permission)
    {
        Assert.Equal([MissingPermission.LocationAlways], PermissionGate.Missing(Android(permission)));
        Assert.Equal([MissingPermission.LocationAlways], PermissionGate.Missing(Ios(permission)));
    }

    [Fact]
    public void EachMissingThingIsNamed()
    {
        var all = PermissionGate.Missing(Android("denied", precise: false, services: false, notifications: false, battery: false));
        Assert.Equal([MissingPermission.LocationServices, MissingPermission.LocationAlways, MissingPermission.PreciseLocation, MissingPermission.Notifications, MissingPermission.BatteryOptimization], all);
    }

    [Fact]
    public void BatterySavingIsAnAndroidMatterAndBackgroundRefreshAnIosOne()
    {
        Assert.DoesNotContain(MissingPermission.BatteryOptimization, PermissionGate.Missing(Ios()));
        Assert.Equal([MissingPermission.BackgroundRefresh], PermissionGate.Missing(Ios(refresh: "denied")));
        Assert.Equal([MissingPermission.BackgroundRefresh], PermissionGate.Missing(Ios(refresh: "restricted")));
        Assert.Empty(PermissionGate.Missing(Android(battery: true)));
    }

    [Fact]
    public void WhatThePhoneCannotTellIsNeverAProblem()
    {
        Assert.Empty(PermissionGate.Missing(Android(precise: null, services: null, notifications: null, battery: null)));
        Assert.Empty(PermissionGate.Missing(Ios(refresh: null, notifications: null)));
    }
}
