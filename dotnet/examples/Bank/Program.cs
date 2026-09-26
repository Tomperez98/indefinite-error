// A tiny bank: deposits into accounts, stored in SQLite.
//
// Two ways to deposit. POST /deposits/unkeyed just adds the amount; POST
// /deposits carries an idempotency key and applies each key once. Under
// indefinite errors a retrying client double-counts with the first and never
// with the second, which the tests in ../Bank.Tests show.
//
// Run it with faults on, and send a seed per request:
//
//     dotnet run --IndefiniteErrors=true

using Bank;

var builder = WebApplication.CreateBuilder(args);

// The one place configuration is read. Never on in production: any caller could fault the server.
var indefiniteErrors = builder.Configuration.GetValue<bool>("IndefiniteErrors");
var database = builder.Configuration.GetConnectionString("Bank") ?? "Data Source=:memory:";

builder.Services.AddSingleton(_ => new Store(database));
if (indefiniteErrors)
{
    builder.Services.AddIndefiniteErrors();
}

var app = builder.Build();
if (indefiniteErrors)
{
    app.UseIndefiniteErrors();
}

app.MapPost("/deposits/unkeyed", async (Deposit body, Store store, CancellationToken ct) =>
{
    await store.DepositAsync(body.Account, body.Amount, ct);
    return new { status = "ok" };
});

app.MapPost("/deposits", async (KeyedDeposit body, Store store, CancellationToken ct) =>
{
    await store.DepositOnceAsync(body.Key, body.Account, body.Amount, ct);
    return new { status = "ok" };
});

app.MapGet("/accounts/{account}", async (string account, Store store, CancellationToken ct) =>
    new { balance = await store.BalanceAsync(account, ct) });

app.Run();

internal sealed record Deposit(string Account, long Amount);

internal sealed record KeyedDeposit(string Key, string Account, long Amount);

/// <summary>The entry point, visible to WebApplicationFactory in the tests.</summary>
public partial class Program;
