using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DSH_Modbus采集工具.Modeling;

namespace DSH_Modbus采集工具.Ui
{
    /// <summary>
    /// PLC 状态机画布：把 <see cref="MachineStateLayout"/> 里的固定布局画出来，
    /// 并按每个状态的真假切换配色。
    /// <para>
    /// **布局是死的，只有颜色和文字会变**，所以整套视觉对象只在
    /// <see cref="Bind"/> 时构建一次，之后只做属性更新。
    /// </para>
    /// <para>
    /// ⚠️ 所有坐标都是「设计坐标」，画布尺寸固定、由外层 <c>Viewbox</c> 缩放，
    /// 因此这里不需要任何自适应代码。
    /// </para>
    /// </summary>
    public partial class MachineStateView : UserControl
    {
        /// <summary>连线上的文字字号（设计坐标）。</summary>
        private const double EdgeLabelFontSize = 11;

        /// <summary>连线粗细（设计坐标）。</summary>
        private const double EdgeThickness = 2;

        /// <summary>箭头长度（设计坐标）。</summary>
        private const double ArrowLength = 9;

        private readonly Dictionary<string, BoxVisual> _boxes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, LabelVisual> _labels = new(StringComparer.Ordinal);

        public MachineStateView()
        {
            InitializeComponent();
        }

        /// <summary>双击某个状态框时触发，参数是该状态。</summary>
        public event EventHandler<MachineStateItem>? StateDoubleClicked;

        /// <summary>某条连线的标签被改完（已写回模型），调用方据此落盘。</summary>
        public event EventHandler? EdgeLabelCommitted;

        /// <summary>用一组状态重建整个画面。</summary>
        public void Bind(
            IReadOnlyList<MachineStateItem> items, IReadOnlyList<MachineEdgeLabel> edgeLabels)
        {
            RootCanvas.Children.Clear();
            _boxes.Clear();
            _labels.Clear();

            RootCanvas.Width = MachineStateLayout.DesignWidth;
            RootCanvas.Height = MachineStateLayout.DesignHeight;

            BuildContainer();
            BuildEdges();
            BuildEdgeLabels(edgeLabels);
            BuildBoxes(items);
        }

        /// <summary>状态的真假变了：只更新受影响的框。</summary>
        public void ApplyVisualState(MachineStateItem item)
        {
            if (_boxes.TryGetValue(item.Id, out BoxVisual? visual))
            {
                visual.Apply(item);
            }
        }

        private void BuildContainer()
        {
            DesignRect r = MachineStateLayout.Container;
            var border = new Border
            {
                Width = r.Width,
                Height = r.Height,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false,
            };

            border.SetResourceReference(Border.BackgroundProperty, "Brush.MachineState.Container");
            border.SetResourceReference(
                Border.BorderBrushProperty, "Brush.MachineState.Container.Border");

            Canvas.SetLeft(border, r.X);
            Canvas.SetTop(border, r.Y);
            RootCanvas.Children.Add(border);
        }

        private void BuildEdges()
        {
            foreach (MachineStateEdge edge in MachineStateLayout.Edges)
            {
                IReadOnlyList<DesignPoint> pts = edge.Points;

                var figure = new PathFigure
                {
                    StartPoint = new Point(pts[0].X, pts[0].Y),
                    IsClosed = false,
                    IsFilled = false,
                };

                var segment = new PolyLineSegment();
                for (int i = 1; i < pts.Count; i++)
                {
                    segment.Points.Add(new Point(pts[i].X, pts[i].Y));
                }

                figure.Segments.Add(segment);

                var geometry = new PathGeometry();
                geometry.Figures.Add(figure);

                var path = new Path
                {
                    Data = geometry,
                    StrokeThickness = EdgeThickness,
                    StrokeLineJoin = PenLineJoin.Miter,
                    StrokeStartLineCap = PenLineCap.Flat,
                    StrokeEndLineCap = PenLineCap.Flat,
                };

                path.SetResourceReference(Shape.StrokeProperty, "Brush.MachineState.Line");
                RootCanvas.Children.Add(path);

                // 箭头画在线之后，尖端正好压住线的末端
                AddArrowHead(pts[pts.Count - 2], pts[pts.Count - 1]);

                if (edge.DoubleArrow)
                {
                    AddArrowHead(pts[1], pts[0]);
                }
            }
        }

