namespace ServerLauncher;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += async (_, args) =>
        {
            args.Handled = true;
            await ReportUnhandledExceptionAsync(args.Exception, "Dispatcher unhandled exception");
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                _ = ReportUnhandledExceptionAsync(exception, "AppDomain unhandled exception");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            _ = ReportUnhandledExceptionAsync(args.Exception, "Unobserved task exception");
        };
    }

    private static async Task ReportUnhandledExceptionAsync(Exception exception, string context)
    {
        try
        {
            var settingsService = new Services.SettingsService();
            var settings = await settingsService.LoadAsync();
            var reporter = new Services.BugReportService();
            await reporter.HandleAsync(exception, context, settings, null);
            System.Windows.MessageBox.Show(
                "Ошибка сохранена в локальный отчет.",
                "minivibe",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        catch
        {
            System.Windows.MessageBox.Show(
                exception.Message,
                "Ошибка лаунчера",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }
}
