using System;
using System.Windows;
using System.Windows.Controls;

namespace DCST4.Infrastructure
{
    /// <summary>
    /// Lays out two children on one row: the first at the left, the second at the right. When the row is too narrow for
    /// both, the second goes to the line below, still at the right. When the first is collapsed, the second is at the left.
    /// </summary>
    public sealed class SplitRowPanel : Panel
    {
        /// <summary>The least space between the two children on one row.</summary>
        public double Gap { get; set; } = 16;

        /// <summary>True: takes the whole width it is given. False: only what the children need, for use inside another one.</summary>
        public bool FillWidth { get; set; } = true;

        private bool _wrapped;

        protected override Size MeasureOverride(Size available)
        {
            var left = Child(0);
            var right = Child(1);
            left?.Measure(available);
            right?.Measure(available);
            var l = Desired(left);
            var r = Desired(right);
            var gap = l.Width > 0 && r.Width > 0 ? Gap : 0;
            _wrapped = l.Width > 0 && r.Width > 0 && l.Width + gap + r.Width > available.Width;
            var width = _wrapped ? Math.Max(l.Width, r.Width) : l.Width + gap + r.Width;
            var height = _wrapped ? l.Height + r.Height : Math.Max(l.Height, r.Height);
            // Takes the whole width it is given, so that the second child can sit at its right edge.
            if (FillWidth && !double.IsInfinity(available.Width) && l.Width > 0) width = Math.Max(width, available.Width);
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size final)
        {
            var left = Child(0);
            var right = Child(1);
            var l = Desired(left);
            var r = Desired(right);
            if (_wrapped)
            {
                left?.Arrange(new Rect(0, 0, Math.Min(l.Width, final.Width), l.Height));
                right?.Arrange(new Rect(Math.Max(0, final.Width - r.Width), l.Height, Math.Min(r.Width, final.Width), r.Height));
            }
            else
            {
                var height = Math.Max(l.Height, r.Height);
                left?.Arrange(new Rect(0, (height - l.Height) / 2, l.Width, l.Height));
                var x = l.Width > 0 ? Math.Max(l.Width + Gap, final.Width - r.Width) : 0;
                right?.Arrange(new Rect(x, (height - r.Height) / 2, r.Width, r.Height));
            }
            return final;
        }

        private UIElement Child(int index) => InternalChildren.Count > index ? InternalChildren[index] : null;

        private static Size Desired(UIElement element) =>
            element == null || element.Visibility == Visibility.Collapsed ? new Size(0, 0) : element.DesiredSize;
    }
}
