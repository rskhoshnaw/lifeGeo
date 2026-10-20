using System.Net;
using System.Text;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

public class CustomersApiClientTests
{
    private static readonly Guid CustomerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CityId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static (CustomersApiClient Client, Recorder Recorder) Create(Func<HttpRequestMessage, string, HttpResponseMessage> respond, AuthResult<string>? token = null)
    {
        var recorder = new Recorder(respond);
        var http = new HttpClient(recorder) { BaseAddress = new Uri("https://api.test") };
        return (new CustomersApiClient(http, new FixedSession(token ?? AuthResult<string>.Ok("access-1"))), recorder);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task GetCustomer_ReadsContactDetailsAndThePlacePath()
    {
        var (client, recorder) = Create((_, _) => Json(HttpStatusCode.OK, $$"""
            {"id":"{{CustomerId}}","type":"Pharmacy","code":"P-001","name":"Shifa","phone":"0750","address":"By the bridge","placeId":"{{CityId}}",
             "placePath":[{"id":"{{CityId}}","kind":"city","name":"Erbil"}],"approvalStatus":"approved"}
            """));

        var (result, customer) = await client.GetCustomerAsync(CustomerId);

        Assert.Equal(VisitActionResult.Success, result);
        Assert.Equal(("P-001", "Shifa", "0750", "By the bridge", CityId), (customer!.Code, customer.Name, customer.Phone, customer.Address, customer.PlaceId));
        Assert.Equal(new[] { ("city", "Erbil") }, customer.PlacePath!.Select(p => (p.Kind, p.Name)).ToArray());
        Assert.Equal(($"/customers/{CustomerId}", "Bearer access-1"), (recorder.Last!.RequestUri!.AbsolutePath, recorder.Last.Headers.Authorization!.ToString()));
    }

    [Fact]
    public async Task SetDoctor_SendsTheDetailsToTheDoctorRouteAndMyPermissionsAreRead()
    {
        var (client, recorder) = Create((_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));
        var result = await client.SetDoctorAsync(CustomerId, new DoctorDetails("Azadi", "ent", true, null, "A+"));
        Assert.Equal(VisitActionResult.Success, result);
        Assert.Equal(($"/customers/{CustomerId}/doctor", HttpMethod.Put), (recorder.Last!.RequestUri!.AbsolutePath, recorder.Last.Method));
        Assert.Contains("\"clinicName\":\"Azadi\"", recorder.LastBody);
        Assert.Contains("\"customerClass\":\"A\\u002B\"", recorder.LastBody);   // the encoder writes + as \u002B; the server reads it back as +

        var (me, meRecorder) = Create((_, _) => Json(HttpStatusCode.OK, """{"id":"u1","email":"rep@t1","roles":["SalesRep"],"permissions":["customers.read","customers.write"]}"""));
        var (permissionsResult, permissions) = await me.GetMyPermissionsAsync();
        Assert.Equal(VisitActionResult.Success, permissionsResult);
        Assert.Equal(new[] { "customers.read", "customers.write" }, permissions.ToArray());
        Assert.Equal("/me", meRecorder.Last!.RequestUri!.AbsolutePath);
        Assert.Equal(VisitActionResult.ServerError, (await Create((_, _) => Json(HttpStatusCode.OK, "{}")).Client.GetMyPermissionsAsync()).Result);
    }

    [Fact]
    public async Task GetCustomer_WorksWithoutProfileFieldsAndRefusesABrokenAnswer()
    {
        var (client, _) = Create((_, _) => Json(HttpStatusCode.OK, $$"""{"id":"{{CustomerId}}","type":"Doctor","code":"D-1","name":"Dr"}"""));
        var (_, customer) = await client.GetCustomerAsync(CustomerId);
        Assert.Equal((null, null, null), (customer!.Phone, customer.PlaceId, customer.PlacePath));

        var (broken, _) = Create((_, _) => Json(HttpStatusCode.OK, "{}"));
        Assert.Equal((VisitActionResult.ServerError, null), await broken.GetCustomerAsync(CustomerId));
        var (notJson, _) = Create((_, _) => Json(HttpStatusCode.OK, "<html>"));
        Assert.Equal(VisitActionResult.ServerError, (await notJson.GetCustomerAsync(CustomerId)).Result);
    }

    [Fact]
    public async Task SetProfile_SendsPhoneAddressAndPlaceAndNullsClearThem()
    {
        var (client, recorder) = Create((_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));

        Assert.Equal(VisitActionResult.Success, await client.SetProfileAsync(CustomerId, "0750", "Near", CityId));
        Assert.Equal((HttpMethod.Put, $"/customers/{CustomerId}/profile"), (recorder.Last!.Method, recorder.Last.RequestUri!.AbsolutePath));
        Assert.Equal($$"""{"phone":"0750","address":"Near","placeId":"{{CityId}}"}""", recorder.LastBody);

        await client.SetProfileAsync(CustomerId, null, null, null);
        Assert.Equal("""{"phone":null,"address":null,"placeId":null}""", recorder.LastBody);
    }

    [Fact]
    public async Task GetPlaces_ListsCitiesWithoutAParentAndTheChildrenOfAPlace()
    {
        var (client, recorder) = Create((_, _) => Json(HttpStatusCode.OK, $$"""
            [{"id":"{{CityId}}","parentId":null,"kind":"city","name":"Erbil","nameCkb":"هەولێر","nameAr":null,"nameEn":"Erbil","status":"active","mergedInto":null,"childCount":3}]
            """));

        var (result, cities) = await client.GetPlacesAsync(null);
        Assert.Equal(VisitActionResult.Success, result);
        Assert.Equal("/places?kind=city&limit=2000", recorder.Last!.RequestUri!.PathAndQuery);
        var city = Assert.Single(cities);
        Assert.Equal(("Erbil", 3, "هەولێر", "Erbil"), (city.Name, city.ChildCount, city.DisplayName("ckb"), city.DisplayName("en")));
        Assert.Equal("Erbil", city.DisplayName("ar"));   // no Arabic name: the main name

        await client.GetPlacesAsync(CityId);
        Assert.Equal($"/places?parentId={CityId}&limit=2000", recorder.Last.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task GetPlaces_RefusesRowsWithoutAnIdOrName()
    {
        var (client, _) = Create((_, _) => Json(HttpStatusCode.OK, $$"""[{"id":"{{CityId}}","kind":"city","name":" "}]"""));
        Assert.Equal((VisitActionResult.ServerError, 0), ((await client.GetPlacesAsync(null)).Result, (await client.GetPlacesAsync(null)).Places.Count));
    }

    [Fact]
    public async Task CreatePlace_SendsParentKindAndNameAndReturnsTheNewId()
    {
        var (client, recorder) = Create((_, _) => Json(HttpStatusCode.Created, $$"""{"id":"{{CustomerId}}"}"""));

        var (result, id) = await client.CreatePlaceAsync(CityId, "neighbourhood", "Ankawa");

        Assert.Equal((VisitActionResult.Success, (Guid?)CustomerId), (result, id));
        Assert.Equal((HttpMethod.Post, "/places"), (recorder.Last!.Method, recorder.Last.RequestUri!.AbsolutePath));
        Assert.Equal($$"""{"parentId":"{{CityId}}","kind":"neighbourhood","name":"Ankawa"}""", recorder.LastBody);
    }

    [Fact]
    public async Task CreateCustomer_ReadsTheBareIdAndSendsTypeCodeAndName()
    {
        var (client, recorder) = Create((_, _) => Json(HttpStatusCode.Created, $"\"{CustomerId}\""));

        var (result, id) = await client.CreateCustomerAsync("Pharmacy", "N261005-AB23", "Shifa");

        Assert.Equal((VisitActionResult.Success, (Guid?)CustomerId), (result, id));
        Assert.Equal("""{"type":"Pharmacy","code":"N261005-AB23","name":"Shifa"}""", recorder.LastBody);
    }

    [Fact]
    public async Task SetLocation_SendsAGpsPositionWithItsAccuracy()
    {
        var (client, recorder) = Create((_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));

        Assert.Equal(VisitActionResult.Success, await client.SetLocationAsync(CustomerId, new DeviceFix(36.19, 44.01, 8.5, DateTimeOffset.UtcNow, false)));

        Assert.Equal((HttpMethod.Put, $"/customers/{CustomerId}/location"), (recorder.Last!.Method, recorder.Last.RequestUri!.AbsolutePath));
        Assert.Equal("""{"latitude":36.19,"longitude":44.01,"source":"gps","accuracyMeters":8.5}""", recorder.LastBody);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, VisitActionResult.SessionExpired)]
    [InlineData(HttpStatusCode.Forbidden, VisitActionResult.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, VisitActionResult.NotFound)]
    [InlineData(HttpStatusCode.BadRequest, VisitActionResult.InvalidData)]
    [InlineData(HttpStatusCode.Conflict, VisitActionResult.Conflict)]
    [InlineData(HttpStatusCode.InternalServerError, VisitActionResult.ServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, VisitActionResult.ServerError)]
    public async Task EveryStatusHasItsOwnResult(HttpStatusCode status, VisitActionResult expected)
    {
        var (client, _) = Create((_, _) => Json(status, """{"code":"X"}"""));
        Assert.Equal(expected, await client.SetProfileAsync(CustomerId, "1", null, null));
        Assert.Equal(expected, (await client.CreateCustomerAsync("Pharmacy", "N1", "x")).Result);
        Assert.Equal(expected, (await client.CreatePlaceAsync(CityId, "street", "x")).Result);
    }

    [Fact]
    public async Task NoTokenOrNoConnectionAreReportedAsSuchAndNothingIsSent()
    {
        var (expired, recorder) = Create((_, _) => throw new InvalidOperationException("must not be called"), AuthResult<string>.Unauthorized());
        Assert.Equal(VisitActionResult.SessionExpired, await expired.SetProfileAsync(CustomerId, "1", null, null));
        Assert.Null(recorder.Last);

        var (offline, _) = Create((_, _) => throw new HttpRequestException("no network"));
        Assert.Equal(VisitActionResult.NetworkError, await offline.SetProfileAsync(CustomerId, "1", null, null));
        Assert.Equal(VisitActionResult.NetworkError, (await offline.GetPlacesAsync(null)).Result);
        Assert.Equal(VisitActionResult.NetworkError, (await offline.GetCustomerAsync(CustomerId)).Result);

        var (noNetworkForToken, _) = Create((_, _) => Json(HttpStatusCode.OK, "[]"), AuthResult<string>.NetworkOrServerError("offline"));
        Assert.Equal(VisitActionResult.NetworkError, await noNetworkForToken.SetProfileAsync(CustomerId, "1", null, null));
    }

    private sealed class FixedSession(AuthResult<string> token) : IAuthSession
    {
        public Task<AuthResult<string>> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(token);
    }

    private sealed class Recorder(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        public string LastBody { get; private set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Last = request;
            LastBody = request.Content is null ? "" : request.Content.ReadAsStringAsync(cancellationToken).Result;
            return Task.FromResult(respond(request, LastBody));
        }
    }
}
