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
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Logging;

namespace QuantConnect.Brokerages.Bitbank
{
    /// <summary>
    /// History via the public candlestick endpoint. Intraday candle types are paged per day
    /// (JST date key), daily candles per year.
    /// </summary>
    public partial class BitbankBrokerage
    {
        // bitbank candlestick date keys use JST (UTC+9)
        private static readonly TimeSpan JstOffset = TimeSpan.FromHours(9);
        private bool _historyWarningFired;

        /// <summary>
        /// Gets the history for the requested security
        /// </summary>
        public override IEnumerable<BaseData> GetHistory(HistoryRequest request)
        {
            if (request.Symbol.SecurityType != SecurityType.Crypto || request.Symbol.ID.Market != BitbankMarket.Name)
            {
                if (!_historyWarningFired)
                {
                    _historyWarningFired = true;
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidHistoryRequest",
                        "BitbankBrokerage.GetHistory(): only bitbank Crypto symbols are supported"));
                }
                return null;
            }

            if (request.TickType != TickType.Trade)
            {
                return null;
            }

            string candleType;
            switch (request.Resolution)
            {
                case Resolution.Minute:
                    candleType = "1min";
                    break;
                case Resolution.Hour:
                    candleType = "1hour";
                    break;
                case Resolution.Daily:
                    candleType = "1day";
                    break;
                default:
                    if (!_historyWarningFired)
                    {
                        _historyWarningFired = true;
                        OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidHistoryRequest",
                            $"BitbankBrokerage.GetHistory(): unsupported resolution {request.Resolution}, use Minute, Hour or Daily"));
                    }
                    return null;
            }

            return GetCandleHistory(request, candleType);
        }

        private IEnumerable<BaseData> GetCandleHistory(HistoryRequest request, string candleType)
        {
            var pair = _symbolMapper.GetBrokerageSymbol(request.Symbol);
            var period = request.Resolution.ToTimeSpan();
            var startUtc = request.StartTimeUtc;
            var endUtc = request.EndTimeUtc;

            foreach (var dateKey in GetDateKeys(startUtc, endUtc, request.Resolution))
            {
                List<Messages.BitbankCandle> candles;
                try
                {
                    candles = _restApiClient.GetCandlesticks(pair, candleType, dateKey);
                }
                catch (Exception e)
                {
                    Log.Error(e, $"BitbankBrokerage.GetHistory({pair}, {candleType}, {dateKey})");
                    continue;
                }

                foreach (var candle in candles)
                {
                    var barTimeUtc = QuantConnect.Time.UnixMillisecondTimeStampToDateTime(candle.Timestamp);
                    if (barTimeUtc < startUtc || barTimeUtc + period > endUtc)
                    {
                        continue;
                    }

                    yield return new TradeBar(
                        barTimeUtc.ConvertFromUtc(request.ExchangeHours.TimeZone),
                        request.Symbol,
                        candle.Open,
                        candle.High,
                        candle.Low,
                        candle.Close,
                        candle.Volume,
                        period);
                }
            }
        }

        /// <summary>
        /// Enumerates the candlestick endpoint date keys (JST-based) covering the requested UTC range
        /// </summary>
        public static IEnumerable<string> GetDateKeys(DateTime startUtc, DateTime endUtc, Resolution resolution)
        {
            var startJst = startUtc + JstOffset;
            var endJst = endUtc + JstOffset;

            if (resolution == Resolution.Daily)
            {
                for (var year = startJst.Year; year <= endJst.Year; year++)
                {
                    yield return year.ToStringInvariant();
                }
            }
            else
            {
                for (var date = startJst.Date; date <= endJst.Date; date = date.AddDays(1))
                {
                    yield return date.ToStringInvariant("yyyyMMdd");
                }
            }
        }
    }
}
