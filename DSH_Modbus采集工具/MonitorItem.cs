using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using DSH_Modbus采集工具.Drivers;
using DSH_Modbus采集工具.Modeling;

namespace DSH_Modbus采集工具
{
    /// <summary>
    /// 枚举值 → 大写名，供显示格式下拉框用。
    /// <para>
    /// 格式名按工业惯例全大写（<c>BIN</c>/<c>DEC</c>/<c>HEX</c>/<c>ASCII</c>），
    /// 而枚举名是 <c>Bin</c>/<c>Dec</c>… —— 直接用会把界面显示成驼峰，与手册不一致。
    /// </para>
    /// </summary>
    public sealed class EnumUpperConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => value?.ToString()?.ToUpperInvariant() ?? string.Empty;

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// 自由监控列表里的一行 —— <see cref="DataPoint"/> 的界面包装。
    /// <para>
    /// 与模型分开的原因：模型要能直接 JSON 往返（故不放 INPC 与界面态），
    /// 而界面需要「当前值」「解析错误」这类随采集变化的可通知属性。
    /// 两者职责不同，合在一起会让 JSON 里混进界面字段。
    /// </para>
    /// </summary>
    public sealed class MonitorItem : INotifyPropertyChanged
    {
        // 初值刻意留空而不是「—」：输入框里放个破折号既不是有效值、又要用户先删掉才能输，
        // 而「空」本身已经表达了"还没读到"。
        private string _currentValue = string.Empty;
        private string? _resolveError;
        private string? _readError;
        private string? _writeError;
        private bool _isEditing;
        private int _address = -1;

        /// <summary>
        /// 变量查找器：由 <see cref="MonitorItemSet.SetVariableLookup"/> 注入，
        /// 按名字取全局变量定义（取不到返回 null）。
        /// <para>
        /// 传委托而不是直接引用 <c>GlobalVariableSet</c>：本类在根目录、属于界面层，
        /// 而变量表是另一套独立数据（各存各的 json），不该在这里硬绑死。
        /// </para>
        /// </summary>
        private Func<string, GlobalVariable?>? _variableLookup;

        /// <summary>
        /// 本行是「内存变量」：变量存在但**没有映射地址**，故不参与 Modbus 读写，
        /// 值由用户在「当前值」格里直接维护（见 <see cref="TrySetMemoryValue"/>）。
        /// </summary>
        private bool _isMemoryVariable;

        private bool _isSelected;

