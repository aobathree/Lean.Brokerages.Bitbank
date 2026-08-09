// Downloads bitbank public candlestick data and writes it in Lean's crypto
// data format (Data/crypto/bitbank/{daily|hour}/{symbol}_trade.zip).
//
// Usage:
//   dotnet run --project tools/CandleDownloader -- \
//     --pairs btc_jpy,xrp_jpy --resolution daily --from 2018 --data-dir Data
//
//   --resolution daily : fetches 1day candles (one request per year)
//   --resolution hour  : fetches 1hour candles (one request per day; slow)
//   --from             : first year (daily) or first date yyyyMMdd (hour)
//
// Public API only; no credentials required. Existing zips are overwritten.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

var pairs = new List<string> { "btc_jpy", "xrp_jpy", "eth_jpy", "sol_jpy", "doge_jpy", "xlm_jpy", "ada_jpy", "ltc_jpy" };
var resolution = "daily";
var from = "2018";
var dataDir = "Data";   // relative to the Lean repo root

for (var i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--pairs": pairs = args[i + 1].Split(',').Select(p => p.Trim()).ToList(); break;
        case "--resolution": resolution = args[i + 1]; break;
        case "--from": from = args[i + 1]; break;
        case "--data-dir": dataDir = args[i + 1]; break;
    }
}

if (resolution != "daily" && resolution != "hour")
{
    Console.Error.WriteLine("ERROR: --resolution must be 'daily' or 'hour'");
    return 1;
}

var outputDir = Path.Combine(dataDir, "crypto", "bitbank", resolution);
Directory.CreateDirectory(outputDir);

using var http = new HttpClient { BaseAddress = new Uri("https://public.bitbank.cc") };
http.Timeout = TimeSpan.FromSeconds(30);

var utcToday = DateTime.UtcNow.Date;

foreach (var pair in pairs)
{
    var symbol = pair.Replace("_", "");   // btc_jpy -> btcjpy (Lean file symbol)
    var lines = new SortedDictionary<long, string>();
    var requests = BuildRequestDates(resolution, from, utcToday);
    var missing = 0;

    foreach (var date in requests)
    {
        var candleType = resolution == "daily" ? "1day" : "1hour";
        var url = $"/{pair}/candlestick/{candleType}/{date}";
        JsonDocument doc;
        try
        {
            var body = await FetchWithRetryAsync(http, url);
            if (body == null) { missing++; continue; }
            doc = JsonDocument.Parse(body);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  {pair} {date}: {ex.Message} (skipped)");
            missing++;
            continue;
        }

        using (doc)
        {
            if (doc.RootElement.GetProperty("success").GetInt32() != 1) { missing++; continue; }
            var sticks = doc.RootElement.GetProperty("data").GetProperty("candlestick");
            if (sticks.GetArrayLength() == 0) { missing++; continue; }

            foreach (var row in sticks[0].GetProperty("ohlcv").EnumerateArray())
            {
                // [open, high, low, close, volume, unixtime_ms] (numbers arrive as strings)
                var ts = row[5].GetInt64();
                var t = DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime;
                if (t >= utcToday) continue;   // drop today's incomplete bar
                var line = string.Join(",",
                    t.ToString("yyyyMMdd HH:mm", CultureInfo.InvariantCulture),
                    Num(row[0]), Num(row[1]), Num(row[2]), Num(row[3]), Num(row[4]));
                lines[ts] = line;
            }
        }
        // stay well under public API tolerance
        await Task.Delay(resolution == "daily" ? 150 : 120);
    }

    if (lines.Count == 0)
    {
        Console.WriteLine($"{pair}: no data (pair may not have existed yet) — skipped");
        continue;
    }

    var zipPath = Path.Combine(outputDir, $"{symbol}_trade.zip");
    File.Delete(zipPath);
    using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
    {
        var entry = zip.CreateEntry($"{symbol}.csv");
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        foreach (var line in lines.Values) writer.WriteLine(line);
    }

    var first = DateTimeOffset.FromUnixTimeMilliseconds(lines.Keys.First()).UtcDateTime;
    var last = DateTimeOffset.FromUnixTimeMilliseconds(lines.Keys.Last()).UtcDateTime;
    Console.WriteLine($"{pair}: {lines.Count} bars {first:yyyy-MM-dd} .. {last:yyyy-MM-dd} -> {zipPath}" +
                      (missing > 0 ? $" ({missing} empty periods)" : ""));
}

return 0;

static IEnumerable<string> BuildRequestDates(string resolution, string from, DateTime utcToday)
{
    if (resolution == "daily")
    {
        for (var y = int.Parse(from); y <= utcToday.Year; y++) yield return y.ToString();
    }
    else
    {
        var d = DateTime.ParseExact(from, "yyyyMMdd", CultureInfo.InvariantCulture);
        for (; d <= utcToday; d = d.AddDays(1)) yield return d.ToString("yyyyMMdd");
    }
}

static async Task<string?> FetchWithRetryAsync(HttpClient http, string url)
{
    for (var attempt = 1; ; attempt++)
    {
        var response = await http.GetAsync(url);
        if (response.IsSuccessStatusCode) return await response.Content.ReadAsStringAsync();
        if ((int)response.StatusCode == 404) return null;
        if (attempt >= 3) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
        await Task.Delay(1000 * attempt * attempt);
    }
}

static string Num(JsonElement e) => e.ValueKind == JsonValueKind.String
    ? e.GetString()!
    : e.GetRawText();
