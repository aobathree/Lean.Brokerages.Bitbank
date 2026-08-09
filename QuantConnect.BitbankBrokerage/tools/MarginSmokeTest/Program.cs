// Margin trading smoke test: verifies the full margin order lifecycle on a REAL account.
//
//   Phase 0 (read-only) : margin availability preflight — pair flags, JPY balance, open positions
//   Phase 1 (no risk)   : post-only limit BUY with position_side=long far below market
//                         -> stream confirms the order carries position_side -> cancel
//   Phase 2 (REAL trade): market BUY 0.0001 BTC position_side=long  (open long)
//                         -> /user/margin/positions shows the position
//                         market SELL 0.0001 BTC position_side=long (close long)
//                         -> position gone, realized PnL reported
//
// Phase 2 OPENS AND CLOSES A REAL MARGIN POSITION. Expected cost: two taker fees
// (0.12% x ~2,000 JPY notional x 2) + spread + at most one day of position interest
// if run across 00:00 JST — normally a few to a few tens of JPY.
//
// Requirements: margin trading review completed on the bitbank account (error 50058
// otherwise), and enough JPY collateral for a 0.0001 BTC position at 2x leverage.
//
// Run it yourself, deliberately (from the repository root, credentials via 1Password CLI):
//   op run --env-file=QuantConnect.BitbankBrokerage/.env.1password -- `
//     dotnet run --project QuantConnect.BitbankBrokerage/tools/MarginSmokeTest -- --yes
// Phase 1 only (never opens a position):
//   ... -- --yes --cancel-only
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
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
    Console.Error.WriteLine("This test places REAL margin orders and (unless --cancel-only) opens and");
    Console.Error.WriteLine("closes a REAL minimum-lot margin position on your account (cost: a few JPY).");
    Console.Error.WriteLine("Re-run with --yes to proceed:");
    Console.Error.WriteLine("  op run --env-file=QuantConnect.BitbankBrokerage/.env.1password -- " +
        "dotnet run --project QuantConnect.BitbankBrokerage/tools/MarginSmokeTest -- --yes");
    return 1;
}
var cancelOnly = args.Contains("--cancel-only");

using var restClient = new BitbankRestApiClient(apiKey, apiSecret,
    "https://api.bitbank.cc", "https://public.bitbank.cc");

// ---------------------------------------------------------------- phase 0: preflight
Console.WriteLine("=== bitbank MARGIN smoke test ===");
Console.WriteLine();
Console.WriteLine("--- phase 0: preflight (read-only) ---");

var pairInfo = restClient.GetPairs().FirstOrDefault(p => p["name"]?.ToString() == Pair);
if (pairInfo == null)
{
    Console.Error.WriteLine($"ERROR: pair {Pair} not found in /spot/pairs");
    return 1;
}
var marginLongStopped = pairInfo["stop_margin_long_order"]?.ToObject<bool?>();
Console.WriteLine($"  pair {Pair}: is_enabled={pairInfo["is_enabled"]} " +
    $"stop_margin_long_order={marginLongStopped?.ToString() ?? "?"} " +
    $"stop_margin_short_order={pairInfo["stop_margin_short_order"] ?? "?"}");
if (marginLongStopped == true)
{
    Console.Error.WriteLine("ERROR: margin long orders are currently suspended for this pair.");
    return 1;
}

var jpy = restClient.GetAssets().FirstOrDefault(a => a.Asset == "jpy");
Console.WriteLine($"  JPY balance: free={jpy?.FreeAmount ?? 0} locked={jpy?.LockedAmount ?? 0}");

List<BitbankMarginPosition> positions;
try
{
    positions = restClient.GetMarginPositions();
}
catch (BitbankApiException e) when (e.ErrorCode == 50058)
{
    Console.Error.WriteLine("ERROR: margin trading review has not been completed on this account (code 50058).");
    Console.Error.WriteLine("Complete the review in the bitbank app first.");
    return 1;
}
Console.WriteLine($"  open margin positions: {positions.Count}");
foreach (var p in positions)
{
    Console.WriteLine($"    {p.Pair} {p.PositionSide} open={p.OpenAmount} avg={p.AveragePrice}");
}
var preexistingLong = positions.Where(p => p.Pair == Pair && p.PositionSide == "long").Sum(p => p.OpenAmount);
if (preexistingLong > 0)
{
    Console.WriteLine($"  NOTE: a pre-existing {Pair} long ({preexistingLong}) is open; " +
        "phase 2 still trades a fixed 0.0001 and verifies the DELTA of open_amount.");
}

