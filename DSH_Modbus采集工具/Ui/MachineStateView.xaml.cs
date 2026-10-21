using System.Windows;
using System.Windows.Controls;
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

        public MachineStateView()
        {
            InitializeComponent();
        }

        /// <summary>双击某个状态框时触发，参数是该状态。</summary>
        public event EventHandler<MachineStateItem>? StateDoubleClicked;

        /// <summary>用一组状态重建整个画面。</summary>
        public void Bind(IReadOnlyList<MachineStateItem> items)
        {
            RootCanvas.Children.Clear();
            _boxes.Clear();

            RootCanvas.Width = MachineStateLayout.DesignWidth;
            RootCanvas.Height = MachineStateLayout.DesignHeight;

            BuildContainer();
            BuildEdges();
            BuildEdgeLabels();
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
        /// 连线标签：**压在线上**，用卡片底色把线盖掉一段，与 drawio 的观感一致。
        /// <para>
        /// 之所以不用 <c>Canvas.Left</c> 直接居中：标签宽度要等测量完才知道，
        /// 于是用 <see cref="RenderTransformOrigin"/> + 半宽半高的平移变换来居中自己。
        /// </para>
        /// </summary>
        private void BuildEdgeLabels()
        {
            foreach (MachineStateEdge edge in MachineStateLayout.Edges)
            {
                var label = new TextBlock
                {
                    Text = edge.LabelLines == 2 ? BreakLabel(edge.Label) : edge.Label,
                    FontSize = EdgeLabelFontSize,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.NoWrap,
                    Padding = new Thickness(2, 0, 2, 0),
                    IsHitTestVisible = false,
                };

                label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.MachineState.Label");
                label.SetResourceReference(TextBlock.BackgroundProperty, "Brush.Card.Background");

                var offset = new TranslateTransform();
                label.RenderTransform = offset;
                label.SizeChanged += (_, e) =>
                {
                    offset.X = -e.NewSize.Width / 2;
                    offset.Y = -e.NewSize.Height / 2;
                };

                Canvas.SetLeft(label, edge.LabelPoint.X);
                Canvas.SetTop(label, edge.LabelPoint.Y);
                RootCanvas.Children.Add(label);
            }
        }

        /// <summary>
        /// 把标着「折两行」的标签按空格断开。
        /// <para>
        /// ⚠️ <c>Reset Done</c> 折成「Reset / Done」而不是按宽度自动换行 ——
        /// 自动换行会在缩放到某个比例时突然变一行，图会「跳」。
        /// </para>
        /// </summary>
        private static string BreakLabel(string text)
        {
            int space = text.IndexOf(' ');
            return space <= 0 || space == text.Length - 1
                ? text
                : text[..space] + "\n" + text[(space + 1)..];
        }

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
