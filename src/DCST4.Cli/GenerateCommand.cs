using DCST4.Core.Generation.Datasets;
using DCST4.Core.Pipeline;
using DCST4.Core.TestData;
using DCST4.Core.Testing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Cli
{
    /// <summary>The command "generate": a reproducible dataset as a DICOM File-set with a manifest.</summary>
    internal sealed class GenerateCommand
    {
        public const string ManifestFileName = "manifest.json";

        public TestDatasetRequest Request { get; } = new TestDatasetRequest();
        public string OutputFolder { get; private set; }
        public string LogFolder { get; private set; }

        /// <summary>Returns null and sets <paramref name="error"/> if the arguments after "generate" are not usable.</summary>
        public static GenerateCommand Parse(IReadOnlyList<string> args, out string error)
        {
            error = null;
            var command = new GenerateCommand();
            var request = command.Request;

            for (var i = 1; i < args.Count; i += 2)
            {
                var name = args[i];
                if (i + 1 >= args.Count)
                {
                    error = $"{name} needs a value.";
                    return null;
                }

                var value = args[i + 1];
                switch (name.ToLowerInvariant())
                {
                    case "--dataset": request.ProfileId = value; break;
                    case "--out": command.OutputFolder = value; break;
                    case "--log-dir": command.LogFolder = value; break;
                    case "--uid-root": request.UidRoot = value; break;
                    case "--seed": if (!TryInt(name, value, n => request.Seed = n, out error)) return null; break;
                    case "--patients": if (!TryInt(name, value, n => request.Patients = n, out error)) return null; break;
                    case "--studies": if (!TryInt(name, value, n => request.StudiesPerPatient = n, out error)) return null; break;
                    case "--series": if (!TryInt(name, value, n => request.SeriesPerStudy = n, out error)) return null; break;
                    case "--instances": if (!TryInt(name, value, n => request.InstancesPerSeries = n, out error)) return null; break;
                    case "--rows": if (!TryInt(name, value, n => request.Rows = n, out error)) return null; break;
                    case "--columns": if (!TryInt(name, value, n => request.Columns = n, out error)) return null; break;
                    case "--bits": if (!TryInt(name, value, n => request.BitsStored = n, out error)) return null; break;
                    case "--frames": if (!TryInt(name, value, n => request.Frames = n, out error)) return null; break;
                    case "--patient-naming":
                        if (!TestPatientNames.TryParse(value, out var naming))
                        {
                            error = CliArguments.PatientNamingError(value);
                            return null;
                        }
                        request.PatientNaming = naming;
                        break;
                    case "--transfer-syntax":
                        var syntax = CompressionCatalog.Find(value);
                        if (syntax == null)
                        {
                            error = $"Unknown transfer syntax '{value}'. Use one of: {string.Join(", ", CompressionCatalog.All.Select(c => c.Id))}.";
                            return null;
                        }
                        request.TransferSyntax = syntax.Syntax;
                        break;
                    case "--charset":
                        if (!TryCharacterSet(value, out var characterSet))
                        {
                            error = $"Unknown character set '{value}'. Use one of: default, empty, latin1, utf8.";
                            return null;
                        }
                        request.CharacterSet = characterSet;
                        break;
                    case "--edge":
                        if (!TryEdgeCases(value, out var edgeCases))
                        {
                            error = $"Unknown edge case in '{value}'. Use any of: private, long, nested, none, separated by commas.";
                            return null;
                        }
                        request.EdgeCases = edgeCases;
                        break;
                    default:
                        error = $"Unknown option {name}.";
                        return null;
                }
            }

            if (string.IsNullOrWhiteSpace(request.ProfileId)) error = "--dataset is required.";
            else if (string.IsNullOrWhiteSpace(command.OutputFolder)) error = "--out is required.";
            return error == null ? command : null;
        }

        public async Task<int> ExecuteAsync(CancellationToken token)
        {
            var generator = new TestDatasetGenerator(Request);
            var manifestPath = Path.Combine(OutputFolder, ManifestFileName);

            Directory.CreateDirectory(OutputFolder);

            if (generator.IsUnavailable)
            {
                foreach (var item in generator.Manifest.Unavailable) Console.WriteLine($"Unavailable: {item.ProfileId}, {item.BitsStored} bits, {item.SamplesPerPixel} samples: {item.Reason}");
                generator.Manifest.Save(manifestPath);
                Console.WriteLine("Manifest: " + manifestPath);
                return RunExitCodes.Clean;
            }

            var run = new StoreRun(generator, new StoreRunOptions
            {
                Send = false,
                Save = true,
                SaveFolder = OutputFolder,
                TransferSyntax = generator.TransferSyntax,
            });
            var summary = await run.ExecuteAsync(token).ConfigureAwait(false);

            generator.Manifest.Save(manifestPath);
            Console.WriteLine(summary);
            Console.WriteLine("Folder:   " + OutputFolder);
            Console.WriteLine("Manifest: " + manifestPath);

            if (summary.WasCancelled) return RunExitCodes.Cancelled;
            if (summary.FatalError != null)
            {
                Console.Error.WriteLine(summary.FatalError);
                return RunExitCodes.Failures;
            }
            return summary.SaveFailed > 0 ? RunExitCodes.Failures : RunExitCodes.Clean;
        }

        private static bool TryInt(string option, string value, Action<int> apply, out string error)
        {
            error = null;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                apply(number);
                return true;
            }
            error = $"{option} needs a number, not '{value}'.";
            return false;
        }

        private static bool TryCharacterSet(string value, out CharacterSetVariant variant)
        {
            switch (value.Trim().ToLowerInvariant())
            {
                case "default": variant = CharacterSetVariant.Default; return true;
                case "empty": variant = CharacterSetVariant.EmptyAscii; return true;
                case "latin1": variant = CharacterSetVariant.Latin1Swedish; return true;
                case "utf8": variant = CharacterSetVariant.Utf8Swedish; return true;
                default: variant = CharacterSetVariant.Default; return false;
            }
        }

        private static bool TryEdgeCases(string value, out EdgeCases edgeCases)
        {
            edgeCases = EdgeCases.None;
            foreach (var part in value.Split(',').Select(p => p.Trim().ToLowerInvariant()))
            {
                switch (part)
                {
                    case "none": break;
                    case "private": edgeCases |= EdgeCases.PrivateTags; break;
                    case "long": edgeCases |= EdgeCases.LongStrings; break;
                    case "nested": edgeCases |= EdgeCases.NestedSequences; break;
                    default: return false;
                }
            }
            return true;
        }
    }
}
