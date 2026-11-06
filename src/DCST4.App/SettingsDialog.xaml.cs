using DCST4.Core.Generation;
using DCST4.Models;
using DCST4.Properties;
using FellowOakDicom;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace DCST4
{
    public partial class SettingsDialog : Window
    {
        /// <summary>A row in the checklist of common tags.</summary>
        private sealed class CommonTagChoice
        {
            public CommonTag Common { get; set; }
            public bool IsChecked { get; set; }
            public string Name => Common.Name;
            public string Number => Common.Tag.Number + " " + Common.Tag.VrCode;
        }

        /// <summary>A row in the checklist of tags to burn in.</summary>
        private sealed class BurnInTagChoice
        {
            public DicomTag Tag { get; set; }
            public bool IsChecked { get; set; }
            public string Name => Tag.DictionaryEntry.Name;
            public string Number => Tag.ToString();
        }

        private readonly List<CommonTagChoice> _commonTags;
        private readonly List<BurnInTagChoice> _burnInTags;
        private readonly ObservableCollection<AdditionalTag> _customTags = new ObservableCollection<AdditionalTag>();

        public SettingsDialog()
        {
            InitializeComponent();
            var s = Settings.Default;
            ConnectionTimeoutBox.Text = s.ConnectionTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            AssociationTimeoutBox.Text = s.AssociationTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            ResponseTimeoutBox.Text = s.ResponseTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            LogDimseDatasets.IsChecked = s.Client_LogDimseDatasets;
            LogDataPDUs.IsChecked = s.Client_LogDataPDUs;
            NewValuesAfterRun.IsChecked = s.NewValuesAfterRun;
            BurnInText.IsChecked = s.BurnInText;

            var burnedIn = BurnedInTagCatalog.Parse(s.BurnInTags);
            _burnInTags = BurnedInTagCatalog.All.Select(t => new BurnInTagChoice { Tag = t, IsChecked = burnedIn.Contains(t) }).ToList();
            BurnInTagsList.ItemsSource = _burnInTags;

            var saved = ReadSavedTags(s.AdditionalTags);
            _commonTags = CommonTagCatalog.All
                .Select(c => new CommonTagChoice { Common = c, IsChecked = saved.Any(t => t.Key == c.Tag.Key) })
                .ToList();
            foreach (var tag in saved.Where(t => CommonTagCatalog.Find(t) == null)) _customTags.Add(tag);

            CommonTagsList.ItemsSource = _commonTags;
            CustomTagsGrid.ItemsSource = _customTags;
            VrBox.ItemsSource = AdditionalTagRules.TextVrs;
            VrBox.SelectedItem = "LO";
        }

        private static List<AdditionalTag> ReadSavedTags(string xml)
        {
            try
            {
                return AdditionalTagSetting.Deserialize(xml).Select(t => t.ToTag()).Where(t => t != null).ToList();
            }
            catch (InvalidOperationException ex)
            {
                Log.ForContext<SettingsDialog>().Warning(ex, "Could not read the saved additional tags, they are ignored");
                return new List<AdditionalTag>();
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryRead(ConnectionTimeoutBox, "Connection timeout", 1, 3600, out var connection)) return;
            if (!TryRead(AssociationTimeoutBox, "Association timeout", 1, 3600, out var association)) return;
            if (!TryRead(ResponseTimeoutBox, "Response timeout", 0, 86400, out var response)) return;

            var s = Settings.Default;
            s.ConnectionTimeoutSeconds = connection;
            s.AssociationTimeoutSeconds = association;
            s.ResponseTimeoutSeconds = response;
            s.Client_LogDimseDatasets = LogDimseDatasets.IsChecked == true;
            s.Client_LogDataPDUs = LogDataPDUs.IsChecked == true;
            s.NewValuesAfterRun = NewValuesAfterRun.IsChecked == true;
            s.BurnInText = BurnInText.IsChecked == true;
            s.BurnInTags = BurnedInTagCatalog.Format(_burnInTags.Where(c => c.IsChecked).Select(c => c.Tag));
            s.AdditionalTags = AdditionalTagSetting.Serialize(
                _commonTags.Where(c => c.IsChecked).Select(c => c.Common.Tag).Concat(_customTags).Select(AdditionalTagSetting.From));
            s.Save();
            DialogResult = true;
        }

        private void TagBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!AdditionalTagParser.TryResolve(TagBox.Text, out var group, out var element))
            {
                TagNameText.Text = string.Empty;
                CreatorBox.IsEnabled = false;
                VrBox.IsEnabled = true;
                return;
            }

            var isPrivate = (group & 1) == 1;
            CreatorBox.IsEnabled = isPrivate;
            var entry = AdditionalTagParser.DictionaryEntry(group, element);
            TagNameText.Text = isPrivate ? "Private tag" : entry == null ? "Not in the DICOM dictionary" : entry.Name;

            var known = AdditionalTagParser.KnownVrs(group, element);
            VrBox.IsEnabled = known.Count != 1;
            if (known.Count > 0 && !known.Contains(VrBox.SelectedItem as string)) VrBox.SelectedItem = known[0];
        }

        private void AddTagButton_Click(object sender, RoutedEventArgs e)
        {
            var tag = AdditionalTagParser.Create(TagBox.Text, VrBox.SelectedItem as string, CreatorBox.Text, ValueBox.Text, out var problem);
            if (tag != null && CommonTagCatalog.Find(tag) != null)
            {
                problem = $"{tag.Name} is in the list of common tags. Tick it there.";
            }

            var existing = tag == null ? null : _customTags.FirstOrDefault(t => t.Number == tag.Number);
            if (problem == null)
            {
                problem = AdditionalTagRules.CheckCombination(_customTags.Where(t => t != existing).Concat(new[] { tag }));
            }
            if (problem != null)
            {
                MessageBox.Show(this, problem, "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                TagBox.Focus();
                return;
            }

            if (existing == null)
            {
                _customTags.Add(tag);
            }
            else
            {
                _customTags[_customTags.IndexOf(existing)] = tag;
            }
            TagBox.Clear();
            CreatorBox.Clear();
            ValueBox.Clear();
            // Otherwise the next private tag silently takes the VR the dictionary gave this one.
            VrBox.SelectedItem = "LO";
            TagBox.Focus();
        }

        private void RemoveTagButton_Click(object sender, RoutedEventArgs e)
        {
            if (CustomTagsGrid.SelectedItem is AdditionalTag tag) _customTags.Remove(tag);
        }

        /// <summary>Puts the selected tag in the boxes, so it can be changed and added again.</summary>
        private void CustomTagsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(CustomTagsGrid.SelectedItem is AdditionalTag tag)) return;
            TagBox.Text = tag.Number;
            VrBox.SelectedItem = tag.VrCode;
            CreatorBox.Text = tag.PrivateCreator ?? string.Empty;
            ValueBox.Text = tag.DefaultValue;
        }

        private bool TryRead(TextBox box, string name, int min, int max, out int value)
        {
            if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min && value <= max)
            {
                return true;
            }
            MessageBox.Show(this, $"{name} must be a whole number between {min} and {max}.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            box.Focus();
            return false;
        }
    }
}
