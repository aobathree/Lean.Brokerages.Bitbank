// Minimum-lot order lifecycle smoke test (integration test phase P3):
//   1. subscribe to the private stream
//   2. place a post-only limit BUY far below the market (won't fill; min lot 0.0001 BTC)
//   3. wait for the spot_order_new event
//   4. cancel the order
//   5. wait for the CANCELED_UNFILLED event
//
// This PLACES A REAL ORDER on your bitbank account (canceled immediately, and priced
// ~50% below market with post_only so it cannot execute). Run it yourself, deliberately:
//   op run --env-file=.env.1password -- dotnet run --project tools/OrderSmokeTest -- --yes
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Threading;
using QuantConnect.Brokerages.Bitbank.Api;
using QuantConnect.Brokerages.Bitbank.Messages;
using QuantConnect.Brokerages.Bitbank.Streaming;

const string Pair = "btc_jpy";
const decimal Amount = 0.0001m; // bitbank minimum order size for btc_jpy

var apiKey = Environment.GetEnvironmentVariable("BITBANK_API_KEY");
var apiSecret = Environment.GetEnvironmentVariable("BITBANK_API_SECRET");

if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
{
    Console.Error.WriteLine("ERROR: BITBANK_API_KEY / BITBANK_API_SECRET are not set.");
    return 1;
}
if (!args.Contains("--yes"))
{
    Console.Error.WriteLine("This test places (and immediately cancels) a REAL minimum-lot order on your account.");
    Console.Error.WriteLine("Re-run with --yes to proceed:");
    Console.Error.WriteLine("  op run --env-file=.env.1password -- dotnet run --project tools/OrderSmokeTest -- --yes");
    return 1;
}

using var restClient = new BitbankRestApiClient(apiKey, apiSecret,
    "https://api.bitbank.cc", "https://public.bitbank.cc");

// current best bid from the public ticker; order priced at 50% of it (integer: price_digits=0)
using var httpClient = new HttpClient();
var tickerJson = httpClient.GetStringAsync($"https://public.bitbank.cc/{Pair}/ticker").Result;
var ticker = BitbankRestApiClient.ParseResponse(tickerJson, 200);
var bestBid = decimal.Parse(ticker["buy"].ToString(), System.Globalization.CultureInfo.InvariantCulture);
var orderPrice = Math.Floor(bestBid * 0.5m);

Console.WriteLine("=== bitbank order lifecycle smoke test ===");
Console.WriteLine($"  pair        : {Pair}");
Console.WriteLine($"  side/type   : buy / limit (post_only)");
Console.WriteLine($"  amount      : {Amount} BTC (minimum lot)");
Console.WriteLine($"  price       : {orderPrice} JPY (best bid {bestBid} x 0.5 — will not fill)");
Console.WriteLine($"  max exposure: ~{Math.Ceiling(Amount * orderPrice)} JPY if it somehow filled");
Console.WriteLine();
Console.Write("Press Enter to place the order, Ctrl+C to abort... ");
Console.ReadLine();

// 1) subscribe to the private stream and record events per order id
var events = new ConcurrentDictionary<string, ConcurrentQueue<string>>();
using var stream = new BitbankPrivateStreamClient(() => restClient.GetPrivateStreamCredentials());
stream.MessageReceived += (_, message) =>
{
    var method = message["method"]?.ToString();
    if (method is "spot_order_new" or "spot_order")
    {
        foreach (var order in message["params"])
        {
            var queue = events.GetOrAdd(order["order_id"].ToString(), _ => new ConcurrentQueue<string>());
            queue.Enqueue($"{method}:{order["status"]}");
            Console.WriteLine($"  [stream] {method} order={order["order_id"]} status={order["status"]}");
        }
    }
};
stream.Start();
Thread.Sleep(2000); // allow the first long-poll to establish

// 2) place the order
var placed = restClient.CreateOrder(new BitbankOrderRequest
{
    Pair = Pair,
    Amount = Amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
    Price = orderPrice.ToString(System.Globalization.CultureInfo.InvariantCulture),
    Side = "buy",
    Type = "limit",
    PostOnly = true
});
Console.WriteLine($"placed: order_id={placed.OrderId} status={placed.Status}");
var orderId = placed.OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture);

// 3) wait for the stream to confirm the new order
var confirmed = WaitFor(() => events.TryGetValue(orderId, out var q) && q.Any(e => e.Contains("UNFILLED")),
    TimeSpan.FromSeconds(15));
Console.WriteLine(confirmed
    ? "stream confirmed the new order (spot_order_new)"
    : "WARN: no stream event within 15s (order state will be verified via REST)");

// 4) cancel
var canceled = restClient.CancelOrder(Pair, placed.OrderId);
Console.WriteLine($"cancel requested: status={canceled.Status}");

// 5) wait for the cancellation event
var cancelSeen = WaitFor(() => events.TryGetValue(orderId, out var q) && q.Any(e => e.Contains("CANCELED_UNFILLED")),
    TimeSpan.FromSeconds(15));

// final REST verification
var final = restClient.GetOrder(Pair, placed.OrderId);
Console.WriteLine($"final order status via REST: {final.Status}");

var streamOk = confirmed && cancelSeen;
var restOk = final.Status == BitbankOrderStatus.CanceledUnfilled;
Console.WriteLine();
Console.WriteLine($"RESULT: order lifecycle {(restOk ? "OK" : "FAILED")}, private stream events {(streamOk ? "OK" : "MISSING")}");
return restOk ? 0 : 1;

static bool WaitFor(Func<bool> condition, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (condition())
        {
            return true;
        }
        Thread.Sleep(200);
    }
    return condition();
}
