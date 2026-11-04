using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DSH_Modbus采集工具.Modeling;
using DSH_Modbus采集工具.Ui;

// ============================================================
// 总览页「数据采集」视图 →「实时数据」子页签（P1.4）
//
//   固定 1280×720 画布 + 背景图 + 每个数据项一张可拖动的小卡片。
//   卡片读 GlobalVariable.Numeric（十进制）而不是 Value（那可能是 Hex/Bin）。
//
//   ⚠️ 卡片**直接放在 Canvas 里**，不套 ItemsControl —— 拖动时要 reparent，
//      而 ItemsControl 生成的那层 ContentPresenter 会让「摘父级」失败
//      （2026-10-31 在全局变量表踩过一次，直接崩）。
// ============================================================

namespace DSH_Modbus采集工具
{
    public partial class MainWindow
    {
        /// <summary>画布上每张卡片的运行时控件与状态。</summary>
        private sealed class LiveCard
        {
            public required AcquisitionItem Item { get; init; }
            public required Border Root { get; init; }
            public required TextBlock NameText { get; init; }
            public required TextBlock ValueText { get; init; }

            /// <summary>数值外层的矩形框（跟着比例缩放它的内距）。</summary>
            public required Border ValueFrame { get; init; }

            /// <summary>是否正在被拖动（决定了 mouseup 时要不要落盘）。</summary>
            public bool Dragging { get; set; }

            /// <summary>按下时鼠标相对卡片左上角的偏移（DIP）。</summary>
            public Point GrabOffset { get; set; }

            /// <summary>按下时的屏幕/画布位置，用于判断是否超过拖动阈值。</summary>
            public Point PressPoint { get; set; }

            /// <summary>是否已经越过阈值、真的开始拖了（区分「点一下」和「拖动」）。</summary>
            public bool DragStarted { get; set; }
        }

        /// <summary>当前实时画布上的卡片（按添加顺序）。</summary>
        private readonly List<LiveCard> _liveCards = new();

        /// <summary>正在被拖动的卡片；null = 没在拖。</summary>
        private LiveCard? _liveDragging;

        /// <summary>
        /// 「实时数据」子页签此刻是否可见（决定要不要每拍刷数值）。
        /// <para>三个条件缺一不可：总览页可见、当前视图是「数据采集」、子页签停在「实时数据」。</para>
        /// </summary>
        private bool IsAcqLiveVisible =>
            _overviewPageActive
            && IsAcquisitionViewSelected
            && AcqLiveSegment?.IsChecked == true
            && !_windowClosed;

        /// <summary>卡片拖动阈值（DIP）。小于它就当「点一下」，不算拖动。</summary>
        /// <remarks>
        /// 不设阈值的话，手一抖就会把卡片挪偏，而用户以为自己只是点了一下。
        /// 3 DIP 在 150% DPI 下约 4.5 物理像素，比一次自然抖动略大。
        /// </remarks>
        private const double LiveDragThreshold = 3;

        /// <summary>卡片距画布边缘的最小留白，拖动时不让它整个跑出去。</summary>
        private const double LiveEdgePadding = 2;

        /// <summary>
        /// 卡片的**设计基准内距/尺寸**（逻辑值）。显示时这些都要乘上缩放比。
        /// <para>
        /// 2026-11-03 用户定案：**卡片整体跟随图片比例缩放**（改前只有位置跟着缩，
        /// 字号固定，于是窗口一小卡片就会互相压住）。现在字号、内距、边角都按比例走。
        /// </para>
        /// </summary>
        private const double LiveCardPadLeft = 8;

        /// <summary>卡片内距上（逻辑值，见 <see cref="LiveCardPadLeft"/>）。</summary>
        private const double LiveCardPadTop = 5;

        /// <summary>卡片内距右。</summary>
        private const double LiveCardPadRight = 8;

        /// <summary>卡片内距下。</summary>
        private const double LiveCardPadBottom = 6;

        /// <summary>卡片圆角（逻辑值）。</summary>
        private const double LiveCardCornerRadius = 6;

        /// <summary>数值框的内距（逻辑值，横向）。</summary>
        private const double LiveValueFramePadX = 6;

        /// <summary>数值框的内距（逻辑值，纵向）。</summary>
        private const double LiveValueFramePadY = 1;

        /// <summary>数值框与名称之间的间距（逻辑值）。</summary>
        private const double LiveValueFrameGap = 3;

