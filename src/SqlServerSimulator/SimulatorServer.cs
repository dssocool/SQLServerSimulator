using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using SqlServerSimulator.Mapping;
using SqlServerSimulator.Tds;

namespace SqlServerSimulator;

public sealed class SimulatorServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly MappingStore _mappings;
    private readonly X509Certificate2 _certificate;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public SimulatorServer(MappingStore mappings, int port)
    {
        _mappings = mappings;
        _certificate = TlsCertificate.CreateSelfSigned();
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_cts.Token);
        Console.WriteLine($"SQL Server Simulator listening on 127.0.0.1:{Port}");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = HandleClientAsync(client, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var session = new TdsSession(client.GetStream(), _mappings, _certificate);
                await session.RunAsync(ct);
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or OperationCanceledException)
            {
                // client disconnected
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[error] {ex}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; } catch { }
        }
        _cts.Dispose();
        _certificate.Dispose();
    }
}
