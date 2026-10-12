using Avalonia.Controls;

namespace MediaPager.App.Desktop;

public sealed partial class MainWindow : Window
{
    private DesktopApiHost? _apiHost;
    private bool _starting;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        if (_starting) return;
        _starting = true;
        try
        {
            StartupMessage.Text = "Starting the local MediaPager host…";
            _apiHost = await DesktopApiHost.StartAsync();
            Browser.Source = _apiHost.Address;
            Browser.IsVisible = true;
            StartupPanel.IsVisible = false;
        }
        catch (Exception exception)
        {
            StartupMessage.Text = $"MediaPager could not start: {exception.Message}";
        }
    }

    private async void OnClosed(object? sender, EventArgs eventArgs)
    {
        if (_apiHost is not null)
            await _apiHost.DisposeAsync();
    }
}
