using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DSH_Modbus采集工具.Modeling;

namespace DSH_Modbus采集工具.Ui
{
    /// <summary>
    /// 给 <see cref="TextBox"/> 挂一个「像 IDE 那样」的变量名补全下拉（P7.1）。
    /// <para>
    /// 候选**怎么算**在 <see cref="CompletionSource"/>（纯逻辑、可离线断言）；
    /// 本类只管**怎么显示与按键**。分开是因为前者要能被独立工程链接源码测，
    /// 后者只能靠 UIA 端到端测 —— 混在一起会逼着测试去启动窗口。
    /// </para>
    /// <para>
    /// 三处刻意设计，都是被现场需求逼出来的（详见 <c>Design.md</c> ADR-33）：
    /// </para>
    /// <list type="number">
    /// <item>**弹窗不抢焦点**（<c>WS_EX_NOACTIVATE</c>）：否则鼠标点候选时文本框先失焦，
    /// 触发「失焦即校验」弹出「不是有效软元件或变量」，而此刻用户其实什么都没做错。</item>
    /// <item>**回车 / Esc 在列表打开时被吃掉**：文本框所在的位置回车 = 提交、Esc = 关窗，
    /// 这两个键必须优先归列表。</item>
    /// <item>**组字期间不弹**：中文输入法拼音未上屏时弹补全，会和输入法候选条打架。</item>
    /// </list>
    /// </summary>
    public sealed class CompletionPopup : IDisposable
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private readonly TextBox _box;
        private readonly Func<IReadOnlyList<CompletionCandidate>> _source;
        private readonly Popup _popup;
        private readonly Border _border;
        private readonly ListBox _list;

        private List<CompletionCandidate> _items = new();
        private int _wordStart;
        private int _wordLength;

        /// <summary>插入候选时压住 <c>TextChanged</c>，避免「插入即重新弹一遍」。</summary>
        private bool _suppress;

        /// <summary>正在用输入法组字（拼音未上屏）。</summary>
        private bool _composing;

