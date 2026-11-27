using System;
using System.Collections.Generic;

namespace DSH_Modbus采集工具.Monitoring
{
    /// <summary>报文方向：接收靠左显示、发送靠右显示（P2.4）。</summary>
    public enum TrafficDirection
    {
        /// <summary>从设备/服务端收到。</summary>
        Receive = 0,

        /// <summary>发往设备/服务端。</summary>
        Send = 1,
    }

    /// <summary>监控目标的类型：决定抓哪类原始字节，以及 HEX 是否可用。</summary>
    public enum TrafficTargetKind
    {
        /// <summary>PLC 连接（以太网 MBAP 帧 / 串口 RTU 帧）。</summary>
        Plc = 0,

        /// <summary>一台已建立的扫码器连接（裸 TCP 字节）。</summary>
        Scanner = 1,

        /// <summary>MES HTTP 上报（文本协议，只提供 TEXT）。</summary>
        Http = 2,
    }

    /// <summary>
    /// 「监控目标」下拉框的一项。
    /// <para>
    /// <see cref="Key"/> 是**身份**，用引用相等比较：PLC / HTTP 用 <see cref="TrafficKeys"/> 的
    /// 单例，扫码器用 <c>ScannerDefinition</c> 实例。改名只换 <see cref="DisplayName"/>，
    /// 身份不变，故重连 / 改名不会把正在抓的缓冲串到另一台设备上。
    /// </para>
    /// </summary>
    public sealed class TrafficTarget
    {
        public TrafficTarget(object key, string displayName, TrafficTargetKind kind)
        {
            Key = key;
            DisplayName = displayName;
            Kind = kind;
        }

        public object Key { get; }

        public string DisplayName { get; }

        public TrafficTargetKind Kind { get; }
    }

    /// <summary>PLC 与 HTTP 的单例身份键 —— 与各抓包点共用，保证引用相等成立。</summary>
    public static class TrafficKeys
    {
        public static readonly object Plc = new();

        public static readonly object Http = new();
    }

    /// <summary>一帧被抓住的报文。不可变，可跨线程读写（引用本身只读）。</summary>
    public sealed class TrafficFrame
    {
        internal TrafficFrame(
            long sequence, DateTime timestamp, TrafficDirection direction, byte[] payload, string? caption)
        {
            Sequence = sequence;
            Timestamp = timestamp;
            Direction = direction;
            Payload = payload;
            Caption = caption;
        }

        /// <summary>缓冲内单调递增的序号，供 UI 增量刷新时定位。</summary>
        public long Sequence { get; }

        public DateTime Timestamp { get; }

        public TrafficDirection Direction { get; }

        /// <summary>原始字节：HEX 直接按字节显示，TEXT 按 UTF-8 解码。</summary>
        public byte[] Payload { get; }

        /// <summary>
        /// HTTP 的说明行（如「请求：POST http://…」「响应：200 OK」）。
        /// 二进制帧为 <c>null</c>；TEXT 显示时它单独占第一行。
        /// </summary>
        public string? Caption { get; }
    }

    /// <summary>一次缓冲快照。UI 线程每拍取一次，只拿只读数组，不碰内部锁。</summary>
    public readonly record struct TrafficSnapshot(
        TrafficFrame[] Frames,
        long FirstSequence,
        long LastSequence,
        int Generation);

    /// <summary>
    /// 报文环形缓冲（上限 1000 条，超出丢最老的）。
    /// <para>
    /// 抓包点跑在通讯热路径上（100ms 轮询）、UI 每 200ms 取一次快照，故 <see cref="Add"/>
    /// 与 <see cref="Snapshot"/> 之间必须加锁；锁内只做「丢最老 + 追加 + 拷引用数组」，
    /// 不做格式化、不碰控件。
    /// </para>
    /// <para>
    /// <see cref="Generation"/> 在 <see cref="Clear"/> 时自增：UI 靠它判断「缓冲被清过」，
    /// 不能只比序号 —— 清空后序号仍单调，旧行不会自己消失。
    /// </para>
    /// </summary>
    public sealed class TrafficBuffer
    {
        /// <summary>与运行日志同一口径：只留最近 1000 条。</summary>
        public const int Capacity = 1000;

