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

    public SimulatorServer(MappingStore mappings, int port, IPAddress? bindAddress = null)
    {
        _mappings = mappings;
        BindAddress = bindAddress ?? IPAddress.Any;
        _certificate = TlsCertificate.LoadOrCreatePersisted(AppContext.BaseDirectory);
        _listener = new TcpListener(BindAddress, port);
    }

    public IPAddress BindAddress { get; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_cts.Token);
        var bindLabel = BindAddress.Equals(IPAddress.Any) ? "0.0.0.0 (all interfaces)" : BindAddress.ToString();
        Console.WriteLine($"SQL Server Simulator listening on {bindLabel}:{Port}");
        Console.WriteLine($"TLS certificate (public part): {Path.Combine(AppContext.BaseDirectory, "simulator-tls.cer")}");
        Console.WriteLine("For clients that validate the certificate (e.g. Power BI 'Use encrypted connection'),");
        Console.WriteLine("install that .cer into 'Trusted Root Certification Authorities' and connect using 'localhost,<port>'.");
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
