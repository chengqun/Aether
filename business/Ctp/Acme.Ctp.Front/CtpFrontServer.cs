using System.Net;
using System.Net.Sockets;
using Acme.Ctp.Simulator;

namespace Acme.Ctp.Front;

/// <summary>
/// CTP 前置服务端：在指定端口监听 CTP 线协议连接，每个连接一个 <see cref="CtpFrontSession"/> 状态机，
/// 对接 <see cref="SimTradingCore"/>（共享实例——与 SimRpcServer 平级，同一撮合核心双协议接入）。
/// <para>
/// 2026-09-29 MVP：帧分帧器 + 握手/认证/登录状态机 + SimTradingCore.Login 对接。
/// 业务操作（查/报/撤/行情）和推送待后续迭代。
/// </para>
/// </summary>
public sealed class CtpFrontServer : IDisposable
{
    private readonly SimTradingCore _core;
    private readonly string _host;
    private readonly int _port;
    private readonly Action<string> _log;
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private readonly CancellationTokenSource _cts = new();

    public CtpFrontServer(SimTradingCore core, string host, int port, Action<string>? log = null)
    {
        _core = core;
        _host = host;
        _port = port;
        _log = log ?? (msg => Console.WriteLine($"[CTP-Front] {msg}"));
    }

    public IPEndPoint? LocalEndpoint => _listener?.LocalEndpoint as IPEndPoint;

    public void Start()
    {
        var addr = IPAddress.TryParse(_host, out var ip) ? ip : IPAddress.Loopback;
        _listener = new TcpListener(addr, _port);
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _log($"CTP 前置已启动: {_host}:{_port}");
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; } catch { }
        }
        _log("CTP 前置已停止");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch { break; }

            var session = new CtpFrontSession(client, _core, _log);
            _ = Task.Run(() => session.RunAsync(ct), ct);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
    }
}
