using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileMonitorTool
{
    internal sealed class ActionExecutor : IDisposable
    {
        private const int TimeoutMs = 10 * 60 * 1000;
        private readonly Config _config;
        private readonly SemaphoreSlim _semaphore;
        private volatile bool _disposed;

        public ActionExecutor(Config config)
        {
            _config = config;
            _semaphore = new SemaphoreSlim(Math.Max(1, config.MaxConcurrentActions));
        }

        public void Execute(string filePath)
        {
            var rule = _config.MatchRule(filePath);
            if (rule == null)
            {
                if (_config.LogUnmatched)
                    Logger.Info("未匹配规则，已忽略: " + filePath);
                return;
            }

            string command = BuildCommand(rule.Command, filePath);
            string workDir = Path.GetDirectoryName(filePath) ?? AppDomain.CurrentDomain.BaseDirectory;

            Task.Run(async () =>
            {
                if (_disposed) return;
                try { await _semaphore.WaitAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { return; }

                try
                {
                    if (_disposed) return;
                    RunCommand(command, workDir, rule);
                }
                finally
                {
                    try { _semaphore.Release(); } catch (ObjectDisposedException) { }
                }
            });
        }

        private static string BuildCommand(string template, string filePath)
        {
            return template
                .Replace("{filepath}", filePath)
                .Replace("{filename}", Path.GetFileName(filePath))
                .Replace("{directory}", Path.GetDirectoryName(filePath) ?? "")
                .Replace("{ext}", Path.GetExtension(filePath).TrimStart('.'));
        }

        private void RunCommand(string command, string workDir, ActionRule rule)
        {
            string tag = "Action." + rule.Order +
                         (rule.MatchType == RuleMatchType.Name ? " [Name]" : " [Ext]");

            try
            {
                string exePath, arguments;
                ProcessStartInfo psi;

                if (TrySplitExecutable(command, out exePath, out arguments))
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        WorkingDirectory = workDir,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    Logger.Info("[" + tag + "] 直接启动: " + exePath + " " + arguments);
                }
                else
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/s /c \"" + command + "\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        WorkingDirectory = workDir,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    Logger.Info("[" + tag + "] 通过 cmd 执行: " + command);
                }

                using (var p = Process.Start(psi))
                {
                    if (p == null)
                    {
                        Logger.Error("[" + tag + "] 无法启动进程: " + command);
                        return;
                    }

                    var stdoutTask = p.StandardOutput.ReadToEndAsync();
                    var stderrTask = p.StandardError.ReadToEndAsync();

                    bool exited = p.WaitForExit(TimeoutMs);

                    if (!exited)
                    {
                        Logger.Warn("[" + tag + "] 超时（" + TimeoutMs + "ms），尝试结束进程: " + command);
                        try { p.Kill(); } catch { }
                    }

                    string stdout = SafeGet(stdoutTask);
                    string stderr = SafeGet(stderrTask);

                    if (!string.IsNullOrWhiteSpace(stdout))
                        Logger.Info("[" + tag + "] 输出: " + stdout.Trim());
                    if (!string.IsNullOrWhiteSpace(stderr))
                        Logger.Warn("[" + tag + "] 错误输出: " + stderr.Trim());

                    if (!exited)
                        Logger.Error("[" + tag + "] 动作执行超时: " + command);
                    else if (p.ExitCode != 0)
                        Logger.Error("[" + tag + "] 动作执行失败，退出码 " + p.ExitCode + ": " + command);
                    else
                        Logger.Info("[" + tag + "] 动作执行完成: " + command);
                }
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == 740)
                    Logger.Error("[" + tag + "] 需要管理员权限才能执行: " + command, ex);
                else
                    Logger.Error("[" + tag + "] 启动进程失败（Win32 " + ex.NativeErrorCode + "）: " + command, ex);
            }
            catch (Exception ex)
            {
                Logger.Error("[" + tag + "] 动作执行失败: " + command, ex);
            }
        }

        private static string SafeGet(Task<string> task)
        {
            try { return task.Wait(2000) ? task.Result : ""; }
            catch { return ""; }
        }

        private static bool TrySplitExecutable(string command, out string fileName, out string arguments)
        {
            fileName = null;
            arguments = null;
            if (string.IsNullOrWhiteSpace(command)) return false;

            string s = command.Trim();
            string exe;
            int consumed;

            if (s[0] == '"')
            {
                int end = s.IndexOf('"', 1);
                if (end < 0) return false;
                exe = s.Substring(1, end - 1);
                consumed = end + 1;
            }
            else
            {
                int space = s.IndexOf(' ');
                if (space < 0) { exe = s; consumed = s.Length; }
                else { exe = s.Substring(0, space); consumed = space; }
            }

            string ext = Path.GetExtension(exe).ToLowerInvariant();
            if (ext != ".exe" && ext != ".com") return false;

            fileName = exe;
            arguments = consumed < s.Length ? s.Substring(consumed).TrimStart() : "";
            return true;
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}