        /// <summary>数值框圆角（逻辑值）。</summary>
        private const double LiveValueFrameCornerRadius = 4;

        /// <summary>卡片最小宽度（逻辑值）。</summary>
        private const double LiveCardMinWidth = 70;

        /// <summary>
        /// 卡片缩放的**下限**。设为 0 = **严格跟随比例**（2026-11-03 用户定案）。
        /// <para>
        /// 曾经设成 3.5 / 5 兜「字太小看不清」，但那会让**名称与数值的比例失真**
        /// （当时名称 12 / 数值 24，触底后变成 5:14.4），而用户要的是
        /// 「卡片和图片一起缩」的准确关系。
        /// </para>
        /// <para>
        /// 保留这个常量是为了将来需要兜底时改一处即可；当前为 0，等于不生效。
        /// 真要兜底，**两个字号必须用同一个下限策略**，否则比例会歪。
        /// </para>
        /// </summary>
        private const double LiveMinReadableFontSize = 0;

        /// <summary>
        /// 当前显示缩放比：**逻辑坐标 1280×720 → 屏幕 DIP** 的倍率，X/Y 分开存。
        /// <para>
        /// 2026-11-02 用户定案：图要缩到「能完全显示整张图的最大比例」且**不要滚动条**。
        /// </para>
        /// <para>
        /// ⚠️ 为什么是两个比例而不是一个：画布尺寸**跟着图片长宽比走**（4:3 的图就是 4:3 的画布），
        /// 而逻辑坐标空间固定是 1280×720。把一个 16:9 的逻辑空间铺到 4:3 的屏幕上，
        /// X/Y 的倍率必然不同 —— 用统一倍率就只能留黑边或裁掉图。
        /// </para>
        /// <para>
        /// ⚠️ **卡片的逻辑坐标永远是 1280×720 那一套**，不随窗口变 ——
        /// 存盘里、选项框里看到的都是逻辑坐标，换台机器换个窗口大小，同一个数字
        /// 仍落在图上的同一点。只有「显示」时才乘比例。
        /// </para>
        /// </summary>
        private double _liveScaleX = 1.0;

        /// <summary>Y 方向显示缩放比（见 <see cref="_liveScaleX"/>）。</summary>
        private double _liveScaleY = 1.0;

        /// <summary>
        /// 当前背景图的长宽比（宽/高）。没配图时按 16:9 —— 即逻辑空间的默认比例。
        /// 配了图就取图片真实比例，图片按它定尺寸，图才能**不变形**。
        /// </summary>
        private double _liveImageAspect =
            (double)AcquisitionTaskRules.LiveCanvasWidth / AcquisitionTaskRules.LiveCanvasHeight;

        /// <summary>
        /// 图片在画布内的左上角偏移（显示 DIP）。
        /// <para>
        /// 2026-11-02 用户定案：**卡片可以拖到图片外的留白区**，只要还在父卡片范围内。
        /// 所以画布撑满整个父卡片，图片在其中居中 —— 四周露出的就是可放卡片的留白。
        /// 卡片坐标以**图片左上角**为原点，故换算出屏幕位置时要加这个偏移。
        /// </para>
        /// </summary>
        private double _liveImageOffsetX;

        /// <summary>图片在画布内的上边偏移（显示 DIP），见 <see cref="_liveImageOffsetX"/>。</summary>
        private double _liveImageOffsetY;

        // ---------- 布局与缩放 ----------

        /// <summary>面板尺寸变了：重算缩放比并重排（含背景图尺寸）。</summary>
        private void AcqLivePanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyAcqLiveLayout();
        }

