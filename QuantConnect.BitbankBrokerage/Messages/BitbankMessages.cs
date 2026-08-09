/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace QuantConnect.Brokerages.Bitbank.Messages
{
    /// <summary>
    /// bitbank order statuses
    /// </summary>
    public static class BitbankOrderStatus
    {
#pragma warning disable 1591
        public const string Inactive = "INACTIVE";
        public const string Unfilled = "UNFILLED";
        public const string PartiallyFilled = "PARTIALLY_FILLED";
        public const string FullyFilled = "FULLY_FILLED";
        public const string CanceledUnfilled = "CANCELED_UNFILLED";
        public const string CanceledPartiallyFilled = "CANCELED_PARTIALLY_FILLED";
        public const string Rejected = "REJECTED";
#pragma warning restore 1591
    }

    /// <summary>
    /// Exception carrying a bitbank API error code, see
    /// https://github.com/bitbankinc/bitbank-api-docs/blob/master/errors.md
    /// </summary>
    public class BitbankApiException : Exception
    {
        /// <summary>
        /// bitbank error code, 0 when the failure was not a bitbank error envelope
        /// </summary>
        public int ErrorCode { get; }

        /// <summary>
        /// Creates a new exception for the given bitbank error code
        /// </summary>
        public BitbankApiException(int errorCode, string message) : base(message)
        {
            ErrorCode = errorCode;
        }
    }

    /// <summary>
    /// A single asset balance from GET /v1/user/assets
    /// </summary>
    public class BitbankAsset
    {
#pragma warning disable 1591
        [JsonProperty("asset")]
        public string Asset { get; set; }

        [JsonProperty("free_amount")]
        public decimal FreeAmount { get; set; }

        [JsonProperty("locked_amount")]
        public decimal LockedAmount { get; set; }

        [JsonProperty("onhand_amount")]
        public decimal OnhandAmount { get; set; }
#pragma warning restore 1591
    }

    /// <summary>
    /// Order representation shared by the REST order endpoints and the private stream
    /// spot_order_new / spot_order messages
    /// </summary>
    public class BitbankOrder
    {
#pragma warning disable 1591
        [JsonProperty("order_id")]
        public long OrderId { get; set; }

        [JsonProperty("pair")]
        public string Pair { get; set; }

        [JsonProperty("side")]
        public string Side { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("start_amount")]
        public decimal? StartAmount { get; set; }

        [JsonProperty("remaining_amount")]
        public decimal? RemainingAmount { get; set; }

        [JsonProperty("executed_amount")]
        public decimal? ExecutedAmount { get; set; }

        [JsonProperty("price")]
        public decimal? Price { get; set; }

        [JsonProperty("trigger_price")]
        public decimal? TriggerPrice { get; set; }

        [JsonProperty("average_price")]
        public decimal? AveragePrice { get; set; }

        [JsonProperty("post_only")]
        public bool? PostOnly { get; set; }

        [JsonProperty("ordered_at")]
        public long OrderedAt { get; set; }

        [JsonProperty("canceled_at")]
        public long? CanceledAt { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }
#pragma warning restore 1591
    }

    /// <summary>
    /// Request body for POST /v1/user/spot/order
    /// </summary>
    public class BitbankOrderRequest
    {
#pragma warning disable 1591
        [JsonProperty("pair")]
        public string Pair { get; set; }

        [JsonProperty("amount")]
        public string Amount { get; set; }

        [JsonProperty("price", NullValueHandling = NullValueHandling.Ignore)]
        public string Price { get; set; }

        [JsonProperty("side")]
        public string Side { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("post_only", NullValueHandling = NullValueHandling.Ignore)]
        public bool? PostOnly { get; set; }

        [JsonProperty("trigger_price", NullValueHandling = NullValueHandling.Ignore)]
        public string TriggerPrice { get; set; }
#pragma warning restore 1591
    }

    /// <summary>
    /// A private stream spot_trade message entry (one execution)
    /// </summary>
    public class BitbankStreamTrade
    {
#pragma warning disable 1591
        [JsonProperty("trade_id")]
        public long TradeId { get; set; }

        [JsonProperty("order_id")]
        public long OrderId { get; set; }

        [JsonProperty("pair")]
        public string Pair { get; set; }

        [JsonProperty("side")]
        public string Side { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("price")]
        public decimal Price { get; set; }

        [JsonProperty("amount")]
        public decimal Amount { get; set; }

        [JsonProperty("maker_taker")]
        public string MakerTaker { get; set; }

        [JsonProperty("fee_amount_base")]
        public decimal FeeAmountBase { get; set; }

        [JsonProperty("fee_amount_quote")]
        public decimal FeeAmountQuote { get; set; }

        [JsonProperty("executed_at")]
        public long ExecutedAt { get; set; }
#pragma warning restore 1591
    }

    /// <summary>
    /// A public stream transaction entry from transactions_{pair}
    /// </summary>
    public class BitbankTransaction
    {
#pragma warning disable 1591
        [JsonProperty("transaction_id")]
        public long TransactionId { get; set; }

        [JsonProperty("side")]
        public string Side { get; set; }

        [JsonProperty("price")]
        public decimal Price { get; set; }

        [JsonProperty("amount")]
        public decimal Amount { get; set; }

        [JsonProperty("executed_at")]
        public long ExecutedAt { get; set; }
#pragma warning restore 1591
    }

    /// <summary>
    /// depth_diff_{pair} message: absolute amounts, "0" removes the level
    /// </summary>
    public class BitbankDepthDiff
    {
#pragma warning disable 1591
        [JsonProperty("a")]
        public List<string[]> Asks { get; set; }

        [JsonProperty("b")]
        public List<string[]> Bids { get; set; }

        [JsonProperty("t")]
        public long Timestamp { get; set; }

        [JsonProperty("s")]
        public long Sequence { get; set; }
#pragma warning restore 1591
    }

    /// <summary>
    /// depth_whole_{pair} message: full snapshot of up to 200 levels per side
    /// </summary>
    public class BitbankDepthWhole
    {
#pragma warning disable 1591
        [JsonProperty("asks")]
        public List<string[]> Asks { get; set; }

        [JsonProperty("bids")]
        public List<string[]> Bids { get; set; }

        [JsonProperty("timestamp")]
        public long Timestamp { get; set; }

        [JsonProperty("sequenceId")]
        public long SequenceId { get; set; }
#pragma warning restore 1591
    }

    /// <summary>
    /// A single candle from the public candlestick endpoint: [open, high, low, close, volume, unixtime_ms]
    /// </summary>
    public class BitbankCandle
    {
#pragma warning disable 1591
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public decimal Volume { get; set; }
        public long Timestamp { get; set; }
#pragma warning restore 1591
    }
}
