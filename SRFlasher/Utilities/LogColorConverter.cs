using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SRFlasher.Utilities
{
    /// <summary>
    /// Colors log entries by severity based on their prefix:
    /// [ERROR] / [WARNING] / [✓] use the active theme's severity brushes
    /// (bright variants on dark/OLED themes, deep variants on light);
    /// normal lines always resolve the theme's TextPrimary explicitly —
    /// they must never rely on control inheritance, which the default
    /// WPF ListBoxItem style breaks with a system-color foreground
    /// (black text even in dark mode).
    /// </summary>
    public class LogColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush ErrorBrush = CreateFrozen("#E74C3C");
        private static readonly SolidColorBrush WarningBrush = CreateFrozen("#E17055");
        private static readonly SolidColorBrush SuccessBrush = CreateFrozen("#00B894");
        private static readonly SolidColorBrush NormalBrush = CreateFrozen("#D8DCE3");

        private static SolidColorBrush CreateFrozen(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        private static Brush Resolve(string key, Brush fallback)
            => Application.Current?.TryFindResource(key) as Brush ?? fallback;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var text = value as string;
            if (string.IsNullOrEmpty(text))
                return Resolve("TextPrimary", NormalBrush);

            if (text.IndexOf("[ERROR]", StringComparison.OrdinalIgnoreCase) >= 0)
                return Resolve("LogErrorBrush", ErrorBrush);
            if (text.IndexOf("[WARNING]", StringComparison.OrdinalIgnoreCase) >= 0)
                return Resolve("LogWarningBrush", WarningBrush);
            if (text.Contains("[✓]"))
                return Resolve("LogSuccessBrush", SuccessBrush);

            return Resolve("TextPrimary", NormalBrush);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
