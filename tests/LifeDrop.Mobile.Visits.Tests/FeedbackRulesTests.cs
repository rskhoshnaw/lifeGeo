using LifeDrop.Mobile.Visits;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// What the app checks before sending feedback, and the requests it makes (plan step 5.30).
public class FeedbackRulesTests
{
    [Fact]
    public void Feedback_needs_a_known_kind_and_a_text_that_fits()
    {
        Assert.True(FeedbackRules.IsValid("suggestion", " drivers are late "));
        Assert.True(FeedbackRules.IsValid("note", new string('x', FeedbackRules.MaxBody)));
        Assert.False(FeedbackRules.IsValid("note", new string('x', FeedbackRules.MaxBody + 1)));
        Assert.False(FeedbackRules.IsValid("praise", "x"));
        Assert.False(FeedbackRules.IsValid(null, "x"));
        Assert.False(FeedbackRules.IsValid("note", "   "));
        Assert.False(FeedbackRules.IsValid("note", null));
    }

    [Theory]
    [InlineData("IMG_0001.JPG", "image/jpeg")]
    [InlineData("photo.jpeg", "image/jpeg")]
    [InlineData("shot.png", "image/png")]
    [InlineData("a.webp", "image/webp")]
    [InlineData("doc.pdf", null)]
    [InlineData("noextension", null)]
    [InlineData(null, null)]
    public void A_photo_content_type_comes_from_the_file_name(string? name, string? type) => Assert.Equal(type, FeedbackRules.PhotoContentType(name));

    [Fact]
    public void Statuses_and_kinds_map_to_text_keys_that_exist_in_every_language()
    {
        Assert.Equal("app.feedback.status.in_review", FeedbackRules.StatusKey("in_review"));
        Assert.Equal("app.feedback.status.done", FeedbackRules.StatusKey("done"));
        Assert.Equal("app.feedback.status.new", FeedbackRules.StatusKey("something"));
        Assert.Equal("app.feedback.kind.complaint", FeedbackRules.KindKey("complaint"));
        Assert.Equal("app.feedback.kind.note", FeedbackRules.KindKey("other"));
        foreach (var key in FeedbackRules.Kinds.Select(FeedbackRules.KindKey).Concat(new[] { "new", "in_review", "done" }.Select(FeedbackRules.StatusKey)))
            foreach (var (code, _) in LifeDrop.Mobile.Localization.Texts.Languages) Assert.False(string.IsNullOrWhiteSpace(LifeDrop.Mobile.Localization.Texts.Raw(code, key)), $"{key}.{code}");
    }
}
