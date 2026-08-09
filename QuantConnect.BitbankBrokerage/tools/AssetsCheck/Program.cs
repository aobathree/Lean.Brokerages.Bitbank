// One-shot bitbank private API connectivity check: fetches /v1/user/assets and
// active orders using credentials from BITBANK_API_KEY / BITBANK_API_SECRET.
// Run via: op run --env-file=.env.1password -- dotnet run --project tools/AssetsCheck
using System;
using QuantConnect.Brokerages.Bitbank.Api;

var apiKey = Environment.GetEnvironmentVariable("BITBANK_API_KEY");
var apiSecret = Environment.GetEnvironmentVariable("BITBANK_API_SECRET");

if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
{
    Console.Error.WriteLine("ERROR: BITBANK_API_KEY / BITBANK_API_SECRET are not set.");
    Console.Error.WriteLine("Run via: op run --env-file=.env.1password -- dotnet run --project tools/AssetsCheck");
    return 1;
}

using var client = new BitbankRestApiClient(apiKey, apiSecret,
    "https://api.bitbank.cc", "https://public.bitbank.cc");

try
{
    Console.WriteLine("== GET /v1/user/assets ==");
    foreach (var asset in client.GetAssets())
    {
        if (asset.OnhandAmount != 0 || asset.Asset is "jpy" or "btc")
        {
            Console.WriteLine($"  {asset.Asset,-8} onhand={asset.OnhandAmount} free={asset.FreeAmount} locked={asset.LockedAmount}");
        }
    }

    Console.WriteLine("== GET /v1/user/spot/active_orders ==");
    var orders = client.GetActiveOrders();
    Console.WriteLine($"  active orders: {orders.Count}");
    foreach (var order in orders)
    {
        Console.WriteLine($"  #{order.OrderId} {order.Pair} {order.Side} {order.Type} remaining={order.RemainingAmount} price={order.Price} status={order.Status}");
    }

    Console.WriteLine("== GET /v1/user/subscribe (private stream credentials) ==");
    var (channel, token) = client.GetPrivateStreamCredentials();
    Console.WriteLine($"  pubnub_channel={channel}");
    Console.WriteLine($"  pubnub_token={token[..Math.Min(8, token.Length)]}... (len={token.Length})");

    Console.WriteLine("OK: private API connectivity verified.");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"FAILED: {e.Message}");
    return 1;
}
