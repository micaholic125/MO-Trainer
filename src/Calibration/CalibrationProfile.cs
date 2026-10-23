using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MoTrainer.Core;
using MoTrainer.Util;

// FieldLocator 需要 Logger（候选链回退时记录日志）

namespace MoTrainer.Calibration
{
    /// <summary>
    /// 一条定位结果：相对模块基址的偏移链 + 校准时的观测指纹。
    /// Chain 为空即表示"未校准"，对应功能必须自动禁用。
    /// </summary>
    public sealed class FieldLocator
    {
        /// <summary>最多保留多少条候选链。多条链用于对抗"静态根随会话变化"的风险。</summary>
        public const int MaxChains = 8;

        private readonly List<PointerChain> _chains = new List<PointerChain>();

        public string Fingerprint = "";
        public string Note = "";

        /// <summary>字段的存储宽度（4 或 2 字节）。</summary>
        public int ValueWidth = 4;

        /// <summary>游戏显示值与存储值的倍数（1 / 10 / 100）。</summary>
        public int ValueScale = 1;

        /// <summary>
        /// 目标就是一个静态地址（游戏模块内的**全局变量**）：解析结果 = moduleBase + StaticOffset，
        /// **不做任何解引用**。设置后优先于指针链。
        ///
        /// 为什么需要它：真机实测「选中数量」是模块 .data 里的全局变量 ——
        /// 它的地址本身就是固定的，压根不需要指针链。硬套指针链反而会去解引用它，
        /// 读到的变成"变量内容"而不是"变量本身"，结果必然全错。
        /// 附带好处：模块基址固定（无 ASLR），静态偏移跨会话恒定，比指针链更稳。
        /// </summary>
        public bool HasStaticAddress;
        public uint StaticOffset;

        /// <summary>主链（第一条候选）。写回时以它为准。</summary>
        public PointerChain Chain
        {
            get { return _chains.Count == 0 ? PointerChain.Empty : _chains[0]; }
            set
            {
                _chains.Clear();
                if (value != null && !value.IsEmpty) { _chains.Add(value); }
            }
        }

        /// <summary>全部候选链，按优先顺序排列。</summary>
        public List<PointerChain> Chains
        {
            get { return new List<PointerChain>(_chains); }
        }

        public int ChainCount { get { return _chains.Count; } }

        public bool IsCalibrated { get { return HasStaticAddress || _chains.Count > 0; } }

        public void SetOffsets(uint[] offsets)
        {
            _chains.Clear();
            if (offsets != null && offsets.Length > 0) { _chains.Add(new PointerChain(offsets)); }
        }

        /// <summary>
        /// 一次性写入多条候选链。
        /// 实测发现：指向 House 对象的静态全局不止一个，且不同会话里"能被找到的那几个"会变，
        /// 因此必须把当时找到的多条链全部留下，运行时依次尝试，而不是只押注一条。
        /// </summary>
        public void SetFromPaths(List<PointerPath> paths)
        {
            _chains.Clear();
            if (paths == null) { return; }

            for (int i = 0; i < paths.Count && _chains.Count < MaxChains; i++)
            {
                PointerPath path = paths[i];
                if (path == null || path.IsEmpty) { continue; }

                PointerChain chain = path.ToChain();
                if (!ContainsChain(chain)) { _chains.Add(chain); }
            }
        }

        private bool ContainsChain(PointerChain chain)
        {
            uint[] target = chain.Offsets;
            for (int i = 0; i < _chains.Count; i++)
            {
                uint[] other = _chains[i].Offsets;
                if (other.Length != target.Length) { continue; }
                bool same = true;
                for (int k = 0; k < target.Length; k++)
                {
                    if (other[k] != target[k]) { same = false; break; }
                }
                if (same) { return true; }
            }
            return false;
        }

        public void Clear()
        {
            _chains.Clear();
            HasStaticAddress = false;
            StaticOffset = 0;
            Fingerprint = "";
        }

        /// <summary>把目标标成一个静态全局变量地址。</summary>
        public void SetStaticAddress(uint offset)
        {
            _chains.Clear();
            HasStaticAddress = true;
            StaticOffset = offset;
        }

        public void AddChain(PointerChain chain)
        {
            if (chain == null || chain.IsEmpty) { return; }
            if (_chains.Count >= MaxChains) { return; }
            if (!ContainsChain(chain)) { _chains.Add(chain); }
        }

