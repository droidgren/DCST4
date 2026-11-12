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
            "  DCST4.Cli.exe report --run <file.json> [--out <file.html>]\n" +
            "  DCST4.Cli.exe compare --runs <a.json> <b.json> [<c.json> ...] [--threshold <percent>] [--out <file.html>]\n" +
            "  DCST4.Cli.exe generate --dataset <id> --out <folder> [options below]\n" +
            "  DCST4.Cli.exe list-datasets\n" +
            "  DCST4.Cli.exe list-codecs [--log-dir <folder>]\n" +
            "  DCST4.Cli.exe list-mixes\n" +
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
            "Options of run for the Performance tests (load-throughput, load-ramp, load-soak), which store many new objects.\n" +
            "They are not part of 'all': name them or use --tests Performance. The plan is printed before anything is sent.\n" +
            "  --load-associations <n>          Associations at once. Default 4\n" +
            "  --load-duration <time>           How long load-throughput runs, as 20, 90s, 5m or 1h. Default 20 s\n" +
            "  --load-count <n>                 Send exactly this many instead of running for a duration\n" +
            "  --load-per-association <n>       Release and open a new association after this many. 0 (default) keeps one\n" +
            "  --load-rate <n>                  Instances per second over all associations. 0 (default) is as fast as answered\n" +
            "  --load-mix <id>                  What is sent, from list-mixes. Default ct\n" +
            "  --load-pdu <bytes>               Maximum PDU length announced and sent with. 0 (default) is fo-dicom's default\n" +
            "  --load-warmup <seconds>          Left out of the summary at the start of the run and of each ramp step. Default 2\n" +
            "  --load-max-instances <n>         The most instances a run sends. Default 5000, 12000 for the ramp, rate x duration for the soak\n" +
            "  --ramp-steps <list>              Associations per ramp step. Default 1,2,4,8\n" +
            "  --ramp-step-duration <time>      How long each ramp step runs. Default 10 s\n" +
            "  --soak-duration <time>           How long load-soak runs. Default 30m\n" +
            "  --soak-rate <n>                  Instances per second during the soak. Default 2\n" +
            "  --soak-snapshot <time>           How often the soak writes a snapshot. Default 60 s\n" +
            "  --tta-sample <n>                 Query for 1 in n stored instances, for time-to-available. 0 is off. Default 50\n" +
            "  --tta-interval <seconds>         How often the sampled instances are queried. Default 2\n" +
            "  --tta-timeout <seconds>          How long a sampled instance may take to be found. Default 60\n" +
            "\n" +
            "Options of generate:\n" +
            "  --seed <n>  --uid-root <root>  --patients <n>  --studies <n>  --series <n>  --instances <n>\n" +
            "  --rows <n>  --columns <n>  --bits <n>  --frames <n>\n" +
            "  --transfer-syntax <name or UID>   explicit (default), implicit, or a name from list-codecs\n" +
            "  --charset default|empty|latin1|utf8\n" +
            "  --edge private,long,nested\n" +
            "\n" +
            "report writes a run as one HTML file that is complete in itself, beside the JSON file unless --out says otherwise.\n" +
            "run writes the same file by itself. compare takes the first run as the baseline and writes DCST4-compare-<time>.html\n" +
            "in the folder of the last run, unless --out says otherwise.\n" +
            "  --threshold <percent>   How much lower a throughput or higher a p95 latency may be before it is a regression. Default 10\n" +
            "\n" +
            "Exit codes of run: 0 no FAIL or TOOL_ERROR, 1 at least one of those, 2 bad arguments, profile or output folder, 130 cancelled.\n" +
            "Exit codes of compare: 0 no regression, 1 at least one regression, 2 bad arguments or a file that cannot be read.\n" +
            "Exit codes of report: 0 written, 2 bad arguments or a file that cannot be read or written.";

        public string ProfilePath { get; private set; }
        public string Tests { get; private set; }
        public string OutputFolder { get; private set; }
        public string LogFolder { get; private set; }
        public TestPatientNaming PatientNaming { get; private set; } = TestPatientNames.Default;
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
        public DCST4.Core.Performance.LoadSettings Load { get; } = new DCST4.Core.Performance.LoadSettings();

        private static bool TryWhole(string name, string value, out int number, out string error)
        {
            error = null;
            if (int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out number)) return true;
            error = $"{name} must be a whole number, not '{value}'.";
            return false;
        }

        private static bool TryNumber(string name, string value, out double number, out string error)
        {
            error = null;
            if (double.TryParse(value, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out number)) return true;
            error = $"{name} must be a number, not '{value}'.";
            return false;
        }

        private static bool TryDuration(string name, string value, out double seconds, out string error)
        {
            error = null;
            if (DCST4.Core.Performance.LoadSettings.TryParseDuration(value, out seconds)) return true;
            error = $"{name} must be a time such as 90, 90s, 30m or 8h, not '{value}'.";
            return false;
        }

        /// <summary>The options of the Performance tests. Returns false with an error, true if the option was one of them, null if it was not.</summary>
        private bool? TryLoadOption(string name, string value, out string error)
        {
            error = null;
            var load = Load;
            int whole;
            double number;
            switch (name)
            {
                case "--load-associations": if (!TryWhole(name, value, out whole, out error)) return false; load.Associations = whole; return true;
                case "--load-duration": if (!TryDuration(name, value, out number, out error)) return false; load.DurationSeconds = number; return true;
                case "--load-count": if (!TryWhole(name, value, out whole, out error)) return false; load.InstanceCount = whole; return true;
                case "--load-per-association": if (!TryWhole(name, value, out whole, out error)) return false; load.InstancesPerAssociation = whole; return true;
                case "--load-rate": if (!TryNumber(name, value, out number, out error)) return false; load.RatePerSecond = number; return true;
                case "--load-mix":
                    if (DCST4.Core.Performance.DataMixCatalog.Find(value) == null)
                    {
                        error = $"Unknown data mix '{value}'. Use one from list-mixes.";
                        return false;
                    }
                    load.Mix = value.Trim().ToLowerInvariant();
                    return true;
                case "--load-pdu":
                    if (!uint.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pdu))
                    {
                        error = $"{name} must be a number of bytes, not '{value}'.";
                        return false;
                    }
                    load.MaxPdu = pdu;
                    return true;
                case "--load-warmup": if (!TryNumber(name, value, out number, out error)) return false; load.WarmUpSeconds = number; return true;
                case "--load-max-instances": if (!TryWhole(name, value, out whole, out error)) return false; load.MaxInstances = whole; return true;
                case "--ramp-steps":
                    var steps = DCST4.Core.Performance.LoadSettings.ParseSteps(value);
                    if (steps == null)
                    {
                        error = $"{name} must be a list of whole numbers such as 1,2,4,8, not '{value}'.";
                        return false;
                    }
                    load.RampSteps = steps;
                    return true;
                case "--ramp-step-duration": if (!TryDuration(name, value, out number, out error)) return false; load.RampStepSeconds = number; return true;
                case "--soak-duration": if (!TryDuration(name, value, out number, out error)) return false; load.SoakSeconds = number; return true;
                case "--soak-rate": if (!TryNumber(name, value, out number, out error)) return false; load.SoakRatePerSecond = number; return true;
                case "--soak-snapshot": if (!TryDuration(name, value, out number, out error)) return false; load.SnapshotSeconds = number; return true;
                case "--tta-sample": if (!TryWhole(name, value, out whole, out error)) return false; load.SampleEvery = whole; return true;
                case "--tta-interval": if (!TryNumber(name, value, out number, out error)) return false; load.PollIntervalSeconds = number; return true;
                case "--tta-timeout": if (!TryNumber(name, value, out number, out error)) return false; load.PollTimeoutSeconds = number; return true;
                default: return null;
            }
        }

        private static bool TrySeconds(string name, string value, out int seconds, out string error)
        {
            error = null;
            if (int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out seconds)) return true;
            error = $"{name} must be a number of seconds, not '{value}'.";
            return false;
        }

        public static string PatientNamingError(string value) =>
            $"Unknown patient naming '{value}'. Use {nameof(TestPatientNaming.MarkedTestName)} or {nameof(TestPatientNaming.RegistrySwedishName)}.";

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
                        var load = parsed.TryLoadOption(name.ToLowerInvariant(), value, out error);
                        if (load == true) break;
                        if (load == null) error = $"Unknown option {name}.";
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
