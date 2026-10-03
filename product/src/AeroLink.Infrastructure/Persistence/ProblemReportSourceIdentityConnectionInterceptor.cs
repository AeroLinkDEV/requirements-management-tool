using System.Data.Common;
using System.Data;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>SQLite functions for the frozen v1 database-derived source identity. BLOB arguments retain
/// embedded NUL; SQLite's string callback truncates it before the comparer can see it.</summary>
internal sealed class ProblemReportSourceIdentityConnectionInterceptor : DbConnectionInterceptor
{
    internal static readonly ProblemReportSourceIdentityConnectionInterceptor Instance = new();

    internal static void Register(DbConnection connection)
    {
        if (connection is not SqliteConnection sqlite) return;
        // Native Open is also used by supplied-connection owners and local schema guards. It bypasses
        // EF's Opened interceptor, and pooled native handles must regain the deterministic functions.
        sqlite.StateChange -= NativeStateChanged;
        sqlite.StateChange += NativeStateChanged;
        sqlite.CreateFunction<byte[]?, bool, byte[], byte[]?>("aerolink_source_identity_v1", (bytes, fold, marker) =>
        {
            if (bytes is null) return null;
            var text = Decode(bytes, marker);
            return fold ? ProblemReportSourceIdentityKey.SourceKey(text) : ProblemReportSourceIdentityKey.SourceSystem(text);
        }, isDeterministic: true);
        sqlite.CreateFunction<byte[]?, byte[], bool>("aerolink_source_trimmed_v1", (bytes, marker) =>
        {
            if (bytes is null) return true;
            var text = Decode(bytes, marker);
            var scalars = 0;
            foreach (var scalar in text.EnumerateRunes()) scalars++;
            return scalars * 4 == ProblemReportSourceIdentityKey.SourceSystem(text).Length;
        }, isDeterministic: true);
    }

    internal static string Decode(byte[] bytes, byte[] marker)
    {
        // SQLite emits literal text in the database's persisted encoding. Its BLOB marker makes
        // decoding deterministic without querying connection state or caching an early default.
        Encoding encoding = marker.AsSpan().SequenceEqual(new byte[] { 0x41 }) ? new UTF8Encoding(false, true)
            : marker.AsSpan().SequenceEqual(new byte[] { 0x41, 0 }) ? new UnicodeEncoding(false, false, true)
            : marker.AsSpan().SequenceEqual(new byte[] { 0, 0x41 }) ? new UnicodeEncoding(true, false, true)
            : throw new InvalidOperationException("The SQLite source identity encoding is unsupported.");
        return encoding.GetString(bytes);
    }
    private static void NativeStateChanged(object? sender, StateChangeEventArgs state)
    { if (state.CurrentState == ConnectionState.Open && sender is DbConnection connection) Register(connection); }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Register(connection);
    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    { Register(connection); return Task.CompletedTask; }
}
