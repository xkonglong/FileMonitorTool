using System;
using System.Threading;
using System.Windows.Forms;

namespace FileMonitorTool
{
    internal static class Program
    {
        private static Mutex _mutex;

        [STAThread]
        private static void Main()
        {
            bool createdNew;
            _mutex = new Mutex(true, "FileMonitorTool_9F3A2C1B", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("文件监控工具已在运行，请查看系统托盘。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 没有主窗体，Application.Run() 只维持消息循环，托盘图标靠它存活
            using (var app = new TrayApplication())
            {
                Application.Run();
            }

            GC.KeepAlive(_mutex);
        }
    }
}