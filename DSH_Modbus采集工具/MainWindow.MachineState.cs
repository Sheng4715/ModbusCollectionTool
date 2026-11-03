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
// PLC 状态机（P1.2）+ 状态机表达式取值适配器 ExpressionVariables + 系统变量监控（P2.3）
// ============================================================

namespace DSH_Modbus采集工具
{
    public partial class MainWindow
    {
        // ---------- PLC 状态机（P1.2） ----------

        /// <summary>表达式里能引用的变量名：系统变量 + 全局变量（两者不会重名）。</summary>
        private bool ExpressionNameExists(string name) =>
            SystemVariable.Find(name) != null || _globalVarSet.FindByName(name) != null;

        /// <summary>
        /// 全局变量模型列表，供补全用（P7.1）。
        /// <para>
        /// 走 <c>Items.Select(Model)</c> 而不是把 <c>GlobalVarItemSet</c> 直接传下去：
        /// <c>Modeling/</c> 不引用界面层，<c>CompletionSource</c> 只认 <see cref="GlobalVariable"/>。
        /// </para>
        /// </summary>
        private IEnumerable<GlobalVariable> GlobalVarModels =>
            _globalVarSet.Items.Select(item => item.Model);

        /// <summary>
        /// 重算「状态机表达式引用了哪些全局变量」（P1.2，2026-10-21）。
        /// <para>
        /// 采集侧按这份清单拼报文，所以清单必须**只含真正被用到的**变量 ——
        /// 全表都采等于把没人看的地址也拖上总线，与 IO 监控「只采可见页」的原则相悖。
        /// </para>
        /// <para>
        /// 调用时机只有三类：表达式被改、变量定义被改（改名 / 改类型 / 改地址 / 删除）、启动载入。
        /// **不放进每拍**：<see cref="ICollectorHost.CaptureSnapshot"/> 必须便宜，
        /// 而这里要遍历 9 条表达式的变量引用。
        /// </para>
        /// </summary>
        private void RefreshMachineGlobals()
        {
            var needed = new List<GlobalVariable>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Collect(CompiledExpression? compiled)
            {
                if (compiled == null)
                {
                    return;
                }

                foreach (var (name, _) in compiled.UsedNames)
                {
                    if (!seen.Add(name))
                    {
                        continue;
                    }

                    // 系统变量不是全局变量：它的值来自内存快照，不产生任何报文
                    if (SystemVariable.Find(name) != null)
                    {
                        continue;
                    }

                    if (_globalVarSet.FindByName(name) is { } variable)
                    {
                        needed.Add(variable);
                    }
                }
            }

            foreach (MachineStateItem item in _machineSet.Items)
            {
                Collect(item.Compiled);
            }

            // 指示灯（Error / Fault）也由表达式驱动，引用的全局变量同样要上总线
            foreach (MachineStateLamp lamp in _machineSet.Lamps)
            {
                Collect(lamp.Compiled);
            }

            _machineGlobals = needed;
        }

