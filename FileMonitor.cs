using System;
using System.IO;
using System.Threading;

namespace FileMonitorTool
{
    internal sealed class FileMonitor : IDisposable
    {
        private readonly Config _config;
        private readonly Action<string> _onFileReady;
        private FileSystemWatcher _watcher;
        private Debouncer _debouncer;
        private volatile bool _running;

        public FileMonitor(Config config, Action<string> onFileReady)
        {
            _config = config;
            _onFileReady = onFileReady;
        }

        public bool IsRunning { get { return _running; } }

        public void Start()
        {
            Stop();

            if (string.IsNullOrWhiteSpace(_config.WatchPath))
                throw new InvalidOperationException("配置项 WatchPath 为空。");
            if (!Directory.Exists(_config.WatchPath))
                throw new DirectoryNotFoundException("监控目录不存在: " + _config.WatchPath);

            _debouncer = new Debouncer(_config.DebounceMs, HandleFileSettled);

            _watcher = new FileSystemWatcher(_config.WatchPath)
            {
                IncludeSubdirectories = _config.IncludeSubdirectories,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                Filter = "*.*",
                EnableRaisingEvents = false
            };

            _watcher.Created += OnCreatedOrRenamed;
            _watcher.Renamed += OnRenamed;
            if (_config.WatchChanged) _watcher.Changed += OnChanged;
            _watcher.Error += OnWatcherError;

            _watcher.EnableRaisingEvents = true;
            _running = true;

            Logger.Info(string.Format("开始监控: {0} | 规则数: {1} | 子目录: {2} | 去抖: {3}ms | 并发: {4}",
                _config.WatchPath, _config.Rules.Count, _config.IncludeSubdirectories,
                _config.DebounceMs, _config.MaxConcurrentActions));
        }

        public void Stop()
        {
            _running = false;

            if (_watcher != null)
            {
                try { _watcher.EnableRaisingEvents = false; } catch { }
                try { _watcher.Dispose(); } catch { }
                _watcher = null;
            }

            if (_debouncer != null)
            {
                _debouncer.Dispose();
                _debouncer = null;
            }
        }

        private void OnCreatedOrRenamed(object sender, FileSystemEventArgs e) { TryEnqueue(e.FullPath); }
        private void OnRenamed(object sender, RenamedEventArgs e) { TryEnqueue(e.FullPath); }
        private void OnChanged(object sender, FileSystemEventArgs e) { TryEnqueue(e.FullPath); }

        private void TryEnqueue(string fullPath)
        {
            if (!_running) return;
            if (string.IsNullOrEmpty(fullPath)) return;
            if (Directory.Exists(fullPath)) return;
            if (!_config.ShouldTrackFile(Path.GetFileName(fullPath)))
            {
                // 预筛掉的文件本来不会进入去抖，但 LogUnmatched 打开时仍留一条痕迹
                if (_config.LogUnmatched)
                    Logger.Info("未匹配任何规则，已忽略: " + fullPath);
                return;
            }

            var debouncer = _debouncer;
            if (debouncer != null) debouncer.Add(fullPath);
        }

        private void HandleFileSettled(string fullPath)
        {
            if (!_running) return;

            try
            {
                if (!File.Exists(fullPath)) return;
                if (IsFileReady(fullPath))
                {
                    _onFileReady(fullPath);
                    return;
                }

                Timer retry = null;
                int delay = Math.Max(500, _config.DebounceMs * 2);
                retry = new Timer(_ =>
                {
                    try
                    {
                        if (!_running) return;
                        if (File.Exists(fullPath) && IsFileReady(fullPath))
                            _onFileReady(fullPath);
                        else
                            Logger.Warn("文件未就绪，已跳过: " + fullPath);
                    }
                    catch (Exception ex) { Logger.Error("重试处理失败: " + fullPath, ex); }
                    finally { try { retry.Dispose(); } catch { } }
                }, null, delay, Timeout.Infinite);
            }
            catch (Exception ex)
            {
                Logger.Error("处理文件失败: " + fullPath, ex);
            }
        }

        private static bool IsFileReady(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    return true;
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            Logger.Error("FileSystemWatcher 错误（可能缓冲区溢出，可尝试减少监控范围/关闭子目录）", e.GetException());
        }

        public void Dispose() { Stop(); }
    }
}