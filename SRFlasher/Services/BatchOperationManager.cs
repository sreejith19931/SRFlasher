using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace SRFlasher.Services
{
    /// <summary>
    /// Represents a single ROM flashing job in the batch queue.
    /// Implements INotifyPropertyChanged so the UI reflects live status,
    /// progress, and message updates while a job runs.
    /// </summary>
    public class RomFlashJob : INotifyPropertyChanged
    {
        public enum JobStatus { Queued, Running, Completed, Failed, Cancelled }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Notify([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private int _id;
        public int Id
        {
            get => _id;
            set { _id = value; Notify(); }
        }

        private string _romFilePath;
        public string RomFilePath
        {
            get => _romFilePath;
            set { _romFilePath = value; Notify(); }
        }

        private string _romFileName;
        public string RomFileName
        {
            get => _romFileName;
            set { _romFileName = value; Notify(); }
        }

        private JobStatus _status;
        public JobStatus Status
        {
            get => _status;
            set
            {
                _status = value;
                Notify();
                Notify(nameof(ElapsedDisplay));
            }
        }

        private double _progress;
        public double Progress
        {
            get => _progress;
            set { _progress = value; Notify(); }
        }

        private string _statusMessage;
        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; Notify(); }
        }

        private DateTime _createdTime;
        public DateTime CreatedTime
        {
            get => _createdTime;
            set { _createdTime = value; Notify(); }
        }

        private DateTime? _completedTime;
        public DateTime? CompletedTime
        {
            get => _completedTime;
            set
            {
                _completedTime = value;
                Notify();
                Notify(nameof(ElapsedDisplay));
            }
        }

        /// <summary>
        /// Human-readable duration for finished jobs ("12.3s"), empty otherwise.
        /// </summary>
        public string ElapsedDisplay
        {
            get
            {
                if (CompletedTime.HasValue &&
                    (Status == JobStatus.Completed || Status == JobStatus.Failed || Status == JobStatus.Cancelled))
                {
                    return $"({(CompletedTime.Value - CreatedTime).TotalSeconds:0.#}s)";
                }

                return string.Empty;
            }
        }

        private List<string> _selectedPartitions = new List<string>();
        public List<string> SelectedPartitions
        {
            get => _selectedPartitions;
            set
            {
                _selectedPartitions = value;
                Notify();
                Notify(nameof(PartitionsDisplay));
            }
        }

        /// <summary>
        /// Target slot captured with the job ("A", "B", "Both", or null = active slot).
        /// </summary>
        private string _targetSlot;
        public string TargetSlot
        {
            get => _targetSlot;
            set { _targetSlot = value; Notify(); Notify(nameof(PartitionsDisplay)); }
        }

        /// <summary>Flash vbmeta with verity/verification disabled for this job.</summary>
        private bool _disableVerity;
        public bool DisableVerity
        {
            get => _disableVerity;
            set { _disableVerity = value; Notify(); Notify(nameof(PartitionsDisplay)); }
        }

        /// <summary>Clean install: run a factory wipe (fastboot -w) before flashing.</summary>
        private bool _wipeData;
        public bool WipeData
        {
            get => _wipeData;
            set { _wipeData = value; Notify(); Notify(nameof(PartitionsDisplay)); }
        }

        /// <summary>Post-flash destination ("system", "recovery", "bootloader", or null = stay).</summary>
        private string _postFlashTarget;
        public string PostFlashTarget
        {
            get => _postFlashTarget;
            set { _postFlashTarget = value; Notify(); Notify(nameof(PartitionsDisplay)); }
        }

        /// <summary>
        /// Set by the batch worker once the job has run: true when its post-flash
        /// reboot was applied, false when the batch suppressed it. Null while the
        /// job is still queued, in which case the row predicts from the queue.
        /// </summary>
        private bool? _postFlashApplied;
        public bool? PostFlashApplied
        {
            get => _postFlashApplied;
            set { _postFlashApplied = value; Notify(); Notify(nameof(PartitionsDisplay)); }
        }

        /// <summary>
        /// Codename(s) the ROM declares, captured when the job was queued so the
        /// batch can refuse a package built for a different device.
        /// </summary>
        public string[] DeviceIdentifiers { get; set; } = new string[0];

        public string PartitionsDisplay
        {
            get
            {
                var parts = (SelectedPartitions == null || SelectedPartitions.Count == 0)
                    ? "(current selection)"
                    : string.Join(", ", SelectedPartitions);

                var extras = new List<string>();
                if (!string.IsNullOrWhiteSpace(TargetSlot)) extras.Add($"slot {TargetSlot}");
                if (DisableVerity) extras.Add("verity off");
                if (WipeData) extras.Add("clean install");

                // The batch only reboots after the last job, so a queued job with
                // work behind it must not advertise a reboot it will never get.
                if (!string.IsNullOrWhiteSpace(PostFlashTarget))
                {
                    // Once the job has run this is history, not a prediction; only
                    // a job that has not started yet asks the queue.
                    var rebootApplied = PostFlashApplied
                        ?? !BatchOperationManager.Instance.HasPendingJobAfter(Id);
                    extras.Add(rebootApplied ? $"→ {PostFlashTarget}" : "stays in fastboot");
                }

                return extras.Count == 0 ? parts : $"{parts} · {string.Join(" · ", extras)}";
            }
        }

        /// <summary>
        /// Ask the UI to re-read <see cref="PartitionsDisplay"/> when the queue
        /// around this job changes.
        /// </summary>
        internal void NotifyQueueDependentDisplay() => Notify(nameof(PartitionsDisplay));

        public RomFlashJob(int id, string filePath)
        {
            Id = id;
            RomFilePath = filePath;
            RomFileName = System.IO.Path.GetFileName(filePath);
            Status = JobStatus.Queued;
            Progress = 0;
            StatusMessage = "Queued";
            CreatedTime = DateTime.Now;
        }
    }

    /// <summary>
    /// Manages batch ROM flashing operations with queuing and sequential execution.
    /// </summary>
    public class BatchOperationManager
    {
        private static BatchOperationManager _instance;
        private readonly LoggingService _logger;
        private int _nextJobId = 1;

        public ObservableCollection<RomFlashJob> JobQueue { get; }
        public event EventHandler QueueChanged;
        public event EventHandler<RomFlashJob> JobStatusChanged;

        private BatchOperationManager()
        {
            _logger = LoggingService.Instance;
            JobQueue = new ObservableCollection<RomFlashJob>();
            JobQueue.CollectionChanged += (s, e) =>
            {
                QueueChanged?.Invoke(this, EventArgs.Empty);
                RefreshJobDisplays();
            };
        }

        public static BatchOperationManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (typeof(BatchOperationManager))
                    {
                        if (_instance == null)
                        {
                            _instance = new BatchOperationManager();
                        }
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// Add a ROM file to the batch queue.
        /// </summary>
        /// <param name="romFilePath">Path to the ROM file.</param>
        /// <param name="selectedPartitions">
        /// Partitions to flash for this job. Pass null to fall back to the
        /// partitions currently selected in the UI at processing time.
        /// </param>
        public RomFlashJob AddJob(string romFilePath, List<string> selectedPartitions = null,
            string targetSlot = null, bool disableVerity = false, bool wipeData = false,
            string postFlashTarget = null, string[] deviceIdentifiers = null)
        {
            if (!System.IO.File.Exists(romFilePath))
            {
                _logger.Warning("Cannot add job for non-existent file: {FilePath}", romFilePath);
                return null;
            }

            var job = new RomFlashJob(_nextJobId++, romFilePath);
            if (selectedPartitions != null)
                job.SelectedPartitions = selectedPartitions;
            job.TargetSlot = targetSlot;
            job.DisableVerity = disableVerity;
            job.WipeData = wipeData;
            job.PostFlashTarget = postFlashTarget;
            job.DeviceIdentifiers = deviceIdentifiers ?? new string[0];
            JobQueue.Add(job);
            _logger.Information("Added job to queue: {JobId} - {FileName}", job.Id, job.RomFileName);
            return job;
        }

        /// <summary>
        /// Remove a job from the queue.
        /// </summary>
        public void RemoveJob(int jobId)
        {
            var job = JobQueue.FirstOrDefault(j => j.Id == jobId);
            if (job != null && job.Status != RomFlashJob.JobStatus.Running)
            {
                JobQueue.Remove(job);
                _logger.Information("Removed job from queue: {JobId}", jobId);
            }
        }

        /// <summary>
        /// Move a queued job up (-1) or down (+1) within the queue.
        /// Only jobs that are still queued (not running) can be moved, and the
        /// swap target must also be queued to keep the running job's position.
        /// </summary>
        public bool MoveJob(int jobId, int direction)
        {
            var job = JobQueue.FirstOrDefault(j => j.Id == jobId);
            if (job == null || job.Status != RomFlashJob.JobStatus.Queued)
                return false;

            int index = JobQueue.IndexOf(job);
            int target = index + direction;
            if (target < 0 || target >= JobQueue.Count)
                return false;
            if (JobQueue[target].Status != RomFlashJob.JobStatus.Queued)
                return false;

            JobQueue.Move(index, target);
            RefreshJobDisplays();
            _logger.Debug("Moved job {JobId} {Direction}", jobId, direction < 0 ? "up" : "down");
            return true;
        }

        /// <summary>
        /// Update job status.
        /// </summary>
        public void UpdateJobStatus(int jobId, RomFlashJob.JobStatus status, string message = null, double? progress = null)
        {
            var job = JobQueue.FirstOrDefault(j => j.Id == jobId);
            if (job != null)
            {
                job.Status = status;
                if (message != null) job.StatusMessage = message;
                if (progress.HasValue) job.Progress = progress.Value;
                if (status == RomFlashJob.JobStatus.Completed || status == RomFlashJob.JobStatus.Failed)
                {
                    job.CompletedTime = DateTime.Now;
                }

                RefreshJobDisplays();
                JobStatusChanged?.Invoke(this, job);
            }
        }

        /// <summary>
        /// Clear all completed jobs.
        /// </summary>
        public void ClearCompleted()
        {
            var completed = JobQueue.Where(j => 
                j.Status == RomFlashJob.JobStatus.Completed ||
                j.Status == RomFlashJob.JobStatus.Failed ||
                j.Status == RomFlashJob.JobStatus.Cancelled).ToList();

            foreach (var job in completed)
            {
                JobQueue.Remove(job);
            }

            _logger.Information("Cleared {Count} completed jobs", completed.Count);
        }

        /// <summary>
        /// Serialize the still-queued (not running/finished) jobs for persistence.
        /// Format: one job per line,
        /// "filepath||partitions||slot||verity||wipe||postFlash||devices" ('|' is
        /// illegal in Windows filenames, so the separator can never appear inside
        /// a path). Shorter lines from older versions are still understood.
        /// </summary>
        public string SerializeQueue()
        {
            var lines = new List<string>();
            foreach (var job in JobQueue)
            {
                if (job.Status != RomFlashJob.JobStatus.Queued) continue;
                var parts = job.SelectedPartitions != null && job.SelectedPartitions.Count > 0
                    ? string.Join(",", job.SelectedPartitions)
                    : string.Empty;
                lines.Add(string.Join("||",
                    job.RomFilePath,
                    parts,
                    job.TargetSlot ?? string.Empty,
                    job.DisableVerity ? "1" : "0",
                    job.WipeData ? "1" : "0",
                    job.PostFlashTarget ?? string.Empty,
                    string.Join(",", job.DeviceIdentifiers ?? new string[0])));
            }
            return string.Join("\n", lines);
        }

        /// <summary>
        /// Restore queued jobs from a previous session. Missing files are skipped
        /// with a log warning (AddJob refuses to queue them).
        /// </summary>
        public void RestoreQueue(string serialized)
        {
            if (string.IsNullOrWhiteSpace(serialized)) return;
            foreach (var line in serialized.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var fields = line.Split(new[] { "||" }, StringSplitOptions.None);
                if (fields.Length < 2) continue;

                var path = fields[0];
                var parts = fields[1]
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToList();

                // Options are absent in two-field (legacy) entries.
                var slot = fields.Length > 2
                    ? SanitizeChoice(fields[2], "A", "B", "Both")
                    : null;
                var post = fields.Length > 5
                    ? SanitizeChoice(fields[5], "system", "recovery", "bootloader")
                    : null;

                AddJob(path, parts, slot,
                    fields.Length > 3 && fields[3] == "1",
                    fields.Length > 4 && fields[4] == "1",
                    post,
                    SanitizeIdentifiers(fields.Length > 6 ? fields[6] : null));
            }
            _logger.Information("Batch queue restored from settings");
        }

        /// <summary>
        /// Restrict a persisted option to a value the flasher understands, so a
        /// hand-edited settings file cannot smuggle extra arguments into a
        /// fastboot command line.
        /// </summary>
        private static string SanitizeChoice(string value, params string[] allowed)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            foreach (var candidate in allowed)
            {
                if (string.Equals(candidate, value.Trim(), StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// Keep only plausible codenames from a persisted field, so a hand-edited
        /// settings file cannot inject arbitrary text into the compatibility check.
        /// </summary>
        private static string[] SanitizeIdentifiers(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return new string[0];

            return value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim())
                .Where(v => v.Length > 0 && v.Length <= 64 &&
                            v.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.'))
                .ToArray();
        }

        /// <summary>
        /// True when another queued (or currently running) job sits after this one,
        /// which is exactly when the batch suppresses a job's post-flash reboot.
        /// </summary>
        public bool HasPendingJobAfter(int jobId)
        {
            int index = JobQueue.IndexOf(JobQueue.FirstOrDefault(j => j.Id == jobId));
            if (index < 0)
                return false;

            for (int i = index + 1; i < JobQueue.Count; i++)
            {
                var status = JobQueue[i].Status;
                if (status == RomFlashJob.JobStatus.Queued || status == RomFlashJob.JobStatus.Running)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Re-read per-row text that depends on the rest of the queue.
        /// </summary>
        private void RefreshJobDisplays()
        {
            foreach (var job in JobQueue)
                job.NotifyQueueDependentDisplay();
        }

        /// <summary>
        /// Get count of queued jobs waiting to run.
        /// </summary>
        public int GetQueuedJobCount()
        {
            return JobQueue.Count(j => j.Status == RomFlashJob.JobStatus.Queued);
        }

        /// <summary>
        /// Get the next job to process.
        /// </summary>
        public RomFlashJob GetNextPendingJob()
        {
            return JobQueue.FirstOrDefault(j => j.Status == RomFlashJob.JobStatus.Queued);
        }

        /// <summary>
        /// Processes all queued jobs sequentially using the supplied job processor.
        /// Stops on cancellation or when no queued jobs remain.
        /// </summary>
        /// <param name="processJobAsync">Async callback that performs the work for a job. Returns true on success.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task ProcessQueueAsync(
            Func<RomFlashJob, CancellationToken, Task<bool>> processJobAsync,
            CancellationToken ct = default)
        {
            if (processJobAsync == null)
                throw new ArgumentNullException(nameof(processJobAsync));

            while (!ct.IsCancellationRequested)
            {
                var job = GetNextPendingJob();
                if (job == null)
                    break;

                UpdateJobStatus(job.Id, RomFlashJob.JobStatus.Running, "Processing...");

                bool success;
                try
                {
                    success = await processJobAsync(job, ct);
                }
                catch (OperationCanceledException)
                {
                    UpdateJobStatus(job.Id, RomFlashJob.JobStatus.Cancelled, "Cancelled");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error("Batch job {JobId} ({FileName}) failed: {Message}",
                        job.Id, job.RomFileName, ex.Message);
                    UpdateJobStatus(job.Id, RomFlashJob.JobStatus.Failed, ex.Message);
                    continue;
                }

                UpdateJobStatus(
                    job.Id,
                    success ? RomFlashJob.JobStatus.Completed : RomFlashJob.JobStatus.Failed,
                    success ? "Completed" : "Failed",
                    success ? 100.0 : 0.0);
            }
        }
    }
}