        /// <summary>
        /// 把画布撑满父卡片、图片在其中按真实比例居中缩放，并重排卡片。
        /// <para>
        /// **画布 = 整个父卡片**（不再等于图片）—— 这就是「卡片能移到图片外、但不出父卡片」的实现方式：
        /// 画布边界天然就是父卡片边界，卡片夹在画布里即可。
        /// </para>
        /// <para>
        /// 图片按**真实长宽比**缩到能完整显示的最大尺寸（宽或高贴边，另一维按比例居中），
        /// 所以既不变形、也不出滚动条。
        /// </para>
        /// </summary>
        private void ApplyAcqLiveLayout()
        {
            if (AcqLiveCanvas == null || AcqLivePanel == null)
            {
                return;
            }

            double availableW = AcqLivePanel.ActualWidth;
            double availableH = AcqLivePanel.ActualHeight;

            // 还没布局出尺寸（启动首帧 / 面板隐藏时是 0）：保持上一次的比例，别把画布压成 0
            if (availableW <= 1 || availableH <= 1)
            {
                return;
            }

            // 画布撑满父卡片（靠 Stretch 自动填满，**不要**设 Width/Height ——
            // 设了反而把它固定成显式尺寸，且与 Stretch 并存时布局行为会变得难以预测）
            // 卡片的活动范围就是它

            double aspect = _liveImageAspect;
            if (aspect <= 0 || double.IsNaN(aspect) || double.IsInfinity(aspect))
            {
                aspect = (double)AcquisitionTaskRules.LiveCanvasWidth / AcquisitionTaskRules.LiveCanvasHeight;
            }

            // 图片在画布内能占的最大矩形（保持长宽比）
            double displayW;
            double displayH;

            if (availableW / availableH >= aspect)
            {
                displayH = availableH;
                displayW = availableH * aspect;
            }
            else
            {
                displayW = availableW;
                displayH = availableW / aspect;
            }

            // 居中：两侧/上下露出的部分就是可放卡片的留白
            _liveImageOffsetX = (availableW - displayW) / 2;
            _liveImageOffsetY = (availableH - displayH) / 2;

            // 逻辑空间 1280×720 映射到**图片**这个矩形上
            _liveScaleX = displayW / AcquisitionTaskRules.LiveCanvasWidth;
            _liveScaleY = displayH / AcquisitionTaskRules.LiveCanvasHeight;

            if (_liveScaleX <= 0 || double.IsNaN(_liveScaleX) || double.IsInfinity(_liveScaleX))
            {
                _liveScaleX = 1.0;
            }

            if (_liveScaleY <= 0 || double.IsNaN(_liveScaleY) || double.IsInfinity(_liveScaleY))
            {
                _liveScaleY = 1.0;
            }

            if (AcqLiveBackground != null)
            {
                AcqLiveBackground.Width = displayW;
                AcqLiveBackground.Height = displayH;
                Canvas.SetLeft(AcqLiveBackground, _liveImageOffsetX);
                Canvas.SetTop(AcqLiveBackground, _liveImageOffsetY);
            }

            foreach (LiveCard card in _liveCards)
            {
                // 卡片本身的字号/内距也要跟着比例走（2026-11-03 用户要求），
                // 否则窗口一小，卡片显得越来越大、互相压住
                ApplyLiveCardScale(card);
                PositionLiveCard(card);
            }

        }

        // ---------- 构建 ----------

        /// <summary>
        /// 按当前任务组重建实时画布（切组、增删数据项、改字号、换图后都调它）。
        /// </summary>
        private void RebuildAcqLiveCanvas()
        {
            if (AcqLiveCanvas == null)
            {
                return;
            }

            // 清空旧卡片（背景图是 Canvas 的第 0 个子元素，要留着）
            foreach (LiveCard card in _liveCards)
            {
                AcqLiveCanvas.Children.Remove(card.Root);
            }

            _liveCards.Clear();
            _liveDragging = null;

            AcquisitionTask? task = FindAcqTaskByName(_acqHistoryGroup);
            SyncAcqLiveBackground(task);

            if (task == null)
            {
                // ⚠️ 没选中组时必须**不显示**这里的提示（2026-11-03 修）：
                // 一个可用组都没有时，RefreshAcqGroupCombo 已经在中间显示了
                // 「没有可用的任务组（需启用且无配置错误）」；这里再画一句
                // 「该任务组不存在」会**叠在同一位置**，看成乱码（实测踩到）。
                // 只有「组名非空但对不上」才是真异常，那时才说话。
                if (!string.IsNullOrEmpty(_acqHistoryGroup))
                {
                    ShowAcqLiveHint("该任务组不存在");
                    return;
                }

                HideAcqLiveHint();
                return;
            }

            if (task.Items == null || task.Items.Count == 0)
            {
                ShowAcqLiveHint("该任务组还没有数据项");
                return;
            }

            HideAcqLiveHint();

            for (int i = 0; i < task.Items.Count; i++)
            {
                AcquisitionItem item = task.Items[i];

                // 旧 json 载入时已经补过 Id（见 AcquisitionTaskSet.Load）；
                // 这里是兜底 —— 万一有别的路径造出无 Id 的项，也不要让布局错乱
                if (string.IsNullOrEmpty(item.Id))
                {
                    item.Id = AcquisitionTaskSet.NewItemId();
                }

                if (!item.HasLivePosition)
                {
                    // 新项自动摆放：按行铺开，避免全部叠在 (16,16) 上 ——
                    // 叠住会让用户以为「刚加的项没生效」
                    ApplyAutoPosition(item, i);
                }

                LiveCard card = CreateLiveCard(item);
                _liveCards.Add(card);
                AcqLiveCanvas.Children.Add(card.Root);

                PositionLiveCard(card);
                RefreshLiveCardValue(card);
            }

            // 卡片建完后重算一次布局：背景图尺寸与缩放比可能在这一轮才变得可用
            // （首次进入该视图时 panel 的 ActualWidth 要等布局走完）
            ApplyAcqLiveLayout();
        }

