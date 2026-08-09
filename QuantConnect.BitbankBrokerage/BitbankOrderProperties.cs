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

using QuantConnect.Interfaces;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.Bitbank
{
    /// <summary>
    /// The bitbank margin position book an order acts on: combined with the order side it
    /// determines open vs close (e.g. buy + Long opens a long, sell + Long closes a long)
    /// </summary>
    public enum BitbankPositionSide
    {
        /// <summary>Long position book (bitbank position_side "long")</summary>
        Long,

        /// <summary>Short position book (bitbank position_side "short")</summary>
        Short
    }

    /// <summary>
    /// Contains additional properties and settings for an order submitted to the bitbank brokerage
    /// </summary>
    public class BitbankOrderProperties : OrderProperties
    {
        /// <summary>
        /// This flag will ensure the order executes only as a maker (rebated fee) order.
        /// If the order would immediately match against the book, bitbank rejects it
        /// and no part of the order will execute.
        /// Note: this flag is only applied to limit orders (bitbank's post_only parameter).
        /// </summary>
        public bool PostOnly { get; set; }

        /// <summary>
        /// Margin trading only: the position book this order acts on (bitbank's position_side
        /// parameter). When left null the brokerage derives it from the currently open margin
        /// positions: an order first closes an opposing open position, otherwise it opens a new
        /// one. Set it explicitly to force open/close behavior, e.g. sell + Short opens a new
        /// short even while a long position is open.
        /// Requires the margin account type (config "bitbank-account-type": "margin").
        /// </summary>
        public BitbankPositionSide? PositionSide { get; set; }

        /// <summary>
        /// Returns a new instance clone of this object
        /// </summary>
        public override IOrderProperties Clone()
        {
            return (BitbankOrderProperties)MemberwiseClone();
        }
    }
}
