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
using System.Linq;
using Newtonsoft.Json.Linq;
using QuantConnect.Brokerages.Bitbank.Messages;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.Bitbank
{
    /// <summary>
    /// Private stream (PubNub) message processing: order lifecycle and fills
    /// </summary>
    public partial class BitbankBrokerage
    {
        private void ProcessPrivateMessage(JObject message)
        {
            try
            {
                var method = message["method"]?.ToString();
                switch (method)
                {
                    case "spot_order_new":
                    case "spot_order":
                        foreach (var orderToken in (JArray)message["params"])
                        {
                            HandleOrderUpdate(orderToken.ToObject<BitbankOrder>());
                        }
                        break;

                    case "spot_trade":
                        foreach (var tradeToken in (JArray)message["params"])
                        {
                            HandleTrade(tradeToken.ToObject<BitbankStreamTrade>());
                        }
                        break;

                    case "spot_order_invalidation":
                        foreach (var idToken in (JArray)message["params"]["order_id"])
                        {
                            HandleInvalidation(idToken.ToObject<long>());
                        }
                        break;

                    case "asset_update":
                        // balances are re-synced by Lean's periodic cash sync
                        break;
                }
            }
            catch (Exception e)
            {
                Log.Error(e, $"BitbankBrokerage.ProcessPrivateMessage(): {message}");
            }
        }

        private void HandleOrderUpdate(BitbankOrder bitbankOrder)
        {
            if (_closedOrders.ContainsKey(bitbankOrder.OrderId))
            {
                return;
            }

            var order = FindOrder(bitbankOrder.OrderId);
            if (order == null)
            {
                return;
            }

            switch (bitbankOrder.Status)
            {
                case BitbankOrderStatus.CanceledUnfilled:
                case BitbankOrderStatus.CanceledPartiallyFilled:
                    _closedOrders.TryAdd(bitbankOrder.OrderId, 0);
                    _fills.TryRemove(bitbankOrder.OrderId, out _);
                    OnOrderEvent(new OrderEvent(order, GetTime(bitbankOrder.CanceledAt), OrderFee.Zero, "Bitbank Order Event")
                    {
                        Status = OrderStatus.Canceled
                    });
                    break;

                case BitbankOrderStatus.Rejected:
                    _closedOrders.TryAdd(bitbankOrder.OrderId, 0);
                    _fills.TryRemove(bitbankOrder.OrderId, out _);
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, "Bitbank Order Event")
                    {
                        Status = OrderStatus.Invalid,
                        Message = "Order rejected by bitbank"
                    });
                    break;

                // UNFILLED: Submitted was already emitted on placement.
                // PARTIALLY_FILLED / FULLY_FILLED: fill events are emitted from spot_trade messages.
            }
        }

        private void HandleTrade(BitbankStreamTrade trade)
        {
            var order = FindOrder(trade.OrderId);
            if (order == null)
            {
                return;
            }

            var cumulative = _fills.AddOrUpdate(trade.OrderId, trade.Amount, (_, total) => total + trade.Amount);
            var isCompleted = cumulative >= order.AbsoluteQuantity;
            var status = isCompleted ? OrderStatus.Filled : OrderStatus.PartiallyFilled;

            var fee = OrderFee.Zero;
            if (trade.FeeAmountQuote != 0)
            {
                CurrencyPairUtil.DecomposeCurrencyPair(order.Symbol, out _, out var quoteCurrency);
                fee = new OrderFee(new CashAmount(trade.FeeAmountQuote, quoteCurrency));
            }
            else if (trade.FeeAmountBase != 0)
            {
                CurrencyPairUtil.DecomposeCurrencyPair(order.Symbol, out var baseCurrency, out _);
                fee = new OrderFee(new CashAmount(trade.FeeAmountBase, baseCurrency));
            }

            var fillQuantity = trade.Side == "buy" ? trade.Amount : -trade.Amount;
            OnOrderEvent(new OrderEvent(order, GetTime(trade.ExecutedAt), fee,
                $"Bitbank Order Event: {trade.MakerTaker}")
            {
                Status = status,
                FillPrice = trade.Price,
                FillQuantity = fillQuantity
            });

            if (isCompleted)
            {
                _closedOrders.TryAdd(trade.OrderId, 0);
                _fills.TryRemove(trade.OrderId, out _);
            }
        }

        private void HandleInvalidation(long orderId)
        {
            if (_closedOrders.ContainsKey(orderId))
            {
                return;
            }
            var order = FindOrder(orderId);
            if (order == null)
            {
                return;
            }
            _closedOrders.TryAdd(orderId, 0);
            OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, "Bitbank Order Event")
            {
                Status = OrderStatus.Canceled,
                Message = "Order invalidated by bitbank matching engine (asset shortage)"
            });
        }

        private Order FindOrder(long bitbankOrderId)
        {
            var order = _orderProvider?.GetOrdersByBrokerageId(bitbankOrderId)?.FirstOrDefault();
            if (order == null)
            {
                Log.Trace($"BitbankBrokerage: received event for unknown order id {bitbankOrderId}");
            }
            return order;
        }

        private static DateTime GetTime(long? unixMilliseconds)
        {
            return unixMilliseconds.HasValue && unixMilliseconds.Value > 0
                ? QuantConnect.Time.UnixMillisecondTimeStampToDateTime(unixMilliseconds.Value)
                : DateTime.UtcNow;
        }
    }
}
