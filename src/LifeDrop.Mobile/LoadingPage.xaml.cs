using LifeDrop.Mobile.Auth;
using System.Net.Http;
using System;

#if ANDROID
using LifeDrop.Mobile.Auth.Android;
#endif

namespace LifeDrop.Mobile;

public partial class LoadingPage : ContentPage
{
    public LoadingPage()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        
#if ANDROID
        var store = new AndroidSecureCredentialStore(Android.App.Application.Context);
#else
        ISecureCredentialStore store = null!;
#endif
        if (store == null) 
        {
            Application.Current!.Windows[0].Page = new NavigationPage(new MainPage());
            return;
        }

        try
        {
            var sessionData = await store.LoadSessionAsync();
            if (sessionData != null)
            {
                var url = Preferences.Get("ApiUrl", "");
                if (string.IsNullOrEmpty(url))
                {
#if DEBUG
                    url = "http://127.0.0.1:5256";
                    Preferences.Set("ApiUrl", url);
#else
                    Application.Current!.Windows[0].Page = new NavigationPage(new MainPage());
                    return;
#endif
                }

                var client = new HttpClient { BaseAddress = new Uri(url) };
                var apiClient = new AuthApiClient(client);

                var authSession = new AuthSession(store, apiClient, TimeProvider.System);
                // Call GetAccessTokenAsync to determine if session is usable.
                var result = await authSession.GetAccessTokenAsync();

                if (result.IsSuccess)
                {
                    Application.Current!.Windows[0].Page = new NavigationPage(new HomePage());
                    return;
                }
            }
        }
        catch
        {
            // Ignore and drop to login
        }

        Application.Current!.Windows[0].Page = new NavigationPage(new MainPage());
    }
}
