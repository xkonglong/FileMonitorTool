using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace FileMonitorTool
{
    internal sealed class ActionExecutor
    {
        private const int TimeoutMs = 10 * 60 * 1000; // 10 分钟
        private readonly string _template;

        public ActionExecutor(string commandTemplate)
        {
            _template = commandTemplate ?? "";
        }

        public void Execute(string filePath)
        {
            if (string.IsNullOrWhiteSpace(_template))
            {
                Logger.Warn("ActionCommand 未配置，仅记录事件: " + filePath);
                return;
            }

            string command = _template
                .Replace("{filepath}", filePath)
                .Replace("{filename}", Path.GetFileName(filePath))
                .Replace("{directory}", Path.GetDirectoryName(filePath) ?? "")
                .Replace("{ext}", Path.GetExtension(filePath).TrimStart('.'));

            string workDir = Path.GetDirectoryName(filePath) ?? AppDomain.CurrentDomain.BaseDirectory;

            Task.Run(() => RunCommand(command, workDir));
        }

        private void RunCommand(string command, string workDir)
        {
            try
            {
                string exePath, arguments;
                ProcessStartInfo psi;

                if (TrySplitExecutable(command, out exePath, out arguments))
                {
                    // 直接启动 exe：绕过 cmd 的引号剥离问题
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
                    Logger.Info("直接启动: " + exePath + " " + arguments);
                }
                else
                {
                    // 内置命令 / 环境变量 / 重定向，交给 cmd；/s 保证引号正确处理
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
                    Logger.Info("通过 cmd 执行: " + command);
                }

                using (var p = Process.Start(psi))
                {
                    if (p == null)
                    {
                        Logger.Error("无法启动进程: " + command);
                        return;
                    }

                    var stdoutTask = p.StandardOutput.ReadToEndAsync();
                    var stderrTask = p.StandardError.ReadToEndAsync();

                    bool exited = p.WaitForExit(TimeoutMs);

                    if (!exited)
                    {
                        Logger.Warn("动作执行超时（" + TimeoutMs + "ms），尝试结束进程: " + command);
                        try { p.Kill(); } catch { }
                    }

                    string stdout = SafeGet(stdoutTask);
                    string stderr = SafeGet(stderrTask);

                    if (!string.IsNullOrWhiteSpace(stdout))
                        Logger.Info("输出: " + stdout.Trim());
                    if (!string.IsNullOrWhiteSpace(stderr))
                        Logger.Warn("错误输出: " + stderr.Trim());

                    if (!exited)
                    {
                        Logger.Error("动作执行超时: " + command);
                    }
                    else if (p.ExitCode != 0)
                    {
                        Logger.Error("动作执行失败，退出码 " + p.ExitCode + ": " + command);
                    }
                    else
                    {
                        Logger.Info("动作执行完成: " + command);
                    }
                }
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == 740)
                    Logger.Error("需要管理员权限才能执行（请以管理员运行本工具，或调整该 exe 的清单）: " + command, ex);
                else
                    Logger.Error("启动进程失败（Win32 错误 " + ex.NativeErrorCode + "）: " + command, ex);
            }
            catch (Exception ex)
            {
                Logger.Error("动作执行失败: " + command, ex);
            }
        }

        private static string SafeGet(Task<string> task)
        {
            try
            {
                return task.Wait(2000) ? task.Result : "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// 把命令模板拆成“可执行文件 + 参数”，支持首段带引号。
        /// 仅识别 .exe/.com；.bat/.cmd 和内置命令仍走 cmd。
        /// </summary>
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
                if (space < 0)
                {
                    exe = s;
                    consumed = s.Length;
                }
                else
                {
                    exe = s.Substring(0, space);
                    consumed = space;
                }
            }

            string ext = Path.GetExtension(exe).ToLowerInvariant();
            if (ext != ".exe" && ext != ".com") return false;

            fileName = exe;
            arguments = consumed < s.Length ? s.Substring(consumed).TrimStart() : "";
            return true;
        }
    }
}