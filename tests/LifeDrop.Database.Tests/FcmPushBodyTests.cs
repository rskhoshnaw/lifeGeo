using System.Text.Json;
using LifeDrop.Application.Push;
using LifeDrop.Infrastructure.Push;
using Xunit;

namespace LifeDrop.Database.Tests;

public sealed class FcmPushBodyTests
{
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Known_kind_carries_the_data_and_an_iphone_alert_key()
    {
        using var doc = JsonDocument.Parse(FcmPushSender.BuildBody("tok", new PushMessage(Id, "customer.profile_changed", "c1")));
        var message = doc.RootElement.GetProperty("message");
        Assert.Equal("customer.profile_changed", message.GetProperty("data").GetProperty("kind").GetString());
        Assert.Equal("HIGH", message.GetProperty("android").GetProperty("priority").GetString());
        var aps = message.GetProperty("apns").GetProperty("payload").GetProperty("aps");
        Assert.Equal("push_customer_profile_changed", aps.GetProperty("alert").GetProperty("loc-key").GetString());
        Assert.Equal("10", message.GetProperty("apns").GetProperty("headers").GetProperty("apns-priority").GetString());
    }

    [Fact]
    public void A_managers_message_carries_its_text_in_the_data_and_in_the_iphone_alert()
    {
        using var doc = JsonDocument.Parse(FcmPushSender.BuildBody("tok", new PushMessage(Id, "manager.message", "Meeting at 9")));
        var message = doc.RootElement.GetProperty("message");
        Assert.Equal("Meeting at 9", message.GetProperty("data").GetProperty("entityId").GetString());
        Assert.Equal("Meeting at 9", message.GetProperty("apns").GetProperty("payload").GetProperty("aps").GetProperty("alert").GetProperty("body").GetString());
    }

    [Fact]
    public void Unknown_kind_stays_data_only()
    {
        using var doc = JsonDocument.Parse(FcmPushSender.BuildBody("tok", new PushMessage(Id, "something.else", "x")));
        Assert.False(doc.RootElement.GetProperty("message").TryGetProperty("apns", out _));
    }
}
