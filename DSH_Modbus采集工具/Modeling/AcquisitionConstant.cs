using System;
using System.Globalization;
using System.Text;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 采集「来源」的常量字面量（P3.1，2026-11-05 用户定案 **方案 B**）。
    /// <para>
    /// 语法：**以 <c>#</c> 开头的就是常量**，其余一律按变量名解析。
    /// 判据之所以干净，是因为全局变量名规则（<see cref="GlobalVariableRules.TryValidateName"/>）
    /// 只允许「字母或下划线开头 + 字母数字下划线」，**变量名不可能以 <c>#</c> 开头**，
    /// 两者永不歧义，旧 json 里的纯变量名也完全不受影响。
    /// </para>
    /// <para>
    /// ⚠️ **常量不自带类型前缀**（这是与「方案 A：<c>INT : 123</c>」的关键区别）：
    /// 类型由**写入变量**决定 —— 写入变量本身是全局变量、天然带类型，而既有定案要求
    /// 「来源类型必须与写入变量完全相同」（<see cref="AcquisitionTaskRules.WriteTypesMatch"/>）。
    /// 若常量再声明一遍类型，等于把同一条信息写两次，还会让 <c>#123</c> 写不进 DINT 变量。
    /// </para>
    /// <para>
    /// 支持的写法：
    /// <list type="bullet">
    /// <item><c>#123</c> / <c>#-5</c> —— 十进制整数</item>
    /// <item><c>#0x1F</c> —— 十六进制；<c>#0b1010</c> —— 二进制</item>
    /// <item><c>#1.234</c> —— 小数（写入 REAL 变量）</item>
    /// <item><c>#"ABCD"</c> —— 字符串，**双引号必填**（写入 STRING 变量）</item>
    /// <item><c>#1</c> / <c>#0</c> / <c>#TRUE</c> / <c>#FALSE</c> —— BOOL（两种写法都接受）</item>
    /// </list>
    /// </para>
    /// <para>
    /// ⚠️ **字符串内部不允许出现双引号**（用户定案）：不做转义语法，含引号即明确报错，
    /// 免得「<c>#"A"B"</c>」这种输入被猜成别的意思。
    /// </para>
    /// <para>
    /// 纯逻辑、不引用 WPF：界面拿它标色与标红，执行侧拿它取值，离线断言可直接链接进来。
    /// </para>
    /// </summary>
    public static class AcquisitionConstant
    {
        /// <summary>常量前缀。变量名不可能以它开头，故判据无歧义。</summary>
        public const char Prefix = '#';

        /// <summary>
        /// 该来源文本是不是常量写法（只判前缀，不校验内容）。
        /// <para>空串与纯空白不算常量 —— 那是「没填」，与「填了个坏常量」要分开报。</para>
        /// </summary>
        public static bool IsConstant(string? text) =>
            !string.IsNullOrWhiteSpace(text) && text.TrimStart()[0] == Prefix;

        /// <summary>
        /// 解析常量字面量。
        /// </summary>
        /// <param name="text">来源文本，需以 <c>#</c> 开头</param>
        /// <param name="literal">
        /// 成功时的「规范化文本」—— 与变量取值的形态一致，直接交给
        /// <see cref="ValueCodec.TryEncodeRegisters"/> 即可编码。
        /// </param>
        /// <param name="isQuoted">
        /// 是否是字符串常量（带引号）。调用方据此判断该常量只能写进 STRING 变量。
        /// </param>
        /// <param name="isBoolWord">
        /// 是否用了 <c>TRUE</c> / <c>FALSE</c> 写法。仅供界面提示，编码时已归一成 <c>1</c> / <c>0</c>。
        /// </param>
        /// <param name="error">失败原因（可直接显示给用户）</param>
        public static bool TryParse(
            string? text,
            out string literal,
            out bool isQuoted,
            out bool isBoolWord,
            out string error)
        {
            literal = string.Empty;
            isQuoted = false;
            isBoolWord = false;
            error = string.Empty;

            string raw = (text ?? string.Empty).Trim();
            if (raw.Length == 0 || raw[0] != Prefix)
            {
                error = "常量必须以 # 开头，如 #123、#\"ABCD\"";
                return false;
            }

            string body = raw.Substring(1).Trim();
            if (body.Length == 0)
            {
                error = "常量内容不能为空（# 后面要跟值，如 #123）";
                return false;
            }

            // ---- 字符串常量：#"..." ----
            if (body[0] == '"')
            {
                if (body.Length < 2 || body[^1] != '"')
                {
                    error = "字符串常量要用一对双引号包起来，如 #\"ABCD\"";
                    return false;
                }

                string inner = body.Substring(1, body.Length - 2);
                if (inner.IndexOf('"') >= 0)
                {
                    // 用户定案：不做转义语法，含引号直接报错
                    error = "字符串常量里不能再出现双引号";
                    return false;
                }

                literal = inner;
                isQuoted = true;
                return true;
            }

            // ---- 字符串常量漏了收尾引号（如 #"ABC）----
            if (body.IndexOf('"') >= 0)
            {
                error = "字符串常量要用一对双引号包起来，如 #\"ABCD\"";
                return false;
            }

            // ---- BOOL 的单词写法 ----
            if (body.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
            {
                literal = "1";
                isBoolWord = true;
                return true;
            }

            if (body.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
            {
                literal = "0";
                isBoolWord = true;
                return true;
            }

            // ---- 数值：保留原文本，进制与范围交给 ValueCodec 按写入变量类型判定 ----
            // 不做 pre-parse：`0x1F` / `0b1010` / `-5` / `1.234` 的合法性由编码层统一判定，
            // 这里再解析一遍只会出现两套规则、两种措辞。
            literal = body;
            return true;
        }

        /// <summary>
        /// 常量写进给定写入变量类型时，该用什么显示格式去编码。
        /// <para>
        /// 变量的 <c>Format</c> 是「给人看的显示格式」（可能是 HEX / BIN），
        /// 而常量是**用户直书的字面量**：<c>#0x1F</c> 自带进制、<c>#123</c> 就是十进制。
        /// 若拿变量的 HEX 格式去解析 <c>#123</c>，会被当成十六进制 0x123 = 291 —— 静默写错值。
        /// 所以常量一律按 <see cref="DisplayFormat.Dec"/> 解析，进制靠 <c>0x</c> / <c>0b</c> 前缀自标定
        /// （<see cref="ValueCodec.TryParseIntegerLiteral"/> 的 DEC 分支只认十进制，
        /// 故这里把自标定前缀翻译成对应格式）。
        /// </para>
        /// </summary>
        public static DisplayFormat FormatFor(string literal, DataType targetType)
        {
            if (targetType == DataType.String)
            {
                return DisplayFormat.Ascii;
            }

            string s = (literal ?? string.Empty).Trim();

            // 有符号/无符号整数与 REAL 都用同一个判据：看字面量自带的前缀
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return DisplayFormat.Hex;
            }

            if (s.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            {
                return DisplayFormat.Bin;
            }

            return DisplayFormat.Dec;
        }
    }
}
