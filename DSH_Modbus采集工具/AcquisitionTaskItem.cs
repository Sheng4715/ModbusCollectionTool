using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DSH_Modbus采集工具.Modeling;

namespace DSH_Modbus采集工具
{
    /// <summary>
    /// 采集任务组列表里的**一个数据项行**（P3.1）。
    /// <para>
    /// 与 <c>GlobalVarItem</c> 同一思路：模型（<see cref="AcquisitionItem"/>）只存
    /// 「项名称 + 变量名称」，而「数据类型」这一列是**只读反推**出来的，
    /// 属于界面态，故放在包装里。
    /// </para>
    /// </summary>
    public sealed class AcquisitionItemRow : INotifyPropertyChanged
    {
        private readonly Func<string, GlobalVariable?> _findVariable;
        private int _index;

        public AcquisitionItemRow(AcquisitionItem model, Func<string, GlobalVariable?> findVariable)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));
            _findVariable = findVariable ?? throw new ArgumentNullException(nameof(findVariable));
        }

        public AcquisitionItem Model { get; }

        /// <summary>序号（1 起，由所属任务组统一重排）</summary>
        public int Index
        {
            get => _index;
            set
            {
                if (_index == value)
                {
                    return;
                }

                _index = value;
                Raise();
            }
        }

        /// <summary>项名称（如「压力」）</summary>
        public string Name
        {
            get => Model.Name;
            set
            {
                if (Model.Name == value)
                {
                    return;
                }

                Model.Name = value ?? string.Empty;
                Raise();
            }
        }

        /// <summary>变量名称（引用一条全局变量）</summary>
        public string VariableName
        {
            get => Model.VariableName;
            set
            {
                if (Model.VariableName == value)
                {
                    return;
                }

                Model.VariableName = value ?? string.Empty;
                Raise();
                Raise(nameof(TypeDisplayName));
                Raise(nameof(HasVariableError));
                Raise(nameof(VariableHint));
            }
        }

        /// <summary>被引用的全局变量定义；未填或找不到时为 <c>null</c>。</summary>
        public GlobalVariable? Variable =>
            Model.VariableName.Length == 0 ? null : _findVariable(Model.VariableName);

        /// <summary>
        /// 数据类型列（**只读**，由变量反推）。
        /// <para>没填变量、或变量名不存在时给一个短横占位 —— 留空白会让人以为这列坏了。</para>
        /// </summary>
        public string TypeDisplayName =>
            Variable?.TypeDisplayName ?? "—";

        /// <summary>变量名填了、但在全局变量表里找不到 → 标红提示。</summary>
        public bool HasVariableError =>
            Model.VariableName.Length > 0 && Variable == null;

        /// <summary>红字提示文案（悬停可见）</summary>
        public string VariableHint =>
            HasVariableError
                ? "全局变量表里没有“" + Model.VariableName + "”"
                : (Variable == null ? "尚未选择变量" : Variable.Comment);

        /// <summary>变量定义变化（改名 / 改类型）后刷新只读列。</summary>
        public void RefreshVariable()
        {
            Raise(nameof(TypeDisplayName));
            Raise(nameof(HasVariableError));
            Raise(nameof(VariableHint));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// 采集任务组列表里的**一个任务组行**（P3.1）。
    /// <para>
    /// 行内三栏是文案（任务类型 / 执行方式 / 共N项），一个开关（启用），
    /// 展开后挂一串 <see cref="AcquisitionItemRow"/>。
    /// </para>
    /// </summary>
    public sealed class AcquisitionTaskItem : INotifyPropertyChanged
    {
        private readonly Func<string, GlobalVariable?> _findVariable;
        private bool _isExpanded;
        private int _index;

        public AcquisitionTaskItem(AcquisitionTask model, Func<string, GlobalVariable?> findVariable)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));
            _findVariable = findVariable ?? throw new ArgumentNullException(nameof(findVariable));

            Items = new ObservableCollection<AcquisitionItemRow>();
            foreach (AcquisitionItem item in model.Items)
            {
                Items.Add(new AcquisitionItemRow(item, findVariable));
            }

            ReindexItems();
        }

        public AcquisitionTask Model { get; }

        /// <summary>
        /// 序号（1 起，由所属集合统一重排）。
        /// <para>
        /// **不落盘**：它是列表位置，不是数据。删掉中间一组后，后面所有组的序号都要减一 ——
        /// 存进 JSON 只会让文件和界面各说各话。
        /// </para>
        /// </summary>
        public int Index
        {
            get => _index;
            set
            {
                if (_index == value)
                {
                    return;
                }

                _index = value;
                Raise();
            }
        }

        /// <summary>该任务组下的数据项（界面集合，与 <see cref="AcquisitionTask.Items"/> 一一对应）</summary>
        public ObservableCollection<AcquisitionItemRow> Items { get; }

        /// <summary>任务名称</summary>
        public string Name
        {
            get => Model.Name;
            set
            {
                if (Model.Name == value)
                {
                    return;
                }

                Model.Name = value ?? string.Empty;
                Raise();
            }
        }

        /// <summary>任务类型显示名（只读，改它走任务选项对话框）</summary>
        public string KindDisplay => AcquisitionTaskRules.DescribeKind(Model.Kind);

        /// <summary>执行方式显示名（只读，改它走任务选项对话框）</summary>
        public string ScheduleDisplay => AcquisitionTaskRules.DescribeSchedule(Model);

        /// <summary>「共 N 项」列的文字</summary>
        public string ItemCountText => "共 " + Items.Count + " 项";

        /// <summary>启用开关</summary>
        public bool Enabled
        {
            get => Model.Enabled;
            set
            {
                if (Model.Enabled == value)
                {
                    return;
                }

                Model.Enabled = value;
                Raise();
            }
        }

        /// <summary>是否展开数据项子表（**界面态，不落盘**）</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value)
                {
                    return;
                }

                _isExpanded = value;
                Raise();
                Raise(nameof(ExpandGlyph));
                Raise(nameof(ExpandToolTip));
            }
        }

        /// <summary>展开 / 收起按钮的字形（Segoe 图标字体：&#xE70D; 下箭头 / &#xE70E; 上箭头）</summary>
        public string ExpandGlyph => _isExpanded ? "\uE70E" : "\uE70D";

        public string ExpandToolTip => _isExpanded ? "收起数据项" : "展开数据项";

        /// <summary>新增一个数据项并返回（默认名「项N」）</summary>
        public AcquisitionItemRow AddItem()
        {
            var model = new AcquisitionItem { Name = AcquisitionTaskRules.NextItemName(Model.Items) };
            Model.Items.Add(model);

            var row = new AcquisitionItemRow(model, _findVariable);
            Items.Add(row);
            ReindexItems();
            RaiseCount();
            return row;
        }

        /// <summary>删除一个数据项</summary>
        public void RemoveItem(AcquisitionItemRow row)
        {
            if (row == null)
            {
                return;
            }

            Model.Items.Remove(row.Model);
            Items.Remove(row);
            ReindexItems();
            RaiseCount();
        }

        /// <summary>
        /// 清空本组全部数据项（子表表头的 🗑）。
        /// <para>
        /// 与 <c>AcquisitionTaskItemSet.Clear</c> 不是一回事：那个清的是**任务组**，
        /// 这个只清一个组里的**数据项**，组本身还在。命名上刻意区分开。
        /// </para>
        /// </summary>
        public void ClearItems()
        {
            if (Items.Count == 0)
            {
                return;
            }

            Model.Items.Clear();
            Items.Clear();
            RaiseCount();
        }

        /// <summary>
        /// 把「任务选项」对话框的结果写回模型。
        /// <para>
        /// ⚠️ 四项（类型 / 执行方式 / 周期 / 表达式）**都没有行内编辑器**，只能从这里进；
        /// 集中的好处是「改完必须刷新哪几列」只写一次（<see cref="RefreshFromModel"/>）。
        /// </para>
        /// </summary>
        public void ApplyDialogResult(
            AcquisitionTaskKind kind,
            AcquisitionTriggerMode trigger,
            double periodSeconds,
            string? expression)
        {
            Model.Kind = kind;
            Model.Trigger = trigger;
            Model.PeriodSeconds = periodSeconds;
            Model.Expression = expression ?? string.Empty;
            RefreshFromModel();
        }

        /// <summary>任务属性被对话框改过之后刷新各只读列</summary>
        public void RefreshFromModel()
        {
            Raise(nameof(Name));
            Raise(nameof(KindDisplay));
            Raise(nameof(ScheduleDisplay));
            Raise(nameof(Enabled));
        }

        /// <summary>变量表变化（改名 / 改类型 / 增删）后，刷新所有项的类型列</summary>
        public void RefreshVariables()
        {
            foreach (AcquisitionItemRow row in Items)
            {
                row.RefreshVariable();
            }
        }

        private void ReindexItems()
        {
            for (int i = 0; i < Items.Count; i++)
            {
                Items[i].Index = i + 1;
            }
        }

        private void RaiseCount()
        {
            Raise(nameof(ItemCountText));
            Raise(nameof(Items));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// 采集任务组集合的界面侧编排：增删、落盘。
    /// <para>底层数据与规则在 <see cref="AcquisitionTaskSet"/>，本类只加界面所需的包装。</para>
    /// </summary>
    public sealed class AcquisitionTaskItemSet
    {
        private readonly ObservableCollection<AcquisitionTaskItem> _items =
            new ObservableCollection<AcquisitionTaskItem>();

        private readonly AcquisitionTaskSet _set = new AcquisitionTaskSet();
        private readonly Func<string, GlobalVariable?> _findVariable;

        public AcquisitionTaskItemSet(Func<string, GlobalVariable?> findVariable)
        {
            _findVariable = findVariable ?? throw new ArgumentNullException(nameof(findVariable));
        }

        public ObservableCollection<AcquisitionTaskItem> Items => _items;

        /// <summary>载入定义（文件不存在则为空）</summary>
        public void Load()
        {
            _set.Load();
            Rebuild();
        }

        /// <summary>新增一个任务组（默认名「任务组N」），即时落盘</summary>
        public AcquisitionTaskItem Add()
        {
            AcquisitionTask task = _set.Add();

            // 新加的直接展开 —— 用户点「+」的下一步必然是往里加数据项
            var item = new AcquisitionTaskItem(task, _findVariable) { IsExpanded = true };
            _items.Add(item);
            Reindex();
            Save();
            return item;
        }

        /// <summary>删除一个任务组，即时落盘</summary>
        public void Remove(AcquisitionTaskItem item)
        {
            if (item == null)
            {
                return;
            }

            _items.Remove(item);
            _set.Remove(item.Model);

            // 删掉中间一组后，后面的组要整体减一，否则界面跳号
            Reindex();
            Save();
        }

        /// <summary>清空全部任务组，即时落盘</summary>
        public void Clear()
        {
            _items.Clear();
            _set.Clear();
            Reindex();
            Save();
        }

        /// <summary>
        /// 任务名称校验（含重名）。**不做落盘**：名字是边打字边提交的，
        /// 逐字符写盘没有意义，由调用方在失焦通过后统一 <see cref="Save"/>。
        /// </summary>
        public bool Rename(AcquisitionTaskItem item, string newName, out string error)
        {
            bool ok = _set.Rename(item.Model, newName, out error);
            if (ok)
            {
                item.Name = item.Model.Name;
            }

            return ok;
        }

        /// <summary>变量表变化后刷新所有项的类型列</summary>
        public void RefreshVariables()
        {
            foreach (AcquisitionTaskItem item in _items)
            {
                item.RefreshVariables();
            }
        }

        /// <summary>落盘</summary>
        public bool Save() => _set.Save();

        /// <summary>
        /// 按当前顺序重排任务组序号（1 起）。
        /// <para>
        /// ⚠️ 每次增删都要调 —— 序号是**位置**而不是数据，删掉中间一组后
        /// 后面所有组都得减一，否则界面会出现 1、2、4、5 这样的跳号。
        /// </para>
        /// </summary>
        private void Reindex()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                _items[i].Index = i + 1;
            }
        }

        private void Rebuild()
        {
            _items.Clear();
            int index = 0;
            foreach (AcquisitionTask task in _set.Items)
            {
                var item = new AcquisitionTaskItem(task, _findVariable)
                {
                    // 首个任务组默认展开：进来就能看见「数据项」长什么样；
                    // 其余收起，否则任务一多整页都是子表。展开态是界面态，不落盘。
                    IsExpanded = index++ == 0,
                };

                _items.Add(item);
            }

            Reindex();
        }
    }
}