// current best bid/ask from the public ticker
using var httpClient = new HttpClient();
var tickerJson = httpClient.GetStringAsync($"https://public.bitbank.cc/{Pair}/ticker").Result;
var ticker = BitbankRestApiClient.ParseResponse(tickerJson, 200);
var bestBid = decimal.Parse(ticker["buy"].ToString(), CultureInfo.InvariantCulture);
var bestAsk = decimal.Parse(ticker["sell"].ToString(), CultureInfo.InvariantCulture);
var notional = Math.Ceiling(Amount * bestAsk);
var estimatedCost = Math.Ceiling(notional * 0.0012m * 2 + Amount * (bestAsk - bestBid));
Console.WriteLine($"  best bid/ask: {bestBid} / {bestAsk} (notional ~{notional} JPY, " +
    $"phase 2 estimated cost ~{estimatedCost} JPY: 2x taker fee + spread)");
Console.WriteLine();

// ---------------------------------------------------------------- private stream
var orderEvents = new ConcurrentDictionary<string, ConcurrentQueue<string>>();
var trades = new ConcurrentQueue<BitbankStreamTrade>();
using var stream = new BitbankPrivateStreamClient(() => restClient.GetPrivateStreamCredentials());
stream.MessageReceived += (_, message) =>
{
    var method = message["method"]?.ToString();
    switch (method)
    {
        case "spot_order_new":
        case "spot_order":
            foreach (var order in message["params"])
            {
                var queue = orderEvents.GetOrAdd(order["order_id"].ToString(), _ => new ConcurrentQueue<string>());
                queue.Enqueue($"{method}:{order["status"]}:{order["position_side"]}");
                Console.WriteLine($"  [stream] {method} order={order["order_id"]} " +
                    $"status={order["status"]} position_side={order["position_side"] ?? "(none)"}");
            }
            break;
        case "spot_trade":
            foreach (var tradeToken in message["params"])
            {
                var trade = tradeToken.ToObject<BitbankStreamTrade>();
                trades.Enqueue(trade);
                Console.WriteLine($"  [stream] spot_trade order={trade.OrderId} {trade.Side} {trade.Amount}@{trade.Price} " +
                    $"position_side={trade.PositionSide ?? "(none)"} fee_quote={trade.FeeAmountQuote} " +
                    $"pnl={(trade.ProfitLoss.HasValue ? trade.ProfitLoss.Value.ToString(CultureInfo.InvariantCulture) : "-")} " +
                    $"interest={(trade.Interest.HasValue ? trade.Interest.Value.ToString(CultureInfo.InvariantCulture) : "-")}");
            }
            break;
        case "margin_position_update":
        case "margin_payable_update":
        case "margin_notice_update":
            Console.WriteLine($"  [stream] {method}: {message["params"]}");
            break;
    }
};
stream.Start();
Thread.Sleep(2000); // allow the first long-poll to establish

// ---------------------------------------------------------------- phase 1: order lifecycle, no fill
Console.WriteLine("--- phase 1: margin order place/cancel (will not fill) ---");
var limitPrice = Math.Floor(bestBid * 0.5m);
Console.WriteLine($"  post-only limit BUY {Amount} {Pair} @ {limitPrice} (best bid x 0.5) position_side=long");
Console.Write("Press Enter to place the order, Ctrl+C to abort... ");
if (Console.ReadLine() == null)
{
    Console.Error.WriteLine("ERROR: stdin closed (non-interactive session) — aborting before placing any order.");
    return 1;
}