        /// <summary>
        /// 依次尝试每条候选链，返回第一条能解析成功的地址。
        /// 全部失败时 error 会汇总每条链的失败原因。
        /// </summary>
        public bool TryResolve(MemoryIO memory, uint moduleBase, out uint address, out string error)
        {
            address = 0;
            error = "";

            // 静态全局变量：地址 = 模块基址 + 偏移，直接就是目标本身，不需要解引用
            if (HasStaticAddress)
            {
                address = moduleBase + StaticOffset;
                return true;
            }

            if (_chains.Count == 0)
            {
                error = "未校准：偏移链为空";
                return false;
            }

            StringBuilder failures = new StringBuilder();
            for (int i = 0; i < _chains.Count; i++)
            {
                uint candidate;
                string reason;
                if (_chains[i].TryResolve(memory, moduleBase, out candidate, out reason))
                {
                    address = candidate;
                    if (i > 0)
                    {
                        Logger.Info("Profile", string.Format(CultureInfo.InvariantCulture,
                            "主链解析失败，已改用第 {0} 条候选链：{1}", i + 1, _chains[i].Serialize()));
                    }
                    return true;
                }

                if (failures.Length > 0) { failures.Append("；"); }
                failures.Append(string.Format(CultureInfo.InvariantCulture,
                    "第{0}条({1}) {2}", i + 1, _chains[i].Serialize(), reason));
            }

            error = "全部 " + _chains.Count + " 条候选链都解析失败：" + failures;
            return false;
        }

        public uint ResolveOrZero(MemoryIO memory, uint moduleBase)
        {
            uint address;
            string error;
            return TryResolve(memory, moduleBase, out address, out error) ? address : 0;
        }

        public JsonValue ToJson()
        {
            JsonValue json = JsonValue.NewObject();

            // 兼容旧格式：chain 仍是主链
            json.Set("chain", ChainToJson(Chain));

            JsonValue all = JsonValue.NewArray();
            for (int i = 0; i < _chains.Count; i++) { all.Add(ChainToJson(_chains[i])); }
            json.Set("chains", all);

            json.Set("chainCount", _chains.Count);
            json.Set("depth", Chain.Depth);
            json.Set("fingerprint", Fingerprint == null ? "" : Fingerprint);
            json.Set("note", Note == null ? "" : Note);
            json.Set("valueWidth", ValueWidth);
            json.Set("valueScale", ValueScale);

            // 静态全局变量：只存模块内偏移，模块基址固定（无 ASLR），跨会话恒定
            json.Set("hasStaticAddress", HasStaticAddress);
            json.Set("staticAddress", HasStaticAddress
                ? "0x" + StaticOffset.ToString("X", CultureInfo.InvariantCulture)
                : "");

            json.Set("calibrated", IsCalibrated);
            return json;
        }

        private static JsonValue ChainToJson(PointerChain chain)
        {
            JsonValue offsets = JsonValue.NewArray();
            if (chain != null)
            {
                uint[] values = chain.Offsets;
                for (int i = 0; i < values.Length; i++)
                {
                    offsets.Add("0x" + values[i].ToString("X", CultureInfo.InvariantCulture));
                }
            }
            return offsets;
        }

        private static PointerChain ChainFromJson(JsonValue array)
        {
            if (array == null || array.Kind != JsonKind.Array) { return PointerChain.Empty; }

            List<uint> values = new List<uint>();
            for (int i = 0; i < array.Count; i++)
            {
                uint value;
                if (HexUtil.TryParseHex(array.GetAt(i).AsString(""), out value)) { values.Add(value); }
            }
            return values.Count == 0 ? PointerChain.Empty : new PointerChain(values.ToArray());
        }

        public static FieldLocator FromJson(JsonValue json)
        {
            FieldLocator locator = new FieldLocator();
            if (json == null) { return locator; }

            // 优先读新的 chains 数组，缺失时退回旧的单链字段
            JsonValue all = json.Get("chains");
            if (all != null && all.Kind == JsonKind.Array && all.Count > 0)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    locator.AddChain(ChainFromJson(all.GetAt(i)));
                }
            }
            else
            {
                locator.AddChain(ChainFromJson(json.Get("chain")));
            }

            locator.Fingerprint = json.Get("fingerprint") == null ? "" : json.Get("fingerprint").AsString("");
            locator.Note = json.Get("note") == null ? "" : json.Get("note").AsString("");
            locator.ValueWidth = json.Get("valueWidth") == null ? 4 : json.Get("valueWidth").AsInt(4);
            locator.ValueScale = json.Get("valueScale") == null ? 1 : json.Get("valueScale").AsInt(1);
            if (locator.ValueWidth != 2) { locator.ValueWidth = 4; }
            if (locator.ValueScale <= 0) { locator.ValueScale = 1; }

