using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DSH_Modbus采集工具.Drivers;
using DSH_Modbus采集工具.Io;
using DSH_Modbus采集工具.Logging;
using DSH_Modbus采集工具.Expressions;
using DSH_Modbus采集工具.Modeling;
using DSH_Modbus采集工具.Net;
using DSH_Modbus采集工具.Polling;
using DSH_Modbus采集工具.Themes;
using DSH_Modbus采集工具.Ui;

// ============================================================
// IO 表导入（P2）
// ============================================================

namespace DSH_Modbus采集工具
{
    public partial class MainWindow
    {
        // ---------- IO 表导入（P2） ----------

        private void ImportIoTableButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "导入 IO 点位表",
                Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            IoImportResult result;
            try
            {
                result = IoTableImporter.Read(_currentDriver, dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"导入失败：{ex.Message}", "导入 IO 表",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (result.SourceRowCount == 0)
            {
                MessageBox.Show(this,
                    "未在表中识别到点位地址（应形如 X0 / Y37），当前注释未变更。",
                    "导入 IO 表", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int ok = result.OkCount;

            if (ok == 0)
            {
                MessageBox.Show(this,
                    $"成功导入 0 项，失败 {result.FailureCount} 项。\n\n" +
                    $"{IoTableImporter.FormatFailures(result.Failures)}\n\n当前注释未变更。",
                    "导入 IO 表", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _ioSet.Replace(result.Comments);
            SaveComments();
            RebuildIoPoints(); // 点位集随之变化：重建监控列表并复位到第 1 页

            string message = $"成功导入 {ok} 项，失败 {result.FailureCount} 项。";
            if (result.FailureCount > 0)
            {
                message += $"\n\n失败明细：\n{IoTableImporter.FormatFailures(result.Failures)}";
            }

            MessageBox.Show(this, message, "导入 IO 表", MessageBoxButton.OK,
                result.FailureCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

    }
}
