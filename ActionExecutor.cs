using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace FileMonitorTool
{
    internal sealed class ActionExecutor
    {
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

            // 放到线程池执行，避免长任务阻塞事件回调线程
            Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c " + command,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        WorkingDirectory = Path.GetDirectoryName(filePath) ?? AppDomain.CurrentDomain.BaseDirectory
                    };

                    using (var p = Process.Start(psi))
                    {
                        if (p != null) p.WaitForExit();
                    }

                    Logger.Info("动作执行完成: " + command);
                }
                catch (Exception ex)
                {
                    Logger.Error("动作执行失败: " + command, ex);
                }
            });
        }
    }
}