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
        private bool _isWriteTask;
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

        /// <summary>
        /// 来源变量（**仅写入类型**）：值从哪个全局变量取。
        /// <para>读取类型下这个字段恒为空，界面也不显示这一列。</para>
        /// </summary>
        public string SourceVariableName
        {
            get => Model.SourceVariableName;
            set
            {
                if (Model.SourceVariableName == value)
                {
                    return;
                }

                Model.SourceVariableName = value ?? string.Empty;
                Raise();
                Raise(nameof(HasSourceError));
                Raise(nameof(SourceHint));

                // 来源一改，两件事跟着变：写入变量这一格能不能编辑；已填的写入变量还合不合法
                Raise(nameof(CanEditVariable));
                Raise(nameof(HasVariableError));
                Raise(nameof(VariableHint));
            }
        }

        /// <summary>被引用的全局变量定义；未填或找不到时为 <c>null</c>。</summary>
        public GlobalVariable? Variable =>
            Model.VariableName.Length == 0 ? null : _findVariable(Model.VariableName);

        /// <summary>
        /// 来源变量的解析结果：**系统变量或全局变量**都算数。
        /// <para>
        /// ⚠️ 与上面的 <see cref="Variable"/> 口径**不同**：写入变量要落到 PLC 地址上，
        /// 只能是全局变量；来源变量只是「值从哪来」，系统变量（如 <c>sys_True</c>、
        /// <c>sys_DateTime_Second</c>）同样有意义。
        /// </para>
        /// </summary>
        public AcquisitionSource? SourceVariable =>
            AcquisitionSource.Resolve(Model.SourceVariableName, _findVariable);

        /// <summary>
        /// 所属任务组是不是**写入类型**。由父行统一设置 ——
        /// 数据项自己看不出这件事（读取类型下根本没有「来源变量」这一格）。
        /// </summary>
        public bool IsWriteTask => _isWriteTask;

        /// <summary>
        /// 写入变量这一格**能不能打字**（用户定案 2026-10-27）。
        /// <para>
        /// 读取类型：恒可编辑。
        /// 写入类型：**来源变量填对之前一律置灰**。顺序即语义 ——
        /// 写入变量的合法取值范围（数据类型必须与来源相同）完全由来源决定，
        /// 先让人挑目标、挑完再报「类型不对」，等于让人先做一遍注定要改的选择。
        /// </para>
        /// </summary>
        public bool CanEditVariable => !_isWriteTask || SourceVariable != null;

        /// <summary>父行设置写入类型，并刷新所有随之变化的列。</summary>
        internal void SetWriteTask(bool value)
        {
            if (_isWriteTask == value)
            {
                return;
            }

            _isWriteTask = value;
            Raise(nameof(IsWriteTask));
            Raise(nameof(CanEditVariable));
            Raise(nameof(HasVariableError));
            Raise(nameof(VariableHint));
        }

        /// <summary>来源变量填了、但系统变量表与全局变量表里都没有 → 标红提示。</summary>
        public bool HasSourceError =>
            Model.SourceVariableName.Length > 0 && SourceVariable == null;

        /// <summary>来源变量红字提示文案（悬停可见）</summary>
        public string SourceHint =>
            HasSourceError
                ? "系统变量表和全局变量表里都没有“" + Model.SourceVariableName + "”"
                : SourceVariable switch
                {
                    null => "尚未选择来源变量（可填系统变量或全局变量）",
                    { IsSystemVariable: true } s => "系统变量 · " + s.TypeDisplayName + " · " + s.Comment,
                    { } s => s.Comment,
                };

        /// <summary>
        /// 数据类型列（**只读**，由变量反推）。
        /// <para>没填变量、或变量名不存在时给一个短横占位 —— 留空白会让人以为这列坏了。</para>
        /// </summary>
        public string TypeDisplayName =>
            Variable?.TypeDisplayName ?? "—";

        /// <summary>
        /// 写入变量这一格有问题 → 标红（输入框与数据类型列共用这一条）。
        /// <para>
        /// 三种情况：名字在全局变量表里查不到；写入类型下数据类型与来源变量不同；
        /// 写入类型下变量落在**只读区**（X 等输入元件，用户定案 2026-10-29）。
        /// </para>
        /// <para>
        /// ⚠️ **未映射地址不算错**（用户定案 2026-10-27）：允许先建一条纯内存变量当写入目标，
        /// 地址以后再补。这里若拦下来，界面上就再也填不进去了。
        /// </para>
        /// </summary>
        public bool HasVariableError
        {
            get
            {
                if (Model.VariableName.Length == 0)
                {
                    return false;
                }

                GlobalVariable? variable = Variable;
                if (variable == null)
                {
                    return true;
                }

                AcquisitionSource? source = SourceVariable;
                if (_isWriteTask
                    && source != null
                    && !AcquisitionTaskRules.WriteTypesMatch(source.Type, variable.Type))
                {
                    return true;
                }

                // 只读区不能当写入目标（用户定案 2026-10-29）：全局变量本身允许映射 X，
                // 所以这个判据只在这一格、且只在写入类型下成立。与配置检查同一口径。
                return _isWriteTask && variable.IsReadOnly;
            }
        }

        /// <summary>红字提示文案（悬停可见）</summary>
        public string VariableHint
        {
            get
            {
                string name = Model.VariableName;

                if (name.Length == 0)
                {
                    // 写入类型：来源没填对 → 先催来源；来源已经填对了 → 就只剩这一格空着。
                    // 不加后半句的话，来源填好后提示还在催「先填写来源变量」，等于骗人。
                    return _isWriteTask && SourceVariable == null
                        ? "先填写来源变量，再选择写入变量"
                        : "尚未选择变量";
                }

                GlobalVariable? variable = Variable;
                if (variable == null)
                {
                    return "全局变量表里没有“" + name + "”";
                }

                AcquisitionSource? source = SourceVariable;
                if (_isWriteTask
                    && source != null
                    && !AcquisitionTaskRules.WriteTypesMatch(source.Type, variable.Type))
                {
                    return AcquisitionTaskRules.DescribeTypeMismatch(
                        source.Name, source.Type, name, variable.Type);
                }

                if (_isWriteTask && variable.IsReadOnly)
                {
                    return variable.Address + " 属于只读区（输入元件），不能作为写入目标";
                }

                string hint = string.IsNullOrWhiteSpace(variable.Comment) ? name : variable.Comment;
                return variable.HasAddress ? hint : hint + " · 未映射地址（数据不会下发到设备）";
            }
        }

        /// <summary>变量定义变化（改名 / 改类型）后刷新只读列。</summary>
        public void RefreshVariable()
        {
            Raise(nameof(TypeDisplayName));
            Raise(nameof(HasVariableError));
            Raise(nameof(VariableHint));
            Raise(nameof(HasSourceError));
            Raise(nameof(SourceHint));
            Raise(nameof(CanEditVariable));
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
                var row = new AcquisitionItemRow(item, findVariable);
                row.SetWriteTask(IsWrite);
                AttachItem(row);
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

        /// <summary>是否写入类型 —— 子表的列集合要跟着它变（写入多一列「来源变量」）</summary>
        public bool IsWrite => Model.Kind == AcquisitionTaskKind.Write;

        /// <summary>
        /// 执行方式显示名（只读，改它走任务选项对话框）。
        /// <para>
        /// **只有两个字**：<c>周期</c> / <c>触发</c>（用户定案 2026-10-29）。
        /// 周期多少秒、触发是上升沿 —— 这些细节在「…」任务选项对话框里看，
        /// 列里不重复一遍，省下的宽度留给「任务名称」。
        /// </para>
        /// </summary>
        public string ScheduleDisplay => AcquisitionTaskRules.DescribeShortTrigger(Model.Trigger);

        /// <summary>「共 N 项」列的文字</summary>
        public string ItemCountText => "共 " + Items.Count + " 项";

        /// <summary>
        /// 该任务组当前的配置问题清单（**空 = 配置完整，可以执行**）。
        /// <para>
        /// 判据在 <see cref="AcquisitionTaskCheck"/>，界面与执行侧共用同一份 ——
        /// 否则迟早出现「界面标着配置错误、执行却在照跑」这种自相矛盾。
        /// </para>
        /// </summary>
        public IReadOnlyList<string> ConfigIssues =>
            AcquisitionTaskCheck.Describe(Model, _findVariable);

        /// <summary>
        /// 配置不完整 → 任务组行的启用开关被关掉并锁住、右侧显示琥珀色「配置错误」，整组不执行（用户定案 2026-10-27）。
        /// <para>用琥珀不用红：红字是本项目的「格子里填错了」，琥珀是「这东西现在不可用」。</para>
        /// </summary>
        public bool HasConfigError => ConfigIssues.Count > 0;

        /// <summary>提示文字（与判据同源，界面里不再另写一份字面量）</summary>
        public string ConfigErrorText => AcquisitionTaskCheck.ErrorText;

        /// <summary>
        /// **有效启用状态** —— 全项目唯一的「这组现在跑不跑」判定（用户定案 2026-10-27）。
        /// <para>
        /// 配置错误时强制为 false：界面把启用开关显示为「关」且灰掉不可点，
        /// 执行侧因此只需要看 `Enabled` 一条既有路径，不必再单独判一次配置。
        /// </para>
        /// <para>
        /// ⚠️ <see cref="Model"/>.Enabled 存的是**用户意图**，这里不改它 ——
        /// 数据管理是边输边存，用户清空变量名重打的那一瞬间也算「配置错误」；
        /// 若拿这个瞬时状态去改写落盘的意图，打完字这组就永久停采了，且没有任何痕迹。
        /// 所以修好配置后开关会自己回到「开」并继续采集。
        /// </para>
        /// <para>
        /// ⚠️ 必须**可写**：CheckBox.IsChecked 默认 TwoWay，绑只读属性会在模板实例化时抛
        /// XamlParseException（「无法对只读属性进行 TwoWay 绑定」）—— 整个采集页直接打不开，
        /// 实机踩过。set 只把**用户意图**转交给 <see cref="Enabled"/>，
        /// EffectiveEnabled 本身永远由 get 算，不存第二份状态。
        /// 配置错误时开关被 <see cref="CanToggleEnabled"/> 禁用，正常点不到这里。
        /// </para>
        /// </summary>
        public bool EffectiveEnabled
        {
            get => Model.Enabled && !HasConfigError;
            set => Enabled = value;
        }

        /// <summary>启用开关能否被点：配置错误时锁住（与 <see cref="EffectiveEnabled"/> 配套）</summary>
        public bool CanToggleEnabled => !HasConfigError;

        /// <summary>
        /// 配置变化后刷新开关与提示（`HasConfigError` / `EffectiveEnabled` / `CanToggleEnabled`）。
        /// <para>
        /// 触发点有三类：行内改变量（订阅 <see cref="AcquisitionItemRow.PropertyChanged"/>）、
        /// 增删数据项（走 <see cref="RaiseCount"/>）、改任务选项或变量表（走
        /// <see cref="RefreshFromModel"/> / <see cref="RefreshVariables"/>）。
        /// </para>
        /// </summary>
        public void RefreshConfigError()
        {
            Raise(nameof(HasConfigError));
            Raise(nameof(EffectiveEnabled));
            Raise(nameof(CanToggleEnabled));
        }

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
                Raise(nameof(EffectiveEnabled));
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
            row.SetWriteTask(IsWrite);
            AttachItem(row);
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

            row.PropertyChanged -= Row_PropertyChanged;
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

            foreach (AcquisitionItemRow row in Items)
            {
                row.PropertyChanged -= Row_PropertyChanged;
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
            string? expression,
            bool recordEnabled,
            string? savePath)
        {
            Model.Kind = kind;
            Model.Trigger = trigger;
            Model.PeriodSeconds = periodSeconds;
            Model.Expression = expression ?? string.Empty;
            Model.RecordEnabled = recordEnabled;
            Model.SavePath = savePath ?? string.Empty;

            // 从写入切回读取时，「来源变量」不再有意义。留着不清的话，切回写入会
            // 冒出上次的残留值，而用户中途早改过别的项了，看见只会困惑。
            if (kind != AcquisitionTaskKind.Write)
            {
                foreach (AcquisitionItemRow row in Items)
                {
                    row.SourceVariableName = string.Empty;
                }
            }

            RefreshFromModel();
        }

        /// <summary>任务属性被对话框改过之后刷新各只读列</summary>
        public void RefreshFromModel()
        {
            Raise(nameof(Name));
            Raise(nameof(KindDisplay));
            Raise(nameof(ScheduleDisplay));
            Raise(nameof(Enabled));
            Raise(nameof(IsWrite));

            // 读写互切会改写每一行的规则：写入变量只在写入类型下受「先填来源变量」约束
            foreach (AcquisitionItemRow row in Items)
            {
                row.SetWriteTask(IsWrite);
            }

            // 读写互切会整套换掉判据（读取只查变量，写入还要查来源与类型）
            RefreshConfigError();
        }

        /// <summary>变量表变化（改名 / 改类型 / 增删）后，刷新所有项的类型列</summary>
        public void RefreshVariables()
        {
            foreach (AcquisitionItemRow row in Items)
            {
                row.RefreshVariable();
            }

            // 变量被改名或删掉，原本「配置完整」的组可能就报错了
            RefreshConfigError();
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

            // 增删数据项会改变配置完整性（空组 = 配置错误），这里一并刷新
            RefreshConfigError();
        }

        /// <summary>把新行挂进集合，并订阅它的变量变化（组级「配置错误」靠它转发）。</summary>
        private void AttachItem(AcquisitionItemRow row)
        {
            row.PropertyChanged += Row_PropertyChanged;
            Items.Add(row);
        }

        /// <summary>
        /// 行内改变量 → 组级配置问题跟着变。
        /// <para>
        /// 数据项自己不知道父组（它连「是不是写入类型」都是父行注入的），所以只能由父行订阅。
        /// 只认这两个属性：其余（<c>Index</c> / <c>IsExpanded</c>）不影响配置。
        /// </para>
        /// </summary>
        private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AcquisitionItemRow.VariableName)
                || e.PropertyName == nameof(AcquisitionItemRow.SourceVariableName))
            {
                RefreshConfigError();
            }
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
