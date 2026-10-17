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
    /// 全局变量列表里的一行 —— <see cref="GlobalVariable"/> 的界面包装。
    /// <para>
    /// 与自由监控的 <c>MonitorItem</c> 同一思路：模型要能 JSON 往返（故不含 INPC 与界面态），
    /// 界面需要「当前值」「解析错误」这类随读写变化、要通知绑定的属性。
    /// </para>
    /// </summary>
    public sealed class GlobalVarItem : INotifyPropertyChanged
    {
        private string _value = "0";
        private string _resolveError = string.Empty;
        private string _readError = string.Empty;
        private string _writeError = string.Empty;
        private bool _isEditing;

        public GlobalVarItem(GlobalVariable model)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));
        }

        public GlobalVariable Model { get; }

        /// <summary>序号（界面左侧显示，1 起）</summary>
        public int Id { get; set; }

        public string Name
        {
            get => Model.Name;
            set
            {
                if (Model.Name == value)
                {
                    return;
                }

                Model.Name = value;
                Raise();
            }
        }

        /// <summary>
        /// 当前值。绑定到「当前值」输入框。
        /// <para>关联地址时由轮询回填；不关联地址时由用户手工输入。</para>
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

                _value = value ?? string.Empty;
                Model.Value = _value;
                Raise();
            }
        }

        /// <summary>映射地址原文（元件名，如 <c>HD100</c>）；空串 = 内存变量</summary>
        public string Address
        {
            get => Model.Address;
            set
            {
                if (Model.Address == value)
                {
                    return;
                }

                Model.Address = value ?? string.Empty;
                Raise();
            }
        }

        public string Comment
        {
            get => Model.Comment;
            set
            {
                if (Model.Comment == value)
                {
                    return;
                }

                Model.Comment = value ?? string.Empty;
                Raise();
            }
        }

        /// <summary>解析 / 校验失败原因（红色）。空 = 正常。</summary>
        public string ResolveError
        {
            get => _resolveError;
            set
            {
                if (_resolveError == value)
                {
                    return;
                }

                _resolveError = value ?? string.Empty;
                Raise();
            }
        }

        /// <summary>读失败原因（琥珀色）。空 = 正常。</summary>
        public string ReadError
        {
            get => _readError;
            set
            {
                if (_readError == value)
                {
                    return;
                }

                _readError = value ?? string.Empty;
                Raise();
            }
        }

        /// <summary>写失败原因（琥珀色）。空 = 正常。</summary>
        public string WriteError
        {
            get => _writeError;
            set
            {
                if (_writeError == value)
                {
                    return;
                }

                _writeError = value ?? string.Empty;
                Raise();
            }
        }

        /// <summary>用户正在编辑「当前值」—— 轮询不得回填，否则输入到一半被冲掉。</summary>
        public bool IsEditing
        {
            get => _isEditing;
            set
            {
                if (_isEditing == value)
                {
                    return;
                }

                _isEditing = value;
                Raise();
            }
        }

        /// <summary>是否关联了 PLC 地址</summary>
        public bool HasAddress => Model.HasAddress;

        /// <summary>映射区是否只读（方案 C：X 等输入元件）</summary>
        public bool IsReadOnly => Model.IsReadOnly;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// 全局变量集合的界面侧编排：增删改、校验、落盘、值重置。
    /// <para>底层数据与规则在 <see cref="GlobalVariableSet"/>，本类只加界面所需的包装。</para>
    /// </summary>
    public sealed class GlobalVarItemSet
    {
        private readonly ObservableCollection<GlobalVarItem> _items = new();
        private readonly GlobalVariableSet _set = new();

        public ObservableCollection<GlobalVarItem> Items => _items;

        public CommDriver? Driver => _set.Driver;

        public void SetDriver(CommDriver? driver)
        {
            _set.SetDriver(driver);
            Reindex();
        }

        /// <summary>载入定义并重置所有值为初始值</summary>
        public void Load()
        {
            _set.Load();
            Rebuild();
        }

        /// <summary>把所有变量的值重置为初始值（0 / 空串）</summary>
        public void ResetValues()
        {
            _set.ResetValues();
            foreach (GlobalVarItem item in _items)
            {
                item.Value = item.Model.Value;
            }
        }

        /// <summary>新增一条变量；失败返回 null 并给出原因</summary>
        public GlobalVarItem? Add(string name, out string error)
        {
            var model = new GlobalVariable
            {
                Name = name,
                Type = DataType.Int,
                Format = GlobalVariable.DefaultFormatFor(DataType.Int),
                Address = string.Empty,
                Comment = string.Empty,
            };

            GlobalVariable? added = _set.Add(model, out error);
            if (added == null)
            {
                return null;
            }

            var item = new GlobalVarItem(added);
            _items.Add(item);
            Reindex();
            Save();
            return item;
        }

        public void Remove(GlobalVarItem item)
        {
            _items.Remove(item);
            _set.Remove(item.Model);
            Reindex();
            Save();
        }

        public void Clear()
        {
            _items.Clear();
            _set.Clear();
            Save();
        }

        /// <summary>改名（含合法性与重名检查）</summary>
        public bool Rename(GlobalVarItem item, string newName, out string error)
        {
            bool ok = _set.Rename(item.Model, newName, out error);
            if (ok)
            {
                item.Name = item.Model.Name;
            }

            return ok;
        }

        /// <summary>重新解析一条（改类型 / 改地址后调用）</summary>
        public void ResolveOnly(GlobalVarItem item)
        {
            _set.ResolveOnly(item.Model);
            item.ResolveError = item.Model.ResolveError;
        }

        /// <summary>重新解析全部（换设备类型后调用）</summary>
        public void ReResolveAll()
        {
            _set.ReResolveAll();
            foreach (GlobalVarItem item in _items)
            {
                item.ResolveError = item.Model.ResolveError;
            }
        }

        /// <summary>落盘（只写定义）</summary>
        public bool Save() => _set.Save();

        /// <summary>
        /// 生成读取计划：只含**关联了地址且解析成功**的变量，按存储区分组、
        /// 地址连续者并块（单块 ≤2000 单元）。
        /// <para>
        /// 与自由监控的 <c>MonitorItemSet.BuildReadPlan</c> 是同一套算法，但两者类型不同
        /// （这里不共用是为免把自由监控那条已验证的路径改出问题）。
        /// </para>
        /// </summary>
        public List<GlobalVarReadBlock> BuildReadPlan()
        {
            var plan = new List<GlobalVarReadBlock>();

            IEnumerable<GlobalVarItem> readable = _items.Where(
                x => x.Model.HasAddress && x.Model.ResolvedAddress >= 0);

            foreach (var group in readable.GroupBy(x => x.Model.Area))
            {
                var ordered = group.OrderBy(x => x.Model.ResolvedAddress).ToList();
                var current = new List<(GlobalVarItem Item, int Units)>();

                foreach (GlobalVarItem item in ordered)
                {
                    int start = item.Model.ResolvedAddress;
                    int units = UnitsOf(item);

                    if (current.Count > 0)
                    {
                        int curEnd = 0;
                        foreach (var c in current)
                        {
                            int end = c.Item.Model.ResolvedAddress + c.Units;
                            if (end > curEnd)
                            {
                                curEnd = end;
                            }
                        }

                        int blockStart = current.Min(c => c.Item.Model.ResolvedAddress);
                        bool adjacent = start <= curEnd;
                        bool withinLimit = (Math.Max(curEnd, start + units) - blockStart) <= MaxUnitsPerRead;

                        if (!adjacent || !withinLimit)
                        {
                            plan.Add(MakeBlock(group.Key, blockStart, current));
                            current = new List<(GlobalVarItem, int)>();
                        }
                    }

                    current.Add((item, units));
                }

                if (current.Count > 0)
                {
                    plan.Add(MakeBlock(group.Key, current.Min(c => c.Item.Model.ResolvedAddress), current));
                }
            }

            return plan;
        }

        /// <summary>本项占用的读取单元数：线圈恒为 1，寄存器按类型与 STRING 长度算。</summary>
        private static int UnitsOf(GlobalVarItem item)
        {
            DataTypeSpec spec = DataTypes.Get(item.Model.Type);
            return spec.CoilOnly ? 1 : spec.RegistersForLength(item.Model.Length);
        }

        private static GlobalVarReadBlock MakeBlock(
            ModbusArea area, int start, List<(GlobalVarItem Item, int Units)> entries)
        {
            int end = start;
            foreach (var e in entries)
            {
                int e2 = e.Item.Model.ResolvedAddress + e.Units;
                if (e2 > end)
                {
                    end = e2;
                }
            }

            return new GlobalVarReadBlock
            {
                Area = area,
                StartAddress = start,
                Count = end - start,
                Entries = entries.Select(e => new GlobalVarReadEntry
                {
                    Item = e.Item,
                    Offset = e.Item.Model.ResolvedAddress - start,
                    Units = e.Units,
                }).ToList(),
            };
        }

        /// <summary>单次报文最多读取的单元数。</summary>
        private const int MaxUnitsPerRead = 2000;

        /// <summary>
        /// 把一次读到的原始单元回填到块内各变量。
        /// <para>逐项 try/catch：某一项解码失败只标该项，不连累同块其它项。</para>
        /// </summary>
        public static void ApplyBlock(
            GlobalVarReadBlock block, bool[]? coils, ushort[]? registers, CommDriver driver)
        {
            foreach (GlobalVarReadEntry e in block.Entries)
            {
                // 用户正在编辑「当前值」时**不回填**，否则输入到一半被下一拍冲掉
                if (e.Item.IsEditing)
                {
                    continue;
                }

                try
                {
                    if (block.Area is ModbusArea.Coil or ModbusArea.DiscreteInput)
                    {
                        if (coils == null || e.Offset >= coils.Length)
                        {
                            e.Item.ReadError = "响应长度不足";
                            continue;
                        }

                        e.Item.Value = ValueCodec.FormatCoil(coils[e.Offset]);
                    }
                    else
                    {
                        if (registers == null || e.Offset + e.Units > registers.Length)
                        {
                            e.Item.ReadError = "响应长度不足";
                            continue;
                        }

                        ReadOnlySpan<ushort> slice = registers.AsSpan(e.Offset, e.Units);
                        e.Item.Value = ValueCodec.FormatRegisters(
                            e.Item.Model.Type,
                            slice,
                            e.Item.Model.Format,
                            driver.Int32ByteOrder,
                            driver.StringByteOrder,
                            e.Item.Model.Length);
                    }

                    e.Item.ReadError = string.Empty;
                }
                catch (Exception ex)
                {
                    e.Item.ReadError = ex.Message;
                }
            }
        }

        /// <summary>单块读失败：把原因写到该块内每一项（不触发断线）。</summary>
        public static void MarkBlockFailed(GlobalVarReadBlock block, string reason)
        {
            foreach (GlobalVarReadEntry e in block.Entries)
            {
                e.Item.ReadError = reason;
            }
        }

        private void Rebuild()
        {
            _items.Clear();
            foreach (GlobalVariable v in _set.Items)
            {
                var item = new GlobalVarItem(v) { Value = v.Value };
                _items.Add(item);
            }

            Reindex();
        }

        private void Reindex()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                _items[i].Id = i + 1;
            }
        }
    }

    /// <summary>一次读请求的规划（全局变量版）。</summary>
    public sealed class GlobalVarReadBlock
    {
        public ModbusArea Area { get; init; }

        public int StartAddress { get; init; }

        public int Count { get; init; }

        public List<GlobalVarReadEntry> Entries { get; init; } = new();
    }

    /// <summary>块内某一变量：相对块起始地址的偏移与自身占用长度。</summary>
    public sealed class GlobalVarReadEntry
    {
        public GlobalVarItem Item { get; init; } = null!;

        public int Offset { get; init; }

        public int Units { get; init; }
    }
}
