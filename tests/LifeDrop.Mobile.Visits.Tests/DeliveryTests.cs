using System.Net;
using System.Text;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// Delivery by the driver from the phone (plan step 2.4).
public class DeliveryTests
{
    private static DeliveryDetail Order(string status = "on_the_way") => new(Guid.NewGuid(), "O-000001", status, "Shifa", "0750", "Ankawa", 36.19, 44.0, null, null, null, new[]
    {
        new DeliveryLine(1, "PAR-500", "Paracetamol", "box", 10m, null, null),
        new DeliveryLine(2, "AMX-250", "Amoxicillin", "box", 3m, null, null)
    });

    [Fact]
    public void DeliveringNeedsTheReceiverAndAReasonForEveryShortLine()
    {
        var draft = new DeliveryDraft(Order());
        Assert.False(draft.CanDeliver);                  // nobody named
        draft.ReceiverName = "  Ali ";
        Assert.True(draft.CanDeliver);                   // as prepared
        Assert.Equal(new[] { 10m, 3m }, draft.Lines.Select(l => l.Quantity).ToArray());

        Assert.True(draft.SetQuantity(2, "2"));
        Assert.False(draft.CanDeliver);                  // short, no reason
        draft.SetReason(2, " customer refused one ");
        Assert.True(draft.CanDeliver);
        Assert.Equal(new DeliveredLineInput(2, 2m, "customer refused one"), draft.Lines[1]);
        Assert.True(draft.SetQuantity(2, ""));           // back to as prepared: the reason is dropped
        Assert.Null(draft.Lines[1].Reason);
    }

    [Fact]
    public void MoreThanPreparedIsRefusedAndNothingDeliveredIsAFailureNotADelivery()
    {
        var draft = new DeliveryDraft(Order()) { ReceiverName = "Ali" };
        Assert.False(draft.SetQuantity(1, "11"));
        Assert.False(draft.SetQuantity(1, "abc"));
        Assert.Equal(10m, draft.QuantityOf(1));
        Assert.True(draft.SetQuantity(1, "٠"));          // Arabic-Indic 0
        Assert.True(draft.SetQuantity(2, "0"));
        draft.SetReason(1, "closed");
        draft.SetReason(2, "closed");
        Assert.False(draft.CanDeliver);                  // not a single item handed over: use "not delivered"
    }

    [Theory]
    [InlineData("handed_over")]
    [InlineData("picked_up")]
    [InlineData("delivered")]
    public void OnlyAnOrderOnTheWayCanBeDelivered(string status) => Assert.False(new DeliveryDraft(Order(status)) { ReceiverName = "Ali" }.CanDeliver);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string Body = "";
        public HttpRequestMessage? Last;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            Body = request.Content?.ReadAsStringAsync(cancellationToken).Result ?? "";
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Session : IAuthSession
    {
        public Task<AuthResult<string>> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(AuthResult<string>.Ok("t"));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static DeliveryApiClient Client(Handler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://api.test") }, new Session());

    [Fact]
    public async Task TheClientReadsTheDeliveryAndSendsTheStepsWithTheEvidence()
    {
        var id = Guid.NewGuid();
        var detail = new Handler(_ => Json(HttpStatusCode.OK, $$$"""{"id":"{{{id}}}","number":"O-000001","customerName":"Shifa","status":"on_the_way","createdAt":"2026-10-06T08:00:00+00:00","items":[{"lineNo":1,"productCode":"P1","productName":"Paracetamol","unit":"box","quantity":10,"preparedQuantity":8,"deliveredQuantity":null}],"delivery":{"phone":"0750","address":"Ankawa","latitude":36.19,"longitude":44.0}}"""));
        var (result, order) = await Client(detail).GetOrderAsync(id);
        Assert.Equal(VisitActionResult.Success, result);
        Assert.Equal(("0750", 8m, 36.19), (order!.Phone, order.Lines[0].Prepared, order.Latitude));   // what the warehouse prepared is the baseline
        Assert.Equal($"/deliveries/{id}", detail.Last!.RequestUri!.AbsolutePath);

        var step = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var location = new PhoneLocation(36.1912, 44.0093, 8, false);
        Assert.Equal(VisitActionResult.Success, await Client(step).PickUpAsync(id, location));
        Assert.Equal($"/deliveries/{id}/pickup", step.Last!.RequestUri!.AbsolutePath);
        Assert.Contains("\"location\":{\"latitude\":36.1912,\"longitude\":44.0093,\"accuracyMeters\":8,\"isMocked\":false}", step.Body);
        Assert.Equal(VisitActionResult.Success, await Client(step).DepartAsync(id, null));          // no position: still sent
        Assert.DoesNotContain("latitude", step.Body);
        Assert.Equal(VisitActionResult.Success, await Client(step).DeliverAsync(id, "Ali", null, new[] { new DeliveredLineInput(1, 8m, null) }, location));
        Assert.Contains("\"receiverName\":\"Ali\"", step.Body);
        Assert.Contains("\"lines\":[{\"lineNo\":1,\"quantity\":8,\"reason\":null}]", step.Body);
        Assert.Equal(VisitActionResult.Success, await Client(step).FailAsync(id, "closed", null));
        Assert.Contains("\"reason\":\"closed\"", step.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, "GPS_MOCKED", VisitActionResult.GpsMocked)]
    [InlineData(HttpStatusCode.Conflict, "CONFLICT", VisitActionResult.Conflict)]
    [InlineData(HttpStatusCode.BadRequest, "INVALID_DATA", VisitActionResult.InvalidData)]
    [InlineData(HttpStatusCode.NotFound, "NOT_FOUND", VisitActionResult.NotFound)]
    [InlineData(HttpStatusCode.Forbidden, "FORBIDDEN", VisitActionResult.Forbidden)]
    public async Task TheClientMapsTheServersAnswers(HttpStatusCode status, string code, VisitActionResult expected)
    {
        var client = Client(new Handler(_ => Json(status, $"{{\"code\":\"{code}\"}}")));
        Assert.Equal(expected, await client.PickUpAsync(Guid.NewGuid(), null));
        Assert.Equal(expected, (await client.GetOrdersAsync(null)).Result);
    }

    [Fact]
    public void ThePhoneLocationComesFromAFixOrIsAbsent()
    {
        Assert.Null(PhoneLocation.From(null));
        Assert.Equal(new PhoneLocation(36.1, 44.2, 9, true), PhoneLocation.From(new DeviceFix(36.1, 44.2, 9, DateTimeOffset.UtcNow, true)));
    }
}
