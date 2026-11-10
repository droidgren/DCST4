using DCST4.Core;
using DCST4.Core.Generation.Datasets;
using DCST4.Core.TestData;
using DCST4.Core.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ILogger = Serilog.ILogger;

namespace DCST4.Cli
{
    /// <summary>Command line front-end on the same engine as the WPF app.</summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            TrySetConsoleEncoding();

            try
            {
                return RunAsync(args).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Unhandled exception");
                Console.Error.WriteLine("Unexpected error: " + ex.Message);
                return RunExitCodes.Failures;
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        private static async Task<int> RunAsync(string[] args)
        {
            if (args.Length == 1 && args[0].Equals("--version", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(ProductInfo.Version);
                return RunExitCodes.Clean;
            }

            if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h" || args[0] == "/?"))
            {
                Console.WriteLine(CliArguments.Usage);
                return RunExitCodes.Clean;
            }

            if (args.Length == 1 && args[0].Equals("list-datasets", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var dataset in DatasetProfileCatalog.All)
                {
                    Console.WriteLine(dataset.Id.PadRight(30) + dataset.DisplayName + (dataset.IncludedByDefault ? string.Empty : " (only when asked for by name)"));
                }
                return RunExitCodes.Clean;
            }

            if (args.Length >= 1 && args[0].Equals("list-codecs", StringComparison.OrdinalIgnoreCase)) return ListCodecs(args);

            if (args.Length >= 1 && args[0].Equals("generate", StringComparison.OrdinalIgnoreCase)) return await GenerateAsync(args).ConfigureAwait(false);

            if (args.Length == 0 || !args[0].Equals("run", StringComparison.OrdinalIgnoreCase)) return UsageError("Expected the command 'run', 'generate', 'list-datasets' or 'list-codecs'.");

            var arguments = CliArguments.Parse(args, out var argumentError);
            if (arguments == null) return UsageError(argumentError);

            ConfigureLogging(arguments.LogFolder);

            TargetProfile profile;
            try
            {
                profile = TargetProfile.Load(arguments.ProfilePath);
            }
            catch (TargetProfileException ex)
            {
                return Refuse(ex.Message);
            }

            var problem = profile.Validate();
            if (problem != null) return Refuse("The profile cannot be used: " + problem);

            var tests = TestCatalog.Select(arguments.Tests, out var selectionError);
            if (tests == null) return Refuse(selectionError);

            // Before anything is sent, so that a test run never ends with nowhere to put its results.
            try
            {
                Directory.CreateDirectory(arguments.OutputFolder);
            }
            catch (Exception ex) when (IsFileProblem(ex))
            {
                return Refuse($"Cannot use the output folder {arguments.OutputFolder}: {ex.Message}");
            }

            var options = new TestRunOptions
            {
                Seed = arguments.Seed,
                SyntaxOverride = arguments.TransferSyntax,
                IndexWaitSeconds = arguments.IndexWaitSeconds,
                CommitLingerSeconds = arguments.CommitLingerSeconds,
                CommitWaitSeconds = arguments.CommitWaitSeconds,
                CommitGraceSeconds = arguments.CommitGraceSeconds,
                MaxAssociations = arguments.MaxAssociations,
                IdleWaitSeconds = arguments.IdleWaitSeconds,
                ArtimWaitSeconds = arguments.ArtimWaitSeconds,
                FaultSettleSeconds = arguments.FaultSettleSeconds,
                // Under --out and kept, so that what was sent and retrieved can be looked at afterwards.
                WorkFolder = Path.Combine(arguments.OutputFolder, "work-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture)),
            };

            if (arguments.MatrixPath != null)
            {
                try
                {
                    options.Matrix = DCST4.Core.Capability.CapabilityMatrix.Load(arguments.MatrixPath);
                }
                catch (DCST4.Core.Capability.CapabilityFileException ex)
                {
                    return Refuse(ex.Message);
                }
            }

            return await WithCancelKeyAsync(token => ExecuteAsync(profile, tests, arguments.OutputFolder, arguments.PatientNaming, options, token)).ConfigureAwait(false);
        }

        private static async Task<int> WithCancelKeyAsync(Func<CancellationToken, Task<int>> action)
        {
            using (var cancellation = new CancellationTokenSource())
            {
                ConsoleCancelEventHandler onCancel = (s, e) =>
                {
                    // A second Ctrl+C is left to end the process, in case stopping takes too long.
                    if (cancellation.IsCancellationRequested) return;
                    e.Cancel = true;
                    Console.Error.WriteLine("Cancelling...");
                    cancellation.Cancel();
                };
                Console.CancelKeyPress += onCancel;

                try
                {
                    return await action(cancellation.Token).ConfigureAwait(false);
                }
                finally
                {
                    Console.CancelKeyPress -= onCancel;
                }
            }
        }

        private static async Task<int> GenerateAsync(string[] args)
        {
            var command = GenerateCommand.Parse(args, out var argumentError);
            if (command == null) return UsageError(argumentError);

            var problems = command.Request.Validate();
            if (problems.Count > 0) return Refuse(string.Join(" ", problems));

            var manifest = Path.Combine(command.OutputFolder, GenerateCommand.ManifestFileName);
            if (File.Exists(manifest)) return Refuse($"{manifest} already exists. Use a new folder, so that an earlier manifest is not lost.");

            ConfigureLogging(command.LogFolder);

            try
            {
                return await WithCancelKeyAsync(command.ExecuteAsync).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsFileProblem(ex))
            {
                return Refuse($"Cannot use the output folder {command.OutputFolder}: {ex.Message}");
            }
        }

        private static int ListCodecs(string[] args)
        {
            string logFolder = null;
            for (var i = 1; i < args.Length; i += 2)
            {
                if (!args[i].Equals("--log-dir", StringComparison.OrdinalIgnoreCase) || i + 1 >= args.Length) return UsageError($"Unknown option {args[i]}.");
                logFolder = args[i + 1];
            }

            ConfigureLogging(logFolder);
            foreach (var result in CompressionCatalog.ProbeAll())
            {
                var kind = result.SamplesPerPixel == 1 ? result.BitsStored + " bit" : "RGB";
                Console.WriteLine(result.SyntaxId.PadRight(16) + kind.PadRight(8) + (result.Available ? "available" : "unavailable: " + result.Reason));
            }
            return RunExitCodes.Clean;
        }

        private static async Task<int> ExecuteAsync(TargetProfile profile, System.Collections.Generic.IReadOnlyList<ITestCase> tests, string outputFolder, TestPatientNaming patientNaming, TestRunOptions options, CancellationToken token)
        {
            var runner = new TestRunner();
            runner.Progress += progress =>
            {
                if (progress.Stage == TestProgressStage.Activity)
                {
                    Console.WriteLine($"[{progress.Index + 1}/{progress.Total}] {progress.Test.Id} ... {progress.Activity}");
                }
                else if (progress.Stage == TestProgressStage.Completed)
                {
                    Console.WriteLine($"[{progress.Index + 1}/{progress.Total}] {progress.Test.Id}: {progress.Result.Result}: {progress.Result.Explanation}");
                }
            };

            var report = await runner.RunAsync(profile, tests, token, patientNaming, options).ConfigureAwait(false);
            Console.WriteLine(string.Join(", ", report.Summary.Where(p => p.Value > 0).Select(p => $"{p.Key} {p.Value}")) + (report.Cancelled ? " (cancelled)" : string.Empty));

            try
            {
                var files = ResultFileWriter.Write(report, outputFolder);
                Console.WriteLine("JSON: " + files.Json);
                Console.WriteLine("CSV:  " + files.Csv);
                if (files.CapabilityJson != null)
                {
                    Console.WriteLine("Capability matrix: " + files.CapabilityJson);
                    Console.WriteLine("Capability matrix: " + files.CapabilityCsv);
                }
                if (files.ChainCsv != null) Console.WriteLine("Chain: " + files.ChainCsv);
            }
            catch (Exception ex) when (IsFileProblem(ex))
            {
                return Refuse($"The results could not be saved in {outputFolder}: {ex.Message}");
            }

            return RunExitCodes.From(report);
        }

        private static void ConfigureLogging(string logFolder)
        {
            ILogger logger;
            try
            {
                var folder = !string.IsNullOrWhiteSpace(logFolder)
                    ? logFolder
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"DCST4\Logs");

                logger = new LoggerConfiguration()
                    .MinimumLevel.Verbose()
                    .WriteTo.File(
                        Path.Combine(folder, "Cli-.log"),
                        Serilog.Events.LogEventLevel.Verbose,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 7,
                        shared: true,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
                    .CreateLogger();
            }
            catch (Exception ex) when (IsFileProblem(ex))
            {
                // A test run is still worth doing without a log.
                Console.Error.WriteLine("Warning: no log file: " + ex.Message);
                logger = new LoggerConfiguration().CreateLogger();
            }

            Log.Logger = logger;
            DicomSetup.Initialize(services => services.AddLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddSerilog(logger);
            }));
        }

        private static bool IsFileProblem(Exception ex) =>
            ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException;

        private static int UsageError(string message)
        {
            Console.Error.WriteLine(message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(CliArguments.Usage);
            return RunExitCodes.Usage;
        }

        private static int Refuse(string message)
        {
            Log.Error("{message}", message);
            Console.Error.WriteLine(message);
            return RunExitCodes.Usage;
        }

        private static void TrySetConsoleEncoding()
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch (IOException)
            {
                // No console to set it on.
            }
        }
    }
}
