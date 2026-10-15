using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HfsqlOdbcStudio.Models;
using Microsoft.Win32;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using UiMessageBox = Wpf.Ui.Controls.MessageBox;
using UiMessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;
using WpfTextBlock = System.Windows.Controls.TextBlock;

namespace HfsqlOdbcStudio.Services;

public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "Yes", string cancelText = "Cancel", bool danger = false);

    string? SaveFile(string title, string filter, string defaultFileName);

    /// <summary>Shows a modal window owned by the main window and returns its DialogResult.</summary>
    bool? ShowModal(Window window);
}

public sealed class DialogService : IDialogService
{
    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "Yes", string cancelText = "Cancel", bool danger = false)
    {
        var box = new UiMessageBox
        {
            Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new WpfTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 },
            },
            PrimaryButtonText = confirmText,
            CloseButtonText = cancelText,
            IsSecondaryButtonEnabled = false,
            PrimaryButtonAppearance = danger ? ControlAppearance.Danger : ControlAppearance.Primary,
            Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var result = await box.ShowDialogAsync();
        return result == UiMessageBoxResult.Primary;
    }

    public string? SaveFile(string title, string filter, string defaultFileName)
    {
        var dlg = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = defaultFileName,
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dlg.ShowDialog(Application.Current.MainWindow) == true ? dlg.FileName : null;
    }

    public bool? ShowModal(Window window)
    {
        window.Owner = Application.Current.MainWindow;
        return window.ShowDialog();
    }
}

public interface INotificationService
{
    void Success(string title, string message);

    void Error(string title, string message);

    void Warning(string title, string message);

    void Info(string title, string message);
}

/// <summary>Snackbar (toast) notifications via WPF-UI.</summary>
public sealed class NotificationService(ISnackbarService snackbar) : INotificationService
{
    public void Success(string title, string message) =>
        Show(title, message, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24, 4);

    public void Error(string title, string message) =>
        Show(title, message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24, 8);

    public void Warning(string title, string message) =>
        Show(title, message, ControlAppearance.Caution, SymbolRegular.Warning24, 6);

    public void Info(string title, string message) =>
        Show(title, message, ControlAppearance.Info, SymbolRegular.Info24, 4);

    private void Show(string title, string message, ControlAppearance appearance, SymbolRegular icon, int seconds)
    {
        void DoShow()
        {
            if (snackbar.GetSnackbarPresenter() is null) return;
            snackbar.Show(title, message, appearance, new SymbolIcon(icon), TimeSpan.FromSeconds(seconds));
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) DoShow();
        else dispatcher.Invoke(DoShow);
    }
}

/// <summary>Light/dark theme following Windows, with a manual override. One fixed accent color.</summary>
public sealed class AppThemeService
{
    public static readonly Color Accent = Color.FromRgb(0x00, 0x67, 0xC0);

    private Window? _window;

    public ThemeMode Mode { get; private set; } = ThemeMode.System;

    public bool IsDark => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

    public event EventHandler? ThemeChanged;

    public void Initialize(Window window, ThemeMode mode)
    {
        _window = window;
        ApplicationThemeManager.Changed += (theme, _) =>
        {
            ApplicationAccentColorManager.Apply(Accent, theme, false);
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        };
        Apply(mode);
    }

    public void Apply(ThemeMode mode)
    {
        Mode = mode;
        if (_window is null) return;

        if (mode == ThemeMode.System)
        {
            ApplicationThemeManager.ApplySystemTheme(false);
            SystemThemeWatcher.Watch(_window, WindowBackdropType.Mica, false);
        }
        else
        {
            SystemThemeWatcher.UnWatch(_window);
            ApplicationThemeManager.Apply(mode == ThemeMode.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.Mica, false);
        }

        ApplicationAccentColorManager.Apply(Accent, ApplicationThemeManager.GetAppTheme(), false);
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public ThemeMode Next() => Mode switch
    {
        ThemeMode.System => ThemeMode.Light,
        ThemeMode.Light => ThemeMode.Dark,
        _ => ThemeMode.System,
    };
}
