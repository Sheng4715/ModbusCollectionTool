namespace DSH_Modbus采集工具.Ui
{
    /// <summary>
    /// drawio 颜色盘（30 色）与字号候选：状态编辑窗与指示灯编辑窗共用同一份，
    /// 避免两处各抄一遍、日后改出一份色差或字号分叉。
    /// </summary>
    internal static class ColorPalette
    {
        /// <summary>drawio 原色盘，10 列 × 3 行。</summary>
        public static readonly string[] Colors =
        {
            "#FFFFFF", "#E6E6E6", "#CCCCCC", "#B3B3B3", "#999999",
            "#808080", "#666666", "#4D4D4D", "#333333", "#000000",
            "#F8CECC", "#EA6B66", "#B85450", "#D5E8D4", "#82B366",
            "#DAE8FC", "#6C8EBF", "#FFF2CC", "#FFCD28", "#FFE6CC",
            "#D79B00", "#E1D5E7", "#9673A6", "#A20025", "#1BA1E2",
            "#0050EF", "#60A917", "#008A00", "#F0A30A", "#E51400",
        };

        /// <summary>文字字号候选（模型各自另行夹取范围）。</summary>
        public static readonly double[] FontSizes = { 8, 10, 12, 14, 15, 16, 18, 20, 24, 28 };
    }
}
