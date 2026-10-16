using System;

namespace DSH_Modbus采集工具.Polling
{
    /// <summary>
    /// 自动重连的退避策略：<c>1 → 2 → 4 → 8 → 10</c> 秒，封顶 10s。
    /// <para>
    /// 刻意做成纯函数：退避表**越界夹取**属于「错了也不报错、只是重连变得没规律」那类问题，
    /// 抽出来才能断言。
    /// </para>
    /// </summary>
    public static class ReconnectPolicy
    {
        /// <summary>退避间隔（秒）。</summary>
        public static readonly int[] DelaysSeconds = { 1, 2, 4, 8, 10 };

        /// <summary>退避上限（秒），即表尾值。</summary>
        public static int MaxDelaySeconds => DelaysSeconds[^1];

        /// <summary>
        /// 第 <paramref name="attempt"/> 次尝试（**从 0 开始**）的退避秒数；
        /// 达到或超出表长后一直取表尾（不再增长）。负数按 0 处理。
        /// </summary>
        public static int DelayForAttempt(int attempt)
        {
            if (attempt < 0)
            {
                attempt = 0;
            }

            return DelaysSeconds[Math.Min(attempt, DelaysSeconds.Length - 1)];
        }
    }
}
