// Private stream (PubNub) reception check: subscribes to the user's private channel
// and prints every message method for a fixed duration. Read-only, no orders.
// Run via: op run --env-file=.env.1password -- dotnet run --project tools/StreamCheck [seconds]
using System;
using System.Threading;
using QuantConnect.Brokerages.Bitbank.Api;
using QuantConnect.Brokerages.Bitbank.Streaming;
using QuantConnect.Logging;

var apiKey = Environment.GetEnvironmentVariable("BITBANK_API_KEY");
var apiSecret = Environment.GetEnvironmentVariable("BITBANK_API_SECRET");
var durationSeconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 60;

if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
{
    Console.Error.WriteLine("ERROR: BITBANK_API_KEY / BITBANK_API_SECRET are not set.");
    Console.Error.WriteLine("Run via: op run --env-file=.env.1password -- dotnet run --project tools/StreamCheck");
    return 1;
}

// surface the connector's internal trace logs (credential fetch, reconnects) on the console
Log.LogHandler = new ConsoleLogHandler();

using var restClient = new BitbankRestApiClient(apiKey, apiSecret,
    "https://api.bitbank.cc", "https://public.bitbank.cc");

var messageCount = 0;
using var stream = new BitbankPrivateStreamClient(() => restClient.GetPrivateStreamCredentials());
stream.MessageReceived += (_, message) =>
{
    Interlocked.Increment(ref messageCount);
    Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] method={message["method"]} params={message["params"]?.ToString(Newtonsoft.Json.Formatting.None)}");
};

Console.WriteLine($"Subscribing to the private stream for {durationSeconds}s...");
Console.WriteLine("Tip: while this runs, any account activity (placing/canceling an order from the");
Console.WriteLine("bitbank app, deposits, etc.) will appear here as spot_order / asset_update events.");
stream.Start();

var deadline = DateTime.UtcNow.AddSeconds(durationSeconds);
while (DateTime.UtcNow < deadline)
{
    Thread.Sleep(500);
    if (!stream.IsRunning)
    {
        Console.Error.WriteLine("FAILED: private stream loop stopped unexpectedly.");
        return 1;
    }
}

Console.WriteLine($"OK: stream stayed connected for {durationSeconds}s, received {messageCount} message(s).");
Console.WriteLine("(0 messages is normal when there was no account activity during the window.)");
return 0;
