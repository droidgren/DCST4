using System.Configuration;

namespace DCST4.Properties
{
    [SettingsProvider(typeof(CustomFileSettingsProvider))]
    internal sealed class Settings : ApplicationSettingsBase
    {
        public static Settings Default { get; } = (Settings)Synchronized(new Settings());

        // ------------------------------------------------------------ Connection
        [UserScopedSetting, DefaultSettingValue("DCST4")]
        public string CallingAET { get => (string)this[nameof(CallingAET)]; set => this[nameof(CallingAET)] = value; }

        [UserScopedSetting, DefaultSettingValue("ServerAET")]
        public string ServerAET { get => (string)this[nameof(ServerAET)]; set => this[nameof(ServerAET)] = value; }

        [UserScopedSetting, DefaultSettingValue("ServerHostname")]
        public string ServerHost { get => (string)this[nameof(ServerHost)]; set => this[nameof(ServerHost)] = value; }

        [UserScopedSetting, DefaultSettingValue("104")]
        public int ServerPort { get => (int)this[nameof(ServerPort)]; set => this[nameof(ServerPort)] = value; }

        [UserScopedSetting, DefaultSettingValue("False")]
        public bool UseTLS { get => (bool)this[nameof(UseTLS)]; set => this[nameof(UseTLS)] = value; }

        [UserScopedSetting, DefaultSettingValue("False")]
        public bool TlsIgnoreCertErrors { get => (bool)this[nameof(TlsIgnoreCertErrors)]; set => this[nameof(TlsIgnoreCertErrors)] = value; }

        /// <summary>Thumbprint of the client certificate in CurrentUser\My. Empty = no client certificate.</summary>
        [UserScopedSetting, DefaultSettingValue("")]
        public string TlsClientCertThumbprint { get => (string)this[nameof(TlsClientCertThumbprint)]; set => this[nameof(TlsClientCertThumbprint)] = value; }

        /// <summary>Saved destination profiles, serialized as XML.</summary>
        [UserScopedSetting, DefaultSettingValue("")]
        public string ConnectionProfiles { get => (string)this[nameof(ConnectionProfiles)]; set => this[nameof(ConnectionProfiles)] = value; }

        // ------------------------------------------------------------ Network and logging
        [UserScopedSetting, DefaultSettingValue("10")]
        public int ConnectionTimeoutSeconds { get => (int)this[nameof(ConnectionTimeoutSeconds)]; set => this[nameof(ConnectionTimeoutSeconds)] = value; }

        [UserScopedSetting, DefaultSettingValue("10")]
        public int AssociationTimeoutSeconds { get => (int)this[nameof(AssociationTimeoutSeconds)]; set => this[nameof(AssociationTimeoutSeconds)] = value; }

        /// <summary>Time to wait for each response, in seconds. 0 = no limit.</summary>
        [UserScopedSetting, DefaultSettingValue("60")]
        public int ResponseTimeoutSeconds { get => (int)this[nameof(ResponseTimeoutSeconds)]; set => this[nameof(ResponseTimeoutSeconds)] = value; }

        [UserScopedSetting, DefaultSettingValue("False")]
        public bool Client_LogDimseDatasets { get => (bool)this[nameof(Client_LogDimseDatasets)]; set => this[nameof(Client_LogDimseDatasets)] = value; }

        [UserScopedSetting, DefaultSettingValue("False")]
        public bool Client_LogDataPDUs { get => (bool)this[nameof(Client_LogDataPDUs)]; set => this[nameof(Client_LogDataPDUs)] = value; }

        // ------------------------------------------------------------ Exam
        [UserScopedSetting, DefaultSettingValue("CT")]
        public string Modality { get => (string)this[nameof(Modality)]; set => this[nameof(Modality)] = value; }

        [UserScopedSetting, DefaultSettingValue("")]
        public string ExamType { get => (string)this[nameof(ExamType)]; set => this[nameof(ExamType)] = value; }

        [UserScopedSetting, DefaultSettingValue("1")]
        public int SeriesCount { get => (int)this[nameof(SeriesCount)]; set => this[nameof(SeriesCount)] = value; }

        [UserScopedSetting, DefaultSettingValue("0")]
        public int ImagesPerSeries { get => (int)this[nameof(ImagesPerSeries)]; set => this[nameof(ImagesPerSeries)] = value; }

        [UserScopedSetting, DefaultSettingValue("0")]
        public int Rows { get => (int)this[nameof(Rows)]; set => this[nameof(Rows)] = value; }

        [UserScopedSetting, DefaultSettingValue("0")]
        public int Columns { get => (int)this[nameof(Columns)]; set => this[nameof(Columns)] = value; }

        [UserScopedSetting, DefaultSettingValue("0")]
        public int BitsStored { get => (int)this[nameof(BitsStored)]; set => this[nameof(BitsStored)] = value; }

