using System.Windows;

namespace DSH_Modbus采集工具.Ui;

/// <summary>
/// 「这个框里的内容有错」的标记，只供样式里的 <c>DataTrigger</c> 读取（2026-11-04 加）。
///
/// <para>
/// 原来扫码器每行把「还缺什么」写在展开块顶部一行红字里，用户要求改成
/// **哪个格子填错就哪个格子变红**（"去掉红色的提示文字，相关配置错误给输入框标红即可"）。
/// 各字段的 bool 在 <c>ScannerItem</c> 上互不相同（<c>IpError</c> / <c>PortError</c> …），
/// 共享样式没法直接绑其中某一个，于是让它读这个附加属性：XAML 里逐个框写
/// <c>ui:ScannerErrorMark.IsError="{Binding IpError}"</c>，样式只写一份触发器。
/// </para>
///
/// <para>
/// ⚠️ 不要改用 <see cref="FrameworkElement.Tag"/> 传这个标志：<c>Tag</c> 是 <c>object</c>，
/// <c>DataTrigger.Value="True"</c> 会保持字符串、与装箱的 <c>bool</c> 比不相等，
/// 触发器永远不亮（这种错表现成「改了代码毫无反应」，很难查）。
/// 声明成 <c>bool</c> 的附加属性才有类型转换。
/// </para>
/// </summary>
public static class ScannerErrorMark
{
    public static readonly DependencyProperty IsErrorProperty =
        DependencyProperty.RegisterAttached(
            "IsError",
            typeof(bool),
            typeof(ScannerErrorMark),
            new FrameworkPropertyMetadata(false));

    public static bool GetIsError(DependencyObject element)
    {
        return (bool)element.GetValue(IsErrorProperty);
    }

    public static void SetIsError(DependencyObject element, bool value)
    {
        element.SetValue(IsErrorProperty, value);
    }
}
