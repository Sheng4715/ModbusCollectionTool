using System;
using System.IO;
using System.Windows;
using DSH_Modbus采集工具.Modeling;

namespace DSH_Modbus采集工具.Ui
{
    /// <summary>
    /// 实时数据背景图的选择入口（P1.4）。
    /// <para>
    /// 两处共用：任务组选项对话框里的「选择…」，以及总览页实时数据页签右侧的
    /// 「导入图片」按钮。**这里只管弹框**，拷贝落在哪、怎么改名由
    /// <see cref="LiveImageStore"/> 决定（那部分能离线断言，这部分不能）。
    /// </para>
    /// </summary>
    public static class LiveImagePicker
    {
        /// <summary>
        /// 弹文件选择框，选中后拷贝进 <c>Images\</c>，返回相对路径。
        /// </summary>
        /// <param name="owner">对话框归属窗口。</param>
        /// <param name="current">当前已配的相对路径（可为空）：用它把文件框定位到 Images 目录。</param>
        /// <returns>相对路径；用户取消返回 <c>null</c>。</returns>
        /// <exception cref="IOException">拷贝失败。</exception>
        public static string? Pick(Window? owner, string? current)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择实时数据背景图",
                Multiselect = false,
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*",
            };

            // 已经配过图的话，从 Images 目录开始找 —— 否则每次都要从头翻
            string imagesDir = LiveImageStore.Resolve(
                AppContext.BaseDirectory, LiveImageStore.FolderName);

            if (!string.IsNullOrWhiteSpace(current))
            {
                dialog.InitialDirectory = imagesDir;
            }

            bool? ok = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
            if (ok != true)
            {
                return null;
            }

            return LiveImageStore.CopyIntoFolder(AppContext.BaseDirectory, dialog.FileName);
        }
    }
}
