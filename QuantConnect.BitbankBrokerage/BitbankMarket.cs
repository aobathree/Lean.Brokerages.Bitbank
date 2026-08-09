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

using System.Runtime.CompilerServices;
using QuantConnect.Configuration;

namespace QuantConnect.Brokerages.Bitbank
{
    /// <summary>
    /// Registers the bitbank market with Lean at assembly load, so that a stock
    /// (unmodified) Lean build can resolve <c>Symbol.Create("BTCJPY",
    /// SecurityType.Crypto, BitbankMarket.Name)</c> without any change to
    /// <c>Market.cs</c>. The numeric market id defaults to 44 (first free id after
    /// the built-in markets) and can be overridden with the
    /// <c>bitbank-market-id</c> config key should a future Lean version claim it.
    /// </summary>
    public static class BitbankMarket
    {
        /// <summary>Market name used in symbol properties and market hours entries</summary>
        public const string Name = "bitbank";

        /// <summary>Default numeric market identifier</summary>
        public const int DefaultId = 44;

        /// <summary>
        /// Runs when the plugin assembly is loaded (factory discovery in live mode,
        /// or the first reference to any plugin type in an algorithm).
        /// </summary>
        [ModuleInitializer]
        internal static void Register()
        {
            if (Market.Encode(Name) == null)
            {
                Market.Add(Name, Config.GetInt("bitbank-market-id", DefaultId));
            }
        }
    }
}