var phase1Order = restClient.CreateOrder(new BitbankOrderRequest
{
    Pair = Pair,
    Amount = Amount.ToString(CultureInfo.InvariantCulture),
    Price = limitPrice.ToString(CultureInfo.InvariantCulture),
    Side = "buy",
    Type = "limit",
    PostOnly = true,
    PositionSide = "long"
});
Console.WriteLine($"placed: order_id={phase1Order.OrderId} status={phase1Order.Status} position_side={phase1Order.PositionSide}");
var phase1Id = phase1Order.OrderId.ToString(CultureInfo.InvariantCulture);
var responseSideOk = phase1Order.PositionSide == "long";

var newSeen = WaitFor(() => orderEvents.TryGetValue(phase1Id, out var q) && q.Any(e => e.Contains("UNFILLED")),
    TimeSpan.FromSeconds(15));
var streamSideOk = orderEvents.TryGetValue(phase1Id, out var phase1Queue) &&
    phase1Queue.Any(e => e.EndsWith(":long", StringComparison.Ordinal));
Console.WriteLine(newSeen
    ? $"stream confirmed the new margin order (position_side {(streamSideOk ? "present" : "MISSING in stream event")})"
    : "WARN: no stream event within 15s");

restClient.CancelOrder(Pair, phase1Order.OrderId);
var cancelSeen = WaitFor(() => orderEvents.TryGetValue(phase1Id, out var q) && q.Any(e => e.Contains("CANCELED_UNFILLED")),
    TimeSpan.FromSeconds(15));
var phase1Final = restClient.GetOrder(Pair, phase1Order.OrderId);
var phase1Ok = responseSideOk && phase1Final.Status == BitbankOrderStatus.CanceledUnfilled;
Console.WriteLine($"phase 1: {(phase1Ok ? "OK" : "FAILED")} " +
    $"(final status {phase1Final.Status}, stream events {(newSeen && cancelSeen ? "OK" : "MISSING")})");
Console.WriteLine();

if (cancelOnly)
{
    Console.WriteLine("--cancel-only: skipping phase 2 (no position opened)");
    Console.WriteLine();
    Console.WriteLine($"RESULT: {(phase1Ok ? "OK" : "FAILED")}");
    return phase1Ok ? 0 : 1;
}

// ---------------------------------------------------------------- phase 2: open -> verify -> close
Console.WriteLine("--- phase 2: REAL position: open long -> verify -> close ---");
Console.WriteLine($"  market BUY {Amount} {Pair} position_side=long, then market SELL to close (~{estimatedCost} JPY cost)");
Console.Write("Press Enter to OPEN the position, Ctrl+C to abort... ");
if (Console.ReadLine() == null)
{
    Console.Error.WriteLine("ERROR: stdin closed (non-interactive session) — aborting before opening a position.");
    return 1;
}

var openOrder = restClient.CreateOrder(new BitbankOrderRequest
{
    Pair = Pair,
    Amount = Amount.ToString(CultureInfo.InvariantCulture),
    Side = "buy",
    Type = "market",
    PositionSide = "long"
});
Console.WriteLine($"open order: order_id={openOrder.OrderId} status={openOrder.Status}");

// market orders fill near-instantly; confirm via REST polling (stream events arrive in parallel)
var openFilled = WaitFor(() =>
    restClient.GetOrder(Pair, openOrder.OrderId).Status == BitbankOrderStatus.FullyFilled,
    TimeSpan.FromSeconds(20));
if (!openFilled)
{
    Console.Error.WriteLine("ERROR: open order did not reach FULLY_FILLED within 20s.");
    Console.Error.WriteLine($"Check order {openOrder.OrderId} and any open position in the bitbank app and close it manually.");
    return 1;
}
var openFinal = restClient.GetOrder(Pair, openOrder.OrderId);
Console.WriteLine($"open filled: avg price={openFinal.AveragePrice} executed={openFinal.ExecutedAmount}");

// verify the position book: long open_amount must have grown by Amount
var positionSeen = WaitFor(() =>
    restClient.GetMarginPositions().Where(p => p.Pair == Pair && p.PositionSide == "long")
        .Sum(p => p.OpenAmount) >= preexistingLong + Amount,
    TimeSpan.FromSeconds(15));
