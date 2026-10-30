// M1 的 Tier-1 载荷桥。原规格 14 §4 白名单里 M1 可用的基座只跑 WASM（WASI preview1 子集），
// 而 原规格 13 §2 的 Tier-1 脚本形态（JS 解释）在 M1 没有活的 .NET 引擎（契约 §4.2：QuickJS.NET 判死）。
// M1 的诚实边界（原规格 50：执行语义属 M2）：宿主把入口文件字节嵌入一个最小 WASM 模块的数据段，
// 模块导出 tick() —— 沙箱真实装载并执行的载荷由插件入口派生，生命周期（发现/校验/装载/禁用/
// 热重载）全部走真链路；JS 解释执行不在 M1 声称范围。入口本身是 .wasm 时原样直载，不经本类。
//
// 模块布局（Wasmtime 34 可装载的最小合法模块）：
//   type   () -> ()          func    typeidx 0
//   memory min=ceil((4+载荷)/64KiB)  export  "tick" → func 0
//   code   0 locals, end     data    active @0 = uint32 LE 载荷长度 + 载荷字节
using System;
using System.IO;
using System.Linq;

namespace PetDock.Hosting;

internal static class WasmCarrier
{
    private const int HeaderBytes = 4; // 数据段头：uint32 LE 载荷长度
    private const int PageSize = 65536;

    /// <summary>把入口字节嵌入最小 WASM 载体模块（导出 tick，数据段携带入口内容）。</summary>
    public static byte[] Build(ReadOnlySpan<byte> payload)
    {
        long total = HeaderBytes + payload.Length;
        uint pages = (uint)Math.Max(1, (total + PageSize - 1) / PageSize);

        using var output = new MemoryStream(64 + (int)total);
        byte[] header = { 0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00 }; // magic + version
        output.Write(header, 0, header.Length);

        WriteSection(output, 1, new byte[] { 0x01, 0x60, 0x00, 0x00 }); // type: () -> ()
        WriteSection(output, 3, new byte[] { 0x01, 0x00 }); // func: 1 个函数，typeidx 0
        WriteSection(output, 5, new byte[] { 0x01, 0x00 }.Concat(Uleb(pages)).ToArray()); // memory: min pages
        WriteSection(output, 7, new byte[] { 0x01, 0x04, 0x74, 0x69, 0x63, 0x6B, 0x00, 0x00 }); // export "tick"
        WriteSection(output, 10, new byte[] { 0x01, 0x02, 0x00, 0x0B }); // code: 1 body(size 2), 0 locals, end

        // data: 1 段，active memidx 0，offset = i32.const 0 end，内容 = 4 字节 LE 长度 + 载荷
        using var data = new MemoryStream((int)total);
        data.WriteByte(0x01);
        data.WriteByte(0x00);
        data.WriteByte(0x41);
        data.WriteByte(0x00);
        data.WriteByte(0x0B);
        WriteUleb(data, (uint)total);
        Span<byte> lengthBytes = stackalloc byte[HeaderBytes];
        BitConverter.TryWriteBytes(lengthBytes, (uint)payload.Length);
        data.Write(lengthBytes);
        data.Write(payload);
        WriteSection(output, 11, data.ToArray());

        return output.ToArray();
    }

    /// <summary>按内容嗅探是否为原生 WASM 模块（magic 4 字节）——比扩展名更可靠地决定直载还是装载体。</summary>
    public static bool IsWasmModule(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x61 && bytes[2] == 0x73 && bytes[3] == 0x6D;

    private static void WriteSection(Stream output, int id, byte[] content)
    {
        output.WriteByte((byte)id);
        WriteUleb(output, (uint)content.Length);
        output.Write(content, 0, content.Length);
    }

    private static void WriteUleb(Stream output, uint value)
    {
        while (value >= 0x80)
        {
            output.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        output.WriteByte((byte)value);
    }

    private static byte[] Uleb(uint value)
    {
        using var ms = new MemoryStream(5);
        WriteUleb(ms, value);
        return ms.ToArray();
    }
}
