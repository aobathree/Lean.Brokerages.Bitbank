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
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Bitbank
{
    /// <summary>
    /// Provides an implementation of <see cref="FeeModel"/> that models bitbank (bitbank.cc) order fees.
    /// bitbank charges fees in the quote currency (JPY) and pays a rebate to makers on most pairs:
    /// standard rates are -0.02% maker / 0.12% taker (btc_jpy currently runs 0% / 0.10%).
    /// Actual per-pair rates are published by the GET /spot/pairs endpoint
    /// (fields maker_fee_rate_quote / taker_fee_rate_quote); live fills report the exact fee
    /// via the private stream, so this model is an estimate for backtesting and pre-trade checks.
    /// Margin trading: open/close order fees follow the same standard maker/taker rates
    /// (per-pair fields margin_open_/margin_close_..._fee_rate_quote). The daily position
    /// interest (建玉金利, 0.04%/day charged at 00:00 JST) is NOT modelled here; live accounts
    /// see it reflected in cash balances via Lean's cash sync.
    /// </summary>
    public class BitbankFeeModel : FeeModel
    {
        /// <summary>
        /// Standard maker fee rate for most JPY pairs (negative value = rebate)
        /// https://bitbank.cc/docs/fees/
        /// </summary>
        public const decimal StandardMakerFee = -0.0002m;

        /// <summary>
        /// Standard taker fee rate for most JPY pairs
        /// https://bitbank.cc/docs/fees/
        /// </summary>
        public const decimal StandardTakerFee = 0.0012m;

        private readonly decimal _makerFee;

        private readonly decimal _takerFee;

        /// <summary>
        /// Creates bitbank fee model setting fee values
        /// </summary>
        /// <param name="makerFee">Maker fee rate, defaults to the standard -0.02% rebate</param>
        /// <param name="takerFee">Taker fee rate, defaults to the standard 0.12%</param>
        public BitbankFeeModel(decimal makerFee = StandardMakerFee, decimal takerFee = StandardTakerFee)
        {
            _makerFee = makerFee;
            _takerFee = takerFee;
        }

        /// <summary>
        /// Get the fee for this order in quote currency (JPY)
        /// </summary>
        /// <param name="parameters">A <see cref="OrderFeeParameters"/> object
        /// containing the security and order</param>
        /// <returns>The cost of the order in quote currency; negative for maker rebates</returns>
        public override OrderFee GetOrderFee(OrderFeeParameters parameters)
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters), "The 'parameters' argument cannot be null.");
            }

            var order = parameters.Order;
            var security = parameters.Security;
            var props = order.Properties as BitbankOrderProperties;

            // marketable limit orders are considered takers
            var isMaker = order.Type == OrderType.Limit && ((props != null && props.PostOnly) || !order.IsMarketable);
            var feePercentage = isMaker ? _makerFee : _takerFee;

            // get order value in quote currency, then apply maker/taker fee factor
            var unitPrice = order.Direction == OrderDirection.Buy ? security.AskPrice : security.BidPrice;
            unitPrice *= security.SymbolProperties.ContractMultiplier;

            var fee = unitPrice * order.AbsoluteQuantity * feePercentage;

            return new OrderFee(new CashAmount(fee, security.QuoteCurrency.Symbol));
        }
    }
}