        /// <summary>
        /// 重算「采集任务组引用了哪些全局变量」（P3.1）。
        /// <para>
        /// 只算**启用**的组：停用的组不采，把它的变量也拖上总线等于白读。
        /// </para>
        /// <para>
        /// 写入类型的「来源变量」也要收进来 —— 写的时候要从它取值；
        /// 「写入变量」同样收，因为记录表格里要写它的值。
        /// </para>
        /// <para>
        /// 调用时机与 <see cref="RefreshMachineGlobals"/> 一致（表达式 / 变量 / 任务改动时），
        /// 同样**不放进每拍** —— <see cref="ICollectorHost.CaptureSnapshot"/> 必须便宜。
        /// </para>
        /// </summary>
        private void RefreshAcquisitionGlobals()
        {
            var needed = new List<GlobalVariable>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            // 只收**全局变量**：系统变量是程序算出来的，不上总线、不需要采集。
            // 来源变量允许填系统变量（用户定案 2026-10-26），这里查不到就自然跳过 ——
            // 不是漏写，别在这里给它补一条"查系统变量"的分支
            void Take(string? name)
            {
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                {
                    return;
                }

                if (_globalVarSet.FindByName(name) is { } variable)
                {
                    needed.Add(variable);
                }
            }

            foreach (AcquisitionTaskItem task in _acqTaskSet.Items)
            {
                // 停用的组不采。配置错误的组走同一条路径：它的「有效启用」就是 false
                // （用户定案 2026-10-27），所以这里不必再单独判配置
                if (!task.EffectiveEnabled)
                {
                    continue;
                }

                foreach (AcquisitionItemRow row in task.Items)
                {
                    Take(row.VariableName);

                    if (task.IsWrite)
                    {
                        Take(row.SourceVariableName);
                    }
                }

                // 触发式的**表达式引用**也要收（2026-10-31 修）。
                // 漏了它就会出现：表达式写着 sys_X > 0 → 等变量变成 1 才触发，
                // 但那个变量根本没被采，永远停在初始值 0，触发沿永远等不到。
                // 编译是惰性的（正常拍里由 EvaluateTrigger 触发），故这里按源文本现编一次，
                // 编不出来就跳过 —— 表达式非法时不该把整组变量拖上总线
                if (task.Model.Trigger == AcquisitionTriggerMode.Edge
                    && !string.IsNullOrWhiteSpace(task.Model.Expression)
                    && CompiledExpression.TryParse(task.Model.Expression, out CompiledExpression? compiled, out _)
                    && compiled != null)
                {
                    foreach (var (name, _) in compiled.UsedNames)
                    {
                        Take(name);
                    }
                }
            }

            _acquisitionGlobals = needed;
        }

        /// <summary>当前是否停在「PLC状态机」视图（页头下拉框第 0 项）。</summary>
        private bool IsMachineViewSelected => OverviewViewCombo?.SelectedIndex == 0;

        /// <summary>当前是否停在「数据采集」视图（页头下拉框第 2 项）。</summary>
        private bool IsAcquisitionViewSelected => OverviewViewCombo?.SelectedIndex == 2;

        /// <summary>
        /// 状态机定时器启停：**总览页可见 且 停在「PLC状态机」视图** 才跑。
        /// </summary>
        private void SyncMachineTimer()
        {
            bool shouldRun = _overviewPageActive
                && IsMachineViewSelected
                && !_windowClosed;

            // 采集侧看这个标志：状态机可见就要采它引用的全局变量（与 IO 监控同一逻辑）。
            // 放在下面的早退**之前** —— 否则「状态没变」时标志就更新不到了。
            _machineViewActive = shouldRun;

            if (shouldRun == _machineTimer.IsEnabled)
            {
                return;
            }

            if (shouldRun)
            {
                RefreshMachineStates();
                _machineTimer.Start();
            }
            else
            {
                _machineTimer.Stop();
            }
        }

        /// <summary>
        /// 一拍：刷新系统变量快照 → 求值 9 条表达式 → 把变了的框重画。
        /// </summary>
        private void RefreshMachineStates()
        {
            _machineVariables.UpdateSystemValues(
                SystemVariableValues.Capture(DateTime.Now, _runClock.Elapsed));

            _machineSet.EvaluateAll(_machineVariables);

            foreach (MachineStateItem item in _machineSet.Items)
            {
                MachineStateViewControl.ApplyVisualState(item);
            }

            foreach (MachineStateLamp lamp in _machineSet.Lamps)
            {
                MachineStateViewControl.ApplyLampState(lamp);
            }
        }

