using IndefiniteError;
using Microsoft.Data.Sqlite;

namespace Bank;

/// <summary>The durable state, in SQLite. Its writes are the boundary where outcomes get lost.</summary>
public sealed class Store : IDisposable
{
    // Mark each write that talks to the outside world. A fault lands right
    // before it (the write never happened) or right after it (it committed, but
    // the response is lost).
    private static readonly IndefiniteSite Deposit = new("store.deposit");
    private static readonly IndefiniteSite DepositOnce = new("store.deposit_once");

    private readonly SqliteConnection _db;
    private readonly SemaphoreSlim _gate = new(1, 1); // one connection: one statement at a time

    public Store(string connectionString)
    {
        _db = new SqliteConnection(connectionString);
        _db.Open();
        Execute("CREATE TABLE IF NOT EXISTS accounts (id TEXT PRIMARY KEY, balance INTEGER NOT NULL)");
        Execute("CREATE TABLE IF NOT EXISTS applied (key TEXT PRIMARY KEY)");
    }

    /// <summary>Adds <paramref name="amount"/> to <paramref name="account"/>.</summary>
    public Task DepositAsync(string account, long amount, CancellationToken cancellationToken) =>
        Deposit.RunAsync(() => Transaction(tx => AddAsync(tx, account, amount, cancellationToken), cancellationToken));

    /// <summary>
    /// Applies <paramref name="key"/> once: the first call adds the amount, every
    /// later call with the same key adds nothing. The key and the money are one
    /// transaction, so a retry finds both or neither.
    /// </summary>
    public Task DepositOnceAsync(string key, string account, long amount, CancellationToken cancellationToken) =>
        DepositOnce.RunAsync(() => Transaction(
            async tx =>
            {
                using var applied = Command(tx, "INSERT INTO applied VALUES ($key) ON CONFLICT DO NOTHING", ("$key", key));
                if (await applied.ExecuteNonQueryAsync(cancellationToken) == 1)
                {
                    await AddAsync(tx, account, amount, cancellationToken);
                }
            },
            cancellationToken));

    /// <summary>The account's balance, or 0 if it was never touched.</summary>
    public async Task<long> BalanceAsync(string account, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var query = Command(null, "SELECT balance FROM accounts WHERE id = $id", ("$id", account));
            return await query.ExecuteScalarAsync(cancellationToken) is long balance ? balance : 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        _gate.Dispose();
    }

    private async Task Transaction(Func<SqliteTransaction, Task> body, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var tx = (SqliteTransaction)await _db.BeginTransactionAsync(cancellationToken);
            await body(tx);
            await tx.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task AddAsync(SqliteTransaction tx, string account, long amount, CancellationToken cancellationToken)
    {
        using var add = Command(
            tx,
            "INSERT INTO accounts VALUES ($id, $amount) ON CONFLICT (id) DO UPDATE SET balance = balance + excluded.balance",
            ("$id", account),
            ("$amount", amount));
        await add.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteCommand Command(SqliteTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    {
        var command = _db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }

    private void Execute(string sql)
    {
        using var command = Command(null, sql);
        command.ExecuteNonQuery();
    }
}
