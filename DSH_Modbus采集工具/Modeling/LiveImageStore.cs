using System;
using System.IO;

namespace DSH_Modbus采集工具.Modeling
{
    /// <summary>
    /// 实时数据背景图的落盘规则（P1.4，2026-11-02 从 UI 里抽出来）。
    /// <para>
    /// **纯文件操作、不含任何 WPF 类型** —— 因此能被独立工程链接源码做断言。
    /// 抽出来的原因：这里有三条容易写错的规则（同名不覆盖、重复选同文件复用、
    /// 相对路径落盘），只有能离线跑断言才验证得了。
    /// </para>
    /// </summary>
    public static class LiveImageStore
    {
        /// <summary>图片落在 exe 旁的哪个目录（相对路径）。</summary>
        public const string FolderName = "Images";

        /// <summary>
        /// 把 <paramref name="sourcePath"/> 拷进 <paramref name="baseDirectory"/>/Images，
        /// 返回**相对路径**（如 <c>Images\cy50571.png</c>）。
        /// </summary>
        /// <param name="baseDirectory">程序目录（生产传 <c>AppContext.BaseDirectory</c>）。</param>
        /// <param name="sourcePath">用户选中的文件绝对路径。</param>
        /// <exception cref="IOException">拷贝失败（磁盘满 / 权限 / 文件被占用）。</exception>
        /// <exception cref="FileNotFoundException">源文件不存在。</exception>
        public static string CopyIntoFolder(string baseDirectory, string sourcePath)
        {
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("图片文件不存在：" + sourcePath, sourcePath);
            }

            string imagesDir = Path.Combine(baseDirectory, FolderName);
            Directory.CreateDirectory(imagesDir);

            string fileName = Path.GetFileName(sourcePath);
            string target = Path.Combine(imagesDir, fileName);

            string stem = Path.GetFileNameWithoutExtension(sourcePath);
            string ext = Path.GetExtension(sourcePath);

            int suffix = 1;
            while (File.Exists(target))
            {
                // 已经是同一份内容：直接复用，不必再拷一份。
                //
                // ⚠️ 必须比**内容**，不能只比路径：用户第一次选 D:\图\panel.png 拷进
                // Images\panel.png，第二次又选**原来那个** D:\图\panel.png —— 此时
                // target 与 source 路径完全不同，只比路径就会判成「新文件」而存成
                // panel_1.png、panel_2.png…（实测踩到，断言 ⑪④ 抓住）。
                // 反过来也不能只比文件名：两个不同目录下的同名文件是**不同的图**，
                // 复用会让用户换了图却看不出变化。
                if (SameContent(target, sourcePath))
                {
                    return Path.Combine(FolderName, Path.GetFileName(target));
                }

                target = Path.Combine(imagesDir, stem + "_" + suffix + ext);
                suffix++;
            }

            File.Copy(sourcePath, target);

            return Path.Combine(FolderName, Path.GetFileName(target));
        }

        /// <summary>相对路径 → 绝对路径；空串返回空串（调用方据此跳过）。</summary>
        public static string Resolve(string baseDirectory, string relative) =>
            string.IsNullOrWhiteSpace(relative)
                ? string.Empty
                : Path.Combine(baseDirectory, relative);

        /// <summary>
        /// 两个文件内容是否相同。先比长度（便宜且能排除绝大多数），再逐字节比。
        /// <para>
        /// 不用哈希：图片通常几百 KB，逐字节读一次就够了，引入哈希只是多一层开销；
        /// 也不用 <c>FileInfo.LastWriteTime</c>：拷贝会改时间戳，不可靠。
        /// </para>
        /// </summary>
        private static bool SameContent(string left, string right)
        {
            try
            {
                if (string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var a = new FileInfo(left);
                var b = new FileInfo(right);

                if (!a.Exists || !b.Exists || a.Length != b.Length)
                {
                    return false;
                }

                using FileStream fsA = a.OpenRead();
                using FileStream fsB = b.OpenRead();

                const int BufferSize = 8192;
                byte[] bufA = new byte[BufferSize];
                byte[] bufB = new byte[BufferSize];

                while (true)
                {
                    int readA = fsA.Read(bufA, 0, BufferSize);
                    int readB = fsB.Read(bufB, 0, BufferSize);

                    if (readA != readB)
                    {
                        return false;
                    }

                    if (readA == 0)
                    {
                        return true;   // 同时读到尾
                    }

                    if (!bufA.AsSpan(0, readA).SequenceEqual(bufB.AsSpan(0, readB)))
                    {
                        return false;
                    }
                }
            }
            catch (IOException)
            {
                // 读不了就当「不同」：宁可多存一份，也不要误判成同一张图
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>去掉尾部分隔符并转成绝对路径，用于可靠比较。</summary>
        private static string Normalize(string path) =>
            Path.GetFullPath(path).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
