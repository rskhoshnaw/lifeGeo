using LifeDrop.Application.Customers;
using System;
using System.Linq;
using Xunit;

namespace LifeDrop.Application.Tests.Customers;

public class CustomerVisitWindowsTests
{
    private static VisitWindowDto W(int day, string opens, string closes) => new(day, TimeOnly.Parse(opens), TimeOnly.Parse(closes));

    [Fact]
    public void IsValid_AcceptsSeveralNonOverlappingWindowsOnOneDayAndAnEmptyList()
    {
        Assert.True(CustomerVisitWindows.IsValid(Array.Empty<VisitWindowDto>()));
        Assert.True(CustomerVisitWindows.IsValid(new[] { W(2, "09:00", "13:00"), W(2, "13:00", "15:00"), W(2, "17:00", "20:00"), W(0, "08:00", "12:00") }));
    }

    [Fact]
    public void IsValid_RejectsOverlapBackwardsEmptyAndOutOfRangeWindows()
    {
        Assert.False(CustomerVisitWindows.IsValid(null));
        Assert.False(CustomerVisitWindows.IsValid(new[] { W(2, "09:00", "13:00"), W(2, "12:59", "15:00") }));   // overlap
        Assert.False(CustomerVisitWindows.IsValid(new[] { W(2, "13:00", "09:00") }));                          // backwards
        Assert.False(CustomerVisitWindows.IsValid(new[] { W(2, "09:00", "09:00") }));                          // empty
        Assert.False(CustomerVisitWindows.IsValid(new[] { W(7, "09:00", "13:00") }));                          // weekday
        Assert.False(CustomerVisitWindows.IsValid(new[] { W(-1, "09:00", "13:00") }));
    }

    [Fact]
    public void IsValid_RejectsMoreThanTheMaximumNumberOfWindows()
    {
        var tooMany = Enumerable.Range(0, CustomerVisitWindows.MaxWindows + 1).Select(i => W(i % 7, $"{i / 7 * 6:00}:00", $"{i / 7 * 6 + 5:00}:00")).ToArray();
        Assert.False(CustomerVisitWindows.IsValid(tooMany));
        Assert.True(CustomerVisitWindows.IsValid(tooMany.Take(CustomerVisitWindows.MaxWindows).ToArray()));
    }

    [Fact]
    public void IsValid_AllowsTheSameHoursOnDifferentDays()
    {
        Assert.True(CustomerVisitWindows.IsValid(Enumerable.Range(0, 7).Select(d => W(d, "09:00", "13:00")).ToArray()));
    }
}