            uint staticOffset;
            string staticText = json.Get("staticAddress") == null ? "" : json.Get("staticAddress").AsString("");
            if (staticText.Length > 0 && HexUtil.TryParseHex(staticText, out staticOffset))
            {
                locator.SetStaticAddress(staticOffset);
            }
            return locator;
        }

        /// <summary>数值编码的文字描述，例如 "4 字节×10"。</summary>
        public string DescribeEncoding()
        {
            if (ValueWidth == 4 && ValueScale == 1) { return "4 字节"; }
            StringBuilder text = new StringBuilder();
            text.Append(ValueWidth).Append(" 字节");
            if (ValueScale != 1) { text.Append("×").Append(ValueScale); }
            return text.ToString();
        }

        public string Describe(uint moduleBase)
        {
            if (HasStaticAddress)
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "0x{0:X8}+0x{1:X}（静态全局变量，无需指针链）", moduleBase, StaticOffset);
            }
            if (!IsCalibrated) { return "未校准"; }

            StringBuilder text = new StringBuilder(Chain.Format(moduleBase));
            if (_chains.Count > 1) { text.Append("（另备 " + (_chains.Count - 1) + " 条候选链）"); }
            return text.ToString();
        }
    }

    /// <summary>
    /// 校准结果数据模型。**缺失字段即视为未校准**，对应功能必须自动禁用并提示重新校准。
    /// 所有地址都是"相对模块基址的偏移链"，模块基址变化时无需重新校准。
    /// </summary>
    public sealed class CalibrationProfile
    {
        public const int CurrentVersion = 1;

        // ---- 环境指纹 ----
        public int Version = CurrentVersion;
        public string GameVersion = "";
        public string EngineExe = "gamemd.exe";
        public string ExeCrc32 = "";
        public uint ModuleBase;
        public uint ModuleSize;
        public string CreatedAt = "";
        public string UpdatedAt = "";
        public string Notes = "";

        // ---- 指针链 ----
        public FieldLocator Money = new FieldLocator();
        public FieldLocator PowerOutput = new FieldLocator();
        public FieldLocator PowerDrain = new FieldLocator();
        public FieldLocator SelectedCount = new FieldLocator();
        public FieldLocator SelectedList = new FieldLocator();
        public FieldLocator TechnoArray = new FieldLocator();
        public FieldLocator TechnoArrayCount = new FieldLocator();
        public FieldLocator MapShroud = new FieldLocator();

        /// <summary>
        /// 作废用已废弃方法定位出来的字段。
        ///
        /// 电力早期用"窗口差分"定位（靠"造东西时哪个数值变了"来猜），
        /// 实测会把干扰字段当成电力（选出的字段数值 11361，量级明显不对）。
        /// 这类记录必须丢弃 —— 否则它会被当成"已校准"，直接把正确的重新校准流程挡在外面。
        /// </summary>
        public int DiscardDeprecatedLocators()
        {
            int discarded = 0;
            if (IsDeprecated(PowerDrain)) { PowerDrain.Clear(); PowerDrain.Note = ""; discarded++; }
            if (IsDeprecated(PowerOutput)) { PowerOutput.Clear(); PowerOutput.Note = ""; discarded++; }
            return discarded;
        }

        private static bool IsDeprecated(FieldLocator locator)
        {
            return locator != null && locator.IsCalibrated
                && locator.Note != null
                && locator.Note.IndexOf("窗口差分", StringComparison.Ordinal) >= 0;
        }

        // ---- 对象内的字段偏移（不是指针链，是结构体内部偏移）----
        public uint OffsetTechnoOwner;
        public uint OffsetTechnoHealth;
        public uint OffsetTechnoIronCurtain;
        public uint OffsetProductionTimer;

        /// <summary>
        /// 单位满血值。由「单位校准」页在记录满血快照时测得 ——
        /// 无敌功能就是把这个值持续写回血量字段。
        /// </summary>
        public uint TechnoMaxHealth;

        // ---- 地图数据 ----
        public uint ShroudSize;

        // ---- 用户偏好 ----
        public uint DefaultMoney = 100000;

        // ============================================================ 状态查询

        public bool HasMoney { get { return Money.IsCalibrated; } }
        public bool HasPowerOutput { get { return PowerOutput.IsCalibrated; } }
        public bool HasPowerDrain { get { return PowerDrain.IsCalibrated; } }
        public bool HasSelection { get { return SelectedList.IsCalibrated; } }
        public bool HasTechnoArray { get { return TechnoArray.IsCalibrated; } }
        public bool HasMapShroud { get { return MapShroud.IsCalibrated; } }

        public bool IsCalibratedAtAll
        {
            // 「黑幕」不计入：全图视野（S6-3）至今没有定位成功，交付版不提供该功能。
            // 用户 2026-10-17 决定把它从玩家可见的校准清单里拿掉，避免玩家去校准一个
            // 交付版里根本用不上的字段。字段本身仍保留在 profile 里，供将来继续排查。
            get { return HasMoney || HasPowerOutput || HasPowerDrain || HasSelection || HasTechnoArray; }
        }

        /// <summary>列出尚未校准的项，用于界面提示"还差什么"。</summary>
        public List<string> MissingItems()
        {
            List<string> missing = new List<string>();
            if (!HasMoney) { missing.Add("金钱"); }
            if (!HasPowerOutput && !HasPowerDrain) { missing.Add("电力"); }
            if (!HasSelection) { missing.Add("选中单位列表"); }
            if (!HasTechnoArray) { missing.Add("单位对象数组"); }
            // 不列「地图黑幕数据」：全图视野未交付（S6-3 未定论），见 IsCalibratedAtAll 的注释。
            return missing;
        }

        public int CalibratedItemCount()
        {
            int count = 0;
            if (HasMoney) { count++; }
            if (HasPowerOutput || HasPowerDrain) { count++; }
            if (HasSelection) { count++; }
            if (HasTechnoArray) { count++; }
            return count;
        }

        public void ClearAll()
        {
            Money.Clear();
            PowerOutput.Clear();
            PowerDrain.Clear();
            SelectedCount.Clear();
            SelectedList.Clear();
            TechnoArray.Clear();
            TechnoArrayCount.Clear();
            MapShroud.Clear();

            OffsetTechnoOwner = 0;
            OffsetTechnoHealth = 0;
            OffsetTechnoIronCurtain = 0;
            OffsetProductionTimer = 0;
            ShroudSize = 0;
        }

        /// <summary>环境指纹是否与当前游戏一致（不一致说明换了版本，需要重新校准）。</summary>
        public bool MatchesEnvironment(uint moduleBase, uint moduleSize, string exeCrc32)
        {
            if (ModuleBase != 0 && ModuleBase != moduleBase) { return false; }
            if (ModuleSize != 0 && moduleSize != 0 && ModuleSize != moduleSize) { return false; }
            if (!string.IsNullOrEmpty(ExeCrc32) && !string.IsNullOrEmpty(exeCrc32)
                && !string.Equals(ExeCrc32, exeCrc32, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return true;
        }

        // ============================================================ 序列化

        public JsonValue ToJson()
        {
            JsonValue json = JsonValue.NewObject();

            json.Set("version", Version);
            json.Set("gameVersion", GameVersion);
            json.Set("engineExe", EngineExe);
            json.Set("exeCrc32", ExeCrc32);
            json.SetHex("moduleBase", ModuleBase);
            json.SetHex("moduleSize", ModuleSize);
            json.Set("createdAt", CreatedAt);
            json.Set("updatedAt", UpdatedAt);
            json.Set("notes", Notes);

            JsonValue locators = JsonValue.NewObject();
            locators.Set("money", Money.ToJson());
            locators.Set("powerOutput", PowerOutput.ToJson());
            locators.Set("powerDrain", PowerDrain.ToJson());
            locators.Set("selectedCount", SelectedCount.ToJson());
            locators.Set("selectedList", SelectedList.ToJson());
            locators.Set("technoArray", TechnoArray.ToJson());
            locators.Set("technoArrayCount", TechnoArrayCount.ToJson());
            locators.Set("mapShroud", MapShroud.ToJson());
            json.Set("locators", locators);

            JsonValue offsets = JsonValue.NewObject();
            offsets.SetHex("technoOwner", OffsetTechnoOwner);
            offsets.SetHex("technoHealth", OffsetTechnoHealth);
            offsets.SetHex("technoIronCurtain", OffsetTechnoIronCurtain);
            offsets.SetHex("productionTimer", OffsetProductionTimer);
            offsets.SetHex("technoMaxHealth", TechnoMaxHealth);
            json.Set("fieldOffsets", offsets);

            JsonValue map = JsonValue.NewObject();
            map.SetHex("shroudSize", ShroudSize);
            json.Set("map", map);

            JsonValue prefs = JsonValue.NewObject();
            prefs.Set("defaultMoney", DefaultMoney);
            json.Set("preferences", prefs);

            return json;
        }

        public static CalibrationProfile FromJson(JsonValue json)
        {
            CalibrationProfile profile = new CalibrationProfile();
            if (json == null) { return profile; }

            profile.Version = GetInt(json, "version", CurrentVersion);
            profile.GameVersion = GetString(json, "gameVersion", "");
            profile.EngineExe = GetString(json, "engineExe", "gamemd.exe");
            profile.ExeCrc32 = GetString(json, "exeCrc32", "");
            profile.ModuleBase = GetUInt(json, "moduleBase", 0);
            profile.ModuleSize = GetUInt(json, "moduleSize", 0);
            profile.CreatedAt = GetString(json, "createdAt", "");
            profile.UpdatedAt = GetString(json, "updatedAt", "");
            profile.Notes = GetString(json, "notes", "");

            JsonValue locators = json.Get("locators");
            if (locators != null)
            {
                profile.Money = FieldLocator.FromJson(locators.Get("money"));
                profile.PowerOutput = FieldLocator.FromJson(locators.Get("powerOutput"));
                profile.PowerDrain = FieldLocator.FromJson(locators.Get("powerDrain"));
                profile.SelectedCount = FieldLocator.FromJson(locators.Get("selectedCount"));
                profile.SelectedList = FieldLocator.FromJson(locators.Get("selectedList"));
                profile.TechnoArray = FieldLocator.FromJson(locators.Get("technoArray"));
                profile.TechnoArrayCount = FieldLocator.FromJson(locators.Get("technoArrayCount"));
                profile.MapShroud = FieldLocator.FromJson(locators.Get("mapShroud"));
            }

            JsonValue offsets = json.Get("fieldOffsets");
            if (offsets != null)
            {
                profile.OffsetTechnoOwner = GetUInt(offsets, "technoOwner", 0);
                profile.OffsetTechnoHealth = GetUInt(offsets, "technoHealth", 0);
                profile.OffsetTechnoIronCurtain = GetUInt(offsets, "technoIronCurtain", 0);
                profile.OffsetProductionTimer = GetUInt(offsets, "productionTimer", 0);
                profile.TechnoMaxHealth = GetUInt(offsets, "technoMaxHealth", 0);
            }

            JsonValue map = json.Get("map");
            if (map != null) { profile.ShroudSize = GetUInt(map, "shroudSize", 0); }

            JsonValue prefs = json.Get("preferences");
            if (prefs != null) { profile.DefaultMoney = GetUInt(prefs, "defaultMoney", 100000); }

            return profile;
        }

        private static string GetString(JsonValue json, string key, string fallback)
        {
            JsonValue value = json.Get(key);
            return value == null ? fallback : value.AsString(fallback);
        }

        private static int GetInt(JsonValue json, string key, int fallback)
        {
            JsonValue value = json.Get(key);
            return value == null ? fallback : value.AsInt(fallback);
        }

        private static uint GetUInt(JsonValue json, string key, uint fallback)
        {
            JsonValue value = json.Get(key);
            if (value == null) { return fallback; }

            // 允许写成 "0x793000" 这样的十六进制字符串
            string text = value.AsString(null);
            if (!string.IsNullOrEmpty(text) && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                uint parsed;
                if (HexUtil.TryParseHex(text, out parsed)) { return parsed; }
                return fallback;
            }
            return value.AsUInt(fallback);
        }

        /// <summary>一行式摘要，用于日志。</summary>
        public string Describe()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendFormat(CultureInfo.InvariantCulture,
                "已校准 {0} 项｜金钱={1}｜电力={2}｜选中={3}｜对象数组={4}",
                CalibratedItemCount(),
                HasMoney ? "有" : "无",
                (HasPowerOutput || HasPowerDrain) ? "有" : "无",
                HasSelection ? "有" : "无",
                HasTechnoArray ? "有" : "无");
            // 不写「黑幕」：该字段只对未交付的全图视野有意义，见 IsCalibratedAtAll 的注释。

            List<string> missing = MissingItems();
            if (missing.Count > 0)
            {
                builder.Append("｜待校准：");
                for (int i = 0; i < missing.Count; i++)
                {
                    if (i > 0) { builder.Append('、'); }
                    builder.Append(missing[i]);
                }
            }
            return builder.ToString();
        }
    }
}
