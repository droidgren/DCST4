using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace DCST4
{
    /// <summary>
    /// In-app help, in the same layout as DMWLT4's. The topic list is built from the top-level Sections of the document
    /// (their Tag is the title), and selecting a topic scrolls it to the top of the viewer. The search box above the list
    /// finds a phrase anywhere in the document, keeps only the topics that have it, and selects each match in turn.
    /// </summary>
    public partial class HelpWindow : Window
    {
        public class Topic : INotifyPropertyChanged
        {
            private int _matchCount;

            public string Title { get; set; }
            public Section Section { get; set; }

            /// <summary>Matches of the current search in this topic, 0 without a search.</summary>
            public int MatchCount
            {
                get => _matchCount;
                set
                {
                    if (_matchCount == value) return;
                    _matchCount = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayTitle));
                }
            }

            public string DisplayTitle => MatchCount > 0 ? Title + " (" + MatchCount.ToString(CultureInfo.CurrentCulture) + ")" : Title;

            public override string ToString() => Title;

            public event PropertyChangedEventHandler PropertyChanged;
            private void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        /// <summary>A run of text in the document, at <see cref="Offset"/> in the flattened text.</summary>
        private sealed class Segment
        {
            public int Offset;
            public int Length;
            public TextPointer Start;
            public Topic Topic;
        }

        private struct Match
        {
            public int Offset;
            public int Length;
            public Topic Topic;
        }

        public List<Topic> Topics { get; }

        private readonly string _text;
        private readonly List<Segment> _segments = new List<Segment>();
        private List<Match> _matches = new List<Match>();
        private int _current = -1;
        private bool _syncingTopicList;

        public HelpWindow()
        {
            InitializeComponent();
            Title = AppInfo.DisplayName + " Help";

            Topics = HelpDocument.Blocks.OfType<Section>()
                .Where(s => s.Tag is string)
                .Select(s => new Topic { Title = (string)s.Tag, Section = s })
                .ToList();
            TopicList.ItemsSource = Topics;
            TopicList.SelectedIndex = 0;

            _text = BuildIndex();

            PreviewKeyDown += Window_PreviewKeyDown;
        }

        /// <summary>The topics in the list: all of them, or those with a match while a search is active.</summary>
        public IReadOnlyList<Topic> ShownTopics => TopicList.Items.OfType<Topic>().ToList();

        /// <summary>Matches of the current search in the whole document.</summary>
        public int MatchCount => _matches.Count;

        /// <summary>The selected match, from 1, or 0 when there is none.</summary>
        public int CurrentMatch => _current + 1;

        /// <summary>The text under the search box, such as "3 of 12 in 4 topics".</summary>
        public string SearchStatusText => SearchStatus.Visibility == Visibility.Visible ? SearchStatus.Text : string.Empty;

        /// <summary>The text that is selected in the document, which is the current match during a search.</summary>
        public string SelectedText => Viewer.Selection?.Text ?? string.Empty;

        /// <summary>Scrolls the given topic (by title) to the top and selects it in the list.</summary>
        public void ShowTopic(string title)
        {
            var topic = Topics.FirstOrDefault(t => t.Title == title);
            if (topic == null) return;
            if (ReferenceEquals(TopicList.SelectedItem, topic)) ScrollToSection(topic.Section);
            else TopicList.SelectedItem = topic;
        }

        /// <summary>Puts the keyboard in the search box, with what is there selected.</summary>
        public void FocusSearch()
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.SelectAll();
        }

        /// <summary>Searches as if the text had been typed in the search box.</summary>
        public void Search(string text) => SearchBox.Text = text ?? string.Empty;

        public void NextMatch() => Step(+1);

        public void PreviousMatch() => Step(-1);

        // ---------------------------------------------------------------- index

        /// <summary>
        /// Flattens the text of every topic into one string, remembering where each run starts, so that a phrase is found
        /// also when part of it is bold. Paragraphs, list items and table cells end with a line break, so a match never
        /// runs from one into the next.
        /// </summary>
        private string BuildIndex()
        {
            var text = new StringBuilder();
            foreach (var topic in Topics)
            {
                var end = topic.Section.ContentEnd;
                var pointer = topic.Section.ContentStart;
                while (pointer != null && pointer.CompareTo(end) < 0)
                {
                    var context = pointer.GetPointerContext(LogicalDirection.Forward);
                    if (context == TextPointerContext.Text)
                    {
                        var run = pointer.GetTextInRun(LogicalDirection.Forward);
                        _segments.Add(new Segment { Offset = text.Length, Length = run.Length, Start = pointer, Topic = topic });
                        text.Append(run);
                        pointer = pointer.GetPositionAtOffset(run.Length, LogicalDirection.Forward);
                        continue;
                    }

                    var element = pointer.GetAdjacentElement(LogicalDirection.Forward);
                    if (context == TextPointerContext.ElementEnd && (element is Block || element is ListItem || element is TableCell)) text.Append('\n');
                    else if (context == TextPointerContext.ElementStart && element is LineBreak) text.Append(' ');
                    pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
                }
                text.Append('\n');
            }
            return text.ToString();
        }

        /// <summary>The position in the document of an offset in the flattened text.</summary>
        private TextPointer PointerAt(int offset, bool isEnd)
        {
            int low = 0, high = _segments.Count - 1, found = -1;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                var segment = _segments[middle];
                var starts = isEnd ? segment.Offset < offset : segment.Offset <= offset;
                if (starts) { found = middle; low = middle + 1; }
                else high = middle - 1;
            }
            if (found < 0) return null;
            var hit = _segments[found];
            var within = Math.Min(offset - hit.Offset, hit.Length);
            return hit.Start.GetPositionAtOffset(within, isEnd ? LogicalDirection.Backward : LogicalDirection.Forward);
        }

        // ---------------------------------------------------------------- search

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RunSearch();

        private void RunSearch()
        {
            var query = SearchBox.Text.Trim();
            var anchor = _current >= 0 && _current < _matches.Count ? _matches[_current].Offset : 0;

            _matches = new List<Match>();
            _current = -1;
            foreach (var topic in Topics) topic.MatchCount = 0;

            if (query.Length == 0)
            {
                var selected = TopicList.SelectedItem;
                SyncTopicList(() =>
                {
                    TopicList.ItemsSource = Topics;
                    TopicList.SelectedItem = selected;
                });
                ClearSelection();
                SearchStatus.Visibility = Visibility.Collapsed;
                return;
            }

            var segment = 0;
            for (var at = _text.IndexOf(query, StringComparison.OrdinalIgnoreCase); at >= 0; at = _text.IndexOf(query, at + query.Length, StringComparison.OrdinalIgnoreCase))
            {
                while (segment + 1 < _segments.Count && _segments[segment + 1].Offset <= at) segment++;
                var topic = _segments[segment].Topic;
                _matches.Add(new Match { Offset = at, Length = query.Length, Topic = topic });
                topic.MatchCount++;
            }

            var shown = Topics.Where(t => t.MatchCount > 0).ToList();
            SyncTopicList(() => TopicList.ItemsSource = shown);
            SearchStatus.Visibility = Visibility.Visible;

            if (_matches.Count == 0)
            {
                ClearSelection();
                SearchStatus.Text = "No matches";
                return;
            }

            // Typing on refines the search where it stands rather than jumping back to the first topic.
            var next = _matches.FindIndex(m => m.Offset >= anchor);
            ShowMatch(next < 0 ? 0 : next);
        }

        private void Step(int direction)
        {
            if (_matches.Count == 0) return;
            var next = _current < 0 ? (direction > 0 ? 0 : _matches.Count - 1) : (_current + direction + _matches.Count) % _matches.Count;
            ShowMatch(next);
        }

        private void ShowMatch(int index)
        {
            _current = index;
            var match = _matches[index];
            var start = PointerAt(match.Offset, false);
            var end = PointerAt(match.Offset + match.Length, true);
            if (start != null && end != null)
            {
                Viewer.Selection?.Select(start, end);
                ScrollTo(start, Math.Max(12, ViewportHeight() / 3));
            }
            SyncTopicList(() => TopicList.SelectedItem = match.Topic);
            TopicList.ScrollIntoView(match.Topic);

            var topics = _matches.Select(m => m.Topic).Distinct().Count();
            SearchStatus.Text = string.Format(CultureInfo.CurrentCulture, "{0} of {1} in {2} {3}",
                index + 1, _matches.Count, topics, topics == 1 ? "topic" : "topics");
        }

        private void ClearSelection()
        {
            var selection = Viewer.Selection;
            selection?.Select(selection.Start, selection.Start);
        }

        /// <summary>Changes the topic list without the change scrolling the document.</summary>
        private void SyncTopicList(Action change)
        {
            _syncingTopicList = true;
            try { change(); }
            finally { _syncingTopicList = false; }
        }

        // ---------------------------------------------------------------- keys and list

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                FocusSearch();
                e.Handled = true;
            }
            else if (e.Key == Key.F3)
            {
                Step(shift ? -1 : +1);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                // The first Esc ends a search, the next one closes the window.
                if (SearchBox.Text.Length > 0) SearchBox.Text = string.Empty;
                else Close();
                e.Handled = true;
            }
        }

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Step((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : +1);
                e.Handled = true;
            }
        }

        private void TopicList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingTopicList || !(TopicList.SelectedItem is Topic topic)) return;

            // During a search a topic opens at its first match.
            var first = _matches.FindIndex(m => ReferenceEquals(m.Topic, topic));
            if (first >= 0) ShowMatch(first);
            else ScrollToSection(topic.Section);
        }

        // ---------------------------------------------------------------- scrolling

        private void ScrollToSection(Section section)
        {
            // Leave a little room above the heading (and reach the very top for the first topic).
            if (!ScrollTo(section.ContentStart, 12)) section.BringIntoView();
        }

        /// <summary>Scrolls so that <paramref name="position"/> is <paramref name="room"/> below the top of the viewer.</summary>
        private bool ScrollTo(TextPointer position, double room)
        {
            var scrollViewer = FindChild<ScrollViewer>(Viewer);
            if (scrollViewer == null) return false;

            // Measure with the document scrolled to the top, so the position is the same whether the
            // character rect is relative to the viewport or to the document. This happens in one layout
            // pass, so the jump to the top is not visible.
            scrollViewer.ScrollToVerticalOffset(0);
            scrollViewer.UpdateLayout();
            var rect = position.GetCharacterRect(LogicalDirection.Forward);
            if (rect.IsEmpty) return false;
            scrollViewer.ScrollToVerticalOffset(Math.Max(0, rect.Top - room));
            return true;
        }

        private double ViewportHeight() => FindChild<ScrollViewer>(Viewer)?.ViewportHeight ?? 0;

        private static T FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;
                var found = FindChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }
    }
}
