using System.Windows;
using Serilog;

namespace PptPoc.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var config = AppConfigLoader.Load();

        // Setup Serilog file logging
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                config.LogFilePath,
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("PPT Speaker Highlight POC starting");

        // Handle unhandled exceptions
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception");
            Log.CloseAndFlush();
        };

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Dispatcher unhandled exception");
            args.Handled = true;
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("PPT Speaker Highlight POC exiting");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}

