using SRFlasher.Dialogs;
using SRFlasher.Services;
using SRFlasher.Utilities;
using SRFlasher.ViewModels;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SRFlasher
{
    /// <summary>
    /// Main application window with MVVM-based architecture.
    /// Business logic is handled by MainWindowViewModel.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainWindowViewModel _viewModel;

        // When true, the log list is anchored to the bottom and follows new entries.
        private bool _logAutoScroll = true;
        // The ScrollViewer inside the log ListBox template.
        private ScrollViewer _logScrollViewer;
        // Guards against re-entrant SelectionChanged while we reset the combo.
        private bool _suppressRecentSelection;
        // Current visual state of the ROM drop zone (avoids re-resolving the
        // highlight brushes on every DragOver tick).
        private bool _romDropHighlighted;

        /// <summary>
        /// Find the first ScrollViewer in the visual tree of an element.
        /// </summary>
        private static ScrollViewer FindScrollViewer(DependencyObject parent)
        {
            if (parent == null) return null;
            if (parent is ScrollViewer sv) return sv;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var result = FindScrollViewer(child);
                if (result != null)
                    return result;
            }
            return null;
        }

        public MainWindow()
        {
            // Fade in on load (animated to 1 in MainWindow_Loaded).
            Opacity = 0;

            InitializeComponent();

            // Restore saved window size/position before the window is shown.
            _viewModel = new MainWindowViewModel();
            DataContext = _viewModel;
            _viewModel.ApplySavedWindowBounds(this);

            // Follow new log entries while the user is scrolled to the bottom.
            _viewModel.FilteredLogs.CollectionChanged += (s, e) =>
            {
                if (_logAutoScroll)
                    _logScrollViewer?.ScrollToBottom();
            };

            // The FOLLOW toggle also forces the list back to the newest line.
            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.FollowLogs) && _viewModel.FollowLogs)
                {
                    _logAutoScroll = true;
                    Dispatcher.BeginInvoke(new Action(() => _logScrollViewer?.ScrollToBottom()),
                        System.Windows.Threading.DispatcherPriority.Background);
                }
            };

            // The log list lives inside a tab that is not selected at startup,
            // so its template (and ScrollViewer) only exists once the tab is
            // opened for the first time. Hook it then.
            LogList.Loaded += LogList_Loaded;

            // Side-banner (toast) motion: entrance plays through Utilities.Motion
            // (fade + slide from the right edge with an overshoot settle) the
            // moment a toast is added; exits play before the item is removed.
            _viewModel.Toasts.CollectionChanged += (s, e) =>
            {
                if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add || e.NewItems == null)
                    return;
                foreach (MainWindowViewModel.ToastItem item in e.NewItems)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var border = FindToastBorder(ToastHost.ItemContainerGenerator.ContainerFromItem(item));
                        if (border != null)
                            Utilities.Motion.ToastIn(border);
                    }), System.Windows.Threading.DispatcherPriority.Loaded);
                }
            };
            _viewModel.ToastCloseRequested += item =>
            {
                Dispatcher.BeginInvoke(new Action(async () => await CloseToastWithAnimationAsync(item)));
            };

            // 4.1: the DEVICE dashboard populates the moment the tab is opened
            // — an empty card on first open read as a broken page. The manual
            // REFRESH DETAILS button still forces a full re-read.
            TabDevice.IsVisibleChanged += async (s, e) =>
            {
                if ((bool)e.NewValue && !_viewModel.IsFlashing)
                    await _viewModel.RefreshDeviceDashboardAsync();
            };

            // Soft dip while theme brushes swap so the switch doesn't pop harshly.
            ThemeManager.Instance.ThemeChanged += (s, e) =>
            {
                BeginAnimation(Window.OpacityProperty,
                    new DoubleAnimation(1, 0.85, TimeSpan.FromMilliseconds(130)) { AutoReverse = true });
                ApplyTitleBarTheme();
            };

            // Setup window event handlers
            SourceInitialized += MainWindow_SourceInitialized;
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;

            LoggingService.Instance.Information("MainWindow initialized");
        }

        private void LogScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // A zero ExtentHeightChange means the user scrolled (not new content).
            if (e.ExtentHeightChange == 0)
            {
                _logAutoScroll = _logScrollViewer.ScrollableHeight <= 0 ||
                    Math.Abs(e.VerticalOffset + e.ViewportHeight - e.ExtentHeight) < 1.0;
                // Keep the FOLLOW toggle in sync with the scroll position.
                if (_viewModel != null && _viewModel.FollowLogs != _logAutoScroll)
                    _viewModel.FollowLogs = _logAutoScroll;
            }
            else if (_logAutoScroll)
            {
                _logScrollViewer.ScrollToBottom();
            }
        }

        // LOGS tab - severity chip (ALL / ERRORS / WARN)
        private void LogLevelChip_Checked(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null && sender is System.Windows.Controls.RadioButton rb && rb.Tag is string level)
                _viewModel.LogLevelFilter = level;
        }

        private void LogList_Loaded(object sender, RoutedEventArgs e)
        {
            AttachLogScrollViewer();
        }

        /// <summary>
        /// Subscribe to the ScrollViewer inside the log ListBox template and
        /// pin the view to the newest entry. Safe to call repeatedly: the tab
        /// content is unrealized at startup and realized later.
        /// </summary>
        private void AttachLogScrollViewer()
        {
            var scrollViewer = FindScrollViewer(LogList);
            if (scrollViewer == null || ReferenceEquals(scrollViewer, _logScrollViewer))
                return;

            if (_logScrollViewer != null)
                _logScrollViewer.ScrollChanged -= LogScrollViewer_ScrollChanged;

            _logScrollViewer = scrollViewer;
            _logScrollViewer.ScrollChanged += LogScrollViewer_ScrollChanged;
            _logScrollViewer.ScrollToBottom();
        }

        // Keyboard shortcuts:
        //   Ctrl+O -> Browse for a ROM, Ctrl+F -> Flash, Ctrl+C -> copy the selected
        //   log lines, F5 -> refresh devices.
        private async void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // F1 toggles the shortcuts overlay (G5).
            if (e.Key == Key.F1)
            {
                ShortcutsOverlay.Visibility = ShortcutsOverlay.Visibility == Visibility.Visible
                    ? Visibility.Collapsed : Visibility.Visible;
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape && ShortcutsOverlay.Visibility == Visibility.Visible)
            {
                ShortcutsOverlay.Visibility = Visibility.Collapsed;
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F5)
            {
                await _viewModel.RefreshDeviceStateAsync();
                e.Handled = true;
                return;
            }

            // The log context menu advertises Ctrl+C; honour it whenever the log
            // list has a selection. Text boxes keep their own copy behaviour.
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (!(e.OriginalSource is TextBox) && !(Keyboard.FocusedElement is TextBox)
                    && LogList.SelectedItems.Count > 0)
                {
                    MenuCopySelected_Click(sender, e);
                    e.Handled = true;
                }
                return;
            }

            // Don't hijack Ctrl+O / Ctrl+F while typing in a text field.
            if (Keyboard.Modifiers != ModifierKeys.Control || e.OriginalSource is TextBox)
                return;

            if (e.Key == Key.O)
            {
                BtnBrowse_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.F)
            {
                // The shortcut must respect the same gate as the button: no
                // confirm dialog when the flash would be refused anyway.
                if (_viewModel.FlashEnabled)
                {
                    BtnFlash_Click(sender, e);
                    e.Handled = true;
                }
            }
        }

        // Sidebar - collapse to an icon rail / expand back.
        private bool _sidebarAnimating;

        private void BtnToggleSidebar_Click(object sender, RoutedEventArgs e)
        {
            if (_sidebarAnimating) return;
            ToggleSidebarAnimated(Sidebar.Visibility != Visibility.Visible);
        }

        /// <summary>
        /// Direct morph between the expanded sidebar and the icon rail: both
        /// elements are on screen at the same time — the panel's width and
        /// opacity run while the rail fades/slides in over it — so the sidebar
        /// turns INTO the rail in one continuous motion instead of fading out
        /// and re-appearing. Completed handlers are attached BEFORE
        /// BeginAnimation (animations freeze once started); on any failure the
        /// layout falls back to the instant toggle.
        /// </summary>
        private void ToggleSidebarAnimated(bool toExpanded)
        {
            _sidebarAnimating = true;
            try
            {
                var ease = new System.Windows.Media.Animation.CubicEase
                    { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut };
                var dur = TimeSpan.FromMilliseconds(280);

                if (toExpanded)
                {
                    // Both visible at once: the rail fades/slides out while the
                    // panel grows from the rail's width to full width.
                    SidebarRail.RenderTransform = new System.Windows.Media.TranslateTransform(0, 0);
                    Sidebar.Visibility = Visibility.Visible;
                    Sidebar.Width = 52;
                    Sidebar.Opacity = 0;

                    var grow = new System.Windows.Media.Animation.DoubleAnimation
                    {
                        From = 52, To = 260, Duration = dur, EasingFunction = ease
                    };
                    var panelIn = new System.Windows.Media.Animation.DoubleAnimation
                    {
                        From = 0, To = 1, Duration = dur, EasingFunction = ease
                    };
                    var railTt = new System.Windows.Media.TranslateTransform(0, 0);
                    SidebarRail.RenderTransform = railTt;
                    var railOut = new System.Windows.Media.Animation.DoubleAnimation
                    {
                        From = 0, To = -24, Duration = dur, EasingFunction = ease
                    };
                    var railOutFade = new System.Windows.Media.Animation.DoubleAnimation
                    {
                        From = 1, To = 0, Duration = dur, EasingFunction = ease
                    };
                    grow.Completed += (s, e) =>
                    {
                        try
                        {
                            Sidebar.BeginAnimation(System.Windows.Controls.Border.WidthProperty, null);
                            Sidebar.Width = 260;
                            SidebarRail.Visibility = Visibility.Collapsed;
                            SidebarRail.RenderTransform = null;
                            SidebarRail.BeginAnimation(UIElement.OpacityProperty, null);
                            SidebarRail.Opacity = 1;
                        }
                        catch { }
                        _sidebarAnimating = false;
                    };
                    Sidebar.BeginAnimation(System.Windows.Controls.Border.WidthProperty, grow);
                    Sidebar.BeginAnimation(UIElement.OpacityProperty, panelIn);
                    railTt.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, railOut);
                    SidebarRail.BeginAnimation(UIElement.OpacityProperty, railOutFade);
                }
                else
                {
                    // Both visible at once: the panel shrinks toward the rail's
                    // width while fading, the rail fades/slides in over it.
                    SidebarRail.Visibility = Visibility.Visible;
                    SidebarRail.Opacity = 0;
                    var railTt = new System.Windows.Media.TranslateTransform(-24, 0);
                    SidebarRail.RenderTransform = railTt;

                    var shrink = new System.Windows.Media.Animation.DoubleAnimation
                    {
                        From = 260, To = 52, Duration = dur, EasingFunction = ease
                    };
                    var panelOut = new System.Windows.Media.Animation.DoubleAnimation
                    {
                        From = 1, To = 0, Duration = dur, EasingFunction = ease
                    };
                    var railIn = new System.Windows.Media.Animation.DoubleAnimation
                    {
                        From = -24, To = 0, Duration = dur, EasingFunction = ease
                    };
                    var railInFade = new System.Windows.Media.Animation.DoubleAnimation
                    {
                        From = 0, To = 1, Duration = dur, EasingFunction = ease
                    };
                    shrink.Completed += (s, e) =>
                    {
                        try
                        {
                            Sidebar.BeginAnimation(System.Windows.Controls.Border.WidthProperty, null);
                            Sidebar.Visibility = Visibility.Collapsed;
                            Sidebar.Width = 260;
                            Sidebar.Opacity = 1;
                            SidebarRail.RenderTransform = null;
                            SidebarRail.BeginAnimation(UIElement.OpacityProperty, null);
                            SidebarRail.Opacity = 1;
                        }
                        catch { }
                        _sidebarAnimating = false;
                    };
                    Sidebar.BeginAnimation(System.Windows.Controls.Border.WidthProperty, shrink);
                    Sidebar.BeginAnimation(UIElement.OpacityProperty, panelOut);
                    railTt.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, railIn);
                    SidebarRail.BeginAnimation(UIElement.OpacityProperty, railInFade);
                }
            }
            catch
            {
                // Never strand the layout: fall back to the instant toggle.
                Sidebar.Visibility = toExpanded ? Visibility.Visible : Visibility.Collapsed;
                Sidebar.Width = 260;
                Sidebar.Opacity = 1;
                SidebarRail.Visibility = toExpanded ? Visibility.Collapsed : Visibility.Visible;
                SidebarRail.Opacity = 1;
                SidebarRail.RenderTransform = null;
                _sidebarAnimating = false;
            }
        }


        // Sidebar - interface mode (Simple / Expert) for the collapsed rail.
        private void BtnModeSimple_Click(object sender, RoutedEventArgs e) => _viewModel.IsExpertMode = false;
        private void BtnModeExpert_Click(object sender, RoutedEventArgs e) => _viewModel.IsExpertMode = true;

        // G5: shortcuts overlay close button.
        private void BtnCloseShortcuts_Click(object sender, RoutedEventArgs e)
            => ShortcutsOverlay.Visibility = Visibility.Collapsed;

        // After Care: jump to the POST-FLASH tab for the next step.
        private void BtnAfterCarePostFlash_Click(object sender, RoutedEventArgs e)
            => TabAdditional.IsSelected = true;

        // S2: copy the device serial to the clipboard.
        private void BtnCopySerial_Click(object sender, RoutedEventArgs e)
        {
            var serial = _viewModel.DeviceSerialDisplay;
            if (string.IsNullOrWhiteSpace(serial) || serial == "—")
                return;
            try
            {
                Clipboard.SetText(serial);
                _viewModel.ShowToast("Success", "Serial copied", serial + " is on the clipboard.");
            }
            catch (Exception ex)
            {
                _viewModel.Log($"[ERROR] Could not copy serial: {ex.Message}");
            }
        }

        // S4: clear the recent ROM list.
        private void BtnClearRecent_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.ClearRecentFiles();
            _viewModel.Log("[Recent] Recent file list cleared.");
        }

        // G7: ADDITIONAL FILES stacks vertically when the window is narrow, so
        // the device card never gets squeezed to its 300px minimum.
        private void PostFlashLayoutGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!(sender is System.Windows.Controls.Grid grid)) return;

            var narrow = e.NewSize.Width < 760;
            var target = grid.Tag as bool?;
            if (target == narrow) return;   // nothing changed
            grid.Tag = narrow;

            // Children in XAML order: [0] header, [1] file card, [2] device
            // card, [3] finish strip. The queue strip is gone.
            var children = grid.Children.Cast<System.Windows.UIElement>().ToList();
            if (children.Count < 4) return;
            var header = children[0]; var file = children[1];
            var device = children[2]; var finish = children[3];

            grid.ColumnDefinitions.Clear();
            grid.RowDefinitions.Clear();

            if (!narrow)
            {
                grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
                grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });

                System.Windows.Controls.Grid.SetRow(header, 0);
                System.Windows.Controls.Grid.SetColumnSpan(header, 2);
                System.Windows.Controls.Grid.SetRow(file, 1);
                System.Windows.Controls.Grid.SetColumn(file, 0);
                System.Windows.Controls.Grid.SetRow(device, 1);
                System.Windows.Controls.Grid.SetColumn(device, 1);
                System.Windows.Controls.Grid.SetRow(finish, 2);
                System.Windows.Controls.Grid.SetColumn(finish, 0);
                System.Windows.Controls.Grid.SetColumnSpan(finish, 2);
            }
            else
            {
                grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
                grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
                grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
                grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                System.Windows.Controls.Grid.SetRow(header, 0);
                System.Windows.Controls.Grid.SetColumnSpan(header, 1);
                System.Windows.Controls.Grid.SetRow(file, 1);
                System.Windows.Controls.Grid.SetColumn(file, 0);
                System.Windows.Controls.Grid.SetRow(device, 2);
                System.Windows.Controls.Grid.SetColumn(device, 0);
                System.Windows.Controls.Grid.SetRow(finish, 3);
                System.Windows.Controls.Grid.SetColumn(finish, 0);
                System.Windows.Controls.Grid.SetColumnSpan(finish, 1);
            }
        }

        // Toolbar - open the current log file in Explorer.
        private void BtnOpenLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = LoggingService.Instance.GetLogFilePath();
                if (File.Exists(path))
                {
                    Process.Start("explorer.exe", $"/select,\"{path}\"");
                }
                else
                {
                    Process.Start("explorer.exe", LoggingService.Instance.GetLogDirectory());
                }
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Warning("Failed to open log location: {Message}", ex.Message);
            }
        }

        // ROM path - double-click to copy to clipboard.
        private void PathText_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var text = (sender as System.Windows.Controls.TextBox)?.Text ?? PathText.Text;
            if (string.IsNullOrEmpty(text) || text == "No file selected." || text == "Validation failed.")
                return;

            try
            {
                Clipboard.SetText(text);
                _viewModel.Log("[UI] ROM path copied to clipboard.");
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Warning("Failed to copy ROM path: {Message}", ex.Message);
            }
        }

        private const int WmSettingChange = 0x001A;

        private void MainWindow_SourceInitialized(object sender, EventArgs e)
        {
            ApplyTitleBarTheme();

            // Windows announces a light/dark switch with WM_SETTINGCHANGE and
            // the "ImmersiveColorSet" area, so the Auto theme can follow along
            // without waiting for a restart.
            (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(WindowProc);

            // Close-button guard: while any device-writing operation runs
            // (IsFlashing — payload install, sideload, image/partition flashes,
            // wipes, restores), the title-bar X is disabled outright. Passive
            // states (device monitoring, probing, log-only activity) stay
            // closable. The Closing handler below remains as a backstop.
            UpdateCloseDisabledState();
            _viewModel.PropertyChanged += (s, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.IsFlashing) ||
                    string.IsNullOrEmpty(args.PropertyName))
                    UpdateCloseDisabledState();

                // A7: the 100% moment pops, the rest of the bar stays calm.
                else if (args.PropertyName == nameof(MainWindowViewModel.ProgressValue) &&
                         _viewModel.ProgressValue >= 100)
                    Motion.PopScale(PercentageText);

                // A3: the mode change gets a soft entrance, not a hard swap.
                // Synchronous for the same reason as the tab transition — a
                // deferred call paints the resting state first, then replays.
                else if (args.PropertyName == nameof(MainWindowViewModel.DeviceModeDisplay))
                    Motion.FadeSlideIn(SidebarModeText, 6);
            };
        }

        // A1: every tab page arrives the same way — it fades in while sliding
        // from the left toward the right (fromX -16 → 0), so all pages share
        // one left-to-right transition regardless of which tab you came from.
        // Gated by the motion setting.
        //
        // The call MUST be synchronous: the animation clock starts on the next
        // render pass, so the first painted frame of the new tab is already
        // transparent and offset. Deferring (BeginInvoke) let a frame render
        // at rest first — the page flashed, then replayed the slide; and when
        // the callback ran before the content was re-attached, the animation
        // ran against a detached element and the page simply popped in.
        private void MainTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (e.OriginalSource != sender || !Motion.Enabled) return;   // only the tab switch, not inner selections
            if (MainTabs.SelectedItem is System.Windows.Controls.TabItem tab &&
                tab.Content is FrameworkElement content)
                Motion.FadeSlideIn(content, 0, -16);
        }

        #region Close button guard (Win32 system menu)
        private const int SC_CLOSE = 0xF060;
        private const int MF_BYCOMMAND = 0x0;
        private const int MF_GRAYED = 0x1;
        private bool _closeDisabledApplied;

        [DllImport("user32.dll")]
        private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

        [DllImport("user32.dll")]
        private static extern bool EnableMenuItem(IntPtr hMenu, int uIDEnableItem, int uEnable);

        private void UpdateCloseDisabledState()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var menu = GetSystemMenu(hwnd, false);
            if (menu == IntPtr.Zero) return;
            var disable = _viewModel.IsFlashing;
            if (disable == _closeDisabledApplied) return;
            // MF_ENABLED is 0; gray the Close item so the X button renders
            // disabled and Alt+F4 is swallowed by the system.
            EnableMenuItem(menu, SC_CLOSE, MF_BYCOMMAND | (disable ? MF_GRAYED : 0));
            _closeDisabledApplied = disable;
        }
        #endregion

        private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmSettingChange &&
                ThemeManager.Instance.CurrentTheme == ThemeManager.ThemeMode.Auto)
            {
                var area = Marshal.PtrToStringUni(lParam);
                if (string.IsNullOrEmpty(area) || area == "ImmersiveColorSet")
                    ThemeManager.Instance.RefreshAutoTheme();
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// Windows draws the title bar, so it has to be told about the app theme
        /// separately from the WPF resource swap.
        /// </summary>
        private void ApplyTitleBarTheme()
        {
            var effective = ThemeManager.Instance.EffectiveTheme;
            WindowChrome.ApplyTitleBar(this,
                effective == ThemeManager.ThemeMode.Dark || effective == ThemeManager.ThemeMode.Oled);
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Gentle fade-in on startup.
            BeginAnimation(Window.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));

            // Windows (UIPI) silently blocks drag & drop from a non-elevated
            // Explorer into an elevated process. Say so once instead of leaving
            // the drag dead with no explanation.
            if (new System.Security.Principal.WindowsPrincipal(
                    System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                _viewModel.Log("[UI] Running as Administrator — Windows blocks drag & drop from Explorer " +
                               "into elevated apps. Use Browse instead, or restart the app normally.");
            }

            _viewModel.StartDeviceMonitoring();
            LoggingService.Instance.Information("Device monitoring started");

            // Fire-and-forget update check: throttled to once a day, silent on
            // failure (the About card on SETTINGS always offers CHECK NOW).
            var updateCheck = _viewModel.CheckForUpdatesAsync(manual: false);
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Guard against closing mid-flash: a half-written partition can
            // leave the device unbootable.
            if (_viewModel.IsFlashing)
            {
                var closeAnyway = ConfirmDialog.Show(
                    this,
                    "Flash in progress",
                    "A flash operation is in progress. Closing now may interrupt the flashing and leave the device unbootable.\n\n" +
                    "Close anyway and abort the operation?",
                    "Close anyway",
                    "Keep flashing");

                if (!closeAnyway)
                {
                    e.Cancel = true;
                    return;
                }

                _viewModel.AbortFlash();
            }

            // Persist window size/position (RestoreBounds covers maximized windows).
            var bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;
            _viewModel.SaveWindowBounds(bounds.Top, bounds.Left, bounds.Width, bounds.Height);

            _viewModel.Shutdown();
            LoggingService.Instance.Information("MainWindow closing");
        }

        // ROM Selection Tab - Recent files quick load
        private async void RecentFilesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressRecentSelection)
                return;

            var combo = sender as System.Windows.Controls.ComboBox;
            // Items are display names (file names); map the index back to the
            // full path in the view model.
            var idx = combo?.SelectedIndex ?? -1;
            var filePath = (idx >= 0 && idx < _viewModel.RecentFiles.Count)
                ? _viewModel.RecentFiles[idx]
                : null;

            // Reset the selection so picking the same entry again re-triggers.
            _suppressRecentSelection = true;
            combo.SelectedIndex = -1;
            _suppressRecentSelection = false;

            if (string.IsNullOrWhiteSpace(filePath))
                return;

            // Avoid re-validating (and re-adding) a file that is already selected.
            if (filePath == _viewModel.SelectedFilePath)
            {
                _viewModel.Log($"[Recent] {Path.GetFileName(filePath)} is already the selected ROM.");
                PathText.Text = filePath;
                return;
            }

            await _viewModel.LoadRecentFileAsync(filePath);
            PathText.Text = filePath;
        }

        // ROM Selection Tab - Browse Button
        // The whole drop zone is clickable now; these keep the button-less
        // zones from stacking two dialogs on a double-click and leave the
        // path text box alone (it has its own click behavior).
        private void RomDropZone_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount > 1 || e.OriginalSource is System.Windows.Controls.TextBox) return;
            BtnBrowse_Click(sender, e);
        }

        private void AdditionalDropZone_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount > 1 || e.OriginalSource is System.Windows.Controls.TextBox) return;
            BtnBrowseAdditional_Click(sender, e);
        }

        private async void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Title = "Select ROM File",
                Filter = "ROM Files (*.zip;*.tar;*.tar.gz;*.tgz)|*.zip;*.tar;*.tar.gz;*.tgz|All Files (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };

            if (openFileDialog.ShowDialog() == true)
            {
                var ok = await _viewModel.ValidateAndSelectRomAsync(openFileDialog.FileName);
                PathText.Text = ok ? openFileDialog.FileName : "Validation failed.";
                if (ok)
                {
                    RefreshRecentFilesUI();
                }
            }
        }

        private void RefreshRecentFilesUI()
        {
            // The ComboBox is bound to RecentFiles; force a refresh of the binding target.
            var binding = System.Windows.Data.BindingOperations.GetBindingExpression(
                RecentFilesCombo, System.Windows.Controls.ComboBox.ItemsSourceProperty);
            binding?.UpdateTarget();
        }

        // ROM Selection Tab - select every partition chip
        // ROM Selection Tab - select/clear every partition chip (R5)
        private void BtnSelectAllPartitions_Click(object sender, RoutedEventArgs e)
            => _viewModel.SetAllPartitions(true);

        // ROM Selection Tab - clear every partition chip
        private void BtnClearPartitions_Click(object sender, RoutedEventArgs e)
            => _viewModel.SetAllPartitions(false);

        // ROM Selection Tab - Reboot Button
        private async void BtnReboot_Click(object sender, RoutedEventArgs e)
        {
            if (!Confirm("Reboot the device to the bootloader?", "Confirm Reboot"))
                return;
            await _viewModel.RebootToBootloaderAsync();
        }

        // ROM Selection Tab - install a package that has to run from recovery
        private async void BtnInstallRomRecovery_Click(object sender, RoutedEventArgs e)
        {
            var wipeNote = _viewModel.ChkWipeDataBeforeFlash
                ? "\nWARNING: CLEAN INSTALL — ALL USER DATA (apps, photos, settings) WILL BE ERASED before the install."
                : "\n• Dirty install: user data (apps, photos, settings) is preserved.";

            var deviceNote = _viewModel.RomPackageNote;

            var message =
                "Install this ROM?\n\n" +
                $"• Package: {Path.GetFileName(_viewModel.SelectedFilePath)}\n" +
                $"• Type: {_viewModel.RomKindDisplay}\n" +
                $"• {_viewModel.RomPlanNote}" + wipeNote + "\n\n" +
                "The app will reboot the phone into recovery and apply the package there. " +
                "The recovery must support 'Apply update from ADB' (Lineage recovery or TWRP). " +
                "Do not unplug the cable until the device reports Done." +
                (string.IsNullOrEmpty(deviceNote) ? string.Empty : "\n\n" + deviceNote);

            if (!Confirm(message, "Confirm ROM Install"))
                return;
            await _viewModel.InstallSelectedRomViaSideloadAsync();
        }

        // ROM Selection Tab - Flash Button
        private async void BtnFlash_Click(object sender, RoutedEventArgs e)
        {
            // Flash-time battery guard: a popup informs the user when the
            // reading is below the recommended value — charge first or
            // continue at their own risk (applies to both the payload and
            // the partition-only route below).
            if (!_viewModel.ConfirmLowBatteryBeforeFlash())
                return;

            // Hard-brick guard: writing the boot chain to BOTH slots removes
            // the device's fallback slot. Warn once, offer the safer switch.
            if (!_viewModel.ConfirmBothSlotBootChainBeforeFlash())
                return;

            // Payload ROM: the primary action is the two-stage install — stage 1
            // flashes the boot chain over fastboot and reboots to recovery,
            // stage 2 sideloads the same package for the full install.
            // This is mode-independent: a payload package cannot be installed
            // any other way, so Expert mode gets the same correct flow (the
            // partition-only path is still available via POST-FLASH images).
            if (_viewModel.RomIsPayloadOta)
            {
                var sizeNote = string.IsNullOrWhiteSpace(_viewModel.RomFileSizeDisplay)
                    ? string.Empty
                    : "  (" + _viewModel.RomFileSizeDisplay + ")";
                var flashType = _viewModel.ChkWipeDataBeforeFlash
                    ? "CLEAN (all user data will be erased)"
                    : "Dirty (user data retained)";

                var twoStageMessage =
                    "Install this ROM?\n\n" +
                    $"• Package: {Path.GetFileName(_viewModel.SelectedFilePath)}{sizeNote}\n" +
                    $"• Flash Type: {flashType}\n" +
                    "• Stage 1: flash the boot-chain images (" +
                    string.Join(", ", _viewModel.GetFlashPartitions()) + ") over fastboot, then reboot to recovery\n" +
                    "• Stage 2: sideload this package so its installer writes system, vendor and product\n" +
                    "\n\n!!Do not unplug the cable at any point." +
                    (string.IsNullOrEmpty(_viewModel.RomPackageNote) ? string.Empty : "\n\n" + _viewModel.RomPackageNote);

                if (!Confirm(twoStageMessage, "Confirm ROM Install"))
                    return;
                await _viewModel.InstallPayloadRomAsync();
                return;
            }

            var partitions = string.Join(", ", _viewModel.GetFlashPartitions());
            var slotNote = _viewModel.SlotChoice == "Both"
                ? "\n• Target slots: BOTH (A and B)"
                : _viewModel.SlotChoice == "Auto" ? "\n• Target slot: active slot (auto)"
                : $"\n• Target slot: {_viewModel.SlotChoice}";

            var wipeNote = _viewModel.ChkWipeDataBeforeFlash
                ? "\nWARNING: CLEAN FLASH — ALL USER DATA (apps, photos, settings) WILL BE ERASED (fastboot -w) BEFORE flashing."
                : "\n• Dirty flash: user data (apps, photos, settings) is preserved.";

            var packageNote = _viewModel.RomPackageNote;
            var packageWarning = string.IsNullOrEmpty(packageNote) ? string.Empty : "\n\n" + packageNote;

            // In Expert mode a payload package can still be written partition by
            // partition — say plainly that this is not a ROM install.
            var routeNote = _viewModel.RomFastbootNotApplicable && _viewModel.IsExpertMode
                ? "\n\nEXPERT WARNING: this package's own installer will NOT run. " +
                  "Only the boot-chain partitions listed above are written; system, vendor and product are left untouched."
                : string.Empty;

            var message =
                $"Flash the selected package?\n\n" +
                $"• Type: {_viewModel.RomKindDisplay}\n" +
                $"• Partitions to overwrite: {partitions}" + slotNote + wipeNote + "\n" +
                $"• {_viewModel.RomCompatibilityText}\n" +
                $"• {_viewModel.RomIntegrityText}\n\n" +
                "Only the partitions listed above are overwritten.\n" +
                "For a clean install across ROM families, keep 'wipe data before flash' ticked." +
                packageWarning + routeNote;

            if (!Confirm(message, "Confirm Flash"))
                return;
            await _viewModel.FlashRomAsync();
        }

        // Additional Files Tab - Flash Additional Files
        private async void BtnAddAdditional_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Title = "Select Additional Image Files",
                Filter = "Image Files (*.img)|*.img|All Files (*.*)|*.*",
                Multiselect = true
            };

            if (openFileDialog.ShowDialog() != true)
                return;

            // Build and confirm the file -> partition mapping.
            var mapping = string.Join("\n", openFileDialog.FileNames.Select(f =>
                $"  {Path.GetFileName(f)}  ->  {Path.GetFileNameWithoutExtension(f).ToLowerInvariant()}"));

            if (!Confirm("This will flash the following files via fastboot. " +
                         "Each file name must match the target partition:\n\n" + mapping +
                         "\n\nContinue?", "Confirm Additional Flash"))
                return;

            await _viewModel.FlashAdditionalFilesAsync(openFileDialog.FileNames.ToList());
        }

        // Additional Files Tab - Reboot to Recovery
        private async void BtnRebootRecovery_Click(object sender, RoutedEventArgs e)
        {
            if (!Confirm("Reboot the device to recovery mode?", "Confirm Reboot"))
                return;
            await _viewModel.RebootToRecoveryAsync();
        }

        // Additional Files Tab - Wipe Cache
        private async void BtnRescue_Click(object sender, RoutedEventArgs e)
        {
            if (!Confirm("Wipe the cache partition? This may resolve boot issues but will clear cached data.", "Confirm Wipe Cache"))
                return;
            await _viewModel.WipeCacheAsync();
        }

        // Additional Files Tab - Finish & Reboot
        private async void BtnFinish_Click(object sender, RoutedEventArgs e)
        {
            if (!Confirm("Finish and reboot the device to system?", "Confirm Reboot"))
                return;
            await _viewModel.FinishAndRebootAsync();
        }

        // ---------- Welcome page: choice buttons slide the page left ----------
        private void WelcomeBtnRom_Click(object sender, RoutedEventArgs e)
        {
            AnimateWelcomeAway(() => _viewModel.EnterAppFlashRom());
        }

        private void WelcomeBtnFiles_Click(object sender, RoutedEventArgs e)
        {
            AnimateWelcomeAway(() => _viewModel.EnterAppFlashFiles());
        }

        private void WelcomeBtnSkip_Click(object sender, RoutedEventArgs e)
        {
            AnimateWelcomeAway(() => _viewModel.EnterAppSkip());
        }

        /// <summary>
        /// Smooth morph consistent with the sidebar toggle: the welcome page
        /// itself turns into the expanded sidebar. Its background panel shrinks
        /// from full width into the sidebar slot while its content fades out,
        /// the real sidebar fades in underneath it, and the main content area
        /// slides in gently from the right as it is revealed.
        /// </summary>
        private void AnimateWelcomeAway(Action onDone)
        {
            try
            {
                var page = WelcomePage;
                if (page == null) { onDone?.Invoke(); return; }
                page.IsHitTestVisible = false;

                var full = Math.Max(ActualWidth, 600);
                var ease = new System.Windows.Media.Animation.CubicEase
                    { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut };
                var dur = TimeSpan.FromMilliseconds(560);

                // Anchor the page to the left so it can shrink into the
                // sidebar's slot (the sidebar sits at column 0, 260px).
                page.HorizontalAlignment = HorizontalAlignment.Left;
                page.Width = full;

                // The real sidebar waits underneath, fully transparent.
                Sidebar.Visibility = Visibility.Visible;
                Sidebar.Opacity = 0;
                var sidebarIn = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(300),
                    BeginTime = TimeSpan.FromMilliseconds(260), EasingFunction = ease
                };
                Sidebar.BeginAnimation(UIElement.OpacityProperty, sidebarIn);

                // The main content slides in from the right as it is revealed.
                var contentTt = new System.Windows.Media.TranslateTransform(60, 0);
                MainContent.RenderTransform = contentTt;
                MainContent.Opacity = 0;
                var contentIn = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(340),
                    BeginTime = TimeSpan.FromMilliseconds(200), EasingFunction = ease
                };
                var contentSlide = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 60, To = 0, Duration = TimeSpan.FromMilliseconds(340),
                    BeginTime = TimeSpan.FromMilliseconds(200), EasingFunction = ease
                };
                MainContent.BeginAnimation(UIElement.OpacityProperty, contentIn);
                contentTt.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, contentSlide);

                // The welcome page shrinks into the sidebar slot while its
                // inner content fades away.
                var shrink = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = full, To = 260, Duration = dur, EasingFunction = ease
                };
                var pageContentOut = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 1, To = 0, Duration = TimeSpan.FromMilliseconds(260), EasingFunction = ease
                };
                shrink.Completed += (s2, e2) =>
                {
                    try
                    {
                        page.BeginAnimation(WidthProperty, null);
                        page.RenderTransform = null;
                        page.IsHitTestVisible = true;
                        Sidebar.BeginAnimation(UIElement.OpacityProperty, null);
                        Sidebar.Opacity = 1;
                        MainContent.BeginAnimation(UIElement.OpacityProperty, null);
                        MainContent.Opacity = 1;
                        MainContent.RenderTransform = null;
                    }
                    catch { }
                    // Collapses the welcome page (WelcomeVisibility) and sets
                    // the tab chosen by the user.
                    onDone?.Invoke();
                };
                page.BeginAnimation(WidthProperty, shrink);
                // Fade the whole welcome content (both columns) while the
                // page shrinks into the sidebar slot.
                foreach (var child in page.Children.OfType<UIElement>())
                    child.BeginAnimation(UIElement.OpacityProperty, pageContentOut);
            }
            catch
            {
                onDone?.Invoke();
            }
        }

        // Additional Files Tab - Emergency Abort. While an operation is running,
        // the user must confirm through a themed warning about the dangers of
        // cancelling mid-flash; outside an operation it aborts directly.
        private void BtnAbort_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.FooterAbortEnabled)
                FlashAbortConfirmWindow.Show(() => _viewModel.AbortFlash());
            else
                _viewModel.AbortFlash();
        }

        // After Care - copy the flash journal to the clipboard
        private void BtnCopyReport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(_viewModel.AfterCareBody);
                _viewModel.ShowToast("Success", "Report copied", "The flash journal is on the clipboard.");
            }
            catch (Exception ex)
            {
                _viewModel.Log($"[ERROR] Could not copy the report: {ex.Message}");
            }
        }

        // Toast - dismiss one notification
        private void ToastDismiss_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is MainWindowViewModel.ToastItem item)
                _viewModel.DismissToast(item);
        }

        /// <summary>
        /// Plays the toast's exit animation (slide out right + fade), then
        /// removes the item. With motion disabled the item is removed at once.
        /// Idempotent: the item's Closing flag guards double dismissals.
        /// </summary>
        private async System.Threading.Tasks.Task CloseToastWithAnimationAsync(MainWindowViewModel.ToastItem item)
        {
            if (item == null) return;
            var border = FindToastBorder(ToastHost.ItemContainerGenerator.ContainerFromItem(item));
            if (border != null && Utilities.Motion.Enabled)
            {
                Utilities.Motion.ToastOut(border);
                await System.Threading.Tasks.Task.Delay(220);
            }
            _viewModel.RemoveToast(item);
        }

        /// <summary>Find the toast's root Border inside its generated container.</summary>
        private static Border FindToastBorder(DependencyObject parent)
        {
            if (parent == null) return null;
            if (parent is Border b) return b;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var result = FindToastBorder(System.Windows.Media.VisualTreeHelper.GetChild(parent, i));
                if (result != null) return result;
            }
            return null;
        }

        // Batch Queue - Process
        private async void BtnProcessBatch_Click(object sender, RoutedEventArgs e)
        {
            if (!Confirm("This will flash all ROMs in the batch queue sequentially. Continue?", "Confirm Batch Process"))
                return;
            await _viewModel.ProcessBatchQueueAsync();
        }

        // Batch Queue - clear finished jobs
        private void BtnClearCompletedBatch_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.ClearCompletedBatchJobs();
        }

        // Batch Queue - Abort
        private void BtnAbortBatch_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.AbortBatch();
        }

        // Drag & drop - highlight valid ROM files while hovering.
        private static bool IsAdditionalFileDrop(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return false;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            var file = files?.FirstOrDefault();
            if (string.IsNullOrEmpty(file) || !File.Exists(file))
                return false;
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".img") return true;
            if (ext != ".zip") return false;
            var name = Path.GetFileName(file).ToLowerInvariant();
            return name.Contains("twrp") || name.Contains("orangefox") || name.Contains("ofx") ||
                   name.Contains("recovery") || name.Contains("kernelsu") || name.Contains("apatch") ||
                   name.Contains("magisk") || name.Contains("gapps") || name.Contains("nik");
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = (IsRomFileDrop(e) || IsAdditionalFileDrop(e))
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        // ROM drop zone - light up while a valid package hovers over it. The
        // events are left unhandled so Window_DragOver / Window_Drop remain the
        // single implementation of the actual validation.
        private void RomDropZone_DragOver(object sender, DragEventArgs e)
        {
            SetRomDropHighlight(IsRomFileDrop(e));
        }

        // Shared by DragLeave and Drop: either way, the drag is over.
        private void RomDropZone_ResetHighlight(object sender, DragEventArgs e)
        {
            SetRomDropHighlight(false);
        }

        /// <summary>
        /// Swap the drop zone between its idle and “drop here” look, using
        /// resource references so the highlight follows theme changes. DragOver
        /// fires continuously, so nothing happens unless the state really flips.
        /// </summary>
        private void SetRomDropHighlight(bool active)
        {
            if (_romDropHighlighted == active)
                return;

            _romDropHighlighted = active;
            var outline = RomDropZoneOutline;
            if (outline != null)
            {
                outline.SetResourceReference(System.Windows.Shapes.Rectangle.StrokeProperty,
                    active ? "AccentRed" : "BorderMedium");
                outline.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty,
                    active ? "DangerTintBg" : "ControlInputBg");
                outline.StrokeThickness = active ? 2 : 1.5;
            }
        }

        private static bool IsRomFileDrop(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return false;

            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            var file = files?.FirstOrDefault();
            if (string.IsNullOrEmpty(file) || !File.Exists(file))
                return false;

            var ext = Path.GetExtension(file).ToLowerInvariant();

            // Only ROM packages the validator accepts are droppable. .img files
            // belong to the POST-FLASH tab, so the drag cursor refuses them
            // instead of accepting the drop and failing validation afterwards.
            return ext == ".zip" || ext == ".tar" || ext == ".tgz" ||
                   (ext == ".gz" && file.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase));
        }

        // Drag & drop - validate and select the dropped ROM file.
        private async void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            var file = files?.FirstOrDefault();
            if (string.IsNullOrEmpty(file) || !File.Exists(file))
                return;

            var ext = Path.GetExtension(file).ToLowerInvariant();
            var name = Path.GetFileName(file).ToLowerInvariant();

            // 3.1: route by kind. Additional-file candidates (recovery
            // images, installer/root/add-on zips) go to the ADDITIONAL FILES
            // slot; everything ROM-shaped goes through the ROM validator.
            var additionalLike = ext == ".img" ||
                (ext == ".zip" &&
                 (name.Contains("twrp") || name.Contains("orangefox") || name.Contains("ofx") ||
                  name.Contains("recovery") || name.Contains("kernelsu") || name.Contains("apatch") ||
                  name.Contains("magisk") || name.Contains("gapps") || name.Contains("nik")));
            if (additionalLike)
            {
                _viewModel.SetAdditionalFile(file);
                return;
            }

            if (ext == ".zip" || ext == ".tar" || ext == ".tgz" ||
                (ext == ".gz" && file.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)))
            {
                var ok = await _viewModel.ValidateAndSelectRomAsync(file);
                PathText.Text = ok ? file : "Validation failed.";
                if (ok)
                {
                    RefreshRecentFilesUI();
                }
                return;
            }

            _viewModel.SetAdditionalFile(file);
        }

        // Additional Files Tab - Apply update via ADB sideload
        private async void BtnSideload_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Title = "Select Update Package for Sideload (GAPPS, Magisk, kernel zip…)",
                Filter = "Update Packages (*.zip)|*.zip|All Files (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };

            if (openFileDialog.ShowDialog() != true)
                return;

            var modeNote = _viewModel.ChkWipeDataBeforeFlash ? "" :
                "\n\nIf the device is not in sideload mode yet, the app will reboot it " +
                "there automatically (system → recovery → sideload).";

            if (!Confirm($"Apply '{Path.GetFileName(openFileDialog.FileName)}' via ADB sideload?" + modeNote,
                         "Confirm Sideload"))
                return;

            await _viewModel.SideloadUpdateAsync(openFileDialog.FileName, allowRebootChain: true);
        }

        // ==================== Custom recovery & migration (R1/R2) ====================

        // Custom Recovery - browse for a recovery .img (or .zip sideload package)
        private void BtnBrowseRecovery_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Title = "Select Recovery Image (TWRP / OrangeFox)",
                Filter = "Recovery Images (*.img;*.zip)|*.img;*.zip|All Files (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };

            if (openFileDialog.ShowDialog() == true)
            {
                _viewModel.RecoveryFilePath = openFileDialog.FileName;
            }
        }

        // ---------- ADDITIONAL FILES: unified drop/browse file slot ----------
        private void AdditionalDropZone_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            var outline = AdditionalDropZoneOutline;
            if (outline != null)
            {
                outline.SetResourceReference(System.Windows.Shapes.Rectangle.StrokeProperty, "AccentRed");
                outline.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "DangerTintBg");
                outline.StrokeThickness = 2;
            }
        }

        private void AdditionalDropZone_ResetHighlight(object sender, DragEventArgs e)
        {
            var outline = AdditionalDropZoneOutline;
            if (outline != null)
            {
                outline.SetResourceReference(System.Windows.Shapes.Rectangle.StrokeProperty, "BorderMedium");
                outline.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "ControlInputBg");
                outline.StrokeThickness = 1.5;
            }
        }

        private void AdditionalDropZone_Drop(object sender, DragEventArgs e)
        {
            AdditionalDropZone_ResetHighlight(sender, e);
            e.Handled = true; // keep the window-level ROM drop handler out of this zone
            try
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                    _viewModel.SetAdditionalFile(files[0]);
            }
            catch (Exception ex)
            {
                Services.LoggingService.Instance.Warning("Additional drop failed: {Message}", ex.Message);
            }
        }

        private void BtnBrowseAdditional_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Add a ZIP package or an IMG image",
                Filter = "Packages & images (*.zip;*.img)|*.zip;*.img|All Files (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dlg.ShowDialog() == true)
                _viewModel.SetAdditionalFile(dlg.FileName);
        }

        // Sideload the added .zip (GAPPS, Magisk, OTA...).
        private async void BtnApplyAdditionalZip_Click(object sender, RoutedEventArgs e)
        {
            var path = _viewModel.RecoveryFilePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
                !Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                return;
            if (!Confirm($"Sideload '{Path.GetFileName(path)}'?\n\n" +
                         "The app reboots through recovery into sideload by itself. Follow any on-device prompts.",
                         "Confirm Sideload"))
                return;
            await _viewModel.SideloadUpdateAsync(path, allowRebootChain: true);
        }

        // A/B repair: read-what-is-there, flash the zip's recovery.img to both
        // recovery partitions, verify by readback. For the case where a
        // sideloaded installer reports success but the device still boots the
        // old recovery.
        private async void BtnFlashRecoveryBothSlots_Click(object sender, RoutedEventArgs e)
        {
            await _viewModel.FlashRecoveryBothSlotsAsync();
        }

        // One-press image flash: writes the added .img to the partition its
        // name indicates (boot.img -> boot, magisk_patched -> boot, ...).
        private async void BtnFlashAdditionalImage_Click(object sender, RoutedEventArgs e)
        {
            var path = _viewModel.RecoveryFilePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
                !Path.GetExtension(path).Equals(".img", StringComparison.OrdinalIgnoreCase))
                return;
            var partition = _viewModel.AdditionalImagePartition;
            var n = Path.GetFileName(path).ToLowerInvariant();
            var isRootBoot = partition.Equals("boot", StringComparison.OrdinalIgnoreCase) &&
                (n.Contains("magisk") || n.Contains("patched") || n.Contains("root") || n.Contains("kernelsu") || n.Contains("apatch"));
            var extra = isRootBoot
                ? "\n\nThis looks like a PATCHED ROOT image. It must come from the boot image of the ROM " +
                  "currently installed on the phone — a boot image patched from a different ROM can leave " +
                  "the phone unable to boot. If you just installed a new ROM, let it boot to system first, " +
                  "patch that ROM's boot image in the Magisk app, then flash the result."
                : "";
            if (_viewModel.AdditionalImageBothSlots && partition.Equals("boot", StringComparison.OrdinalIgnoreCase))
                extra += "\n\nBOTH SLOTS: the image is written to boot_a AND boot_b — root survives the next OTA slot flip.";
            if (!Confirm($"Flash '{Path.GetFileName(path)}' to the '{partition}' partition?{extra}\n\n" +
                         "Requires the device in bootloader mode with an unlocked bootloader.",
                         isRootBoot ? "Confirm Root Boot Flash" : "Confirm Image Flash"))
                return;
            await _viewModel.FlashDroppedImageAsync(isRootBoot: isRootBoot);
        }

        // P1: temporarily boot a custom recovery image (writes nothing).
        private async void BtnBootRecoveryImage_Click(object sender, RoutedEventArgs e)
        {
            var path = _viewModel.RecoveryFilePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;
            await _viewModel.BootRecoveryImageAsync();
        }

        // P1: permanently flash a custom recovery image (dedicated recovery
        // partition devices only — A/B devices are refused with guidance).
        private async void BtnFlashRecoveryImage_Click(object sender, RoutedEventArgs e)
        {
            var path = _viewModel.RecoveryFilePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;
            if (!Confirm("Install this image as the PERMANENT recovery?\n\n" +
                         "It replaces the existing recovery until you flash something else over it.",
                         "Confirm Recovery Flash"))
                return;
            await _viewModel.FlashRecoveryImageAsync();
        }

        // P3: direct zip install through the twrp CLI (no sideload session).
        private async void BtnInstallZipCli_Click(object sender, RoutedEventArgs e)
        {
            var path = _viewModel.RecoveryFilePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;
            await _viewModel.InstallZipViaRecoveryCliAsync();
        }

        // P3: image flash through the twrp CLI (no bootloader round-trip).
        private async void BtnFlashImageCli_Click(object sender, RoutedEventArgs e)
        {
            var path = _viewModel.RecoveryFilePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;
            var partition = _viewModel.AdditionalImagePartition;
            if (!Confirm("Write '" + Path.GetFileName(path) + "' to the '" + partition + "' partition via the twrp CLI?",
                         "Confirm Image Flash (Recovery CLI)"))
                return;
            await _viewModel.InstallImageViaRecoveryCliAsync();
        }

        // Settings page: gear icon opens it.
        private void BtnOpenSettings_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.SelectedTabIndex = 4;
        }

        // Settings page: theme chips.
        private void ThemeChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.RadioButton rb && rb.Tag is string tag)
                _viewModel.SetTheme(tag);
        }

        // 4.1: refresh the DEVICE dashboard.
        private async void BtnRefreshDashboard_Click(object sender, RoutedEventArgs e)
        {
            await _viewModel.RefreshDeviceDashboardAsync();
        }

        // 4.4: extract the package's loose boot-chain images for Magisk patching.
        private async void BtnExtractBootImage_Click(object sender, RoutedEventArgs e)
        {
            await _viewModel.ExtractBootImagesAsync();
        }

        // 3.2: copy a pasteable support bundle to the clipboard.
        private void BtnCopyDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(_viewModel.BuildDiagnostics());
                _viewModel.ShowToast("Success", "Diagnostics copied",
                    "App version, device state, backups and the log tail are on the clipboard — paste them where help is asked.");
            }
            catch (Exception ex)
            {
                Services.LoggingService.Instance.Warning("Diagnostics copy failed: {Message}", ex.Message);
            }
        }

        // 3.6: open the device's PC backup folder.
        private void BtnOpenBackupsFolder_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.OpenBackupsFolder();
        }

        // Update checker: manual check goes online immediately; OPEN RELEASES
        // PAGE appears only when a newer version was found.
        private async void BtnCheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            await _viewModel.CheckForUpdatesAsync(manual: true);
        }

        private void BtnOpenReleases_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.OpenReleasesPage();
        }

        // 3.6: delete all but the newest backup set.
        private void BtnDeleteOldBackups_Click(object sender, RoutedEventArgs e)
        {
            if (!Confirm("Delete all stock-recovery backups EXCEPT the newest set?\n\n" +
                         "The newest set stays restorable; older sets are removed from the PC.",
                         "Confirm Delete"))
                return;
            var removed = _viewModel.DeleteOldBackups();
            if (removed == 0)
                _viewModel.ShowToast("Info", "Nothing to delete", "There are no older backup sets for this device.");
        }

        // 3.4: re-select a recently added additional file.
        private void AdditionalRecentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0)
                return;
            var path = e.AddedItems[0] as string;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;
            _viewModel.SetAdditionalFile(path);
            AdditionalRecentCombo.SelectedItem = null;
        }

        // Re-run the recovery capability probe (state may have changed
        // while the device stayed in recovery).
        private async void BtnProbeRecheck_Click(object sender, RoutedEventArgs e)
        {
            await _viewModel.RecheckRecoveryProbeAsync();
        }

        // Rescue tools (fastboot/adb, small and targeted).
        private async void BtnEraseFrp_Click(object sender, RoutedEventArgs e)
        {
            if (!Confirm("Erase the FRP partition?\n\n" +
                         "This clears the Google account lock marker. Only do this on a device you own " +
                         "and are repurposing.", "Confirm FRP Erase"))
                return;
            await _viewModel.EraseFrpAsync();
        }

        private async void BtnEraseMetadata_Click(object sender, RoutedEventArgs e)
        {
            if (!Confirm("Erase the metadata partition?\n\n" +
                         "This removes encryption keys and FBE state — the ROM re-encrypts fresh on next boot. " +
                         "Data that is currently encrypted becomes unreadable.", "Confirm Metadata Wipe"))
                return;
            await _viewModel.EraseMetadataAsync();
        }

        private async void BtnEraseMisc_Click(object sender, RoutedEventArgs e)
        {
            if (!Confirm("Erase the misc partition?\n\n" +
                         "This clears the pending boot-into-recovery command — use it when the phone is stuck " +
                         "in a recovery boot loop.", "Confirm Misc Erase"))
                return;
            await _viewModel.EraseMiscAsync();
        }

        private async void BtnSwitchSlot_Click(object sender, RoutedEventArgs e)
        {
            if (!Confirm("Switch the active A/B slot?\n\n" +
                         "The next boot starts from the OTHER slot — the standard rescue when the current slot does not boot.",
                         "Confirm Slot Switch"))
                return;
            await _viewModel.SwitchActiveSlotAsync();
        }

        private async void BtnBootFastbootd_Click(object sender, RoutedEventArgs e)
        {
            await _viewModel.BootFastbootdAsync();
        }

        // P5: TWRP boot backup/restore via the twrp CLI.
        private async void BtnTwrpBackupBoot_Click(object sender, RoutedEventArgs e)
        {
            await _viewModel.BackupBootViaTwrpAsync();
        }

        private async void BtnTwrpRestoreBoot_Click(object sender, RoutedEventArgs e)
        {
            if (!Confirm("Restore the newest SRF-boot TWRP backup?\n\n" +
                         "This writes the backed-up boot image (kernel + recovery ramdisk) back to the device.",
                         "Confirm Boot Restore"))
                return;
            await _viewModel.RestoreBootViaTwrpAsync();
        }

        // Stock recovery backup restore (safety net).
        private async void BtnRestoreStockRecovery_Click(object sender, RoutedEventArgs e)
        {
            if (!Confirm("Restore the backed-up stock recovery?\n\n" +
                         "This replaces the currently installed recovery with the backed-up one.",
                         "Confirm Restore"))
                return;
            await _viewModel.RestoreStockRecoveryAsync();
        }

        // Reboot to bootloader from this page.
        private async void BtnRebootBootloaderAdditional_Click(object sender, RoutedEventArgs e)
        {
            if (!Confirm("Reboot the device to the bootloader?", "Confirm Reboot"))
                return;
            await _viewModel.RebootToBootloaderAsync();
        }

        // Reboot to system from this page.
        private async void BtnRebootSystemAdditional_Click(object sender, RoutedEventArgs e)
        {
            if (!Confirm("Reboot the device to the operating system?", "Confirm Reboot"))
                return;
            await _viewModel.FinishAndRebootAsync();
        }

        // Sideload the .zip currently selected in the recovery picker (GAPPS/Magisk/add-on).
        private async void BtnSideloadRecoveryZip_Click(object sender, RoutedEventArgs e)
        {
            var path = _viewModel.RecoveryFilePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
                !Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                Dialogs.ConfirmDialog.Show(this, "Select a ZIP package",
                    "Select a .zip package first. Recovery .img files are booted or flashed, not sideloaded.",
                    "OK", null, danger: false);
                return;
            }

            if (!Confirm($"Sideload '{Path.GetFileName(path)}'?\\n\\n" +
                         "The app may reboot through recovery into sideload. Follow any on-device prompts if your recovery requires them.",
                         "Confirm ZIP Sideload"))
                return;

            await _viewModel.SideloadUpdateAsync(path, allowRebootChain: true);
        }

        // Custom Recovery - temp-boot the selected recovery image (fastboot boot)
        private async void BtnBootRecovery_Click(object sender, RoutedEventArgs e)
        {
            if (!Confirm($"Boot '{Path.GetFileName(_viewModel.RecoveryFilePath)}' temporarily via fastboot boot?",
                         "Confirm Boot Recovery"))
                return;

            await _viewModel.BootRecoveryImageAsync();
        }

        // Custom Recovery - flash the selected recovery image permanently
        private async void BtnFlashRecovery_Click(object sender, RoutedEventArgs e)
        {
            if (!Confirm($"Install '{Path.GetFileName(_viewModel.RecoveryFilePath)}' to the recovery partition?\n\n" +
                         "Note: on A/B devices only the active slot's recovery is replaced.",
                         "Confirm Flash Recovery"))
                return;

            await _viewModel.FlashRecoveryImageAsync();
        }

        // Sideload entry - reboot the ADB device into sideload mode
        private async void BtnEnterSideload_Click(object sender, RoutedEventArgs e)
        {
            if (!Confirm("Reboot the device into sideload mode (recovery → 'Apply update from ADB')?",
                         "Confirm Enter Sideload"))
                return;

            await _viewModel.EnterSideloadModeAsync();
        }

        // Migration safety - full factory wipe (fastboot -w). Double confirmation.
        private async void BtnWipeData_Click(object sender, RoutedEventArgs e)
        {
            if (!Confirm("WIPE ALL USER DATA?\n\n" +
                         "This erases userdata AND cache (fastboot -w). Apps, photos and settings\n" +
                         "are gone and CANNOT be recovered. This is required when switching\n" +
                         "between ROM families (AOSP <-> HyperOS/MIUI).",
                         "CONFIRM FACTORY WIPE"))
                return;

            if (!Confirm("Last chance — really erase everything on the device?",
                         "FINAL CONFIRMATION"))
                return;

            await _viewModel.FactoryWipeAsync();
        }

        // Batch queue - move job up
        private void BtnJobUp_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is RomFlashJob job)
                _viewModel.MoveJobInBatch(job.Id, -1);
        }

        // Batch queue - move job down
        private void BtnJobDown_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is RomFlashJob job)
                _viewModel.MoveJobInBatch(job.Id, +1);
        }

        // Batch queue - remove job
        private void BtnJobRemove_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is RomFlashJob job)
                _viewModel.RemoveJobFromBatch(job.Id);
        }

        // Toolbar - Clear Logs
        private void BtnClearLogs_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.ClearLogs();
        }

        /// <summary>
        /// Show a confirmation dialog and return true if the user clicks Yes.
        /// </summary>
        private bool Confirm(string message, string title = "Confirm")
        {
            // Themed + owned, unlike MessageBox: it follows the app theme and
            // stays centered on this window instead of appearing unowned.
            return ConfirmDialog.Show(this, title, message, "Yes", "No");
        }

        // Log context menu - copy the currently selected log entries.
        private void MenuCopySelected_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selected = LogList.SelectedItems?.Cast<object>().Select(o => o?.ToString())?.ToList();
                var text = string.Join(Environment.NewLine, selected ?? Enumerable.Empty<string>());
                if (!string.IsNullOrEmpty(text))
                {
                    Clipboard.SetText(text);
                    _viewModel.Log($"[UI] Copied {(selected?.Count ?? 0)} log line(s) to the clipboard.");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Warning("Failed to copy selected log entries: {Message}", ex.Message);
            }
        }

        // Log context menu - copy the entire (filtered) log.
        private void MenuCopyAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var text = string.Join(Environment.NewLine, _viewModel.FilteredLogs);
                if (!string.IsNullOrEmpty(text))
                {
                    Clipboard.SetText(text);
                    _viewModel.Log($"[UI] Copied the whole log ({_viewModel.FilteredLogs.Count} line(s)) to the clipboard.");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Warning("Failed to copy log: {Message}", ex.Message);
            }
        }
    }

    /// <summary>
    /// Themed, non-modal "RECOVERY BOOTED" guidance window. Matches the active
    /// app theme (dark / light / OLED) via dynamic resource references and
    /// closes ITSELF when the view-model raises SideloadGuidanceClosed — i.e.
    /// the moment the app detects sideload mode on the device. A GOT IT button
    /// lets the user dismiss it early.
    /// </summary>
    /// <summary>
    /// Gives a code-built popup the app's chrome: no OS title bar — a themed
    /// header strip (panel background, primary title text, ghost close button)
    /// that is draggable and closes like a normal window.
    /// </summary>
    public static class ThemedPopupChrome
    {
        public static void Apply(Window window, string title)
        {
            try
            {
                window.WindowStyle = WindowStyle.None;

                var header = new System.Windows.Controls.Border { Height = 34 };
                header.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "PanelBackground");
                header.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "BorderMedium");
                header.BorderThickness = new Thickness(0, 0, 0, 1);
                header.MouseLeftButtonDown += (s, e) =>
                {
                    try { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) window.DragMove(); }
                    catch { }
                };

                var titleText = new System.Windows.Controls.TextBlock
                {
                    Text = title,
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(14, 0, 0, 0)
                };
                titleText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextPrimary");

                var close = new Button
                {
                    Content = "✕",
                    Width = 30,
                    Height = 24,
                    Padding = new Thickness(0),
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                close.SetResourceReference(Button.StyleProperty, "GhostButton");
                close.Click += (s, e) => { try { window.Close(); } catch { } };

                var headerGrid = new System.Windows.Controls.Grid();
                headerGrid.Children.Add(titleText);
                headerGrid.Children.Add(close);
                header.Child = headerGrid;

                var existing = window.Content as UIElement;
                var outer = new System.Windows.Controls.Grid();
                outer.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
                outer.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                System.Windows.Controls.Grid.SetRow(header, 0);
                System.Windows.Controls.Grid.SetRow(existing, 1);
                outer.Children.Add(header);
                outer.Children.Add(existing);
                window.Content = outer;
            }
            catch (Exception ex)
            {
                Services.LoggingService.Instance.Warning("Themed chrome failed: {Message}", ex.Message);
            }
        }
    }

    public static class SideloadGuidanceWindow
    {
        private static Window _open;

        public static void Show(ViewModels.MainWindowViewModel vm)
        {
            try
            {
                // One guidance window at a time.
                _open?.Close();
                _open = null;

                var owner = Application.Current?.MainWindow;
                if (owner == null || vm == null) return;

                var icon = new System.Windows.Controls.TextBlock
                {
                    Text = "", // checkmark circle
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    FontSize = 22,
                    FontWeight = FontWeights.Bold
                };
                icon.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "SuccessGreen");

                var title = new System.Windows.Controls.TextBlock
                {
                    Text = "RECOVERY BOOTED",
                    FontSize = 15,
                    FontWeight = FontWeights.Bold
                };
                title.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextPrimary");

                var body = new System.Windows.Controls.TextBlock
                {
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Text = "On the phone screen choose:\n\n        Apply update  ->  Apply from ADB"
                };
                body.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextPrimary");

                var sub = new System.Windows.Controls.TextBlock
                {
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 10, 0, 0),
                    Text = "This dialog closes automatically the moment the app detects sideload - the ROM then installs by itself. Stay on this screen."
                };
                sub.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextSecondary");

                var dismiss = new Button
                {
                    Content = "GOT IT",
                    Height = 30,
                    FontSize = 11,
                    Padding = new Thickness(18, 0, 18, 0),
                    Margin = new Thickness(0, 16, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                dismiss.SetResourceReference(Button.StyleProperty, "GhostButton");

                var header = new System.Windows.Controls.StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                header.Children.Add(icon);
                header.Children.Add(new System.Windows.Controls.TextBlock { Text = "   " });
                header.Children.Add(title);

                var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(24, 20, 24, 20) };
                panel.Children.Add(header);
                panel.Children.Add(new System.Windows.Controls.TextBlock { Text = " " });
                panel.Children.Add(body);
                panel.Children.Add(sub);
                panel.Children.Add(dismiss);

                var card = new System.Windows.Controls.Border
                {
                    Child = panel,
                    CornerRadius = new CornerRadius(10),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(0)
                };
                card.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "PanelBackground");
                card.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "SuccessGreen");

                var win = new Window
                {
                    Title = "RECOVERY BOOTED",
                    Content = card,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    ShowInTaskbar = false,
                    MaxWidth = 380
                };
                win.SetResourceReference(Window.BackgroundProperty, "WindowBackground");

                void CloseNow()
                {
                    try { win.Close(); } catch { }
                }
                vm.SideloadGuidanceClosed += CloseNow;
                win.Closed += (s, e) =>
                {
                    vm.SideloadGuidanceClosed -= CloseNow;
                    if (ReferenceEquals(_open, win)) _open = null;
                };
                dismiss.Click += (s, e) => CloseNow();

                if (owner != null && win.Owner != owner) win.Owner = owner;
                ThemedPopupChrome.Apply(win, "RECOVERY BOOTED");
                _open = win;
                win.Show();
            }
            catch (Exception ex)
            {
                Services.LoggingService.Instance.Warning("Guidance window failed: {Message}", ex.Message);
            }
        }
    }

    /// <summary>
    /// Themed confirmation shown when the user presses ABORT while an operation
    /// is running. Warns about the dangers of cancelling mid-flash; "KEEP
    /// FLASHING" is the default (Enter/Escape both keep the flash running).
    /// Matches the active app theme via dynamic resource references.
    /// </summary>
    public static class FlashAbortConfirmWindow
    {
        private static Window _open;

        public static void Show(Action onConfirm)
        {
            try
            {
                _open?.Close();
                _open = null;

                var owner = Application.Current?.MainWindow;
                if (owner == null) { onConfirm?.Invoke(); return; }

                var title = new System.Windows.Controls.TextBlock
                {
                    Text = "CANCEL THE FLASH?",
                    FontSize = 15,
                    FontWeight = FontWeights.Bold
                };
                title.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "WarningAmber");

                var warn = new System.Windows.Controls.TextBlock
                {
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 12, 0, 0),
                    Text = "Cancelling mid-flash can leave the device in an incomplete state:"
                };
                warn.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextPrimary");

                var bullets = new System.Windows.Controls.TextBlock
                {
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0),
                    Text = "    -  a half-written boot chain can stop the phone from booting\n    -  a partial ROM install may need the whole flash redone\n    -  the phone may stay stuck on the boot logo or in recovery"
                };
                bullets.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextSecondary");

                var advice = new System.Windows.Controls.TextBlock
                {
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 10, 0, 0),
                    Text = "Only cancel if the operation is clearly stuck. Otherwise let it finish."
                };
                advice.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "WarningAmber");

                var keep = new Button
                {
                    Content = "KEEP FLASHING",
                    Height = 30,
                    FontSize = 11,
                    Padding = new Thickness(18, 0, 18, 0),
                    IsDefault = true,
                    IsCancel = true   // Enter and Escape both keep the flash running
                };
                keep.SetResourceReference(Button.StyleProperty, "GhostButton");

                var abort = new Button
                {
                    Content = "ABORT ANYWAY",
                    Height = 30,
                    FontSize = 11,
                    Padding = new Thickness(18, 0, 18, 0),
                    Margin = new Thickness(12, 0, 0, 0)
                };
                abort.SetResourceReference(Button.StyleProperty, "DangerButton");

                var buttons = new System.Windows.Controls.StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 18, 0, 0)
                };
                buttons.Children.Add(keep);
                buttons.Children.Add(abort);

                var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(24, 20, 24, 20), MaxWidth = 420 };
                panel.Children.Add(title);
                panel.Children.Add(warn);
                panel.Children.Add(bullets);
                panel.Children.Add(advice);
                panel.Children.Add(buttons);

                var card = new System.Windows.Controls.Border
                {
                    Child = panel,
                    CornerRadius = new CornerRadius(10),
                    BorderThickness = new Thickness(1)
                };
                card.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "PanelBackground");
                card.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "DangerRed");

                var win = new Window
                {
                    Title = "CANCEL THE FLASH?",
                    Content = card,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    ShowInTaskbar = false,
                    MaxWidth = 460
                };
                win.SetResourceReference(Window.BackgroundProperty, "WindowBackground");

                void CloseOnly()
                {
                    try { win.Close(); } catch { }
                }
                keep.Click += (s2, e2) => CloseOnly();
                abort.Click += (s2, e2) =>
                {
                    try { win.Close(); } catch { }
                    onConfirm?.Invoke();
                };

                win.Owner = owner;
                ThemedPopupChrome.Apply(win, "CANCEL THE FLASH?");
                _open = win;
                win.Show();
            }
            catch (Exception ex)
            {
                Services.LoggingService.Instance.Warning("Abort confirm window failed: {Message}", ex.Message);
                onConfirm?.Invoke();
            }
        }
    }
}
