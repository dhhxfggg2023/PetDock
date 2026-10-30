namespace PetDock.DyberPet;

/// <summary>
/// 只读 PNG 尺寸读取：IHDR 块在固定偏移（8 字节签名 + 4 长度 + 4 "IHDR"，宽高在第 16..23 字节，大端）。
/// 导入期要扫全包数百帧取 naturalSize，整图解码太重；头解析零解码成本，格式不对就抛（不给静默错包）。
/// </summary>
internal static class PngMeta
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static (int Width, int Height) ReadSize(string path)
    {
        Span<byte> header = stackalloc byte[24];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        int read = 0;
        while (read < header.Length)
        {
            int n = stream.Read(header[read..]);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        if (read < header.Length || !header[..8].SequenceEqual(Signature))
        {
            throw new InvalidDataException($"不是 PNG（签名不符）: {path}");
        }

        string chunkType = System.Text.Encoding.ASCII.GetString(header[12..16]);
        if (!string.Equals(chunkType, "IHDR", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"PNG 首块不是 IHDR（{chunkType}）: {path}");
        }

        int width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
        int height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException($"PNG 尺寸非法 {width}x{height}: {path}");
        }

        return (width, height);
    }
}
