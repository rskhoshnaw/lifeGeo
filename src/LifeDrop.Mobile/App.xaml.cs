using Microsoft.Extensions.DependencyInjection;

namespace LifeDrop.Mobile;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new LoadingPage());
	}
}