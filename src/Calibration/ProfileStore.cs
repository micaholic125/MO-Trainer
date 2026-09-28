using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MoTrainer.Core;
using MoTrainer.Util;

// 报告里会列出全部候选指针链，见 AppendLocator

namespace MoTrainer.Calibration
{
    /// <summary>
    /// 校准配置与校准报告的落盘管理。
    /// 全部文件都在工具自身目录内，不写注册表、不写 C 盘。
    /// </summary>
    public sealed class ProfileStore
    {
        public const string ConfigFolderName = "config";
        public const string ReportsFolderName = "reports";
        public const string ProfileFileName = "profile.json";

        private readonly string _baseDirectory;

        public ProfileStore(string baseDirectory)
        {
            if (string.IsNullOrEmpty(baseDirectory))
            {
                baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            }
            _baseDirectory = baseDirectory;
        }

        public string BaseDirectory { get { return _baseDirectory; } }
        public string ConfigFolder { get { return Path.Combine(_baseDirectory, ConfigFolderName); } }
        public string ReportsFolder { get { return Path.Combine(_baseDirectory, ReportsFolderName); } }
        public string ProfilePath { get { return Path.Combine(ConfigFolder, ProfileFileName); } }

        // ============================================================ 读写

        /// <summary>
        /// 读取校准配置。文件不存在、损坏或版本不识别时返回一份空配置（不抛异常），
        /// 上层据此把功能置为"未校准"。
        /// </summary>
        public CalibrationProfile Load()
        {
            string path = ProfilePath;
            if (!File.Exists(path))
            {
                Logger.Info("Profile", "未找到校准配置，按未校准处理：" + path);
                return new CalibrationProfile();
            }

            try
            {
                string text = File.ReadAllText(path, Encoding.UTF8);
                JsonValue json;
                string error;
                if (!JsonValue.TryParse(text, out json, out error))
                {
                    Logger.Error("Profile", "校准配置解析失败：" + error + "，按未校准处理");
                    return new CalibrationProfile();
                }

                CalibrationProfile profile = CalibrationProfile.FromJson(json);

                int discarded = profile.DiscardDeprecatedLocators();
                if (discarded > 0)
                {
                    Logger.Warn("Profile", string.Format(CultureInfo.InvariantCulture,
                        "已作废 {0} 项由已废弃方法（窗口差分）定位的字段，需要重新校准", discarded));
                }

                Logger.Info("Profile", "已加载校准配置：" + profile.Describe());
                return profile;
            }
            catch (Exception ex)
            {
                Logger.Error("Profile", "读取校准配置失败，按未校准处理", ex);
                return new CalibrationProfile();
            }
        }

        public bool Save(CalibrationProfile profile, out string error)
        {
            error = "";
            if (profile == null)
            {
                error = "配置为空";
                return false;
            }

            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(profile.CreatedAt)) { profile.CreatedAt = now; }
            profile.UpdatedAt = now;

            try
            {
                Directory.CreateDirectory(ConfigFolder);
                JsonValue.SaveFile(ProfilePath, profile.ToJson(), true);
                Logger.Info("Profile", "校准配置已保存：" + ProfilePath);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                Logger.Error("Profile", "保存校准配置失败：" + error);
                return false;
            }
        }

        // ============================================================ 报告

