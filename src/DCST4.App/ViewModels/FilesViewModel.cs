using DCST4.Core.Files;
using DCST4.Core.Performance;
using DCST4.Core.Pipeline;
using DCST4.Infrastructure;
using DCST4.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace DCST4.ViewModels
{
    /// <summary>
    /// The Files tab: a folder or DICOMDIR of existing files, what was found in it, and how it is to be sent. Reading the folder
    /// and sending are started by <see cref="MainViewModel"/>, which owns the busy state and the Cancel command.
    /// </summary>
    public sealed class FilesViewModel : INotifyPropertyChanged
    {
        /// <summary>The group of the choice that keeps each file's own syntax, before the codec families.</summary>
        public const string AsInFilesFamily = "Files";
        public const string AsInFilesName = "As in the files";
        private const int MaxSkippedShown = 200;

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        /// <summary>Raised when what a run of the files would send changes, for the custom mix on the Performance tab.</summary>
        public event Action Changed;

        public FilesViewModel()
        {
            SyntaxView = FamilyGroupedView.Create(SyntaxChoices);
            ReloadFromSettings();
        }

        public void ReloadFromSettings()
        {
            _folder = Settings.Default.FilesFolder ?? string.Empty;
            _includeSubfolders = Settings.Default.FilesIncludeSubfolders;
            _selectedSyntax = SyntaxChoices[0];
            _allowFallback = false;
            Clear();
        }

        private string _folder;
        /// <summary>A folder, or a DICOMDIR file.</summary>
        public string Folder { get => _folder; set { _folder = value ?? string.Empty; OnPropertyChanged(); Settings.Default.FilesFolder = _folder; } }

        private bool _includeSubfolders;
        public bool IncludeSubfolders { get => _includeSubfolders; set { _includeSubfolders = value; OnPropertyChanged(); Settings.Default.FilesIncludeSubfolders = value; } }

        /// <summary>What the folder held when it was last read. Null before a folder is read.</summary>
        public FolderScan Scan { get; private set; }
        public bool HasFiles => Scan != null && Scan.Files.Count > 0;

        public string SummaryText => _readProblem != null ? _readProblem
            : Scan == null ? "No folder read yet. Select a folder or a DICOMDIR; it is read at once."
            : Scan.Files.Count == 0 ? "No DICOM files found." + (Scan.Skipped.Count > 0 ? " The skipped files are listed below." : string.Empty)
            : (Scan.DicomDirPath != null ? "Through the DICOMDIR: " : string.Empty) + Scan.Summary + ".";

        private string _readProblem;
        private string _scannedFolder;
        private bool _scannedSubfolders;

        /// <summary>True when the scan is of the folder and the subfolder choice shown now, so it need not be read again.</summary>
        public bool IsScanCurrent => Scan != null && string.Equals(_scannedFolder, (Folder ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)
            && _scannedSubfolders == IncludeSubfolders;
        public string SyntaxText => HasFiles ? Scan.SyntaxSummary : string.Empty;
        public string ModalityText => HasFiles ? string.Join(", ", Scan.Modalities) : string.Empty;
        public string SkippedHeader => Scan == null || Scan.Skipped.Count == 0 ? "Skipped: none" : $"Skipped: {Scan.Skipped.Count}";
        public IReadOnlyList<SkippedFile> Skipped => Scan == null ? (IReadOnlyList<SkippedFile>)new SkippedFile[0] : Scan.Skipped.Take(MaxSkippedShown).ToList();

        private bool _reidentify = true;
        /// <summary>True: each patient becomes a test patient, with new UIDs and DCST4's marking. False: the files go unchanged.</summary>
        public bool Reidentify
        {
            get => _reidentify;
            set { _reidentify = value; OnPropertyChanged(); OnPropertyChanged(nameof(KeepUnchanged)); OnPropertyChanged(nameof(EffectText)); Changed?.Invoke(); }
        }

        /// <summary>The other radio button.</summary>
        public bool KeepUnchanged { get => !_reidentify; set => Reidentify = !value; }

        /// <summary>What sending will do to these files, one sentence a line.</summary>
        public string EffectText => HasFiles ? string.Join("\n", FilesDescription.Describe(Scan, _reidentify)) : string.Empty;

        /// <summary>"As in the files" first, which keeps each file's own syntax and compressed pixel data, then every syntax a run can use.</summary>
        public IReadOnlyList<StoreSyntaxChoice> SyntaxChoices { get; } =
            new[] { new StoreSyntaxChoice(AsInFilesFamily, AsInFilesName, null) }
                .Concat(TransferSyntaxOption.All.Select(o => new StoreSyntaxChoice(o.Family, o.Name, o.Syntax))).ToList();
        public ICollectionView SyntaxView { get; }

        private StoreSyntaxChoice _selectedSyntax;
        public StoreSyntaxChoice SelectedSyntax
        {
            get => _selectedSyntax ?? SyntaxChoices[0];
            set { _selectedSyntax = value ?? SyntaxChoices[0]; OnPropertyChanged(); Changed?.Invoke(); }
        }

        private bool _allowFallback;
        public bool AllowFallback { get => _allowFallback; set { _allowFallback = value; OnPropertyChanged(); } }

        /// <summary>The files as the custom mix of the Performance tab. Null before a folder with files is read.</summary>
        public DataMix BuildMix() => HasFiles ? DataMix.FromFiles(Scan, SelectedSyntax.Syntax, SelectedSyntax.Name) : null;

        /// <summary>A new folder was read. Re-identifying is chosen again, since that is the default for every new folder.</summary>
        public void SetScan(FolderScan scan, string folder, bool includeSubfolders)
        {
            Scan = scan;
            _scannedFolder = (folder ?? string.Empty).Trim();
            _scannedSubfolders = includeSubfolders;
            _readProblem = null;
            _reidentify = true;
            RaiseAll();
        }

        /// <summary>The folder could not be read. The reason is shown on the tab instead of in a message box.</summary>
        public void SetReadProblem(string problem)
        {
            Clear();
            _readProblem = problem;
            OnPropertyChanged(nameof(SummaryText));
        }

        public void Clear()
        {
            Scan = null;
            _scannedFolder = null;
            _readProblem = null;
            _reidentify = true;
            RaiseAll();
        }

        private void RaiseAll()
        {
            OnPropertyChanged(string.Empty);
            Changed?.Invoke();
        }
    }
}
