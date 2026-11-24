using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// HTTP 数据上报的报文构造（P3.1b，2026-11-24 定案）。
    /// 纯逻辑、不引 WPF、不碰网络 —— 本类只把「字段名称 + 来源变量值」拼成
    /// **纯平铺 JSON**（无时间戳、无任务组名、无外层封装），发报由执行侧负责。
    /// </summary>
    public static class MesReport
    {
        /// <summary>默认请求方法。</summary>
        public const string DefaultMethod = "POST";

        /// <summary>允许的请求方法（去 GET：GET 不带 body，与平铺 JSON 语义冲突）。</summary>
        public static readonly string[] Methods = { "POST", "PUT" };

        /// <summary>校验请求方法是否合法。</summary>
        public static bool IsValidMethod(string? method) =>
            !string.IsNullOrWhiteSpace(method)
            && Array.IndexOf(Methods, method!.Trim().ToUpperInvariant()) >= 0;

        /// <summary>
        /// 把上报任务的数据项拼成平铺 JSON 文本。
        /// <para>
        /// 每个数据项 = 一个 JSON 键值对：键 = 项名称（字段名称），值 = 来源变量的当前值
        /// 按类型序列化。来源变量口径与写入任务一致：系统变量 / 全局变量 / 常量 / 数组元素。
        /// </para>
        /// </summary>
        /// <param name="task">上报任务组</param>
        /// <param name="findGlobal">全局变量查表；null 时全局变量都查不到</param>
        /// <param name="systemValues">系统变量快照；null 时系统变量取不到（报错）</param>
        /// <param name="json">成功时输出的 JSON 文本（纯平铺，UTF-8 可安全编码）</param>
        /// <param name="error">失败原因；成功时空串</param>
        public static bool TryBuildBody(
            AcquisitionTask? task,
            Func<string, GlobalVariable?>? findGlobal,
            IReadOnlyDictionary<string, string>? systemValues,
            out string json,
            out string error)
        {
            json = string.Empty;
            error = string.Empty;

            if (task == null)
            {
                error = "任务组不存在";
                return false;
            }

            if (task.Items == null || task.Items.Count == 0)
            {
                error = "没有数据项";
                return false;
            }

            var sb = new StringBuilder();
            sb.Append('{');

            bool first = true;
            for (int i = 0; i < task.Items.Count; i++)
            {
                AcquisitionItem item = task.Items[i];
                if (item == null)
                {
                    continue;
                }

                string field = (item.Name ?? string.Empty).Trim();
                AcquisitionSource? source = AcquisitionSource.Resolve(item.SourceVariableName, findGlobal);
                if (source == null)
                {
                    error = AcquisitionTaskCheck.DescribeItemPrefix(i + 1, item.Name)
                        + AcquisitionSource.DescribeResolveError(item.SourceVariableName, findGlobal);
                    return false;
                }

                string? text = AcquisitionSource.ValueOf(item.SourceVariableName, findGlobal, systemValues);
                if (text == null)
                {
                    error = AcquisitionTaskCheck.DescribeItemPrefix(i + 1, item.Name) + "取不到来源变量的值";
                    return false;
                }

                // ⚠️ 逗号必须在**写这一对之前**补：原来的写法是「写完再补逗号」，
                // 于是 2 个项会拼成 {"a":1,"b":2,} —— 末尾多一个逗号是非法 JSON，
                // MES 侧直接 400。首项不补，后续每项前补一个。
                if (!first)
                {
                    sb.Append(',');
                }
                first = false;

                if (!TryWriteJsonValue(sb, field, text, source, out string valueError))
                {
                    error = AcquisitionTaskCheck.DescribeItemPrefix(i + 1, item.Name) + valueError;
                    return false;
                }
            }

            if (first)
            {
                error = "没有数据项";
                return false;
            }

            sb.Append('}');
            json = sb.ToString();
            return true;
        }

        // 内部：把「字段名 + 值文本 + 来源类型」写成一个 JSON 键值对片段（不含逗号，逗号由调用方拼）。
        private static bool TryWriteJsonValue(
            StringBuilder sb, string field, string text, AcquisitionSource source, out string error)
        {
            error = string.Empty;

            sb.Append('"');
            sb.Append(Escape(field));
            sb.Append("\":");

            // 常量：按字面量形式推断类型（上报没有「写入变量」可反推类型）。
            //
            // ⚠️ 必须解析 `source.Name`（原始的「#12」/「#"AB"」），**不能**解析 `text`：
            // AcquisitionSource.ValueOf 对常量返回的是去掉 `#` 的**字面量**（"12"），
            // 拿它去过 AcquisitionConstant.TryParse 会一律报「常量必须以 # 开头」，
            // 于是每个常量来源都发不出去。
            if (source.IsConstant)
            {
                if (!AcquisitionConstant.TryParse(
                        source.Name, out string literal, out bool isQuoted, out bool isBoolWord, out string constError))
                {
                    error = "来源常量无效：" + constError;
                    return false;
                }

                if (isQuoted)
                {
                    sb.Append('"').Append(Escape(literal)).Append('"');
                }
                else if (isBoolWord)
                {
                    sb.Append(literal == "1" ? "true" : "false");
                }
                else if (double.TryParse(
                    literal, NumberStyles.Float, CultureInfo.InvariantCulture, out double _))
                {
                    // 数字（整数或小数都发 JSON number）
                    sb.Append(literal);
                }
                else
                {
                    // 兜底：非数字又非引号/布尔，当字符串发（理论上走不到，AcquisitionConstant 已归一）
                    sb.Append('"').Append(Escape(literal)).Append('"');
                }
                return true;
            }

            // 变量（系统变量 / 全局变量 / 数组元素）：按类型序列化。
            DataType type = source.IsArrayElement
                ? (source.BaseVariable?.ElementType ?? DataType.Int)
                : source.Type;

            switch (type)
            {
                case DataType.Bool:
                    // Value 文本是 "1"/"0"
                    sb.Append(text == "1" ? "true" : "false");
                    return true;

                case DataType.String:
                    sb.Append('"').Append(Escape(text)).Append('"');
                    return true;

                case DataType.Real:
                    // 浮点：发数字。文本可能是 "1.5" 或 "1,5"（取决于回填格式）——
                    // 统一转成不变量文化的小数。
                    if (double.TryParse(
                            text, NumberStyles.Float, CultureInfo.InvariantCulture, out double realValue))
                    {
                        sb.Append(realValue.ToString("R", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append('0');
                    }
                    return true;

                case DataType.Int:
                case DataType.UInt:
                case DataType.DInt:
                case DataType.UDInt:
                    // 整数：发数字。文本可能带 HEX/BIN 显示格式，但这里取的是十进制显示值
                    // （Value 是显示格式文本；为避免歧义，只发能按十进制解析的值，否则 0）。
                    if (long.TryParse(
                            text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long intValue))
                    {
                        sb.Append(intValue.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append('0');
                    }
                    return true;

                default:
                    // Array 整条不会被引用（只能引用元素），理论走不到；兜底当字符串。
                    sb.Append('"').Append(Escape(text)).Append('"');
                    return true;
            }
        }

        // JSON 字符串转义（双引号、反斜杠、控制字符）。中文原样保留。
        private static string Escape(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
