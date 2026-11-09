using System;
using System.Collections.Generic;

namespace DCST4.Cli
{
    /// <summary>The parsed command line of "run".</summary>
    internal sealed class CliArguments
    {
        public const string Usage =
            "Usage:\n" +
            "  DCST4.Cli.exe run --profile <file> --tests <ids|category|all> --out <folder> [--log-dir <folder>]\n" +
            "  DCST4.Cli.exe --version\n" +
            "\n" +
            "  --tests     Comma-separated test ids or categories, for example c-echo or Capability.\n" +
            "  --log-dir   Where to write the log. Default: %LocalAppData%\\DCST4\\Logs\n" +
            "\n" +
            "Exit codes: 0 no FAIL or TOOL_ERROR, 1 at least one of those, 2 bad arguments, profile or output folder, 130 cancelled.";

        public string ProfilePath { get; private set; }
        public string Tests { get; private set; }
        public string OutputFolder { get; private set; }
        public string LogFolder { get; private set; }

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
                    default:
                        error = $"Unknown option {name}.";
                        return null;
                }
            }

            if (string.IsNullOrWhiteSpace(parsed.ProfilePath)) error = "--profile is required.";
            else if (string.IsNullOrWhiteSpace(parsed.Tests)) error = "--tests is required.";
            else if (string.IsNullOrWhiteSpace(parsed.OutputFolder)) error = "--out is required.";
            return error == null ? parsed : null;
        }
    }
}