        /// <summary>给没摆过的项算一个默认位置（按序号蛇形铺开，**逻辑坐标**）。</summary>
        private void ApplyAutoPosition(AcquisitionItem item, int index)
        {
            int perRow = AcquisitionTaskRules.LiveAutoPlacePerRow;
            int row = index / perRow;
            int col = index % perRow;

            item.LiveX = AcquisitionTaskRules.LiveAutoPlaceStart
                + col * AcquisitionTaskRules.LiveAutoPlaceStepX;
            item.LiveY = AcquisitionTaskRules.LiveAutoPlaceStart
                + row * AcquisitionTaskRules.LiveAutoPlaceStepY;
        }

        /// <summary>
        /// 把**显示坐标**夹进画布（画布 = 父卡片），留出卡片自身尺寸，不让它整个跑出去。
        /// <para>
        /// ⚠️ 参数与返回都是**显示 DIP**，不是逻辑坐标 —— 因为卡片尺寸本来就不缩放，
        /// 和画布的可用空间是同一个坐标系。逻辑坐标要夹取时，先换算成显示坐标再调这里，
        /// 用完再除回逻辑（见 `LiveCard_MouseMove`）。
        /// </para>
        /// <para>
        /// ⚠️ 下限是 <see cref="LiveEdgePadding"/> 而不是 0：**卡片可以盖到图片之外**
        /// （用户定案 2026-11-02），但至少整张卡片要留在父卡片里，不能被拖出去看不见。
        /// </para>
        /// </summary>
        private static double ClampToCanvas(double value, double canvasSize, double controlSize)
        {
            double max = canvasSize - controlSize - LiveEdgePadding;

            if (max < LiveEdgePadding)
            {
                max = LiveEdgePadding;
            }

            if (value < LiveEdgePadding)
            {
                return LiveEdgePadding;
            }

            return value > max ? max : value;
        }

