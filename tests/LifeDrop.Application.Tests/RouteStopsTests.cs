using System;
using System.Linq;
using LifeDrop.Application.Tracking;
using Xunit;

namespace LifeDrop.Application.Tests;

// Stops in a route (plan step 2.11): at least 5 minutes within 50 metres of the first point of the stop.
public class RouteStopsTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 6, 0, 0, TimeSpan.Zero);

    // Points every 30 seconds; longitude moves by about 11 m per 0.0001 degree at this latitude.
    private static RoutePointDto P(int seconds, double lngDelta = 0, double lat = 36.19) => new(lat, 44.0 + lngDelta, 10, Start.AddSeconds(seconds), false);

    [Fact]
    public void FiveMinutesWithinFiftyMetresIsAStopAndItsPositionIsTheAverage()
    {
        var points = Enumerable.Range(0, 11).Select(i => P(i * 30, i % 2 == 0 ? 0 : 0.0002)).ToList();   // 5 minutes, about 22 m apart
        var stop = Assert.Single(RouteStops.Compute(points));
        Assert.Equal((Start, Start.AddSeconds(300)), (stop.From, stop.To));
        Assert.InRange(stop.Longitude, 44.0, 44.0002);   // the average of points at 44.0 and 44.0002
    }

    [Fact]
    public void LessThanFiveMinutesOrMovingOnIsNotAStop()
    {
        Assert.Empty(RouteStops.Compute(Enumerable.Range(0, 10).Select(i => P(i * 30)).ToList()));   // 4.5 minutes
        // Walking away: every point is more than 50 m from the previous anchor
        Assert.Empty(RouteStops.Compute(Enumerable.Range(0, 30).Select(i => P(i * 30, i * 0.001)).ToList()));
        Assert.Empty(RouteStops.Compute(Array.Empty<RoutePointDto>()));
    }

    [Fact]
    public void TwoStopsWithATripBetween()
    {
        var points = Enumerable.Range(0, 12).Select(i => P(i * 30)).ToList();                                       // stop 1: 5.5 minutes
        points.AddRange(Enumerable.Range(12, 4).Select(i => P(i * 30, (i - 11) * 0.01)));                          // driving: 1 km every 30 s
        points.AddRange(Enumerable.Range(16, 12).Select(i => P(i * 30, 0.05)));                                    // stop 2: 5.5 minutes
        var stops = RouteStops.Compute(points);
        Assert.Equal(2, stops.Count);
        Assert.True(stops[0].To < stops[1].From);
        Assert.Equal(44.0, stops[0].Longitude, 4);
        Assert.Equal(44.05, stops[1].Longitude, 4);
    }

    [Fact]
    public void DistanceIsInMeters()
    {
        Assert.Equal(0, RouteStops.DistanceMeters(36.19, 44.0, 36.19, 44.0), 3);
        Assert.InRange(RouteStops.DistanceMeters(36.0, 44.0, 37.0, 44.0), 111_000, 111_400);   // one degree of latitude
    }
}
