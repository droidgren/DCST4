using System;
using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;

namespace DCST4.Infrastructure
{
    /// <summary>Keeps a list or grid scrolled to its newest item while items are appended.</summary>
    public static class FollowNewest
    {
        /// <summary>
        /// Scrolls <paramref name="view"/> to the last item of <paramref name="items"/> after items are added.
        /// The scroll is queued on the dispatcher rather than done in the CollectionChanged handler. A handler
        /// subscribed before the control's own binding runs before the control has seen the new item, and
        /// ScrollIntoView then forces a layout pass that fails the ItemContainerGenerator consistency check
        /// ("ItemsControl is inconsistent with its items source"). A burst of adds gives one scroll.
        /// </summary>
        public static void Attach(INotifyCollectionChanged items, FrameworkElement view, Action<object> scrollIntoView, bool onlyWhenVisible)
        {
            if (!(items is IList list)) throw new ArgumentException("The collection must be an IList.", nameof(items));
            var pending = false;
            items.CollectionChanged += (s, e) =>
            {
                if (e.Action != NotifyCollectionChangedAction.Add || pending) return;
                pending = true;
                view.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    pending = false;
                    if (list.Count == 0 || (onlyWhenVisible && !view.IsVisible)) return;
                    scrollIntoView(list[list.Count - 1]);
                }));
            };
        }
    }
}
