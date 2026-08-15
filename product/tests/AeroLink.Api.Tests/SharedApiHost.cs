// #563 phase 2 pilot: one API host (and one SQLite database file) shared by every test in a class.
//
// xUnit runs the tests of one class serially, so the class fixture is the natural reuse boundary: the
// host/database is built once per class instead of once per test. Each test still gets a fresh
// HttpClient (fresh cookie/session container) and seeds its own uniquely named logical data, so nothing
// is shared across tests except the process and the schema. The fixture is deliberately opt-in per class;
// the rest of the suite keeps its per-test factories.
//
// Telemetry attribution: the factory is constructed from this fixture, so 563A telemetry records the
// class as "SharedApiHost" with method "class fixture" (one factory per pilot class). It is a fixture
// owner, exactly like ShowcaseApiFixture: its host/dispose cost is class startup, outside any single
// test's TRX wall.

namespace AeroLink.Api.Tests;

public sealed class SharedApiHost : IDisposable
{
    private readonly AeroLinkApiFactory _factory;

    public SharedApiHost()
    {
        _factory = new AeroLinkApiFactory(callerFile: "SharedApiHost", callerMember: "class fixture");
    }

    internal AeroLinkApiFactory Factory => _factory;

    public HttpClient CreateClient() => _factory.CreateClient();

    public void Dispose() => _factory.Dispose();
}
