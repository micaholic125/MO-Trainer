using System;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace MoTrainer.Util
{
    /// <summary>
    /// 构建标识：手里这份 exe 是什么时候编译出来的。
    ///
    /// **为什么需要它**：本工程的 exe 会被反复重建，而"手里的程序比源码旧一步"这件事
    /// **真的发生过**（记录在案：某一版程序 16:58 打的，源码 17:01 才改完）。
    /// 后果是用户对着一个旧程序试了半天，报上来的现象跟新代码对不上，双方白折腾。
    /// 有了这一行，一眼就能看穿"是不是跑到了旧的"。
    ///
    /// **为什么取"可执行文件自身的写入时间"而不是让构建脚本生成一个标记文件**：
    /// 本项目有两条编译路径 —— `build.ps1` 和 `tools\selfcheck-loop.ps1`（后者直接调 csc）——
    /// 任何"由构建脚本写入"的标记都会在其中一条路径上忘记更新，于是标记本身开始说谎。
    /// 而文件写入时间就是编译落盘的那一刻，用哪条路径编译都拿得到真实值，不需要任何配合。
    /// </summary>
    public static class BuildInfo
    {
        private static string _text;

        /// <summary>形如 "2026-09-29 10:08:15"；读不到时返回一句可读的说明而不是空串。</summary>
        public static string Text
        {
            get
            {
                if (_text == null) { _text = Resolve(); }
                return _text;
            }
        }

        private static string Resolve()
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                string path = assembly == null ? "" : assembly.Location;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return "未知（定位不到程序文件）";
                }
                return File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                // 构建标识读不出来不是致命问题，但也不能假装读到了 —— 如实写清原因
                return "未知（" + ex.GetType().Name + "）";
            }
        }
    }
}
