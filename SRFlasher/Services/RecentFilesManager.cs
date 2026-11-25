using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SRFlasher.Services
{
    /// <summary>
    /// Manages recent ROM files for quick access to recently flashed files.
    /// </summary>
    public class RecentFilesManager
    {
        private static RecentFilesManager _instance;
        private readonly LoggingService _logger;
        private readonly int _maxRecentFiles = 10;
        private List<string> _recentFiles;

        private RecentFilesManager()
        {
            _logger = LoggingService.Instance;
            LoadRecentFiles();
            LoadRecentAdditional();
        }

        public static RecentFilesManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (typeof(RecentFilesManager))
                    {
                        if (_instance == null)
                        {
                            _instance = new RecentFilesManager();
                        }
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// Get list of recent ROM files.
        /// </summary>
        public List<string> GetRecentFiles()
        {
            // Filter out files that no longer exist
            var validFiles = _recentFiles.Where(f => File.Exists(f)).ToList();

            if (validFiles.Count != _recentFiles.Count)
            {
                _recentFiles = validFiles;
                SaveRecentFiles();
            }

            return validFiles.Take(_maxRecentFiles).ToList();
        }

        /// <summary>
        /// Add a file to recent files list (moves to top if already exists).
        /// </summary>
        public void AddRecentFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    _logger.Warning("Cannot add non-existent file to recent list: {FilePath}", filePath);
                    return;
                }

                // Remove if already exists
                _recentFiles.Remove(filePath);

                // Add to beginning
                _recentFiles.Insert(0, filePath);

                // Keep only max items
                if (_recentFiles.Count > _maxRecentFiles)
                {
                    _recentFiles = _recentFiles.Take(_maxRecentFiles).ToList();
                }

                SaveRecentFiles();
                _logger.Debug("Added to recent files: {FileName}", Path.GetFileName(filePath));
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to add recent file: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// Remove a file from recent files.
        /// </summary>
        public void RemoveRecentFile(string filePath)
        {
            if (_recentFiles.Remove(filePath))
            {
                SaveRecentFiles();
                _logger.Debug("Removed from recent files: {FileName}", Path.GetFileName(filePath));
            }
        }

        /// <summary>
        /// Clear all recent files.
        /// </summary>
        public void ClearRecentFiles()
        {
            _recentFiles.Clear();
            SaveRecentFiles();
            _logger.Information("Recent files cleared");
        }

        // ==================== Additional-file recents (3.4) ====================
        private List<string> _recentAdditional = new List<string>();

        /// <summary>
        /// Recents for the ADDITIONAL FILES slot (recovery images, installer
        /// zips, root manager zips, payload images) — separate from ROM
        /// recents, persisted the same way.
        /// </summary>
        public List<string> GetRecentAdditional()
        {
            var valid = _recentAdditional.Where(f => File.Exists(f)).ToList();
            if (valid.Count != _recentAdditional.Count)
            {
                _recentAdditional = valid;
                SaveRecentAdditional();
            }
            return valid.Take(_maxRecentFiles).ToList();
        }

        public void AddRecentAdditional(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
                _recentAdditional.Remove(filePath);
                _recentAdditional.Insert(0, filePath);
                if (_recentAdditional.Count > _maxRecentFiles)
                    _recentAdditional = _recentAdditional.Take(_maxRecentFiles).ToList();
                SaveRecentAdditional();
            }
            catch (Exception ex)
            {
                _logger.Warning("Failed to save additional recents: {Message}", ex.Message);
            }
        }

        private void LoadRecentAdditional()
        {
            try
            {
                _recentAdditional = new List<string>();
                var raw = Properties.Settings.Default["RecentAdditional"] as string ?? string.Empty;
                if (!string.IsNullOrEmpty(raw))
                    _recentAdditional = raw.Split(new[] { "|" }, StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            catch
            {
                _recentAdditional = new List<string>();
            }
        }

        private void SaveRecentAdditional()
        {
            try
            {
                Properties.Settings.Default["RecentAdditional"] = string.Join("|", _recentAdditional);
                Properties.Settings.Default.Save();
            }
            catch (Exception ex)
            {
                _logger.Warning("Failed to save additional recents: {Message}", ex.Message);
            }
        }

        private void LoadRecentFiles()
        {
            try
            {
                _recentFiles = new List<string>();
                string recentFilesStr = Properties.Settings.Default["RecentFiles"] as string ?? string.Empty;

                if (!string.IsNullOrEmpty(recentFilesStr))
                {
                    var files = recentFilesStr.Split(new[] { "|" }, StringSplitOptions.RemoveEmptyEntries);
                    _recentFiles = files.ToList();
                }

                _logger.Debug("Loaded {Count} recent files", _recentFiles.Count);
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to load recent files: {Message}", ex.Message);
                _recentFiles = new List<string>();
            }
        }

        private void SaveRecentFiles()
        {
            try
            {
                string recentFilesStr = string.Join("|", _recentFiles);
                Properties.Settings.Default["RecentFiles"] = recentFilesStr;
                Properties.Settings.Default.Save();
            }
            catch (Exception ex)
            {
                _logger.Warning("Failed to save recent files: {Message}", ex.Message);
            }
        }
    }
}
