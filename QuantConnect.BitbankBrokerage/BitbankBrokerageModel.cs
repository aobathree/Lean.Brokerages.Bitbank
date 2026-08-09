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
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Benchmarks;
using QuantConnect.Orders.Fees;
using System.Collections.Generic;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.Bitbank
{
    /// <summary>
    /// Provides bitbank (bitbank.cc) specific properties. bitbank is a Japanese spot crypto
    /// exchange offering JPY-quoted pairs only, with a cash account and no order amendment API.
    /// </summary>
    public class BitbankBrokerageModel : DefaultBrokerageModel
    {
        /// <summary>
        /// Notifies users that order updates are not supported: bitbank has no amend endpoint,
        /// orders must be canceled and re-submitted.
        /// </summary>
        private readonly BrokerageMessageEvent _message = new(BrokerageMessageType.Warning, 0, QuantConnect.Messages.DefaultBrokerageModel.OrderUpdateNotSupported);

        /// <summary>
        /// Order types supported by bitbank spot trading: limit, market, stop and stop_limit
        /// </summary>
        private readonly HashSet<OrderType> _supportedOrderTypes = new()
        {
            OrderType.Limit,
            OrderType.Market,
            OrderType.StopMarket,
            OrderType.StopLimit
        };

        /// <summary>
        /// Gets a map of the default markets to be used for each security type
        /// </summary>
        public override IReadOnlyDictionary<SecurityType, string> DefaultMarkets => GetDefaultMarkets(BitbankMarket.Name);

        /// <summary>
        /// Initializes a new instance of the <see cref="BitbankBrokerageModel"/> class
        /// </summary>
        /// <param name="accountType">The type of account to be modelled, defaults to <see cref="AccountType.Cash"/></param>
        public BitbankBrokerageModel(AccountType accountType = AccountType.Cash)
            : base(accountType)
        {
            if (accountType == AccountType.Margin)
            {
                throw new ArgumentException("The bitbank brokerage does not currently support Margin trading.", nameof(accountType));
            }
        }

        /// <summary>
        /// bitbank global leverage rule: spot cash trading only
        /// </summary>
        public override decimal GetLeverage(Security security)
        {
            return 1m;
        }

        /// <summary>
        /// Get the benchmark for this model
        /// </summary>
        /// <param name="securities">SecurityService to create the security with if needed</param>
        /// <returns>The benchmark for this brokerage</returns>
        public override IBenchmark GetBenchmark(SecurityManager securities)
        {
            var symbol = Symbol.Create("BTCJPY", SecurityType.Crypto, BitbankMarket.Name);
            return SecurityBenchmark.CreateInstance(securities, symbol);
        }

        /// <summary>
        /// Provides bitbank fee model
        /// </summary>
        public override IFeeModel GetFeeModel(Security security)
        {
            return new BitbankFeeModel();
        }

        /// <summary>
        /// bitbank does not support updating orders: cancel and re-submit instead
        /// </summary>
        /// <param name="security">The security of the order</param>
        /// <param name="order">The order to be updated</param>
        /// <param name="request">The requested update to be made to the order</param>
        /// <param name="message">If this function returns false, a brokerage message detailing why the order may not be updated</param>
        /// <returns>Always false</returns>
        public override bool CanUpdateOrder(Security security, Order order, UpdateOrderRequest request, out BrokerageMessageEvent message)
        {
            message = _message;
            return false;
        }

        /// <summary>
        /// Evaluates whether the exchange will accept the order
        /// </summary>
        /// <param name="security">The security of the order</param>
        /// <param name="order">The order to be processed</param>
        /// <param name="message">If this function returns false, a brokerage message detailing why the order may not be submitted</param>
        /// <returns>True if the brokerage could process the order, false otherwise</returns>
        public override bool CanSubmitOrder(Security security, Order order, out BrokerageMessageEvent message)
        {
            if (order == null || security == null)
            {
                var parameter = order == null ? nameof(order) : nameof(security);
                throw new ArgumentNullException(parameter, $"{parameter} parameter cannot be null. Please provide a valid {parameter} for submission.");
            }

            if (security.Type != SecurityType.Crypto)
            {
                message = new BrokerageMessageEvent(BrokerageMessageType.Warning, "NotSupported",
                    QuantConnect.Messages.DefaultBrokerageModel.UnsupportedSecurityType(this, security));
                return false;
            }

            if (!_supportedOrderTypes.Contains(order.Type))
            {
                message = new BrokerageMessageEvent(BrokerageMessageType.Warning, "NotSupported",
                    QuantConnect.Messages.DefaultBrokerageModel.UnsupportedOrderType(this, order, _supportedOrderTypes));
                return false;
            }

            if (!IsValidOrderSize(security, order.Quantity, out message))
            {
                return false;
            }

            return base.CanSubmitOrder(security, order, out message);
        }

        /// <summary>
        /// Gets a new buying power model for the security; bitbank is cash-account only
        /// </summary>
        /// <param name="security">The security to get a buying power model for</param>
        /// <returns>The buying power model for this brokerage/security</returns>
        public override IBuyingPowerModel GetBuyingPowerModel(Security security)
        {
            return new CashBuyingPowerModel();
        }

        /// <summary>
        /// Gets the default markets for different security types, overriding the market name for Crypto securities.
        /// </summary>
        /// <param name="marketName">The default market name for Crypto securities.</param>
        protected static IReadOnlyDictionary<SecurityType, string> GetDefaultMarkets(string marketName)
        {
            var map = DefaultMarketMap.ToDictionary();
            map[SecurityType.Crypto] = marketName;
            return map.ToReadOnlyDictionary();
        }
    }
}
