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
                        await RunTestSuite(port, closedPort, temp, check);
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
        private static async Task RunTestSuite(int port, int closedPort, string temp, Action<string, bool, string> check)
        {
            Console.WriteLine("\nTests tab");
            var h = new Harness(port);
            var vm = h.Vm;
            var suite = vm.TestSuite;
            var folder = Path.Combine(temp, "tab");
            suite.ResultsFolder = Path.Combine(folder, "app");
            vm.ProfileName = "Comparison";

            check("The list holds the catalog, ticked", suite.Choices.Count == TestCatalog.All.Count && suite.Choices.All(c => c.IsSelected), suite.Choices.Count.ToString());

            // ------------------------------------------------ the same content as the command line
            Scp.Reset();
            await h.Execute(vm.RunTestsCommand);
            check("Run: no dialog", h.Messages.Count == 0, string.Join("; ", h.Messages));
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

            await CheckTestsTabLayout(port, check);
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
                var problems = listener.Errors.Where(e => e.Contains("TestSuite") || e.Contains("Timeline") || e.Contains("Explanation") || e.Contains("Choices")).ToList();
                check("Tests tab: every binding resolves", problems.Count == 0, string.Join("\n", problems));

                var picture = Environment.GetEnvironmentVariable("DCST4_TESTS_TAB_PNG");
                if (!string.IsNullOrEmpty(picture))
                {
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var stream = File.Create(picture)) encoder.Save(stream);
                }
            }
            finally
            {
                PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            }
        }
    }
}
