using Microsoft.Extensions.DependencyInjection;

namespace LifeDrop.Mobile;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		AppLanguage.Load();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new LoadingPage { FlowDirection = AppLanguage.Direction });
	}
}