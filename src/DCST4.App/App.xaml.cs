using DCST4.Core;
using DCST4.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using ILogger = Serilog.ILogger;

namespace DCST4
{
    public partial class App : Application
    {
        /// <summary>%LocalAppData%\DCST4\Logs</summary>
        public static string LogDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"DCST4\Logs");

        protected override void OnStartup(StartupEventArgs e)
        {
            var serilogLogger = ConfigureLogging();
            RegisterGlobalExceptionHandlers();
            SetAppUserModelId();

            DicomSetup.Initialize(services => services.AddLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddSerilog(serilogLogger);
            }));

            base.OnStartup(e);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SetCurrentProcessExplicitAppUserModelID(string appId);

        /// <summary>Must run before the first window. Without the ID the program still works, so a failure is only logged.</summary>
        private static void SetAppUserModelId()
        {
            try
            {
                SetCurrentProcessExplicitAppUserModelID(AppInfo.AppUserModelId);
            }
            catch (Exception ex)
            {
                Log.ForContext<App>().Warning(ex, "Could not set the AppUserModelID");
            }
        }

        private static ILogger ConfigureLogging()
        {
            var logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.File(
                    Path.Combine(LogDirectory, "Trace.log"),
                    Serilog.Events.LogEventLevel.Verbose,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.Sink(UiLogSink.Instance)
                .CreateLogger();

            Log.Logger = logger;
            return logger;
        }

        /// <summary>
        /// Makes sure unexpected exceptions end up in the log instead of silently crashing the app.
        /// </summary>
        private void RegisterGlobalExceptionHandlers()
        {
            DispatcherUnhandledException += (s, args) =>
            {
                Log.Error(args.Exception, "Unhandled UI exception");
                MessageBox.Show(
                    "An unexpected error occurred:\n" + args.Exception.Message + "\n\nSee the log for details.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception (terminating: {terminating})", args.IsTerminating);
                if (args.IsTerminating) Log.CloseAndFlush();
            };

            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                Log.Error(args.Exception, "Unobserved task exception");
                args.SetObserved();
            };
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }
}