        private void AddArrowHead(DesignPoint from, DesignPoint to)
        {
            double dx = to.X - from.X;
            double dy = to.Y - from.Y;
            double length = Math.Sqrt((dx * dx) + (dy * dy));
            if (length < 0.001)
            {
                return;
            }

            double ux = dx / length;
            double uy = dy / length;

            // 两条腰与轴线各夹约 23°
            const double Spread = 0.40;
            double cos = Math.Cos(Spread);
            double sin = Math.Sin(Spread);

            var head = new Polygon
            {
                Points = new PointCollection
                {
                    new(to.X, to.Y),
                    new(
                        to.X - (ArrowLength * ((ux * cos) - (uy * sin))),
                        to.Y - (ArrowLength * ((uy * cos) + (ux * sin)))),
                    new(
                        to.X - (ArrowLength * ((ux * cos) + (uy * sin))),
                        to.Y - (ArrowLength * ((uy * cos) - (ux * sin)))),
                },
            };

            head.SetResourceReference(Shape.FillProperty, "Brush.MachineState.Line");
            RootCanvas.Children.Add(head);
        }

        /// <summary>
        /// 连线标签（状态机切换条件，SC）：**压在线上**，用卡片底色把线盖掉一段。
        /// <para>
        /// 双击就地变成输入框：回车（不带 Shift）提交、Shift+回车换行、ESC 取消、失焦提交。
        /// 文字为空时**留一个空框**而不是整块消失 —— 否则那条条件再也点不回来。
        /// </para>
        /// <para>
        /// 之所以不用 <c>Canvas.Left</c> 直接居中：标签宽度要等测量完才知道，
        /// 于是用半宽半高的平移变换来居中自己。
        /// </para>
        /// </summary>
        private void BuildEdgeLabels(IReadOnlyList<MachineEdgeLabel> labels)
        {
            var byId = labels.ToDictionary(x => x.Id, StringComparer.Ordinal);

            foreach (MachineStateEdge edge in MachineStateLayout.Edges)
            {
                if (!byId.TryGetValue(edge.Id, out MachineEdgeLabel? model))
                {
                    continue;
                }

                var text = new TextBlock
                {
                    FontSize = EdgeLabelFontSize,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.NoWrap,
                    IsHitTestVisible = false,
                };

                text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.MachineState.Label");
                text.SetBinding(
                    TextBlock.TextProperty,
                    new Binding(nameof(MachineEdgeLabel.Text)) { Source = model });

                var editor = new TextBox
                {
                    FontSize = EdgeLabelFontSize,
                    MinWidth = 70,
                    Padding = new Thickness(3, 0, 3, 0),
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.NoWrap,
                    Visibility = Visibility.Collapsed,
                    VerticalContentAlignment = VerticalAlignment.Center,
                };

                editor.SetResourceReference(TextBox.BackgroundProperty, "Brush.Card.Background");
                editor.SetResourceReference(TextBox.ForegroundProperty, "Brush.MachineState.Label");
                editor.SetResourceReference(TextBox.BorderBrushProperty, "Brush.Accent");

                var grid = new Grid();
                grid.Children.Add(text);
                grid.Children.Add(editor);

                var border = new Border
                {
                    Padding = new Thickness(3, 1, 3, 1),
                    Child = grid,
                    Cursor = Cursors.Hand,
                    ToolTip = "双击编辑切换条件",
                };

                border.SetResourceReference(Border.BackgroundProperty, "Brush.Card.Background");

                var offset = new TranslateTransform();
                border.RenderTransform = offset;
                border.SizeChanged += (_, e) =>
                {
                    offset.X = -e.NewSize.Width / 2;
                    offset.Y = -e.NewSize.Height / 2;
                };

                Canvas.SetLeft(border, edge.LabelPoint.X);
                Canvas.SetTop(border, edge.LabelPoint.Y);
                RootCanvas.Children.Add(border);

                var visual = new LabelVisual(model, border, text, editor, NotifyEdgeLabelCommitted);
                _labels[edge.Id] = visual;
                visual.Refresh();
            }
        }

