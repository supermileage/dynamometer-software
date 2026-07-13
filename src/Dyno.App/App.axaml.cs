using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Dyno.App.ViewModels;
using Dyno.App.Views;
using Serilog;
using Serilog.Extensions.Logging;

namespace Dyno.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Composition root: build the Serilog-backed logger factory once and hand it
            // to the view model, which owns the Dyno.Core device link.
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Console()
                .WriteTo.File("logs/dyno-.log", rollingInterval: RollingInterval.Day)
                .CreateLogger();

            var loggerFactory = new SerilogLoggerFactory(Log.Logger);
            var viewModel = new MainWindowViewModel(loggerFactory);

            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.ShutdownRequested += (_, _) => viewModel.Shutdown();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
