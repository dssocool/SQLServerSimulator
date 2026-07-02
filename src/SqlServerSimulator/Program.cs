using System.Net;
using SqlServerSimulator;
using SqlServerSimulator.Mapping;

var configPath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "mappings", "mappings.json");
var port = args.Length > 1 ? int.Parse(args[1]) : 11433;
var bindAddress = args.Length > 2 ? IPAddress.Parse(args[2]) : IPAddress.Any;

var mappings = MappingConfig.Load(configPath);
await using var server = new SimulatorServer(mappings, port, bindAddress);
server.Start();

Console.WriteLine("Press Ctrl+C to stop.");
var exit = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; exit.TrySetResult(); };
await exit.Task;