        private void NotifyEdgeLabelCommitted() => EdgeLabelCommitted?.Invoke(this, EventArgs.Empty);

        private void BuildBoxes(IReadOnlyList<MachineStateItem> items)
        {
            foreach (MachineStateItem item in items)
            {
                if (!MachineStateLayout.Boxes.TryGetValue(item.Id, out DesignRect rect))
                {
                    continue;
                }

                var text = new TextBlock
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    IsHitTestVisible = false,
                };

                var border = new Border
                {
                    Width = rect.Width,
                    Height = rect.Height,
                    CornerRadius = new CornerRadius(5),
                    BorderThickness = new Thickness(1.5),
                    Child = text,
                    Cursor = Cursors.Hand,
                    ToolTip = "双击编辑该状态",
                    Tag = item,
                };

                border.MouseLeftButtonDown += Border_MouseLeftButtonDown;

                Canvas.SetLeft(border, rect.X);
                Canvas.SetTop(border, rect.Y);
                RootCanvas.Children.Add(border);

                var visual = new BoxVisual(item, border, text);
                _boxes[item.Id] = visual;
                visual.Apply(item);
            }
        }

        private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount < 2 || sender is not Border { Tag: MachineStateItem item })
            {
                return;
            }

            e.Handled = true;
            StateDoubleClicked?.Invoke(this, item);
        }

        /// <summary>
        /// 一条连线标签的视觉对象：平时是只读文字，双击就地变输入框。
        /// </summary>
        private sealed class LabelVisual
        {
            private readonly MachineEdgeLabel _model;
            private readonly Border _border;
            private readonly TextBlock _text;
            private readonly TextBox _editor;
            private readonly Action _onCommitted;
            private bool _editing;

            public LabelVisual(
                MachineEdgeLabel model,
                Border border,
                TextBlock text,
                TextBox editor,
                Action onCommitted)
            {
                _model = model;
                _border = border;
                _text = text;
                _editor = editor;
                _onCommitted = onCommitted;

                _border.MouseLeftButtonDown += Border_MouseLeftButtonDown;
                _editor.PreviewKeyDown += Editor_PreviewKeyDown;
                _editor.LostFocus += Editor_LostFocus;
                _model.PropertyChanged += (_, _) => Refresh();
            }

            /// <summary>空文字时留一个可见的小空框，否则那条连线就再也点不回来了。</summary>
            public void Refresh()
            {
                bool empty = string.IsNullOrWhiteSpace(_model.Text);
                _border.BorderThickness = new Thickness(empty ? 1 : 0);
                _border.MinWidth = empty ? 22 : 0;
                _border.MinHeight = empty ? 16 : 0;

                if (empty)
                {
                    _border.SetResourceReference(
                        Border.BorderBrushProperty, "Brush.MachineState.Inactive.Border");
                }
            }

            private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            {
                if (e.ClickCount < 2)
                {
                    return;
                }

                e.Handled = true;
                BeginEdit();
            }

            private void BeginEdit()
            {
                if (_editing)
                {
                    return;
                }

                _editing = true;
                _editor.Text = _model.Text;
                _text.Visibility = Visibility.Collapsed;
                _editor.Visibility = Visibility.Visible;
                _editor.Focus();
                _editor.SelectAll();
            }

            private void EndEdit(bool commit)
            {
                if (!_editing)
                {
                    return;
                }

                _editing = false;

                if (commit)
                {
                    // 只去掉行尾的空白，中间的空格和换行是用户有意敲的
                    _model.Text = _editor.Text.TrimEnd('\r', '\n');
                }

                _editor.Visibility = Visibility.Collapsed;
                _text.Visibility = Visibility.Visible;
                Refresh();

                if (commit)
                {
                    _onCommitted();
                }
            }

            private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    EndEdit(commit: false);
                    return;
                }

                // 回车提交；Shift+回车留给 TextBox 自己插换行。
                // ⚠️ 主键盘回车是 Key.Return，Key.Enter 是**小键盘**那个 —— 两个都要认，
                // 只判 Key.Enter 的话按主回车毫无反应（实测踩过）。
                if (e.Key is Key.Enter or Key.Return && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
                {
                    e.Handled = true;
                    EndEdit(commit: true);
                }
            }

            private void Editor_LostFocus(object sender, RoutedEventArgs e) =>
                EndEdit(commit: true);
        }

        /// <summary>一个状态框的两件视觉对象，以及「上次画成什么样」的缓存。</summary>
        private sealed class BoxVisual
        {
            private readonly Border _border;
            private readonly TextBlock _text;
            private SolidColorBrush? _activeFill;
            private SolidColorBrush? _activeBorder;
            private string _colorKey = string.Empty;
            private bool? _lastActive;

            public BoxVisual(MachineStateItem item, Border border, TextBlock text)
            {
                Item = item;
                _border = border;
                _text = text;
            }

            public MachineStateItem Item { get; }

            public void Apply(MachineStateItem item)
            {
                // 文字与字体每次都比对赋值：编辑弹窗改完它们，此时真假与颜色都可能没变，
                // 若一并被下面的早退挡掉，画布上就还是旧文字（WPF 同值赋值是空操作，不贵）。
                _text.Text = item.Text;
                _text.FontSize = item.FontSize;
                _text.FontWeight = item.FontStyle is MachineStateFontStyle.Bold
                    or MachineStateFontStyle.BoldItalic
                    ? FontWeights.SemiBold
                    : FontWeights.Normal;
                _text.FontStyle = item.FontStyle is MachineStateFontStyle.Italic
                    or MachineStateFontStyle.BoldItalic
                    ? FontStyles.Italic
                    : FontStyles.Normal;

                bool active = item.IsActive;
                bool colorChanged = !string.Equals(_colorKey, item.Color, StringComparison.Ordinal);

                // 50ms 一拍，绝大多数时候真假和颜色都没变 —— 早退掉，别碰画笔
                if (_lastActive == active && !colorChanged)
                {
                    return;
                }

                _lastActive = active;
                _colorKey = item.Color;

                if (active)
                {
                    _activeFill ??= new SolidColorBrush();
                    _activeBorder ??= new SolidColorBrush();

                    _activeFill.Color = ParseColor(item.Color);
                    _activeBorder.Color = Darken(_activeFill.Color);

                    _border.Background = _activeFill;
                    _border.BorderBrush = _activeBorder;
                    _text.Foreground = TextBrush;
                }
                else
                {
                    // 交给主题：深浅两套各有自己的「浅灰」
                    _border.SetResourceReference(Border.BackgroundProperty, "Brush.MachineState.Inactive");
                    _border.SetResourceReference(
                        Border.BorderBrushProperty, "Brush.MachineState.Inactive.Border");
                    _text.SetResourceReference(
                        TextBlock.ForegroundProperty, "Brush.MachineState.Inactive.Text");
                }

            }

            /// <summary>点亮时框内的字用深色 —— 用户挑的填充色基本都是浅色。</summary>
            private static readonly SolidColorBrush TextBrush =
                new(Color.FromRgb(0x1A, 0x1A, 0x1A));

            private static Color ParseColor(string hex)
            {
                try
                {
                    return (Color)ColorConverter.ConvertFromString(hex);
                }
                catch
                {
                    return Color.FromRgb(0xF8, 0xCE, 0xCC);
                }
            }

            private static Color Darken(Color color, double factor = 0.72) => Color.FromRgb(
                (byte)(color.R * factor),
                (byte)(color.G * factor),
                (byte)(color.B * factor));
        }
    }
}
