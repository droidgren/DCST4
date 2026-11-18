using DCST4.Core.Pipeline;
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
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DCST4.Tests
{
    /// <summary>
    /// Retakes the README pictures of the app: DCST4.Tests.exe screenshots &lt;folder&gt;. Not part of the test run.
    /// The real windows are shown on screen against the mock SCP and copied from the screen, title bar included, so the
    /// screen must be unlocked and nothing may cover them. Settings stay in memory: nothing here saves them, the main
    /// window is never closed (closing saves), and user.config is compared with a copy taken first and put back if it
    /// changed. Every field shows 127.0.0.1, MOCKSCP and C:\Temp\DCST4-results, so no host or user name ends up in a picture.
    /// </summary>
    internal static class Screenshots
    {
        private const int ScpPort = 11114;
        private const int ListenPort = 11112;
        private const string ResultsFolder = @"C:\Temp\DCST4-results";

        public static int Run(string folder)
        {
            SetProcessDPIAware();
            folder = Path.GetFullPath(folder);
            Directory.CreateDirectory(folder);

            var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DCST4", "user.config");
            var configBefore = File.Exists(config) ? File.ReadAllBytes(config) : null;
            var resultsExisted = Directory.Exists(ResultsFolder);

            Exception failure = null;
            try
            {
                Scp.Reset();
                using (DicomServerFactory.Create<MockStorageScp>(ScpPort))
                {
                    var thread = new Thread(() =>
                    {
                        var dispatcher = Dispatcher.CurrentDispatcher;
                        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                        dispatcher.InvokeAsync(async () =>
                        {
                            try { await TakeAll(folder); }
                            catch (Exception ex) { failure = ex; }
                            finally { dispatcher.InvokeShutdown(); }
                        });
                        Dispatcher.Run();
                    });
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.Start();
                    thread.Join();
                }
            }
            finally
            {
                var configAfter = File.Exists(config) ? File.ReadAllBytes(config) : null;
                var unchanged = configBefore == null ? configAfter == null : configAfter != null && configBefore.SequenceEqual(configAfter);
                if (!unchanged)
                {
                    if (configBefore != null) File.WriteAllBytes(config, configBefore);
                    else File.Delete(config);
                    Console.WriteLine("WARNING: user.config had changed and was put back as it was.");
                }
                if (!resultsExisted && Directory.Exists(ResultsFolder)) Directory.Delete(ResultsFolder, true);
            }

            if (failure != null)
            {
                Console.WriteLine(failure);
                return 1;
            }
            Console.WriteLine("Pictures written to " + folder);
            return 0;
        }

        private static async Task TakeAll(string folder)
        {
            // The settings class is internal to the app. Only changed in memory.
            var settings = (System.Configuration.ApplicationSettingsBase)typeof(MainWindow).Assembly.GetType("DCST4.Properties.Settings")
                .GetProperty("Default", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).GetValue(null);
            settings["ShowUpdateWindowAtStartup"] = false;

            var window = new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = 40, Top = 40, Width = 1240, Height = 800, Topmost = true };
            var vm = (MainViewModel)window.DataContext;
            vm.ShowMessage = (message, icon, title) => Console.WriteLine("Message: " + message);
            vm.SaveSettings = () => { };
            vm.OpenDocument = path => { };

            // Every setting at its default, in memory, so the pictures show what a new user sees.
            vm.Confirm = question => true;
            vm.ResetSettings();
            settings["ShowUpdateWindowAtStartup"] = false;
            vm.ProfileName = string.Empty;
            vm.SelectedProfile = null;
            vm.CallingAET = "DCST4";
            vm.ServerAET = "MOCKSCP";
            vm.ServerHost = "127.0.0.1";
            vm.ServerPort = ScpPort;
            vm.UseTLS = false;
            vm.LocalScpAe = "DCST4";
            vm.LocalScpPort = ListenPort;
            vm.SendImages = true;
            vm.SaveImages = false;
            vm.SaveFolder = string.Empty;
            vm.AllowFallback = false;
            vm.PrivatePayloadMb = 0;
            vm.BurnInText = false;
            vm.NewValuesAfterRun = false;
            vm.MinAge = 0;
            vm.MaxAge = 100;
            vm.CharacterSet = Core.Generation.DicomCharacterSets.Latin1;
            vm.AdditionalTagSettings = new[]
            {
                new AdditionalTagSetting { Tag = "(0008,1070)", Vr = "PN" },
                new AdditionalTagSetting { Tag = "(0008,1040)", Vr = "LO" },
                new AdditionalTagSetting { Tag = "(0038,0010)", Vr = "LO" },
                new AdditionalTagSetting { Tag = "(0010,2180)", Vr = "SH", Value = "Test pilot" },
                new AdditionalTagSetting { Tag = "(0011,1001)", Vr = "LO", PrivateCreator = "DCST4", Value = "Private text" },
            };
            vm.TestSuite.ResultsFolder = ResultsFolder;
            vm.SelectedModality = Core.Generation.Modalities.ModalityCatalog.Find("CT");
            vm.SelectedTransferSyntax = TransferSyntaxOption.ExplicitVrLe;
            vm.SeriesCount = 1;
            vm.ImagesPerSeries = 15;

            window.Show();
            window.Activate();
            await Settle();

            // ------------------------------------------------ dialogs, with the defaults
            await ShowAndCapture(new TestSettingsDialog(new TestSettingsViewModel(vm)), window, Path.Combine(folder, "test-settings.png"));
            await ShowAndCapture(new PerformanceSettingsDialog(new PerformanceSettingsViewModel(vm.Performance)), window, Path.Combine(folder, "performance-settings.png"));
            var settingsDialog = new SettingsDialog(new ResultSettingsViewModel(vm));
            await ShowAndCapture(settingsDialog, window, Path.Combine(folder, "settings-tags.png"), () => Select(settingsDialog, "Additional tags"));
            await ShowAndCapture(settingsDialog, window, Path.Combine(folder, "settings-results.png"), () => Select(settingsDialog, "Test and Performance Results"));
            await ShowAndCapture(settingsDialog, window, Path.Combine(folder, "settings-identifiers.png"), () => Select(settingsDialog, "Identifiers"));

            // The tags preview of the exam above, in its own window.
            Core.Pipeline.TagPreviewResult tags = null;
            vm.ShowTagPreview = result => tags = result;
            await Execute(vm, vm.TagPreviewCommand);
            if (tags == null) throw new InvalidOperationException("The tags preview was not made: " + vm.StatusText);
            var tagWindow = new TagPreviewWindow { Width = 1000, Height = 700 };
            tagWindow.SetResult(tags);
            await ShowAndCapture(tagWindow, window, Path.Combine(folder, "tags-preview.png"));

            // Shorter Storage Commitment waits, since the mock answers at once. Not in any picture.
            vm.CommitLingerSeconds = 0;
            vm.CommitWaitSeconds = 5;
            vm.CommitGraceSeconds = 1;

            // ------------------------------------------------ a run with a few warnings and failures
            Scp.Reset();
            Scp.StatusFor = i => i == 4 || i == 9 || i == 14 ? DicomStatus.StorageCoercionOfDataElements
                : i == 6 || i == 13 ? DicomStatus.StorageStorageOutOfResources : null;
            Scp.ErrorCommentFor = i => i == 4 || i == 9 || i == 14 ? "Mock SCP: simulated coercion" : i == 6 || i == 13 ? "Mock SCP: simulated full disk" : null;
            Scp.OffendingElement = new Scp.Tag { Value = DicomTag.PatientName };
            Select(window, "Exam");
            Select(window, "Results");
            await Execute(vm, vm.RunCommand);
            Capture(window, Path.Combine(folder, "main.png"));

            Select(window, "Identifiers");
            await Settle();
            Capture(window, Path.Combine(folder, "identifiers.png"));

            // ------------------------------------------------ the tests, the chain and the capabilities
            Scp.Reset();
            Scp.KeepStored = true;
            Scp.MoveDestinations["DCST4"] = Tuple.Create("127.0.0.1", ListenPort);
            Scp.Commit = CommitMode.NewAssociation;
            Scp.CommitDestinations["DCST4"] = Tuple.Create("127.0.0.1", ListenPort, "DCST4");
            foreach (var choice in vm.TestSuite.Choices) choice.IsSelected = choice.Test.Category != TestCategory.Limits;
            vm.Confirm = question =>
            {
                var confirm = new ConfirmDialog(question) { Owner = window, Icon = window.Icon, WindowStartupLocation = WindowStartupLocation.CenterOwner, Topmost = true };
                confirm.Show();
                Pump();
                Capture(confirm, Path.Combine(folder, "confirm.png"));
                confirm.Hide();
                return true;
            };
            Select(window, "Tests");
            await Execute(vm, vm.RunTestsCommand, 600);
            vm.TestSuite.SelectedRow = vm.TestSuite.Rows.FirstOrDefault();
            await Settle();
            Capture(window, Path.Combine(folder, "tests.png"));

            Select(window, "Chain");
            vm.TestSuite.Chain.SelectedRow = vm.TestSuite.Chain.Rows.FirstOrDefault();
            await Settle();
            Capture(window, Path.Combine(folder, "chain.png"));

            Select(window, "Capabilities");
            await Settle();
            Capture(window, Path.Combine(folder, "capabilities.png"));

            // ------------------------------------------------ a short load test
            // At a set rate, so that the run lasts its 20 s and the chart has something to show, and kept, so that
            // time-to-available finds what it looks for.
            Scp.Reset();
            Scp.RecordReceived = false;
            Scp.ReceiveInMemory = true;
            Scp.KeepStored = true;
            var p = vm.Performance;
            p.SelectedMode = p.Modes.First(m => m.Mode == Core.Performance.LoadMode.Load);
            p.SelectedMix = Core.Performance.DataMixCatalog.Find("ct");
            p.RatePerSecond = 50;
            vm.Confirm = question => true;
            Select(window, "Performance");
            await Execute(vm, vm.RunPerformanceCommand, 180);
            Capture(window, Path.Combine(folder, "performance.png"));

            // ------------------------------------------------ the new help topic
            var help = new HelpWindow { Owner = window, Icon = window.Icon, WindowStartupLocation = WindowStartupLocation.Manual, Left = window.Left + 60, Top = window.Top + 40, Width = 1000, Height = 720, Topmost = true };
            help.Show();
            await Settle();
            help.ShowTopic("Transfer syntaxes");
            await Settle();
            Capture(help, Path.Combine(folder, "help-transfer-syntaxes.png"));
            help.Hide();

            window.Hide();
            Scp.Reset();
        }

        private static async Task ShowAndCapture(Window dialog, Window owner, string path, Action before = null)
        {
            dialog.Owner = owner;
            // The app's windows take the icon of DCST4.exe, which this process does not have.
            dialog.Icon = owner.Icon;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dialog.Topmost = true;
            dialog.Show();
            before?.Invoke();
            await Settle();
            Capture(dialog, path);
            // Hidden, not closed, so that no OK or Closing handler can write anything.
            dialog.Hide();
        }

        private static async Task Execute(MainViewModel vm, System.Windows.Input.ICommand command, int timeoutSeconds = 60)
        {
            command.Execute(null);
            var sw = Stopwatch.StartNew();
            while (vm.IsBusy && sw.Elapsed < TimeSpan.FromSeconds(timeoutSeconds)) await Task.Delay(50);
            if (vm.IsBusy) throw new TimeoutException("The run did not finish within " + timeoutSeconds + " s.");
            await Settle();
        }

        private static async Task Settle()
        {
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(400);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        /// <summary>Lets a window appear while the caller cannot await, as inside the Confirm callback.</summary>
        private static void Pump()
        {
            var end = DateTime.UtcNow.AddMilliseconds(500);
            while (DateTime.UtcNow < end)
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Thread.Sleep(20);
            }
        }

        private static void Select(DependencyObject root, string header)
        {
            var tab = Descendants(root).OfType<TabItem>().FirstOrDefault(t => t.Header as string == header);
            if (tab == null) throw new InvalidOperationException("No tab named " + header);
            tab.IsSelected = true;
            tab.UpdateLayout();
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                yield return node;
                foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) stack.Push(child);
            }
        }

        /// <summary>Copies the window from the screen, frame and title bar included, without the invisible resize border.</summary>
        private static void Capture(Window window, string path)
        {
            window.Activate();
            window.UpdateLayout();
            var hwnd = new WindowInteropHelper(window).Handle;
            if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var r, Marshal.SizeOf(typeof(Rect))) != 0) GetWindowRect(hwnd, out r);
            using (var bitmap = new System.Drawing.Bitmap(r.Right - r.Left, r.Bottom - r.Top))
            {
                using (var g = System.Drawing.Graphics.FromImage(bitmap)) g.CopyFromScreen(r.Left, r.Top, 0, 0, bitmap.Size);
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            Console.WriteLine("  " + Path.GetFileName(path) + "  " + (r.Right - r.Left) + " x " + (r.Bottom - r.Top));
        }

        private const int DwmwaExtendedFrameBounds = 9;

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
    }
}