        /// <summary>
        /// 生成人类可读的校准报告，供用户回传分析。
        /// 返回报告文件路径；失败返回 null。
        /// </summary>
        public string WriteReport(
            CalibrationProfile profile,
            MemoryIO memory,
            string environmentSummary,
            IList<string> detailLines)
        {
            try
            {
                Directory.CreateDirectory(ReportsFolder);
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                string path = Path.Combine(ReportsFolder, "calibration-" + stamp + ".txt");

                StringBuilder text = new StringBuilder(4096);
                text.AppendLine("=========================================================");
                text.AppendLine(" MO Trainer 校准报告");
                text.AppendLine("=========================================================");
                text.AppendLine("生成时间 : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                text.AppendLine("工具目录 : " + _baseDirectory);
                text.AppendLine();

                text.AppendLine("---- 环境 ----");
                if (!string.IsNullOrEmpty(environmentSummary))
                {
                    text.AppendLine(environmentSummary);
                }
                text.AppendLine("模块基址 : 0x" + profile.ModuleBase.ToString("X8", CultureInfo.InvariantCulture));
                text.AppendLine("模块大小 : 0x" + profile.ModuleSize.ToString("X", CultureInfo.InvariantCulture));
                text.AppendLine("exe CRC  : " + profile.ExeCrc32);
                text.AppendLine("游戏版本 : " + profile.GameVersion);
                text.AppendLine();

                text.AppendLine("---- 校准结果 ----");
                text.AppendLine(profile.Describe());
                text.AppendLine();

                AppendLocator(text, "金钱", profile.Money, profile, memory);
                AppendLocator(text, "电力产出", profile.PowerOutput, profile, memory);
                AppendLocator(text, "电力消耗", profile.PowerDrain, profile, memory);
                AppendLocator(text, "选中数量", profile.SelectedCount, profile, memory);
                AppendLocator(text, "选中列表", profile.SelectedList, profile, memory);
                AppendLocator(text, "对象数组", profile.TechnoArray, profile, memory);
                AppendLocator(text, "对象数组计数", profile.TechnoArrayCount, profile, memory);
                AppendLocator(text, "地图黑幕", profile.MapShroud, profile, memory);

                if (profile.OffsetTechnoOwner != 0 || profile.OffsetTechnoHealth != 0
                    || profile.OffsetTechnoIronCurtain != 0 || profile.OffsetProductionTimer != 0)
                {
                    text.AppendLine("---- 对象内字段偏移 ----");
                    text.AppendLine("归属阵营 : +0x" + profile.OffsetTechnoOwner.ToString("X", CultureInfo.InvariantCulture));
                    text.AppendLine("血量     : +0x" + profile.OffsetTechnoHealth.ToString("X", CultureInfo.InvariantCulture));
                    text.AppendLine("铁幕计时 : +0x" + profile.OffsetTechnoIronCurtain.ToString("X", CultureInfo.InvariantCulture));
                    text.AppendLine("生产进度 : +0x" + profile.OffsetProductionTimer.ToString("X", CultureInfo.InvariantCulture));
                    text.AppendLine();
                }

                if (detailLines != null && detailLines.Count > 0)
                {
                    text.AppendLine("---- 校准过程明细 ----");
                    for (int i = 0; i < detailLines.Count; i++)
                    {
                        text.AppendLine(detailLines[i]);
                    }
                    text.AppendLine();
                }

                text.AppendLine("---- 原始配置 JSON（便于程序化回填）----");
                text.AppendLine(profile.ToJson().ToJson(true));

                File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
                Logger.Info("Profile", "校准报告已生成：" + path);
                return path;
            }
            catch (Exception ex)
            {
                Logger.Error("Profile", "生成校准报告失败", ex);
                return null;
            }
        }

        private static void AppendLocator(
            StringBuilder text, string title, FieldLocator locator, CalibrationProfile profile, MemoryIO memory)
        {
            text.Append("[" + title + "] ");
            if (!locator.IsCalibrated)
            {
                text.AppendLine("未校准");
                return;
            }

            // 统一走 Describe()：它同时处理"指针链"与"静态全局变量"两种定位方式
            text.AppendLine("表达式   : " + locator.Describe(profile.ModuleBase));
            text.AppendLine("数值格式 : " + locator.DescribeEncoding());

            List<PointerChain> chains = locator.Chains;
            if (chains.Count > 1)
            {
                for (int i = 1; i < chains.Count; i++)
                {
                    text.AppendLine("  候选链 " + (i + 1) + " : " + chains[i].Format(profile.ModuleBase));
                }
            }

            uint address;
            string error;
            if (locator.TryResolve(memory, profile.ModuleBase, out address, out error))
            {
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  解析地址 : 0x{0:X8}", address));

                uint value;
                if (memory != null && memory.ReadUInt32(address, out value))
                {
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  当前值   : {0} (0x{1:X8})", value, value));
                }
                else
                {
                    text.AppendLine("  当前值   : 读取失败");
                }
            }
            else
            {
                text.AppendLine("  解析失败 : " + error);
            }

            if (!string.IsNullOrEmpty(locator.Fingerprint))
            {
                text.AppendLine("  指纹     : " + locator.Fingerprint);
            }
            if (memory != null)
            {
                List<string> trace = locator.Chain.Trace(memory, profile.ModuleBase);
                for (int i = 0; i < trace.Count; i++)
                {
                    text.AppendLine("  " + trace[i]);
                }
            }
            text.AppendLine();
        }

        /// <summary>列出最近生成的报告，便于界面展示。</summary>
        public List<string> ListReports(int max)
        {
            List<string> files = new List<string>();
            try
            {
                if (!Directory.Exists(ReportsFolder)) { return files; }
                string[] found = Directory.GetFiles(ReportsFolder, "calibration-*.txt");
                Array.Sort(found, StringComparer.OrdinalIgnoreCase);
                Array.Reverse(found);
                int count = found.Length < max ? found.Length : max;
                for (int i = 0; i < count; i++) { files.Add(found[i]); }
            }
            catch (Exception ex)
            {
                Logger.Warn("Profile", "列出校准报告失败：" + ex.Message);
            }
            return files;
        }
    }
}
