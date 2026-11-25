using System;
using System.Globalization;
using System.Windows.Data;

namespace SRFlasher.Utilities
{
    /// <summary>
    /// Maps the session progress (0-100) to the phone's fill height in the
    /// FLASHING IN PROGRESS diagram: the phone fills from the bottom as the
    /// ROM installs.
    /// </summary>
    public class ProgressToPhoneFillConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var percent = value as double? ?? 0;
            if (double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var max))
                percent = Math.Min(percent, max);
            percent = Math.Max(0, Math.Min(100, percent));
            // 62 px of usable fill height inside the phone shell.
            return percent / 100.0 * 62.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
