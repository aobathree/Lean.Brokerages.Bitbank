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
using System.Collections.Concurrent;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using QuantConnect.Brokerages.Bitbank.Messages;
using QuantConnect.Brokerages.Bitbank.Streaming;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Packets;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.Bitbank
{
    /// <summary>
    /// IDataQueueHandler implementation: live trade ticks from transactions_{pair} and
    /// quote ticks from a local order book fed by depth_whole_{pair}/depth_diff_{pair}
    /// </summary>
    public partial class BitbankBrokerage
    {
        private readonly ConcurrentDictionary<string, BitbankOrderBookManager> _orderBooks = new();
        private readonly ConcurrentDictionary<string, Symbol> _subscribedPairs = new();
        private readonly object _tickLocker = new();

        /// <summary>
        /// Sets the job we're subscribing for; initializes the instance when the engine
        /// created it via Composer for standalone data-queue-handler use
        /// </summary>
        public void SetJob(LiveNodePacket job)
        {
            var aggregator = _aggregator ?? Composer.Instance.GetPart<IDataAggregator>() ??
                Composer.Instance.GetExportedValueByTypeName<IDataAggregator>(
                    Config.Get("data-aggregator", "QuantConnect.Lean.Engine.DataFeeds.AggregationManager"), false);

            Initialize(
                job.BrokerageData.TryGetValue("bitbank-api-key", out var apiKey) && !string.IsNullOrEmpty(apiKey)
                    ? apiKey : BitbankBrokerageFactory.GetCredential("bitbank-api-key", "BITBANK_API_KEY"),
                job.BrokerageData.TryGetValue("bitbank-api-secret", out var apiSecret) && !string.IsNullOrEmpty(apiSecret)
                    ? apiSecret : BitbankBrokerageFactory.GetCredential("bitbank-api-secret", "BITBANK_API_SECRET"),
                job.BrokerageData.TryGetValue("bitbank-rest-url", out var restUrl) ? restUrl : Config.Get("bitbank-rest-url", "https://api.bitbank.cc"),
                job.BrokerageData.TryGetValue("bitbank-public-url", out var publicUrl) ? publicUrl : Config.Get("bitbank-public-url", "https://public.bitbank.cc"),
                job.BrokerageData.TryGetValue("bitbank-websocket-url", out var wsUrl) ? wsUrl : Config.Get("bitbank-websocket-url", "wss://stream.bitbank.cc"),
                _orderProvider,
                aggregator);

            if (!IsConnected)
            {
                Connect();
            }
        }

        /// <summary>
        /// Subscribe to the specified configuration
        /// </summary>
        public IEnumerator<BaseData> Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
        {
            if (!CanSubscribe(dataConfig.Symbol))
            {
                return null;
            }

            var enumerator = _aggregator.Add(dataConfig, newDataAvailableHandler);
            _subscriptionManager.Subscribe(dataConfig);
            return enumerator;
        }

        /// <summary>
        /// Removes the specified configuration
        /// </summary>
        public void Unsubscribe(SubscriptionDataConfig dataConfig)
        {
            _subscriptionManager.Unsubscribe(dataConfig);
            _aggregator.Remove(dataConfig);
        }

        private static bool CanSubscribe(Symbol symbol)
        {
            return symbol.SecurityType == SecurityType.Crypto &&
                   symbol.ID.Market == BitbankMarket.Name &&
                   !symbol.Value.Contains("UNIVERSE", StringComparison.InvariantCulture);
        }

        private bool SubscribeChannels(IEnumerable<Symbol> symbols, TickType tickType)
        {
            foreach (var symbol in symbols)
            {
                var pair = _symbolMapper.GetBrokerageSymbol(symbol);
                _subscribedPairs[pair] = symbol;

                if (tickType == TickType.Trade)
                {
                    Log.Trace($"BitbankBrokerage.SubscribeChannels(): joining transactions_{pair}");
                    _socketClient.JoinRoom($"transactions_{pair}");
                }
                else
                {
                    var manager = _orderBooks.GetOrAdd(pair, _ =>
                    {
                        var newManager = new BitbankOrderBookManager(symbol);
                        newManager.OrderBook.BestBidAskUpdated += OnBestBidAskUpdated;
                        return newManager;
                    });
                    Log.Trace($"BitbankBrokerage.SubscribeChannels(): joining depth_whole_{pair} and depth_diff_{pair}");
                    _socketClient.JoinRoom($"depth_whole_{pair}");
                    _socketClient.JoinRoom($"depth_diff_{pair}");
                }
            }
            return true;
        }

        private bool UnsubscribeChannels(IEnumerable<Symbol> symbols, TickType tickType)
        {
            foreach (var symbol in symbols)
            {
                var pair = _symbolMapper.GetBrokerageSymbol(symbol);

                if (tickType == TickType.Trade)
                {
                    _socketClient.ForgetRoom($"transactions_{pair}");
                }
                else
                {
                    _socketClient.ForgetRoom($"depth_whole_{pair}");
                    _socketClient.ForgetRoom($"depth_diff_{pair}");
                    if (_orderBooks.TryRemove(pair, out var manager))
                    {
                        manager.OrderBook.BestBidAskUpdated -= OnBestBidAskUpdated;
                    }
                }

                if (!_subscriptionManager.IsSubscribed(symbol, TickType.Trade) &&
                    !_subscriptionManager.IsSubscribed(symbol, TickType.Quote))
                {
                    _subscribedPairs.TryRemove(pair, out _);
                }
            }
            return true;
        }

        private readonly ConcurrentDictionary<string, byte> _roomsSeen = new();

        private void OnStreamMessage(object sender, (string Room, JToken Data) e)
        {
            try
            {
                if (_roomsSeen.TryAdd(e.Room, 0))
                {
                    Log.Trace($"BitbankBrokerage.OnStreamMessage(): first message received for room {e.Room}");
                }
                if (e.Room.StartsWith("transactions_", StringComparison.Ordinal))
                {
                    HandleTransactions(e.Room.Substring("transactions_".Length), e.Data);
                }
                else if (e.Room.StartsWith("depth_whole_", StringComparison.Ordinal))
                {
                    var pair = e.Room.Substring("depth_whole_".Length);
                    if (_orderBooks.TryGetValue(pair, out var manager))
                    {
                        manager.HandleWhole(e.Data.ToObject<BitbankDepthWhole>());
                    }
                }
                else if (e.Room.StartsWith("depth_diff_", StringComparison.Ordinal))
                {
                    var pair = e.Room.Substring("depth_diff_".Length);
                    if (_orderBooks.TryGetValue(pair, out var manager))
                    {
                        manager.HandleDiff(e.Data.ToObject<BitbankDepthDiff>());
                    }
                }
            }
            catch (Exception exception)
            {
                Log.Error(exception, $"BitbankBrokerage.OnStreamMessage({e.Room})");
            }
        }

        private void HandleTransactions(string pair, JToken data)
        {
            if (!_subscribedPairs.TryGetValue(pair, out var symbol))
            {
                return;
            }

            foreach (var transactionToken in (JArray)data["transactions"])
            {
                var transaction = transactionToken.ToObject<BitbankTransaction>();
                var tick = new Tick
                {
                    Symbol = symbol,
                    Time = QuantConnect.Time.UnixMillisecondTimeStampToDateTime(transaction.ExecutedAt),
                    TickType = TickType.Trade,
                    Value = transaction.Price,
                    Quantity = transaction.Amount
                };
                lock (_tickLocker)
                {
                    _aggregator.Update(tick);
                }
            }
        }

        private void OnBestBidAskUpdated(object sender, BestBidAskUpdatedEventArgs e)
        {
            var tick = new Tick
            {
                Symbol = e.Symbol,
                Time = DateTime.UtcNow,
                TickType = TickType.Quote,
                BidPrice = e.BestBidPrice,
                BidSize = e.BestBidSize,
                AskPrice = e.BestAskPrice,
                AskSize = e.BestAskSize,
                Value = (e.BestBidPrice + e.BestAskPrice) / 2m
            };
            lock (_tickLocker)
            {
                _aggregator.Update(tick);
            }
        }
    }
}
