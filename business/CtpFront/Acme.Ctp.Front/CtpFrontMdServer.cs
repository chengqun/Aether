using System.Net;
using System.Net.Sockets;
using Acme.Ctp.Simulator;

namespace Acme.Ctp.Front;

/// <summary>
/// CTP 行情腿前置服务端（M3）：独立端口监听（host 默认 17802，env <c>AETHER_CTP_MD_FRONT_PORT</c>），
/// 每连接一个 <see cref="CtpFrontMdSession"/>。与 <see cref="CtpFrontServer"/>（交易腿）共享同一
/// <see cref="SimTradingCore"/>——行情随柜台：tick 来自同一撮合核心的 <c>TickProduced</c>。
/// </summary>
public sealed class CtpFrontMdServer : IDisposable
{
    private readonly SimTradingCore _core;
    private readonly string _host;
    private readonly int _port;
    private readonly Action<string> _log;
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private readonly CancellationTokenSource _cts = new();

    public CtpFrontMdServer(SimTradingCore core, string host, int port, Action<string>? log = null)
    {
        _core = core;
        _host = host;
        _port = port;
        _log = log ?? (msg => Console.WriteLine($"[CTP-Front-MD] {msg}"));
    }

    public IPEndPoint? LocalEndpoint => _listener?.LocalEndpoint as IPEndPoint;

    public void Start()
    {
        var addr = IPAddress.TryParse(_host, out var ip) ? ip : IPAddress.Loopback;
        _listener = new TcpListener(addr, _port);
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _log($"CTP 行情腿前置已启动: {_host}:{_port}");
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; } catch { }
        }
        _log("CTP 行情腿前置已停止");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch { break; }
            var session = new CtpFrontMdSession(client, _core, _log);
            _ = Task.Run(() => session.RunAsync(ct), ct);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
    }
}