        /// <summary>造一张卡片（纯代码，不走 XAML 模板 —— 拖动要拿到具体控件）。</summary>
        /// <remarks>
        /// 控件上的字号/内距这里只给**初值**，真正的显示尺寸由
        /// <see cref="ApplyLiveCardScale"/> 按当前缩放比写一次 —— 窗口变化时也靠它重刷。
        /// </remarks>
        private LiveCard CreateLiveCard(AcquisitionItem item)
        {
            var nameText = new TextBlock
            {
                Text = AcquisitionLiveText.Title(item),
                FontSize = item.LiveNameFontSize,
                Foreground = Res("Brush.Text.Muted", Colors.Gray),
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            var valueText = new TextBlock
            {
                Text = AcquisitionLiveText.Placeholder,
                FontSize = item.LiveValueFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res("Brush.Text.Primary", Colors.Black),
                // 数值**靠左**对齐（2026-11-03 用户要求）：
                // 卡片可以拖到图片外的留白区、宽度会被内容撑开，居中的话
                // 数值在框里相对位置随卡片宽度浮动，看着不稳
                TextAlignment = TextAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            // 数值外面再套一圈矩形框（2026-11-03 用户要求）：
            // 让「值」和「项名称」在视觉上分开，读数时眼睛有落点 ——
            // 光靠字号差在小窗口下不够（数值缩到 24 号时和名称的差别不明显）。
            // 视觉上是「输入框」的语感（框线 + 白底），但卡片本身是只读的。
            var valueFrame = new Border
            {
                Child = valueText,
                Background = Res("Brush.Input.Background", Colors.White),

                // ⚠️ 框线颜色与父卡片**对调**（2026-11-03 用户要求）：
                // 内框用 Card.Border（浅 #E8E8E8），外框用 Input.Border（深 #C8C8C8）。
                // 层次因此是「外面的卡片框更实、里面的数值框更轻」——
                // 反过来（内深外浅）会让数值框比卡片本体还抢眼，喧宾夺主。
                BorderBrush = Res("Brush.Card.Border", Colors.Gainsboro),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                // 卡片整体要能拖，框不能把鼠标事件吃掉
                IsHitTestVisible = false,
            };

            var stack = new StackPanel
            {
                // 让子元素吃满卡片宽度 —— 数值框才会**统一撑到整卡宽**。
                // 用 StackPanel（纵向）时子元素默认按内容宽度排，框会缩成只有数字那么宽，
                // 多张卡片的框宽就参差不齐。
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            nameText.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(nameText);
            stack.Children.Add(valueFrame);

            var root = new Border
            {
                Child = stack,
                Background = Res("Brush.Card.Background", Colors.White),

                // 与数值框的框线**对调**（见 valueFrame 里的说明）：
                // 外层卡片用 Input.Border（深），内层数值用 Card.Border（浅）
                BorderBrush = Res("Brush.Input.Border", Colors.Silver),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.SizeAll,
                Tag = item,
            };

            var card = new LiveCard
            {
                Item = item,
                Root = root,
                NameText = nameText,
                ValueText = valueText,
                ValueFrame = valueFrame,
            };

            // 字号 / 内距 / 圆角按当前比例写一次
            ApplyLiveCardScale(card);

            // ⚠️ 用 Preview 而不是冒泡事件：卡片里的 TextBlock 虽然不拦截鼠标，
            // 但 Preview 能保证「点在卡片任何位置」都归拖动管
            root.PreviewMouseLeftButtonDown += (_, e) => LiveCard_MouseDown(card, e);
            root.PreviewMouseMove += (_, e) => LiveCard_MouseMove(card, e);
            root.PreviewMouseLeftButtonUp += (_, e) => LiveCard_MouseUp(card, e);
            // Border 没有 MouseDoubleClick 事件（那是 Control 的）—— 双击在 MouseDown 里按 ClickCount 判定

            // 拖到一半被系统打断（Alt+Tab、弹窗抢焦点）时要收尾，
            // 否则 _liveDragging 永远不为 null，之后所有点击都被当成拖动
            root.LostMouseCapture += (_, _) => EndLiveDrag(card, applyPosition: true);

            return card;
        }

        /// <summary>从主题取画刷；取不到用兜底色（这套代码可能被单测跑到，没有 Application）。</summary>
        private static Brush Res(string key, Color fallback)
        {
            if (Application.Current?.TryFindResource(key) is Brush brush)
            {
                return brush;
            }

            return new SolidColorBrush(fallback);
        }

        /// <summary>
        /// 把卡片的字号、内距、圆角按当前缩放比写到控件上（2026-11-03）。
        /// <para>
        /// **用户设定的字号是「逻辑字号」**（当前默认 20 / 20，2026-11-04 起），
        /// 存在模型里永不改；显示时才乘比例。
        /// 这样窗口拉大拉小，卡片和图片的相对大小关系保持不变 —— 图片缩一半，卡片也缩一半。
        /// </para>
        /// <para>
        /// ⚠️ 缩放比取 <c>_liveScaleX</c>（横向）即可：X/Y 两个比例只在「图片长宽比 ≠
        /// 逻辑空间比例」时才会不相等（见字段注释），而**字号必须用同一个数**，
        /// 否则字会被横向或纵向拉扁。
        /// </para>
        /// <para>
        /// ⚠️ 有下限 <see cref="LiveMinReadableFontSize"/>：比例极小时字会先停在下限，
        /// 不会缩到看不见。
        /// </para>
        /// </summary>
        private void ApplyLiveCardScale(LiveCard card)
        {
            double scale = _liveScaleX;

            if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
            {
                scale = 1.0;
            }

            // 字号：各自乘比例。两个字号用**同一个比例**、同一个下限策略，
            // 这样名称:数值 的视觉比例恒定（默认 20:20 时就是一样大，
            // 但用户可以在选项框里改成不同值，比例照样跟着走）。
            double nameFont = ApplyFontFloor(card.Item.LiveNameFontSize * scale);
            double valueFont = ApplyFontFloor(card.Item.LiveValueFontSize * scale);

            card.NameText.FontSize = nameFont;
            card.ValueText.FontSize = valueFont;

            // 内距 / 圆角 / 间距同样按比例；这些没有下限 —— 缩到 0 也无所谓，
            // 真正影响可读性的是字号，那几个数已经兜住了
            card.Root.Padding = new Thickness(
                LiveCardPadLeft * scale,
                LiveCardPadTop * scale,
                LiveCardPadRight * scale,
                LiveCardPadBottom * scale);

            card.Root.CornerRadius = new CornerRadius(LiveCardCornerRadius * scale);
            card.Root.MinWidth = LiveCardMinWidth * scale;

            card.ValueFrame.Margin = new Thickness(0, LiveValueFrameGap * scale, 0, 0);
            card.ValueFrame.Padding = new Thickness(
                LiveValueFramePadX * scale,
                LiveValueFramePadY * scale,
                LiveValueFramePadX * scale,
                LiveValueFramePadY * scale);
            card.ValueFrame.CornerRadius = new CornerRadius(LiveValueFrameCornerRadius * scale);
        }

        /// <summary>
        /// 给字号套下限。当前下限为 0 → 原值返回（严格跟随比例）。
        /// <para>**两个字号共用这一个函数**，保证下限策略一致、比例不失真。</para>
        /// </summary>
        private static double ApplyFontFloor(double fontSize) =>
            LiveMinReadableFontSize <= 0 ? fontSize : Math.Max(fontSize, LiveMinReadableFontSize);

        /// <summary>
        /// 把卡片摆到它该在的位置：**图片偏移 + 逻辑坐标 × 显示比例**。
        /// <para>
        /// ⚠️ 位置只用 `LiveX/LiveY`（逻辑坐标）乘比例，**不要把显示坐标反写回模型**——
        /// 那会把布局搞成越存越小（窗口缩一次，存盘坐标也跟着缩一次）。
        /// </para>
        /// <para>
        /// 卡片的**大小**由 <see cref="ApplyLiveCardScale"/> 负责（同样按比例），
        /// 两个方法在 <see cref="ApplyAcqLiveLayout"/> 里一起调。
        /// </para>
        /// </summary>
        private void PositionLiveCard(LiveCard card)
        {
            Canvas.SetLeft(card.Root, _liveImageOffsetX + card.Item.LiveX.GetValueOrDefault() * _liveScaleX);
            Canvas.SetTop(card.Root, _liveImageOffsetY + card.Item.LiveY.GetValueOrDefault() * _liveScaleY);
        }

        // ---------- 拖动 ----------


        private void LiveCard_MouseDown(LiveCard card, MouseButtonEventArgs e)
        {
            // 双击：Border 没有 MouseDoubleClick 事件（那是 Control 的），
            // 只能在这里按 ClickCount 判。双击不进入拖动状态机
            if (e.ClickCount > 1)
            {
                LiveCard_DoubleClick(card, e);
                return;
            }

            Point p = e.GetPosition(AcqLiveCanvas);


            card.PressPoint = p;

            // ⚠️ GrabOffset 用**显示坐标**算（p 与 Canvas.GetLeft 都在显示空间里），
            // 这样「按住卡片哪一点、拖动时那一点就贴着鼠标」，不会随缩放比漂移
            double currentLeft = Canvas.GetLeft(card.Root);
            double currentTop = Canvas.GetTop(card.Root);

            card.GrabOffset = new Point(
                p.X - (double.IsNaN(currentLeft) ? 0 : currentLeft),
                p.Y - (double.IsNaN(currentTop) ? 0 : currentTop));

            card.Dragging = true;
            card.DragStarted = false;

            _liveDragging = card;

            // ⚠️ 必须捕获鼠标：不捕获的话鼠标一快就滑出卡片，
            // 后续 MouseMove 收不到，卡片会「粘」在原地不动
            card.Root.CaptureMouse();

            e.Handled = true;
        }

        private void LiveCard_MouseMove(LiveCard card, MouseEventArgs e)
        {
            if (!card.Dragging || !ReferenceEquals(_liveDragging, card))
            {
                return;
            }

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                // 在窗口外松开时收不到 MouseUp，靠这里兜底收尾
                EndLiveDrag(card, applyPosition: true);
                return;
            }

            Point p = e.GetPosition(AcqLiveCanvas);

            if (!card.DragStarted)
            {
                double dx = Math.Abs(p.X - card.PressPoint.X);
                double dy = Math.Abs(p.Y - card.PressPoint.Y);

                if (dx < LiveDragThreshold && dy < LiveDragThreshold)
                {
                    return;   // 还在阈值内，当「点了一下」
                }

                card.DragStarted = true;
            }

            // 新位置（画布显示坐标）= 鼠标位置 − 抓取偏移
            double screenX = p.X - card.GrabOffset.X;
            double screenY = p.Y - card.GrabOffset.Y;

            // 夹取：以**画布**（= 父卡片）为界，不是以图片为界 ——
            // 这样卡片能移到图片外的留白区，但不会跑出父卡片。
            // 卡片本身不缩放，所以这里全程用显示 DIP，不必换算。
            double w = card.Root.ActualWidth > 0 ? card.Root.ActualWidth : 150;
            double h = card.Root.ActualHeight > 0 ? card.Root.ActualHeight : 50;

            double clampedX = ClampToCanvas(screenX, AcqLiveCanvas.ActualWidth, w);
            double clampedY = ClampToCanvas(screenY, AcqLiveCanvas.ActualHeight, h);

            // ⚠️ 写回模型要换回**逻辑坐标**：先减图片偏移、再除显示比例。
            // 忘了这一步的后果：窗口缩小时拖一下，存的坐标就跟着变小，
            // 越拖越往左上跑，且存盘后永久偏。
            card.Item.LiveX = (clampedX - _liveImageOffsetX) / _liveScaleX;
            card.Item.LiveY = (clampedY - _liveImageOffsetY) / _liveScaleY;

            PositionLiveCard(card);
        }

        private void LiveCard_MouseUp(LiveCard card, MouseButtonEventArgs e)
        {
            if (!card.Dragging)
            {
                return;
            }

            EndLiveDrag(card, applyPosition: true);
            e.Handled = true;
        }

        /// <summary>
        /// 结束拖动。**只有真的拖过**才落盘 ——
        /// 每次 mousemove 都写 json 会把文件写坏，而且 100+ 次/秒完全没必要。
        /// </summary>
        private void EndLiveDrag(LiveCard card, bool applyPosition)
        {
            bool wasDragging = card.Dragging;
            bool moved = card.DragStarted;

            card.Dragging = false;
            card.DragStarted = false;

            if (ReferenceEquals(_liveDragging, card))
            {
                _liveDragging = null;
            }

            if (card.Root.IsMouseCaptured)
            {
                card.Root.ReleaseMouseCapture();
            }

            if (applyPosition && wasDragging && moved)
            {
                _acqTaskSet.Save();
            }
        }

        // ---------- 双击：选项框 ----------

        private void LiveCard_DoubleClick(LiveCard card, MouseButtonEventArgs e)
        {
            // 拖动过就不当双击（用手拖了一下又松开，不该弹窗）
            if (card.DragStarted)
            {
                return;
            }

            e.Handled = true;

            AcquireLiveCardOptionDialog.Ask(
                this, card.Item,
                () =>
                {
                    // 「重置位置」：把卡片挪回自动摆放位。**立即生效并落盘**，
                    // 不等「确定」—— 用户在框里能看见卡片已经回去了才是对的
                    int index = _liveCards.FindIndex(c => ReferenceEquals(c, card));
                    ApplyAutoPosition(card.Item, index < 0 ? 0 : index);
                    PositionLiveCard(card);
                    _acqTaskSet.Save();
                },
                out double nameSize, out double valueSize,
                out double x, out double y);

            card.Item.LiveNameFontSize = nameSize;
            card.Item.LiveValueFontSize = valueSize;

            // X / Y：对话框里填的坐标直接落下（整数）。只在「换了值」时写，
            // 避免把拖动留下的非整数值白白取整一次。
            if (Math.Abs(card.Item.LiveX.GetValueOrDefault() - x) > 0.5
                || Math.Abs(card.Item.LiveY.GetValueOrDefault() - y) > 0.5)
            {
                card.Item.LiveX = x;
                card.Item.LiveY = y;
            }

            // 字号或位置可能变了 → 整体重建最省事（卡片数量不多）
            RebuildAcqLiveCanvas();
            _acqTaskSet.Save();
        }

        // ---------- 数值刷新 ----------

        /// <summary>
        /// 刷新所有卡片的数值文本。由采集定时器每拍调用（跟着连接周期走）。
        /// </summary>
        private void RefreshAcqLiveValues()
        {
            if (_liveCards.Count == 0)
            {
                return;
            }

            foreach (LiveCard card in _liveCards)
            {
                RefreshLiveCardValue(card);
            }
        }

        private void RefreshLiveCardValue(LiveCard card)
        {
            GlobalVariable? variable = card.Item.VariableName.Length == 0
                ? null
                : _globalVarSet.FindByName(card.Item.VariableName);

            card.ValueText.Text = AcquisitionLiveText.Format(variable);
        }

        // ---------- 背景图 ----------

        /// <summary>已经解码好的背景图缓存（相对路径 → 位图）。</summary>
        /// <remarks>
        /// 重建画布（改项名称、切组、切页签都会触发）不必每次重读整张图 ——
        /// 一张现场工位图通常几百 KB 到几 MB，失焦一次读一遍无法接受。
        /// 只缓存**当前**这一张：换组/换图时整块换掉，不占多余内存。
        /// </remarks>
        private (string Path, BitmapSource? Image) _liveImageCache = (string.Empty, null);

        /// <summary>按任务组的 <see cref="AcquisitionTask.LiveImagePath"/> 设置背景图。</summary>
        private void SyncAcqLiveBackground(AcquisitionTask? task)
        {
            if (AcqLiveBackground == null)
            {
                return;
            }

            string relative = task?.LiveImagePath ?? string.Empty;

            if (relative.Length == 0)
            {
                AcqLiveBackground.Source = null;
                AcqLiveBackground.Visibility = Visibility.Collapsed;
                _liveImageCache = (string.Empty, null);

                // 没图时回到逻辑空间的默认比例（16:9）
                _liveImageAspect =
                    (double)AcquisitionTaskRules.LiveCanvasWidth / AcquisitionTaskRules.LiveCanvasHeight;
                ApplyAcqLiveLayout();
                return;
            }

            string full = LiveImageStore.Resolve(AppContext.BaseDirectory, relative);

            try
            {
                if (!File.Exists(full))
                {
                    // 图没了：不要安静地显示一张空白 —— 那看起来像「任务组没数据」
                    AcqLiveBackground.Source = null;
                    AcqLiveBackground.Visibility = Visibility.Collapsed;
                    _liveImageCache = (string.Empty, null);
                    ShowAcqLiveHint("背景图片缺失：" + relative
                        + "\n请到「数据管理 → 数据采集 → 任务选项 → 实时背景图」重新选择");
                    return;
                }

                // 还是上次那张图 → 直接用缓存的位图，不重读硬盘
                if (_liveImageCache.Image != null
                    && string.Equals(_liveImageCache.Path, full, StringComparison.OrdinalIgnoreCase))
                {
                    AcqLiveBackground.Source = _liveImageCache.Image;
                    AcqLiveBackground.Visibility = Visibility.Visible;
                    HideAcqLiveHint();
                    ApplyAcqLiveLayout();
                    return;
                }

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;   // 立刻读进内存，不锁文件
                bitmap.UriSource = new Uri(full, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                _liveImageCache = (full, bitmap);

                // ⚠️ 画布尺寸跟着**图片真实长宽比**走：4:3 的图配 4:3 的画布，
                // 图才会铺满且不变形。若沿用固定 16:9 画布，4:3 图只能上下留黑边
                // （2026-11-02 实测踩到 —— 那时截图里图只占中间一条）。
                if (bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
                {
                    _liveImageAspect = (double)bitmap.PixelWidth / bitmap.PixelHeight;
                }

                // 换图后要把「缺失」提示撤掉，否则图回来了提示还挂着
                HideAcqLiveHint();

                AcqLiveBackground.Source = bitmap;
                AcqLiveBackground.Visibility = Visibility.Visible;

                // 长宽比变了 → 显示尺寸要重算
                ApplyAcqLiveLayout();
            }
            catch (Exception)
            {
                AcqLiveBackground.Source = null;
                AcqLiveBackground.Visibility = Visibility.Collapsed;
                ShowAcqLiveHint("背景图片无法读取：" + relative);
            }
        }

        // ---------- 空状态 ----------

        private void ShowAcqLiveHint(string text)
        {
            if (AcqLiveHint == null)
            {
                return;
            }

            AcqLiveHint.Text = text;
            AcqLiveHint.Visibility = Visibility.Visible;
        }

        private void HideAcqLiveHint()
        {
            if (AcqLiveHint != null)
            {
                AcqLiveHint.Visibility = Visibility.Collapsed;
            }
        }
    }
}