var afterOpenLong = restClient.GetMarginPositions()
    .Where(p => p.Pair == Pair && p.PositionSide == "long").Sum(p => p.OpenAmount);
Console.WriteLine($"/user/margin/positions long open_amount: {preexistingLong} -> {afterOpenLong} " +
    $"({(positionSeen ? "OK" : "MISMATCH")})");

// close the long: SELL with position_side=long
Console.WriteLine($"  market SELL {Amount} {Pair} position_side=long (close)");
BitbankOrder closeOrder;
try
{
    closeOrder = restClient.CreateOrder(new BitbankOrderRequest
    {
        Pair = Pair,
        Amount = Amount.ToString(CultureInfo.InvariantCulture),
        Side = "sell",
        Type = "market",
        PositionSide = "long"
    });
}
catch (Exception e)
{
    Console.Error.WriteLine($"ERROR: close order failed: {e.Message}");
    Console.Error.WriteLine($"A LONG POSITION OF {Amount} {Pair} IS STILL OPEN — close it manually in the bitbank app.");
    return 1;
}
Console.WriteLine($"close order: order_id={closeOrder.OrderId} status={closeOrder.Status}");

var closeFilled = WaitFor(() =>
    restClient.GetOrder(Pair, closeOrder.OrderId).Status == BitbankOrderStatus.FullyFilled,
    TimeSpan.FromSeconds(20));
if (!closeFilled)
{
    Console.Error.WriteLine("ERROR: close order did not reach FULLY_FILLED within 20s.");
    Console.Error.WriteLine($"A LONG POSITION OF {Amount} {Pair} MAY STILL BE OPEN — check the bitbank app.");
    return 1;
}
var closeFinal = restClient.GetOrder(Pair, closeOrder.OrderId);
Console.WriteLine($"close filled: avg price={closeFinal.AveragePrice} executed={closeFinal.ExecutedAmount}");

// verify the position book is back to where it started
var positionClosed = WaitFor(() =>
    restClient.GetMarginPositions().Where(p => p.Pair == Pair && p.PositionSide == "long")
        .Sum(p => p.OpenAmount) <= preexistingLong,
    TimeSpan.FromSeconds(15));
var afterCloseLong = restClient.GetMarginPositions()
    .Where(p => p.Pair == Pair && p.PositionSide == "long").Sum(p => p.OpenAmount);
Console.WriteLine($"/user/margin/positions long open_amount: {afterOpenLong} -> {afterCloseLong} " +
    $"({(positionClosed ? "OK" : "STILL OPEN?")})");

// give the stream a moment to deliver the close trade, then summarize costs.
// bitbank margin semantics (verified live 2026-08-09): the open fill carries fee 0 (its fee is
// deferred as the position's unrealized_fee), the close fill's fee_amount_quote bills open+close
// fees together, and profit_loss is the realized PnL NET of those fees and interest.
WaitFor(() => trades.Any(t => t.OrderId == closeOrder.OrderId), TimeSpan.FromSeconds(10));
var myTrades = trades.Where(t => t.OrderId == openOrder.OrderId || t.OrderId == closeOrder.OrderId).ToList();
var totalFees = myTrades.Sum(t => t.FeeAmountQuote);
var realizedPnl = myTrades.Sum(t => t.ProfitLoss ?? 0);
var interest = myTrades.Sum(t => t.Interest ?? 0);
var tradeSideOk = myTrades.Count == 0 || myTrades.All(t => t.PositionSide == "long");
Console.WriteLine();
Console.WriteLine($"round trip summary ({myTrades.Count} stream trades): " +
    $"net realized PnL={realizedPnl} JPY (already net of fees={totalFees} JPY and interest={interest} JPY)");

var phase2Ok = openFilled && closeFilled && positionSeen && positionClosed && tradeSideOk;
Console.WriteLine();
Console.WriteLine($"RESULT: phase 1 {(phase1Ok ? "OK" : "FAILED")}, phase 2 {(phase2Ok ? "OK" : "FAILED")}");
return phase1Ok && phase2Ok ? 0 : 1;

static bool WaitFor(Func<bool> condition, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (condition())
        {
            return true;
        }
        Thread.Sleep(500);
    }
    return condition();
}
