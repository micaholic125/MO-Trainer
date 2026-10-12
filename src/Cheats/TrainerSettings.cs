using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MoTrainer.Util;

namespace MoTrainer.Cheats
{
    /// <summary>
    /// 修改器的运行设置（各功能的开关与目标值）。
    ///
    /// 与校准配置分开存：校准结果是"游戏内存长什么样"的客观事实，
    /// 这里是"我这次想开哪些功能"的主观偏好，两者生命周期不同，混在一起会互相污染。
    /// 文件同样只落在工具自身目录，不写注册表、不写 C 盘。
    /// </summary>
    public sealed class TrainerSettings
    {
        public const string FileName = "trainer.json";
        public const int CurrentVersion = 1;

        private readonly Dictionary<CheatId, uint> _values = new Dictionary<CheatId, uint>();
        private readonly Dictionary<CheatId, bool> _enabled = new Dictionary<CheatId, bool>();

        // ---------------- 读写 ----------------

        public uint GetValue(CheatId id, uint fallback)
        {
            uint value;
            return _values.TryGetValue(id, out value) ? value : fallback;
        }

        public void SetValue(CheatId id, uint value)
        {
            _values[id] = value;
        }

        public bool GetEnabled(CheatId id)
        {
            bool value;
            return _enabled.TryGetValue(id, out value) && value;
        }

        public void SetEnabled(CheatId id, bool enabled)
        {
            _enabled[id] = enabled;
        }

        public void Clear()
        {
            _values.Clear();
            _enabled.Clear();
        }

        /// <summary>
        /// 关闭窗口时该往文件里记什么（**只记"下次启动真的会恢复"的功能**）。
        ///
        /// 规矩（2026-10-12 用户拍板）：
        ///   · **金钱**：记它关闭那一刻的真实状态 —— 下次启动照它恢复；
        ///   · **其余全部记 false**：电力（用户明确选了"不自动开"，理由见 `MainForm` 里那段注释）
        ///     与三个高风险项（无敌 / 秒杀 / 瞬间建造，每次都要用户亲手勾确认，`CLAUDE.md` §4.6）
        ///     都**不会被恢复**，写 true 只是**没人读的死数据**，
        ///     却会让看配置的人以为"某某一直开着"（2026-10-12 就差点据此误判，见开发日志附录三补记）。
        ///
        /// 注意这里的 `moneyWasEnabled` 必须是**关功能之前**取的值 —— 见 `MainForm.OnFormClosing`：
        /// 曾经因为顺序写反（先关功能、再读状态），"金钱下次自动开"这条设计**从未真正生效过**。
        /// </summary>
        public void CaptureExitState(bool moneyWasEnabled)
        {
            CheatId[] all = (CheatId[])Enum.GetValues(typeof(CheatId));
            for (int i = 0; i < all.Length; i++)
            {
                SetEnabled(all[i], all[i] == CheatId.Money && moneyWasEnabled);
            }
        }

        // ---------------- 落盘 ----------------

        public string PathIn(string configFolder)
        {
            return Path.Combine(configFolder, FileName);
        }

        /// <summary>
        /// 读取设置。文件缺失、损坏或版本不识别时返回 false（不抛异常），
        /// 上层据此沿用默认值 —— 配置文件坏掉不该让程序起不来。
        /// </summary>
        public bool Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Logger.Info("Settings", "未找到运行设置，使用默认值：" + path);
                return false;
            }

            try
            {
                string text = File.ReadAllText(path, System.Text.Encoding.UTF8);
                JsonValue json;
                string error;
                if (!JsonValue.TryParse(text, out json, out error))
                {
                    Logger.Warn("Settings", "运行设置解析失败（沿用默认值）：" + error);
                    return false;
                }

                JsonValue features = json.Get("features");
                if (features == null || features.Kind != JsonKind.Object)
                {
                    Logger.Warn("Settings", "运行设置里没有 features 节点（沿用默认值）");
                    return false;
                }

                _values.Clear();
                _enabled.Clear();

                CheatId[] all = (CheatId[])Enum.GetValues(typeof(CheatId));
                for (int i = 0; i < all.Length; i++)
                {
                    JsonValue item = features.Get(CheatNames.Key(all[i]));
                    if (item == null || item.Kind != JsonKind.Object) { continue; }

                    JsonValue enabledNode = item.Get("enabled");
                    if (enabledNode != null) { _enabled[all[i]] = enabledNode.AsBool(false); }

                    JsonValue valueNode = item.Get("value");
                    if (valueNode != null)
                    {
                        uint value;
                        if (HexUtil.TryParseHex(valueNode.AsString(""), out value)) { _values[all[i]] = value; }
                    }
                }

                Logger.Info("Settings", "已加载运行设置：" + Describe());
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("Settings", "读取运行设置失败（沿用默认值）", ex);
                return false;
            }
        }

        public bool Save(string path)
        {
            try
            {
                string folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                JsonValue root = JsonValue.NewObject();
                root.Set("version", CurrentVersion);

                JsonValue features = JsonValue.NewObject();
                CheatId[] all = (CheatId[])Enum.GetValues(typeof(CheatId));
                for (int i = 0; i < all.Length; i++)
                {
                    JsonValue item = JsonValue.NewObject();
                    bool enabled = GetEnabled(all[i]);
                    uint value = GetValue(all[i], 0);
                    if (!enabled && value == 0) { continue; }

                    item.Set("enabled", enabled);
                    item.Set("value", "0x" + value.ToString("X", CultureInfo.InvariantCulture));
                    features.Set(CheatNames.Key(all[i]), item);
                }
                root.Set("features", features);

                JsonValue.SaveFile(path, root, true);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("Settings", "保存运行设置失败：" + path, ex);
                return false;
            }
        }

        public string Describe()
        {
            List<string> on = new List<string>();
            CheatId[] all = (CheatId[])Enum.GetValues(typeof(CheatId));
            for (int i = 0; i < all.Length; i++)
            {
                if (GetEnabled(all[i]))
                {
                    on.Add(CheatNames.Key(all[i]) + "="
                        + GetValue(all[i], 0).ToString(CultureInfo.InvariantCulture));
                }
            }
            return on.Count == 0 ? "全部关闭" : string.Join("｜", on.ToArray());
        }
    }
}
