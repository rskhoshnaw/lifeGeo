namespace LifeDrop.Mobile;

// Opens the phone's own print dialog for an HTML page; there the owner chooses "Save as PDF" (Android) or shares/saves the PDF (iOS) (plan step 5.6).
// Returns false on a platform without such a dialog.
public static class HtmlPrinter
{
#if ANDROID
    private static Android.Webkit.WebView? _view;   // kept until the print job has read the page

    private sealed class Finished(string jobName) : Android.Webkit.WebViewClient
    {
        public override void OnPageFinished(Android.Webkit.WebView? view, string? url)
        {
            base.OnPageFinished(view, url);
            if (view is null || Platform.CurrentActivity?.GetSystemService(Android.Content.Context.PrintService) is not Android.Print.PrintManager manager) return;
            manager.Print(jobName, view.CreatePrintDocumentAdapter(jobName), null);
        }
    }

    public static bool Print(string jobName, string html)
    {
        if (Platform.CurrentActivity is not { } activity) return false;
        _view = new Android.Webkit.WebView(activity) { };
        _view.SetWebViewClient(new Finished(jobName));
        _view.LoadDataWithBaseURL(null, html, "text/html", "UTF-8", null);
        return true;
    }
#elif IOS
    public static bool Print(string jobName, string html)
    {
        var info = UIKit.UIPrintInfo.PrintInfo;
        info.OutputType = UIKit.UIPrintInfoOutputType.General;
        info.JobName = jobName;
        var controller = UIKit.UIPrintInteractionController.SharedPrintController;
        controller.PrintInfo = info;
        controller.PrintFormatter = new UIKit.UIMarkupTextPrintFormatter(html);
        controller.Present(true, (_, _, _) => { });
        return true;
    }
#else
    public static bool Print(string jobName, string html) => false;
#endif
}