        [UserScopedSetting, DefaultSettingValue("ISO_IR 100")]
        public string CharacterSet { get => (string)this[nameof(CharacterSet)]; set => this[nameof(CharacterSet)] = value; }

        /// <summary>Transfer Syntax UID.</summary>
        [UserScopedSetting, DefaultSettingValue("1.2.840.10008.1.2.1")]
        public string TransferSyntax { get => (string)this[nameof(TransferSyntax)]; set => this[nameof(TransferSyntax)] = value; }

        [UserScopedSetting, DefaultSettingValue("False")]
        public bool AllowFallback { get => (bool)this[nameof(AllowFallback)]; set => this[nameof(AllowFallback)] = value; }

        [UserScopedSetting, DefaultSettingValue("0")]
        public int MinAge { get => (int)this[nameof(MinAge)]; set => this[nameof(MinAge)] = value; }

        [UserScopedSetting, DefaultSettingValue("100")]
        public int MaxAge { get => (int)this[nameof(MaxAge)]; set => this[nameof(MaxAge)] = value; }

        /// <summary>Draw new Auto values after each run. Off: they stay until New values is clicked or the program restarts.</summary>
        [UserScopedSetting, DefaultSettingValue("False")]
        public bool NewValuesAfterRun { get => (bool)this[nameof(NewValuesAfterRun)]; set => this[nameof(NewValuesAfterRun)] = value; }

        /// <summary>How test-suite datasets name the patient: a TestPatientNaming name. Does not affect Generate &amp; Send.</summary>
        [UserScopedSetting, DefaultSettingValue("RegistrySwedishName")]
        public string TestPatientNaming { get => (string)this[nameof(TestPatientNaming)]; set => this[nameof(TestPatientNaming)] = value; }

        /// <summary>Tags added on top of the fixed identifier fields, serialized as XML.</summary>
        [UserScopedSetting, DefaultSettingValue("")]
        public string AdditionalTags { get => (string)this[nameof(AdditionalTags)]; set => this[nameof(AdditionalTags)] = value; }

        /// <summary>Draw the black text box with the tags below in the upper left corner of each image.</summary>
        [UserScopedSetting, DefaultSettingValue("False")]
        public bool BurnInText { get => (bool)this[nameof(BurnInText)]; set => this[nameof(BurnInText)] = value; }

        /// <summary>The tags in the text box, as keywords. See BurnedInTagCatalog.</summary>
        [UserScopedSetting, DefaultSettingValue(Core.Generation.BurnedInTagCatalog.DefaultKeywords)]
        public string BurnInTags { get => (string)this[nameof(BurnInTags)]; set => this[nameof(BurnInTags)] = value; }

        /// <summary>Check GitHub at startup and open a window if there is a new version. Off: nothing is sent at startup.</summary>
        [UserScopedSetting, DefaultSettingValue("True")]
        public bool ShowUpdateWindowAtStartup { get => (bool)this[nameof(ShowUpdateWindowAtStartup)]; set => this[nameof(ShowUpdateWindowAtStartup)] = value; }

        // ------------------------------------------------------------ Tests
        /// <summary>The AE title a PACS must know this computer by, for C-MOVE.</summary>
        [UserScopedSetting, DefaultSettingValue("DCST4")]
        public string LocalScpAe { get => (string)this[nameof(LocalScpAe)]; set => this[nameof(LocalScpAe)] = value; }

        [UserScopedSetting, DefaultSettingValue("11113")]
        public int LocalScpPort { get => (int)this[nameof(LocalScpPort)]; set => this[nameof(LocalScpPort)] = value; }

        /// <summary>UIDs in test datasets start with this root.</summary>
        [UserScopedSetting, DefaultSettingValue("2.25")]
        public string TestUidRoot { get => (string)this[nameof(TestUidRoot)]; set => this[nameof(TestUidRoot)] = value; }
        // ------------------------------------------------------------ Output
        [UserScopedSetting, DefaultSettingValue("True")]
        public bool SendImages { get => (bool)this[nameof(SendImages)]; set => this[nameof(SendImages)] = value; }

        [UserScopedSetting, DefaultSettingValue("False")]
        public bool SaveImages { get => (bool)this[nameof(SaveImages)]; set => this[nameof(SaveImages)] = value; }

        [UserScopedSetting, DefaultSettingValue("")]
        public string SaveFolder { get => (string)this[nameof(SaveFolder)]; set => this[nameof(SaveFolder)] = value; }

        /// <summary>Where test runs write their JSON and CSV. Empty = the default folder under %LocalAppData%\DCST4.</summary>
        [UserScopedSetting, DefaultSettingValue("")]
        public string TestResultsFolder { get => (string)this[nameof(TestResultsFolder)]; set => this[nameof(TestResultsFolder)] = value; }
    }
}
