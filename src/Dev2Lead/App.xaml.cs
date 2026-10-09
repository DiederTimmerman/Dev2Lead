namespace Dev2Lead;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new MainPage())
		{
			Title = "Dev2Lead | Your next chapter",
			Width = 1440,
			Height = 940,
			MinimumWidth = 960,
			MinimumHeight = 700
		};
	}
}
