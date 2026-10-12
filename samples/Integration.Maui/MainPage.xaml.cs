using CodeBridge.Hosting;
using Microsoft.Extensions.Options;

namespace CodeBridge.Samples.IntegrationMaui;

public partial class MainPage : ContentPage
{
	private readonly BoardService _board;
	private readonly CodeBridgeOptions _options;
	private readonly IDispatcherTimer _timer;

	public MainPage(BoardService board, IOptions<CodeBridgeOptions> options)
	{
		InitializeComponent();
		_board = board;
		_options = options.Value;

		// Show the connection state whenever it changes (the event can come from any thread).
		_board.Changed += () => Dispatcher.Dispatch(RefreshStatus);

		// The light sensor is polled twice a second while the page is visible.
		_timer = Dispatcher.CreateTimer();
		_timer.Interval = TimeSpan.FromMilliseconds(500);
		_timer.Tick += async (_, _) => await ReadLightAsync();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		RefreshStatus();
		_timer.Start();
	}

	protected override void OnDisappearing()
	{
		_timer.Stop();
		base.OnDisappearing();
	}

	private async void OnConnectClicked(object? sender, EventArgs e)
	{
		// Async void handlers must never let an exception escape: it would close the app.
		try
		{
			_options.Port = string.IsNullOrWhiteSpace(AddressEntry.Text) ? "simulator" : AddressEntry.Text.Trim();
			_options.AccessToken = string.IsNullOrWhiteSpace(TokenEntry.Text) ? null : TokenEntry.Text.Trim();

			await _board.DisconnectAsync();
			await _board.ConnectAsync();
		}
		catch (Exception)
		{
			// the reason is shown through BoardService.LastError
		}

		RefreshStatus();
	}

	private async void OnLedToggled(object? sender, ToggledEventArgs e)
	{
		LedState.Text = e.Value ? "ON" : "OFF";
		LedState.SetAppThemeColor(Label.TextColorProperty, e.Value ? Ok.Light : Muted.Light, e.Value ? Ok.Dark : Muted.Dark);
		await Guard(() => _board.WriteDigitalAsync(2, e.Value));
	}

	private async void OnLampChanged(object? sender, ValueChangedEventArgs e)
	{
		LampReadout.Text = ((int)Math.Round(e.NewValue * 100 / 255)).ToString();
		LampRaw.Text = $"{(int)e.NewValue} / 255";
		await Guard(() => _board.WritePwmAsync(4, (int)e.NewValue));
	}

	private async Task ReadLightAsync()
	{
		if (!_board.IsConnected)
			return;

		try
		{
			var raw = await _board.ReadAnalogAsync(34);
			var level = Math.Clamp(raw / (double)_board.AnalogMax, 0, 1);
			LightFill.ColumnDefinitions[0].Width = new GridLength(level, GridUnitType.Star);
			LightFill.ColumnDefinitions[1].Width = new GridLength(1 - level, GridUnitType.Star);
			LightReadout.Text = (raw * 100 / _board.AnalogMax).ToString();
		}
		catch (Exception)
		{
			// a dropped connection is reported by the board service; the next tick tries again
		}
	}

	private async Task Guard(Func<Task> action)
	{
		try
		{
			await action();
		}
		catch (Exception ex)
		{
			ShowError(ex.Message);
		}
	}

	// Same state vocabulary as the Blazor cards and the editor: green live, blue connecting, red fault, grey idle; the word is always shown too.
	private static readonly (Color Light, Color Dark) Ok = (Color.FromArgb("#0F7A55"), Color.FromArgb("#35C98C"));
	private static readonly (Color Light, Color Dark) Bad = (Color.FromArgb("#C4243B"), Color.FromArgb("#FF7285"));
	private static readonly (Color Light, Color Dark) Accent = (Color.FromArgb("#0A5FC2"), Color.FromArgb("#5AA9FF"));
	private static readonly (Color Light, Color Dark) Muted = (Color.FromArgb("#566170"), Color.FromArgb("#9BA7B6"));
	private static readonly (Color Light, Color Dark) Idle = (Color.FromArgb("#8A96A6"), Color.FromArgb("#4D596A"));

	private void ShowError(string? message)
	{
		ErrorLabel.Text = message ?? string.Empty;
		ErrorLabel.IsVisible = !string.IsNullOrWhiteSpace(message);
	}

	private void RefreshStatus()
	{
		var connected = _board.IsConnected;
		var (dot, word, wordColor) = _board.State switch
		{
			BoardConnectionState.Connected => (Ok, "Connected", Ok),
			BoardConnectionState.Connecting => (Accent, "Connecting", Muted),
			BoardConnectionState.Faulted => (Bad, "Connection lost", Bad),
			_ => (Idle, "Not connected", Muted)
		};

		StatusDot.SetAppThemeColor(BoxView.ColorProperty, dot.Light, dot.Dark);
		StateLabel.SetAppThemeColor(Label.TextColorProperty, wordColor.Light, wordColor.Dark);
		StateLabel.Text = word;
		StatusLabel.Text = connected ? _board.Info?.ChipModel ?? "Board" : "Board";
		SimulatorChip.IsVisible = connected && _board.IsSimulated;
		DetailLabel.Text = connected ? $"{_board.Target} · firmware {_board.FirmwareVersion}" : "Waiting for a board";
		ShowError(connected ? null : _board.LastError);
		ConnectButton.Text = connected ? "Reconnect" : "Connect";
		LedSwitch.IsEnabled = LampSlider.IsEnabled = connected;
	}
}