        /// <summary>
        /// 双击状态框：弹编辑窗。确定后写回模型并落盘。
        /// <para>表达式在弹窗里已经校验并回退过了，这里拿到的都是合法值。</para>
        /// </summary>
        private void MachineStateView_StateDoubleClicked(object? sender, MachineStateItem item)
        {
            Ui.MachineStateEditDialog? dialog =
                Ui.MachineStateEditDialog.Ask(
                this, item, ExpressionNameExists, () => CompletionSource.ForExpression(GlobalVarModels));

            if (dialog != null)
            {
                item.Text = dialog.StateText;
                item.FontStyle = dialog.FontStyleValue;
                item.FontSize = dialog.FontSizeValue;
                item.Color = dialog.ColorHex;

                // 走集合的唯一入口：编译 + 变量存在性校验 + 清掉旧的错误态
                _machineSet.TrySetExpression(item, dialog.ExpressionText, ExpressionNameExists, out _);
                _machineSet.Save();

                // 表达式换了 = 引用的变量可能换了，采集清单要跟着重算
                RefreshMachineGlobals();

                MachineStateViewControl.ApplyVisualState(item);
                RefreshMachineStates();
            }

            // 编辑期间定时器可能停过（弹窗是模态的，但页面仍可见），这里顺手校一次
            SyncMachineTimer();
        }

        /// <summary>
        /// 双击指示灯：弹编辑窗（文字 + 文字大小 + 灯大小 + 灯颜色 + 表达式五项）。
        /// <para>确定后写回模型并落盘，规则与状态框完全一致。</para>
        /// </summary>
        private void MachineStateView_LampDoubleClicked(object? sender, MachineStateLamp lamp)
        {
            Ui.MachineLampEditDialog? dialog =
                Ui.MachineLampEditDialog.Ask(
                this, lamp, ExpressionNameExists, () => CompletionSource.ForExpression(GlobalVarModels));

            if (dialog != null)
            {
                lamp.Text = dialog.LampText;
                lamp.FontSize = dialog.FontSizeValue;
                lamp.DotSize = dialog.DotSizeValue;
                lamp.Color = dialog.LampColor;

                // 走集合的唯一入口：编译 + 变量存在性校验 + 清掉旧的错误态
                _machineSet.TrySetExpression(lamp, dialog.ExpressionText, ExpressionNameExists, out _);
                _machineSet.Save();

                // 表达式换了 = 引用的变量可能换了，采集清单要跟着重算
                RefreshMachineGlobals();

                MachineStateViewControl.ApplyLampState(lamp);
                RefreshMachineStates();
            }

            // 编辑期间定时器可能停过（弹窗是模态的，但页面仍可见），这里顺手校一次
            SyncMachineTimer();
        }

        /// <summary>
        /// 总览页视图切换（PLC状态机 / 生产数据 / 数据采集）。
        /// <para>
        /// ⚠️ 初始化期间也会触发一次（XAML 里的 <c>SelectedIndex="0"</c>）——
        /// 那时 <c>MachineStateViewControl</c> 还没造出来，所以先判空直接返回；
        /// 初始可见性由 XAML 默认值保证（画布可见、另两个视图折叠），与第 0 项一致。
        /// </para>
        /// <para>
        /// ⚠️ **三态**：早先只有两项，判据是「是不是状态机」（<c>!machineView</c> 就当生产数据）。
        /// 加了第三项之后那种写法会把「数据采集」也当成生产数据、显示成「规划中」，
        /// 所以这里必须按 <c>SelectedIndex</c> 逐项判，不能再取反。
        /// </para>
        /// </summary>
        private void OverviewView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MachineStateViewControl == null)
            {
                return;
            }

            int index = OverviewViewCombo?.SelectedIndex ?? 0;

            bool machineView = index == 0;
            bool productionView = index == 1;
            bool acqView = index == 2;

