using System.Collections.Generic;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 切换设备类型前的**影响统计**（P4.3，2026-11-08）。
    ///
    /// <para>
    /// 为什么单独抽出来：这个数字要**先算再问**（弹框里写「将删除 N 个」），
    /// 而算错了会直接误导用户 —— 少算，用户以为没事就点了确定；多算，用户被吓退。
    /// 纯逻辑才能离线断言（见 <c>.tmp/DriverSwitchCheck</c>）。
    /// </para>
    /// <para>
    /// ⚠️ 三类东西的**处置方式不同**，别混：
    /// ① 全局变量的**映射地址**被清空 —— 变量本身保留（名字 / 类型 / 注释都在），只断开关联；
    /// ② 自由监控里**靠元件名**的监控项整条删除 —— 元件名在新品牌下可能根本不存在；
    /// ③ **已导入的 IO 表**整份清除 —— X/Y 点位本来就是按品牌地址表校验过的产物。
    /// 而「引用全局变量的监控行」不删：它靠变量名，变量还在，下一拍会按新地址重新解析。
    /// </para>
    /// </summary>
    public static class DriverSwitchImpact
    {
        /// <summary>
        /// 有多少个全局变量关联了映射地址（= 会被清空的那批）。
        /// </summary>
        public static int CountMappedVariables(IEnumerable<GlobalVariable>? variables)
        {
            if (variables == null)
            {
                return 0;
            }

            int n = 0;
            foreach (GlobalVariable v in variables)
            {
                if (v?.HasAddress == true)
                {
                    n++;
                }
            }

            return n;
        }

        /// <summary>
        /// 有多少个自由监控项是**按元件名**监控的（= 会被整条删除的那批）。
        /// <para>
        /// 判据是「不是变量行」：变量行（<see cref="DataPoint.IsVariable"/>）靠变量名，
        /// 换品牌不受影响；其余都是元件名（<c>HD100</c> / <c>D0</c> 这种），必须删。
        /// </para>
        /// </summary>
        public static int CountElementMonitorItems(IEnumerable<DataPoint>? points)
        {
            if (points == null)
            {
                return 0;
            }

            int n = 0;
            foreach (DataPoint p in points)
            {
                if (p != null && !p.IsVariable)
                {
                    n++;
                }
            }

            return n;
        }

        /// <summary>
        /// 已导入的 IO 表里有多少个点位（= 会被整份清除的那份表）。
        /// <para>
        /// 入参传 X / Y 两个列表的总数即可（调用方算，避免这里依赖 <c>Io/IoPointSet</c>
        /// —— 那样 <c>Modeling/</c> 就要反向引用 <c>Io/</c>，方向不对）。
        /// </para>
        /// </summary>
        public static int CountIoPoints(int xCount, int yCount) =>
            Math.Max(0, xCount) + Math.Max(0, yCount);

        /// <summary>
        /// 拼给确认框看的正文。三项都为 0 时返回空串 —— 调用方据此**不弹框、直接切**。
        /// <para>
        /// 「都为 0 就不问」是刻意的：没有任何东西会被删的时候还拦一道，
        /// 会让用户养成「看都不看就点确定」的习惯，真正有删除时那道确认也就失效了。
        /// </para>
        /// </summary>
        public static string BuildPrompt(
            string? targetDriverName, int mappedVariables, int elementItems, int ioPoints)
        {
            if (mappedVariables <= 0 && elementItems <= 0 && ioPoints <= 0)
            {
                return string.Empty;
            }

            var lines = new List<string>
            {
                $"切换到「{targetDriverName}」后，以下内容将被删除且无法撤销：",
                string.Empty,
            };

            if (mappedVariables > 0)
            {
                lines.Add($"· 全局变量表中的 {mappedVariables} 个映射地址（变量本身保留）");
            }

            if (elementItems > 0)
            {
                lines.Add($"· 自由监控表中的 {elementItems} 个元件监控项");
            }

            if (ioPoints > 0)
            {
                lines.Add($"· 已导入的 IO 表（{ioPoints} 个点位）");
            }

            lines.Add(string.Empty);
            lines.Add("确定要切换吗？");

            return string.Join("\n", lines);
        }
    }
}
