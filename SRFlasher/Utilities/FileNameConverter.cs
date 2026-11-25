using System;
using System.Globalization;
using System.Windows.Data;
using System.IO;

namespace SRFlasher.Utilities
{
    /// <summary>
    /// Shows only the file name of a full path (used by the recents lists).
    /// </summary>
    public class FileNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var path = value as string;
            if (string.IsNullOrEmpty(path)) return string.Empty;
            return Path.GetFileName(path);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
