using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Acme.Ctp.ProtoCapture;

/// <summary>
/// 纯字节管道 TCP 记录代理：本地端口 ←→ 远端前置，双向转发 + 逐帧记录。
/// 不解析、不修改——分析在抓包之后离线做。每帧记录 [时间戳][方向][字节数][hex]。
/// </summary>
public sealed class RecordingProxy : IDisposable
{
    private readonly int _listenPort;
    private readonly string _remoteHost;
    private readonly int _remotePort;
    private readonly string _label;
    private readonly string _captureFile;
    private TcpListener? _listener;
    private readonly List<Task> _pumps = new();
    private readonly object _fileLock = new();
    private int _connectionSeq;
    private bool _disposed;

    public RecordingProxy(int listenPort, string remoteFront, string label, string captureFile)
    {
        _listenPort = listenPort;
        // remoteFront = "tcp://host:port"
        var uri = remoteFront.Replace("tcp://", "");
        var colon = uri.LastIndexOf(':');
        _remoteHost = uri[..colon];
        _remotePort = int.Parse(uri[(colon + 1)..]);
        _label = label;
        _captureFile = captureFile;
    }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, _listenPort);
        _listener.Start();
        _pumps.Add(Task.Run(AcceptLoopAsync));
        Console.WriteLine($"[Proxy:{_label}] 监听 127.0.0.1:{_listenPort} → {_remoteHost}:{_remotePort}");
    }

    private async Task AcceptLoopAsync()
    {
        while (!_disposed)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(); }
            catch { break; }
            var seq = Interlocked.Increment(ref _connectionSeq);
            Console.WriteLine($"[Proxy:{_label}] 连接 #{seq} 接入");
            _pumps.Add(Task.Run(() => PumpConnectionAsync(client, seq)));
        }
    }

    private async Task PumpConnectionAsync(TcpClient client, int seq)
    {
        using var _ = client;
        using var remote = new TcpClient();
        try
        {
            await remote.ConnectAsync(_remoteHost, _remotePort);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Proxy:{_label}] 远端连接失败 #{seq}: {ex.Message}");
            return;
        }

        Console.WriteLine($"[Proxy:{_label}] 连接 #{seq} 远端已通，开始转发");
        var cs = client.GetStream();
        var ss = remote.GetStream();

        var t1 = Task.Run(() => PumpAsync(cs, ss, seq, 'C', "C→S"));   // client → server
        var t2 = Task.Run(() => PumpAsync(ss, cs, seq, 'S', "S→C"));   // server → client
        await Task.WhenAll(t1, t2);
        Console.WriteLine($"[Proxy:{_label}] 连接 #{seq} 断开");
    }

    private async Task PumpAsync(NetworkStream from, NetworkStream to, int connSeq, char direction, string dirLabel)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!_disposed)
            {
                var n = await from.ReadAsync(buffer);
                if (n == 0) break;
                await to.WriteAsync(buffer.AsMemory(0, n));
                RecordFrame(connSeq, direction, buffer, n, dirLabel);
            }
        }
        catch { /* 连接断开 */ }
    }

    private void RecordFrame(int connSeq, char direction, byte[] buffer, int length, string dirLabel)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff");
        var hex = Convert.ToHexString(buffer, 0, Math.Min(length, 256));   // 前 256 字节 hex（防巨帧刷屏）
        var line = $"{timestamp} #{connSeq} {dirLabel} [{length} bytes] {hex}{(length > 256 ? " …" : "")}";
        lock (_fileLock)
        {
            File.AppendAllText(_captureFile, line + "\n");
        }
        Console.WriteLine($"[Proxy:{_label}] {line}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _listener?.Stop(); } catch { }
    }
}
