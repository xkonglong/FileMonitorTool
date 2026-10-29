using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace FileMonitorTool
{
    internal sealed class FileMonitor : IDisposable
    {
        private readonly Config _config;
        private readonly Action<string> _onFileReady;
        private FileSystemWatcher _watcher;
        private Debouncer _debouncer;

        public FileMonitor(Config config, Action<string> onFileReady)
        {
            _config = config;
            _onFileReady = onFileReady;
        }

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
                InternalBufferSize = 64 * 1024,          // 最大值，降低事件丢失概率
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                Filter = "*.*",                           // 先全收，再由 MatchesFilter 精筛
                EnableRaisingEvents = false
            };

            _watcher.Created += OnCreatedOrRenamed;
            _watcher.Renamed += OnRenamed;
            if (_config.WatchChanged) _watcher.Changed += OnChanged;
            _watcher.Error += OnWatcherError;

            _watcher.EnableRaisingEvents = true;

            Logger.Info(string.Format("开始监控: {0} | 过滤: {1} | 子目录: {2} | 去抖: {3}ms",
                _config.WatchPath, _config.FileFilter, _config.IncludeSubdirectories, _config.DebounceMs));
        }

        public void Stop()
        {
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
            if (string.IsNullOrEmpty(fullPath)) return;
            if (Directory.Exists(fullPath)) return;                 // 忽略目录事件
            if (!MatchesFilter(Path.GetFileName(fullPath))) return; // 类型过滤放在入口，减少后续开销

            var debouncer = _debouncer;
            if (debouncer != null) debouncer.Add(fullPath);
        }

        private bool MatchesFilter(string fileName)
        {
            foreach (string raw in _config.Filters)
            {
                string pattern = raw.Trim();
                if (pattern.Length == 0) continue;
                if (pattern == "*.*" || pattern == "*") return true;
                if (WildcardMatch(fileName, pattern)) return true;
            }
            return false;
        }

        /// <summary>只支持 * 和 ? 的简单通配，大小写不敏感。</summary>
        private static bool WildcardMatch(string input, string pattern)
        {
            string regex = "^" + Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            return Regex.IsMatch(input, regex, RegexOptions.IgnoreCase);
        }

        /// <summary>去抖窗口结束后触发：确认文件存在且可读，再交给动作层。</summary>
        private void HandleFileSettled(string fullPath)
        {
            try
            {
                if (!File.Exists(fullPath)) return; // 期间被删除或改名走了
                if (IsFileReady(fullPath))
                {
                    _onFileReady(fullPath);
                    return;
                }

                // 仍被占用（复制未完成 / 编辑器未释放），延迟再试一次
                Timer retry = null;
                int delay = Math.Max(500, _config.DebounceMs * 2);
                retry = new Timer(_ =>
                {
                    try
                    {
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