using System.Net;
using System.Text;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// The area drawn on the map (plan step 1.27).
public class AreaDrawingTests
{
    private static readonly DateOnly Day = new(2026, 10, 6);

    private static AreaDrawing Triangle()
    {
        var drawing = new AreaDrawing();
        Assert.True(drawing.Add(36.18, 44.00));
        Assert.True(drawing.Add(36.18, 44.02));
        Assert.True(drawing.Add(36.20, 44.01));
        return drawing;
    }

    [Fact]
    public void ThreeCornersAreReadyTwoAreNotAndUndoAndClearWork()
    {
        var drawing = new AreaDrawing();
        Assert.False(drawing.IsReady);
        drawing.Add(36.18, 44.00);
        drawing.Add(36.18, 44.02);
        Assert.False(drawing.IsReady);
        drawing.Add(36.20, 44.01);
        Assert.True(drawing.IsReady);
        Assert.True(drawing.Undo());
        Assert.False(drawing.IsReady);
        drawing.Clear();
        Assert.Empty(drawing.Points);
        Assert.False(drawing.Undo());
    }

    [Fact]
    public void ImpossibleCornersAndMoreThanAHundredAreRefused()
    {
        var drawing = new AreaDrawing();
        Assert.False(drawing.Add(91, 44));
        Assert.False(drawing.Add(36, 181));
        Assert.False(drawing.Add(double.NaN, 44));
        for (var i = 0; i < AreaDrawing.MaxPoints; i++) Assert.True(drawing.Add(36 + i * 0.0001, 44 + (i % 2) * 0.001));
        Assert.False(drawing.Add(36.5, 44.5));
        Assert.Equal(AreaDrawing.MaxPoints, drawing.Points.Count);
    }

    [Fact]
    public void ALineASpotAndAHugeShapeAreNotValid()
    {
        Assert.False(AreaDrawing.IsValid(new[] { new AreaPoint(36.19, 44.0), new AreaPoint(36.19, 44.01), new AreaPoint(36.19, 44.02) }));   // a line
        Assert.False(AreaDrawing.IsValid(new[] { new AreaPoint(36.19, 44.0), new AreaPoint(36.19, 44.0), new AreaPoint(36.19, 44.0) }));      // a spot
        Assert.False(AreaDrawing.IsValid(new[] { new AreaPoint(36.0, 43.0), new AreaPoint(36.0, 44.0), new AreaPoint(36.5, 43.5) }));        // a degree wide
        Assert.False(AreaDrawing.IsValid(null));
        Assert.True(AreaDrawing.IsValid(Triangle().Points));
    }

    [Fact]
    public void TheMapPageAnswerIsParsedWithDotsWhateverTheLanguageAndBadTextGivesNothing()
    {
        var points = AreaDrawing.Parse("\"36.18,44.0;36.18,44.02;36.2,44.01\"");
        Assert.Equal(new[] { new AreaPoint(36.18, 44.0), new AreaPoint(36.18, 44.02), new AreaPoint(36.2, 44.01) }, points);
        Assert.Empty(AreaDrawing.Parse(";")!);
        Assert.Null(AreaDrawing.Parse(""));
        Assert.Null(AreaDrawing.Parse(null));
        Assert.Null(AreaDrawing.Parse("36,18,44"));
        Assert.Null(AreaDrawing.Parse("abc,def"));
        Assert.Null(AreaDrawing.Parse("95,44;36,44;36,45"));
    }

    [Fact]
    public void TheMapPageStartsWhereTheUserIs()
    {
        var html = AreaDrawing.MapHtml(36.1911, 44.0092, 14);
        Assert.Contains("setView([36.1911, 44.0092], 14)", html);
        Assert.DoesNotContain("__", html);
        Assert.Contains("function getPoints()", html);
    }

    [Fact]
    public async Task AnAreaAloneIsASearchAndNoValidAreaMeansNoSearch()
    {
        Assert.True(SelfPlanner.CanSearch(null, null, Triangle().Points));
        Assert.False(SelfPlanner.CanSearch(null, null, new[] { new AreaPoint(36.19, 44.0), new AreaPoint(36.19, 44.01) }));

        var api = new Capture();
        var planner = new SelfPlanner(api);
        Assert.Equal(VisitActionResult.Success, await planner.SearchAsync(Day, null, null, Triangle().Points));
        Assert.Equal(3, api.Last!.Area!.Count);
        // A broken drawing is dropped; without anything else there is nothing to search
        api.Last = null;
        Assert.Equal(VisitActionResult.InvalidData, await planner.SearchAsync(Day, null, null, new[] { new AreaPoint(36.19, 44.0) }));
        Assert.Null(api.Last);
    }

    private sealed class Capture : ISelfPlanApiClient
    {
        public SelfPlanQuery? Last;
        public Task<(VisitActionResult, IReadOnlyList<SelfPlanCandidate>)> SearchAsync(SelfPlanQuery query, CancellationToken cancellationToken = default)
        {
            Last = query;
            return Task.FromResult<(VisitActionResult, IReadOnlyList<SelfPlanCandidate>)>((VisitActionResult.Success, Array.Empty<SelfPlanCandidate>()));
        }
        public Task<(VisitActionResult, int)> AddAsync(SelfPlanQuery query, IReadOnlyList<Guid> customerIds, CancellationToken cancellationToken = default) =>
            Task.FromResult((VisitActionResult.Success, 0));
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string Body = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content?.ReadAsStringAsync(cancellationToken).Result ?? "";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"candidates":[]}""", Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Session : IAuthSession
    {
        public Task<AuthResult<string>> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(AuthResult<string>.Ok("t"));
    }

    [Fact]
    public async Task TheClientSendsTheCornersAsLatitudeAndLongitude()
    {
        var handler = new Handler();
        var client = new VisitsApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.test") }, new Session());
        await client.SearchAsync(new SelfPlanQuery(Day, null, null, Triangle().Points));
        Assert.Contains("\"area\":[{\"latitude\":36.18,\"longitude\":44},{\"latitude\":36.18,\"longitude\":44.02},{\"latitude\":36.2,\"longitude\":44.01}]", handler.Body);
        await client.SearchAsync(new SelfPlanQuery(Day, null, "ab"));
        Assert.Contains("\"area\":null", handler.Body);
    }
}
