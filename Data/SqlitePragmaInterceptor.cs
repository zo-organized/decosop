using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DecoSOP.Data;

/// <summary>
/// Sets <c>busy_timeout=5000</c> on every opened connection: when the database is momentarily locked,
/// wait up to 5s for the lock instead of failing. busy_timeout is per-connection state, so it must be
/// re-applied on every open — that's this interceptor's job, and it's cheap (one pragma round-trip).
///
/// WAL journal mode is deliberately NOT set here: it persists in the database file header, so it only
/// needs to be set once (at startup, see Program.cs). Re-issuing <c>PRAGMA journal_mode=WAL</c> on every
/// connection open is measurably expensive under connection churn, so we don't.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string Pragmas = "PRAGMA busy_timeout=5000;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
