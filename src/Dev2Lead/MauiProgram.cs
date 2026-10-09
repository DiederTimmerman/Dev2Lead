using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace Dev2Lead;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();
		builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(90) });
		builder.Services.AddSingleton<Dev2Lead.Core.LearnTranscriptService>();
		builder.Services.AddSingleton<Dev2Lead.Services.DesktopServices>();
		builder.Services.AddSingleton<Dev2Lead.Services.GoogleSignInService>();
		builder.Services.AddSingleton<Dev2Lead.Services.CloudProfileClient>();

#if DEBUG
		builder.Configuration.AddUserSecrets<App>(optional: true);
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif
		builder.Configuration.AddEnvironmentVariables();

		return builder.Build();
	}
}
