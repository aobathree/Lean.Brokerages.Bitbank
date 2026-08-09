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
    /// bitbank (bitbank.cc) brokerage implementation: spot and margin trading on JPY-quoted pairs.
    /// Orders and balances use the private REST API, order/fill events arrive over the
    /// PubNub private stream, and market data over the Socket.IO public stream.
    /// Margin trading (2x leverage, long/short) is enabled via config "bitbank-account-type": "margin".
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

        // true when the account trades on margin: orders carry position_side and
        // holdings come from /v1/user/margin/positions
        private bool _marginTrading;

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
        /// <param name="accountType">Cash for spot trading (default), Margin for margin trading</param>
        public BitbankBrokerage(string apiKey, string apiSecret, string restUrl, string publicUrl,
            string webSocketUrl, IOrderProvider orderProvider, IDataAggregator aggregator,
            AccountType accountType = AccountType.Cash)
            : base("Bitbank")
        {
            Initialize(apiKey, apiSecret, restUrl, publicUrl, webSocketUrl, orderProvider, aggregator, accountType);
        }

        private void Initialize(string apiKey, string apiSecret, string restUrl, string publicUrl,
            string webSocketUrl, IOrderProvider orderProvider, IDataAggregator aggregator, AccountType accountType)
        {
            if (_isInitialized)
            {
                return;
            }

            AccountBaseCurrency = Currencies.JPY;
            _marginTrading = accountType == AccountType.Margin;

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
        /// Cash account: holdings are represented as cash balances, returns an empty list.
        /// Margin account: open positions from GET /v1/user/margin/positions, netted per pair
        /// (Lean holdings are net per symbol; simultaneous long and short books are combined).
        /// </summary>
        public override List<Holding> GetAccountHoldings()
        {
            if (!_marginTrading)
            {
                return new List<Holding>();
            }

            var holdings = new Dictionary<Symbol, Holding>();
            foreach (var position in _restApiClient.GetMarginPositions())
            {
                Symbol symbol;
                try
                {
                    symbol = _symbolMapper.GetLeanSymbol(position.Pair, SecurityType.Crypto, BitbankMarket.Name);
                }
                catch (Exception)
                {
                    Log.Trace($"BitbankBrokerage.GetAccountHoldings(): skipping position for unknown pair {position.Pair}");
                    continue;
                }

                var quantity = position.PositionSide == "short" ? -position.OpenAmount : position.OpenAmount;
                if (holdings.TryGetValue(symbol, out var existing))
                {
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "HedgedMarginPosition",
                        $"Both long and short margin positions are open for {position.Pair}; " +
                        "they are netted into a single Lean holding."));
                    existing.Quantity += quantity;
                }
                else
                {
                    holdings[symbol] = new Holding
                    {
                        Symbol = symbol,
                        Quantity = quantity,
                        AveragePrice = position.AveragePrice,
                        CurrencySymbol = Currencies.GetCurrencySymbol(Currencies.JPY)
                    };
                }
            }
            return holdings.Values.Where(holding => holding.Quantity != 0).ToList();
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

            var explicitPositionSide = (order.Properties as BitbankOrderProperties)?.PositionSide;
            if (_marginTrading)
            {
                request.PositionSide = explicitPositionSide.HasValue
                    ? (explicitPositionSide.Value == BitbankPositionSide.Short ? "short" : "long")
                    : ResolvePositionSide(order.Direction, request.Pair, order.AbsoluteQuantity);
            }
            else if (explicitPositionSide.HasValue)
            {
                throw new NotSupportedException(
                    "BitbankBrokerage: PositionSide requires the margin account type (config \"bitbank-account-type\": \"margin\").");
            }

            return request;
        }

        /// <summary>
        /// Derives position_side from the currently open margin positions for the pair
        /// </summary>
        private string ResolvePositionSide(OrderDirection direction, string pair, decimal quantity)
        {
            decimal openLong = 0, openShort = 0;
            foreach (var position in _restApiClient.GetMarginPositions())
            {
                if (position.Pair != pair)
                {
                    continue;
                }
                if (position.PositionSide == "short")
                {
                    openShort += position.OpenAmount;
                }
                else
                {
                    openLong += position.OpenAmount;
                }
            }
            return DerivePositionSide(direction, openLong, openShort, quantity);
        }

        /// <summary>
        /// Margin position_side rule: an order first closes an opposing open position
        /// (buy closes short, sell closes long), otherwise it opens a new position.
        /// A single bitbank order cannot close one book and open the other, so quantities
        /// exceeding the opposing open amount are rejected: split the order or set
        /// <see cref="BitbankOrderProperties.PositionSide"/> explicitly.
        /// </summary>
        public static string DerivePositionSide(OrderDirection direction, decimal openLong, decimal openShort, decimal quantity)
        {
            var closableAmount = direction == OrderDirection.Buy ? openShort : openLong;
            var closingSide = direction == OrderDirection.Buy ? "short" : "long";
            var openingSide = direction == OrderDirection.Buy ? "long" : "short";

            if (closableAmount == 0)
            {
                return openingSide;
            }
            if (quantity <= closableAmount)
            {
                return closingSide;
            }
            throw new NotSupportedException(
                $"BitbankBrokerage: order quantity {quantity.ToStringInvariant()} exceeds the open {closingSide} position " +
                $"{closableAmount.ToStringInvariant()}. bitbank cannot close one position side and open the other in a single order: " +
                "split the order, or set BitbankOrderProperties.PositionSide explicitly.");
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

            // margin orders carry their position book so re-submission after restart targets the same book
            BitbankOrderProperties properties = null;
            if (!string.IsNullOrEmpty(bitbankOrder.PositionSide))
            {
                properties = new BitbankOrderProperties
                {
                    PositionSide = bitbankOrder.PositionSide == "short" ? BitbankPositionSide.Short : BitbankPositionSide.Long
                };
            }

            Order order;
            switch (bitbankOrder.Type)
            {
                case "market":
                    order = new MarketOrder(symbol, quantity, time, properties: properties);
                    break;
                case "limit":
                    order = new LimitOrder(symbol, quantity, bitbankOrder.Price ?? 0, time, properties: properties);
                    break;
                case "stop":
                    order = new StopMarketOrder(symbol, quantity, bitbankOrder.TriggerPrice ?? 0, time, properties: properties);
                    break;
                case "stop_limit":
                    order = new StopLimitOrder(symbol, quantity, bitbankOrder.TriggerPrice ?? 0, bitbankOrder.Price ?? 0, time, properties: properties);
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
