using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// Marketing messages (plan step 5.7): make a campaign from a template (all the customers you can see that have a phone, or one customer type) and open its queue. Online only.
public sealed class MarketingPage : ContentPage
{
    private static readonly string[] Types = ["", "Pharmacy", "MedicalAssistant", "Doctor", "Wholesaler", "Other"];

    private readonly Entry _name = new() { MaxLength = 100 };
    private readonly ChoiceField _template = new(Texts.Get("app.marketing.template"));
    private readonly ChoiceField _type = new(Texts.Get("app.customer.type"));
    private readonly Button _create = new();
    private readonly VerticalStackLayout _list = new() { Spacing = 8 };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private IReadOnlyList<MarketingTemplateItem> _templates = [];
    private IReadOnlyList<MarketingCampaignItem> _campaigns = [];
    private bool _busy;

    public MarketingPage()
    {
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.marketing.title");
        _name.Placeholder = Texts.Get("app.marketing.name");
        _create.Text = Texts.Get("app.marketing.create");
        _create.Clicked += async (_, _) => await CreateAsync();
        _type.Items = Types.Select(t => t == "" ? Texts.Get("app.marketing.allTypes") : t).ToList();
        Content = new ScrollView { Content = new VerticalStackLayout { Padding = 12, Spacing = 8, Children = { _name, _template, _type, _create, _spinner, _message, _list } } };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (TrackingHost.MarketingApi is not { } api)
        {
            AppLanguage.ShowRoot(new MainPage());
            return;
        }
        var status = await RunAsync(async c =>
        {
            var (templateResult, templates) = await api.GetTemplatesAsync(c);
            if (templateResult != VisitActionResult.Success) return templateResult;
            var (campaignResult, campaigns) = await api.GetCampaignsAsync(c);
            if (campaignResult == VisitActionResult.Success) (_templates, _campaigns) = (templates, campaigns);
            return campaignResult;
        });
        if (status != VisitActionResult.Success) return;
        _template.Items = _templates.Select(t => t.Name).ToList();
        _list.Children.Clear();
        foreach (var campaign in _campaigns.Where(c => c.Delivery != "api"))   // the server sends the api campaigns; they are followed in the panel
        {
            var open = new Button { Text = Texts.Get("app.marketing.queue") };
            var id = campaign.Id;
            open.Clicked += async (_, _) => await Navigation.PushAsync(new MarketingQueuePage(id));
            _list.Children.Add(new Border
            {
                Padding = 10,
                Content = new VerticalStackLayout
                {
                    Spacing = 4,
                    Children = { new Label { Text = campaign.Name, FontAttributes = FontAttributes.Bold }, new Label { Text = Texts.Get("app.marketing.counts", ("queued", campaign.Queued.ToString()), ("sent", campaign.Sent.ToString()), ("skipped", campaign.Skipped.ToString())), FontSize = 12 }, open }
                }
            });
        }
        Show(_templates.Count == 0 ? Texts.Get("app.marketing.noTemplates") : _campaigns.Count == 0 ? Texts.Get("app.marketing.noCampaigns") : null);
    }

    private async Task CreateAsync()
    {
        if (TrackingHost.MarketingApi is not { } api) return;
        if (string.IsNullOrWhiteSpace(_name.Text) || _template.SelectedIndex < 0 || _template.SelectedIndex >= _templates.Count)
        {
            Show(Texts.Get("app.marketing.needNameAndTemplate"));
            return;
        }
        CampaignCreated? created = null;
        var status = await RunAsync(async c =>
        {
            var (result, campaign) = await api.CreateCampaignAsync(_name.Text.Trim(), _templates[_template.SelectedIndex].Id, Types[Math.Max(0, _type.SelectedIndex)], c);
            created = campaign;
            return result;
        });
        if (status != VisitActionResult.Success || created is null) return;
        _name.Text = "";
        await LoadAsync();
        Show(Texts.Get("app.marketing.created", ("queued", created.Queued.ToString()), ("skipped", created.SkippedNoPhone.ToString())));
        await Navigation.PushAsync(new MarketingQueuePage(created.Id));
    }

    private async Task<VisitActionResult> RunAsync(Func<CancellationToken, Task<VisitActionResult>> call)
    {
        if (_busy) return VisitActionResult.NetworkError;
        _busy = true;
        _spinner.IsVisible = _spinner.IsRunning = true;
        VisitActionResult result;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            result = await call(cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = VisitActionResult.NetworkError;
        }
        _busy = false;
        _spinner.IsVisible = _spinner.IsRunning = false;
        if (result != VisitActionResult.Success)
        {
            Show(VisitLifecycle.Describe(result));
            if (result == VisitActionResult.SessionExpired) AppLanguage.ShowRoot(new MainPage());
        }
        return result;
    }

    private void Show(string? text)
    {
        _message.Text = text;
        _message.IsVisible = !string.IsNullOrEmpty(text);
    }
}