            MachineStateViewControl.Visibility = machineView
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (ProductionDataHint != null)
            {
                ProductionDataHint.Visibility = productionView
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (AcquisitionDataView != null)
            {
                AcquisitionDataView.Visibility = acqView
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            // 两个定时器的起停都收在各自的 Sync 里，切换视图只负责把它们叫醒。
            // 顺序：先把状态机求值/数据采集视图的内容准备好，再同步定时器 ——
            // 否则定时器先跑，会对着还没填好的任务组下拉框空转一拍。
            SyncMachineTimer();

            if (machineView)
            {
                RefreshMachineStates();
            }
            else if (acqView)
            {
                RefreshAcqOverviewView();
            }

            SyncAcquisitionViewTimer();
        }

        /// <summary>
        /// 表达式取值适配器：把「系统变量快照 + 全局变量当前值」按 BOOL / 数字 交给表达式引擎。
        /// <para>
        /// ⚠️ 系统变量优先：虽然现在禁止重名（<c>GlobalVariableRules.TryValidateName</c> 会拦），
        /// 但手工改过的 json 仍可能撞上，顺序定死才不会出现「同一份配置两台机器算出不同结果」。
        /// </para>
        /// </summary>
        private sealed class ExpressionVariables : IExpressionVariables
        {
            private IReadOnlyDictionary<string, string> _system =
                new Dictionary<string, string>(StringComparer.Ordinal);

            private GlobalVarItemSet? _globals;

            public void UpdateSystemValues(IReadOnlyDictionary<string, string> values) =>
                _system = values;

            public void AttachGlobals(GlobalVarItemSet globals) => _globals = globals;

            public bool TryGetValue(string name, out ExpressionValue value, out string error)
            {
                if (SystemVariable.Find(name) is { } system)
                {
                    if (_system.TryGetValue(name, out string? text))
                    {
                        return Convert(system.Type, text, out value, out error);
                    }

                    error = $"系统变量“{name}”当前取不到值";
                    value = default;
                    return false;
                }

                GlobalVariable? global = _globals?.FindByName(name);
                if (global == null)
                {
                    error = $"变量“{name}”不存在";
                    value = default;
                    return false;
                }

                return Convert(global.Type, global.Value, out value, out error);
            }

            private static bool Convert(
                DataType type, string? text, out ExpressionValue value, out string error)
            {
                string raw = (text ?? string.Empty).Trim();

                if (type == DataType.Bool)
                {
                    // 与界面里 BOOL 的 "1"/"0" 显示一致；顺手容忍 TRUE/FALSE
                    bool b = raw is "1"
                        || raw.Equals("TRUE", StringComparison.OrdinalIgnoreCase);

                    value = ExpressionValue.FromBool(b);
                    error = string.Empty;
                    return true;
                }

                if (double.TryParse(
                        raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                {
                    value = ExpressionValue.FromNumber(number);
                    error = string.Empty;
                    return true;
                }

                value = ExpressionValue.FromNumber(0);
                error = $"变量值“{raw}”不是数字";
                return false;
            }
        }

        // ---------- 系统变量监控（P2.3） ----------

        /// <summary>
        /// 把当前时刻的系统变量值推给界面。
        /// <para>
        /// 只写「变了的值」—— <see cref="SystemVarRow.Value"/> 的 setter 会挡掉同值赋值，
        /// 故 100ms 一拍、16 行也不会产生无谓的绑定通知。
        /// </para>
        /// </summary>
        private void RefreshSystemVarValues()
        {
            IReadOnlyDictionary<string, string> values =
                SystemVariableValues.Capture(DateTime.Now, _runClock.Elapsed);

            foreach (SystemVarRow row in _sysVarRows)
            {
                if (values.TryGetValue(row.Name, out string? value) && value != null)
                {
                    row.Value = value;
                }
            }
        }

        /// <summary>
        /// 系统变量定时器启停：**本分段可见 且 设备监控页可见** 才跑。
        /// <para>
        /// 与 X/Y、自由监控「采集跟随可见分段」同一套规矩 ——
        /// 用户在看别的页面时，没有理由每 100ms 算一遍 16 个变量。
        /// </para>
        /// </summary>
        private void SyncSystemVarTimer()
        {
            bool shouldRun = _sysViewActive && _ioPageActive && !_windowClosed;

            if (shouldRun == _sysVarTimer.IsEnabled)
            {
                return;
            }

            if (shouldRun)
            {
                RefreshSystemVarValues();
                _sysVarTimer.Start();
            }
            else
            {
                _sysVarTimer.Stop();
            }
        }

    }
}
