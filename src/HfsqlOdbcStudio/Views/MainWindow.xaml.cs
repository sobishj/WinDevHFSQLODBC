using System.ComponentModel;
using System.Windows;
using HfsqlOdbcStudio.Services;
using HfsqlOdbcStudio.ViewModels;

namespace HfsqlOdbcStudio.Views;

public partial class MainWindow
{
    private readonly ISettingsService _settings;

    public MainWindow(MainViewModel viewModel, ISettingsService settings, AppThemeService theme)
    {
        _settings = settings;
        DataContext = viewModel;
        InitializeComponent();
        RestorePlacement();
        theme.Initialize(this, settings.Current.Theme);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SavePlacement();
        base.OnClosing(e);
    }

    private void RestorePlacement()
    {
        var s = _settings.Current;
        if (s.WindowWidth is not { } width || s.WindowHeight is not { } height || s.WindowLeft is not { } left || s.WindowTop is not { } top)
            return;

        // Only restore if the window would be visible on the current virtual screen.
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var bounds = new Rect(left, top, Math.Max(MinWidth, width), Math.Max(MinHeight, height));
        if (!screen.IntersectsWith(bounds) || bounds.Left > screen.Right - 100 || bounds.Top > screen.Bottom - 100) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        if (s.IsMaximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var s = _settings.Current;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (!bounds.IsEmpty)
        {
            s.WindowLeft = bounds.Left;
            s.WindowTop = bounds.Top;
            s.WindowWidth = bounds.Width;
            s.WindowHeight = bounds.Height;
        }

        s.IsMaximized = WindowState == WindowState.Maximized;
        _settings.Save();
    }
}
