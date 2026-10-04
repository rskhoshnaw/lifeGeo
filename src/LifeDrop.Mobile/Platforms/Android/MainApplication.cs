using Android.App;
using Android.Runtime;

namespace LifeDrop.Mobile;

#if DEBUG
[Application(AllowBackup = false, UsesCleartextTraffic = true)]
#else
[Application(AllowBackup = false)]
#endif
public class MainApplication : MauiApplication
{
	public MainApplication(IntPtr handle, JniHandleOwnership ownership)
		: base(handle, ownership)
	{
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
