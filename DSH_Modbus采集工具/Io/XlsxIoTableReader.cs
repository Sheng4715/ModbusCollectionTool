using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace DSH_Modbus采集工具.Io
{
    /// <summary>
    /// xlsx 点位表解析（零第三方依赖：xlsx = zip + XML，用 System.IO.Compression + XmlReader 手写）。
    /// 规则：读 workbook.xml 指定的首张工作表；逐行扫描，凡单元格值形如点位地址（字母开头+纯数字，
    /// 如 X0/Y37/M100），取其右侧相邻单元格（同行列号+1）作为注释。由此自然支持
    /// 「A=输入地址,B=注释 | D=输出地址,E=注释」双栏模板（IO模板.xlsx），表头/标题/杂项列自动落在规则外。
    /// 不支持合并单元格（合并区只有左上格有值，右侧无值时注释视为空）。
    /// </summary>
    public static class XlsxIoTableReader
    {
        private const string SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string OfficeRelsNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        public static List<(string Address, string Comment)> Read(string path)
        {
            using var zip = ZipFile.OpenRead(path);

            List<string> shared = ReadSharedStrings(zip);
            ZipArchiveEntry sheet = ResolveFirstSheet(zip);

            var results = new List<(string Address, string Comment)>();

            using Stream stream = sheet.Open();
            using var reader = XmlReader.Create(stream);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row")
                {
                    continue;
                }

                // 本行全部单元格：(列号, 值)
                var cells = new List<(int Col, string Value)>();

                using (var rowReader = reader.ReadSubtree())
                {
                    while (rowReader.Read())
                    {
                        if (rowReader.NodeType != XmlNodeType.Element || rowReader.LocalName != "c")
                        {
                            continue;
                        }

                        int col = ColumnIndex(rowReader.GetAttribute("r"));
                        if (col < 0)
                        {
                            continue;
                        }

                        string type = rowReader.GetAttribute("t") ?? string.Empty;
                        string value;
                        using (var cellReader = rowReader.ReadSubtree())
                        {
                            value = ReadCell(cellReader, type, shared);
                        }

                        cells.Add((col, value));
                    }
                }

                // 地址形状的单元格 → 右侧相邻格为注释（无右邻格则注释为空）
                foreach ((int col, string value) in cells)
                {
                    if (!IsAddressShaped(value))
                    {
                        continue;
                    }

                    int right = cells.FindIndex(c => c.Col == col + 1);
                    string comment = right >= 0 ? cells[right].Value : string.Empty;
                    results.Add((value, comment));
                }
            }

            return results;
        }

        /// <summary>点位地址形状：首字符为字母、其余全为数字（X0/Y37/M100；表头「输入」、说明文字均不匹配）。</summary>
        private static bool IsAddressShaped(string value)
        {
            if (value.Length < 2 || !char.IsLetter(value[0]))
            {
                return false;
            }

            for (int i = 1; i < value.Length; i++)
            {
                if (!char.IsDigit(value[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>按 workbook.xml 中第一张 sheet 的 r:id → workbook.xml.rels → Target 定位工作表条目；失败则回退首个 sheet*.xml。</summary>
        private static ZipArchiveEntry ResolveFirstSheet(ZipArchive zip)
        {
            string? rid = null;
            ZipArchiveEntry? workbook = zip.GetEntry("xl/workbook.xml");
            if (workbook != null)
            {
                using Stream s = workbook.Open();
                using var reader = XmlReader.Create(s);
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sheet")
                    {
                        rid = reader.GetAttribute("id", OfficeRelsNs);
                        break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(rid))
            {
                ZipArchiveEntry? rels = zip.GetEntry("xl/_rels/workbook.xml.rels");
                if (rels != null)
                {
                    using Stream s = rels.Open();
                    using var reader = XmlReader.Create(s);
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.Element
                            && reader.LocalName == "Relationship"
                            && string.Equals(reader.GetAttribute("Id"), rid, StringComparison.Ordinal))
                        {
                            string target = reader.GetAttribute("Target") ?? string.Empty;
                            string full = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
                            ZipArchiveEntry? entry = zip.GetEntry(full);
                            if (entry != null)
                            {
                                return entry;
                            }

                            break;
                        }
                    }
                }
            }

            return zip.Entries.FirstOrDefault(e =>
                       e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                       && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidDataException("未找到工作表（xl/worksheets/sheet*.xml），请确认是标准 xlsx 文件");
        }

        /// <summary>xl/sharedStrings.xml → 字符串列表（t="s" 的单元格存的是这里的索引）。</summary>
        private static List<string> ReadSharedStrings(ZipArchive zip)
        {
            var list = new List<string>();
            ZipArchiveEntry? entry = zip.GetEntry("xl/sharedStrings.xml");
            if (entry == null)
            {
                return list;
            }

            using Stream stream = entry.Open();
            using var reader = XmlReader.Create(stream);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "si")
                {
                    continue;
                }

                // 富文本 <r><t>…</t></r> 可能多段，拼接全部 <t>。
                //
                // ⚠️ **必须跳过 <rPh>（注音 / ruby）里的 <t>**：中日韩表里常用注音标注，
                // 形如 <si><t>漢字</t><rPh sb="0" eb="2"><t>かんじ</t></rPh></si>。
                // 不排除的话注音会被拼进注释，变成「漢字かんじ」这种半截日文假名。
                var sb = new StringBuilder();
                int phoneticDepth = 0;   // > 0 = 正处在 <rPh> 子树内
                using (var siReader = reader.ReadSubtree())
                {
                    while (siReader.Read())
                    {
                        if (siReader.NodeType == XmlNodeType.Element)
                        {
                            if (siReader.LocalName == "rPh")
                            {
                                phoneticDepth++;
                                continue;
                            }

                            if (siReader.LocalName == "t" && phoneticDepth == 0)
                            {
                                sb.Append(siReader.ReadElementContentAsString());
                            }

                            continue;
                        }

                        // 注音子树结束：<rPh> 的空元素/自闭合由 EndElement 收尾
                        if (siReader.NodeType == XmlNodeType.EndElement && siReader.LocalName == "rPh"
                            && phoneticDepth > 0)
                        {
                            phoneticDepth--;
                        }
                    }
                }

                list.Add(sb.ToString());
            }

            return list;
        }

        /// <summary>读当前 &lt;c&gt; 元素（reader 已定位其上）的值：共享字符串 / inlineStr / 原始值。</summary>
        private static string ReadCell(XmlReader cell, string type, List<string> shared)
        {
            string raw = string.Empty;
            var inline = new StringBuilder();

            while (cell.Read())
            {
                if (cell.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (cell.LocalName == "v")
                {
                    raw = cell.ReadElementContentAsString();
                }
                else if (cell.LocalName == "is")
                {
                    using var inlineReader = cell.ReadSubtree();
                    while (inlineReader.Read())
                    {
                        if (inlineReader.NodeType == XmlNodeType.Element && inlineReader.LocalName == "t")
                        {
                            inline.Append(inlineReader.ReadElementContentAsString());
                        }
                    }
                }
            }

            if (type == "s" && int.TryParse(raw, out int index) && index >= 0 && index < shared.Count)
            {
                return shared[index];
            }

            return inline.Length > 0 ? inline.ToString() : raw;
        }

        /// <summary>"AB12" → 27（0 基列号；缺 r 属性或无字母返回 -1，该单元格被忽略）。</summary>
        private static int ColumnIndex(string? cellRef)
        {
            if (string.IsNullOrEmpty(cellRef))
            {
                return -1;
            }

            int n = 0;
            int i = 0;
            while (i < cellRef!.Length && char.IsLetter(cellRef[i]))
            {
                n = (n * 26) + (char.ToUpperInvariant(cellRef[i]) - 'A') + 1;
                i++;
            }

            return i == 0 ? -1 : n - 1;
        }
    }
}
