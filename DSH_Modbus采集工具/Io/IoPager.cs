using System;
using System.Collections.Generic;

namespace DSH_Modbus采集工具.Io
{
    /// <summary>
    /// IO 分页的**纯计算**部分：页数、页号夹取、当前页切片。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 刻意只做数值运算，不触碰任何控件 —— 分页控件的显隐 / 文字 / 置灰仍由窗口负责，
    /// 这样本类可脱离 WPF 单测，且「点灯前的分页算术」只有一个权威实现。
    /// </para>
    /// <para>
    /// 每页 4 列 × <see cref="RowsPerColumn"/> = <see cref="PageSize"/> 点，X / Y 各自独立页号。
    /// </para>
    /// </remarks>
    internal static class IoPager
    {
        /// <summary>每列固定行数。</summary>
        public const int RowsPerColumn = 16;

        /// <summary>列数固定 4。</summary>
        public const int ColumnCount = 4;

        /// <summary>每页点位数 = 4 × 16 = 64。</summary>
        public const int PageSize = ColumnCount * RowsPerColumn;

        /// <summary>总页数；空列表也算 1 页，便于统一处理分页控件显隐。</summary>
        public static int PageCountOf(List<IoPoint> points) =>
            Math.Max(1, (points.Count + PageSize - 1) / PageSize);

        /// <summary>把页号夹取到 [0, 总页数-1]；用于点位集变化后页号越界的兜底。</summary>
        public static int ClampPage(int pageIndex, int pageCount)
        {
            if (pageIndex > pageCount - 1)
            {
                return pageCount - 1;
            }

            return pageIndex < 0 ? 0 : pageIndex;
        }

        /// <summary>取第 <paramref name="pageIndex"/> 页的点位切片（不足一页则少）。</summary>
        public static List<IoPoint> Slice(List<IoPoint> points, int pageIndex)
        {
            int start = pageIndex * PageSize;
            if (start < 0 || start >= points.Count)
            {
                return new List<IoPoint>();
            }

            int count = Math.Min(PageSize, points.Count - start);
            return points.GetRange(start, count);
        }

        /// <summary>取本页第 <paramref name="col"/> 列的 <see cref="RowsPerColumn"/> 个点（不足则少）。</summary>
        public static List<IoPoint> SliceColumn(List<IoPoint> page, int col)
        {
            int start = col * RowsPerColumn;
            if (start < 0 || start >= page.Count)
            {
                return new List<IoPoint>();
            }

            int count = Math.Min(RowsPerColumn, page.Count - start);
            return page.GetRange(start, count);
        }

        /// <summary>
        /// 翻页：返回新页号；越界（已在首/末页）时返回 -1 表示「不动」。
        /// </summary>
        public static int NextPage(int pageIndex, int pageCount, int delta)
        {
            int next = pageIndex + delta;
            return next < 0 || next >= pageCount ? -1 : next;
        }

        /// <summary>分页控件上显示的页号文本，如 <c>"2/5"</c>。</summary>
        public static string PageText(int pageIndex, int pageCount) => $"{pageIndex + 1}/{pageCount}";
    }
}
