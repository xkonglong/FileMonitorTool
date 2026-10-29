using System;
using System.Collections.Generic;
using System.Threading;

namespace FileMonitorTool
{
    /// <summary>
    /// 按 key（文件路径）去抖：同一路径的连续事件会被合并，静默 delayMs 后才回调一次。
    /// </summary>
    internal sealed class Debouncer : IDisposable
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, Timer> _timers =
            new Dictionary<string, Timer>(StringComparer.OrdinalIgnoreCase);
        private readonly int _delayMs;
        private readonly Action<string> _callback;
        private bool _disposed;

        public Debouncer(int delayMs, Action<string> callback)
        {
            _delayMs = Math.Max(10, delayMs);
            _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        }

        public void Add(string key)
        {
            if (string.IsNullOrEmpty(key)) return;

            lock (_sync)
            {
                if (_disposed) return;

                Timer existing;
                if (_timers.TryGetValue(key, out existing))
                {
                    existing.Change(_delayMs, Timeout.Infinite);
                    return;
                }

                Timer timer = null;
                timer = new Timer(_ => Fire(key, timer), null, _delayMs, Timeout.Infinite);
                _timers[key] = timer;
            }
        }

        private void Fire(string key, Timer timer)
        {
            lock (_sync)
            {
                Timer current;
                if (_timers.TryGetValue(key, out current) && ReferenceEquals(current, timer))
                    _timers.Remove(key);
            }

            try { if (timer != null) timer.Dispose(); } catch { }

            if (_disposed) return;
            try { _callback(key); }
            catch (Exception ex) { Logger.Error("去抖回调异常: " + key, ex); }
        }

        public void Dispose()
        {
            List<Timer> toDispose;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                toDispose = new List<Timer>(_timers.Values);
                _timers.Clear();
            }
            foreach (var t in toDispose)
            {
                try { t.Dispose(); } catch { }
            }
        }
    }
}