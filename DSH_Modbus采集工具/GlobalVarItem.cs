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
    /// 界面需要「解析错误」这类随校验变化、要通知绑定的属性。
    /// </para>
    /// </summary>
    public sealed class GlobalVarItem : INotifyPropertyChanged
    {
        private string _resolveError = string.Empty;

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


        /// <summary>
        /// 类型显示名（如 <c>STRING[9]</c>）—— <c>MonitorTypeCombo</c> 的闭合态模板
        /// 直接绑数据上下文的这个属性，缺了它下拉框会显示成空白。
        /// </summary>
        public string TypeDisplayName => Model.TypeDisplayName;


        /// <summary>类型变更后通知下拉框闭合态重读显示名。</summary>
        public void RaiseTypeChanged() => Raise(nameof(TypeDisplayName));

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// 全局变量集合的界面侧编排：增删改、校验、落盘。
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

        /// <summary>
        /// 按名称查找变量定义（大小写敏感 —— C 标识符语义）。
        /// <para>
        /// 自由监控用它判断「输入框里打的是不是一个已定义的变量」，
        /// 故返回的是**模型**而非界面包装：那边只需要类型 / 地址这些定义字段。
        /// </para>
        /// </summary>
        public GlobalVariable? FindByName(string name) => _set.FindByName(name);

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
        public void ResolveOnly(GlobalVarItem item) => ResolveOnly(item, out _);

        /// <summary>
        /// 同上，并返回失败**类别** —— 界面据此决定弹哪句提示。
        /// </summary>
        public bool ResolveOnly(GlobalVarItem item, out AddressErrorKind kind)
        {
            bool ok = _set.ResolveOnly(item.Model, out kind);
            item.ResolveError = item.Model.ResolveError;
            return ok;
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

        private void Rebuild()
        {
            _items.Clear();
            foreach (GlobalVariable v in _set.Items)
            {
                _items.Add(new GlobalVarItem(v));
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

}
