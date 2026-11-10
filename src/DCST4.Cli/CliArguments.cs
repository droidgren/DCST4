using DCST4.Core.TestData;
using System;
using System.Collections.Generic;

namespace DCST4.Cli
{
    /// <summary>The parsed command line of "run".</summary>
    internal sealed class CliArguments
    {
        public const string Usage =
            "Usage:\n" +
            "  DCST4.Cli.exe run --profile <file> --tests <ids|category|all> --out <folder> [options of run]\n" +
            "  DCST4.Cli.exe generate --dataset <id> --out <folder> [options below]\n" +
            "  DCST4.Cli.exe list-datasets\n" +
            "  DCST4.Cli.exe list-codecs [--log-dir <folder>]\n" +
            "  DCST4.Cli.exe --version\n" +
            "\n" +
            "  --tests            Comma-separated test ids or categories, for example c-echo, negotiation-probe or Capability.\n" +
            "  --log-dir          Where to write the log. Default: %LocalAppData%\\DCST4\\Logs\n" +
            "  --patient-naming   RegistrySwedishName (default) or MarkedTestName, which gives DCST4^TEST^<number>.\n" +
            "\n" +
            "Options of run for the tests that store objects:\n" +
            "  --seed <n>                       Fixes the generated data. The same seed repeats the same UIDs, which are duplicates on a real system\n" +
            "  --matrix <file>                  A saved capability matrix to choose the transfer syntax from\n" +
            "  --transfer-syntax <name or UID>  Send everything in this syntax where the object type allows it\n" +
            "  --index-wait <seconds>           How long an object may take to appear in a query. Default 30\n" +
            "  --commit-linger <seconds>        Keep the Storage Commitment association open this long for the report. 0 releases at once. Default 10\n" +
            "  --commit-wait <seconds>          How long the Storage Commitment report may take. Default 60\n" +
            "  --commit-grace <seconds>         How long to go on listening for a late report after that. Default 30\n" +
            "\n" +
            "Options of run for the Limits tests, which strain the system and break transfers on purpose:\n" +
            "  --max-associations <n>           Stop limit-max-associations at this many open associations. Default 10\n" +
            "  --idle-wait <seconds>            How long limit-idle-timeout waits for an idle association to be ended. Default 120\n" +
            "  --artim-wait <seconds>           How long limit-artim waits on a connection with no association request. Default 60\n" +
            "  --fault-settle <seconds>         How long a fault test looks for the instance after a broken transfer. Default 5\n" +
            "\n" +
            "Options of generate:\n" +
            "  --seed <n>  --uid-root <root>  --patients <n>  --studies <n>  --series <n>  --instances <n>\n" +
            "  --rows <n>  --columns <n>  --bits <n>  --frames <n>\n" +
            "  --transfer-syntax <name or UID>   explicit (default), implicit, or a name from list-codecs\n" +
            "  --charset default|empty|latin1|utf8\n" +
            "  --edge private,long,nested\n" +
            "\n" +
            "Exit codes: 0 no FAIL or TOOL_ERROR, 1 at least one of those, 2 bad arguments, profile or output folder, 130 cancelled.";

        public string ProfilePath { get; private set; }
        public string Tests { get; private set; }
        public string OutputFolder { get; private set; }
        public string LogFolder { get; private set; }
        public TestPatientNaming PatientNaming { get; private set; } = TestPatientNaming.RegistrySwedishName;
        public int? Seed { get; private set; }
        public string MatrixPath { get; private set; }
        public FellowOakDicom.DicomTransferSyntax TransferSyntax { get; private set; }
        public int IndexWaitSeconds { get; private set; } = DCST4.Core.Testing.TestRunOptions.DefaultIndexWaitSeconds;
        public int CommitLingerSeconds { get; private set; } = DCST4.Core.Testing.TestRunOptions.DefaultCommitLingerSeconds;
        public int CommitWaitSeconds { get; private set; } = DCST4.Core.Testing.TestRunOptions.DefaultCommitWaitSeconds;
        public int CommitGraceSeconds { get; private set; } = DCST4.Core.Testing.TestRunOptions.DefaultCommitGraceSeconds;
        public int MaxAssociations { get; private set; } = DCST4.Core.Testing.TestRunOptions.DefaultMaxAssociations;
        public int IdleWaitSeconds { get; private set; } = DCST4.Core.Testing.TestRunOptions.DefaultIdleWaitSeconds;
        public int ArtimWaitSeconds { get; private set; } = DCST4.Core.Testing.TestRunOptions.DefaultArtimWaitSeconds;
        public int FaultSettleSeconds { get; private set; } = DCST4.Core.Testing.TestRunOptions.DefaultFaultSettleSeconds;