        private readonly object _sync = new();
        private readonly List<TrafficFrame> _frames = new();
        private long _nextSequence = 1;
        private int _generation;

        public int Count
        {
            get
            {
                lock (_sync)
                {
                    return _frames.Count;
                }
            }
        }

        public int Generation
        {
            get
            {
                lock (_sync)
                {
                    return _generation;
                }
            }
        }

        /// <summary>追加一帧；满 1000 条时丢最老的一条。</summary>
        public TrafficFrame Add(TrafficDirection direction, byte[] payload, string? caption = null)
        {
            DateTime timestamp = DateTime.Now;

            lock (_sync)
            {
                var frame = new TrafficFrame(_nextSequence++, timestamp, direction, payload, caption);
                if (_frames.Count >= Capacity)
                {
                    _frames.RemoveAt(0);
                }

                _frames.Add(frame);
                return frame;
            }
        }

        /// <summary>清空并让 <see cref="Generation"/> 自增；序号继续单调，不清零。</summary>
        public void Clear()
        {
            lock (_sync)
            {
                _frames.Clear();
                _generation++;
            }
        }

        public TrafficSnapshot Snapshot()
        {
            lock (_sync)
            {
                TrafficFrame[] copy = _frames.ToArray();
                long first = copy.Length > 0 ? copy[0].Sequence : _nextSequence;
                long last = copy.Length > 0 ? copy[^1].Sequence : _nextSequence - 1;
                return new TrafficSnapshot(copy, first, last, _generation);
            }
        }
    }

    /// <summary>
    /// 全局抓包入口：各协议客户端在**已经算好的原始字节**处调 <see cref="Frame"/>。
    /// <para>
    /// 只抓「当前选中的那一个目标」且仅在通讯监控分段可见时抓 —— 这是 P2.4 的定案口径，
    /// 好处是未选中/未可见时热路径上只多两次 volatile 读，零分配。
    /// </para>
    /// <para>
    /// ⚠️ 回调里只做「写进环形缓冲」，不要在客户端里格式化字符串或碰控件：
    /// 这条路径每 100ms 就跑好几次。
    /// </para>
    /// </summary>
    public static class TrafficTap
    {
        private static readonly object AttachSync = new();
        private static volatile TrafficBuffer? _buffer;
        private static volatile object? _activeKey;
        private static volatile bool _enabled;

        /// <summary>绑定窗口侧的缓冲。单窗口应用只调用一次。</summary>
        public static void Attach(TrafficBuffer buffer)
        {
            lock (AttachSync)
            {
                _buffer = buffer;
            }
        }

        /// <summary>开始抓 <paramref name="key"/> 的报文；传 null 等于停止。</summary>
        public static void Activate(object? key)
        {
            if (key == null)
            {
                Deactivate();
                return;
            }

            // 先写 key 再置 enabled：抓包线程看到 enabled=true 时 key 一定已经写好
            _activeKey = key;
            _enabled = true;
        }

        public static void Deactivate() => _enabled = false;

        /// <summary>该目标此刻是否需要抓？客户端据此避免「为抓包白拷一份字节」。</summary>
        public static bool Wants(object owner) => _enabled && ReferenceEquals(owner, _activeKey);

        /// <summary>
        /// 记一帧。调用点传入的必须是**实际收发的那份字节**（不要在这里重新拼帧）：
        /// 本功能的价值就是让现场看到事务 ID / 长度字段 / CRC 的原样。
        /// </summary>
        public static void Frame(
            object owner, TrafficDirection direction, byte[] payload, string? caption = null)
        {
            if (!Wants(owner))
            {
                return;
            }

            // 空的二进制帧没有可读内容；HTTP 的说明行本身有信息（响应体可能为空）
            if (payload.Length == 0 && caption == null)
            {
                return;
            }

            _buffer?.Add(direction, payload, caption);
        }
    }
}
