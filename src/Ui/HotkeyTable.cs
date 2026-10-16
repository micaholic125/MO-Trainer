using System;
using System.Windows.Forms;
using MoTrainer.Cheats;

namespace MoTrainer.Ui
{
    /// <summary>一条全局热键的定义：id / 按键 / 标签（"1"~"5"）/ 对应功能。</summary>
    internal sealed class HotkeyDef
    {
        /// <summary>RegisterHotKey / WM_HOTKEY 用的编号，只要求本窗口内唯一。</summary>
        public readonly int Id;

        public readonly Keys Key;

        /// <summary>显示用的按键尾号（"1" 即 Ctrl + Alt + 1）。</summary>
        public readonly string Label;

        public readonly CheatId Feature;

        public HotkeyDef(int id, Keys key, string label, CheatId feature)
        {
            Id = id;
            Key = key;
            Label = label;
            Feature = feature;
        }
    }

    /// <summary>
    /// 全局热键的**唯一**定义处 —— 注册（RegisterHotKey）与分发（按下之后切换哪个功能）
    /// 都从这里取，界面上的「热键说明」也照这张表生成。
    ///
    /// 为什么必须只有一处（2026-10-16 用户真机反馈）：
    /// 原先"注册"与"按下之后怎么办"是两处各写各的 —— 注册名单上有 5 条，
    /// 而处理按键的 switch 里只有 4 个分支，`HotkeyInstantBuild` 直接落进 `default: return;`。
    /// 表现是：启动日志里明明白白写着「热键已注册：Ctrl+Alt+5」，玩家按下去却
    /// **什么都不发生，也没有任何报错** —— 与 `CLAUDE.md` §4.2 禁止的
    /// "开关能点、程序悄悄什么都不做"是同一类失败。
    /// 现在两处共用一个数组，"注册了却没人处理"在结构上不可能再发生；
    /// 自检里还有一条断言盯着这张表（见 `SelfTest.CheckHotkeys`）。
    /// </summary>
    internal static class HotkeyTable
    {
        // 编号取 0xA001 起，与其它窗口消息 id 区分开。
        private const int IdMoney = 0xA001;
        private const int IdPower = 0xA002;
        private const int IdInvincible = 0xA003;
        private const int IdKillEnemies = 0xA004;
        private const int IdInstantBuild = 0xA005;

        /// <summary>说明文案里按键与功能名之间的对齐空格（照搬原界面的排版，别改）。</summary>
        private const string Gap = "        ";

        /// <summary>
        /// 全部全局热键，按界面上的显示顺序排列。
        /// **新增功能要加热键时只改这一处**（并同时补 `SelfTest.CheckHotkeys` 的断言）。
        /// </summary>
        public static readonly HotkeyDef[] All = new HotkeyDef[]
        {
            new HotkeyDef(IdMoney, Keys.D1, "1", CheatId.Money),
            new HotkeyDef(IdPower, Keys.D2, "2", CheatId.PowerOutput),
            new HotkeyDef(IdInvincible, Keys.D3, "3", CheatId.SelectedInvincible),
            new HotkeyDef(IdKillEnemies, Keys.D4, "4", CheatId.KillEnemies),
            new HotkeyDef(IdInstantBuild, Keys.D5, "5", CheatId.InstantBuild)
        };

        /// <summary>
        /// 按 id 找一条热键。**找不到返回 null，调用方必须处理** ——
        /// 正是"找不到就当没事发生"造成了 2026-10-16 那个"按了没反应"的 bug。
        /// </summary>
        public static HotkeyDef Find(int id)
        {
            for (int i = 0; i < All.Length; i++)
            {
                if (All[i].Id == id) { return All[i]; }
            }
            return null;
        }

        /// <summary>热键说明里的一行，例："Ctrl + Alt + 1        金钱开关"。</summary>
        public static string Describe(HotkeyDef def, string featureName)
        {
            if (def == null) { return ""; }
            return "Ctrl + Alt + " + def.Label + Gap + featureName + "开关";
        }
    }
}
