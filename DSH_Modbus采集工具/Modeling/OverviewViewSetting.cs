using System;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 总览页页头「当前视图」下拉框的三项。
    /// <para>
    /// ⚠️ **数值必须与 <c>MainWindow.xaml</c> 里 <c>OverviewViewCombo</c> 的项顺序一致**
    /// （0 = PLC状态机、1 = 生产数据、2 = 数据采集）：界面按下标取视图，
    /// 而落盘用枚举名，两者靠 <see cref="OverviewViewSetting"/> 换算。
    /// </para>
    /// </summary>
    public enum OverviewViewMode
    {
        /// <summary>PLC 状态机画布（第 0 项，出厂默认）。</summary>
        MachineState = 0,

        /// <summary>生产数据占位（第 1 项，尚未实现）。</summary>
        ProductionData = 1,

        /// <summary>数据采集（第 2 项：实时数据 / 历史数据）。</summary>
        Acquisition = 2,
    }

    /// <summary>
    /// 「当前视图」的取值规则：枚举 ↔ 下拉框索引 ↔ 落盘字符串。
    /// <para>
    /// **纯逻辑，不引 WPF**，可离线断言。抽出来的理由与 <c>ByteOrderSettings</c> 同一层次：
    /// 换算是「三处都要对」的东西（XAML 项顺序、界面下标、json 内容），
    /// 散在窗口里迟早有一处跟不上。
    /// </para>
    /// <para>
    /// ⚠️ **落盘存枚举名而不是数字**（与 <c>ThemeMode</c> 同一取舍）：
    /// 以后往中间插一项，数字会被静默解释成别的视图，枚举名至少能在
    /// <see cref="TryParse"/> 里被识别为「不认识」而退回默认值。
    /// </para>
    /// </summary>
    public static class OverviewViewSetting
    {
        /// <summary>出厂默认视图（= 下拉框第 0 项，与 XAML 的 <c>SelectedIndex="0"</c> 一致）。</summary>
        public const OverviewViewMode Default = OverviewViewMode.MachineState;

        /// <summary>下拉框的项数。改 XAML 的项数时必须同步改这里。</summary>
        public const int Count = 3;

        /// <summary>枚举 → 下拉框下标。越界值退回 <see cref="Default"/> 的下标（不抛）。</summary>
        public static int ToIndex(OverviewViewMode mode)
        {
            int index = (int)mode;
            return index >= 0 && index < Count ? index : (int)Default;
        }

        /// <summary>下拉框下标 → 枚举。越界（如 -1 表示没选中）退回 <see cref="Default"/>。</summary>
        public static OverviewViewMode FromIndex(int index) =>
            index >= 0 && index < Count ? (OverviewViewMode)index : Default;

        /// <summary>
        /// 解析落盘字符串。空串 / 未设置 / 手工改坏 → <c>false</c>（调用方退回 <see cref="Default"/>）。
        /// <para>
        /// ⚠️ **只认枚举名，不认数字**：<c>Enum.TryParse</c> 会把纯数字串也解析成功
        /// （<c>"2"</c> → <see cref="OverviewViewMode.Acquisition"/>），而 <c>Enum.IsDefined</c>
        /// 拦不住它 —— 2 本来就是有名字的成员。若放开，手工改坏的 json 会被**静默**解释成
        /// 某个具体视图，而存名字的初衷恰恰是「对不上就退回默认值」。
        /// 这条是端到端实测抓出来的（喂 <c>"2"</c> 时界面跳到了数据采集）。
        /// </para>
        /// </summary>
        public static bool TryParse(string? raw, out OverviewViewMode mode)
        {
            mode = Default;

            string text = (raw ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return false;
            }

            foreach (string name in Enum.GetNames<OverviewViewMode>())
            {
                if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
                {
                    mode = Enum.Parse<OverviewViewMode>(name);
                    return true;
                }
            }

            return false;
        }

        /// <summary>枚举 → 落盘字符串（枚举名）。</summary>
        public static string Format(OverviewViewMode mode) => mode.ToString();
    }
}