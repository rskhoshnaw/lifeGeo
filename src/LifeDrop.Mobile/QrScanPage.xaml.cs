using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Localization;
using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace LifeDrop.Mobile;

public partial class QrScanPage : ContentPage
{
    // Called back on the UI thread; the caller sets the code field and disposes the page.
    private readonly Action<string> _onCode;
    private bool _handled;

    public BarcodeReaderOptions ScanOptions { get; } = new()
    {
        Formats = BarcodeFormats.QrCode,
        AutoRotate = true,
        Multiple = false,
    };

    public QrScanPage(Action<string> onCode)
    {
        InitializeComponent();
        _onCode = onCode;
        HintLabel.Text = Texts.Get("app.login.scanHint");
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        BarcodeReader.IsDetecting = true;
    }

    protected override void OnDisappearing()
    {
        BarcodeReader.IsDetecting = false;
        base.OnDisappearing();
    }

    private void OnBarcodeDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        if (_handled) return;
        var raw = e.Results.FirstOrDefault()?.Value;
        if (string.IsNullOrWhiteSpace(raw)) return;
        var code = CompanyJoinLink.TryGetCode(raw);
        if (code is null) return;
        _handled = true;

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await Navigation.PopAsync();
            _onCode(code);
        });
    }
}