        private static bool TrySeconds(string name, string value, out int seconds, out string error)
        {
            error = null;
            if (int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out seconds)) return true;
            error = $"{name} must be a number of seconds, not '{value}'.";
            return false;
        }

        public static string PatientNamingError(string value) =>
            $"Unknown patient naming '{value}'. Use {nameof(TestPatientNaming.RegistrySwedishName)} or {nameof(TestPatientNaming.MarkedTestName)}.";

        /// <summary>Returns null and sets <paramref name="error"/> if the arguments after "run" are not usable.</summary>
        public static CliArguments Parse(IReadOnlyList<string> args, out string error)
        {
            error = null;
            var parsed = new CliArguments();

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
                    case "--profile": parsed.ProfilePath = value; break;
                    case "--tests": parsed.Tests = value; break;
                    case "--out": parsed.OutputFolder = value; break;
                    case "--log-dir": parsed.LogFolder = value; break;
                    case "--patient-naming":
                        if (!TestPatientNames.TryParse(value, out var naming))
                        {
                            error = PatientNamingError(value);
                            return null;
                        }
                        parsed.PatientNaming = naming;
                        break;
                    case "--seed":
                        if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seed))
                        {
                            error = $"--seed must be a whole number, not '{value}'.";
                            return null;
                        }
                        parsed.Seed = seed;
                        break;
                    case "--matrix": parsed.MatrixPath = value; break;
                    case "--transfer-syntax":
                        var syntax = DCST4.Core.Generation.Datasets.CompressionCatalog.Find(value);
                        if (syntax == null)
                        {
                            error = $"Unknown transfer syntax '{value}'. Use explicit, implicit, a name from list-codecs, or a Transfer Syntax UID.";
                            return null;
                        }
                        parsed.TransferSyntax = syntax.Syntax;
                        break;
                    case "--index-wait":
                        if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var wait))
                        {
                            error = $"--index-wait must be a number of seconds, not '{value}'.";
                            return null;
                        }
                        parsed.IndexWaitSeconds = wait;
                        break;
                    case "--commit-linger":
                        if (!TrySeconds(name, value, out var linger, out error)) return null;
                        parsed.CommitLingerSeconds = linger;
                        break;
                    case "--commit-wait":
                        if (!TrySeconds(name, value, out var commitWait, out error)) return null;
                        parsed.CommitWaitSeconds = commitWait;
                        break;
                    case "--commit-grace":
                        if (!TrySeconds(name, value, out var grace, out error)) return null;
                        parsed.CommitGraceSeconds = grace;
                        break;
                    case "--max-associations":
                        if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var associations))
                        {
                            error = $"--max-associations must be a whole number, not '{value}'.";
                            return null;
                        }
                        parsed.MaxAssociations = associations;
                        break;
                    case "--idle-wait":
                        if (!TrySeconds(name, value, out var idle, out error)) return null;
                        parsed.IdleWaitSeconds = idle;
                        break;
                    case "--artim-wait":
                        if (!TrySeconds(name, value, out var artim, out error)) return null;
                        parsed.ArtimWaitSeconds = artim;
                        break;
                    case "--fault-settle":
                        if (!TrySeconds(name, value, out var settle, out error)) return null;
                        parsed.FaultSettleSeconds = settle;
                        break;
                    default:
                        error = $"Unknown option {name}.";
                        return null;
                }
            }

            if (string.IsNullOrWhiteSpace(parsed.ProfilePath)) error = "--profile is required.";
            else if (string.IsNullOrWhiteSpace(parsed.Tests)) error = "--tests is required.";
            else if (string.IsNullOrWhiteSpace(parsed.OutputFolder)) error = "--out is required.";
            else error = new DCST4.Core.Testing.TestRunOptions
            {
                IndexWaitSeconds = parsed.IndexWaitSeconds,
                CommitLingerSeconds = parsed.CommitLingerSeconds,
                CommitWaitSeconds = parsed.CommitWaitSeconds,
                CommitGraceSeconds = parsed.CommitGraceSeconds,
                MaxAssociations = parsed.MaxAssociations,
                IdleWaitSeconds = parsed.IdleWaitSeconds,
                ArtimWaitSeconds = parsed.ArtimWaitSeconds,
                FaultSettleSeconds = parsed.FaultSettleSeconds,
            }.Validate();
            return error == null ? parsed : null;
        }
    }
}
