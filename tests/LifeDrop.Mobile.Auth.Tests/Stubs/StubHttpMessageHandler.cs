using System.Net;
using System.Text;

namespace LifeDrop.Mobile.Auth.Tests.Stubs;

public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handlerFunc;

    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handlerFunc)
    {
        _handlerFunc = handlerFunc ?? throw new ArgumentNullException(nameof(handlerFunc));
    }

    public static StubHttpMessageHandler FromResponse(HttpStatusCode statusCode, string jsonContent = "", string mediaType = "application/json")
    {
        return new StubHttpMessageHandler(req =>
        {
            var response = new HttpResponseMessage(statusCode);
            if (!string.IsNullOrEmpty(jsonContent))
            {
                response.Content = new StringContent(jsonContent, Encoding.UTF8, mediaType);
            }
            return response;
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (request.Content is not null)
        {
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        return _handlerFunc(request);
    }
}