        private CompletionPopup(TextBox box, Func<IReadOnlyList<CompletionCandidate>> source)
        {
            _box = box;
            _source = source;

            _list = new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Focusable = false,
                MaxHeight = 220,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ItemContainerStyle = BuildItemStyle(),
            };

            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Auto);

            _border = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(0, 3, 0, 3),
                Child = _list,
            };

            _popup = new Popup
            {
                PlacementTarget = box,
                Placement = PlacementMode.Bottom,
                VerticalOffset = 2,
                StaysOpen = true,
                AllowsTransparency = true,
                Focusable = false,
                PopupAnimation = PopupAnimation.None,
                Child = _border,
            };

            _popup.Opened += Popup_Opened;

            box.TextChanged += Box_TextChanged;
            box.PreviewKeyDown += Box_PreviewKeyDown;
            box.LostFocus += Box_LostFocus;
            box.Unloaded += Box_Unloaded;
            TextCompositionManager.AddPreviewTextInputStartHandler(box, Box_TextInputStart);
            TextCompositionManager.AddPreviewTextInputUpdateHandler(box, Box_TextInputUpdate);

            _list.PreviewMouseLeftButtonDown += List_PreviewMouseLeftButtonDown;
            _list.PreviewMouseMove += List_PreviewMouseMove;
        }

        /// <summary>
        /// 给一个文本框挂上补全。候选由 <paramref name="source"/> 现算（变量表随时可变，
        /// 不能在挂载时抓一份快照）。
        /// </summary>
        public static CompletionPopup Attach(TextBox box, Func<IReadOnlyList<CompletionCandidate>> source)
        {
            // ⚠️ 关掉输入法（实测踩过，见 Design.md §10.17）：
            // 这两处输入框只接受 ASCII（变量名是 C 标识符，表达式只有标识符/数字/运算符），
            // 中文输入法在这里**没有任何用处**，却会带来三个实际问题：
            //   ① 输入法候选窗正好盖在补全弹窗的位置上（都在光标下方）；
            //   ② 回车被输入法先吃掉，补全的「回车确认」永远收不到；
            //   ③ 每次按键都算一次「组字」，补全要么不弹、要么乱弹。
            // 关掉之后，键盘输入直达文本框，补全的键位语义才是确定的。
            InputMethod.SetIsInputMethodEnabled(box, false);

            return new CompletionPopup(box, source);
        }

        /// <summary>列表当前是否展开（供宿主判断「这次失焦该不该校验」）。</summary>
        public bool IsOpen => _popup.IsOpen;

        public void Dispose()
        {
            _box.TextChanged -= Box_TextChanged;
            _box.PreviewKeyDown -= Box_PreviewKeyDown;
            _box.LostFocus -= Box_LostFocus;
            _box.Unloaded -= Box_Unloaded;
            TextCompositionManager.RemovePreviewTextInputStartHandler(_box, Box_TextInputStart);
            TextCompositionManager.RemovePreviewTextInputUpdateHandler(_box, Box_TextInputUpdate);

            _list.PreviewMouseLeftButtonDown -= List_PreviewMouseLeftButtonDown;
            _list.PreviewMouseMove -= List_PreviewMouseMove;

            _popup.Opened -= Popup_Opened;
            _popup.IsOpen = false;
        }

        // ---------- 显示 ----------

        private void Popup_Opened(object? sender, EventArgs e)
        {
            // 让弹窗**不激活**：点它不夺焦点，文本框就不会失焦，
            // 于是「失焦即校验」不会被鼠标点候选这种正常操作误触发。
            if (PresentationSource.FromVisual(_border) is HwndSource source && source.Handle != IntPtr.Zero)
            {
                int style = GetWindowLong(source.Handle, GWL_EXSTYLE);
                SetWindowLong(source.Handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            }
        }

        private void Refresh(bool allowEmptyWord)
        {
            if (_suppress)
            {
                return;
            }

            // 组字中（拼音还没上屏）不弹：否则候选框和输入法候选条打架。
            // 正常打字时 TextInputStart 也会把这个标志置真，但紧接着 TextChanged
            // 就会清掉 —— 所以这里挡住的只有真正的组字过程。
            if (_composing)
            {
                Hide();
                return;
            }

            string text = _box.Text;
            int caret = _box.CaretIndex;

            if (!allowEmptyWord && !CompletionSource.TryGetWord(text, caret, out _, out _))
            {
                Hide();
                return;
            }

            string word;
            int start;
            if (CompletionSource.TryGetWord(text, caret, out start, out string found))
            {
                word = found;
            }
            else
            {
                // Ctrl+Space：光标处没有词也要能把整张表列出来
                word = string.Empty;
                start = caret;
            }

            if (word.Length == 0 && !allowEmptyWord)
            {
                Hide();
                return;
            }

            List<CompletionCandidate> candidates = CompletionSource.Filter(_source(), word);
            if (candidates.Count == 0)
            {
                Hide();
                return;
            }

            _items = candidates;
            _wordStart = start;
            _wordLength = word.Length;
            Show();
        }

        private void Show()
        {
            ApplyTheme();

            _list.Items.Clear();
            foreach (CompletionCandidate c in _items)
            {
                var item = new ListBoxItem
                {
                    Content = BuildRow(c),
                    Focusable = false,
                };
                _list.Items.Add(item);
            }

            _list.SelectedIndex = 0;

            // 宽度跟随输入框，但给一个下限：太窄的话「名字 + 说明」挤成一团
            _border.Width = Math.Max(_box.ActualWidth, 240);
            _popup.IsOpen = true;
        }

        private void Hide()
        {
            _popup.IsOpen = false;
            _items = new List<CompletionCandidate>();
        }

        private void ApplyTheme()
        {
            // 用 SetResourceReference 而不是取值赋死：候选窗虽然短命（每次弹出新建），
            // 但用户完全可能「列表开着的时候切主题」，那时一次性取到的画刷就成了旧色。
            _border.SetResourceReference(Border.BackgroundProperty, "Brush.Popup.Background");
            _border.SetResourceReference(Border.BorderBrushProperty, "Brush.Popup.Border");
        }

        private FrameworkElement BuildRow(CompletionCandidate candidate)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            var name = new TextBlock
            {
                Text = candidate.Text,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Primary");

            left.Children.Add(name);

            if (!string.IsNullOrEmpty(candidate.Detail))
            {
                var detail = new TextBlock
                {
                    Text = candidate.Detail,
                    FontSize = 11,
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                detail.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Muted");

                left.Children.Add(detail);
            }

            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            var kind = new TextBlock
            {
                Text = candidate.Kind,
                FontSize = 10,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            kind.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Tertiary");

            Grid.SetColumn(kind, 1);
            grid.Children.Add(kind);

            return grid;
        }

        private Style BuildItemStyle()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            border.AppendChild(presenter);

            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(UIElement.FocusableProperty, false));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border }));

            var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Brush.Accent.Subtle")));
            style.Triggers.Add(selected);

            return style;
        }

        // ---------- 键盘 ----------

        private void Box_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrlSpace = e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control;

            if (!_popup.IsOpen)
            {
                if (ctrlSpace)
                {
                    Refresh(allowEmptyWord: true);
                    e.Handled = true;
                }

                return;
            }

            switch (e.Key)
            {
                case Key.Down:
                    MoveSelection(+1);
                    e.Handled = true;
                    break;

                case Key.Up:
                    MoveSelection(-1);
                    e.Handled = true;
                    break;

                case Key.Enter:
                case Key.Tab:
                    AcceptSelected();
                    e.Handled = true;
                    break;

                case Key.Escape:
                    // 只关列表：宿主窗口的 Esc（取消/关窗）不该被这次按键触发
                    Hide();
                    e.Handled = true;
                    break;

                case Key.Space when ctrlSpace:
                    Refresh(allowEmptyWord: true);
                    e.Handled = true;
                    break;
            }
        }

        private void MoveSelection(int delta)
        {
            if (_items.Count == 0)
            {
                return;
            }

            int index = _list.SelectedIndex + delta;
            if (index < 0)
            {
                index = _items.Count - 1;
            }
            else if (index >= _items.Count)
            {
                index = 0;
            }

            _list.SelectedIndex = index;
            _list.ScrollIntoView(_list.SelectedItem);
        }

        private void AcceptSelected()
        {
            int index = _list.SelectedIndex;
            if (index >= 0 && index < _items.Count)
            {
                Accept(_items[index]);
            }
        }

        private void Accept(CompletionCandidate candidate)
        {
            string text = _box.Text;
            int start = Math.Min(_wordStart, text.Length);
            int length = Math.Min(_wordLength, text.Length - start);

            _suppress = true;
            try
            {
                _box.Text = text.Substring(0, start) + candidate.Text + text.Substring(start + length);
                _box.CaretIndex = start + candidate.Text.Length;
            }
            finally
            {
                _suppress = false;
            }

            Hide();
            _box.Focus();
        }

        // ---------- 鼠标 ----------

        private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (ItemAt(e.OriginalSource) is ListBoxItem item)
            {
                int index = _list.Items.IndexOf(item);
                if (index >= 0 && index < _items.Count)
                {
                    e.Handled = true;
                    Accept(_items[index]);
                }
            }
        }

        private void List_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (ItemAt(e.OriginalSource) is ListBoxItem item)
            {
                _list.SelectedItem = item;
            }
        }

        private ListBoxItem? ItemAt(object? originalSource)
        {
            if (originalSource is not DependencyObject source)
            {
                return null;
            }

            return ItemsControl.ContainerFromElement(_list, source) as ListBoxItem;
        }

        // ---------- 文本变化 / 输入法 ----------

        private void Box_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppress)
            {
                return;
            }

            // ⚠️ 这里**必须无条件清掉** _composing（实测踩过，见 Design.md §10.17）：
            // 组字期间文本不落定、TextChanged 不触发；一旦触发，就说明字已经上屏了。
            // 早先写成「_composing 为真就跳过」，结果是第一次按键把标志置真、
            // 之后再没人清 —— 补全从此彻底不再弹出，而界面毫无异常（最难查的一种）。
            _composing = false;

            Refresh(allowEmptyWord: false);
        }

        private void Box_TextInputStart(object sender, TextCompositionEventArgs e)
        {
            if (e.TextComposition is { } composition && !string.IsNullOrEmpty(composition.CompositionText))
            {
                _composing = true;
                Hide();
            }
        }

        private void Box_TextInputUpdate(object sender, TextCompositionEventArgs e)
        {
            if (e.TextComposition is { } composition && string.IsNullOrEmpty(composition.CompositionText))
            {
                _composing = false;
            }
        }

        private void Box_LostFocus(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void Box_Unloaded(object sender, RoutedEventArgs e)
        {
            Hide();
        }
    }
}
