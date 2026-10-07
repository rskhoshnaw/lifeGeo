using System.Net;
using System.Text;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// The extra visit-form fields on the phone (plan step 3.2, part 2).
public class CustomFieldsTests
{
    private static CustomField Field(string key, string type, bool required = false, params string[] options) =>
        new(Guid.NewGuid(), key, type, "all", $"Label {key}", null, null, null, options, required, 0, true);

    [Fact]
    public void TheLabelFollowsTheAppLanguageWithEnglishAndTheKeyAsFallbacks()
    {
        var field = new CustomField(Guid.NewGuid(), "chairs", "number", "doctor", "Chairs", "کورسی", null, null, [], false, 0, true);
        Assert.Equal("کورسی", field.LabelFor("ckb"));
        Assert.Equal("Chairs", field.LabelFor("ar"));      // no Arabic label: English
        Assert.Equal("Chairs", field.LabelFor("en"));
        var onlyKurdish = field with { LabelEn = null };
        Assert.Equal("کورسی", onlyKurdish.LabelFor("en"));   // any label the manager wrote
        Assert.Equal("chairs", new CustomField(Guid.NewGuid(), "chairs", "text", "all", null, null, null, null, [], false, 0, true).LabelFor("fa"));
    }

    [Theory]
    [InlineData("4", "4")]
    [InlineData("4.50", "4.5")]
    [InlineData("٤٫٥", "4.5")]      // Arabic-Indic digits and decimal mark
    [InlineData("1,5", "1.5")]
    [InlineData("1.2345", null)]
    [InlineData("abc", null)]
    [InlineData("1000000000000", null)]
    public void NumbersAreNormalisedLikeTheServerDoes(string text, string? expected) => Assert.Equal(expected, CustomAnswersDraft.Normalise(Field("n", "number"), text));

    [Fact]
    public void ChoicesYesNoDatesAndTextFollowTheirTypes()
    {
        Assert.Equal("good", CustomAnswersDraft.Normalise(Field("c", "choice", false, "good", "bad"), " good "));
        Assert.Null(CustomAnswersDraft.Normalise(Field("c", "choice", false, "good", "bad"), "Good"));
        Assert.Equal("true", CustomAnswersDraft.Normalise(Field("y", "yes_no"), "true"));
        Assert.Null(CustomAnswersDraft.Normalise(Field("y", "yes_no"), "yes"));
        Assert.Equal("2026-10-05", CustomAnswersDraft.Normalise(Field("d", "date"), "2026-10-05"));
        Assert.Equal("2026-10-05", CustomAnswersDraft.Normalise(Field("d", "date"), "٢٠٢٦-١٠-٠٥"));
        Assert.Null(CustomAnswersDraft.Normalise(Field("d", "date"), "05/10/2026"));
        Assert.Null(CustomAnswersDraft.Normalise(Field("d", "date"), "2026-02-30"));
        Assert.Equal(new string('x', 1000), CustomAnswersDraft.Normalise(Field("t", "text"), new string('x', 1000)));
        Assert.Null(CustomAnswersDraft.Normalise(Field("t", "text"), new string('x', 1001)));
    }

    [Fact]
    public void RequiredFieldsMustBeAnsweredAndABlankTextRemovesAnAnswer()
    {
        var chairs = Field("chairs", "number", true);
        var notes = Field("notes", "text");
        var draft = new CustomAnswersDraft([chairs, notes]);
        Assert.False(draft.CanSave);                       // chairs is required
        draft.Set(chairs.Id, "4");
        Assert.True(draft.CanSave);
        draft.Set(notes.Id, "abc");
        Assert.True(draft.CanSave);
        draft.Set(chairs.Id, "four");
        Assert.False(draft.CanSave);                       // not a number
        Assert.False(draft.IsValid(chairs));
        draft.Set(chairs.Id, "   ");
        Assert.True(draft.IsMissing(chairs));
        draft.Set(chairs.Id, "5");
        draft.Set(notes.Id, "");
        Assert.Equal([(chairs.Id, "5")], draft.ToAnswers());
    }

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
    private static CustomFieldsApiClient Client(Handler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://api.test") }, new Session());

    [Fact]
    public async Task FieldsAreReadForTheVisitTypeAndAnswersAreSentAsStrings()
    {
        var id = Guid.NewGuid();
        var list = new Handler(_ => Json(HttpStatusCode.OK, $"[{{\"id\":\"{id}\",\"key\":\"chairs\",\"type\":\"number\",\"appliesTo\":\"doctor\",\"labelEn\":\"Chairs\",\"labelCkb\":null,\"labelAr\":null,\"labelFa\":null,\"options\":[],\"isRequired\":true,\"sortOrder\":0,\"isActive\":true}}]"));
        var (result, fields) = await Client(list).GetFieldsAsync("doctor");
        Assert.Equal(VisitActionResult.Success, result);
        Assert.Equal("/form-fields?visitType=doctor", list.Last!.RequestUri!.PathAndQuery);
        Assert.Equal(("chairs", "number", true), (fields[0].Key, fields[0].Type, fields[0].IsRequired));

        var visit = Guid.NewGuid();
        var draft = new CustomAnswersDraft(fields);
        draft.Set(id, "٤");
        var save = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        Assert.Equal(VisitActionResult.Success, await Client(save).SaveAsync(visit, draft));
        Assert.Equal(HttpMethod.Put, save.Last!.Method);
        Assert.Equal($"/visits/{visit}/report/custom", save.Last.RequestUri!.AbsolutePath);
        using var doc = System.Text.Json.JsonDocument.Parse(save.Body);
        var answer = doc.RootElement.GetProperty("answers")[0];
        Assert.Equal((id, "4"), (answer.GetProperty("fieldId").GetGuid(), answer.GetProperty("value").GetString()));

        var untouched = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        Assert.Equal(VisitActionResult.InvalidData, await Client(untouched).SaveAsync(visit, new CustomAnswersDraft(fields)));   // the required field is empty
        Assert.Null(untouched.Last);
    }

    [Fact]
    public async Task SavedAnswersAreReadAndNothingSavedIsNotFound()
    {
        var id = Guid.NewGuid();
        var visit = Guid.NewGuid();
        var read = new Handler(_ => Json(HttpStatusCode.OK, $"{{\"visitId\":\"{visit}\",\"answers\":[{{\"fieldId\":\"{id}\",\"key\":\"chairs\",\"value\":\"5\"}}],\"updatedAt\":\"2026-10-05T06:00:00Z\"}}"));
        var (result, answers) = await Client(read).GetAnswersAsync(visit);
        Assert.Equal(VisitActionResult.Success, result);
        Assert.Equal("5", answers[id]);
        var (missing, none) = await Client(new Handler(_ => Json(HttpStatusCode.NotFound, "{\"code\":\"NOT_FOUND\"}"))).GetAnswersAsync(visit);
        Assert.Equal(VisitActionResult.NotFound, missing);
        Assert.Empty(none);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, VisitActionResult.InvalidData)]
    [InlineData(HttpStatusCode.Forbidden, VisitActionResult.Forbidden)]
    [InlineData(HttpStatusCode.Conflict, VisitActionResult.Conflict)]
    public async Task TheClientMapsTheServersRefusals(HttpStatusCode status, VisitActionResult expected)
    {
        var field = Field("notes", "text");
        var draft = new CustomAnswersDraft([field]);
        draft.Set(field.Id, "x");
        Assert.Equal(expected, await Client(new Handler(_ => Json(status, "{\"code\":\"X\"}"))).SaveAsync(Guid.NewGuid(), draft));
    }
}
