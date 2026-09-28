using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using MoTrainer.Core;

namespace MoTrainer.Ui
{
    /// <summary>
    /// 全界面设计令牌与绘图工具的唯一来源。
    /// 业务代码**禁止硬编码颜色与字号**，一律从这里取。
    /// </summary>
    public static class Theme
    {
        private const string FontFamily = "Microsoft YaHei UI";
        private const string MonoFamily = "Consolas";

        // ---------------- 配色 ----------------
        public static readonly Color Bg0 = FromHex("#14161A");
        public static readonly Color Bg1 = FromHex("#1B1F26");
        public static readonly Color Bg2 = FromHex("#22272F");
        public static readonly Color Border = FromHex("#2A3038");

        public static readonly Color Text0 = FromHex("#E8EAED");
        public static readonly Color Text1 = FromHex("#9AA3AD");
        public static readonly Color Text2 = FromHex("#6B7480");

        public static readonly Color Primary = FromHex("#2E7DFF");
        public static readonly Color PrimaryDark = FromHex("#1E6BE8");
        public static readonly Color PrimaryLight = FromHex("#5AA0FF");

        public static readonly Color Success = FromHex("#34C759");
        public static readonly Color Warning = FromHex("#FF9F0A");
        public static readonly Color Danger = FromHex("#FF453A");

        // ---------------- 字体（px * 0.75 = pt） ----------------
        public static readonly Font Heading = new Font(FontFamily, 16.5f, FontStyle.Bold);
        public static readonly Font Subheading = new Font(FontFamily, 11.25f, FontStyle.Regular);
        public static readonly Font Body = new Font(FontFamily, 9.75f, FontStyle.Regular);
        public static readonly Font BodyBold = new Font(FontFamily, 9.75f, FontStyle.Bold);
        public static readonly Font Small = new Font(FontFamily, 8.25f, FontStyle.Regular);
        public static readonly Font Mono = new Font(MonoFamily, 9f, FontStyle.Regular);

        // ---------------- 度量 ----------------
        public const int RadiusCard = 8;
        public const int RadiusSwitch = 12;
        public const int CardPadding = 16;
        public const int GapCard = 12;
        public const int NavWidth = 180;
        public const int StatusHeight = 56;
        public const int LogHeight = 120;

        private static bool _dwmDisabled;

        public static Color FromHex(string hex)
        {
            if (string.IsNullOrEmpty(hex)) { return Color.Magenta; }
            string s = hex.TrimStart('#');
            if (s.Length != 6) { return Color.Magenta; }
            int r = int.Parse(s.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int g = int.Parse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int b = int.Parse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return Color.FromArgb(r, g, b);
        }

        public static Color Mix(Color a, Color b, double t)
        {
            if (t < 0) { t = 0; }
            if (t > 1) { t = 1; }
            int r = (int)Math.Round(a.R + (b.R - a.R) * t);
            int g = (int)Math.Round(a.G + (b.G - a.G) * t);
            int bl = (int)Math.Round(a.B + (b.B - a.B) * t);
            return Color.FromArgb(r, g, bl);
        }

        public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (radius <= 0)
            {
                path.AddRectangle(bounds);
                return path;
            }
            int d = radius * 2;
            if (d > bounds.Width) { d = bounds.Width; }
            if (d > bounds.Height) { d = bounds.Height; }

            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>开启 Win10 1809+ 的深色标题栏，让非客户区与整体风格一致。</summary>
        public static void EnableDarkTitleBar(Form form)
        {
            if (form == null || _dwmDisabled) { return; }
            try
            {
                int value = 1;
                int hr = Win32Api.DwmSetWindowAttribute(
                    form.Handle, Win32Api.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
                if (hr != 0)
                {
                    hr = Win32Api.DwmSetWindowAttribute(
                        form.Handle, Win32Api.DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref value, sizeof(int));
                }
                if (hr != 0)
                {
                    _dwmDisabled = true;
                }
            }
            catch
            {
                // 旧系统没有 dwmapi 或属性不支持，忽略即可
                _dwmDisabled = true;
            }
        }

        /// <summary>按连接状态取状态色。</summary>
        public static Color StateColor(AttachState state)
        {
            switch (state)
            {
                case AttachState.Attached: return Success;
                case AttachState.Error: return Danger;
                default: return Warning;
            }
        }
    }
}
