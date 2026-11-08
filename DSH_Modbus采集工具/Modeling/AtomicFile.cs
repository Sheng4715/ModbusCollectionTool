using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 配置文件的原子落盘与容错读取。
    /// <para>
    /// 设计意图（2026-11-08）：消除「读失败静默回空 → 下次写无条件覆盖 → 原数据丢失」这条链路。
    /// <list type="number">
    /// <item><see cref="WriteAllText"/> 写临时文件 → 落盘 → 原子替换，并保留上一版为 <c>.bak</c>；</item>
    /// <item><see cref="ReadList{T}"/> 整体反序列化失败时逐个元素抢救，坏元素跳过、好元素照收；</item>
    /// <item>彻底读不回时把原文件改名备份为 <c>.bad-时间戳</c>，而不是放任后续 Save 覆盖掉原始数据。</item>
    /// </list>
    /// 所有方法都不抛异常（返回 <c>bool</c> / 空集合），由调用方决定是否提示。
    /// </para>
    /// </summary>
    public static class AtomicFile
    {
        /// <summary>
        /// 原子写文本（UTF-8 无 BOM）：写同目录临时文件、落盘刷新，再原子替换目标；
        /// 目标已存在时上一版保留为 <c>path + ".bak"</c>。失败返回 <c>false</c>，不抛。
        /// </summary>
        public static bool WriteAllText(string path, string content)
        {
            try
            {
                string dir = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
                Directory.CreateDirectory(dir);

                string tmp = path + ".tmp";
                string bak = path + ".bak";

                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    sw.Write(content);
                    sw.Flush();
                    fs.Flush(flushToDisk: true);
                }

                if (File.Exists(path))
                {
                    try
                    {
                        // 原子替换：旧文件自动移到 bak，且尽量保留元数据
                        File.Replace(tmp, path, bak, ignoreMetadataErrors: true);
                    }
                    catch
                    {
                        // File.Replace 在跨卷 / 权限受限时失败 → 退化为覆盖式 Move
                        File.Move(tmp, path, overwrite: true);
                    }
                }
                else
                {
                    File.Move(tmp, path);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 容错读取一个 JSON 数组：
        /// <list type="bullet">
        /// <item>文件不存在 / 空白 / 内容为 <c>null</c> → 空集合；</item>
        /// <item>整体反序列化成功 → 原样返回；</item>
        /// <item>整体失败 → 逐个元素抢救（坏元素跳过、好元素照收），并把原文件备份为 <c>.bad-时间戳</c>。</item>
        /// </list>
        /// </summary>
        public static List<T> ReadList<T>(string path, JsonSerializerOptions options)
        {
            string json;
            try
            {
                if (!File.Exists(path))
                {
                    return new List<T>();
                }

                json = File.ReadAllText(path);
            }
            catch
            {
                // 读不了（锁定 / 权限）→ 空；备份多半也做不成，尽力即可
                return EmptyAfterBackup<T>(path);
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<T>();
            }

            bool threwFull = false;
            List<T>? list;
            try
            {
                list = JsonSerializer.Deserialize<List<T>>(json, options);
            }
            catch
            {
                threwFull = true;
                list = null;
            }

            if (list != null)
            {
                return list;
            }

            if (!threwFull)
            {
                // 内容合法但为 "null"（或反序列化返回 null）→ 视为空
                return new List<T>();
            }

            // 整体失败 → 逐元素抢救 + 原文件备份（防后续 Save 覆盖）
            var recovered = TryReadElementByElement<T>(json, options, out bool isArray);
            BackupBad(path);
            return isArray ? recovered : new List<T>();
        }

        /// <summary>
        /// 把 <paramref name="path"/> 改名为 <c>.bad-时间戳</c>。失败静默（尽力而为）。
        /// 给「读文件彻底失败但用户可能想手工抢救」的场景留一份原始副本。
        /// </summary>
        public static void BackupBad(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                string dest = path + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                // 同名冲突时加序号，避免覆盖前一次备份
                int seed = 2;
                while (File.Exists(dest))
                {
                    dest = path + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + seed++;
                }

                File.Move(path, dest);
            }
            catch
            {
                // 备份失败不阻断
            }
        }

        private static List<T> EmptyAfterBackup<T>(string path)
        {
            BackupBad(path);
            return new List<T>();
        }

        /// <summary>
        /// 用 <see cref="JsonDocument"/> 逐个数组元素反序列化，坏元素跳过、好元素照收。
        /// <paramref name="isArray"/> 指示文件是否真解析成了一个 JSON 数组（根不是数组则 <c>false</c>）。
        /// </summary>
        private static List<T> TryReadElementByElement<T>(
            string json, JsonSerializerOptions options, out bool isArray)
        {
            isArray = false;
            var result = new List<T>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return result;
                }

                isArray = true;
                foreach (JsonElement el in doc.RootElement.EnumerateArray())
                {
                    T? item;
                    try
                    {
                        item = el.Deserialize<T>(options);
                    }
                    catch
                    {
                        continue; // 单个元素写坏 → 跳过这一条
                    }

                    if (item != null)
                    {
                        result.Add(item);
                    }
                }
            }
            catch
            {
                // 根不是合法 JSON → 抢救失败，返回空
            }

            return result;
        }
    }
}