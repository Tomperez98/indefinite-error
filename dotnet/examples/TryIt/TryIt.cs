#:sdk Microsoft.NET.Sdk.Web
#:project ../../src/IndefiniteError.AspNetCore/IndefiniteError.AspNetCore.csproj

using IndefiniteError;

var ledger = new List<int>();
var add = new IndefiniteSite("ledger.add"); // the boundary write that can lose its outcome

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddIndefiniteErrors();

var app = builder.Build();
app.UseIndefiniteErrors(); // tests only: requests carrying X-Indefinite-Seed get faults
app.MapPost("/deposits", () =>
{
    add.Run(() => ledger.Add(1));
    return "ok";
});
await app.StartAsync();

using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
foreach (var seed in new[] { "70", "74", "0" })
{
    ledger.Clear();
    using var request = new HttpRequestMessage(HttpMethod.Post, "/deposits");
    request.Headers.Add(IndefiniteErrorHeaders.Seed, seed);
    using var response = await client.SendAsync(request);
    Console.WriteLine($"seed={seed}: status={(int)response.StatusCode}, ledger=[{string.Join(", ", ledger)}]");
}

await app.StopAsync();
