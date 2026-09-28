using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MoTrainer.Util
{
    /// <summary>
    /// 把界面上的运行日志导出成文本文件。
    ///
    /// 为什么需要它：日志面板只有两百来像素高，出问题时用户只能截图 ——
    /// 而**截图天然放不下全部内容**。多轮真机反馈都卡在"看不全数据"上，
    /// 明明日志里已经有答案（比如"找到 2 个槽位""归属校验 1/2"），
    /// 却因为被截断而多耗一轮。导成文件就能整段发过来。
    ///
    /// 落在 reports\ 而不是 logs\ —— logs\ 只保留最近 7 天、会被自动清理，
    /// 而导出的东西是用户主动要留下的证据，不能被程序悄悄删掉。
    /// </summary>
    public static class LogExporter
    {
        /// <summary>
        /// 导出并返回文件路径。失败时抛异常，由调用方给出提示。
        /// </summary>
        public static string Export(IList<LogEntry> entries, string title)
        {
            string baseDirectory = ResolveBaseDirectory();
            string directory = Path.Combine(baseDirectory, "reports");
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory,
                "log-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");

            StringBuilder text = new StringBuilder();
            text.AppendLine("MO Trainer 运行日志导出");
            text.AppendLine("=================================================");
            text.AppendLine("导出时间 : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            text.AppendLine("导出位置 : " + (string.IsNullOrEmpty(title) ? "运行日志面板" : title));
            text.AppendLine("行数     : " + (entries == null ? 0 : entries.Count) + "（界面最多保留 500 行）");
            text.AppendLine("程序目录 : " + baseDirectory);
            text.AppendLine("当日日志 : " + (string.IsNullOrEmpty(Logger.CurrentFile) ? "(未初始化)" : Logger.CurrentFile));
            text.AppendLine("-------------------------------------------------");
            text.AppendLine();

            if (entries != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    LogEntry entry = entries[i];
                    if (entry == null) { continue; }
                    text.AppendLine(entry.Format());
                }
            }

            text.AppendLine();
            text.AppendLine("-------------------------------------------------");
            text.AppendLine("（以上为导出瞬间界面里保留的全部日志行）");

            File.WriteAllText(path, text.ToString(), Encoding.UTF8);
            Logger.Info("Log", "已导出运行日志：" + path);
            return path;
        }

        /// <summary>
        /// 程序根目录。优先由日志目录反推 —— 日志目录才是 Logger.Init 真正用的那一个，
        /// 比 AppDomain.BaseDirectory 更贴近实际（两者在正常启动时一致）。
        /// </summary>
        private static string ResolveBaseDirectory()
        {
            string logDir = Logger.LogDirectory;
            if (!string.IsNullOrEmpty(logDir))
            {
                DirectoryInfo parent = Directory.GetParent(logDir);
                if (parent != null) { return parent.FullName; }
            }
            return AppDomain.CurrentDomain.BaseDirectory;
        }
    }
}
