using System;
using System.Threading;
using System.Windows.Forms;
using MoTrainer.Core;
using MoTrainer.Ui;
using MoTrainer.Util;

namespace MoTrainer
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            Logger.Init(baseDirectory);
            Logger.Info("Program", "MO Trainer 启动，运行目录 " + baseDirectory);

            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            Application.ThreadException += OnThreadException;
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            ProcessAttach attach = new ProcessAttach();
            MemoryIO memory = new MemoryIO(attach);
            attach.StartWatchdog();

            try
            {
                Application.Run(new MainForm(attach, memory));
            }
            catch (Exception ex)
            {
                Logger.Error("Program", "主循环异常退出", ex);
                MessageBox.Show(
                    "程序发生异常并已退出。\n\n" + ex.Message + "\n\n详细信息已写入 logs 目录。",
                    "MO Trainer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                attach.Dispose();
                Logger.Info("Program", "MO Trainer 退出");
            }
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            Logger.Error("Program", "UI 线程未处理异常", e.Exception);
            MessageBox.Show(
                "界面出现异常，操作已被取消。\n\n" + e.Exception.Message + "\n\n详细信息已写入 logs 目录。",
                "MO Trainer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = e.ExceptionObject as Exception;
            Logger.Error("Program", "未处理的致命异常", ex);
        }
    }
}
