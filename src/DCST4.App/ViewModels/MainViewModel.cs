using DCST4.Core.Capability;
using DCST4.Core.Generation;
using DCST4.Core.Generation.Modalities;
using DCST4.Core.Network;
using DCST4.Core.Pipeline;
using DCST4.Core.Reporting;
using DCST4.Core.Testing;
using DCST4.Infrastructure;
using DCST4.Models;
using DCST4.Properties;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace DCST4.ViewModels
{
    /// <summary>The main window. Generating and sending live in DCST4.Core; this class only connects them to the screen.</summary>
    public sealed class MainViewModel : INotifyPropertyChanged
    {
        private const int MaxLogLines = 5000;
        private const string StudyDateTimeFormat = "yyyy-MM-dd HH:mm:ss";
        private const string NoSizeEstimate = "-";
        private const int SizeMeasureDelayMs = 300;

        private readonly ILogger _logger = Log.ForContext<MainViewModel>();
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
        private readonly DispatcherTimer _logTimer;
        private readonly DispatcherTimer _sizeTimer;
        private CancellationTokenSource _sizeCts;
        private int _sizeSequence;
        private string _measuredSizeKey;
        private long _measuredImageBytes;
        private CancellationTokenSource _cts;
        private int _previewSeed = Environment.TickCount;
        private string _previewStudyUid;
        private ExamIdentity _identity;
        private bool _loading = true;

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        #region Properties - Connection
        private string _callingAET;
        public string CallingAET { get => _callingAET; set { _callingAET = value; OnPropertyChanged(); Settings.Default.CallingAET = value; } }

        private string _serverAET;
        public string ServerAET { get => _serverAET; set { _serverAET = value; OnPropertyChanged(); Settings.Default.ServerAET = value; } }

        private string _serverHost;
        public string ServerHost { get => _serverHost; set { _serverHost = value; OnPropertyChanged(); Settings.Default.ServerHost = value; } }

        private int _serverPort;
        public int ServerPort { get => _serverPort; set { _serverPort = value; OnPropertyChanged(); Settings.Default.ServerPort = value; } }

        private string _localScpAe;
        /// <summary>The AE title the PACS must know this computer by for C-MOVE.</summary>
        public string LocalScpAe { get => _localScpAe; set { _localScpAe = value; OnPropertyChanged(); Settings.Default.LocalScpAe = value; } }

        private int _localScpPort;
        public int LocalScpPort { get => _localScpPort; set { _localScpPort = value; OnPropertyChanged(); Settings.Default.LocalScpPort = value; } }

        private string _testUidRoot;
        public string TestUidRoot { get => _testUidRoot; set { _testUidRoot = value; OnPropertyChanged(); Settings.Default.TestUidRoot = value; } }

        private int _commitLingerSeconds;
        /// <summary>How long the Storage Commitment association stays open for the report. 0 releases at once.</summary>
        public int CommitLingerSeconds { get => _commitLingerSeconds; set { _commitLingerSeconds = value; OnPropertyChanged(); Settings.Default.CommitLingerSeconds = value; } }

        private int _commitWaitSeconds;
        public int CommitWaitSeconds { get => _commitWaitSeconds; set { _commitWaitSeconds = value; OnPropertyChanged(); Settings.Default.CommitWaitSeconds = value; } }

        private int _commitGraceSeconds;
        public int CommitGraceSeconds { get => _commitGraceSeconds; set { _commitGraceSeconds = value; OnPropertyChanged(); Settings.Default.CommitGraceSeconds = value; } }

        private int _limitMaxAssociations;
        /// <summary>Where the limit test stops opening associations.</summary>
        public int LimitMaxAssociations { get => _limitMaxAssociations; set { _limitMaxAssociations = value; OnPropertyChanged(); Settings.Default.LimitMaxAssociations = value; } }

        private int _limitIdleWaitSeconds;
        public int LimitIdleWaitSeconds { get => _limitIdleWaitSeconds; set { _limitIdleWaitSeconds = value; OnPropertyChanged(); Settings.Default.LimitIdleWaitSeconds = value; } }

        private int _limitArtimWaitSeconds;
        public int LimitArtimWaitSeconds { get => _limitArtimWaitSeconds; set { _limitArtimWaitSeconds = value; OnPropertyChanged(); Settings.Default.LimitArtimWaitSeconds = value; } }

        private int _faultSettleSeconds;
        public int FaultSettleSeconds { get => _faultSettleSeconds; set { _faultSettleSeconds = value; OnPropertyChanged(); Settings.Default.FaultSettleSeconds = value; } }

        /// <summary>Which files a run on the Tests or Performance tab writes. All three unless changed in Test settings.</summary>
        public bool WriteResultJson { get => _writeResultJson; set { _writeResultJson = value; OnPropertyChanged(); Settings.Default.WriteResultJson = value; } }
        public bool WriteResultCsv { get => _writeResultCsv; set { _writeResultCsv = value; OnPropertyChanged(); Settings.Default.WriteResultCsv = value; } }
        public bool WriteResultHtml { get => _writeResultHtml; set { _writeResultHtml = value; OnPropertyChanged(); Settings.Default.WriteResultHtml = value; } }
        /// <summary>After a run, result files in the results folder older than this many days are deleted. 0 keeps everything.</summary>
        public int ResultsKeepDays { get => _resultsKeepDays; set { _resultsKeepDays = value; OnPropertyChanged(); Settings.Default.ResultsKeepDays = value; } }
        private bool _writeResultJson = true, _writeResultCsv = true, _writeResultHtml = true;
        private int _resultsKeepDays;

        private string _tlsExpiredClientCertThumbprint;
        /// <summary>Thumbprint of an expired client certificate in CurrentUser\My for tls-client-cert-expired, or empty for none.</summary>
        public string TlsExpiredClientCertThumbprint
        {
            get => _tlsExpiredClientCertThumbprint;
            set { _tlsExpiredClientCertThumbprint = value ?? string.Empty; OnPropertyChanged(); Settings.Default.TlsExpiredClientCertThumbprint = _tlsExpiredClientCertThumbprint; }
        }

        private bool _useTLS;
        public bool UseTLS { get => _useTLS; set { _useTLS = value; OnPropertyChanged(); Settings.Default.UseTLS = value; } }

        private bool _tlsIgnoreCertErrors;
        /// <summary>Accept self-signed or otherwise invalid server certificates.</summary>
        public bool TlsIgnoreCertErrors { get => _tlsIgnoreCertErrors; set { _tlsIgnoreCertErrors = value; OnPropertyChanged(); Settings.Default.TlsIgnoreCertErrors = value; } }

        private string _tlsClientCertThumbprint;
        /// <summary>Thumbprint of the client certificate in CurrentUser\My, or empty for none.</summary>
        public string TlsClientCertThumbprint
        {
            get => _tlsClientCertThumbprint;
            set { _tlsClientCertThumbprint = value ?? string.Empty; OnPropertyChanged(); Settings.Default.TlsClientCertThumbprint = _tlsClientCertThumbprint; }
        }
        #endregion

        #region Properties - Profiles
        public ObservableCollection<ConnectionProfile> Profiles { get; } = new ObservableCollection<ConnectionProfile>();

        private string _profileName = string.Empty;
        /// <summary>Text of the editable profile combo box: the name used by Save.</summary>
        public string ProfileName { get => _profileName; set { _profileName = value ?? string.Empty; OnPropertyChanged(); } }

        private ConnectionProfile _selectedProfile;
        /// <summary>Selecting a profile copies its values into the connection fields.</summary>
        public ConnectionProfile SelectedProfile
        {
            get => _selectedProfile;
            set
            {
                if (ReferenceEquals(_selectedProfile, value)) return;
                _selectedProfile = value;
                OnPropertyChanged();
                if (value != null) ApplyProfile(value);
            }
        }
        #endregion

        #region Properties - Exam
        public IReadOnlyList<ModalityProfile> Modalities => ModalityCatalog.All;
        public IReadOnlyList<string> CharacterSets { get; } = new[] { DicomCharacterSets.Latin1, DicomCharacterSets.Utf8, DicomCharacterSets.EmptyDisplayName };
        public IReadOnlyList<TransferSyntaxOption> TransferSyntaxes => TransferSyntaxOption.All;
        public ObservableCollection<ExamPreset> Presets { get; } = new ObservableCollection<ExamPreset>();
        public ObservableCollection<int> BitDepths { get; } = new ObservableCollection<int>();

        private ModalityProfile _selectedModality;
        public ModalityProfile SelectedModality
        {
            get => _selectedModality;
            set
            {
                if (value == null || ReferenceEquals(_selectedModality, value)) return;
                _selectedModality = value;
                OnPropertyChanged();
                Settings.Default.Modality = value.Code;
                FillModalityLists();
                if (!_loading)
                {
                    Rows = value.DefaultRows;
                    Columns = value.DefaultColumns;
                    BitsStored = value.DefaultBitDepth;
                    SelectedPreset = value.Presets[0];
                }
            }
        }

        private ExamPreset _selectedPreset;
        public ExamPreset SelectedPreset
        {
            get => _selectedPreset;
            set
            {
                if (value == null || ReferenceEquals(_selectedPreset, value)) return;
                _selectedPreset = value;
                OnPropertyChanged();
                Settings.Default.ExamType = value.Name;
                if (!_loading)
                {
                    ImagesPerSeries = value.DefaultImages;
                    RefreshPreview();
                }
            }
        }

        private int _seriesCount;
        public int SeriesCount { get => _seriesCount; set { _seriesCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(TotalImagesText)); Settings.Default.SeriesCount = value; UpdateManualUidRules(); } }

        private int _imagesPerSeries;
        public int ImagesPerSeries { get => _imagesPerSeries; set { _imagesPerSeries = value; OnPropertyChanged(); OnPropertyChanged(nameof(TotalImagesText)); Settings.Default.ImagesPerSeries = value; UpdateManualUidRules(); } }

        public string TotalImagesText => $"{Math.Max(0, (long)_seriesCount * _imagesPerSeries)} images in total";

        private int _rows;
        public int Rows { get => _rows; set { _rows = value; OnPropertyChanged(); Settings.Default.Rows = value; RefreshSizeEstimate(); } }

        private int _columns;
        public int Columns { get => _columns; set { _columns = value; OnPropertyChanged(); Settings.Default.Columns = value; RefreshSizeEstimate(); } }

        private int _bitsStored;
        public int BitsStored { get => _bitsStored; set { _bitsStored = value; OnPropertyChanged(); Settings.Default.BitsStored = value; RefreshSizeEstimate(); } }

        private string _estimatedSizeText = NoSizeEstimate;
        /// <summary>The size of the whole exam, as shown in the Exam box.</summary>
        public string EstimatedSizeText { get => _estimatedSizeText; private set { _estimatedSizeText = value; OnPropertyChanged(); } }

        private string _characterSet;
        /// <summary>As shown in the list and saved in the settings. <see cref="CharacterSetTagValue"/> is what goes in the image.</summary>
        public string CharacterSet { get => _characterSet; set { _characterSet = value; OnPropertyChanged(); Settings.Default.CharacterSet = value; RefreshPreview(); } }

        private string CharacterSetTagValue => DicomCharacterSets.FromDisplayName(_characterSet);

        private TransferSyntaxOption _selectedTransferSyntax;
        public TransferSyntaxOption SelectedTransferSyntax
        {
            get => _selectedTransferSyntax;
            set
            {
                if (value == null) return;
                _selectedTransferSyntax = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FallbackIsRelevant));
                Settings.Default.TransferSyntax = value.Syntax.UID.UID;
                RefreshSizeEstimate();
            }
        }

        private bool _allowFallback;
        public bool AllowFallback { get => _allowFallback; set { _allowFallback = value; OnPropertyChanged(); Settings.Default.AllowFallback = value; } }

        /// <summary>With Implicit VR selected there is nothing to fall back to.</summary>
        public bool FallbackIsRelevant => !ReferenceEquals(_selectedTransferSyntax, TransferSyntaxOption.ImplicitVrLe);

        private int _minAge;
        public int MinAge { get => _minAge; set { _minAge = value; OnPropertyChanged(); Settings.Default.MinAge = value; RefreshPreview(); } }

        private int _maxAge;
        public int MaxAge { get => _maxAge; set { _maxAge = value; OnPropertyChanged(); Settings.Default.MaxAge = value; RefreshPreview(); } }
        #endregion

        #region Properties - Identifiers
        public ObservableCollection<IdentityFieldViewModel> IdentityFields { get; } = new ObservableCollection<IdentityFieldViewModel>();

        public TestSuiteViewModel TestSuite { get; } = new TestSuiteViewModel();

        public IdentityFieldViewModel PatientNameField { get; }
        public IdentityFieldViewModel PatientIdField { get; }
        public IdentityFieldViewModel BirthDateField { get; }
        public IdentityFieldViewModel SexField { get; }
        public IdentityFieldViewModel AccessionField { get; }
        public IdentityFieldViewModel StudyIdField { get; }
        public IdentityFieldViewModel StudyUidField { get; }
        public IdentityFieldViewModel SeriesUidField { get; }
        public IdentityFieldViewModel SopUidField { get; }
        public IdentityFieldViewModel StudyDateTimeField { get; }
        public IdentityFieldViewModel ReferringField { get; }
        public IdentityFieldViewModel StudyDescriptionField { get; }
        public IdentityFieldViewModel SeriesDescriptionField { get; }
        public IdentityFieldViewModel InstitutionField { get; }
        public IdentityFieldViewModel StationField { get; }

        /// <summary>Read from the settings each time, so a change in the Settings dialog applies to the next run.</summary>
        public bool NewValuesAfterRun { get => Settings.Default.NewValuesAfterRun; set => Settings.Default.NewValuesAfterRun = value; }

        /// <summary>Read from the settings at each run, like <see cref="NewValuesAfterRun"/>.</summary>
        public bool BurnInText { get => Settings.Default.BurnInText; set => Settings.Default.BurnInText = value; }

        /// <summary>The saved selection of tags to burn in, as <see cref="BurnedInTagCatalog.Format"/> writes it.</summary>
        public string BurnInTags { get => Settings.Default.BurnInTags; set => Settings.Default.BurnInTags = value; }

        /// <summary>The tags added in Settings, and the row each has on the Identifiers tab.</summary>
        private readonly List<AdditionalField> _additionalFields = new List<AdditionalField>();

        private sealed class AdditionalField
        {
            public AdditionalTag Tag;
            public IdentityFieldViewModel Field;
        }

        /// <summary>The saved list of added tags. Setting it updates the rows on the Identifiers tab.</summary>
        public IReadOnlyList<AdditionalTagSetting> AdditionalTagSettings
        {
            get => ReadAdditionalTagSettings();
            set
            {
                Settings.Default.AdditionalTags = AdditionalTagSetting.Serialize(value);
                ReloadAdditionalTags();
            }
        }

        private string _identityMessage;
        /// <summary>Warning or problem shown under the identifiers, or null.</summary>
        public string IdentityMessage { get => _identityMessage; private set { _identityMessage = value; OnPropertyChanged(); } }
        #endregion

        #region Properties - Output and results
        private bool _sendImages;
        public bool SendImages { get => _sendImages; set { _sendImages = value; OnPropertyChanged(); OnPropertyChanged(nameof(RunButtonText)); Settings.Default.SendImages = value; } }

        private bool _saveImages;
        public bool SaveImages { get => _saveImages; set { _saveImages = value; OnPropertyChanged(); OnPropertyChanged(nameof(RunButtonText)); Settings.Default.SaveImages = value; } }

        private string _saveFolder;
        public string SaveFolder { get => _saveFolder; set { _saveFolder = value ?? string.Empty; OnPropertyChanged(); Settings.Default.SaveFolder = _saveFolder; } }

        public string RunButtonText => _saveImages && !_sendImages ? "Generate & Save" : "Generate & Send";

        public ObservableCollection<StoreResult> Results { get; } = new ObservableCollection<StoreResult>();
        public ObservableCollection<string> LogLines { get; } = new ObservableCollection<string>();

        private string _statusText = "Ready.";
        public string StatusText { get => _statusText; private set { _statusText = value; OnPropertyChanged(); } }

        private string _lastRunText = string.Empty;
        public string LastRunText { get => _lastRunText; private set { _lastRunText = value; OnPropertyChanged(); } }

        private int _progressMaximum = 1;
        public int ProgressMaximum { get => _progressMaximum; private set { _progressMaximum = value; OnPropertyChanged(); } }

        private int _progressValue;
        public int ProgressValue { get => _progressValue; private set { _progressValue = value; OnPropertyChanged(); } }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                _isBusy = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsIdle));
                CommandManager.InvalidateRequerySuggested();
            }
        }
        public bool IsIdle => !_isBusy;
        #endregion

        #region Replaceable for tests
        /// <summary>Shows a message. Message boxes are only used for things the user started directly and before a run, never during one.</summary>
        public Action<string, MessageBoxImage, string> ShowMessage { get; set; } = (message, icon, title) =>
            MessageBox.Show(message, title ?? DefaultTitle(icon), MessageBoxButton.OK, icon);

        /// <summary>Asks the user a yes/no question.</summary>
        public Func<string, bool> Confirm { get; set; } = message => ConfirmDialog.Ask(message);

        /// <summary>Writes the settings to disk.</summary>
        public Action SaveSettings { get; set; } = () => Settings.Default.Save();

        /// <summary>Opens a file with the program that Windows has for it: a report in the browser.</summary>
        public Action<string> OpenDocument { get; set; } = path =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });

        private static string DefaultTitle(MessageBoxImage icon)
        {
            switch (icon)
            {
                case MessageBoxImage.Error: return "Error";
                case MessageBoxImage.Warning: return "Warning";
                default: return "Information";
            }
        }
        #endregion

        public ICommand PingCommand { get; }
        public ICommand DicomEchoCommand { get; }
        public ICommand RunCommand { get; }
        public ICommand RunTestsCommand { get; }
        public ICommand RunPerformanceCommand { get; }
        public ICommand OpenReportCommand { get; }
        public ICommand OpenPerformanceReportCommand { get; }
        public ICommand CancelCommand { get; }

        /// <summary>The Performance tab.</summary>
        public PerformanceViewModel Performance { get; private set; }
        public ICommand NewValuesCommand { get; }
        public ICommand SaveProfileCommand { get; }
        public ICommand DeleteProfileCommand { get; }
        public ICommand ClearLogCommand { get; }

        public MainViewModel()
        {
            _sizeTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TimeSpan.FromMilliseconds(SizeMeasureDelayMs) };
            _sizeTimer.Tick += async (s, e) => await MeasureSampleImageAsync();

            Action changed = RefreshPreview;
            PatientNameField = new IdentityFieldViewModel("Patient Name", "Surname^Firstname. Auto: invented Swedish name, sometimes with å, ä or ö.", changed);
            PatientIdField = new IdentityFieldViewModel("Patient ID", "Auto: from Skatteverket's official test personnummer.", changed);
            BirthDateField = new IdentityFieldViewModel("Birth Date", "YYYYMMDD. Auto: from the personnummer.", changed);
            SexField = new IdentityFieldViewModel("Sex", "M, F or O. Auto: from the personnummer.", changed);
            AccessionField = new IdentityFieldViewModel("Accession No", "At most 16 characters.", changed);
            StudyIdField = new IdentityFieldViewModel("Study ID", "At most 16 characters.", changed);
            StudyUidField = new IdentityFieldViewModel("Study UID", "Digits and dots, at most 64 characters.", changed);
            SeriesUidField = new IdentityFieldViewModel("Series UID", "Digits and dots, at most 64 characters. Auto: one per series.", changed);
            SopUidField = new IdentityFieldViewModel("SOP Instance UID", "Digits and dots, at most 64 characters. Auto: one per image.", changed);
            StudyDateTimeField = new IdentityFieldViewModel("Study Date/Time", StudyDateTimeFormat + ". Auto: the exam ends when the run starts.", changed);
            ReferringField = new IdentityFieldViewModel("Referring Phys.", "Surname^Firstname.", changed);
            StudyDescriptionField = new IdentityFieldViewModel("Study Descr.", "Auto: from the exam type.", changed);
            SeriesDescriptionField = new IdentityFieldViewModel("Series Descr.", "Auto: from the exam type.", changed);
            InstitutionField = new IdentityFieldViewModel("Institution", "At most 64 characters.", changed);
            StationField = new IdentityFieldViewModel("Station Name", "At most 16 characters.", changed);
            foreach (var field in new[]
            {
                PatientNameField, PatientIdField, BirthDateField, SexField, AccessionField, StudyIdField, StudyUidField, SeriesUidField,
                SopUidField, StudyDateTimeField, ReferringField, StudyDescriptionField, SeriesDescriptionField, InstitutionField, StationField,
            })
            {
                IdentityFields.Add(field);
            }

            DicomEchoCommand = new RelayCommand(async _ => await RunBusyAsync(DoEchoAsync), _ => !IsBusy);
            PingCommand = new RelayCommand(async _ => await RunBusyAsync(ct => DoPingAsync(), cancellable: false), _ => !IsBusy);
            RunCommand = new RelayCommand(async _ => await RunBusyAsync(DoRunAsync), _ => !IsBusy);
            RunTestsCommand = new RelayCommand(async _ => await RunBusyAsync(DoRunTestsAsync), _ => !IsBusy);
            RunPerformanceCommand = new RelayCommand(async _ => await RunBusyAsync(DoRunPerformanceAsync), _ => !IsBusy);
            OpenReportCommand = new RelayCommand(async _ => await OpenReportAsync(TestSuite.LastResultFiles), _ => !IsBusy && HasReport(TestSuite.LastResultFiles));
            OpenPerformanceReportCommand = new RelayCommand(async _ => await OpenReportAsync(Performance.LastResultFiles), _ => !IsBusy && HasReport(Performance.LastResultFiles));
            CancelCommand = new RelayCommand(_ => CancelOperation(), _ => _cts != null && !_cts.IsCancellationRequested);
            NewValuesCommand = new RelayCommand(_ => GenerateNewValues(), _ => !IsBusy);
            SaveProfileCommand = new RelayCommand(_ => SaveProfile(), _ => !IsBusy);
            DeleteProfileCommand = new RelayCommand(_ => DeleteProfile(), _ => !IsBusy && FindProfile(ProfileName) != null);
            ClearLogCommand = new RelayCommand(_ => LogLines.Clear());

            LoadSettings();
            Performance = new PerformanceViewModel(Settings.Default.PerformanceSettings, Settings.Default.PerformanceMode);
            Performance.SetTarget(() => "the destination above");
            Performance.SettingsChanged += () =>
            {
                Settings.Default.PerformanceSettings = Performance.SaveSettings();
                Settings.Default.PerformanceMode = Performance.SelectedMode.Mode.ToString();
            };
            LoadProfiles();
            ReloadAdditionalTags();
            _loading = false;
            UpdateManualUidRules();
            GenerateNewValues();

            _logTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (s, e) => DrainLog(), _dispatcher);
            _logTimer.Start();
            _logger.Information("DCST4 started. Test personnummer: {source}", Core.TestData.TestPersonnummerRegistry.Default.SourceDescription);
        }

        #region Settings
        public const string ResetQuestion =
            "Put all settings back to their defaults?\n\nThis includes the destination fields, the exam, the Tests and Performance tabs, " +
            "File > Settings and the results folder. The saved destination profiles are kept.";

        /// <summary>
        /// Puts every setting back to its default, as on a first start, except the saved destination profiles. Asks first, and
        /// refuses while something runs.
        /// </summary>
        public void ResetSettings()
        {
            if (IsBusy)
            {
                ShowMessage("Wait until the run is over, or cancel it, before resetting the settings.", MessageBoxImage.Warning, null);
                return;
            }
            if (!Confirm(ResetQuestion)) return;

            Settings.Default.ResetToDefaults(nameof(Settings.ConnectionProfiles));

            _loading = true;
            try
            {
                LoadSettings();
                FillModalityLists();
                _selectedProfile = null;
                _profileName = string.Empty;
                Performance.ResetToDefaults();
                TestSuite.ReloadFromSettings();
                Comparison.ReloadFromSettings();
                ReloadAdditionalTags();
            }
            finally
            {
                _loading = false;
            }
            UpdateManualUidRules();
            OnPropertyChanged(string.Empty);
            RefreshPreview();
            SaveSettings();
            _logger.Information("Settings reset to their defaults. The saved destination profiles were kept");
            StatusText = "Settings reset to their defaults. The saved destination profiles were kept.";
        }

        private void LoadSettings()
        {
            var s = Settings.Default;
            _callingAET = s.CallingAET ?? string.Empty;
            _serverAET = s.ServerAET ?? string.Empty;
            _serverHost = s.ServerHost ?? string.Empty;
            _serverPort = s.ServerPort;
            _localScpAe = s.LocalScpAe;
            _localScpPort = s.LocalScpPort;
            _testUidRoot = s.TestUidRoot;
            _commitLingerSeconds = s.CommitLingerSeconds;
            _commitWaitSeconds = s.CommitWaitSeconds;
            _commitGraceSeconds = s.CommitGraceSeconds;
            _limitMaxAssociations = s.LimitMaxAssociations;
            _limitIdleWaitSeconds = s.LimitIdleWaitSeconds;
            _limitArtimWaitSeconds = s.LimitArtimWaitSeconds;
            _faultSettleSeconds = s.FaultSettleSeconds;
            _writeResultJson = s.WriteResultJson;
            _writeResultCsv = s.WriteResultCsv;
            _writeResultHtml = s.WriteResultHtml;
            _resultsKeepDays = s.ResultsKeepDays;
            _tlsExpiredClientCertThumbprint = s.TlsExpiredClientCertThumbprint ?? string.Empty;
            _useTLS = s.UseTLS;
            _tlsIgnoreCertErrors = s.TlsIgnoreCertErrors;
            _tlsClientCertThumbprint = s.TlsClientCertThumbprint ?? string.Empty;

            SelectedModality = ModalityCatalog.Find(s.Modality) ?? ModalityCatalog.All[0];
            SelectedPreset = _selectedModality.Presets.FirstOrDefault(p => p.Name == s.ExamType) ?? _selectedModality.Presets[0];

            _seriesCount = Math.Max(1, s.SeriesCount);
            // 0 means never set: use the modality's and exam type's own defaults.
            _imagesPerSeries = s.ImagesPerSeries > 0 ? s.ImagesPerSeries : _selectedPreset.DefaultImages;
            _rows = s.Rows > 0 ? s.Rows : _selectedModality.DefaultRows;
            _columns = s.Columns > 0 ? s.Columns : _selectedModality.DefaultColumns;
            _bitsStored = _selectedModality.BitDepths.Contains(s.BitsStored) ? s.BitsStored : _selectedModality.DefaultBitDepth;

            _characterSet = CharacterSets.Contains(s.CharacterSet) ? s.CharacterSet : DicomCharacterSets.Latin1;
            _selectedTransferSyntax = TransferSyntaxOption.All.FirstOrDefault(o => o.Syntax.UID.UID == s.TransferSyntax) ?? TransferSyntaxOption.ExplicitVrLe;
            _allowFallback = s.AllowFallback;
            _minAge = s.MinAge;
            _maxAge = s.MaxAge;
            _sendImages = s.SendImages;
            _saveImages = s.SaveImages;
            _saveFolder = s.SaveFolder ?? string.Empty;
        }

        private void FillModalityLists()
        {
            Presets.Clear();
            foreach (var preset in _selectedModality.Presets) Presets.Add(preset);
            BitDepths.Clear();
            foreach (var bits in _selectedModality.BitDepths) BitDepths.Add(bits);
        }
        #endregion

        #region Busy handling
        private async Task RunBusyAsync(Func<CancellationToken, Task> action, bool cancellable = true)
        {
            if (IsBusy) return;
            var cts = cancellable ? new CancellationTokenSource() : null;
            _cts = cts;
            IsBusy = true;
            try
            {
                await action(cts?.Token ?? CancellationToken.None);
            }
            finally
            {
                _cts = null;
                cts?.Dispose();
                IsBusy = false;
            }
        }

        private void CancelOperation()
        {
            var cts = _cts;
            if (cts == null || cts.IsCancellationRequested) return;
            _logger.Information("Cancel requested by user");
            StatusText = "Cancelling...";
            cts.Cancel();
            CommandManager.InvalidateRequerySuggested();
        }
        #endregion

        #region Ping and echo
        private DicomEndpoint BuildEndpoint() => new DicomEndpoint
        {
            Host = ServerHost,
            Port = ServerPort,
            CallingAe = CallingAET,
            CalledAe = ServerAET,
            UseTls = UseTLS,
            TlsIgnoreCertErrors = TlsIgnoreCertErrors,
            TlsClientCertThumbprint = TlsClientCertThumbprint,
            TlsExpiredClientCertThumbprint = TlsExpiredClientCertThumbprint?.Trim(),
        }.Trimmed();

        private static NetworkOptions BuildNetworkOptions()
        {
            var s = Settings.Default;
            return new NetworkOptions
            {
                ConnectionTimeoutSeconds = s.ConnectionTimeoutSeconds,
                AssociationTimeoutSeconds = s.AssociationTimeoutSeconds,
                ResponseTimeoutSeconds = s.ResponseTimeoutSeconds,
                LogDimseDatasets = s.Client_LogDimseDatasets,
                LogDataPdus = s.Client_LogDataPDUs,
            };
        }

        private async Task DoEchoAsync(CancellationToken token)
        {
            var endpoint = BuildEndpoint();
            var problem = endpoint.Validate();
            if (problem != null)
            {
                ShowMessage(problem, MessageBoxImage.Warning, null);
                return;
            }

            StatusText = "Performing C-ECHO...";
            var result = await EchoService.EchoAsync(endpoint, BuildNetworkOptions(), token);
            StatusText = result.Message;
            if (result.Outcome == ConnectivityOutcome.Success)
            {
                ShowMessage($"Connection to {endpoint.Host} successful.\nTime: {result.ElapsedMs} ms", MessageBoxImage.Information, null);
            }
            else if (result.Outcome == ConnectivityOutcome.Failed)
            {
                ShowMessage($"C-ECHO to {endpoint.Host}:\n{result.Message}", MessageBoxImage.Warning, null);
            }
        }

        private async Task DoPingAsync()
        {
            var endpoint = BuildEndpoint();
            var problem = endpoint.ValidateHost();
            if (problem != null)
            {
                ShowMessage(problem, MessageBoxImage.Warning, null);
                return;
            }

            StatusText = $"Pinging {endpoint.Host}...";
            var result = await PingService.PingAsync(endpoint.Host);
            StatusText = result.Message;
            ShowMessage(result.Message, result.Outcome == ConnectivityOutcome.Success ? MessageBoxImage.Information : MessageBoxImage.Warning, "Ping Result");
        }
        #endregion

        #region Identifiers
        /// <summary>Draws a new set of Auto values: a new patient, new identifiers.</summary>
        public void GenerateNewValues()
        {
            _previewSeed = Guid.NewGuid().GetHashCode();
            _previewStudyUid = null;
            RefreshPreview();
        }

        /// <summary>
        /// Recalculates what the next run will use. The seed stays the same until new values are requested, so entering
        /// one manual value does not change the others. Birth date and sex do follow a manually entered personnummer.
        /// </summary>
        private void RefreshPreview()
        {
            if (_loading || _selectedModality == null || _selectedPreset == null) return;

            IdentityOverrides overrides;
            var problems = ReadOverrides(out overrides);

            try
            {
                var generator = new IdentityGenerator(random: new Random(_previewSeed));
                _identity = generator.Generate(overrides, _selectedModality, _selectedPreset, _minAge, _maxAge);
            }
            catch (InvalidOperationException ex)
            {
                // E.g. nobody on the list within the age range.
                IdentityMessage = ex.Message;
                _identity = null;
                RefreshSizeEstimate();
                return;
            }

            if (StudyUidField.IsAuto)
            {
                _previewStudyUid = _previewStudyUid ?? _identity.StudyInstanceUid;
                _identity.StudyInstanceUid = _previewStudyUid;
            }

            PatientNameField.ShowGenerated(_identity.PatientName);
            PatientIdField.ShowGenerated(_identity.PatientId);
            BirthDateField.ShowGenerated(_identity.PatientBirthDate);
            SexField.ShowGenerated(_identity.PatientSex);
            AccessionField.ShowGenerated(_identity.AccessionNumber);
            StudyIdField.ShowGenerated(_identity.StudyId);
            StudyUidField.ShowGenerated(_identity.StudyInstanceUid);
            SeriesUidField.ShowGenerated("(generated for each series)");
            SopUidField.ShowGenerated("(generated for each image)");
            StudyDateTimeField.ShowGenerated("(now, when the run starts)");
            ReferringField.ShowGenerated(_identity.ReferringPhysicianName);
            StudyDescriptionField.ShowGenerated(_identity.StudyDescription);
            SeriesDescriptionField.ShowGenerated(_identity.SeriesDescription);
            InstitutionField.ShowGenerated(_identity.InstitutionName);
            StationField.ShowGenerated(_identity.StationName);
            for (var i = 0; i < _additionalFields.Count; i++)
            {
                var value = _identity.AdditionalTags[i].Value;
                _additionalFields[i].Field.ShowGenerated(value.Length == 0 ? "(sent empty)" : value);
            }

            problems.AddRange(AdditionalTagRules.CheckAll(_identity.AdditionalTags, CharacterSetTagValue));
            if (problems.Count == 0)
            {
                problems.AddRange(ExamValidator.Warnings(new ExamRequest { Identity = _identity }));
            }
            IdentityMessage = problems.Count == 0 ? null : string.Join(" ", problems);
            RefreshSizeEstimate();
        }

        private List<string> ReadOverrides(out IdentityOverrides overrides)
        {
            var problems = new List<string>();
            overrides = new IdentityOverrides
            {
                PatientName = PatientNameField.ManualValue?.Trim(),
                PatientId = PatientIdField.ManualValue?.Trim(),
                PatientBirthDate = BirthDateField.ManualValue?.Trim(),
                PatientSex = SexField.ManualValue?.Trim().ToUpperInvariant(),
                AccessionNumber = AccessionField.ManualValue?.Trim(),
                StudyId = StudyIdField.ManualValue?.Trim(),
                StudyInstanceUid = StudyUidField.ManualValue?.Trim(),
                SeriesInstanceUid = SeriesUidField.ManualValue?.Trim(),
                SopInstanceUid = SopUidField.ManualValue?.Trim(),
                ReferringPhysicianName = ReferringField.ManualValue?.Trim(),
                StudyDescription = StudyDescriptionField.ManualValue?.Trim(),
                SeriesDescription = SeriesDescriptionField.ManualValue?.Trim(),
                InstitutionName = InstitutionField.ManualValue?.Trim(),
                StationName = StationField.ManualValue?.Trim(),
                AdditionalTags = _additionalFields.Select(f => new AdditionalTagValue(f.Tag, f.Field.ManualValue?.Trim())).ToList(),
                AutoValuesInBasicAscii = CharacterSetTagValue == DicomCharacterSets.Empty,
            };

            var manualDate = StudyDateTimeField.ManualValue?.Trim();
            if (manualDate != null)
            {
                if (DateTime.TryParseExact(manualDate, StudyDateTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                {
                    overrides.StudyDateTime = parsed;
                }
                else
                {
                    problems.Add($"Study Date/Time must have the form {StudyDateTimeFormat}.");
                }
            }
            return problems;
        }

        /// <summary>One Series UID cannot be shared by several series, nor one SOP Instance UID by several images.</summary>
        private void UpdateManualUidRules()
        {
            if (_loading) return;
            var seriesRule = _seriesCount <= 1;
            var sopRule = (long)_seriesCount * _imagesPerSeries <= 1;
            SeriesUidField.SetManualAllowed(seriesRule, "A manual Series UID requires exactly one series.");
            SopUidField.SetManualAllowed(sopRule, "A manual SOP Instance UID requires exactly one image.");
            RefreshPreview();
        }

        /// <summary>Brings the rows on the Identifiers tab in line with the saved list. A tag that stays keeps what the user typed.</summary>
        public void ReloadAdditionalTags()
        {
            var previous = _additionalFields.ToList();
            foreach (var item in previous) IdentityFields.Remove(item.Field);
            _additionalFields.Clear();

            foreach (var setting in ReadAdditionalTagSettings())
            {
                var tag = setting.ToTag();
                var problem = tag == null ? $"'{setting.Tag}' is not a tag number." : AdditionalTagRules.CheckTag(tag);
                if (problem != null)
                {
                    _logger.Warning("Saved additional tag ignored: {problem}", problem);
                    continue;
                }

                var kept = previous.FirstOrDefault(p => p.Tag.Key == tag.Key && p.Tag.VrCode == tag.VrCode);
                var hint = $"{tag.Label} {tag.VrCode}" + (tag.PrivateCreator != null ? $", private creator {tag.PrivateCreator}" : string.Empty)
                    + (CommonTagCatalog.Find(tag) != null ? ". Auto: generated." : ". Auto: the default value from Settings.");
                var field = kept?.Field ?? new IdentityFieldViewModel(tag.Name, hint, RefreshPreview);
                _additionalFields.Add(new AdditionalField { Tag = tag, Field = field });
                IdentityFields.Add(field);
            }

            RefreshPreview();
        }

        private List<AdditionalTagSetting> ReadAdditionalTagSettings()
        {
            try
            {
                return AdditionalTagSetting.Deserialize(Settings.Default.AdditionalTags);
            }
            catch (InvalidOperationException ex)
            {
                _logger.Warning(ex, "Could not read the saved additional tags, they are ignored");
                return new List<AdditionalTagSetting>();
            }
        }
        #endregion

        #region Size estimate
        /// <summary>Updates the size in the Exam box. Series, images and the settings get here through <see cref="RefreshPreview"/>.</summary>
        private void RefreshSizeEstimate()
        {
            if (_loading) return;

            CancelSizeMeasurement();
            var request = BuildRequest();
            var uncompressed = ExamSizeEstimator.UncompressedImageBytes(request);
            if (uncompressed == null)
            {
                EstimatedSizeText = NoSizeEstimate;
                return;
            }

            var syntax = _selectedTransferSyntax.Syntax;
            if (!syntax.IsEncapsulated)
            {
                ShowEstimate(uncompressed.Value, request);
            }
            else if (SizeKey(request, syntax) == _measuredSizeKey)
            {
                ShowEstimate(_measuredImageBytes, request);
            }
            else if (request.Identity == null)
            {
                ShowUpperLimit(uncompressed.Value, request);
            }
            else
            {
                EstimatedSizeText = "calculating...";
                _sizeTimer.Start();
            }
        }

        private void CancelSizeMeasurement()
        {
            _sizeTimer.Stop();
            _sizeSequence++;
            _sizeCts?.Cancel();
        }

        private async Task MeasureSampleImageAsync()
        {
            _sizeTimer.Stop();
            var request = BuildRequest();
            var syntax = _selectedTransferSyntax.Syntax;
            var uncompressed = ExamSizeEstimator.UncompressedImageBytes(request);
            if (uncompressed == null || request.Identity == null) return;

            var sequence = ++_sizeSequence;
            var cts = new CancellationTokenSource();
            _sizeCts = cts;
            long? measured = null;
            try
            {
                measured = await Task.Run(() => ExamSizeEstimator.MeasureImageBytes(request, syntax, cts.Token), cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Whatever fo-dicom or a codec throws must not take the program down: this is only a figure on the screen.
                _logger.Warning(ex, "Could not measure a sample image for the size estimate");
            }
            finally
            {
                if (ReferenceEquals(_sizeCts, cts)) _sizeCts = null;
                cts.Dispose();
            }

            if (sequence != _sizeSequence) return;
            if (measured == null)
            {
                ShowUpperLimit(uncompressed.Value, request);
                return;
            }

            _measuredSizeKey = SizeKey(request, syntax);
            _measuredImageBytes = measured.Value;
            ShowEstimate(measured.Value, request);
        }

        private void ShowEstimate(long imageBytes, ExamRequest request) =>
            EstimatedSizeText = "about " + ExamSizeEstimator.Format(imageBytes * request.TotalImages);

        private void ShowUpperLimit(long imageBytes, ExamRequest request) =>
            EstimatedSizeText = "at most " + ExamSizeEstimator.Format(imageBytes * request.TotalImages);

        /// <summary>What the size of a compressed image depends on. The number of images is not part of it.</summary>
        private static string SizeKey(ExamRequest request, FellowOakDicom.DicomTransferSyntax syntax) => string.Join("|",
            request.Modality.Code, request.Rows, request.Columns, request.BitsStored, syntax.UID.UID,
            request.BurnedInTags == null ? string.Empty : string.Join(",", request.BurnedInTags));
        #endregion

        #region Run
        private async Task DoRunAsync(CancellationToken token)
        {
            RefreshPreview();

            var problems = ReadOverrides(out _);
            var options = BuildRunOptions();
            var request = BuildRequest();

            if (request.Identity != null && request.Identity.StudyDateTimeIsAuto) request.Identity.StudyDateTime = DateTime.Now;
            if (request.Identity == null && IdentityMessage != null) problems.Add(IdentityMessage);
            else problems.AddRange(ExamValidator.Validate(request));
            var optionsProblem = options.Validate();
            if (optionsProblem != null) problems.Add(optionsProblem);
            if (problems.Count > 0)
            {
                _logger.Warning("Run not started: {problems}", string.Join(" ", problems));
                StatusText = "Run not started: correct the values first.";
                ShowMessage(string.Join("\n", problems), MessageBoxImage.Warning, null);
                return;
            }

            foreach (var warning in ExamValidator.Warnings(request)) _logger.Warning("{warning}", warning);

            var id = request.Identity;
            _logger.Information("Exam: {modality} {preset}, {series} series x {images} images, {rows}x{columns}, {bits} bits, {charset}. Patient {name}, ID {id}, born {born}, sex {sex}. Accession {accession}, Study ID {studyId}, Study UID {studyUid}",
                request.Modality.Code, request.Preset.Name, request.SeriesCount, request.ImagesPerSeries, request.Rows, request.Columns, request.BitsStored, _characterSet,
                id.PatientName, id.PatientId, id.PatientBirthDate, id.PatientSex, id.AccessionNumber, id.StudyId, id.StudyInstanceUid);
            if (id.AdditionalTags.Count > 0)
            {
                _logger.Information("Additional tags: {tags}", string.Join(", ", id.AdditionalTags.Select(t => $"{t.Tag.Label} = '{t.Value}'")));
            }

            Results.Clear();
            ProgressValue = 0;
            ProgressMaximum = Math.Max(1, request.TotalImages);
            LastRunText = string.Empty;
            StatusText = _sendImages ? $"Sending to {options.Endpoint.CalledAe}..." : "Saving...";

            var run = new StoreRun(new ExamGenerator(request), options);
            run.ImageCompleted += result => _dispatcher.BeginInvoke(new Action(() => AddResult(result, request.TotalImages)), DispatcherPriority.Background);

            var summary = await run.ExecuteAsync(token);

            // Let the result rows queued by the background thread land before the summary replaces the status.
            await _dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            LastRunText = summary.ToString();
            StatusText = DescribeEnd(summary);

            // By default the values stay on screen, so the exam can be looked up in PACS afterwards.
            if (NewValuesAfterRun) GenerateNewValues();
        }

        private void AddResult(StoreResult result, int total)
        {
            Results.Add(result);
            ProgressValue = Results.Count;
            StatusText = $"{(_sendImages ? "Sent" : "Saved")} {Results.Count} of {total}: series {result.SeriesNumber} instance {result.InstanceNumber}, {result.State}";
        }

        private static string DescribeEnd(RunSummary summary)
        {
            var dicomDir = summary.DicomDirError != null ? " The DICOMDIR could not be written: " + summary.DicomDirError
                : summary.DicomDirPath != null ? " DICOMDIR: " + summary.DicomDirPath : string.Empty;

            if (summary.WasCancelled) return "Run cancelled. " + summary + "." + dicomDir;
            if (summary.FatalError != null) return "Run stopped: " + summary.FatalError + dicomDir;
            if (summary.FallbackWarning != null) return "Run finished, but: " + summary.FallbackWarning + dicomDir;
            return "Run finished." + dicomDir;
        }

        private ExamRequest BuildRequest() => new ExamRequest
        {
            Modality = _selectedModality,
            Preset = _selectedPreset,
            Identity = _identity,
            SeriesCount = _seriesCount,
            ImagesPerSeries = _imagesPerSeries,
            Rows = _rows,
            Columns = _columns,
            BitsStored = _bitsStored,
            CharacterSet = CharacterSetTagValue,
            BurnedInTags = Settings.Default.BurnInText ? BurnedInTagCatalog.Parse(Settings.Default.BurnInTags) : null,
        };

        private StoreRunOptions BuildRunOptions() => new StoreRunOptions
        {
            Send = _sendImages,
            Save = _saveImages,
            SaveFolder = _saveFolder?.Trim(),
            TransferSyntax = _selectedTransferSyntax.Syntax,
            AllowFallback = _allowFallback,
            Endpoint = BuildEndpoint(),
            Network = BuildNetworkOptions(),
        };
        #endregion

        #region Tests
        /// <summary>The current destination fields as a profile.</summary>
        public TargetProfile BuildTargetProfile() => new TargetProfile
        {
            Name = ProfileName?.Trim(),
            Endpoint = BuildEndpoint(),
            Network = BuildNetworkOptions(),
            LocalScpAe = _localScpAe,
            LocalScpPort = _localScpPort,
            UidRoot = _testUidRoot,
        };

        /// <summary>What the Tests tab decides about a run, beyond which tests are in it.</summary>
        public TestRunOptions BuildTestRunOptions() => new TestRunOptions
        {
            Matrix = TestSuite.Capabilities.Matrix,
            SyntaxOverride = TestSuite.SelectedStoreSyntax.Syntax,
            CommitLingerSeconds = _commitLingerSeconds,
            CommitWaitSeconds = _commitWaitSeconds,
            CommitGraceSeconds = _commitGraceSeconds,
            MaxAssociations = _limitMaxAssociations,
            IdleWaitSeconds = _limitIdleWaitSeconds,
            ArtimWaitSeconds = _limitArtimWaitSeconds,
            FaultSettleSeconds = _faultSettleSeconds,
        };

        /// <summary>Saves the current destination fields for use with DCST4.Cli.exe.</summary>
        public void SaveTargetProfile(string path)
        {
            var profile = BuildTargetProfile();
            var problem = profile.Validate();
            if (problem != null)
            {
                ShowMessage(problem, MessageBoxImage.Warning, null);
                return;
            }

            try
            {
                profile.Save(path);
                _logger.Information("Saved target profile to {path}", path);
                StatusText = "Profile saved to " + path;
            }
            catch (TargetProfileException ex)
            {
                _logger.Warning(ex, "Could not save the target profile");
                ShowMessage(ex.Message, MessageBoxImage.Warning, null);
            }
        }

        private async Task DoRunTestsAsync(CancellationToken token)
        {
            var profile = BuildTargetProfile();
            var options = BuildTestRunOptions();
            var problem = profile.Validate() ?? options.Validate();
            if (problem == null && TestSuite.SelectedTests.Count == 0) problem = "Select at least one test.";
            if (problem == null && string.IsNullOrWhiteSpace(TestSuite.ResultsFolder)) problem = "Enter a folder for the results.";
            if (problem != null)
            {
                ShowMessage(problem, MessageBoxImage.Warning, null);
                return;
            }

            var tests = TestSuite.SelectedTests;
            var folder = TestSuite.ResultsFolder.Trim();

            // What a PACS has stored may not be possible to delete, so a run that stores anything is confirmed with the destination named.
            var storing = tests.Where(t => t.StoresObjects).ToList();
            if (storing.Count > 0 && !Confirm(DescribeStoring(storing, profile)))
            {
                StatusText = "Tests not started.";
                return;
            }

            TestSuite.Rows.Clear();
            TestSuite.SelectedRow = null;
            TestSuite.LastResultFiles = null;
            ProgressValue = 0;
            ProgressMaximum = Math.Max(1, tests.Count);
            LastRunText = string.Empty;
            StatusText = $"Running {tests.Count} test(s) against {profile.Endpoint.CalledAe}...";

            var runner = new TestRunner();
            runner.Progress += progress => _dispatcher.BeginInvoke(new Action(() => ShowTestProgress(progress)), DispatcherPriority.Background);
            DCST4.Core.TestData.TestPatientNames.TryParse(Settings.Default.TestPatientNaming, out var patientNaming);

            // Counts the seconds in the status bar, so that a test that waits can be told from one that has stopped.
            var ticker = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (s, e) => { if (!token.IsCancellationRequested) ShowTestStatus(); }, _dispatcher);
            TestRunReport report;
            try
            {
                report = await runner.RunAsync(profile, tests, token, patientNaming, options);

                // Let the rows queued by the background thread land before the summary replaces the status.
                await _dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            finally
            {
                ticker.Stop();
                _testHeading = null;
                _testActivity = null;
            }

            var matrix = report.Results.Select(r => r.CapabilityMatrix).FirstOrDefault(m => m != null);
            if (matrix != null) TestSuite.Capabilities.Show(matrix);
            TestSuite.Chain.Show(report);

            LastRunText = string.Join(", ", report.Summary.Where(p => p.Value > 0).Select(p => $"{p.Key} {p.Value}"));
            var ending = report.Cancelled ? "Tests cancelled." : "Tests finished.";
            try
            {
                string saved = null;
                TestSuite.LastResultFiles = await Task.Run(() => WriteRunFiles(report, folder, null, out saved));
                StatusText = $"{ending} {saved}";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                _logger.Error(ex, "Could not write the test results to {folder}", folder);
                StatusText = $"{ending} The results could not be saved: {ex.Message}";
            }
        }

        /// <summary>The question asked before a run with tests that store objects.</summary>
        /// <remarks>Each test with what it stores, and the total. Sizes are uncompressed, the most a test sends.</remarks>
        private static string DescribeStoring(IReadOnlyList<ITestCase> storing, TargetProfile profile)
        {
            var target = $"{profile.Endpoint.CalledAe} at {profile.Endpoint.Host}:{profile.Endpoint.Port}";
            var count = storing.Count == 1 ? "1 of the ticked tests stores" : $"{storing.Count} of the ticked tests store";
            var lines = storing.Select(t => t.Storage == null
                ? $"- {t.Name}"
                : $"- {t.Name}: {t.Storage.Describe()}" + (t.Storage.Note == null ? string.Empty : ". " + t.Storage.Note));
            var total = StorageEstimate.Sum(storing.Select(t => t.Storage));
            return $"{count} new test objects on {target}:\n\n"
                + string.Join("\n", lines)
                + $"\n\nIn all: {total.Describe()}. Sizes are uncompressed. A compressed transfer syntax sends less."
                + "\n\nStart the tests?";
        }

        /// <summary>
        /// Runs the load, ramp or soak test of the Performance tab. It stores many new objects, so the plan is shown with the
        /// destination and has to be confirmed first. The chart is fed once a second from what the test reports.
        /// </summary>
        private async Task DoRunPerformanceAsync(CancellationToken token)
        {
            var profile = BuildTargetProfile();
            var options = BuildTestRunOptions();
            var mode = Performance.SelectedMode.Mode;
            var problem = profile.Validate() ?? options.Validate() ?? Performance.Problem;
            if (problem == null && string.IsNullOrWhiteSpace(TestSuite.ResultsFolder)) problem = "Enter a folder for the results on the Tests tab.";
            if (problem != null)
            {
                ShowMessage(problem, MessageBoxImage.Warning, null);
                return;
            }

            var settings = Performance.Settings;
            var target = $"{profile.Endpoint.CalledAe} at {profile.Endpoint.Host}:{profile.Endpoint.Port}";
            if (!Confirm(settings.DescribePlan(mode, target) + "\n\nStart the test?"))
            {
                StatusText = "Performance test not started.";
                return;
            }

            var folder = TestSuite.ResultsFolder.Trim();
            options.Load = settings;
            options.MetricsFolder = folder;
            Performance.Clear();
            Performance.StartRun(settings, mode);
            ProgressValue = 0;
            // In thousandths, filled once a second as the chart is: time against the planned duration, or images against the count.
            ProgressMaximum = PerformanceProgressSteps;
            LastRunText = string.Empty;
            StatusText = $"Running {Performance.SelectedTest.Name} against {profile.Endpoint.CalledAe}...";

            var runner = new TestRunner();
            runner.Progress += progress =>
            {
                // A second of metrics goes to the chart's queue, which a timer drains once a second on the UI thread.
                if (progress.Stage == TestProgressStage.Metrics)
                {
                    Performance.Enqueue(progress.Metrics);
                    return;
                }
                _dispatcher.BeginInvoke(new Action(() => ShowPerformanceProgress(progress)), DispatcherPriority.Background);
            };
            DCST4.Core.TestData.TestPatientNames.TryParse(Settings.Default.TestPatientNaming, out var patientNaming);

            var ticker = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (s, e) => { if (!token.IsCancellationRequested) ShowTestStatus(); }, _dispatcher);
            var chart = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (s, e) =>
            {
                if (Performance.Drain()) ProgressValue = (int)(Performance.Progress * PerformanceProgressSteps);
            }, _dispatcher);
            TestRunReport report;
            try
            {
                report = await runner.RunAsync(profile, new[] { Performance.SelectedTest }, token, patientNaming, options);
                await _dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            finally
            {
                ticker.Stop();
                chart.Stop();
                _testHeading = null;
                _testActivity = null;
            }

            var result = report.Results.Single();
            Performance.Show(result);
            LastRunText = result.Result.ToString();
            var ending = report.Cancelled ? "Performance test cancelled." : "Performance test finished.";
            try
            {
                string saved = null;
                var series = new[] { result.Performance?.SeriesFile, result.Performance?.WindowsFile };
                var files = await Task.Run(() => WriteRunFiles(report, folder, series, out saved));
                Performance.LastResultFiles = files;
                Performance.Files = string.Join("   ", new[] { files.Json ?? files.Html ?? files.Csv }.Concat(series).Where(f => f != null));
                StatusText = $"{ending} {saved}";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                _logger.Error(ex, "Could not write the test results to {folder}", folder);
                StatusText = $"{ending} The results could not be saved: {ex.Message}";
            }
        }

        /// <summary>The result files with the HTML report beside them. A report that cannot be written is logged: the results are what matter.</summary>
        /// <remarks>
        /// Writes the files that Test settings asks for, and then deletes result files older than the days set there. That only
        /// happens here, after a run has been saved, never at start or when the setting changes, and never to the files of this run.
        /// <paramref name="saved"/> is what the status bar says about it.
        /// </remarks>
        private ResultFilePaths WriteRunFiles(TestRunReport report, string folder, IEnumerable<string> alsoKeep, out string saved)
        {
            var options = new ResultFileOptions { Json = WriteResultJson, Csv = WriteResultCsv, Html = WriteResultHtml };
            var files = ReportFiles.WriteRun(report, folder, out var reportProblem, options);
            if (reportProblem != null) _logger.Warning("Could not write the HTML report of {run}: {problem}", files.Stem, reportProblem);

            saved = options.Any ? $"Results saved in {folder}" : NoResultFilesText;
            if (ResultsKeepDays <= 0) return files;

            var keep = new[] { files.Json, files.Csv, files.Html, files.CapabilityJson, files.CapabilityCsv, files.ChainCsv }.Concat(alsoKeep ?? Enumerable.Empty<string>());
            var cleaned = ResultCleaner.Clean(folder, ResultsKeepDays, DateTime.UtcNow, keep);
            if (cleaned.Deleted + cleaned.Skipped > 0)
            {
                _logger.Information("Deleted {deleted} result files older than {days} days in {folder}, {skipped} could not be deleted", cleaned.Deleted, ResultsKeepDays, folder, cleaned.Skipped);
            }
            if (cleaned.Deleted > 0) saved += cleaned.Deleted == 1 ? ", 1 older result file deleted" : $", {cleaned.Deleted} older result files deleted";
            return files;
        }

        public const string NoResultFilesText = "No result files are written, see Test settings.";

        #region Reports and comparison
        /// <summary>What the Compare runs dialog shows. Kept, so that the list is still there the next time.</summary>
        public CompareRunsViewModel Comparison { get; } = new CompareRunsViewModel();

        /// <summary>Opens the report of a finished run in the browser. The report is made again from the JSON file if it is not there.</summary>
        public Task OpenReportAsync(ResultFilePaths files)
        {
            if (!HasReport(files)) return Task.CompletedTask;
            // With the JSON file the report can be made again if it is gone. Without it, the report that was written is all there is.
            if (files.Json != null) return OpenSavedReportAsync(files.Json);
            return RunBusyAsync(token =>
            {
                try
                {
                    OpenDocument(files.Html);
                    StatusText = $"Report: {files.Html}";
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is System.ComponentModel.Win32Exception)
                {
                    _logger.Error(ex, "Could not open the report {html}", files.Html);
                    ShowMessage("The report could not be opened: " + ex.Message, MessageBoxImage.Warning, null);
                }
                return Task.CompletedTask;
            });
        }

        /// <summary>A run has a report to open when it wrote one, or wrote the JSON file that one can be made from.</summary>
        private static bool HasReport(ResultFilePaths files) => files != null && (files.Html != null || files.Json != null);

        /// <summary>Opens the report of a saved run, from its JSON file.</summary>
        public Task OpenSavedReportAsync(string jsonPath) => RunBusyAsync(async token =>
        {
            try
            {
                var html = ReportFiles.ReportPathFor(jsonPath);
                if (!File.Exists(html))
                {
                    StatusText = "Writing the report...";
                    html = await Task.Run(() => ReportFiles.WriteReportFor(RunFile.Load(jsonPath), jsonPath), token);
                }
                OpenDocument(html);
                StatusText = $"Report: {html}";
            }
            catch (RunFileException ex)
            {
                ShowMessage(ex.Message, MessageBoxImage.Warning, null);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is System.ComponentModel.Win32Exception)
            {
                _logger.Error(ex, "Could not open the report of {json}", jsonPath);
                ShowMessage("The report could not be opened: " + ex.Message, MessageBoxImage.Warning, null);
            }
        });

        /// <summary>
        /// Compares saved runs, the first being the baseline, writes the comparison to the results folder and opens it. The
        /// files are read and compared off the UI thread, and Cancel stops between two files.
        /// </summary>
        public Task CompareRunsAsync(IReadOnlyList<string> jsonPaths, double thresholdPercent) => RunBusyAsync(async token =>
        {
            var options = new ComparisonOptions { ThresholdPercent = thresholdPercent };
            var problem = jsonPaths == null || jsonPaths.Count < 2 ? "Choose at least two runs to compare. The first is the baseline." : options.Validate();
            if (problem == null && string.IsNullOrWhiteSpace(TestSuite.ResultsFolder)) problem = "Enter a folder for the results in Test settings.";
            if (problem != null)
            {
                ShowMessage(problem, MessageBoxImage.Warning, null);
                return;
            }

            var folder = TestSuite.ResultsFolder.Trim();
            StatusText = $"Comparing {jsonPaths.Count} runs...";
            try
            {
                var written = await Task.Run(() =>
                {
                    var comparison = RunComparer.Compare(ReportFiles.LoadRuns(jsonPaths, token), options);
                    token.ThrowIfCancellationRequested();
                    return Tuple.Create(comparison, ReportFiles.WriteComparison(comparison, ReportFiles.NewComparisonPath(folder)));
                }, token);

                Settings.Default.CompareThresholdPercent = thresholdPercent;
                LastRunText = written.Item1.Regressions == 0 ? "No regressions" : written.Item1.Regressions == 1 ? "1 regression" : $"{written.Item1.Regressions} regressions";
                StatusText = $"{written.Item1.Summary} Comparison saved as {written.Item2}";
                OpenDocument(written.Item2);
            }
            catch (OperationCanceledException)
            {
                StatusText = "Comparison cancelled.";
            }
            catch (RunFileException ex)
            {
                StatusText = "Comparison not made.";
                ShowMessage(ex.Message, MessageBoxImage.Warning, null);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is System.ComponentModel.Win32Exception)
            {
                _logger.Error(ex, "Could not write or open the comparison in {folder}", folder);
                StatusText = "Comparison not made.";
                ShowMessage("The comparison could not be written or opened: " + ex.Message, MessageBoxImage.Warning, null);
            }
        });
        #endregion

        /// <summary>The progress bar of a Performance run counts in thousandths.</summary>
        public const int PerformanceProgressSteps = 1000;

        private void ShowPerformanceProgress(TestProgress progress)
        {
            if (progress.Stage == TestProgressStage.Completed)
            {
                // A cancelled run stays where it stopped. A finished one is full, also when the ceiling ended it early.
                if (progress.Result.Result != TestResultClass.INDETERMINATE) ProgressValue = ProgressMaximum;
                return;
            }
            _testHeading = progress.Test.Name;
            _testActivity = progress.Activity;
            _testActivityClock.Restart();
            ShowTestStatus();
        }

        /// <summary>Writes the matrix on the Capabilities tab as JSON or as CSV.</summary>
        public void SaveCapabilityMatrix(string path, bool asCsv)
        {
            var matrix = TestSuite.Capabilities.Matrix;
            if (matrix == null) return;
            try
            {
                if (asCsv) matrix.SaveCsv(path);
                else matrix.SaveJson(path);
                StatusText = $"Capability matrix saved to {path}";
            }
            catch (CapabilityFileException ex)
            {
                _logger.Error(ex, "Could not save the capability matrix to {path}", path);
                ShowMessage(ex.Message, MessageBoxImage.Warning, null);
            }
        }

        /// <summary>Loads what a vendor's Conformance Statement claims, and compares the matrix with it.</summary>
        public void LoadClaimedCapabilities(string path)
        {
            try
            {
                TestSuite.Capabilities.SetClaimed(CapabilityMatrix.Load(path));
                StatusText = $"Claims loaded from {path}";
            }
            catch (CapabilityFileException ex)
            {
                _logger.Warning(ex, "Could not load the claims from {path}", path);
                ShowMessage(ex.Message, MessageBoxImage.Warning, null);
            }
        }

        private string _testHeading;
        private string _testActivity;
        private readonly System.Diagnostics.Stopwatch _testActivityClock = new System.Diagnostics.Stopwatch();

        /// <summary>The running test, what it says it is doing, and for how many seconds it has been doing that.</summary>
        public static string FormatTestStatus(string heading, string activity, long seconds) =>
            string.IsNullOrEmpty(activity) ? $"{heading} ({seconds} s)" : $"{heading}. {activity} ({seconds} s)";

        private void ShowTestStatus()
        {
            if (_testHeading != null) StatusText = FormatTestStatus(_testHeading, _testActivity, _testActivityClock.ElapsedMilliseconds / 1000);
        }

        private void ShowTestProgress(TestProgress progress)
        {
            if (progress.Stage == TestProgressStage.Started || progress.Stage == TestProgressStage.Activity)
            {
                // The seconds count from when the test said what it is doing now, since that is what "up to 60 s" is measured against.
                _testHeading = $"Test {progress.Index + 1} of {progress.Total}: {progress.Test.Name}";
                _testActivity = progress.Activity;
                _testActivityClock.Restart();
                ShowTestStatus();
                return;
            }

            TestSuite.Rows.Add(new TestResultRow(progress.Result));
            ProgressValue = progress.Index + 1;
        }
        #endregion

        #region Log
        private void DrainLog()
        {
            var lines = UiLogSink.Instance.Drain();
            if (lines.Count == 0) return;
            foreach (var line in lines) LogLines.Add(line);
            while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
        }
        #endregion

        #region Profiles
        private void LoadProfiles()
        {
            Profiles.Clear();
            try
            {
                foreach (var p in ConnectionProfile.Deserialize(Settings.Default.ConnectionProfiles)
                                                   .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                                                   .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    Profiles.Add(p);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Could not read saved connection profiles, they are ignored");
            }
        }

        /// <summary>Saved to disk at once, so a crash cannot lose a profile.</summary>
        private void StoreProfiles()
        {
            Settings.Default.ConnectionProfiles = ConnectionProfile.Serialize(Profiles);
            SaveSettings();
        }

        private ConnectionProfile FindProfile(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return Profiles.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.CurrentCultureIgnoreCase));
        }

        private void ApplyProfile(ConnectionProfile p)
        {
            CallingAET = p.CallingAET ?? string.Empty;
            ServerAET = p.ServerAET ?? string.Empty;
            ServerHost = p.ServerHost ?? string.Empty;
            ServerPort = p.ServerPort;
            UseTLS = p.UseTLS;
            TlsIgnoreCertErrors = p.TlsIgnoreCertErrors;
            TlsClientCertThumbprint = p.TlsClientCertThumbprint ?? string.Empty;
            _logger.Information("Loaded destination profile {name}", p.Name);
            StatusText = $"Profile '{p.Name}' loaded.";
        }

        private void SaveProfile()
        {
            var name = ProfileName?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                ShowMessage("Type a name for the profile in the Profile box first.", MessageBoxImage.Warning, null);
                return;
            }

            var profile = FindProfile(name);
            if (profile == null)
            {
                profile = new ConnectionProfile { Name = name };
                // Keep the list sorted by name.
                var index = 0;
                while (index < Profiles.Count && StringComparer.CurrentCultureIgnoreCase.Compare(Profiles[index].Name, name) < 0) index++;
                Profiles.Insert(index, profile);
            }

            profile.CallingAET = CallingAET;
            profile.ServerAET = ServerAET;
            profile.ServerHost = ServerHost;
            profile.ServerPort = ServerPort;
            profile.UseTLS = UseTLS;
            profile.TlsIgnoreCertErrors = TlsIgnoreCertErrors;
            profile.TlsClientCertThumbprint = TlsClientCertThumbprint;
            StoreProfiles();

            _selectedProfile = profile; // values are already current, so do not re-apply
            OnPropertyChanged(nameof(SelectedProfile));
            ProfileName = profile.Name;
            _logger.Information("Saved destination profile {name}", profile.Name);
            StatusText = $"Profile '{profile.Name}' saved.";
        }

        private void DeleteProfile()
        {
            var profile = FindProfile(ProfileName);
            if (profile == null) return;
            if (!Confirm($"Delete the destination profile '{profile.Name}'?")) return;

            Profiles.Remove(profile);
            StoreProfiles();
            _selectedProfile = null;
            OnPropertyChanged(nameof(SelectedProfile));
            ProfileName = string.Empty;
            _logger.Information("Deleted destination profile {name}", profile.Name);
            StatusText = $"Profile '{profile.Name}' deleted.";
        }
        #endregion
    }
}
