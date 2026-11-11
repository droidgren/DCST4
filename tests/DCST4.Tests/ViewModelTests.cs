using DCST4.Core.Capability;
using DCST4.Core.Generation;
using DCST4.Core.Network;
using DCST4.Core.Pipeline;
using DCST4.Core.TestData;
using DCST4.Core.Testing;
using DCST4.Models;
using DCST4.ViewModels;
using FellowOakDicom;
using FellowOakDicom.Network;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DCST4.Tests
{
    /// <summary>Drives the real MainViewModel against the mock SCP. Settings stay in memory: SaveSettings is replaced.</summary>
    internal static class ViewModelTests
    {
        private sealed class Harness
        {
            public readonly MainViewModel Vm;
            public readonly List<string> Messages = new List<string>();
            public int Saves;
            public bool ConfirmAnswer = true;

            public Harness(int port)
            {
                Vm = new MainViewModel
                {
                    ShowMessage = (message, icon, title) => Messages.Add(message),
                    SaveSettings = () => Saves++,
                };
                Vm.Confirm = _ => ConfirmAnswer;
                Vm.ServerHost = "127.0.0.1";
                Vm.ServerPort = port;
                Vm.ServerAET = "MOCKSCP";
                Vm.CallingAET = "DCST4";
                Vm.SendImages = true;
                Vm.SaveImages = false;
                Vm.AllowFallback = false;
                Vm.SelectedTransferSyntax = TransferSyntaxOption.ExplicitVrLe;
                Vm.SelectedModality = Core.Generation.Modalities.ModalityCatalog.Find("MR");
                Vm.SelectedModality = Core.Generation.Modalities.ModalityCatalog.Find("CT");
                Vm.SeriesCount = 1;
                Vm.ImagesPerSeries = 1;
                Vm.Rows = 64;
                Vm.Columns = 64;
                Vm.MinAge = 0;
                Vm.MaxAge = 100;
                Vm.NewValuesAfterRun = false;
                Vm.CharacterSet = DicomCharacterSets.Latin1;
                Vm.BurnInText = false;
                Vm.BurnInTags = BurnedInTagCatalog.DefaultKeywords;
                Vm.AdditionalTagSettings = new AdditionalTagSetting[0];
            }

            /// <summary>Executes a command and waits until the view model is idle again.</summary>
            public async Task Execute(System.Windows.Input.ICommand command, Action duringRun = null)
            {
                command.Execute(null);
                var sw = Stopwatch.StartNew();
                while (Vm.IsBusy && sw.Elapsed < TimeSpan.FromSeconds(60))
                {
                    duringRun?.Invoke();
                    await Task.Delay(10);
                }
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
        }

        public static void Run(int port, int closedPort, string temp, Action<string, bool, string> check)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await RunAll(port, check);
                        await RunSizeEstimate(port, check);
                        RunHelpWindow(check);
                        await RunTestSuite(port, closedPort, temp, check);
                        await RunPerformanceTab(port, temp, check);
                        await RunResetSettings(port, check);
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { dispatcher.InvokeShutdown(); }
                });
                Dispatcher.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null) throw new InvalidOperationException("View model tests failed: " + failure, failure);
        }

        /// <summary>
        /// Reset to defaults, in memory only: SaveSettings is replaced, and every setting is put back afterwards so that no later
        /// section runs with the default results folder under %LocalAppData%.
        /// </summary>
        private static async Task RunResetSettings(int port, Action<string, bool, string> check)
        {
            Console.WriteLine("\nReset to default settings");
            var settings = (System.Configuration.ApplicationSettingsBase)typeof(MainViewModel).Assembly.GetType("DCST4.Properties.Settings")
                .GetProperty("Default", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).GetValue(null);
            var snapshot = settings.Properties.Cast<System.Configuration.SettingsProperty>().ToDictionary(p => p.Name, p => settings[p.Name]);
            try
            {
                var h = new Harness(port);
                var vm = h.Vm;
                var questions = new List<string>();
                vm.Confirm = question => { questions.Add(question); return h.ConfirmAnswer; };

                settings["ConnectionProfiles"] = "<kept on purpose/>";
                vm.TestUidRoot = "1.2.3";
                vm.CommitWaitSeconds = 7;
                vm.LimitMaxAssociations = 3;
                vm.Performance.SelectedMode = vm.Performance.Modes[2];
                vm.Performance.Associations = 13;
                vm.TestSuite.ResultsFolder = @"C:\Temp\DCST4 reset";
                vm.TestSuite.SelectedStoreSyntax = vm.TestSuite.StoreSyntaxChoices[1];
                vm.TestSuite.Choices[0].IsSelected = true;
                vm.BurnInText = true;
                settings["TestPatientNaming"] = "RegistrySwedishName";
                vm.ProfileName = "Something";

                h.ConfirmAnswer = false;
                var savesBefore = h.Saves;
                vm.ResetSettings();
                check("Declined: nothing changes", questions.Count == 1 && questions[0] == MainViewModel.ResetQuestion && vm.TestUidRoot == "1.2.3" && vm.TestSuite.Choices[0].IsSelected && h.Saves == savesBefore, vm.TestUidRoot);
                check("The question says what is kept", MainViewModel.ResetQuestion.Contains("saved destination profiles are kept"), null);

                h.ConfirmAnswer = true;
                var raised = new List<string>();
                vm.PropertyChanged += (s, e) => raised.Add(e.PropertyName);
                vm.ResetSettings();
                check("Confirmed: the destination fields are the defaults", vm.ServerHost == "ServerHostname" && vm.ServerPort == 104 && vm.ServerAET == "ServerAET" && vm.CallingAET == "DCST4" && !vm.UseTLS && vm.ProfileName == string.Empty, vm.ServerHost);
                check("Confirmed: the Tests tab is the defaults, with nothing ticked", vm.TestUidRoot == "2.25" && vm.CommitWaitSeconds == 60 && vm.LimitMaxAssociations == 10 && vm.TestSuite.ResultsFolder == TestSuiteViewModel.DefaultResultsFolder
                    && vm.TestSuite.SelectedStoreSyntax == vm.TestSuite.StoreSyntaxChoices[0] && vm.TestSuite.Choices.All(c => !c.IsSelected), vm.TestSuite.ResultsFolder);
                check("Confirmed: the Performance tab is the defaults", vm.Performance.SelectedMode == vm.Performance.Modes[0] && vm.Performance.Associations == Core.Performance.LoadSettings.DefaultAssociations, vm.Performance.Associations.ToString());
                check("Confirmed: the exam and File > Settings are the defaults", vm.SelectedModality.Code == "CT" && vm.SeriesCount == 1 && !vm.BurnInText && (string)settings["TestPatientNaming"] == "MarkedTestName" && (int)settings["ResponseTimeoutSeconds"] == 60, (string)settings["TestPatientNaming"]);
                check("Confirmed: the saved destination profiles are kept", (string)settings["ConnectionProfiles"] == "<kept on purpose/>", null);
                check("Confirmed: saved once, and every binding told", h.Saves == savesBefore + 1 && raised.Contains(string.Empty) && vm.StatusText.Contains("reset"), vm.StatusText);

                // ------------------------------------------------ not while a run is going on
                vm.ServerHost = "127.0.0.1";
                vm.ServerPort = port;
                vm.ServerAET = "MOCKSCP";
                vm.TestSuite.ResultsFolder = Path.Combine(Path.GetTempPath(), "DCST4-reset-" + Guid.NewGuid().ToString("N"));
                foreach (var choice in vm.TestSuite.Choices) choice.IsSelected = choice.Test.Id == "c-echo";
                Scp.Reset();
                Scp.EchoDelayMs = 3000;
                questions.Clear();
                var tried = false;
                await h.Execute(vm.RunTestsCommand, () =>
                {
                    if (tried || !vm.IsBusy || Scp.EchoCount == 0) return;
                    tried = true;
                    vm.ResetSettings();
                    vm.CancelCommand.Execute(null);
                });
                Scp.EchoDelayMs = 0;
                check("While a run is going on: refused with a message, nothing asked or reset", tried && questions.Count == 0 && h.Messages.Any(m => m.Contains("before resetting")) && vm.ServerHost == "127.0.0.1", string.Join("; ", h.Messages));
                try { Directory.Delete(vm.TestSuite.ResultsFolder, true); } catch (IOException) { }
            }
            finally
            {
                foreach (var pair in snapshot) settings[pair.Key] = pair.Value;
            }
        }

        private static async Task RunAll(int port, Action<string, bool, string> check)
        {
            Console.WriteLine("\nMain window view model");
            var h = new Harness(port);
            var vm = h.Vm;

            // ------------------------------------------------ Auto by default
            check("All identifier fields start on Auto", vm.IdentityFields.All(f => f.IsAuto), null);
            check("Auto Patient ID is on the test list", TestPersonnummerRegistry.Default.Contains(vm.PatientIdField.Value), vm.PatientIdField.Value);
            check("Preview shows the birth date of that number", vm.BirthDateField.Value == vm.PatientIdField.Value.Substring(0, 8), vm.BirthDateField.Value);
            check("Preview has no warning", vm.IdentityMessage == null, vm.IdentityMessage);

            // ------------------------------------------------ Manual values do not disturb the others
            var id = vm.PatientIdField.Value;
            var accession = vm.AccessionField.Value;
            var studyUid = vm.StudyUidField.Value;
            var name = vm.PatientNameField.Value;
            var referring = vm.ReferringField.Value;
            vm.StudyDescriptionField.IsAuto = false;
            vm.StudyDescriptionField.Value = "Typing a description";
            vm.PatientNameField.IsAuto = false;
            vm.PatientNameField.Value = "Åström^Örjan";
            check("A manual value leaves the Auto values alone",
                vm.PatientIdField.Value == id && vm.AccessionField.Value == accession && vm.StudyUidField.Value == studyUid && vm.ReferringField.Value == referring,
                $"{id}->{vm.PatientIdField.Value} {accession}->{vm.AccessionField.Value}");

            vm.PatientNameField.IsAuto = true;
            check("Back to Auto restores the generated name", vm.PatientNameField.Value == name, vm.PatientNameField.Value);
            vm.PatientNameField.IsAuto = false;
            check("Manual again restores what was typed", vm.PatientNameField.Value == "Åström^Örjan", vm.PatientNameField.Value);
            vm.StudyDescriptionField.IsAuto = true;

            vm.NewValuesCommand.Execute(null);
            check("New values draws a new patient", vm.AccessionField.Value != accession && vm.StudyUidField.Value != studyUid, vm.AccessionField.Value);
            check("New values keeps the manual value", vm.PatientNameField.Value == "Åström^Örjan" && !vm.PatientNameField.IsAuto, vm.PatientNameField.Value);
            vm.PatientNameField.IsAuto = true;

            // ------------------------------------------------ Personnummer follow-through and warnings
            vm.PatientIdField.IsAuto = false;
            vm.PatientIdField.Value = "189001019802";
            check("Listed manual ID: no warning", vm.IdentityMessage == null, vm.IdentityMessage);
            check("Manual personnummer fills birth date", vm.BirthDateField.Value == "18900101", vm.BirthDateField.Value);
            check("Manual personnummer fills sex", vm.SexField.Value == "F", vm.SexField.Value);
            vm.PatientIdField.Value = "NOT-ON-THE-LIST";
            check("Unlisted manual ID: warning shown", (vm.IdentityMessage ?? string.Empty).Contains("Skatteverket"), vm.IdentityMessage);
            check("Unreadable manual ID: birth date empty", vm.BirthDateField.Value == string.Empty, vm.BirthDateField.Value);
            vm.PatientIdField.IsAuto = true;
            check("Back to Auto clears the warning", vm.IdentityMessage == null, vm.IdentityMessage);

            vm.MinAge = 500;
            check("Impossible age range: explained", (vm.IdentityMessage ?? string.Empty).Contains("500"), vm.IdentityMessage);
            h.Messages.Clear();
            await h.Execute(vm.RunCommand);
            check("Impossible age range: run does not start", h.Messages.Count == 1 && vm.Results.Count == 0, string.Join("; ", h.Messages));
            vm.MinAge = 0;
            check("Possible age range again: no message", vm.IdentityMessage == null, vm.IdentityMessage);

            // ------------------------------------------------ Manual UID rules
            vm.SeriesUidField.IsAuto = false;
            check("Unticking Auto does not keep the explanatory text as a value", vm.SeriesUidField.Value == string.Empty, vm.SeriesUidField.Value);
            vm.SeriesUidField.Value = DicomUID.Generate().UID;
            vm.SeriesCount = 2;
            check("Two series: Series UID forced back to Auto", vm.SeriesUidField.IsAuto && !vm.SeriesUidField.ManualAllowed, null);
            check("Two series: tooltip explains why", vm.SeriesUidField.ToolTip.Contains("one series"), vm.SeriesUidField.ToolTip);
            check("Two images: SOP UID not manual", !vm.SopUidField.ManualAllowed, null);
            vm.SeriesCount = 1;
            check("One series: Series UID can be manual again", vm.SeriesUidField.ManualAllowed && vm.SeriesUidField.ToolTip == vm.SeriesUidField.Hint, null);
            check("One image: SOP UID can be manual", vm.SopUidField.ManualAllowed, null);
            vm.ImagesPerSeries = 3;
            check("Three images: SOP UID not manual", !vm.SopUidField.ManualAllowed, null);
            vm.ImagesPerSeries = 1;

            // ------------------------------------------------ Modality switching
            vm.SelectedModality = Core.Generation.Modalities.ModalityCatalog.Find("MR");
            check("MR: exam types follow", vm.Presets.Any(p => p.Name == "Knee") && vm.SelectedPreset.Name == "Brain T1", vm.SelectedPreset?.Name);
            check("MR: no 8-bit option", !vm.BitDepths.Contains(8) && vm.BitsStored == 12, vm.BitsStored.ToString());
            check("MR: matrix default", vm.Rows == 256 && vm.Columns == 256, vm.Rows.ToString());
            check("MR: image count from the exam type", vm.ImagesPerSeries == 24, vm.ImagesPerSeries.ToString());
            check("MR: station name follows", vm.StationField.Value == "DCST4-MR", vm.StationField.Value);
            check("MR: description follows", vm.StudyDescriptionField.Value == "MR Brain", vm.StudyDescriptionField.Value);
            vm.SelectedPreset = vm.Presets.First(p => p.Name == "Knee");
            check("Knee: description follows", vm.StudyDescriptionField.Value == "MR Knee", vm.StudyDescriptionField.Value);
            vm.SelectedModality = Core.Generation.Modalities.ModalityCatalog.Find("DX");
            check("DX: 8-bit option", vm.BitDepths.Contains(8), null);
            check("DX: one image by default", vm.ImagesPerSeries == 1, vm.ImagesPerSeries.ToString());
            vm.SelectedModality = Core.Generation.Modalities.ModalityCatalog.Find("US");
            check("US: is offered", vm.Modalities.Any(m => m.Code == "US"), null);
            check("US: only 8 bits", vm.BitDepths.SequenceEqual(new[] { 8 }) && vm.BitsStored == 8, vm.BitsStored.ToString());
            check("US: exam types follow", vm.Presets.Any(p => p.Name == "Thyroid") && vm.SelectedPreset.Name == "Abdomen", vm.SelectedPreset?.Name);
            check("US: matrix default", vm.Rows == 600 && vm.Columns == 800, vm.Rows + "x" + vm.Columns);
            check("US: station name follows", vm.StationField.Value == "DCST4-US", vm.StationField.Value);
            vm.ImagesPerSeries = 2;
            Scp.Reset();
            await h.Execute(vm.RunCommand);
            check("US: a real run reaches the SCP", Scp.StoreCount == 2 && Scp.Received.All(r => r.Dataset.GetString(DicomTag.Modality) == "US"), vm.LastRunText);

            foreach (var code in new[] { "XA", "RF", "MG", "PT", "NM" })
            {
                var modality = Core.Generation.Modalities.ModalityCatalog.Find(code);
                vm.SelectedModality = modality;
                check($"{code}: is offered, with its own lists and defaults",
                    vm.Modalities.Contains(modality) && vm.Presets.SequenceEqual(modality.Presets) && vm.BitDepths.SequenceEqual(modality.BitDepths) && vm.BitsStored == modality.DefaultBitDepth && vm.Rows == modality.DefaultRows && vm.Columns == modality.DefaultColumns,
                    vm.Rows + "x" + vm.Columns + " " + vm.BitsStored);
                check($"{code}: station name follows", vm.StationField.Value == "DCST4-" + code, vm.StationField.Value);
                vm.Rows = 64;
                vm.Columns = 64;
                vm.ImagesPerSeries = 1;
                Scp.Reset();
                await h.Execute(vm.RunCommand);
                check($"{code}: a real run reaches the SCP", Scp.StoreCount == 1 && Scp.Received.Single().Dataset.GetString(DicomTag.Modality) == code, vm.LastRunText);
            }

            // ------------------------------------------------ Patient naming for test-suite datasets
            // The settings class is internal to the app. Only changed in memory, and put back: nothing here saves them.
            var settings = (System.Configuration.ApplicationSettingsBase)typeof(SettingsDialog).Assembly.GetType("DCST4.Properties.Settings").GetProperty("Default").GetValue(null);
            var savedNaming = settings["TestPatientNaming"];
            try
            {
                Func<string> shownNaming = () => (string)((ComboBox)new SettingsDialog().FindName("TestPatientNaming")).SelectedValue;
                settings["TestPatientNaming"] = "MarkedTestName";
                check("Settings: the saved naming is selected", shownNaming() == "MarkedTestName", shownNaming());
                settings["TestPatientNaming"] = "RegistrySwedishName";
                check("Settings: the Swedish name can be chosen", shownNaming() == "RegistrySwedishName", shownNaming());
                settings["TestPatientNaming"] = "MarkedTestName";
                check("Settings: the default is the marked test name", shownNaming() == "MarkedTestName" && settings.Properties["TestPatientNaming"].DefaultValue as string == "MarkedTestName", shownNaming());
                settings["TestPatientNaming"] = "nonsense";
                check("Settings: an unreadable value shows the default", shownNaming() == "MarkedTestName", shownNaming());
            }
            finally
            {
                settings["TestPatientNaming"] = savedNaming;
            }

            // ------------------------------------------------ Burned-in text
            vm.SelectedModality = Core.Generation.Modalities.ModalityCatalog.Find("CT");
            vm.Rows = 64;
            vm.Columns = 64;
            vm.ImagesPerSeries = 1;
            Scp.Reset();
            await h.Execute(vm.RunCommand);
            check("Burned-in text is off when set off", Scp.Received.Single().Dataset.GetString(DicomTag.BurnedInAnnotation) == "NO", Scp.Received.Single().Dataset.GetString(DicomTag.BurnedInAnnotation));
            vm.BurnInText = true;
            Scp.Reset();
            await h.Execute(vm.RunCommand);
            check("Burned-in text on: the run says so", Scp.Received.Single().Dataset.GetString(DicomTag.BurnedInAnnotation) == "YES", Scp.Received.Single().Dataset.GetString(DicomTag.BurnedInAnnotation));
            vm.BurnInTags = string.Empty;
            Scp.Reset();
            await h.Execute(vm.RunCommand);
            check("Burned-in text on with no tags ticked: no text", Scp.Received.Single().Dataset.GetString(DicomTag.BurnedInAnnotation) == "NO", Scp.Received.Single().Dataset.GetString(DicomTag.BurnedInAnnotation));
            vm.BurnInText = false;
            vm.BurnInTags = BurnedInTagCatalog.DefaultKeywords;

            // ------------------------------------------------ Empty character set
            check("Empty value is on the list, but ISO_IR 100 comes first", vm.CharacterSets.Contains(DicomCharacterSets.EmptyDisplayName) && vm.CharacterSets[0] == DicomCharacterSets.Latin1, null);
            vm.CharacterSet = DicomCharacterSets.EmptyDisplayName;
            Scp.Reset();
            h.Messages.Clear();
            await h.Execute(vm.RunCommand);
            var emptyRun = Scp.Received.Single().Dataset;
            check("Empty value: a run goes through with Auto names", h.Messages.Count == 0 && emptyRun.Contains(DicomTag.SpecificCharacterSet), string.Join("; ", h.Messages));
            check("Empty value: the tag is sent with zero length", emptyRun.GetDicomItem<DicomElement>(DicomTag.SpecificCharacterSet).Length == 0, null);
            check("Empty value: the preview shows what is sent", vm.PatientNameField.Value == emptyRun.GetString(DicomTag.PatientName) && vm.PatientNameField.Value.All(c => c < 0x80), vm.PatientNameField.Value);

            vm.PatientNameField.IsAuto = false;
            vm.PatientNameField.Value = "Åström^Märta";
            Scp.Reset();
            h.Messages.Clear();
            await h.Execute(vm.RunCommand);
            check("Empty value: a manual å is blocked with a message", h.Messages.Count == 1 && h.Messages[0].Contains("Patient Name") && h.Messages[0].Contains("Basic ASCII") && Scp.AssociationCount == 0, string.Join("; ", h.Messages));
            vm.PatientNameField.IsAuto = true;
            h.Messages.Clear();

            check("The saved form of Empty value is not an empty setting", vm.CharacterSet == DicomCharacterSets.EmptyDisplayName, vm.CharacterSet);
            check("A saved Empty value is selected at program start", new MainViewModel().CharacterSet == DicomCharacterSets.EmptyDisplayName, null);
            vm.CharacterSet = string.Empty;
            check("A blank saved setting still starts as ISO_IR 100", new MainViewModel().CharacterSet == DicomCharacterSets.Latin1, null);
            vm.CharacterSet = DicomCharacterSets.Latin1;

            // ------------------------------------------------ A full run
            vm.SelectedModality = Core.Generation.Modalities.ModalityCatalog.Find("CT");
            vm.Rows = 64;
            vm.Columns = 64;
            vm.ImagesPerSeries = 12;
            vm.PatientNameField.IsAuto = false;
            vm.PatientNameField.Value = "Åström^Örjan";
            vm.AccessionField.IsAuto = false;
            vm.AccessionField.Value = "ACC-VM-1";
            vm.CharacterSet = DicomCharacterSets.Utf8;
            var previewId = vm.PatientIdField.Value;

            Scp.Reset();
            h.Messages.Clear();
            await h.Execute(vm.RunCommand);
            check("Run: all images get a row", vm.Results.Count == 12, vm.Results.Count.ToString());
            check("Run: SCP received them", Scp.StoreCount == 12, Scp.StoreCount.ToString());
            check("Run: progress reaches the end", vm.ProgressValue == 12 && vm.ProgressMaximum == 12, vm.ProgressValue + "/" + vm.ProgressMaximum);
            check("Run: no dialog", h.Messages.Count == 0, string.Join("; ", h.Messages));
            check("Run: finished status", vm.StatusText == "Run finished.", vm.StatusText);
            check("Run: summary shown", vm.LastRunText.Contains("12 ok"), vm.LastRunText);
            var first = Scp.Received.First().Dataset;
            check("Run: manual name arrives unchanged", first.GetString(DicomTag.PatientName) == "Åström^Örjan", first.GetString(DicomTag.PatientName));
            check("Run: selected character set used", first.GetString(DicomTag.SpecificCharacterSet) == DicomCharacterSets.Utf8, first.GetString(DicomTag.SpecificCharacterSet));
            check("Run: manual accession arrives", first.GetString(DicomTag.AccessionNumber) == "ACC-VM-1", first.GetString(DicomTag.AccessionNumber));
            check("Run: the previewed Patient ID is what is sent", first.GetString(DicomTag.PatientID) == previewId, first.GetString(DicomTag.PatientID));
            var studyDate = first.GetString(DicomTag.StudyDate);
            check("Run: Auto study date is today", studyDate == DateTime.Now.ToString("yyyyMMdd"), studyDate);
            check("Run: Auto values stay afterwards", vm.PatientIdField.Value == previewId, vm.PatientIdField.Value);
            check("Run: manual values stay", vm.AccessionField.Value == "ACC-VM-1", vm.AccessionField.Value);

            vm.AccessionField.IsAuto = true;
            vm.PatientNameField.IsAuto = true;
            vm.CharacterSet = DicomCharacterSets.Latin1;

            // ------------------------------------------------ Values between runs
            vm.ImagesPerSeries = 1;
            Scp.Reset();
            await h.Execute(vm.RunCommand);
            var firstRun = Scp.Received.Single().Dataset;
            Scp.Reset();
            await h.Execute(vm.RunCommand);
            var secondRun = Scp.Received.Single().Dataset;
            check("Second run: same patient, accession and study",
                secondRun.GetString(DicomTag.PatientID) == firstRun.GetString(DicomTag.PatientID)
                && secondRun.GetString(DicomTag.AccessionNumber) == firstRun.GetString(DicomTag.AccessionNumber)
                && secondRun.GetString(DicomTag.StudyInstanceUID) == firstRun.GetString(DicomTag.StudyInstanceUID),
                secondRun.GetString(DicomTag.StudyInstanceUID));
            check("Second run: new series and image UIDs",
                secondRun.GetString(DicomTag.SeriesInstanceUID) != firstRun.GetString(DicomTag.SeriesInstanceUID)
                && secondRun.GetString(DicomTag.SOPInstanceUID) != firstRun.GetString(DicomTag.SOPInstanceUID),
                secondRun.GetString(DicomTag.SOPInstanceUID));

            var studyAfterRuns = vm.StudyUidField.Value;
            check("Screen shows the study that was sent", studyAfterRuns == secondRun.GetString(DicomTag.StudyInstanceUID), studyAfterRuns);
            vm.NewValuesCommand.Execute(null);
            check("New values after a run: new study", vm.StudyUidField.Value != studyAfterRuns, vm.StudyUidField.Value);

            vm.NewValuesAfterRun = true;
            var studyBeforeRun = vm.StudyUidField.Value;
            Scp.Reset();
            await h.Execute(vm.RunCommand);
            check("Setting on: the run used the values on screen", Scp.Received.Single().Dataset.GetString(DicomTag.StudyInstanceUID) == studyBeforeRun, studyBeforeRun);
            check("Setting on: new values after the run", vm.StudyUidField.Value != studyBeforeRun, vm.StudyUidField.Value);
            vm.NewValuesAfterRun = false;

            check("Program start: draws its own values", new Harness(port).Vm.StudyUidField.Value != vm.StudyUidField.Value, null);

            // ------------------------------------------------ Tags added in Settings
            var fixedRows = vm.IdentityFields.Count;
            check("No additional rows by default", fixedRows == 15, fixedRows.ToString());
            var operatorsSetting = AdditionalTagSetting.From(CommonTagCatalog.All.First(c => c.Tag.Element == 0x1070).Tag);
            var customSetting = AdditionalTagSetting.From(new AdditionalTag(0x0011, 0x1001, "LO", "DCST4", "Default text"));
            var emptySetting = AdditionalTagSetting.From(new AdditionalTag(0x0010, 0x2180, "SH"));
            var admissionSetting = AdditionalTagSetting.From(CommonTagCatalog.All.First(c => c.Tag.Group == 0x0038).Tag);
            var patientBefore = vm.PatientIdField.Value;

            vm.AdditionalTagSettings = new[] { operatorsSetting, customSetting, emptySetting, admissionSetting };
            check("Added tags get rows", vm.IdentityFields.Count == fixedRows + 4, vm.IdentityFields.Count.ToString());
            var operatorsRow = vm.IdentityFields[fixedRows];
            var customRow = vm.IdentityFields[fixedRows + 1];
            var emptyRow = vm.IdentityFields[fixedRows + 2];
            var admissionRow = vm.IdentityFields[fixedRows + 3];
            check("Rows start on Auto", operatorsRow.IsAuto && customRow.IsAuto && emptyRow.IsAuto, null);
            check("Common tag: named and generated", operatorsRow.Label == "Operators' Name" && operatorsRow.Value.Contains("^"), operatorsRow.Label + " " + operatorsRow.Value);
            check("Custom tag: Auto shows its default", customRow.Value == "Default text" && customRow.Label == "(0011,1001)", customRow.Label + " " + customRow.Value);
            check("Custom tag without a default says so", emptyRow.Value == "(sent empty)", emptyRow.Value);
            check("The tooltip gives number and VR", operatorsRow.Hint.Contains("(0008,1070) PN") && customRow.Hint.Contains("private creator DCST4"), customRow.Hint);
            check("Adding tags leaves the patient alone", vm.PatientIdField.Value == patientBefore, vm.PatientIdField.Value);

            vm.ImagesPerSeries = 1;
            Scp.Reset();
            await h.Execute(vm.RunCommand);
            var added = Scp.Received.Single().Dataset;
            var customTag = new DicomTag(0x0011, 0x1001, "DCST4");
            check("Run sends what the rows show",
                added.GetString(DicomTag.OperatorsName) == operatorsRow.Value
                && added.GetString(DicomTag.AdmissionID) == admissionRow.Value
                && added.GetString(customTag) == "Default text"
                && added.Contains(DicomTag.Occupation) && added.GetDicomItem<DicomElement>(DicomTag.Occupation).Length == 0,
                added.GetString(DicomTag.OperatorsName));
            check("Run: additional tags are in the log",
                Program.LogSink.Lines.Any(l => l.Contains("Additional tags:") && l.Contains("(0011,1001) = 'Default text'")), null);

            var generatedAdmission = admissionRow.Value;
            vm.NewValuesCommand.Execute(null);
            check("New values draws new values for added tags", admissionRow.Value != generatedAdmission, admissionRow.Value);
            check("New values keeps a custom tag's default", customRow.Value == "Default text", customRow.Value);

            customRow.IsAuto = false;
            customRow.Value = "Typed text";
            Scp.Reset();
            await h.Execute(vm.RunCommand);
            check("A typed value is sent", Scp.Received.Single().Dataset.GetString(customTag) == "Typed text", null);

            var operatorsValue = operatorsRow.Value;
            vm.AdditionalTagSettings = new[] { customSetting };
            check("Removed tags lose their rows", vm.IdentityFields.Count == fixedRows + 1 && !vm.IdentityFields.Contains(operatorsRow) && !vm.IdentityFields.Contains(emptyRow), vm.IdentityFields.Count.ToString());
            check("A tag that stays keeps its typed value", ReferenceEquals(vm.IdentityFields[fixedRows], customRow) && customRow.Value == "Typed text" && !customRow.IsAuto, customRow.Value);

            vm.AdditionalTagSettings = new[] { customSetting, operatorsSetting };
            check("A tag added again has the same generated value", vm.IdentityFields[fixedRows + 1].Value == operatorsValue, vm.IdentityFields[fixedRows + 1].Value);

            vm.AdditionalTagSettings = new[] { emptySetting };
            var departmentRow = vm.IdentityFields[fixedRows];
            departmentRow.IsAuto = false;
            departmentRow.Value = new string('x', 65);
            check("A bad value is explained at once", (vm.IdentityMessage ?? string.Empty).Contains("(0010,2180)"), vm.IdentityMessage);
            Scp.Reset();
            h.Messages.Clear();
            await h.Execute(vm.RunCommand);
            check("A bad value blocks the run", h.Messages.Count == 1 && h.Messages[0].Contains("(0010,2180)") && Scp.AssociationCount == 0, string.Join("; ", h.Messages));

            departmentRow.Value = "Ωmega";
            check("A value ISO_IR 100 cannot hold is explained", (vm.IdentityMessage ?? string.Empty).Contains("ISO_IR 100"), vm.IdentityMessage);
            vm.CharacterSet = Core.Generation.DicomCharacterSets.Utf8;
            check("Changing the character set clears the message", vm.IdentityMessage == null, vm.IdentityMessage);
            vm.CharacterSet = Core.Generation.DicomCharacterSets.Latin1;

            vm.AdditionalTagSettings = new[] { new AdditionalTagSetting { Tag = "nonsense" }, new AdditionalTagSetting { Tag = "(0010,0020)", Vr = "LO" }, emptySetting };
            check("A saved tag that is not allowed is left out", vm.IdentityFields.Count == fixedRows + 1, vm.IdentityFields.Count.ToString());
            vm.AdditionalTagSettings = new AdditionalTagSetting[0];
            check("Back to the fixed rows, with no message", vm.IdentityFields.Count == fixedRows && vm.IdentityMessage == null, vm.IdentityMessage);

            // ------------------------------------------------ A failing run reports without interrupting
            Scp.Reset();
            Scp.StatusFor = i => i % 2 == 0 ? DicomStatus.StorageStorageOutOfResources : DicomStatus.Success;
            h.Messages.Clear();
            vm.ImagesPerSeries = 4;
            await h.Execute(vm.RunCommand);
            check("Failure statuses: no dialog", h.Messages.Count == 0, string.Join("; ", h.Messages));
            check("Failure statuses: rows show them", vm.Results.Count(r => r.State == StoreState.Failure) == 2, vm.LastRunText);

            Scp.Reset();
            Scp.RejectAssociation = true;
            h.Messages.Clear();
            await h.Execute(vm.RunCommand);
            check("Rejected association: no dialog", h.Messages.Count == 0, string.Join("; ", h.Messages));
            check("Rejected association: status says why", vm.StatusText.StartsWith("Run stopped") && vm.StatusText.IndexOf("reject", StringComparison.OrdinalIgnoreCase) >= 0, vm.StatusText);

            // ------------------------------------------------ Input that blocks a run
            Scp.Reset();
            h.Messages.Clear();
            vm.PatientNameField.IsAuto = false;
            vm.PatientNameField.Value = "Łukasz^Żak";
            await h.Execute(vm.RunCommand);
            check("Name ISO_IR 100 cannot hold: blocked with a message", h.Messages.Count == 1 && h.Messages[0].Contains("ISO_IR 100") && Scp.AssociationCount == 0, string.Join("; ", h.Messages));
            vm.PatientNameField.IsAuto = true;

            h.Messages.Clear();
            vm.StudyDateTimeField.IsAuto = false;
            vm.StudyDateTimeField.Value = "yesterday";
            check("Bad study time: explained at once", (vm.IdentityMessage ?? string.Empty).Contains("yyyy-MM-dd"), vm.IdentityMessage);
            await h.Execute(vm.RunCommand);
            check("Bad study time: blocked", h.Messages.Count == 1 && Scp.AssociationCount == 0, string.Join("; ", h.Messages));
            vm.StudyDateTimeField.Value = "2024-03-05 08:09:10";
            Scp.Reset();
            vm.ImagesPerSeries = 1;
            await h.Execute(vm.RunCommand);
            check("Manual study time arrives", Scp.Received.Single().Dataset.GetString(DicomTag.StudyDate) == "20240305" && Scp.Received.Single().Dataset.GetString(DicomTag.StudyTime).StartsWith("080910"), Scp.Received.Single().Dataset.GetString(DicomTag.StudyTime));
            vm.StudyDateTimeField.IsAuto = true;

            h.Messages.Clear();
            vm.CallingAET = new string('A', 17);
            await h.Execute(vm.DicomEchoCommand);
            check("Over-long AE title: echo blocked", h.Messages.Count == 1 && h.Messages[0].Contains("16"), string.Join("; ", h.Messages));
            vm.CallingAET = "DCST4";

            vm.SendImages = false;
            vm.SaveImages = false;
            h.Messages.Clear();
            Scp.Reset();
            await h.Execute(vm.RunCommand);
            check("Neither send nor save: blocked", h.Messages.Count == 1 && Scp.AssociationCount == 0, string.Join("; ", h.Messages));

            // ------------------------------------------------ Save only
            var folder = Path.Combine(Path.GetTempPath(), "DCST4VmTest_" + Guid.NewGuid().ToString("N"));
            try
            {
                vm.SaveImages = true;
                vm.SaveFolder = folder;
                check("Run button says what it does", vm.RunButtonText == "Generate & Save", vm.RunButtonText);
                h.Messages.Clear();
                Scp.Reset();
                vm.ImagesPerSeries = 2;
                await h.Execute(vm.RunCommand);
                check("Save only: files written, nothing sent", Directory.GetFiles(Path.Combine(folder, "DICOM"), "*", SearchOption.AllDirectories).Length == 2 && Scp.AssociationCount == 0, vm.StatusText);
                check("Save only: DICOMDIR is written and named in the status", File.Exists(Path.Combine(folder, "DICOMDIR")) && vm.StatusText.Contains("DICOMDIR"), vm.StatusText);
                check("Save only: rows show the state", vm.Results.All(r => r.State == StoreState.Saved && r.SavedPath != null), vm.LastRunText);

                await h.Execute(vm.RunCommand);
                check("Saved twice with the same values: all files kept", Directory.GetFiles(Path.Combine(folder, "DICOM"), "*", SearchOption.AllDirectories).Length == 4, vm.StatusText);
                check("Saved twice with the same values: DICOMDIR written", !vm.StatusText.Contains("could not be written"), vm.StatusText);
                var patients = FellowOakDicom.Media.DicomDirectory.Open(Path.Combine(folder, "DICOMDIR")).RootDirectoryRecordCollection.ToList();
                var studies = patients.SelectMany(p => p.LowerLevelDirectoryRecordCollection).ToList();
                var series = studies.SelectMany(s => s.LowerLevelDirectoryRecordCollection).ToList();
                var images = series.SelectMany(s => s.LowerLevelDirectoryRecordCollection).ToList();
                check("Saved twice with the same values: one study with two series and four images",
                    patients.Count == 1 && studies.Count == 1 && series.Count == 2 && images.Count == 4,
                    $"{patients.Count} patients, {studies.Count} studies, {series.Count} series, {images.Count} images");
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            vm.SaveImages = false;
            vm.SendImages = true;
            check("Run button returns to sending", vm.RunButtonText == "Generate & Send", vm.RunButtonText);

            // ------------------------------------------------ Echo and cancel
            h.Messages.Clear();
            Scp.Reset();
            await h.Execute(vm.DicomEchoCommand);
            check("Echo: result shown to the user", h.Messages.Count == 1 && h.Messages[0].Contains("successful"), string.Join("; ", h.Messages));
            check("Echo: status has no worklist prefix", vm.StatusText.StartsWith("Echo successful"), vm.StatusText);

            h.Messages.Clear();
            Scp.Reset();
            Scp.ResponseDelayMs = 100;
            vm.ImagesPerSeries = 100;
            var cancelled = false;
            await h.Execute(vm.RunCommand, () =>
            {
                if (!cancelled && vm.Results.Count >= 2 && vm.CancelCommand.CanExecute(null))
                {
                    cancelled = true;
                    vm.CancelCommand.Execute(null);
                }
            });
            check("Cancel: run stops early", cancelled && Scp.StoreCount < 100, Scp.StoreCount.ToString());
            check("Cancel: status says so", vm.StatusText.StartsWith("Run cancelled"), vm.StatusText);
            check("Cancel: no dialog", h.Messages.Count == 0, string.Join("; ", h.Messages));
            check("Cancel: idle again", !vm.IsBusy && vm.RunCommand.CanExecute(null), null);
            vm.ImagesPerSeries = 1;
            Scp.ResponseDelayMs = 0;

            // ------------------------------------------------ Profiles
            foreach (var existing in vm.Profiles.ToList())
            {
                vm.SelectedProfile = existing;
                vm.ProfileName = existing.Name;
                vm.DeleteProfileCommand.Execute(null);
            }
            h.Saves = 0;
            h.Messages.Clear();
            vm.ProfileName = string.Empty;
            vm.SaveProfileCommand.Execute(null);
            check("Profile without a name: asks for one", h.Messages.Count == 1 && vm.Profiles.Count == 0, string.Join("; ", h.Messages));

            vm.CallingAET = "CALLER1";
            vm.ServerAET = "PACS1";
            vm.ServerHost = "host-one";
            vm.ServerPort = 11112;
            vm.ProfileName = "Test PACS";
            vm.SaveProfileCommand.Execute(null);
            check("Profile saved", vm.Profiles.Count == 1 && vm.Profiles[0].Name == "Test PACS", vm.Profiles.Count.ToString());
            check("Profile saved to disk at once", h.Saves == 1, h.Saves.ToString());

            vm.CallingAET = "CALLER2";
            vm.ServerAET = "PACS2";
            vm.ServerHost = "host-two";
            vm.ServerPort = 104;
            vm.ProfileName = "Another";
            vm.SaveProfileCommand.Execute(null);
            check("Profiles are sorted", vm.Profiles.Select(p => p.Name).SequenceEqual(new[] { "Another", "Test PACS" }), string.Join(",", vm.Profiles.Select(p => p.Name)));

            vm.SelectedProfile = vm.Profiles.First(p => p.Name == "Test PACS");
            check("Selecting a profile fills the destination", vm.CallingAET == "CALLER1" && vm.ServerAET == "PACS1" && vm.ServerHost == "host-one" && vm.ServerPort == 11112, vm.ServerHost);

            vm.ProfileName = "Test PACS";
            h.ConfirmAnswer = false;
            vm.DeleteProfileCommand.Execute(null);
            check("Delete needs confirmation", vm.Profiles.Count == 2, vm.Profiles.Count.ToString());
            h.ConfirmAnswer = true;
            vm.DeleteProfileCommand.Execute(null);
            check("Confirmed delete removes it", vm.Profiles.Count == 1 && vm.Profiles[0].Name == "Another", vm.Profiles.Count.ToString());
            check("Delete saved to disk at once", h.Saves == 3, h.Saves.ToString());
        }

        /// <summary>The Tests tab, and that it gives the same result files as the command line.</summary>
        private static async Task RunSizeEstimate(int port, Action<string, bool, string> check)
        {
            Console.WriteLine("\nEstimated size");
            var h = new Harness(port);
            var vm = h.Vm;

            Func<int, string> about = images => "about " + ExamSizeEstimator.Format((ExamSizeEstimator.HeaderBytes + 512 * 512 * 2) * images);
            vm.Rows = 512;
            vm.Columns = 512;
            check("One image", vm.EstimatedSizeText == about(1), vm.EstimatedSizeText);
            vm.SeriesCount = 4;
            check("Follows the number of series", vm.EstimatedSizeText == about(4), vm.EstimatedSizeText);
            vm.ImagesPerSeries = 10;
            check("Follows the number of images", vm.EstimatedSizeText == about(40), vm.EstimatedSizeText);

            vm.Rows = 10;
            check("Out-of-range rows: no number", vm.EstimatedSizeText == "-", vm.EstimatedSizeText);
            vm.Rows = 256;
            check("Follows the rows", vm.EstimatedSizeText == "about " + ExamSizeEstimator.Format((ExamSizeEstimator.HeaderBytes + 256 * 512 * 2) * 40L), vm.EstimatedSizeText);
            vm.Rows = 512;

            vm.SelectedTransferSyntax = TransferSyntaxOption.JpegLossless;
            check("JPEG Lossless: starts by calculating", vm.EstimatedSizeText == "calculating...", vm.EstimatedSizeText);

            Func<Task> measured = async () =>
            {
                var sw = Stopwatch.StartNew();
                while (vm.EstimatedSizeText == "calculating..." && sw.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(20);
            };
            await measured();
            var jpegForty = vm.EstimatedSizeText;
            check("JPEG Lossless: reaches a size", jpegForty.StartsWith("about ", StringComparison.Ordinal) && jpegForty != about(40), jpegForty);

            vm.SeriesCount = 8;
            check("JPEG Lossless: the number of images needs no new sample", vm.EstimatedSizeText.StartsWith("about ", StringComparison.Ordinal) && vm.EstimatedSizeText != jpegForty, vm.EstimatedSizeText);

            vm.Columns = 600;
            check("JPEG Lossless: a new matrix needs a new sample", vm.EstimatedSizeText == "calculating...", vm.EstimatedSizeText);
            vm.Columns = 512;
            await measured();
            check("JPEG Lossless: a change while waiting still ends in a size", vm.EstimatedSizeText.StartsWith("about ", StringComparison.Ordinal), vm.EstimatedSizeText);

            vm.MinAge = 500;
            vm.Rows = 256;
            check("JPEG Lossless with no identity: the upper limit", vm.EstimatedSizeText.StartsWith("at most ", StringComparison.Ordinal), vm.EstimatedSizeText);
            vm.MinAge = 0;
            check("Identity back: measures again", vm.EstimatedSizeText == "calculating...", vm.EstimatedSizeText);
            await measured();
            vm.SelectedTransferSyntax = TransferSyntaxOption.ExplicitVrLe;
            check("Back to uncompressed: calculated at once", vm.EstimatedSizeText == "about " + ExamSizeEstimator.Format((ExamSizeEstimator.HeaderBytes + 256 * 512 * 2) * 80L), vm.EstimatedSizeText);
        }

        private static void RunHelpWindow(Action<string, bool, string> check)
        {
            Console.WriteLine("\nHelp window");
            var help = new HelpWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
            help.Show();
            help.UpdateLayout();
            try
            {
                check("All the topics are listed", help.Topics.Count == 20 && help.Topics.Any(t => t.Title == "Limits and faults") && help.Topics.Any(t => t.Title == "Performance"), help.Topics.Count.ToString());
                check("Topic titles are unique", help.Topics.Select(t => t.Title).Distinct().Count() == help.Topics.Count, null);
                check("Estimated size is explained", new System.Windows.Documents.TextRange(help.Topics[2].Section.ContentStart, help.Topics[2].Section.ContentEnd).Text.Contains("Estimated size"), null);

                var viewer = FindVisualChild<FlowDocumentScrollViewer>(help);
                var scroller = FindVisualChild<ScrollViewer>(viewer);
                check("The document has a scroll bar", scroller != null && scroller.ScrollableHeight > 0, null);

                var last = -1.0;
                var ascending = true;
                foreach (var topic in help.Topics)
                {
                    help.ShowTopic(topic.Title);
                    help.UpdateLayout();
                    ascending &= scroller.VerticalOffset >= last;
                    last = scroller.VerticalOffset;
                }
                check("Each topic scrolls further down the document", ascending && last > 0, last.ToString(System.Globalization.CultureInfo.InvariantCulture));

                help.ShowTopic(help.Topics[0].Title);
                help.UpdateLayout();
                check("The first topic goes back to the top", scroller.VerticalOffset == 0, scroller.VerticalOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            finally
            {
                help.Close();
            }
        }

        private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject parent)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            {
                yield return child;
                foreach (var below in LogicalDescendants(child)) yield return below;
            }
        }

        /// <summary>
        /// The inputs under <paramref name="root"/> that show nothing on hover: a text box that can be typed in, a combo box or a
        /// check box, with no tooltip on itself or on a panel around it. Inputs made from data templates are not in the logical tree.
        /// </summary>
        private static List<string> WithoutToolTip(DependencyObject root)
        {
            var bare = new List<string>();
            foreach (var element in LogicalDescendants(root).OfType<Control>())
            {
                if (!(element is ComboBox || element is CheckBox || (element is TextBox box && !box.IsReadOnly))) continue;
                var explained = false;
                for (DependencyObject at = element; at != null && !explained; at = LogicalTreeHelper.GetParent(at))
                {
                    explained = ToolTipService.GetToolTip(at) != null;
                    if (at == root) break;
                }
                if (explained) continue;
                var binding = System.Windows.Data.BindingOperations.GetBindingExpression(element, element is TextBox ? TextBox.TextProperty : element is ComboBox ? System.Windows.Controls.Primitives.Selector.SelectedItemProperty : System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty);
                bare.Add(element.GetType().Name + " " + (element.Name.Length > 0 ? element.Name : binding?.ParentBinding.Path.Path ?? (element as ContentControl)?.Content?.ToString()));
            }
            return bare;
        }

        private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (var below in VisualDescendants(child)) yield return below;
            }
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var found = child as T ?? FindVisualChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        private static async Task RunPerformanceTab(int port, string temp, Action<string, bool, string> check)
        {
            Console.WriteLine("\nPerformance tab");
            var h = new Harness(port);
            var vm = h.Vm;
            var p = vm.Performance;
            vm.TestSuite.ResultsFolder = Path.Combine(temp, "perf-app");
            p.SelectedMode = p.Modes.First(m => m.Mode == Core.Performance.LoadMode.Load);
            p.SelectedMix = Core.Performance.DataMixCatalog.Find("ct");
            p.Associations = 2;
            p.InstanceCount = 6;
            p.SampleEvery = 0;
            p.WarmUpSeconds = 0;
            p.MaxInstances = 0;

            check("The plan states the count before anything is sent", p.PlanText.StartsWith("Stores 6 new objects", StringComparison.Ordinal) && !p.PlanText.Contains("delete"), p.PlanText);
            check("The Performance tests are not on the Tests tab", vm.TestSuite.Choices.All(c => c.Test.Category != TestCategory.Performance), null);

            Scp.Reset();
            string asked = null;
            vm.Confirm = message => { asked = message; return false; };
            await h.Execute(vm.RunPerformanceCommand);
            check("Run asks first, naming the destination, and a No sends nothing", asked != null && asked.Contains("MOCKSCP at 127.0.0.1:" + port) && Scp.StoreCount == 0 && vm.StatusText.Contains("not started"), asked);

            vm.Confirm = message => true;
            var chartUpdates = 0;
            p.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(p.SeriesVersion)) chartUpdates++; };
            await h.Execute(vm.RunPerformanceCommand);
            check("Run: the test stored what the plan said", Scp.StoreCount == 6 && h.Messages.Count == 0, Scp.StoreCount + " " + string.Join("; ", h.Messages));
            check("Run: the summary table has the whole run", p.Rows.Count == 1 && p.Rows[0].Stored >= 0 && p.Rows[0].Associations == 2 && p.Verdict.StartsWith("PASS", StringComparison.Ordinal), p.Verdict);
            check("Run: the chart got its seconds, drawn at most once a second", p.Series.Count >= 1 && chartUpdates >= 1 && chartUpdates <= p.Series.Count + 2, p.Series.Count + " seconds, " + chartUpdates + " updates");
            check("Run: the result and series files are named", p.Files != null && p.Files.Contains(".json") && p.Files.Contains("-load-throughput.csv"), p.Files);
            check("Run: idle again, progress at the end", !vm.IsBusy && vm.ProgressValue == vm.ProgressMaximum && vm.ProgressMaximum == MainViewModel.PerformanceProgressSteps, vm.ProgressValue + " of " + vm.ProgressMaximum);

            // The progress bar follows a soak while it runs, against its duration.
            p.SelectedMode = p.Modes.First(m => m.Mode == Core.Performance.LoadMode.Soak);
            p.SoakDurationText = "4s";
            p.SoakRatePerSecond = 5;
            p.SnapshotSeconds = 1;
            var seen = new List<int>();
            Scp.Reset();
            await h.Execute(vm.RunPerformanceCommand, () => { if (seen.Count == 0 || seen[seen.Count - 1] != vm.ProgressValue) seen.Add(vm.ProgressValue); });
            var during = seen.Where(v => v > 0 && v < vm.ProgressMaximum).ToList();
            check("Soak: the progress bar rises while it runs, and is full at the end",
                during.Count >= 2 && seen.SequenceEqual(seen.OrderBy(v => v)) && vm.ProgressValue == vm.ProgressMaximum && p.Verdict != null,
                string.Join(",", seen) + " " + p.Verdict);
            p.SelectedMode = p.Modes.First(m => m.Mode == Core.Performance.LoadMode.Load);

            p.RampStepsText = "4,2";
            p.SelectedMode = p.Modes.First(m => m.Mode == Core.Performance.LoadMode.Ramp);
            check("Falling ramp steps: the plan line says what is wrong", p.Problem != null && p.PlanText == p.Problem, p.PlanText);
            p.RampStepsText = "1,2,4";
            check("Valid steps again: the plan is back", p.Problem == null && p.PlanText.StartsWith("Stores up to", StringComparison.Ordinal), p.PlanText);

            // The ceiling box shows the ceiling that applies, not 0.
            var notified = new List<string>();
            p.PropertyChanged += (s, e) => notified.Add(e.PropertyName);
            check("Max instances shows the ramp's default, 2000, and says it is the default", p.MaxInstances == 2000 && p.MaxInstancesNote.Contains("default for this test"), p.MaxInstances + " " + p.MaxInstancesNote);
            p.SelectedMode = p.Modes.First(m => m.Mode == Core.Performance.LoadMode.Load);
            check("Another test: its own default, 1000, and the box is told", p.MaxInstances == 1000 && notified.Contains(nameof(p.MaxInstances)), p.MaxInstances.ToString());
            p.MaxInstances = 5000;
            p.SelectedMode = p.Modes.First(m => m.Mode == Core.Performance.LoadMode.Soak);
            check("A value you set applies to all three tests and is said to be yours", p.MaxInstances == 5000 && p.MaxInstancesNote.Contains("set by you") && p.Settings.MaxInstances == 5000, p.MaxInstances + " " + p.MaxInstancesNote);
            p.MaxInstances = 0;
            p.SoakRatePerSecond = 3;
            p.SoakDurationText = "10m";
            check("0 goes back to the default, which for the soak follows rate and duration", p.MaxInstances == 1800 && p.Settings.MaxInstances == 0, p.MaxInstances.ToString());
            p.SelectedMode = p.Modes.First(m => m.Mode == Core.Performance.LoadMode.Ramp);

            var restored = new PerformanceViewModel(p.SaveSettings(), "Ramp");
            check("The settings are saved and come back", restored.Associations == 2 && restored.InstanceCount == 6 && restored.RampStepsText == "1,2,4" && restored.IsRamp, restored.RampStepsText);
            check("Saved settings that cannot be read give the defaults", new PerformanceViewModel("{not json", "nonsense").Associations == Core.Performance.LoadSettings.DefaultAssociations, null);

            // The chart draws off-screen, with lines in it.
            var chart = new DCST4.Controls.LoadChart { Series = p.Series, Version = p.SeriesVersion, Width = 600, Height = 300 };
            chart.Measure(new Size(600, 300));
            chart.Arrange(new Rect(0, 0, 600, 300));
            var bitmap = new RenderTargetBitmap(600, 300, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(chart);
            var pixels = new byte[600 * 300 * 4];
            bitmap.CopyPixels(pixels, 600 * 4, 0);
            var blue = 0;
            for (var i = 0; i < pixels.Length; i += 4) if (pixels[i] > 150 && pixels[i + 2] < 110) blue++;
            check("The chart renders, with the blue series in it", blue > 20, blue + " blue pixels");

            var many = Enumerable.Range(0, 1500).Select(i => new Core.Performance.LoadSecond { Second = i, Stored = 10, LatencyP95Ms = i == 700 ? 999 : 5 }).ToList();
            var drawn = DCST4.Controls.LoadChart.Downsample(many);
            check("A long soak is drawn as at most 600 points, keeping the highest p95", drawn.Count <= 600 && drawn.Max(r => r.LatencyP95Ms) == 999 && drawn.All(r => r.Stored == 10), drawn.Count.ToString());
            check("Axis maxima are round", DCST4.Controls.LoadChart.NiceMax(37) == 50 && DCST4.Controls.LoadChart.NiceMax(101) == 200 && DCST4.Controls.LoadChart.NiceMax(0) == 1, null);
        }

        /// <summary>The groups, the Check all box and the tooltips of the list on the Tests tab. Leaves nothing ticked.</summary>
        private static void TestGroupsInTheList(TestSuiteViewModel suite, Action<string, bool, string> check)
        {
            var total = suite.Choices.Count;
            check("Check all: clear at the start", suite.AllSelected == false && suite.SelectionText == $"0 of {total} ticked", suite.SelectionText);
            check("Groups: every test in exactly one, the one it names", suite.Groups.Sum(g => g.Tests.Count) == total && suite.Choices.All(c => suite.Groups.Single(g => g.Tests.Contains(c)).Name == c.Group), null);
            check("Groups: in the order of TestGroups, none empty, all folded, no Performance group",
                suite.Groups.Select(g => g.Name).SequenceEqual(TestGroups.All.Where(n => suite.Groups.Any(g => g.Name == n))) && suite.Groups.All(g => g.Tests.Count > 0 && !g.IsExpanded) && suite.Groups.All(g => g.Name != TestGroups.Performance),
                string.Join(", ", suite.Groups.Select(g => g.Name)));

            var store = suite.Groups.Single(g => g.Name == TestGroups.Store);
            store.IsSelected = true;
            check("Group: the header ticks its own tests and no others", store.Tests.All(c => c.IsSelected) && store.IsSelected == true && suite.Choices.Where(c => c.Group != TestGroups.Store).All(c => !c.IsSelected) && suite.AllSelected == null, suite.SelectionText);
            store.Tests[0].IsSelected = false;
            check("Group: one cleared makes it mixed, and the count follows", store.IsSelected == null && store.CountText == $"{store.Tests.Count - 1} of {store.Tests.Count}" && suite.SelectionText == $"{store.Tests.Count - 1} of {total} ticked", store.CountText);
            store.IsSelected = false;
            check("Group: the header clears its tests", store.Tests.All(c => !c.IsSelected) && store.IsSelected == false && suite.AllSelected == false, null);

            suite.AllSelected = true;
            check("Check all: ticks every test and so every group", suite.Choices.All(c => c.IsSelected) && suite.Groups.All(g => g.IsSelected == true) && suite.SelectionText == $"{total} of {total} ticked", suite.SelectionText);
            suite.AllSelected = false;
            check("Check all: clears every test", suite.Choices.All(c => !c.IsSelected) && suite.Groups.All(g => g.IsSelected == false), null);

            var raised = new List<string>();
            System.ComponentModel.PropertyChangedEventHandler listen = (s, e) => raised.Add(e.PropertyName);
            suite.PropertyChanged += listen;
            store.PropertyChanged += listen;
            store.Tests[0].IsSelected = true;
            suite.PropertyChanged -= listen;
            store.PropertyChanged -= listen;
            store.Tests[0].IsSelected = false;
            check("A tick tells the Check all box, the group and both counts", raised.Count(n => n == nameof(TestSuiteViewModel.AllSelected) || n == nameof(TestGroupChoice.IsSelected)) == 2 && raised.Contains(nameof(TestSuiteViewModel.SelectionText)) && raised.Contains(nameof(TestGroupChoice.CountText)), string.Join(",", raised));

            check("Tooltips: what the test does, what it stores, and its id",
                suite.Choices.All(c => c.ToolTipText.StartsWith(c.Test.Description) && c.ToolTipText.EndsWith("Id: " + c.Test.Id) && (c.StorageText != null) == c.Test.StoresObjects
                    && (c.StorageText == null || c.ToolTipText.Contains(c.StorageText))),
                null);
            var ctTip = suite.Choices.Single(c => c.Test.Id == "store-ct").StorageText;
            check("Tooltips: a store test says how much it stores", ctTip == "Stores 1 examination, 1 object, about 34 KB on the destination.", ctTip);
        }

        /// <summary>The Test settings dialog's copy: nothing changes until Apply, and Apply changes nothing when a value is wrong.</summary>
        private static void TestSettingsCopy(MainViewModel vm, string temp, Action<string, bool, string> check)
        {
            var before = vm.BuildTestRunOptions();
            var folder = vm.TestSuite.ResultsFolder;
            var copy = new TestSettingsViewModel(vm);
            check("Test settings: the dialog starts from the current values",
                copy.UidRoot == vm.TestUidRoot && copy.CommitWaitSeconds == before.CommitWaitSeconds.ToString() && copy.MaxAssociations == before.MaxAssociations.ToString()
                && copy.ResultsFolder == folder && copy.StoreSyntax == vm.TestSuite.SelectedStoreSyntax && copy.ExpiredClientCertThumbprint == vm.TlsExpiredClientCertThumbprint,
                copy.UidRoot);

            copy.CommitWaitSeconds = "45";
            copy.UidRoot = "1.2.3";
            check("Test settings: nothing changes before OK", vm.CommitWaitSeconds == before.CommitWaitSeconds && vm.TestUidRoot != "1.2.3", null);

            copy.MaxAssociations = "ten";
            var problem = copy.Apply();
            check("Test settings: a word for a number is refused and named, and nothing is applied", problem != null && problem.Contains("Associations up to") && vm.CommitWaitSeconds == before.CommitWaitSeconds && vm.TestUidRoot != "1.2.3", problem);

            copy.MaxAssociations = "4";
            copy.CommitLingerSeconds = "50";
            problem = copy.Apply();
            check("Test settings: the rules of a run apply, such as the linger not longer than the wait", problem != null && problem.Contains("longer than the wait") && vm.CommitWaitSeconds == before.CommitWaitSeconds, problem);

            copy.CommitLingerSeconds = "0";
            copy.UidRoot = "1.02.3";
            problem = copy.Apply();
            check("Test settings: a bad UID root is refused", problem != null && problem.Contains("UID root"), problem);

            copy.UidRoot = " 1.2.3 ";
            copy.ResultsFolder = "  ";
            problem = copy.Apply();
            check("Test settings: an empty results folder is refused", problem != null && problem.Contains("folder"), problem);

            var newFolder = Path.Combine(temp, "settings-dialog");
            copy.ResultsFolder = newFolder;
            copy.StoreSyntax = copy.StoreSyntaxChoices[1];
            copy.ExpiredClientCertThumbprint = " ABC ";
            problem = copy.Apply();
            var after = vm.BuildTestRunOptions();
            check("Test settings: OK applies every value", problem == null && vm.TestUidRoot == "1.2.3" && after.CommitWaitSeconds == 45 && after.CommitLingerSeconds == 0 && after.MaxAssociations == 4
                && vm.TestSuite.ResultsFolder == newFolder && vm.TestSuite.SelectedStoreSyntax == copy.StoreSyntaxChoices[1] && vm.TlsExpiredClientCertThumbprint == "ABC", problem);

            // Back as the rest of the section expects them.
            vm.TestUidRoot = "2.25";
            vm.CommitLingerSeconds = before.CommitLingerSeconds;
            vm.CommitWaitSeconds = before.CommitWaitSeconds;
            vm.CommitGraceSeconds = before.CommitGraceSeconds;
            vm.LimitMaxAssociations = before.MaxAssociations;
            vm.TestSuite.ResultsFolder = folder;
            vm.TestSuite.SelectedStoreSyntax = vm.TestSuite.StoreSyntaxChoices[0];
            vm.TlsExpiredClientCertThumbprint = string.Empty;
        }

        private static async Task RunTestSuite(int port, int closedPort, string temp, Action<string, bool, string> check)
        {
            Console.WriteLine("\nTests tab");
            var h = new Harness(port);
            var vm = h.Vm;
            var suite = vm.TestSuite;
            var folder = Path.Combine(temp, "tab");
            suite.ResultsFolder = Path.Combine(folder, "app");
            vm.ProfileName = "Comparison";

            var questions = new List<string>();
            vm.Confirm = question => { questions.Add(question); return h.ConfirmAnswer; };

            check("The list holds the catalog without the Performance tests, none ticked", suite.Choices.Count == TestCatalog.All.Count(t => t.Category != TestCategory.Performance) && suite.Choices.All(c => !c.IsSelected), suite.Choices.Count.ToString());
            TestGroupsInTheList(suite, check);
            TestSettingsCopy(vm, folder, check);
            foreach (var choice in suite.Choices) choice.IsSelected = choice.Test.Id == "c-echo";

            // ------------------------------------------------ the same content as the command line
            Scp.Reset();
            await h.Execute(vm.RunTestsCommand);
            check("Run: no dialog", h.Messages.Count == 0, string.Join("; ", h.Messages));
            check("Run: c-echo stores nothing, so nothing is asked", questions.Count == 0, string.Join("; ", questions));
            check("Run: one row with the result", suite.Rows.Count == 1 && suite.Rows[0].Result == TestResultClass.PASS && suite.Rows[0].Status == "0x0000", suite.Rows.Count.ToString());
            check("Run: the row holds the timeline", suite.Rows[0].Timeline.Count == 6, null);
            check("Run: went through the busy state and is idle again", !vm.IsBusy && vm.RunTestsCommand.CanExecute(null), null);
            check("Run: progress reached the end", vm.ProgressValue == 1 && vm.ProgressMaximum == 1, vm.ProgressValue.ToString());
            check("Run: files written", suite.LastResultFiles != null && File.Exists(suite.LastResultFiles.Json) && File.Exists(suite.LastResultFiles.Csv), vm.StatusText);
            check("Run: status names the folder", vm.StatusText.Contains(suite.ResultsFolder), vm.StatusText);

            var appJson = File.ReadAllText(suite.LastResultFiles.Json, Encoding.UTF8);
            var appCsv = File.ReadAllText(suite.LastResultFiles.Csv, Encoding.UTF8);

            var profilePath = Path.Combine(folder, "from-the-app.json");
            vm.SaveTargetProfile(profilePath);
            check("Save as JSON: the file is there", File.Exists(profilePath) && h.Messages.Count == 0, string.Join("; ", h.Messages));
            var saved = TargetProfile.Load(profilePath);
            check("Save as JSON: the destination fields", saved.Endpoint.Host == "127.0.0.1" && saved.Endpoint.Port == port && saved.Endpoint.CalledAe == "MOCKSCP" && saved.Name == "Comparison", saved.Endpoint.Host);

            var cliFolder = Path.Combine(folder, "cli");
            Scp.Reset();
            var cli = await TestSuiteTests.RunCli("run", "--profile", profilePath, "--tests", "c-echo", "--out", cliFolder, "--log-dir", Path.Combine(folder, "logs"));
            check("Command line: exit code 0", cli.ExitCode == 0, cli.Output);
            var cliJson = File.ReadAllText(Directory.GetFiles(cliFolder, "*.json").Single(), Encoding.UTF8);
            var cliCsv = File.ReadAllText(Directory.GetFiles(cliFolder, "*.csv").Single(), Encoding.UTF8);

            var normalizedApp = TestSuiteTests.NormalizeJson(appJson);
            var normalizedCli = TestSuiteTests.NormalizeJson(cliJson);
            check("App and command line: same JSON apart from times", normalizedApp == normalizedCli, "\n  app: " + normalizedApp + "\n  cli: " + normalizedCli);
            check("App and command line: same CSV apart from times", TestSuiteTests.NormalizeCsv(appCsv) == TestSuiteTests.NormalizeCsv(cliCsv), "\n  app: " + appCsv + "\n  cli: " + cliCsv);
            check("App and command line: the comparison had content", normalizedApp.Contains("\"PASS\"") && normalizedApp.Contains("0x0000") && normalizedApp.Contains("A-RELEASE-RP") && normalizedApp.Contains("Comparison"), normalizedApp);

            // ------------------------------------------------ the chain, in the view model
            h.Messages.Clear();
            Scp.Reset();
            Scp.KeepStored = true;
            Scp.MoveDestinations["DCST4"] = Tuple.Create("127.0.0.1", 41215);
            vm.LocalScpPort = 41215;
            Scp.Commit = CommitMode.NewAssociation;
            Scp.CommitDestinations["DCST4"] = Tuple.Create("127.0.0.1", 41215, "DCST4");
            vm.CommitLingerSeconds = 0;
            vm.CommitWaitSeconds = 5;
            vm.CommitGraceSeconds = 1;
            var runOptions = vm.BuildTestRunOptions();
            check("Commitment settings: passed to the run", runOptions.CommitLingerSeconds == 0 && runOptions.CommitWaitSeconds == 5 && runOptions.CommitGraceSeconds == 1, null);
            foreach (var choice in suite.Choices) choice.IsSelected = new[] { "store-ct", "query-index", "retrieve-cget", "retrieve-cmove", "verify-integrity", "commit-stored" }.Contains(choice.Test.Id);
            await h.Execute(vm.RunTestsCommand);
            var storeCt = TestCatalog.All.Single(t => t.Id == "store-ct");
            check("Store warning: asked once, with the destination and the test that stores",
                questions.Count == 1 && questions[0].Contains("MOCKSCP at 127.0.0.1:" + port) && questions[0].Contains("1 of the ticked tests stores") && questions[0].Contains("- " + storeCt.Name + ": 1 examination, 1 object, about 34 KB")
                    && questions[0].Contains("In all: 1 examination, 1 object, about 34 KB.") && !questions[0].Contains("delete"),
                string.Join("; ", questions));
            check("Store warning: the tests that store nothing are not listed", !questions[0].Contains(TestCatalog.All.Single(t => t.Id == "query-index").Name), questions[0]);
            check("Chain: one row per object type", suite.Chain.Rows.Count == 1 && suite.Chain.Rows[0].ObjectType.Length > 0, suite.Chain.Rows.Count.ToString());
            var chainRow = suite.Chain.Rows.Single();
            check("Chain: every step PASS, none invented", chainRow.Accepted == "PASS" && chainRow.Indexed == "PASS" && chainRow.Retrievable == "PASS" && chainRow.Intact == "PASS" && chainRow.Committed == "PASS", chainRow.Detail);
            check("Chain: the detail has the committed step", chainRow.Detail.Contains("Committed: PASS"), chainRow.Detail);

            vm.CommitLingerSeconds = 9;
            vm.CommitWaitSeconds = 3;
            h.Messages.Clear();
            await h.Execute(vm.RunTestsCommand);
            check("Commitment settings: a linger longer than the wait stops the run with a message", h.Messages.Count == 1 && h.Messages[0].Contains("longer than the wait"), string.Join("; ", h.Messages));
            vm.CommitLingerSeconds = 0;
            h.Messages.Clear();
            check("Chain: the detail explains it, level by level", chainRow.Detail.Contains("C-GET") && chainRow.Detail.Contains("C-MOVE") && chainRow.Detail.Contains("Attributes: PASS"), chainRow.Detail);
            check("Chain: the JSON holds the same", File.ReadAllText(suite.LastResultFiles.Json, Encoding.UTF8).Contains("\"chain\""), null);
            check("Chain: the CSV is written", suite.LastResultFiles.ChainCsv != null && File.Exists(suite.LastResultFiles.ChainCsv), null);

            // ------------------------------------------------ declining the store warning
            questions.Clear();
            h.Messages.Clear();
            Scp.Reset();
            h.ConfirmAnswer = false;
            var rowsBefore = suite.Rows.Count;
            await h.Execute(vm.RunTestsCommand);
            check("Store warning: a no runs nothing and leaves the last results", questions.Count == 1 && Scp.AssociationCount == 0 && suite.Rows.Count == rowsBefore && vm.StatusText == "Tests not started." && h.Messages.Count == 0, vm.StatusText);
            h.ConfirmAnswer = true;
            questions.Clear();

            foreach (var choice in suite.Choices) choice.IsSelected = choice.Test.Id == "c-echo";
            await h.Execute(vm.RunTestsCommand);
            check("Chain: a run without store tests clears it", suite.Chain.Rows.Count == 0, suite.Chain.Rows.Count.ToString());

            suite.SelectedStoreSyntax = suite.StoreSyntaxChoices[1];
            check("Store syntax: the list starts with the matrix and has the choices", suite.StoreSyntaxChoices[0].Syntax == null && suite.StoreSyntaxChoices.Count > 2 && suite.SelectedStoreSyntax.Syntax != null, null);
            suite.SelectedStoreSyntax = suite.StoreSyntaxChoices[0];

            // ------------------------------------------------ closed port, from the view model
            h.Messages.Clear();
            vm.ServerPort = closedPort;
            await h.Execute(vm.RunTestsCommand);
            check("Closed port: TIMEOUT or FAIL", suite.Rows.Count == 1 && (suite.Rows[0].Result == TestResultClass.FAIL || suite.Rows[0].Result == TestResultClass.TIMEOUT), suite.Rows.Count.ToString());
            check("Closed port: clear message", suite.Rows[0].Explanation.Contains("127.0.0.1:" + closedPort) && !suite.Rows[0].Explanation.Contains("One or more errors") && !suite.Rows[0].Explanation.Contains("Ett eller flera fel"), suite.Rows[0].Explanation);
            check("Closed port: no dialog during a run", h.Messages.Count == 0, string.Join("; ", h.Messages));
            vm.ServerPort = port;

            // ------------------------------------------------ what the Echo button shows
            h.Messages.Clear();
            vm.ServerPort = closedPort;
            await h.Execute(vm.DicomEchoCommand);
            check("Echo button: a failure still opens a dialog", h.Messages.Count == 1 && h.Messages[0].StartsWith("C-ECHO to 127.0.0.1:"), string.Join("; ", h.Messages));
            vm.ServerPort = port;

            // ------------------------------------------------ refused before running
            h.Messages.Clear();
            Scp.Reset();
            vm.ServerAET = new string('A', 17);
            await h.Execute(vm.RunTestsCommand);
            check("Invalid destination: blocked with a message", h.Messages.Count == 1 && h.Messages[0].Contains("Called AET") && Scp.AssociationCount == 0, string.Join("; ", h.Messages));
            vm.ServerAET = "MOCKSCP";

            h.Messages.Clear();
            suite.Choices[0].IsSelected = false;
            await h.Execute(vm.RunTestsCommand);
            check("Nothing ticked: blocked with a message", h.Messages.Count == 1 && h.Messages[0].Contains("Select") && Scp.AssociationCount == 0, string.Join("; ", h.Messages));
            suite.Choices[0].IsSelected = true;

            // ------------------------------------------------ cancel
            h.Messages.Clear();
            Scp.Reset();
            Scp.EchoDelayMs = 5000;
            var cancelled = false;
            await h.Execute(vm.RunTestsCommand, () =>
            {
                if (!cancelled && Scp.EchoCount == 1 && vm.CancelCommand.CanExecute(null))
                {
                    cancelled = true;
                    vm.CancelCommand.Execute(null);
                }
            });
            check("Cancel: uses the existing command", cancelled, null);
            check("Cancel: the result is INDETERMINATE", suite.Rows.Count == 1 && suite.Rows[0].Result == TestResultClass.INDETERMINATE, suite.Rows.Count.ToString());
            check("Cancel: status says so and files are written", vm.StatusText.StartsWith("Tests cancelled") && suite.LastResultFiles != null, vm.StatusText);
            check("Cancel: no dialog", h.Messages.Count == 0, string.Join("; ", h.Messages));
            Scp.Reset();

            // ------------------------------------------------ a folder that cannot be used
            var notAFolder = Path.Combine(folder, "a-file");
            File.WriteAllText(notAFolder, "not a folder");
            suite.ResultsFolder = notAFolder;
            h.Messages.Clear();
            await h.Execute(vm.RunTestsCommand);
            check("Unusable folder: the status says so, not a crash", vm.StatusText.Contains("could not be saved") && suite.LastResultFiles == null && !vm.IsBusy, vm.StatusText);
            suite.ResultsFolder = Path.Combine(folder, "app");

            // ------------------------------------------------ profiles saved by earlier versions
            const string oldXml = "<?xml version=\"1.0\" encoding=\"utf-16\"?><ArrayOfConnectionProfile xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">"
                + "<ConnectionProfile><Name>From 1.2</Name><CallingAET>OLDCALL</CallingAET><ServerAET>OLDPACS</ServerAET><ServerHost>old-host</ServerHost><ServerPort>4242</ServerPort><UseTLS>true</UseTLS><TlsIgnoreCertErrors>false</TlsIgnoreCertErrors><TlsClientCertThumbprint>AA11</TlsClientCertThumbprint></ConnectionProfile></ArrayOfConnectionProfile>";
            var old = ConnectionProfile.Deserialize(oldXml).Single();
            check("Old profile: still reads", old.Name == "From 1.2" && old.ServerPort == 4242, old.Name);
            var roundTripped = ConnectionProfile.Deserialize(ConnectionProfile.Serialize(new[] { old })).Single();
            check("Old profile: format has no new fields", typeof(ConnectionProfile).GetProperties().Length == 8 && roundTripped.TlsClientCertThumbprint == "AA11", typeof(ConnectionProfile).GetProperties().Length.ToString());

            vm.Profiles.Add(old);
            vm.SelectedProfile = old;
            var fromOld = vm.BuildTargetProfile();
            check("Old profile: becomes a target profile", fromOld.Endpoint.Host == "old-host" && fromOld.Endpoint.Port == 4242 && fromOld.Endpoint.CallingAe == "OLDCALL" && fromOld.Endpoint.CalledAe == "OLDPACS" && fromOld.Endpoint.UseTls && fromOld.Endpoint.TlsClientCertThumbprint == "AA11", fromOld.Endpoint.Host);
            check("Old profile: defaults fill the rest, and it is valid", fromOld.LocalScpAe == TargetProfile.DefaultLocalScpAe && fromOld.UidRoot == TargetProfile.DefaultUidRoot && fromOld.Validate() == null, fromOld.Validate());
            vm.SelectedProfile = null;
            vm.ServerHost = "127.0.0.1";
            vm.ServerPort = port;
            vm.ServerAET = "MOCKSCP";
            vm.CallingAET = "DCST4";
            vm.UseTLS = false;

            await CheckCapabilities(h, folder, check);
            await CheckTestsTabLayout(port, check);
        }

        private static ScpContextTable CapabilityScript()
        {
            var table = new ScpContextTable { Default = ScpAnswer.Reject(DicomPresentationContextResult.RejectTransferSyntaxesNotSupported) };
            table.Set(DicomUID.CTImageStorage, DicomTransferSyntax.ExplicitVRLittleEndian, ScpAnswer.Accept);
            table.Set(DicomUID.CTImageStorage, DicomTransferSyntax.ImplicitVRLittleEndian, ScpAnswer.Accept);
            table.Set(DicomUID.Verification, DicomTransferSyntax.ImplicitVRLittleEndian, ScpAnswer.Accept);
            return table;
        }

        private static async Task CheckCapabilities(Harness h, string folder, Action<string, bool, string> check)
        {
            var vm = h.Vm;
            var suite = vm.TestSuite;
            var capabilities = suite.Capabilities;

            check("Capabilities: empty until a probe has run", !capabilities.HasMatrix && capabilities.Rows.Count == 0 && capabilities.RemoteSummary.Contains("Tests tab"), capabilities.RemoteSummary);

            foreach (var choice in suite.Choices) choice.IsSelected = choice.Test.Id == "negotiation-probe";
            h.Messages.Clear();
            Scp.Reset();
            Scp.ContextTable = CapabilityScript();
            await h.Execute(vm.RunTestsCommand);

            check("Capabilities: the run went through the busy state", !vm.IsBusy && h.Messages.Count == 0 && suite.Rows.Count == 1 && suite.Rows[0].Result == TestResultClass.PASS, suite.Rows.Count > 0 ? suite.Rows[0].Explanation : string.Join("; ", h.Messages));
            check("Capabilities: one row per SOP class, one column per syntax", capabilities.HasMatrix && capabilities.Rows.Count == CapabilityCatalog.SopClasses.Count && capabilities.Columns.Count == CapabilityCatalog.TransferSyntaxes.Count && capabilities.Rows.All(r => r.Cells.Count == capabilities.Columns.Count), capabilities.Rows.Count.ToString());
            var ctRow = capabilities.Rows.Single(r => r.SopClassUid == DicomUID.CTImageStorage.UID);
            var explicitColumn = capabilities.Columns.Select((c, i) => new { c, i }).Single(x => x.c.ShortName == "Explicit LE").i;
            var rleColumn = capabilities.Columns.Select((c, i) => new { c, i }).Single(x => x.c.ShortName == "RLE").i;
            check("Capabilities: an accepted cell", ctRow.Cells[explicitColumn].Code == "✓" && ctRow.Cells[explicitColumn].ToolTip.Contains("Accepted") && ctRow.Cells[explicitColumn].ToolTip.Contains("presentation context"), ctRow.Cells[explicitColumn].ToolTip);
            check("Capabilities: a rejected cell has its own code", ctRow.Cells[rleColumn].Code == "T" && ctRow.Cells[rleColumn].ToolTip.Contains("TransferSyntaxNotSupported"), ctRow.Cells[rleColumn].ToolTip);
            check("Capabilities: the matrix files were saved with the run", suite.LastResultFiles.CapabilityJson != null && File.Exists(suite.LastResultFiles.CapabilityJson), null);
            check("Capabilities: the remote is described", capabilities.RemoteSummary.Contains("MOCKSCP") && capabilities.RemoteSummary.Contains("max PDU"), capabilities.RemoteSummary);

            // ------------------------------------------------ saving
            var jsonPath = Path.Combine(folder, "saved-matrix.json");
            var csvPath = Path.Combine(folder, "saved-matrix.csv");
            vm.SaveCapabilityMatrix(jsonPath, false);
            vm.SaveCapabilityMatrix(csvPath, true);
            check("Capabilities: saved as JSON and CSV", CapabilityMatrix.Load(jsonPath).ToJson() == capabilities.Matrix.ToJson() && File.ReadAllText(csvPath, Encoding.UTF8).StartsWith("sop_class_uid"), null);

            var blocked = Path.Combine(folder, "a-file-not-a-folder");
            File.WriteAllText(blocked, "x");
            h.Messages.Clear();
            vm.SaveCapabilityMatrix(Path.Combine(blocked, "matrix.json"), false);
            check("Capabilities: a save that cannot be done gives a message", h.Messages.Count == 1 && h.Messages[0].Contains("Could not save"), string.Join("; ", h.Messages));

            // ------------------------------------------------ claimed vs actual
            var claimedPath = Path.Combine(folder, "claimed.json");
            File.WriteAllText(claimedPath, "{ \"rows\": [ { \"sopClassUid\": \"" + DicomUID.CTImageStorage.UID + "\", \"cells\": [ "
                + "{ \"transferSyntaxUid\": \"" + DicomTransferSyntax.ExplicitVRLittleEndian.UID.UID + "\", \"result\": \"Accepted\" }, "
                + "{ \"transferSyntaxUid\": \"" + DicomTransferSyntax.RLELossless.UID.UID + "\", \"result\": \"Accepted\" } ] } ] }");
            h.Messages.Clear();
            vm.LoadClaimedCapabilities(claimedPath);
            ctRow = capabilities.Rows.Single(r => r.SopClassUid == DicomUID.CTImageStorage.UID);
            var implicitColumn = capabilities.Columns.Select((c, i) => new { c, i }).Single(x => x.c.ShortName == "Implicit").i;
            check("Claimed: no dialog, and the lists are filled", h.Messages.Count == 0 && capabilities.HasClaimed && capabilities.Differences.Count == 3, capabilities.Differences.Count.ToString());
            check("Claimed: a claimed but rejected cell is marked", ctRow.Cells[rleColumn].Code == "T!" && ctRow.Cells[rleColumn].ToolTip.Contains("Claimed"), ctRow.Cells[rleColumn].Code);
            check("Claimed: an accepted but not claimed cell is marked", ctRow.Cells[implicitColumn].Code == "✓+", ctRow.Cells[implicitColumn].Code);
            check("Claimed: the summary counts them", capabilities.ComparisonSummary.Contains("1 claimed but rejected") && capabilities.ComparisonSummary.Contains("2 accepted but not claimed"), capabilities.ComparisonSummary);

            capabilities.SetClaimed(null);
            ctRow = capabilities.Rows.Single(r => r.SopClassUid == DicomUID.CTImageStorage.UID);
            check("Claimed: clearing removes the marks", !capabilities.HasClaimed && capabilities.Differences.Count == 0 && ctRow.Cells[rleColumn].Code == "T" && capabilities.ComparisonSummary.Length == 0, ctRow.Cells[rleColumn].Code);

            var broken = Path.Combine(folder, "broken-claim.json");
            File.WriteAllText(broken, "{ \"rows\": ");
            h.Messages.Clear();
            vm.LoadClaimedCapabilities(broken);
            check("Claimed: a broken file gives a message and changes nothing", h.Messages.Count == 1 && h.Messages[0].Contains("not valid") && !capabilities.HasClaimed, string.Join("; ", h.Messages));

            // ------------------------------------------------ another run keeps the claims
            vm.LoadClaimedCapabilities(claimedPath);
            Scp.Reset();
            Scp.ContextTable = CapabilityScript();
            h.Messages.Clear();
            await h.Execute(vm.RunTestsCommand);
            check("Claimed: a new run is compared with the same claims", capabilities.HasClaimed && capabilities.Differences.Count == 3, capabilities.Differences.Count.ToString());
            capabilities.SetClaimed(null);
            Scp.Reset();
            foreach (var choice in suite.Choices) choice.IsSelected = true;
        }

        private sealed class BindingErrorListener : TraceListener
        {
            public readonly List<string> Errors = new List<string>();
            public override void Write(string message) { }
            public override void WriteLine(string message) => Errors.Add(message);
        }

        /// <summary>
        /// Lays out the real main window content with the Tests tab open, without showing the window: closing it would save the
        /// settings, and showing it would start the update check. If DCST4_TESTS_TAB_PNG is set, the result is written there as a picture.
        /// </summary>
        private static async Task CheckTestsTabLayout(int port, Action<string, bool, string> check)
        {
            var listener = new BindingErrorListener();
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);

            try
            {
                var window = new MainWindow();
                var vm = (MainViewModel)window.DataContext;
                vm.ShowMessage = (message, icon, title) => { };
                vm.SaveSettings = () => { };
                vm.ServerHost = "127.0.0.1";
                vm.ServerPort = port;
                vm.ServerAET = "MOCKSCP";
                vm.CallingAET = "DCST4";
                vm.ProfileName = string.Empty;
                vm.TestSuite.ResultsFolder = @"C:\Temp\DCST4 results";

                var content = (FrameworkElement)window.Content;
                window.Content = null;
                content.DataContext = vm;

                var tabs = LogicalTreeHelper.GetChildren(content).OfType<Grid>().SelectMany(g => g.Children.OfType<TabControl>()).Single(t => t.Items.OfType<TabItem>().Any(i => (string)i.Header == "Log"));
                var testsTab = tabs.Items.OfType<TabItem>().Single(i => (string)i.Header == "Tests");
                tabs.SelectedItem = testsTab;

                Scp.Reset();
                vm.TestSuite.Rows.Clear();
                vm.TestSuite.Rows.Add(new TestResultRow(new TestResult
                {
                    TestId = "c-echo",
                    Name = "C-ECHO (Verification)",
                    Category = TestCategory.Capability,
                    Result = TestResultClass.PASS,
                    DurationMs = 12,
                    Explanation = "127.0.0.1:" + port + " answered C-ECHO with status 0x0000 (Success).",
                    StatusCodes = { new DimseStatusRecord { Operation = "C-ECHO", Code = "0x0000" } },
                    Timeline =
                    {
                        new ProtocolEvent { OffsetMs = 0, Direction = ProtocolEvent.Sent, Type = "A-ASSOCIATE-RQ", Detail = "Calling AE DCST4, called AE MOCKSCP" },
                        new ProtocolEvent { OffsetMs = 8, Direction = ProtocolEvent.Received, Type = "A-ASSOCIATE-AC", Detail = "Max PDU 262144" },
                        new ProtocolEvent { OffsetMs = 9, Direction = ProtocolEvent.Sent, Type = "C-ECHO-RQ", Detail = "Verification SOP Class" },
                        new ProtocolEvent { OffsetMs = 11, Direction = ProtocolEvent.Received, Type = "C-ECHO-RSP", Status = "0x0000", Detail = "Success" },
                    },
                }));
                vm.TestSuite.SelectedRow = vm.TestSuite.Rows[0];

                const int width = 1150, height = 820;
                content.Measure(new Size(width, height));
                content.Arrange(new Rect(0, 0, width, height));
                content.UpdateLayout();
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => content.UpdateLayout(), DispatcherPriority.ApplicationIdle);

                check("Tests tab: is laid out", testsTab.IsSelected && content.ActualWidth > 0, null);
                var problems = listener.Errors.Where(e => e.Contains("TestSuite") || e.Contains("Timeline") || e.Contains("Explanation") || e.Contains("Choices")
                    || e.Contains("TestChoice") || e.Contains("TestGroupChoice") || e.Contains("Groups") || e.Contains("AllSelected") || e.Contains("SelectionText")).ToList();
                check("Tests tab: every binding resolves", problems.Count == 0, string.Join("\n", problems));
                var expanders = VisualDescendants(content).OfType<Expander>().ToList();
                check("Tests tab: one expander per group, drawn", expanders.Count == vm.TestSuite.Groups.Count && expanders.All(e => e.ActualHeight > 0), expanders.Count.ToString());

                var picture = Environment.GetEnvironmentVariable("DCST4_TESTS_TAB_PNG");
                if (!string.IsNullOrEmpty(picture))
                {
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var stream = File.Create(picture)) encoder.Save(stream);
                }

                // ------------------------------------------------ every setting explains itself on hover
                var bare = WithoutToolTip(content);
                check("Main window: every setting has a tooltip", bare.Count == 0, string.Join("; ", bare));
                bare = WithoutToolTip((DependencyObject)new SettingsDialog().Content);
                check("File > Settings: every setting has a tooltip", bare.Count == 0, string.Join("; ", bare));
                bare = WithoutToolTip((DependencyObject)new TlsOptionsDialog(false, string.Empty).Content);
                check("TLS options: every setting has a tooltip", bare.Count == 0, string.Join("; ", bare));

                // Every setting names its default; the fields that describe one added tag are input, not settings.
                var tagEditor = new[] { "TagBox", "CreatorBox", "ValueBox" };
                var noDefault = new[] { (DependencyObject)new SettingsDialog().Content, new TestSettingsDialog(new TestSettingsViewModel(vm)).Content as DependencyObject, new TlsOptionsDialog(false, string.Empty).Content as DependencyObject }
                    .SelectMany(LogicalDescendants).OfType<Control>()
                    .Where(c => (c is TextBox t && !t.IsReadOnly) || c is ComboBox || c is CheckBox)
                    .Where(c => !tagEditor.Contains(c.Name) && !(ToolTipService.GetToolTip(c) as string ?? string.Empty).Contains("\n\nDefault: "))
                    .Select(c => c.Name).ToList();
                check("Dialogs: every setting's tooltip ends with its default", noDefault.Count == 0, string.Join(", ", noDefault));
                var mainNoDefault = LogicalDescendants(content).OfType<Control>()
                    .Where(c => ((c is TextBox t && !t.IsReadOnly) || c is ComboBox || c is CheckBox) && ToolTipService.GetToolTip(c) is string)
                    .Where(c => !((string)ToolTipService.GetToolTip(c)).Contains("\n\nDefault: ") && !(c is CheckBox box && (string)box.Content == "Check all"))
                    .Select(c => c.Name.Length > 0 ? c.Name : System.Windows.Data.BindingOperations.GetBindingExpression(c, c is TextBox ? TextBox.TextProperty : c is ComboBox ? System.Windows.Controls.Primitives.Selector.SelectedItemProperty : System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.ParentBinding.Path.Path ?? (c as ContentControl)?.Content?.ToString())
                    .ToList();
                check("Main window: every setting's tooltip ends with its default", mainNoDefault.Count == 0, string.Join(", ", mainNoDefault));

                // ------------------------------------------------ the question before a run keeps every line whole
                var longLine = "- C-STORE: Grayscale Softcopy Presentation State: 1 examination, 2 objects, about 36 KB. And a little more so that it is long";
                var confirm = new ConfirmDialog("21 of the ticked tests store new test objects on MOCKSCP at 127.0.0.1:104:\n\n" + longLine + "\n\nStart the tests?");
                var confirmContent = (FrameworkElement)confirm.Content;
                confirm.Content = null;
                confirmContent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                confirmContent.Arrange(new Rect(confirmContent.DesiredSize));
                var messageText = (TextBlock)confirm.FindName("MessageText");
                var lineWidth = new FormattedText(longLine, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(messageText.FontFamily, messageText.FontStyle, messageText.FontWeight, messageText.FontStretch), messageText.FontSize, Brushes.Black, 1.0).Width;
                check("Question: lines are not wrapped, and the window is as wide as the longest", messageText.TextWrapping == TextWrapping.NoWrap && messageText.ActualWidth >= lineWidth - 1 && confirmContent.DesiredSize.Width > lineWidth,
                    $"{messageText.ActualWidth:0} {lineWidth:0} {confirmContent.DesiredSize.Width:0}");
                if (!string.IsNullOrEmpty(picture))
                {
                    var confirmBitmap = new RenderTargetBitmap((int)Math.Ceiling(confirmContent.DesiredSize.Width), (int)Math.Ceiling(confirmContent.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
                    confirmBitmap.Render(confirmContent);
                    var confirmEncoder = new PngBitmapEncoder();
                    confirmEncoder.Frames.Add(BitmapFrame.Create(confirmBitmap));
                    using (var stream = File.Create(Path.ChangeExtension(picture, ".confirm.png"))) confirmEncoder.Save(stream);
                }

                var testSettings = new TestSettingsDialog(new TestSettingsViewModel(vm));
                var dialogContent = (FrameworkElement)testSettings.Content;
                testSettings.Content = null;
                dialogContent.DataContext = testSettings.DataContext;
                dialogContent.Measure(new Size(640, 800));
                dialogContent.Arrange(new Rect(0, 0, 640, dialogContent.DesiredSize.Height));
                dialogContent.UpdateLayout();
                bare = WithoutToolTip(dialogContent);
                check("Test settings: every setting has a tooltip", bare.Count == 0, string.Join("; ", bare));
                var dialogProblems = listener.Errors.Where(e => e.Contains("TestSettingsViewModel")).ToList();
                var boxes = LogicalDescendants(dialogContent).OfType<TextBox>().ToList();
                if (!string.IsNullOrEmpty(picture))
                {
                    var size = dialogContent.DesiredSize;
                    var dialogBitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
                    dialogContent.Arrange(new Rect(size));
                    dialogBitmap.Render(dialogContent);
                    var dialogEncoder = new PngBitmapEncoder();
                    dialogEncoder.Frames.Add(BitmapFrame.Create(dialogBitmap));
                    using (var stream = File.Create(Path.ChangeExtension(picture, ".settings.png"))) dialogEncoder.Save(stream);
                }
                check("Test settings: every binding resolves and every box shows its value", dialogProblems.Count == 0 && boxes.Count == 10 && boxes.Where(b => b.Name != "ExpiredCertBox").All(b => b.Text.Length > 0),
                    string.Join("\n", dialogProblems) + " " + boxes.Count);

                // ------------------------------------------------ the Capabilities tab, with a real probe in it
                Scp.Reset();
                Scp.ContextTable = CapabilityScript();
                var profile = TestSuiteTests.MockProfile(port);
                var outcome = await NegotiationProbe.RunAsync(profile, null, CancellationToken.None);
                Scp.Reset();
                vm.TestSuite.Capabilities.Show(outcome.Matrix);
                var claimed = new CapabilityMatrix();
                claimed.Rows.Add(new MatrixRow { SopClassUid = DicomUID.CTImageStorage.UID, Cells = { new MatrixCell { TransferSyntaxUid = DicomTransferSyntax.RLELossless.UID.UID, Result = CapabilityResult.Accepted } } });
                vm.TestSuite.Capabilities.SetClaimed(claimed);

                var capabilitiesTab = tabs.Items.OfType<TabItem>().Single(i => (string)i.Header == "Capabilities");
                tabs.SelectedItem = capabilitiesTab;
                content.Measure(new Size(width, height));
                content.Arrange(new Rect(0, 0, width, height));
                content.UpdateLayout();
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => content.UpdateLayout(), DispatcherPriority.ApplicationIdle);

                var grid = (DataGrid)window.FindName("CapabilityGrid");
                check("Capabilities tab: is laid out with one column per syntax", capabilitiesTab.IsSelected && grid.Columns.Count == CapabilityCatalog.TransferSyntaxes.Count + 1 && grid.ActualHeight > 0, grid.Columns.Count.ToString());
                var capabilityProblems = listener.Errors.Where(e => e.Contains("Capabilities") || e.Contains("Cells[") || e.Contains("SopClass") || e.Contains("Differences")).ToList();
                check("Capabilities tab: every binding resolves", capabilityProblems.Count == 0, string.Join("\n", capabilityProblems));

                var capabilityPicture = Environment.GetEnvironmentVariable("DCST4_CAPABILITIES_TAB_PNG");
                if (!string.IsNullOrEmpty(capabilityPicture))
                {
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var stream = File.Create(capabilityPicture)) encoder.Save(stream);
                }
            }
            finally
            {
                PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            }
        }
    }
}
