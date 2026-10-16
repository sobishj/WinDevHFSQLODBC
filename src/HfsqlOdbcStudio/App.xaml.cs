using System.Windows;
using System.Windows.Threading;
using HfsqlOdbcStudio.Helpers;
using HfsqlOdbcStudio.Services;
using HfsqlOdbcStudio.ViewModels;
using HfsqlOdbcStudio.Views;
using Wpf.Ui;

namespace HfsqlOdbcStudio;

public partial class App : Application
{
    private int _showingError;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            AppPaths.EnsureCreated();
            FileLog.Info($"Starting HFSQL ODBC Studio {typeof(App).Assembly.GetName().Version} ({(Environment.Is64BitProcess ? "64" : "32")}-bit process)");

            // Composition root
            var settings = new SettingsService();
            settings.Load();
            var drivers = new OdbcDriverService();
            var connections = new ConnectionService(drivers);
            var schema = new SchemaService(connections);
            var queries = new QueryService(connections);
            var profiles = new ProfileService();
            var history = new HistoryService();
            var dialogs = new DialogService();
            var snackbar = new SnackbarService();
            var notify = new NotificationService(snackbar);
            var theme = new AppThemeService();

            var explorer = new ExplorerViewModel(schema, connections, notify, settings);
            var query = new QueryViewModel(queries, schema, connections, explorer, dialogs, notify, history, settings);
            var main = new MainViewModel(connections, profiles, dialogs, notify, explorer, query,
                () => new ConnectionViewModel(drivers, profiles, connections, notify, dialogs));

            var window = new MainWindow(main, settings, theme);
            snackbar.SetSnackbarPresenter(window.SnackbarPresenter);
            MainWindow = window;
            window.Show();

            await main.InitializeAsync();
        }
        catch (Exception ex)
        {
            ReportException(ex, "startup");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        FileLog.Info("Exiting");
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ReportException(e.Exception, "UI thread");
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) ReportException(ex, "background thread (fatal)");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        FileLog.Error("Unobserved task exception", e.Exception);
        e.SetObserved();
    }

    private void ReportException(Exception ex, string source)
    {
        FileLog.Error($"Unhandled exception ({source})", ex);

        // Avoid stacking dialogs if errors repeat while one is open.
        if (Interlocked.Exchange(ref _showingError, 1) == 1) return;
        try
        {
            void Show() => MessageBox.Show(
                "Something went wrong, but the application will try to keep running." + Environment.NewLine + Environment.NewLine +
                ex.GetType().Name + ": " + ex.Message + Environment.NewLine + Environment.NewLine +
                "Details were written to:" + Environment.NewLine + FileLog.CurrentFile,
                "HFSQL ODBC Studio – unexpected error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            if (Dispatcher.CheckAccess()) Show();
            else Dispatcher.Invoke(Show);
        }
        catch
        {
            // Nothing more we can do.
        }
        finally
        {
            Interlocked.Exchange(ref _showingError, 0);
        }
    }
}