        public MonitorItem(DataPoint model)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));
        }

        /// <summary>
        /// 是否是当前选中行（只影响底色，纯界面态，不落盘）。
        /// <para>表头的 ↑↓ 排序作用于选中行，故需要一个可见的「当前行」。</para>
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                OnPropertyChanged();
            }
        }

        /// <summary>注入变量查找器（见 <see cref="_variableLookup"/>）。</summary>
        internal void SetVariableLookup(Func<string, GlobalVariable?>? lookup) => _variableLookup = lookup;

        /// <summary>底层数据模型（持久化的就是它）。</summary>
        public DataPoint Model { get; }

        /// <summary>序号，界面左侧显示。</summary>
        /// <remarks>
        /// ⚠️ 排序后要让界面重读：<c>Model.Id</c> 变了不会自动通知，故由
        /// <see cref="RaiseIdChanged"/> 显式发一次 <c>PropertyChanged</c>。
        /// 少了它，拖动/上移下移之后左边那列 # 会停在旧值。
        /// </remarks>
        public int Id => Model.Id;

        /// <summary>序号被重新分配后通知绑定（见 <see cref="MonitorItemSet.Move"/>）。</summary>
        internal void RaiseIdChanged() => OnPropertyChanged(nameof(Id));

        /// <summary>元件符号，如 <c>D</c> / <c>HD</c> / <c>M</c>。</summary>
        public string Element => Model.Element;

        /// <summary>元件编号。</summary>
        public string Index => Model.Index;

        /// <summary>完整点位名，如 <c>HD100</c>。</summary>
        public string DisplayName => Model.DisplayName;

        /// <summary>
        /// 输入框里显示的文本：变量行是变量名，元件行是 <c>HD100</c> 这样的元件名。
        /// <para>
        /// 持久化对元件行仍是 <see cref="DataPoint.Element"/> + <see cref="DataPoint.Index"/>
        /// 两个字段（故旧 <c>data-points.json</c> 无损）；变量行存 <see cref="DataPoint.VariableName"/>。
        /// </para>
        /// </summary>
        public string Name => Model.IsVariable ? Model.VariableName : Model.DisplayName;

        /// <summary>
        /// 把合并输入框的文本拆成「符号 + 编号」写回模型（符号统一转大写）。
        /// <para>
        /// <b>拆分失败时不改动模型</b>（保留上次有效值），只置 <see cref="ResolveError"/> 标红 ——
        /// 用户打字途中必然经过 <c>"HD"</c>、<c>"HD1"</c> 这类中间态，
        /// 若把中间态也写进模型，会一边打一边把半截配置落盘。
        /// </para>
        /// <para>
        /// 地址解析**不在这里做**：调用方随后统一走 <see cref="MonitorItemSet.AfterChange"/>
        /// （它负责重解析 + 落盘），避免同一个动作出现两条解析路径。
        /// </para>
        /// </summary>
        public bool SetName(string? text, out string error)
        {
            string trimmed = (text ?? string.Empty).Trim();

            // 先当**变量名**试：变量名是 C 标识符，与元件名（元件符号 + 数字）不重叠，
            // 故「能匹配到变量表里的名字」就按变量行处理，否则退回元件解析。
            if (_variableLookup?.Invoke(trimmed) != null)
            {
                error = string.Empty;
                if (Model.VariableName == trimmed)
                {
                    return true;
                }

                Model.VariableName = trimmed;
                OnPropertyChanged(nameof(Name));
                OnPropertyChanged(nameof(IsVariable));
                OnPropertyChanged(nameof(IsTypeEditable));
                return true;
            }

            if (!CommDriver.TrySplitName(text, out string element, out string number, out error))
            {
                Address = -1;
                ResolveError = error;
                return false;
            }

            element = element.ToUpperInvariant();
            bool wasVariable = Model.IsVariable;
            if (Model.Element == element && Model.Index == number && !wasVariable)
            {
                return true;
            }

            // 从变量行改回元件行：变量名要清掉，否则 Resolve 仍走变量分支
            Model.VariableName = string.Empty;
            Model.Element = element;
            Model.Index = number;

            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(Element));
            OnPropertyChanged(nameof(Index));
            OnPropertyChanged(nameof(IsVariable));
            OnPropertyChanged(nameof(IsTypeEditable));
            return true;
        }

        /// <summary>本项是否监控的是全局变量（而非直接的元件地址）。</summary>
        public bool IsVariable => Model.IsVariable;

        /// <summary>
        /// 本项是**内存变量**（变量存在但没有映射地址）：不轮询、不写报文，
        /// 「当前值」格可直接编辑，值写进变量本身。
        /// </summary>
        public bool IsMemoryVariable => _isMemoryVariable;

        /// <summary>切换内存变量标记并通知界面（无变化时静默）。</summary>
        private void SetMemoryVariable(bool value)
        {
            if (_isMemoryVariable == value)
            {
                return;
            }

            _isMemoryVariable = value;
            OnPropertyChanged(nameof(IsMemoryVariable));
        }

        /// <summary>
        /// 变量行：类型下拉框必须**置灰** —— 类型由变量定义决定，
        /// 在监控行里改它等于绕过变量表，改完两边就不一致了。
        /// </summary>
        public bool IsTypeEditable => !IsVariable;

        /// <summary>类型显示名（STRING 带长度），如 <c>DINT</c> / <c>STRING[9]</c>。</summary>
        public string TypeDisplayName => Model.TypeDisplayName;

        /// <summary>显示格式名，如 <c>DEC</c> / <c>HEX</c>。</summary>
        public string FormatDisplayName => Model.Format.ToString().ToUpperInvariant();

        /// <summary>本项读取的原始单元个数（寄存器或线圈）。</summary>
        public int RawCount => Model.RawCount;

        /// <summary>备注。</summary>
        public string Comment => Model.Comment;

        /// <summary>解析后的 Modbus 地址；−1 表示未解析成功。</summary>
        public int Address
        {
            get => _address;
            private set
            {
                if (_address != value)
                {
                    _address = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(AddressText));
                }
            }
        }

        /// <summary>地址的显示文本；未解析时显示破折号。</summary>
        public string AddressText => _address < 0 ? "—" : _address.ToString();

        /// <summary>
        /// 当前值文本。显示的是最近一次**成功读取**的结果；
        /// 未读到（未连接、不在本分段、解析失败）时保持原值，从未读到过则为<b>空</b>。
        /// </summary>
        public string CurrentValue
        {
            get => _currentValue;
            set
            {
                if (_currentValue != value)
                {
                    _currentValue = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 最近一次读取失败的原因；成功或无读取时为 null。
        /// <para>
        /// 与 <see cref="ResolveError"/>（配置错误，标红整行）刻意分开：
        /// 读取失败只针对「当前值」这一格，不代表配置写错了 ——
        /// 例如从站临时回异常码、或读的瞬间断线，配置本身没问题。
        /// </para>
        /// </summary>
        public string? ReadError
        {
            get => _readError;
            set
            {
                if (_readError != value)
                {
                    _readError = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasReadError));
                    OnPropertyChanged(nameof(ValueToolTip));
                    OnPropertyChanged(nameof(ValueBrush));
                }
            }
        }

        public bool HasReadError => !string.IsNullOrEmpty(_readError);

        /// <summary>
        /// 用户正在编辑「当前值」格时为 true。轮询回填必须跳过该行 ——
        /// 否则输入到一半就被下一拍读值覆盖，根本没法输入。
        /// </summary>
        public bool IsEditing
        {
            get => _isEditing;
            set
            {
                if (_isEditing != value)
                {
                    _isEditing = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>最近一次写入失败的原因；成功或无写入时为 null。与 <see cref="ReadError"/> 分开。</summary>
        public string? WriteError
        {
            get => _writeError;
            set
            {
                if (_writeError != value)
                {
                    _writeError = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasWriteError));
                    OnPropertyChanged(nameof(ValueToolTip));
                    OnPropertyChanged(nameof(ValueBrush));
                }
            }
        }

        public bool HasWriteError => !string.IsNullOrEmpty(_writeError);

        /// <summary>
        /// 把轮询读到的值写入界面。**正在编辑时跳过** —— 保住用户手上的输入。
        /// <para>
        /// ⚠️ **不清 <see cref="WriteError"/>**：写失败的原因（如「该地址不可写」）
        /// 与读是否成功无关，清掉会让用户还没看到就消失。它只在下次成功写入时清除。
        /// </para>
        /// <para>
        /// 变量行还要**同时写回变量本身**（<see cref="GlobalVariable.Value"/>）：
        /// 同一份值在 <see cref="CurrentValue"/> 与变量里各存一份，只更新前者的话，
        /// 状态机表达式读到的就是旧值 —— 而且**只在「自由监控可见、状态机不可见」时**露出来，
        /// 因为那种组合下只有本行在刷新。同步之后「变量只有一份值」才成立，不再依赖哪个页面可见。
        /// </para>
        /// </summary>
        /// <returns>真正写入了返回 true；因正在编辑而跳过返回 false。</returns>
        public bool ApplyReadValue(string text)
        {
            if (IsEditing)
            {
                return false;
            }

            CurrentValue = text;

            if (Model.IsVariable && _variableLookup?.Invoke(Model.VariableName) is { } variable)
            {
                variable.Value = text;
            }

            return true;
        }

        /// <summary>
        /// 「当前值」格的悬停提示：**恒为空**。
        /// <para>
        /// 本表格是常驻监控界面，鼠标划过是高频动作，悬停弹提示属于干扰。
        /// 读/写失败改由**琥珀色**表达（<see cref="ValueBrush"/>）——
        /// 红＝配置要改，琥珀＝这一拍没读到/没写进去、配置没问题。
        /// </para>
        /// <para>
        /// 之所以还留这个属性：XAML 里仍绑着它，将来若要恢复「出错时给原因」
        /// 只需改这里一处，不必动 XAML。
        /// </para>
        /// </summary>
        public string? ValueToolTip => null;

        /// <summary>解析失败的原因；成功时为 null。非空时界面标红该行。</summary>
        public string? ResolveError
        {
            get => _resolveError;
            private set
            {
                if (_resolveError != value)
                {
                    _resolveError = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasError));
                    OnPropertyChanged(nameof(RowBrush));
                    OnPropertyChanged(nameof(ToolTipText));
                    OnPropertyChanged(nameof(NameToolTip));
                }
            }
        }

        public bool HasError => !string.IsNullOrEmpty(_resolveError);

        /// <summary>
        /// 整行的悬停提示：**恒为空**。
        /// <para>
        /// 监控表格是常驻界面，鼠标划过是高频动作，悬停弹提示纯属干扰。
        /// 原来正常态显示「地址 / 占用」也没人看，出错态的原因则已有两个更好的出口：
        /// <b>元件名称越界 → 失焦时弹窗</b>（见 <c>MonitorName_LostFocus</c>）；
        /// <b>读取失败 → 「当前值」格转琥珀色</b>（见 <see cref="ValueBrush"/>）。
        /// </para>
        /// </summary>
        public string? ToolTipText => null;

        /// <summary>
        /// 元件名称输入框的悬停提示：**恒为空**。
        /// <para>
        /// 名称无效时会在<b>失焦那一刻弹窗</b>说明原因，比悬停提示更不容易被忽略。
        /// </para>
        /// </summary>
        public string? NameToolTip => null;

        /// <summary>行文字颜色：配置出错标红，正常近黑。</summary>
        public System.Windows.Media.Brush RowBrush => HasError
            ? ErrorBrush
            : NormalBrush;

        /// <summary>
        /// 「当前值」格的颜色：读或写失败标琥珀，否则近黑。
        /// <para>
        /// 刻意与配置错误（<see cref="RowBrush"/> 的红色）区分开：
        /// 红＝配置要改，琥珀＝这一拍没读到/没写进去、配置没问题。
        /// 两者混用会让人去改一个本来正确的地址。
        /// </para>
        /// <para>
        /// 读失败与写失败**共用**琥珀：对用户而言都是「通讯侧的事」，
        /// 具体是读还是写由 ToolTip 文字区分（<see cref="ValueToolTip"/>）。
        /// </para>
        /// </summary>
        public System.Windows.Media.Brush ValueBrush => HasReadError || HasWriteError
            ? ReadErrorBrush
            : NormalBrush;

        private static readonly System.Windows.Media.Brush ErrorBrush = MakeFrozen(0xC4, 0x2B, 0x1C);
        private static readonly System.Windows.Media.Brush ReadErrorBrush = MakeFrozen(0xB2, 0x6A, 0x00);
        private static readonly System.Windows.Media.Brush NormalBrush = MakeFrozen(0x1A, 0x1A, 0x1A);

        private static System.Windows.Media.Brush MakeFrozen(byte r, byte g, byte b)
        {
            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        /// <summary>按当前驱动重新解析地址（切换设备类型或改动元件时调用）。</summary>
        public bool Resolve(CommDriver? driver, out string error)
        {
            // 类型 / 长度 / 格式 改了要让界面上的类型列（含 STRING[n]）与当前值跟随更新。
            // 放在这里而不是各个调用点，是因为所有改动都经 AfterChange → Resolve 这一条路径。
            OnPropertyChanged(nameof(TypeDisplayName));
            OnPropertyChanged(nameof(FormatDisplayName));
            OnPropertyChanged(nameof(RawCount));

            // 每次解析先假定不是内存变量；只有下面那个分支会重新置 true
            SetMemoryVariable(false);

            if (driver == null)
            {
                ResolveError = "未选择设备类型";
                Address = -1;
                error = ResolveError;
                return false;
            }

            // 变量行：类型 / 长度 / 地址都从变量表取，不用本行的副本
            if (Model.IsVariable)
            {
                return ResolveVariable(driver, out error);
            }

            if (DataPointStore.TryResolve(Model, driver, out error))
            {
                Address = Model.Address;
                ResolveError = null;
                return true;
            }

            Address = -1;
            ResolveError = error;
            return false;
        }

        /// <summary>
        /// 变量行的解析：把变量表的类型 / 长度 / 映射地址**同步进本行模型**，再按元件路径解析。
        /// <para>
        /// 同步而不是各读各的，是因为读写链路（<see cref="MonitorItemSet.TryBuildWrite"/>、
        /// <c>ApplyBlock</c> 的解码）都只认 <see cref="DataPoint"/> 自己的字段。
        /// 每次解析都覆盖一次，变量改了类型本行立刻跟着变，不会留过期副本。
        /// </para>
        /// </summary>
        private bool ResolveVariable(CommDriver driver, out string error)
        {
            GlobalVariable? variable = _variableLookup?.Invoke(Model.VariableName);
            if (variable == null)
            {
                // 手工改过的 data-points.json 里可能留着系统变量名（界面已拦，见 CommitMonitorName），
                // 报「不存在」会把人往错方向引 —— 它存在，只是不能在这里用。
                error = SystemVariable.Find(Model.VariableName) != null
                    ? $"系统变量 {Model.VariableName} 不能在自由监控中使用"
                    : $"全局变量 {Model.VariableName} 不存在";
                Address = -1;
                ResolveError = error;
                return false;
            }

            // 变量没有映射地址 = **内存变量**：合法，但不参与 Modbus 读写。
            // 它的「当前值」由用户在监控行里直接维护（见 TrySetMemoryValue），
            // 所以这里不是错误 —— 标红会让一条能用的行看起来是配错了。
            if (!variable.HasAddress)
            {
                Model.Type = variable.Type;
                Model.Format = variable.Format;
                Model.Length = variable.Length;
                Model.Element = string.Empty;
                Model.Index = string.Empty;
                Model.Address = -1;
                Model.Area = variable.Area;

                OnPropertyChanged(nameof(TypeDisplayName));
                OnPropertyChanged(nameof(FormatDisplayName));
                OnPropertyChanged(nameof(RawCount));

                Address = -1;
                ResolveError = null;
                SetMemoryVariable(true);

                // 正在输入时不要用变量里存的旧值冲掉用户手上的内容
                if (!IsEditing)
                {
                    CurrentValue = variable.Value;
                }

                error = string.Empty;
                return true;
            }

            if (variable.ResolvedAddress < 0)
            {
                error = variable.ResolveError.Length > 0
                    ? $"全局变量 {variable.Name} 的映射地址无效：{variable.ResolveError}"
                    : $"全局变量 {variable.Name} 的映射地址未解析";
                Address = -1;
                ResolveError = error;
                return false;
            }

            // 同步定义（类型决定读几个寄存器、怎么解码）
            Model.Type = variable.Type;
            Model.Format = variable.Format;
            Model.Length = variable.Length;
            Model.Element = ExtractElementOf(variable.Address);
            Model.Index = variable.Address[Model.Element.Length..];
            Model.Address = variable.ResolvedAddress;
            Model.Area = variable.Area;

            OnPropertyChanged(nameof(TypeDisplayName));
            OnPropertyChanged(nameof(FormatDisplayName));
            OnPropertyChanged(nameof(RawCount));

            Address = variable.ResolvedAddress;
            ResolveError = null;
            error = string.Empty;
            return true;
        }

        /// <summary>
        /// 提交内存变量的「当前值」：校验文本后写进**变量本身**（<see cref="GlobalVariable.Value"/>）。
        /// <para>
        /// 内存变量没有映射地址，没有寄存器可写 —— 值只存在内存里，
        /// 所以这里**不产生任何报文**，也不需要连接 PLC。
        /// 写进变量而不是只写本行，是为了让监控同一变量的多行保持一致，
        /// 且重新解析时能把值取回来。
        /// </para>
        /// <para>
        /// 校验规则与 Modbus 写入完全一致（BOOL 只认 0/1，其余按类型与显示格式解析），
        /// 免得同一个值在这里能填、接到地址后却写不进去。
        /// </para>
        /// </summary>
        /// <param name="orders">三套字节顺序（设备连接页下拉框的当前值）；只影响编码，不影响是否合法。</param>
        public bool TrySetMemoryValue(string? text, ByteOrderSettings orders, out string error)
        {
            error = string.Empty;

            if (!_isMemoryVariable)
            {
                error = "本行不是内存变量";
                return false;
            }

            GlobalVariable? variable = _variableLookup?.Invoke(Model.VariableName);
            if (variable == null)
            {
                error = $"全局变量 {Model.VariableName} 不存在";
                return false;
            }

            string value = (text ?? string.Empty).Trim();
            DataTypeSpec spec = DataTypes.Get(variable.Type);

            if (spec.CoilOnly)
            {
                if (value != "0" && value != "1")
                {
                    error = "线圈只能写 0 或 1";
                    return false;
                }
            }
            else if (!ValueCodec.TryEncodeRegisters(
                         variable.Type,
                         variable.Format,
                         value,
                         variable.Length,
                         orders,
                         out _,
                         out error))
            {
                return false;
            }

            variable.Value = value;
            CurrentValue = value;
            return true;
        }

        /// <summary>从元件名里取符号部分（"HD100" → "HD"）。</summary>
        private static string ExtractElementOf(string address)
        {
            int i = 0;
            while (i < address.Length && char.IsLetter(address[i]))
            {
                i++;
            }

            return i == 0 ? address : address[..i];
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// 自由监控项集合：增删改 + 落盘 + 地址解析。
    /// <para>
    /// 逐个项改动（类型、格式、编号）都要重解析并落盘 —— 统一走 <see cref="AfterChange"/>，
    /// 避免每个调用点各写一遍而漏掉某一处。
    /// </para>
    /// </summary>
    public sealed class MonitorItemSet
    {
        private readonly ObservableCollection<MonitorItem> _items = new();

        public ObservableCollection<MonitorItem> Items => _items;

        /// <summary>当前驱动，用于解析地址；随设备类型切换而更新。</summary>
        public CommDriver? Driver { get; private set; }

        /// <summary>
        /// 数据解释用的三套字节顺序。默认取当前驱动，用户改设备连接页的下拉框后由窗口覆盖。
        /// <para>
        /// 放在这里而不是每次从窗口读：轮询在后台线程回填（<see cref="ApplyBlock"/>），
        /// 而 <c>Polling/</c> 不许碰 WPF，只能从宿主暴露的集合上取这份快照。
        /// </para>
        /// </summary>
        public ByteOrderSettings ByteOrders { get; private set; } = ByteOrderSettings.Default;

        /// <summary>变量查找器，由窗口在载入全局变量表后注入（见 <see cref="MonitorItem.SetVariableLookup"/>）。</summary>
        private Func<string, GlobalVariable?>? _variableLookup;

        /// <summary>
        /// 注入变量查找器。
        /// <para>
        /// 必须在 <see cref="Load"/> **之前**调用 —— 载入时就要按变量表解析，
        /// 否则第一轮会把变量行当成元件行而标红。
        /// </para>
        /// </summary>
        public void SetVariableLookup(Func<string, GlobalVariable?>? lookup)
        {
            _variableLookup = lookup;

            foreach (MonitorItem item in _items)
            {
                item.SetVariableLookup(lookup);
            }
        }

        public void SetDriver(CommDriver? driver)
        {
            Driver = driver;

            // 换设备类型 = 换品牌约定：字序一律回到该驱动的出厂默认（用户定案）。
            // 调用方（窗口）随后会把下拉框同步成同一份值，故这里不需要通知谁。
            ByteOrders = ByteOrderSettings.FromDriver(driver);

            ReResolveAll();
        }

        /// <summary>用户改了设备连接页的字节顺序下拉框：立即生效于下一拍读/写。</summary>
        public void SetByteOrders(ByteOrderSettings orders) => ByteOrders = orders;

        /// <summary>
        /// 从磁盘载入。解析失败项**保留在列表里并标红**，不静默丢弃 ——
        /// 否则用户会以为配置丢了；标红才能让人看到「哪一项需要改」。
        /// </summary>
        public void Load()
        {
            _items.Clear();
            foreach (DataPoint p in DataPointStore.Load())
            {
                var item = new MonitorItem(p);
                item.SetVariableLookup(_variableLookup);
                item.Resolve(Driver, out _);
                _items.Add(item);
            }
        }

        public MonitorItem Add(DataPoint model)
        {
            model.Id = NextId();
            var item = new MonitorItem(model);
            item.SetVariableLookup(_variableLookup);
            item.Resolve(Driver, out _);
            _items.Add(item);
            Save();
            return item;
        }

        public void Remove(MonitorItem item)
        {
            if (_items.Remove(item))
            {
                Save();
            }
        }

        /// <summary>
        /// 把一项挪到新位置（用户点表头 ↑↓ 排序）。
        /// <para>
        /// ⚠️ 这里的 <c>_items</c> **同时**是界面集合与落盘顺序（`DataPointStore` 按它写盘、
        /// 按文件顺序读回），所以一次 <c>Move</c> 两边都改了 —— 不需要像全局变量那样
        /// 维护两份列表。改完顺序重启也是新顺序。
        /// </para>
        /// <para>越界会被夹到两端；位置没变时直接返回 false（调用方据它决定要不要落盘）。</para>
        /// </summary>
        public bool Move(MonitorItem item, int newIndex)
        {
            int oldIndex = _items.IndexOf(item);
            if (oldIndex < 0)
            {
                return false;
            }

            if (newIndex < 0)
            {
                newIndex = 0;
            }

            if (newIndex > _items.Count - 1)
            {
                newIndex = _items.Count - 1;
            }

            if (newIndex == oldIndex)
            {
                return false;
            }

            _items.Move(oldIndex, newIndex);

            // 顺序变了，行号也要跟着重排：从 1 连续编号，
            // 否则会出现「显示 3、6、7」这种跟实际位置对不上的序号
            for (int i = 0; i < _items.Count; i++)
            {
                MonitorItem row = _items[i];
                if (row.Model.Id != i + 1)
                {
                    row.Model.Id = i + 1;
                    row.RaiseIdChanged();
                }
            }

            Save();
            return true;
        }

        /// <summary>清空全部监控项并落盘（「全部删除」按钮）。调用方负责先做二次确认。</summary>
        public void Clear()
        {
            if (_items.Count == 0)
            {
                return;
            }

            _items.Clear();
            Save();
        }

        /// <summary>某项的类型 / 格式 / 编号 / 备注被改动后调用：重解析 + 落盘。</summary>
        public void AfterChange(MonitorItem item)
        {
            item.Resolve(Driver, out _);
            Save();
        }

        /// <summary>
        /// 只重解析、**不落盘**。用于「试算」场景 ——
        /// 例如失焦时先判断新名称是否越界，越界就不该写进 data-points.json。
        /// </summary>
        public void ResolveOnly(MonitorItem item)
        {
            item.Resolve(Driver, out _);
        }

        public void ReResolveAll()
        {
            foreach (MonitorItem item in _items)
            {
                item.Resolve(Driver, out _);
            }
        }

        /// <summary>
        /// 生成一次读取计划：把当前可读的项按「区域 + 是否连续」归并成若干次报文。
        /// <para>
        /// 归并规则与 IO 轮询同源（<c>PollOnceAsync</c>）：
        /// 不同 <see cref="ModbusArea"/> 不能混在一次请求里（区间空隙地址从站不接受），
        /// 故先按区分组；同区内再按<b>地址连续性</b>分块 —— 自由监控的项彼此可能
        /// 隔很远（D0 与 D8000），一次跨越上千点既慢又易超 2000 上限。
        /// </para>
        /// <para>
        /// 只返回<b>解析成功</b>的项；解析失败的项由调用方标「配置有误」，
        /// 不发报文（地址未知，发了也没意义）。
        /// </para>
        /// </summary>
        public List<ReadBlock> BuildReadPlan()
        {
            var plan = new List<ReadBlock>();

            foreach (var areaGroup in ReadableItems().GroupBy(x => x.Item.Model.Area))
            {
                // 区内按地址排序后顺序扫描，地址不连续的断开成新块
                var ordered = areaGroup.OrderBy(x => x.Item.Address).ToList();
                var current = new List<(MonitorItem Item, int Units)>();

                foreach (var entry in ordered)
                {
                    int start = entry.Item.Address;
                    int units = UnitsOf(entry.Item);

                    if (current.Count > 0)
                    {
                        // 与本块已有范围的末地址相比，紧邻或重叠则并入；有空洞则断开
                        int curEnd = 0;
                        foreach (var c in current)
                        {
                            int end = c.Item.Address + c.Units;
                            if (end > curEnd)
                            {
                                curEnd = end;
                            }
                        }

                        int blockStart = current.Min(c => c.Item.Address);
                        bool adjacent = start <= curEnd;
                        bool withinLimit = (Math.Max(curEnd, start + units) - blockStart) <= MaxUnitsPerRead;

                        if (!adjacent || !withinLimit)
                        {
                            plan.Add(MakeBlock(areaGroup.Key, blockStart, current));
                            current = new List<(MonitorItem, int)>();
                        }
                    }

                    current.Add((entry.Item, units));
                }

                if (current.Count > 0)
                {
                    plan.Add(MakeBlock(areaGroup.Key, current.Min(c => c.Item.Address), current));
                }
            }

            return plan;
        }

        private IEnumerable<(MonitorItem Item, int Units)> ReadableItems()
        {
            foreach (MonitorItem item in _items)
            {
                if (item.Address >= 0 && !item.HasError)
                {
                    yield return (item, UnitsOf(item));
                }
            }
        }

        /// <summary>本项占用的读取单元数：线圈恒为 1，寄存器按类型与 STRING 长度算。</summary>
        private static int UnitsOf(MonitorItem item)
        {
            DataTypeSpec spec = DataTypes.Get(item.Model.Type);
            return spec.CoilOnly ? 1 : spec.RegistersForLength(item.Model.Length);
        }

        private static ReadBlock MakeBlock(ModbusArea area, int start, List<(MonitorItem Item, int Units)> entries)
        {
            int end = start;
            foreach (var e in entries)
            {
                int e2 = e.Item.Address + e.Units;
                if (e2 > end)
                {
                    end = e2;
                }
            }

            return new ReadBlock
            {
                Area = area,
                StartAddress = start,
                Count = end - start,
                Entries = entries.Select(e => new ReadEntry
                {
                    Item = e.Item,
                    Offset = e.Item.Address - start,
                    Units = e.Units,
                }).ToList(),
            };
        }

        /// <summary>单次报文最多读取的单元数（线圈/寄存器分别有 2000 上限，取小者统一约束）。</summary>
        private const int MaxUnitsPerRead = 2000;

        /// <summary>
        /// 把一次读到的原始单元回填到块内各项，按各行的类型/格式/字节顺序格式化成显示文本。
        /// <para>
        /// 逐项 try/catch：某一项解码失败（理论上不该发生，解析阶段已校验过长度）
        /// 只标该项读失败，不能连累同块其它项的值。
        /// </para>
        /// </summary>
        public static void ApplyBlock(ReadBlock block, bool[]? coils, ushort[]? registers, ByteOrderSettings orders)
        {
            foreach (ReadEntry e in block.Entries)
            {
                try
                {
                    if (block.Area is ModbusArea.Coil or ModbusArea.DiscreteInput)
                    {
                        if (coils == null || e.Offset >= coils.Length)
                        {
                            e.Item.ReadError = "响应长度不足";
                            continue;
                        }

                        e.Item.ApplyReadValue(ValueCodec.FormatCoil(coils[e.Offset]));
                    }
                    else
                    {
                        if (registers == null || e.Offset + e.Units > registers.Length)
                        {
                            e.Item.ReadError = "响应长度不足";
                            continue;
                        }

                        ReadOnlySpan<ushort> slice = registers.AsSpan(e.Offset, e.Units);
                        e.Item.ApplyReadValue(ValueCodec.FormatRegisters(
                            e.Item.Model.Type,
                            slice,
                            e.Item.Model.Format,
                            orders,
                            e.Item.Model.Length));
                    }

                    // 读成功即清掉上一次的读失败标记（含「长度不足」的临时故障）
                    e.Item.ReadError = null;
                }
                catch (Exception ex)
                {
                    e.Item.ReadError = ex.Message;
                }
            }
        }

        /// <summary>把本集合内所有项标记为读失败（整条连接读崩时用，如断开瞬间）。</summary>
        public void MarkAllReadFailed(string reason)
        {
            foreach (MonitorItem item in _items)
            {
                // 内存变量根本不在读取计划里，给它标"读失败"是误报
                if (!item.IsMemoryVariable)
                {
                    item.ReadError = reason;
                }
            }
        }

        /// <summary>
        /// 生成某一项的写入计划：把界面文本编码成报文参数。
        /// <para>
        /// 只读区（输入寄存器 / 离散输入）**不预先拦截** —— 按需求仍可提交，
        /// 由从站回异常码，让用户看到真实原因（而非被界面挡住）。
        /// </para>
        /// </summary>
        public static bool TryBuildWrite(
            MonitorItem item,
            string? text,
            ByteOrderSettings orders,
            out WritePlan plan,
            out string error)
        {
            plan = default;
            error = string.Empty;

            if (item.HasError)
            {
                error = item.ResolveError ?? "该项配置有误";
                return false;
            }

            // 内存变量没有地址，谈不上报文；它的值走 TrySetMemoryValue
            if (item.IsMemoryVariable)
            {
                error = "内存变量不通过 Modbus 写入";
                return false;
            }

            DataTypeSpec spec = DataTypes.Get(item.Model.Type);

            // 线圈：BOOL 走 FC05，只接受 0 / 1
            if (spec.CoilOnly)
            {
                string s = (text ?? string.Empty).Trim();
                if (s != "0" && s != "1")
                {
                    error = "线圈只能写 0 或 1";
                    return false;
                }

                plan = new WritePlan
                {
                    Area = item.Model.Area,
                    Address = item.Address,
                    IsCoil = true,
                    CoilValue = s == "1",
                };
                return true;
            }

            if (!ValueCodec.TryEncodeRegisters(
                    item.Model.Type,
                    item.Model.Format,
                    text,
                    item.Model.Length,
                    orders,
                    out ushort[] regs,
                    out error))
            {
                return false;
            }

            plan = new WritePlan
            {
                Area = item.Model.Area,
                Address = item.Address,
                IsCoil = false,
                Registers = regs,
            };
            return true;
        }

        private void Save()
        {
            var models = new List<DataPoint>(_items.Count);
            foreach (MonitorItem item in _items)
            {
                models.Add(item.Model);
            }

            DataPointStore.Save(models);
        }

        private int NextId()
        {
            int max = 0;
            foreach (MonitorItem item in _items)
            {
                if (item.Id > max)
                {
                    max = item.Id;
                }
            }

            return max + 1;
        }
    }

    /// <summary>
    /// 一次 Modbus 读请求的规划：读 <see cref="Area"/> 区从 <see cref="StartAddress"/> 起
    /// <see cref="Count"/> 个单元，再按 <see cref="Entries"/> 的偏移回填各项。
    /// </summary>
    public sealed class ReadBlock
    {
        public ModbusArea Area { get; init; }

        public int StartAddress { get; init; }

        public int Count { get; init; }

        public List<ReadEntry> Entries { get; init; } = new();
    }

    /// <summary>块内某一项：从块起始地址的偏移（单元数）与自身占用长度。</summary>
    public sealed class ReadEntry
    {
        public MonitorItem Item { get; init; } = null!;

        public int Offset { get; init; }

        public int Units { get; init; }
    }

}
