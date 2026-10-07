using Microsoft.Extensions.Logging;
using LifeDrop.Mobile.Auth;
#if ANDROID
using LifeDrop.Mobile.Auth.Android;
#endif

namespace LifeDrop.Mobile;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("Vazirmatn-Regular.ttf", "VazirmatnRegular");
				fonts.AddFont("Vazirmatn-SemiBold.ttf", "VazirmatnSemibold");
			});

		// Removed hardcoded API URL. The authentication screen will construct
		// the client dynamically to support development-only URLs.

#if ANDROID
		builder.Services.AddSingleton<ISecureCredentialStore>(sp => 
			new AndroidSecureCredentialStore(Android.App.Application.Context));
#endif

		builder.Services.AddTransient<DeviceRegistrationCoordinator>();
		builder.Services.AddTransient<LoginCoordinator>();
		builder.Services.AddTransient<LogoutCoordinator>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
