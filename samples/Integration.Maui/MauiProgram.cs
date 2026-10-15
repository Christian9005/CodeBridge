using CodeBridge.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeBridge.Samples.IntegrationMaui;

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
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		// The cards draw their own input frames: drop the native underline/outline so an Entry does not get a second border.
		Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("CodeBridgeFlat", (handler, view) =>
		{
#if WINDOWS
			var clear = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
			foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
				handler.PlatformView.Resources[key] = clear;
#elif ANDROID
			handler.PlatformView.Background = null;
#endif
		});

		// One shared board for the whole app. A phone has no USB serial port: use the Wi-Fi address of a paired ESP32,
		// or the built-in simulator to try the app without hardware. MAUI does not run hosted services, so the page connects.
		builder.Services.AddCodeBridge(options =>
		{
			options.Port = "simulator";
			options.AutoConnect = false;
		});
		builder.Services.AddTransient<MainPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
