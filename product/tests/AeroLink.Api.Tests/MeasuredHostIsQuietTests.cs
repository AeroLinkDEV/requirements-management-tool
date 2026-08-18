namespace AeroLink.Api.Tests;

/// <summary>
/// A query-count assertion is a statement about one request, so the host must not be issuing queries of its
/// own while it is measured.
///
/// The managed-document inventory test asserts a request costs between one and eight commands, and it failed
/// on CI having counted nine. The ninth was not the request's: six background workers poll this database on
/// their own timers, and every command they issue reaches the same interceptor. An idle host was measured
/// issuing fourteen commands in eight seconds with nothing in flight -- the job worker's claim query, the
/// webhook dispatcher's delivery query, a version poll -- so a worker waking inside a measured window pushed
/// a passing request over its bound. Nothing about the product was wrong, and nothing about the assertion was
/// wrong either; the measurement was being taken in a room that would not stay still.
/// </summary>
public sealed class MeasuredHostIsQuietTests
{
    [Fact]
    public async Task A_host_built_for_measurement_issues_no_commands_of_its_own()
    {
        var commands = new ProblemReportPagingCommandInterceptor();
        using var factory = new AeroLinkApiFactory(commandInterceptor: commands);
        using var client = factory.CreateClient();
        await ProblemReportApiTests.BootstrapAndLoginAsync(client);

        commands.Clear();
        // Comfortably longer than the shortest worker interval, which is the webhook dispatcher's three
        // seconds. A shorter wait would pass whether or not the workers were running.
        await Task.Delay(TimeSpan.FromSeconds(8));

        var captured = commands.Commands.ToArray();
        Assert.True(captured.Length == 0,
            $"An idle measured host issued {captured.Length} commands with no request in flight. A query-count "
            + "assertion cannot mean anything while this is true:\n"
            + string.Join("\n", captured.Select(command => command.Split('\n')[0]).Distinct()));
    }
}
