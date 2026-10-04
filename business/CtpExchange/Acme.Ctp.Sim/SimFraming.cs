using System.Buffers.Binary;

namespace Acme.Ctp.Sim;

/// <summary>
/// 帧编解码：4 字节小端长度前缀 + UTF-8 JSON 载荷。单帧上限 16 MiB——超过即视为协议故障，调用方断开连接。
/// </summary>
public static class SimFraming
{
    private const int MaxFrameBytes = 16 * 1024 * 1024;

    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>同步写帧（fire-and-forget 发送方是同步 void 方法，如 RemoteCtpSession.Logout——本地回环小帧，同步写可接受）。</summary>
    public static void WriteFrame(Stream stream, ReadOnlySpan<byte> payload)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        stream.Write(header);
        stream.Write(payload);
        stream.Flush();
    }

    /// <summary>读一帧（不带取消令牌——接收循环靠 socket 关闭退出，不用 CT 掐断）。</summary>
    public static async Task<byte[]> ReadFrameAsync(Stream stream)
    {
        var header = await ReadExactAsync(stream, 4).ConfigureAwait(false);
        var len = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (len is < 0 or > MaxFrameBytes)
            throw new IOException($"仿真线协议帧长度非法：{len}");
        return len == 0 ? Array.Empty<byte>() : await ReadExactAsync(stream, len).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count)
    {
        var buf = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buf.AsMemory(read)).ConfigureAwait(false);
            if (n == 0) throw new IOException("仿真柜台连接已关闭。");
            read += n;
        }
        return buf;
    }
}
