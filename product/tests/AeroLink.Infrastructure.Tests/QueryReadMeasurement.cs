using System.Collections;
using System.Data;
using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AeroLink.Infrastructure.Tests;

/// <summary>Measures rows delivered by the provider, not rows scanned inside its plan. JSON bytes are UTF-8
/// payload bytes in returned columns named *Json*. Plan evidence separately establishes database scan work.</summary>
internal sealed class QueryReadMeasurement : DbCommandInterceptor
{
    public bool Enabled { get; set; }
    public List<Statement> Statements { get; } = [];
    public long Rows => Statements.Sum(x => x.Rows);
    public long JsonBytes => Statements.Sum(x => x.JsonBytes);
    public void Reset() => Statements.Clear();
    internal sealed record Parameter(string Name, object? Value);
    internal sealed class Statement(string sql, IReadOnlyList<Parameter> parameters)
    {
        public string Sql { get; } = sql;
        public IReadOnlyList<Parameter> Parameters { get; } = parameters;
        public long Rows { get; set; }
        public long JsonBytes { get; set; }
    }
    private DbDataReader Observe(DbCommand command, DbDataReader reader)
    {
        if (!Enabled) return reader;
        var statement = new Statement(command.CommandText,
            command.Parameters.Cast<DbParameter>().Select(x => new Parameter(x.ParameterName, x.Value)).ToArray());
        Statements.Add(statement);
        return new CountingReader(reader, statement);
    }
    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData,
        DbDataReader result) => Observe(command, result);
    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        DbDataReader result, CancellationToken cancellationToken = default) => ValueTask.FromResult(Observe(command, result));

    private sealed class CountingReader(DbDataReader inner, Statement statement) : DbDataReader
    {
        private bool Count(bool read)
        {
            if (!read) return false;
            statement.Rows++;
            for (var i = 0; i < inner.FieldCount; i++)
                if (inner.GetName(i).Contains("Json", StringComparison.OrdinalIgnoreCase)
                    && !inner.IsDBNull(i) && inner.GetValue(i) is string json)
                    statement.JsonBytes += Encoding.UTF8.GetByteCount(json);
            return true;
        }
        public override bool Read() => Count(inner.Read());
        public override async Task<bool> ReadAsync(CancellationToken ct) => Count(await inner.ReadAsync(ct));
        public override bool NextResult() => inner.NextResult();
        public override Task<bool> NextResultAsync(CancellationToken ct) => inner.NextResultAsync(ct);
        public override int Depth => inner.Depth;
        public override int FieldCount => inner.FieldCount;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override object this[int ordinal] => inner[ordinal];
        public override object this[string name] => inner[name];
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        public override long GetBytes(int ordinal, long offset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, offset, buffer, bufferOffset, length);
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override long GetChars(int ordinal, long offset, char[]? buffer, int bufferOffset, int length) => inner.GetChars(ordinal, offset, buffer, bufferOffset, length);
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        public override string GetName(int ordinal) => inner.GetName(ordinal);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override string GetString(int ordinal) => inner.GetString(ordinal);
        public override object GetValue(int ordinal) => inner.GetValue(ordinal);
        public override int GetValues(object[] values) => inner.GetValues(values);
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken ct) => inner.IsDBNullAsync(ordinal, ct);
        public override T GetFieldValue<T>(int ordinal) => inner.GetFieldValue<T>(ordinal);
        public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken ct) => inner.GetFieldValueAsync<T>(ordinal, ct);
        public override DataTable? GetSchemaTable() => inner.GetSchemaTable();
        public override IEnumerator GetEnumerator() => ((IEnumerable)inner).GetEnumerator();
        public override void Close() => inner.Close();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
