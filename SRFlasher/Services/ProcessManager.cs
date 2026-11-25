using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SRFlasher.Services
{
    /// <summary>
    /// Manages process execution with timeout, cancellation token, and error handling support.
    /// </summary>
    public class ProcessManager
    {
        private readonly LoggingService _logger;
        // Live tool processes spawned by this manager (adb/fastboot/pnputil/
        // powershell children). Used to reap hung tools when the app exits —
        // a hung "twrp install" wait must not outlive its parent.
        private readonly ConcurrentDictionary<int, Process> _children = new ConcurrentDictionary<int, Process>();

        public ProcessManager()
        {
            _logger = LoggingService.Instance;
        }

        /// <summary>
        /// Terminates every tracked child tool process. The adb SERVER is
        /// deliberately spared so other tools keep working after SR Flasher
        /// exits; individual adb client calls die with their parent anyway.
        /// </summary>
        public void KillChildren()
        {
            foreach (var kvp in _children)
            {
                try
                {
                    var process = kvp.Value;
                    var name = Path.GetFileNameWithoutExtension(process.StartInfo.FileName) ?? "";
                    if (name.Equals("adb", StringComparison.OrdinalIgnoreCase))
                        continue; // keep the shared server alive
                    if (!process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit(3000);
                        _logger.Debug("Killed child tool process {Name} ({Pid}) on shutdown", name, kvp.Key);
                    }
                }
                catch { /* the child may already be gone */ }
                finally
                {
                    _children.TryRemove(kvp.Key, out _);
                }
            }
        }

        /// <summary>
        /// Result of a process execution.
        /// </summary>
        public class ProcessResult
        {
            public int ExitCode { get; set; }
            public string Output { get; set; }
            public string Error { get; set; }
            public bool TimedOut { get; set; }
            public bool Cancelled { get; set; }
            public TimeSpan ExecutionTime { get; set; }
        }

        /// <summary>
        /// Execute a process and capture its output with timeout and cancellation support.
        /// </summary>
        /// <param name="fileName">Path to the executable.</param>
        /// <param name="arguments">Command-line arguments.</param>
        /// <param name="timeout">Maximum time to wait for process completion (null for infinite).</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        /// <param name="outputCallback">Optional callback for real-time output streaming.</param>
        /// <returns>Process execution result.</returns>
        public async Task<ProcessResult> ExecuteAsync(
            string fileName,
            string arguments,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default,
            Action<string> outputCallback = null)
        {
            var result = new ProcessResult();
            var stopwatch = Stopwatch.StartNew();
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();
            var tcs = new TaskCompletionSource<bool>();

            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = new Process { StartInfo = processInfo, EnableRaisingEvents = true })
                {
                    // Setup output handlers
                    process.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data != null)
                        {
                            outputBuilder.AppendLine(e.Data);
                            outputCallback?.Invoke(e.Data);
                        }
                    };

                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data != null)
                        {
                            errorBuilder.AppendLine(e.Data);
                            outputCallback?.Invoke(e.Data);
                        }
                    };

                    process.Exited += (s, e) =>
                    {
                        tcs.TrySetResult(true);
                    };

                    // Start the process
                    try
                    {
                        process.Start();
                    }
                    catch (Exception ex)
                    {
                        result.Error = $"Failed to start process: {ex.Message}";
                        _logger.Error("Failed to start process {FileName}: {Message}", fileName, ex.Message);
                        stopwatch.Stop();
                        result.ExecutionTime = stopwatch.Elapsed;
                        return result;
                    }

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    _children.TryAdd(process.Id, process);

                    // Wait for process completion with timeout and cancellation
                    try
                    {
                        var timeoutMs = timeout?.TotalMilliseconds ?? Timeout.Infinite;
                        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            if (timeout.HasValue)
                            {
                                cts.CancelAfter(timeout.Value);
                            }

                            await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, cts.Token));

                            if (cts.Token.IsCancellationRequested)
                            {
                                if (timeout.HasValue && !cancellationToken.IsCancellationRequested)
                                {
                                    result.TimedOut = true;
                                    _logger.Warning("Process {FileName} timed out after {Timeout}ms", fileName, timeoutMs);
                                }
                                else if (cancellationToken.IsCancellationRequested)
                                {
                                    result.Cancelled = true;
                                    _logger.Information("Process {FileName} was cancelled", fileName);
                                }

                                if (!process.HasExited)
                                {
                                    try
                                    {
                                        process.Kill();
                                        process.WaitForExit(5000);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.Warning("Failed to kill process {FileName}: {Message}", fileName, ex.Message);
                                    }
                                }
                            }
                        }

                        // Ensure the process has fully exited before reading its exit code.
                        if (!process.HasExited)
                        {
                            try
                            {
                                process.Kill();
                                process.WaitForExit(5000);
                            }
                            catch (Exception ex)
                            {
                                _logger.Warning("Failed to kill process {FileName}: {Message}", fileName, ex.Message);
                            }
                        }
                        else
                        {
                            // WaitForExit() (parameterless) also flushes the async
                            // output/error readers so the buffers are complete.
                            try
                            {
                                process.WaitForExit();
                            }
                            catch (Exception ex)
                            {
                                _logger.Debug("Failed to flush output for {FileName}: {Message}", fileName, ex.Message);
                            }
                        }

                        try
                        {
                            result.ExitCode = process.ExitCode;
                        }
                        catch (InvalidOperationException)
                        {
                            // Process did not exit; leave ExitCode at default (0).
                            result.ExitCode = -1;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        result.Cancelled = true;
                        if (!process.HasExited)
                        {
                            try
                            {
                                process.Kill();
                                process.WaitForExit(5000);
                            }
                            catch { }
                        }

                        try
                        {
                            result.ExitCode = process.ExitCode;
                        }
                        catch (InvalidOperationException)
                        {
                            result.ExitCode = -1;
                        }
                    }

                    result.Output = outputBuilder.ToString();
                    result.Error = errorBuilder.ToString();
                    if (process.Id > 0) _children.TryRemove(process.Id, out _);
                }
            }
            catch (Exception ex)
            {
                result.Error = $"Process execution failed: {ex.Message}";
                _logger.Error("Process execution failed for {FileName}: {Message}", fileName, ex.Message);
            }
            finally
            {
                stopwatch.Stop();
                result.ExecutionTime = stopwatch.Elapsed;
            }

            return result;
        }

        /// <summary>
        /// Execute a process synchronously with timeout.
        /// </summary>
        public ProcessResult Execute(
            string fileName,
            string arguments,
            TimeSpan? timeout = null,
            Action<string> outputCallback = null)
        {
            return ExecuteAsync(fileName, arguments, timeout, CancellationToken.None, outputCallback)
                .GetAwaiter()
                .GetResult();
        }

        /// <summary>
        /// Execute a process for output only (like "adb devices").
        /// </summary>
        public async Task<string> GetOutputAsync(
            string fileName,
            string arguments,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            var result = await ExecuteAsync(fileName, arguments, timeout, cancellationToken);
            return result.TimedOut ? $"[TIMEOUT] {result.Output}" : result.Output;
        }
    }
}
