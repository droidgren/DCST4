using DCST4.Core.Pipeline;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace DCST4
{
    /// <summary>Shows the tags preview of the Exam tab. One window is reused, so previews do not pile up.</summary>
    public partial class TagPreviewWindow : Window
    {
        /// <summary>The tag of an item in a sequence, shown in the Tag column of an item row.</summary>
        private const string ItemTag = "(FFFE,E000)";

        private static TagPreviewWindow _open;
        private List<TagRowView> _all = new List<TagRowView>();

        public TagPreviewWindow()
        {
            InitializeComponent();
            Closed += (s, e) => { if (ReferenceEquals(_open, this)) _open = null; };
        }

        /// <summary>Shows <paramref name="result"/> in the open tags window, or in a new one.</summary>
        public static void ShowTags(TagPreviewResult result, Window owner)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            var window = _open;
            if (window == null)
            {
                window = new TagPreviewWindow();
                if (owner != null && owner.IsLoaded) window.Owner = owner;
                _open = window;
            }
            window.SetResult(result);
            if (!window.IsVisible) window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }

        public void SetResult(TagPreviewResult result)
        {
            DescriptionText.Text = result.Description;
            _all = Views(result.Rows);
            ApplyFilter();
        }

        /// <summary>The rows the grid shows now, after the filter.</summary>
        public IReadOnlyList<TagRowView> ShownRows => (IReadOnlyList<TagRowView>)TagsGrid.ItemsSource ?? new List<TagRowView>();

        public string Filter
        {
            get => FilterBox.Text;
            set => FilterBox.Text = value;
        }

        /// <summary>Each row with the rows it is inside: its sequence and item, and theirs.</summary>
        private static List<TagRowView> Views(IReadOnlyList<TagPreviewRow> rows)
        {
            var views = new List<TagRowView>();
            var path = new List<TagRowView>();
            foreach (var row in rows)
            {
                while (path.Count > 0 && path[path.Count - 1].Row.Depth >= row.Depth) path.RemoveAt(path.Count - 1);
                var view = new TagRowView(row, path.ToList());
                views.Add(view);
                path.Add(view);
            }
            return views;
        }

        private void ApplyFilter()
        {
            var filter = FilterBox.Text?.Trim();
            if (string.IsNullOrEmpty(filter))
            {
                TagsGrid.ItemsSource = _all;
                return;
            }

            // A match inside a sequence keeps the sequence and item above it, so it can be told where it is.
            var keep = new HashSet<TagRowView>();
            foreach (var view in _all.Where(v => v.Matches(filter)))
            {
                keep.Add(view);
                foreach (var parent in view.Parents) keep.Add(parent);
            }
            TagsGrid.ItemsSource = _all.Where(keep.Contains).ToList();
        }

        /// <summary>Tab-separated, one row per line, with the tag indented as in the grid.</summary>
        public string CopyText()
        {
            var text = new StringBuilder();
            text.AppendLine("Tag\tVR\tName\tValue");
            foreach (var view in ShownRows) text.Append(view.IndentedTag).Append('\t').Append(view.Vr).Append('\t').Append(view.Name).Append('\t').AppendLine(view.Value);
            return text.ToString();
        }

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(CopyText());
            }
            catch (COMException ex)
            {
                // Another program can hold the clipboard for a moment.
                MessageBox.Show(this, "The tags could not be copied: " + ex.Message, "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>One row of the grid.</summary>
        public sealed class TagRowView
        {
            public TagRowView(TagPreviewRow row, IReadOnlyList<TagRowView> parents)
            {
                Row = row;
                Parents = parents;
                IndentedTag = new string(' ', row.Depth * 2) + (string.IsNullOrEmpty(row.Tag) ? ItemTag : row.Tag);
            }

            public TagPreviewRow Row { get; }
            public IReadOnlyList<TagRowView> Parents { get; }
            public string IndentedTag { get; }
            public string Vr => Row.Vr;
            public string Name => Row.Name;
            public string Value => Row.Value;

            public bool Matches(string filter) =>
                Contains(Row.Tag, filter) || Contains(Row.Name, filter) || Contains(Row.Value, filter)
                // (0010,0020) is also found as 00100020 or 0010,0020.
                || Contains(Row.Tag?.Replace("(", string.Empty).Replace(")", string.Empty).Replace(",", string.Empty), filter);

            private static bool Contains(string text, string filter) =>
                text != null && text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
