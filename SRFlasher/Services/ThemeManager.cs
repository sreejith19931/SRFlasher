using System;
using System.Collections.Generic;
using System.Windows;

namespace SRFlasher.Services
{
    /// <summary>
    /// Manages application theme (Light/Dark/Auto) with system detection and persistence.
    /// </summary>
    public class ThemeManager
    {
        private static ThemeManager _instance;
        private readonly LoggingService _logger;

        public enum ThemeMode
        {
            Auto,      // Follow system theme
            Light,
            Dark,
            Oled       // True-black variant of Dark
        }

        private ThemeMode _currentTheme = ThemeMode.Auto;

        /// <summary>
        /// The theme actually rendered: Auto resolved against the Windows setting.
        /// </summary>
        public ThemeMode EffectiveTheme { get; private set; } = ThemeMode.Dark;

        public event EventHandler ThemeChanged;

        private ThemeManager()
        {
            _logger = LoggingService.Instance;
            LoadSavedTheme();
        }

        public static ThemeManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (typeof(ThemeManager))
                    {
                        if (_instance == null)
                        {
                            _instance = new ThemeManager();
                        }
                    }
                }
                return _instance;
            }
        }

        public ThemeMode CurrentTheme
        {
            get => _currentTheme;
            set
            {
                if (_currentTheme != value)
                {
                    _currentTheme = value;
                    ApplyTheme(value);
                    SaveTheme(value);
                    ThemeChanged?.Invoke(this, EventArgs.Empty);
                    _logger.Information("Theme changed to: {Theme}", value);
                }
            }
        }

        /// <summary>
        /// Apply theme to current application window.
        /// </summary>
        public void ApplyTheme(ThemeMode theme)
        {
            try
            {
                ThemeMode effectiveTheme = theme;

                // Detect system theme if Auto is selected
                if (theme == ThemeMode.Auto)
                {
                    effectiveTheme = GetSystemTheme();
                }

                EffectiveTheme = effectiveTheme;

                string themeUri;
                switch (effectiveTheme)
                {
                    case ThemeMode.Light:
                        themeUri = "pack://application:,,,/Themes/LightTheme.xaml";
                        break;
                    case ThemeMode.Oled:
                        themeUri = "pack://application:,,,/Themes/OledTheme.xaml";
                        break;
                    default:
                        themeUri = "pack://application:,,,/Themes/DarkTheme.xaml";
                        break;
                }

                // Remove existing theme
                var themeDictionaries = Application.Current.Resources.MergedDictionaries;
                for (int i = themeDictionaries.Count - 1; i >= 0; i--)
                {
                    if (themeDictionaries[i].Source != null && 
                        themeDictionaries[i].Source.OriginalString.Contains("Theme.xaml"))
                    {
                        themeDictionaries.RemoveAt(i);
                    }
                }

                // Add new theme
                var newTheme = new ResourceDictionary { Source = new Uri(themeUri) };
                themeDictionaries.Add(newTheme);

                _logger.Debug("Applied theme: {Theme}", effectiveTheme);
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to apply theme: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// Detect system theme (Windows light/dark mode).
        /// </summary>
        private ThemeMode GetSystemTheme()
        {
            try
            {
                // Try to detect Windows theme from registry
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        var value = key.GetValue("AppsUseLightTheme");
                        if (value is int intValue)
                        {
                            return intValue == 1 ? ThemeMode.Light : ThemeMode.Dark;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Failed to detect system theme: {Message}", ex.Message);
            }

            // Default to Dark if detection fails
            return ThemeMode.Dark;
        }

        /// <summary>
        /// Load saved theme preference from application settings.
        /// </summary>
        private void LoadSavedTheme()
        {
            try
            {
                string savedTheme = Properties.Settings.Default["Theme"] as string ?? "Auto";
                if (Enum.TryParse<ThemeMode>(savedTheme, true, out var theme))
                {
                    _currentTheme = theme;
                    ApplyTheme(theme);
                    _logger.Information("Loaded saved theme: {Theme}", theme);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Failed to load saved theme: {Message}", ex.Message);
                _currentTheme = ThemeMode.Auto;
                ApplyTheme(ThemeMode.Auto);
            }
        }

        /// <summary>
        /// Save theme preference to application settings.
        /// </summary>
        private void SaveTheme(ThemeMode theme)
        {
            try
            {
                Properties.Settings.Default["Theme"] = theme.ToString();
                Properties.Settings.Default.Save();
            }
            catch (Exception ex)
            {
                _logger.Warning("Failed to save theme preference: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// Re-resolve the Auto theme against the current Windows setting and apply
        /// it when it changed. Called when Windows reports a light/dark switch;
        /// does nothing while an explicit Dark/Light/OLED choice is active.
        /// </summary>
        public void RefreshAutoTheme()
        {
            if (_currentTheme != ThemeMode.Auto)
                return;

            var systemTheme = GetSystemTheme();
            if (systemTheme == EffectiveTheme)
                return;

            ApplyTheme(ThemeMode.Auto);
            ThemeChanged?.Invoke(this, EventArgs.Empty);
            _logger.Information("Windows theme changed; auto theme now {Theme}", systemTheme);
        }
    }
}
