using DCST4.Core.Files;
using DCST4.Core.Generation.Datasets;
using DCST4.Core.Pipeline;
using DCST4.Core.TestData;
using DCST4.Core.Testing;
using FellowOakDicom;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Cli
{
    /// <summary>
    /// The command "send-files": the DICOM files of a folder or a DICOMDIR, re-identified as test patients unless
    /// --keep-identity says otherwise, sent to the profile's destination and/or saved as a File-set.
    /// </summary>
    internal sealed class SendFilesCommand
    {
        public string Source { get; private set; }
        public string ProfilePath { get; private set; }
        public string SaveFolder { get; private set; }
        public string LogFolder { get; private set; }
        public string UidRoot { get; private set; }
        public bool KeepIdentity { get; private set; }
        public bool IncludeSubfolders { get; private set; } = true;
        public bool AllowFallback { get; private set; }
        public TestPatientNaming PatientNaming { get; private set; } = TestPatientNames.Default;
        /// <summary>Null sends each file in its own syntax.</summary>
        public DicomTransferSyntax TransferSyntax { get; private set; }

        /// <summary>Returns null and sets <paramref name="error"/> if the arguments after "send-files" are not usable.</summary>
        public static SendFilesCommand Parse(IReadOnlyList<string> args, out string error)
        {
            error = null;
            var command = new SendFilesCommand();
            for (var i = 1; i < args.Count; i++)
            {
                var name = args[i].ToLowerInvariant();
                // The switches have no value.
                switch (name)
                {
                    case "--keep-identity": command.KeepIdentity = true; continue;
                    case "--no-subfolders": command.IncludeSubfolders = false; continue;
                    case "--allow-fallback": command.AllowFallback = true; continue;
                }

                if (i + 1 >= args.Count)
                {
                    error = $"{args[i]} needs a value.";
                    return null;
                }
                var value = args[++i];
                switch (name)
                {
                    case "--source": command.Source = value; break;
                    case "--profile": command.ProfilePath = value; break;
                    case "--save": command.SaveFolder = value; break;
                    case "--log-dir": command.LogFolder = value; break;
                    case "--uid-root": command.UidRoot = value; break;
                    case "--patient-naming":
                        if (!TestPatientNames.TryParse(value, out var naming))
                        {
                            error = CliArguments.PatientNamingError(value);
                            return null;
                        }
                        command.PatientNaming = naming;
                        break;
                    case "--transfer-syntax":
                        if (value.Equals("as-is", StringComparison.OrdinalIgnoreCase))
                        {
                            command.TransferSyntax = null;
                            break;
                        }
                        var syntax = CompressionCatalog.Find(value);
                        if (syntax == null)
                        {
                            error = $"Unknown transfer syntax '{value}'. Use as-is (default), explicit, implicit, a name from list-codecs, or a Transfer Syntax UID.";
                            return null;
                        }
                        command.TransferSyntax = syntax.Syntax;
                        break;
                    default:
                        error = $"Unknown option {args[i - 1]}.";
                        return null;
                }
            }

            if (string.IsNullOrWhiteSpace(command.Source)) error = "--source is required: a folder, or a DICOMDIR.";
            else if (command.ProfilePath == null && command.SaveFolder == null) error = "Give --profile to send, --save to save, or both.";
            return error == null ? command : null;
        }

        /// <summary>Prints what will be done, then does it. <paramref name="profile"/> is null when the files are only saved.</summary>
        public async Task<int> ExecuteAsync(TargetProfile profile, CancellationToken token)
        {
            Console.WriteLine("Reading " + Source + " ...");
            var scan = await Task.Run(() => FolderScanner.Scan(Source, IncludeSubfolders, token), token).ConfigureAwait(false);
            Console.WriteLine((scan.DicomDirPath != null ? "Through its DICOMDIR: " : string.Empty) + scan.Summary + ".");
            if (scan.Files.Count > 0) Console.WriteLine("Transfer syntaxes: " + scan.SyntaxSummary + ".");
            if (scan.Skipped.Count > 0)
            {
                Console.WriteLine($"{scan.Skipped.Count} skipped:");
                foreach (var skipped in scan.Skipped.Take(20)) Console.WriteLine("  " + skipped);
                if (scan.Skipped.Count > 20) Console.WriteLine($"  ... and {scan.Skipped.Count - 20} more, in the log.");
            }
            if (scan.Files.Count == 0)
            {
                Console.Error.WriteLine("No DICOM files to send.");
                return RunExitCodes.Usage;
            }

            foreach (var line in FilesDescription.Describe(scan, !KeepIdentity)) Console.WriteLine(line);

            var source = new FolderImageSource(scan, new FolderSendOptions
            {
                Reidentify = !KeepIdentity,
                UidRoot = UidRoot ?? profile?.UidRoot,
                PatientNaming = PatientNaming,
            });
            if (source.Reidentifier != null) Console.WriteLine("Test patients: " + string.Join(", ", source.Reidentifier.Patients) + ".");

            var run = new StoreRun(source, new StoreRunOptions
            {
                Send = profile != null,
                Save = SaveFolder != null,
                SaveFolder = SaveFolder,
                TransferSyntax = TransferSyntax,
                AllowFallback = AllowFallback,
                Endpoint = profile?.Endpoint,
                Network = profile?.Network ?? new DCST4.Core.Network.NetworkOptions(),
            });
            if (profile != null) Console.WriteLine($"Sending to {profile.Endpoint.CalledAe} at {profile.Endpoint.Host}:{profile.Endpoint.Port} ...");
            var summary = await run.ExecuteAsync(token).ConfigureAwait(false);

            Console.WriteLine(summary);
            if (summary.DicomDirPath != null) Console.WriteLine("DICOMDIR: " + summary.DicomDirPath);
            if (summary.FallbackWarning != null) Console.WriteLine(summary.FallbackWarning);
            foreach (var failed in summary.Results.Where(r => r.State == DCST4.Core.Network.StoreState.Failure || r.State == DCST4.Core.Network.StoreState.NotSent || r.SaveError != null).Take(20))
            {
                Console.WriteLine($"  {failed.SourcePath}: {failed.State} {failed.StatusCodeText} {failed.Description} {failed.ErrorComment} {failed.SaveError}".TrimEnd());
            }

            if (summary.WasCancelled) return RunExitCodes.Cancelled;
            if (summary.FatalError != null)
            {
                Console.Error.WriteLine(summary.FatalError);
                return RunExitCodes.Failures;
            }
            return summary.Failure > 0 || summary.NotSent > 0 || summary.SaveFailed > 0 ? RunExitCodes.Failures : RunExitCodes.Clean;
        }
    }
}
