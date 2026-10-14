using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
        private string _currentValue = "—";
        private string? _resolveError;
        private int _address = -1;

        public MonitorItem(DataPoint model)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));
        }

        /// <summary>底层数据模型（持久化的就是它）。</summary>
        public DataPoint Model { get; }

        /// <summary>序号，界面左侧显示。</summary>
        public int Id => Model.Id;

        /// <summary>元件符号，如 <c>D</c> / <c>HD</c> / <c>M</c>。</summary>
        public string Element => Model.Element;

        /// <summary>元件编号。</summary>
        public string Index => Model.Index;

        /// <summary>完整点位名，如 <c>HD100</c>。</summary>
        public string DisplayName => Model.DisplayName;

        /// <summary>
        /// 完整元件名称，如 <c>HD100</c> —— Brief 方案 2 的合并输入框绑定它。
        /// 持久化仍是 <see cref="DataPoint.Element"/> + <see cref="DataPoint.Index"/> 两个字段
        /// （故旧 <c>data-points.json</c> 无损），此处只是把两者拼起来给界面用。
        /// </summary>
        public string Name => Model.DisplayName;

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
            if (!CommDriver.TrySplitName(text, out string element, out string number, out error))
            {
                Address = -1;
                ResolveError = error;
                return false;
            }

            element = element.ToUpperInvariant();
            if (Model.Element == element && Model.Index == number)
            {
                return true;
            }

            Model.Element = element;
            Model.Index = number;

            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(Element));
            OnPropertyChanged(nameof(Index));
            return true;
        }

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
        /// 当前值文本。**本阶段为占位**「—」——
        /// 读取链路（FC03/FC01 组读）属于下一步，本次只做界面与增删改持久化。
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

        /// <summary>悬停提示：正常显示地址与占用，出错显示原因。</summary>
        public string ToolTipText => HasError
            ? $"配置有误：{_resolveError}"
            : $"{DisplayName} → Modbus 地址 {_address}，读 {RawCount} 个单元";

        /// <summary>
        /// 元件名称输入框的悬停提示：<b>出错时优先显示错误原因</b>。
        /// <para>
        /// 不能只用静态格式提示 —— 方案 2 的元件是自由文本，输错概率高于方案 1 的下拉选择，
        /// 而用户排查时鼠标第一个停的地方就是这个输入框；
        /// 若框上恒定显示「如 HD100 / X17」，真正的错误原因（藏在整行 ToolTip 上）就被遮住了。
        /// </para>
        /// </summary>
        public string NameToolTip => HasError
            ? $"配置有误：{_resolveError}"
            : "元件名称，如 HD100 / X17（X/Y 为八进制编号）";

        /// <summary>行文字颜色：出错标红，正常近黑。</summary>
        public System.Windows.Media.Brush RowBrush => HasError
            ? ErrorBrush
            : NormalBrush;

        private static readonly System.Windows.Media.Brush ErrorBrush = MakeFrozen(0xC4, 0x2B, 0x1C);
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
            if (driver == null)
            {
                ResolveError = "未选择设备类型";
                Address = -1;
                error = ResolveError;
                return false;
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

        public void SetDriver(CommDriver? driver)
        {
            Driver = driver;
            ReResolveAll();
        }

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
                item.Resolve(Driver, out _);
                _items.Add(item);
            }
        }

        public MonitorItem Add(DataPoint model)
        {
            model.Id = NextId();
            var item = new MonitorItem(model);
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

        public void ReResolveAll()
        {
            foreach (MonitorItem item in _items)
            {
                item.Resolve(Driver, out _);
            }
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
}
