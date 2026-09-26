// A retrying client must keep the bank's books right under indefinite errors.
//
// Each run makes Deposits deposits of 1 against a fresh bank, every request
// with its own seed, retrying whenever the outcome is indefinite, as a real
// client would after a timeout. Then it checks one invariant: the balance is
// exactly Deposits.

using System.Net;
using System.Net.Http.Json;
using IndefiniteError;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace Bank.Tests;

public sealed class BankTests
{
    private const int Runs = 50;
    private const int Deposits = 20;
    private const int Attempts = 30; // a seed may fault half its requests: retry until one gets through

    /// <summary>One seed per request, derived from the run: replay a run, replay its faults.</summary>
    private static long Seed(int run, int step, int attempt) => (run * 1_000_000L) + (step * 1_000L) + attempt;

    /// <summary>What one run did: its final balance, and every fault the server reported.</summary>
    private sealed record Run(long Balance, IReadOnlyList<string> Faults)
    {
        public bool Ok => Balance == Deposits;

        public override string ToString() => $"balance {Balance}, expected {Deposits}; faults: [{string.Join(", ", Faults)}]";
    }

    private static async Task<Run> RunDeposits(int run, string path, Func<int, object> body)
    {
        await using var bank = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("IndefiniteErrors", "true")
            .ConfigureLogging(logging => logging.ClearProviders()));
        using var client = bank.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var faults = new List<string>();
        for (var step = 0; step < Deposits; step++)
        {
            var gotThrough = false;
            for (var attempt = 0; attempt < Attempts && !gotThrough; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body(step)) };
                request.Headers.Add(IndefiniteErrorHeaders.Seed, Seed(run, step, attempt).ToString(System.Globalization.CultureInfo.InvariantCulture));
                using var response = await client.SendAsync(request, ct);
                switch (response.StatusCode)
                {
                    case HttpStatusCode.OK:
                        gotThrough = true;
                        break;
                    case HttpStatusCode.InternalServerError:
                        // Indefinite: it may or may not have happened. The fault header is
                        // for us, debugging; the client logic must not read it. Retry.
                        faults.Add(response.Headers.GetValues(IndefiniteErrorHeaders.Fault).Single());
                        break;
                    default:
                        Assert.Fail($"unexpected status {response.StatusCode}");
                        break;
                }
            }

            Assert.True(gotThrough, $"step {step} never got through in {Attempts} attempts");
        }

        var balance = await client.GetFromJsonAsync<BalanceBody>("/accounts/alice", ct);
        return new Run(balance!.Balance, faults);
    }

    private sealed record BalanceBody(long Balance);

    // The key is fixed per deposit, not per attempt: a retry is the same deposit.
    private static object Keyed(int step) => new { key = $"deposit-{step}", account = "alice", amount = 1 };

    private static object Unkeyed(int step) => new { account = "alice", amount = 1 };

    [Fact]
    public async Task Keyed_deposits_count_exactly_once()
    {
        for (var run = 0; run < Runs; run++)
        {
            var result = await RunDeposits(run, "/deposits", Keyed);
            Assert.True(result.Ok, $"run {run}: {result}");
        }
    }

    [Fact]
    public async Task Unkeyed_deposits_double_count_and_the_faults_say_why()
    {
        // The bug this library exists to find, found: an AFTER fault, then a retry.
        var broken = new Dictionary<int, Run>();
        for (var run = 0; run < Runs; run++)
        {
            var result = await RunDeposits(run, "/deposits/unkeyed", Unkeyed);
            if (!result.Ok)
            {
                broken[run] = result;
            }
        }

        Assert.NotEmpty(broken); // else the faults never reached store.deposit
        Assert.All(broken.Values, result =>
        {
            Assert.True(result.Balance > Deposits, $"retries only ever add: {result}");
            Assert.Contains(result.Faults, fault => fault.StartsWith("after store.deposit#", StringComparison.Ordinal));
        });

        // Every port replays the same seeds, so every port finds the same broken runs.
        Assert.Equal(39, broken.Count);
        Assert.Equal("balance 21, expected 20; faults: [after store.deposit#0 (seed=1004000)]", broken[1].ToString());
    }
}
