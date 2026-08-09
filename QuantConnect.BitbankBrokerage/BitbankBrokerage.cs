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
using System.Linq;
using Newtonsoft.Json.Linq;
using QuantConnect.Brokerages.Bitbank.Api;
using QuantConnect.Brokerages.Bitbank.Messages;
using QuantConnect.Brokerages.Bitbank.Streaming;
using QuantConnect.Data;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.Bitbank
{
    /// <summary>
    /// bitbank (bitbank.cc) brokerage implementation: spot trading on JPY-quoted pairs.
    /// Orders and balances use the private REST API, order/fill events arrive over the
    /// PubNub private stream, and market data over the Socket.IO public stream.
    /// </summary>
    [BrokerageFactory(typeof(BitbankBrokerageFactory))]
    public partial class BitbankBrokerage : Brokerage, IDataQueueHandler
    {
        private BitbankRestApiClient _restApiClient;
        private BitbankSocketIoClient _socketClient;
        private BitbankPrivateStreamClient _privateStreamClient;
        private ISymbolMapper _symbolMapper;
        private IOrderProvider _orderProvider;
        private IDataAggregator _aggregator;
        private BrokerageConcurrentMessageHandler<JObject> _messageHandler;
        private EventBasedDataQueueHandlerSubscriptionManager _subscriptionManager;

        // per bitbank order id: cumulative filled quantity, used to derive PartiallyFilled vs Filled
        private readonly ConcurrentDictionary<long, decimal> _fills = new();
        // bitbank order ids for which a terminal order event (Filled/Canceled/Invalid) was already emitted
        private readonly ConcurrentDictionary<long, byte> _closedOrders = new();

        private bool _isInitialized;

        /// <summary>
        /// Parameterless constructor for Composer discovery; initialization happens in <see cref="SetJob"/>
        /// </summary>
        public BitbankBrokerage() : base("Bitbank")
        {
        }

        /// <summary>
        /// Creates and initializes a new instance
        /// </summary>
        /// <param name="apiKey">bitbank API key (empty for data-only use)</param>
        /// <param name="apiSecret">bitbank API secret</param>
        /// <param name="restUrl">Private REST host, e.g. https://api.bitbank.cc</param>
        /// <param name="publicUrl">Public data host, e.g. https://public.bitbank.cc</param>
        /// <param name="webSocketUrl">Public stream host, e.g. wss://stream.bitbank.cc</param>
        /// <param name="orderProvider">Lean order provider used to resolve orders by brokerage id</param>
        /// <param name="aggregator">Data aggregator for live ticks</param>
        public BitbankBrokerage(string apiKey, string apiSecret, string restUrl, string publicUrl,
            string webSocketUrl, IOrderProvider orderProvider, IDataAggregator aggregator)
            : base("Bitbank")
        {
            Initialize(apiKey, apiSecret, restUrl, publicUrl, webSocketUrl, orderProvider, aggregator);
        }

        private void Initialize(string apiKey, string apiSecret, string restUrl, string publicUrl,
            string webSocketUrl, IOrderProvider orderProvider, IDataAggregator aggregator)
        {
            if (_isInitialized)
            {
                return;
            }

            AccountBaseCurrency = Currencies.JPY;

            _restApiClient = new BitbankRestApiClient(apiKey, apiSecret, restUrl, publicUrl);
            _orderProvider = orderProvider;
            _aggregator = aggregator;
            _symbolMapper = new SymbolPropertiesDatabaseSymbolMapper(BitbankMarket.Name);
            _messageHandler = new BrokerageConcurrentMessageHandler<JObject>(ProcessPrivateMessage);

            _socketClient = new BitbankSocketIoClient(webSocketUrl);
            _socketClient.MessageReceived += OnStreamMessage;

            if (_restApiClient.HasCredentials)
            {
                _privateStreamClient = new BitbankPrivateStreamClient(() => _restApiClient.GetPrivateStreamCredentials());
                _privateStreamClient.MessageReceived += (_, message) => _messageHandler.HandleNewMessage(message);
            }

            // distinct channel per tick type: Trade and Quote must each trigger SubscribeImpl
            // (the default constructor maps every tick type to a single shared channel)
            _subscriptionManager = new EventBasedDataQueueHandlerSubscriptionManager(tickType => tickType.ToString());
            _subscriptionManager.SubscribeImpl = (symbols, tickType) => SubscribeChannels(symbols, tickType);
            _subscriptionManager.UnsubscribeImpl = (symbols, tickType) => UnsubscribeChannels(symbols, tickType);

            _isInitialized = true;
        }

        /// <summary>
        /// True when the public stream is connected and, if credentials were supplied,
        /// the private stream polling loop is running
        /// </summary>
        public override bool IsConnected =>
            (_socketClient?.IsConnected ?? false) &&
            (_privateStreamClient == null || _privateStreamClient.IsRunning);

        /// <summary>
        /// Connects the public stream and starts the private stream when credentials are available
        /// </summary>
        public override void Connect()
        {
            if (!_isInitialized)
            {
                throw new InvalidOperationException("BitbankBrokerage.Connect(): brokerage is not initialized.");
            }
            if (IsConnected)
            {
                return;
            }

            _socketClient.Connect();
            _privateStreamClient?.Start();
        }

        /// <summary>
        /// Disconnects both streams
        /// </summary>
        public override void Disconnect()
        {
            _privateStreamClient?.Stop();
            _socketClient?.Dispose();
        }

        /// <summary>
        /// Places a new order via POST /v1/user/spot/order
        /// </summary>
        public override bool PlaceOrder(Order order)
        {
            var submitted = false;
            _messageHandler.WithLockedStream(() =>
            {
                BitbankOrderRequest request;
                try
                {
                    request = BuildOrderRequest(order);
                }
                catch (NotSupportedException e)
                {
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, "Bitbank Order Event")
                    {
                        Status = OrderStatus.Invalid,
                        Message = e.Message
                    });
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "NotSupported", e.Message));
                    return;
                }

                try
                {
                    var result = _restApiClient.CreateOrder(request);
                    order.BrokerId.Add(result.OrderId.ToStringInvariant());
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, "Bitbank Order Event")
                    {
                        Status = OrderStatus.Submitted
                    });
                    submitted = true;
                }
                catch (Exception e)
                {
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, "Bitbank Order Event")
                    {
                        Status = OrderStatus.Invalid,
                        Message = e.Message
                    });
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "PlaceOrderError", e.Message));
                }
            });
            return submitted;
        }

        /// <summary>
        /// bitbank has no order amendment endpoint: always returns false, cancel and re-submit instead
        /// </summary>
        public override bool UpdateOrder(Order order)
        {
            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "UpdateOrderNotSupported",
                "bitbank does not support updating orders. Cancel and re-submit instead."));
            return false;
        }

        /// <summary>
        /// Cancels an order via POST /v1/user/spot/cancel_order. The Canceled order event is
        /// emitted when the private stream confirms the cancellation.
        /// </summary>
        public override bool CancelOrder(Order order)
        {
            if (order.BrokerId.Count == 0)
            {
                return false;
            }

            var canceled = false;
            _messageHandler.WithLockedStream(() =>
            {
                var pair = _symbolMapper.GetBrokerageSymbol(order.Symbol);
                var orderId = long.Parse(order.BrokerId[0], System.Globalization.CultureInfo.InvariantCulture);
                try
                {
                    _restApiClient.CancelOrder(pair, orderId);
                    canceled = true;
                }
                catch (BitbankApiException e) when (e.ErrorCode == 50026)
                {
                    // already canceled: treat as success, the stream event handles state
                    canceled = true;
                }
                catch (Exception e)
                {
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "CancelOrderError", e.Message));
                }
            });
            return canceled;
        }

        /// <summary>
        /// Fetches open orders via GET /v1/user/spot/active_orders
        /// </summary>
        public override List<Order> GetOpenOrders()
        {
            var orders = new List<Order>();
            foreach (var bitbankOrder in _restApiClient.GetActiveOrders())
            {
                Symbol symbol;
                try
                {
                    symbol = _symbolMapper.GetLeanSymbol(bitbankOrder.Pair, SecurityType.Crypto, BitbankMarket.Name);
                }
                catch (Exception)
                {
                    Log.Trace($"BitbankBrokerage.GetOpenOrders(): skipping order {bitbankOrder.OrderId} for unknown pair {bitbankOrder.Pair}");
                    continue;
                }

                var order = ConvertOrder(bitbankOrder, symbol);
                if (order == null)
                {
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "UnsupportedOrderType",
                        $"Skipping unsupported bitbank order type '{bitbankOrder.Type}' for order {bitbankOrder.OrderId}"));
                    continue;
                }
                orders.Add(order);
            }
            return orders;
        }

        /// <summary>
        /// bitbank is a spot cash account: holdings are represented as cash balances
        /// </summary>
        public override List<Holding> GetAccountHoldings()
        {
            return new List<Holding>();
        }

        /// <summary>
        /// Fetches balances via GET /v1/user/assets, one CashAmount per asset
        /// </summary>
        public override List<CashAmount> GetCashBalance()
        {
            var balances = new List<CashAmount>();
            foreach (var asset in _restApiClient.GetAssets())
            {
                if (asset.OnhandAmount != 0)
                {
                    balances.Add(new CashAmount(asset.OnhandAmount, asset.Asset.LazyToUpper()));
                }
            }
            if (!balances.Any(b => b.Currency == Currencies.JPY))
            {
                balances.Add(new CashAmount(0, Currencies.JPY));
            }
            return balances;
        }

        /// <summary>
        /// Maps a Lean order to a bitbank create-order request
        /// </summary>
        public BitbankOrderRequest BuildOrderRequest(Order order)
        {
            var request = new BitbankOrderRequest
            {
                Pair = _symbolMapper.GetBrokerageSymbol(order.Symbol),
                Amount = order.AbsoluteQuantity.ToStringInvariant(),
                Side = order.Direction == OrderDirection.Buy ? "buy" : "sell"
            };

            switch (order)
            {
                case MarketOrder:
                    request.Type = "market";
                    break;

                case LimitOrder limit:
                    request.Type = "limit";
                    request.Price = limit.LimitPrice.ToStringInvariant();
                    if ((order.Properties as BitbankOrderProperties)?.PostOnly == true)
                    {
                        request.PostOnly = true;
                    }
                    break;

                case StopMarketOrder stopMarket:
                    request.Type = "stop";
                    request.TriggerPrice = stopMarket.StopPrice.ToStringInvariant();
                    break;

                case StopLimitOrder stopLimit:
                    request.Type = "stop_limit";
                    request.Price = stopLimit.LimitPrice.ToStringInvariant();
                    request.TriggerPrice = stopLimit.StopPrice.ToStringInvariant();
                    break;

                default:
                    throw new NotSupportedException($"BitbankBrokerage: unsupported order type {order.Type}");
            }

            if (order.TimeInForce != Orders.TimeInForce.GoodTilCanceled)
            {
                throw new NotSupportedException("BitbankBrokerage: only GoodTilCanceled time in force is supported.");
            }

            return request;
        }

        /// <summary>
        /// Converts a bitbank active order to a Lean order, or null for unsupported types.
        /// The remaining quantity is used so restored orders fill the outstanding amount only.
        /// </summary>
        public static Order ConvertOrder(BitbankOrder bitbankOrder, Symbol symbol)
        {
            var quantity = bitbankOrder.RemainingAmount ?? bitbankOrder.StartAmount ?? 0;
            if (bitbankOrder.Side == "sell")
            {
                quantity = -quantity;
            }
            var time = QuantConnect.Time.UnixMillisecondTimeStampToDateTime(bitbankOrder.OrderedAt);

            Order order;
            switch (bitbankOrder.Type)
            {
                case "market":
                    order = new MarketOrder(symbol, quantity, time);
                    break;
                case "limit":
                    order = new LimitOrder(symbol, quantity, bitbankOrder.Price ?? 0, time);
                    break;
                case "stop":
                    order = new StopMarketOrder(symbol, quantity, bitbankOrder.TriggerPrice ?? 0, time);
                    break;
                case "stop_limit":
                    order = new StopLimitOrder(symbol, quantity, bitbankOrder.TriggerPrice ?? 0, bitbankOrder.Price ?? 0, time);
                    break;
                default:
                    return null;
            }

            order.BrokerId.Add(bitbankOrder.OrderId.ToStringInvariant());
            order.Status = bitbankOrder.Status == BitbankOrderStatus.PartiallyFilled
                ? OrderStatus.PartiallyFilled
                : OrderStatus.Submitted;
            return order;
        }

        /// <summary>
        /// Disposes clients and streams
        /// </summary>
        public override void Dispose()
        {
            _privateStreamClient.DisposeSafely();
            _socketClient.DisposeSafely();
            _restApiClient.DisposeSafely();
            _messageHandler.DisposeSafely();
            base.Dispose();
        }
    }
}
