using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DCST4.Infrastructure
{
    /// <summary>Collapsed when true, visible when false.</summary>
    public sealed class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is bool flag && flag ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is Visibility visibility && visibility != Visibility.Visible;
    }
}
