using System.ComponentModel;
using System.Runtime.CompilerServices;
using DSH_Modbus采集工具.Modeling;

namespace DSH_Modbus采集工具
{
    /// <summary>
    /// 设备监控页「系统变量」监控表里的一行 —— <see cref="SystemVariable"/> 的界面包装（P2.3）。
    /// <para>
    /// 与 <see cref="GlobalVarItem"/> / <c>MonitorItem</c> 的区别：那两个是**可编辑**的模型包装，
    /// 这个是**纯展示** —— 只有 <see cref="Value"/> 会变（定时器每 100ms 推一次），
    /// 名称 / 类型 / 注释全来自写死的清单，永远不变。
    /// </para>
    /// <para>
    /// ⚠️ 值仍然只存在<b>内存</b>里：系统变量不落盘、不可改写，重启就从头算。
    /// </para>
    /// </summary>
    public sealed class SystemVarRow : INotifyPropertyChanged
    {
        private string _value = string.Empty;

        public SystemVarRow(SystemVariable model)
        {
            Model = model ?? throw new System.ArgumentNullException(nameof(model));
        }

        public SystemVariable Model { get; }

        public int Id => Model.Id;

        public string Name => Model.Name;

        public string TypeName => Model.TypeName;

        public string Comment => Model.Comment;

        /// <summary>
        /// 当前值（显示文本）。<c>BOOL</c> 是 <c>"1"</c> / <c>"0"</c>，其余是十进制整数。
        /// <para>setter 里做「值没变就不通知」—— 100ms 一拍、16 行，
        /// 每秒几百次无谓的 PropertyChanged 会被 WPF 逐条派发到绑定上。</para>
        /// </summary>
        public string Value
        {
            get => _value;
            set
            {
                if (_value == value)
                {
                    return;
                }

                _value = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
