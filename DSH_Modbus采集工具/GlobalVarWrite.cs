using System;
using DSH_Modbus采集工具.Drivers;
using DSH_Modbus采集工具.Modeling;

namespace DSH_Modbus采集工具
{
    /// <summary>
    /// 全局变量的写入规划（P3.3）：文本 → <see cref="WritePlan"/>。
    /// <para>
    /// 与 <see cref="MonitorItemSet.TryBuildWrite"/> 同一套编解码规则（都走 <see cref="ValueCodec"/>），
    /// 差别只在入参：变量没有 <c>MonitorItem</c>，用 <see cref="GlobalVariable"/> 直接构造。
    /// 两条路径共用同一个 <see cref="WritePlan"/>，报文层不必区分来源。
    /// </para>
    /// </summary>
    public static class GlobalVarWrite
    {
        /// <summary>
        /// 把一条变量的文本值编码成写入计划。
        /// </summary>
        /// <param name="v">变量（须已解析出地址）</param>
        /// <param name="text">用户输入的文本</param>
        /// <param name="driver">当前驱动（提供字节顺序约定）</param>
        /// <param name="plan">产出的写入计划</param>
        /// <param name="error">失败原因</param>
        public static bool TryBuild(
            GlobalVariable v,
            string? text,
            CommDriver driver,
            out WritePlan plan,
            out string error)
        {
            plan = default;
            error = string.Empty;

            if (v == null || driver == null)
            {
                error = "参数为空";
                return false;
            }

            if (!v.HasAddress)
            {
                error = "该变量未关联地址";
                return false;
            }

            if (v.ResolvedAddress < 0)
            {
                error = v.ResolveError.Length > 0 ? v.ResolveError : "地址未解析";
                return false;
            }

            if (v.IsReadOnly)
            {
                error = "该地址为只读输入区，不可写";
                return false;
            }

            DataTypeSpec spec = DataTypes.Get(v.Type);

            // 线圈：BOOL 走 FC05，只接受 0 / 1
            if (spec.CoilOnly)
            {
                string s = (text ?? string.Empty).Trim();
                if (s != "0" && s != "1")
                {
                    error = "线圈只能写 0 或 1";
                    return false;
                }

                plan = new WritePlan
                {
                    Area = v.Area,
                    Address = v.ResolvedAddress,
                    IsCoil = true,
                    CoilValue = s == "1",
                };
                return true;
            }

            if (!ValueCodec.TryEncodeRegisters(
                    v.Type,
                    v.Format,
                    text,
                    v.Length,
                    driver.Int32ByteOrder,
                    driver.StringByteOrder,
                    out ushort[] regs,
                    out error))
            {
                return false;
            }

            plan = new WritePlan
            {
                Area = v.Area,
                Address = v.ResolvedAddress,
                IsCoil = false,
                Registers = regs,
            };
            return true;
        }
    }
}
