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

using System.Collections.Generic;
using NUnit.Framework;
using QuantConnect.Brokerages.Bitbank.Messages;
using QuantConnect.Brokerages.Bitbank.Streaming;

namespace QuantConnect.Brokerages.Bitbank.Tests
{
    [TestFixture]
    public class BitbankOrderBookManagerTests
    {
        private static Symbol CreateSymbol()
        {
            return Symbol.Create("BTCJPY", SecurityType.Crypto, BitbankMarket.Name);
        }

        private static BitbankDepthDiff Diff(long sequence, List<string[]> bids = null, List<string[]> asks = null)
        {
            return new BitbankDepthDiff
            {
                Sequence = sequence,
                Bids = bids ?? new List<string[]>(),
                Asks = asks ?? new List<string[]>(),
                Timestamp = sequence
            };
        }

        [Test]
        public void AppliesOnlyDiffsNewerThanSnapshot()
        {
            // official docs example: diff{s=3}, diff{s=5}, diff{s=6}, diff{s=8}, whole{sequenceId=5}
            // => rebaseline with the whole, then apply s=6 and s=8 only
            var manager = new BitbankOrderBookManager(CreateSymbol());

            manager.HandleDiff(Diff(3, bids: new List<string[]> { new[] { "100", "5" } }));
            manager.HandleDiff(Diff(5, bids: new List<string[]> { new[] { "100", "7" } }));
            manager.HandleDiff(Diff(6, bids: new List<string[]> { new[] { "100", "2" } }));
            manager.HandleDiff(Diff(8, asks: new List<string[]> { new[] { "101", "0" }, new[] { "102", "3" } }));

            manager.HandleWhole(new BitbankDepthWhole
            {
                SequenceId = 5,
                Bids = new List<string[]> { new[] { "100", "1" } },
                Asks = new List<string[]> { new[] { "101", "1" } }
            });

            Assert.AreEqual(100m, manager.OrderBook.BestBidPrice);
            Assert.AreEqual(2m, manager.OrderBook.BestBidSize);   // s=6 applied over the snapshot
            Assert.AreEqual(102m, manager.OrderBook.BestAskPrice); // s=8 removed 101 and added 102
            Assert.AreEqual(3m, manager.OrderBook.BestAskSize);
        }

        [Test]
        public void AppliesLiveDiffsAfterSnapshot()
        {
            var manager = new BitbankOrderBookManager(CreateSymbol());
            manager.HandleWhole(new BitbankDepthWhole
            {
                SequenceId = 10,
                Bids = new List<string[]> { new[] { "100", "1" } },
                Asks = new List<string[]> { new[] { "101", "1" } }
            });

            manager.HandleDiff(Diff(11, bids: new List<string[]> { new[] { "100.5", "4" } }));

            Assert.AreEqual(100.5m, manager.OrderBook.BestBidPrice);
            Assert.AreEqual(4m, manager.OrderBook.BestBidSize);
        }

        [Test]
        public void IgnoresStaleLiveDiffs()
        {
            var manager = new BitbankOrderBookManager(CreateSymbol());
            manager.HandleWhole(new BitbankDepthWhole
            {
                SequenceId = 10,
                Bids = new List<string[]> { new[] { "100", "1" } },
                Asks = new List<string[]> { new[] { "101", "1" } }
            });

            manager.HandleDiff(Diff(9, bids: new List<string[]> { new[] { "100", "9" } }));

            Assert.AreEqual(1m, manager.OrderBook.BestBidSize);
        }

        [Test]
        public void ZeroAmountRemovesLevel()
        {
            var manager = new BitbankOrderBookManager(CreateSymbol());
            manager.HandleWhole(new BitbankDepthWhole
            {
                SequenceId = 1,
                Bids = new List<string[]> { new[] { "100", "1" }, new[] { "99", "2" } },
                Asks = new List<string[]> { new[] { "101", "1" } }
            });

            manager.HandleDiff(Diff(2, bids: new List<string[]> { new[] { "100", "0" } }));

            Assert.AreEqual(99m, manager.OrderBook.BestBidPrice);
            Assert.AreEqual(2m, manager.OrderBook.BestBidSize);
        }

        [Test]
        public void RebaselinesOnEachSnapshot()
        {
            var manager = new BitbankOrderBookManager(CreateSymbol());
            manager.HandleWhole(new BitbankDepthWhole
            {
                SequenceId = 1,
                Bids = new List<string[]> { new[] { "100", "1" } },
                Asks = new List<string[]> { new[] { "101", "1" } }
            });

            // the price level 100 is absent from the newer snapshot: it must disappear
            manager.HandleWhole(new BitbankDepthWhole
            {
                SequenceId = 20,
                Bids = new List<string[]> { new[] { "98", "5" } },
                Asks = new List<string[]> { new[] { "99", "5" } }
            });

            Assert.AreEqual(98m, manager.OrderBook.BestBidPrice);
            Assert.AreEqual(99m, manager.OrderBook.BestAskPrice);
        }
    }
}
