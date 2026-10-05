using System.Net;
using System.Text.Json;
using AeroLink.Infrastructure;
using AeroLink.Infrastructure.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

// Qualification-only executable. It calls the production dispatcher without a mock transport,
// injected clock, fault hook or altered gate. Every input and output belongs to the parent fixture.
if (args.Length != 1) throw new ArgumentException("An owned qualification configuration file is required.");
var configuration = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(args[0])).Build();
var connection = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("AeroLink"));
if (!IPAddress.TryParse(connection.Host, out var address) || !IPAddress.IsLoopback(address)
    || connection.Port == 54329 || connection.Database is null || !connection.Database.StartsWith("notification_process_", StringComparison.Ordinal))
    throw new InvalidOperationException("Qualification host refuses an unowned or persistent database.");
var authorityRoot = Environment.GetEnvironmentVariable("AEROLINK_NOTIFICATION_AUTHORITY_ROOT");
if (string.IsNullOrWhiteSpace(authorityRoot) || string.IsNullOrWhiteSpace(configuration["DataProtection:KeyRingPath"]))
    throw new InvalidOperationException("Qualification requires an explicitly owned authority and key ring.");
var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(configuration);
services.AddAeroLinkInfrastructure(configuration);
await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();
var result = await scope.ServiceProvider.GetRequiredService<NotificationDispatcher>().DispatchAsync(25, CancellationToken.None);
Console.WriteLine(JsonSerializer.Serialize(new { result.Sent, result.Suppressed, result.Failed }));
