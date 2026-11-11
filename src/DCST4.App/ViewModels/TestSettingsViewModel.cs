using DCST4.Core.Testing;
using System.Collections.Generic;
using System.Globalization;

namespace DCST4.ViewModels
{
    /// <summary>
    /// The Test settings dialog: a copy of the settings of the Tests tab, so that Cancel leaves them as they were. OK checks
    /// them all and only then hands them to <see cref="MainViewModel"/>. Numbers are text here, so that a typo is reported
    /// instead of being dropped by the binding.
    /// </summary>
    public sealed class TestSettingsViewModel
    {
        private readonly MainViewModel _main;

        public TestSettingsViewModel(MainViewModel main)
        {
            _main = main;
            UidRoot = main.TestUidRoot;
            StoreSyntaxChoices = main.TestSuite.StoreSyntaxChoices;
            StoreSyntax = main.TestSuite.SelectedStoreSyntax;
            ResultsFolder = main.TestSuite.ResultsFolder;
            CommitLingerSeconds = Text(main.CommitLingerSeconds);
            CommitWaitSeconds = Text(main.CommitWaitSeconds);
            CommitGraceSeconds = Text(main.CommitGraceSeconds);
            MaxAssociations = Text(main.LimitMaxAssociations);
            IdleWaitSeconds = Text(main.LimitIdleWaitSeconds);
            ArtimWaitSeconds = Text(main.LimitArtimWaitSeconds);
            FaultSettleSeconds = Text(main.FaultSettleSeconds);
            ExpiredClientCertThumbprint = main.TlsExpiredClientCertThumbprint;
        }

        public string UidRoot { get; set; }
        public IReadOnlyList<StoreSyntaxChoice> StoreSyntaxChoices { get; }
        public StoreSyntaxChoice StoreSyntax { get; set; }
        public string ResultsFolder { get; set; }
        public string CommitLingerSeconds { get; set; }
        public string CommitWaitSeconds { get; set; }
        public string CommitGraceSeconds { get; set; }
        public string MaxAssociations { get; set; }
        public string IdleWaitSeconds { get; set; }
        public string ArtimWaitSeconds { get; set; }
        public string FaultSettleSeconds { get; set; }
        public string ExpiredClientCertThumbprint { get; set; }

        /// <summary>Checks every value and, if all can be used, applies them. Returns the first problem, or null when applied.</summary>
        public string Apply()
        {
            var problem = TargetProfile.ValidateUidRoot(UidRoot);
            if (problem != null) return problem;
            if (string.IsNullOrWhiteSpace(ResultsFolder)) return "Enter a folder for the results.";

            if (!TryRead(CommitLingerSeconds, "Keep the association open", out var linger, ref problem)
                || !TryRead(CommitWaitSeconds, "Wait for the report", out var wait, ref problem)
                || !TryRead(CommitGraceSeconds, "Then keep listening", out var grace, ref problem)
                || !TryRead(MaxAssociations, "Associations up to", out var associations, ref problem)
                || !TryRead(IdleWaitSeconds, "Idle wait", out var idle, ref problem)
                || !TryRead(ArtimWaitSeconds, "ARTIM wait", out var artim, ref problem)
                || !TryRead(FaultSettleSeconds, "After a fault, look for", out var settle, ref problem))
            {
                return problem;
            }

            problem = new TestRunOptions
            {
                CommitLingerSeconds = linger,
                CommitWaitSeconds = wait,
                CommitGraceSeconds = grace,
                MaxAssociations = associations,
                IdleWaitSeconds = idle,
                ArtimWaitSeconds = artim,
                FaultSettleSeconds = settle,
            }.Validate();
            if (problem != null) return problem;

            _main.TestUidRoot = UidRoot.Trim();
            _main.TestSuite.SelectedStoreSyntax = StoreSyntax ?? StoreSyntaxChoices[0];
            _main.TestSuite.ResultsFolder = ResultsFolder.Trim();
            _main.CommitLingerSeconds = linger;
            _main.CommitWaitSeconds = wait;
            _main.CommitGraceSeconds = grace;
            _main.LimitMaxAssociations = associations;
            _main.LimitIdleWaitSeconds = idle;
            _main.LimitArtimWaitSeconds = artim;
            _main.FaultSettleSeconds = settle;
            _main.TlsExpiredClientCertThumbprint = (ExpiredClientCertThumbprint ?? string.Empty).Trim();
            return null;
        }

        private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

        private static bool TryRead(string text, string name, out int value, ref string problem)
        {
            if (int.TryParse((text ?? string.Empty).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value)) return true;
            problem = $"{name} must be a whole number of 0 or more.";
            return false;
        }
    }
}
