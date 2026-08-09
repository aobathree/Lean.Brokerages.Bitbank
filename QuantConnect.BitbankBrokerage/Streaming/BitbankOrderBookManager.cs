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
using System.Globalization;
using System.Linq;
using QuantConnect.Brokerages.Bitbank.Messages;

namespace QuantConnect.Brokerages.Bitbank.Streaming
{
    /// <summary>
    /// Maintains a local order book from bitbank's depth_whole/depth_diff channels following
    /// the official sequencing rules: diffs are buffered, each whole snapshot rebaselines the
    /// book and buffered diffs with a sequence id greater than the snapshot's are re-applied
    /// in ascending order. A diff amount of zero removes the price level.
    /// See https://github.com/bitbankinc/bitbank-api-docs/blob/master/public-stream.md#how-to-manage-a-local-order-book-correctly
    /// </summary>
    public class BitbankOrderBookManager
    {
        private readonly object _locker = new();
        private readonly List<BitbankDepthDiff> _buffer = new();
        private long _lastWholeSequence = -1;
        private long _lastAppliedSequence = -1;

        /// <summary>
        /// The underlying Lean order book, exposes BestBidAskUpdated
        /// </summary>
        public DefaultOrderBook OrderBook { get; }

        /// <summary>
        /// Creates a manager for the given symbol
        /// </summary>
        public BitbankOrderBookManager(Symbol symbol)
        {
            OrderBook = new DefaultOrderBook(symbol);
        }

        /// <summary>
        /// Processes a depth_diff message
        /// </summary>
        public void HandleDiff(BitbankDepthDiff diff)
        {
            lock (_locker)
            {
                if (_lastWholeSequence < 0)
                {
                    // no snapshot yet: buffer only
                    _buffer.Add(diff);
                    return;
                }

                _buffer.Add(diff);
                PruneBuffer();

                if (diff.Sequence > _lastAppliedSequence)
                {
                    ApplyDiff(diff);
                    _lastAppliedSequence = diff.Sequence;
                }
            }
        }

        /// <summary>
        /// Processes a depth_whole snapshot: rebaselines the book and re-applies newer buffered diffs
        /// </summary>
        public void HandleWhole(BitbankDepthWhole whole)
        {
            lock (_locker)
            {
                OrderBook.Clear();
                foreach (var level in whole.Bids ?? new List<string[]>())
                {
                    OrderBook.UpdateBidRow(ParseDecimal(level[0]), ParseDecimal(level[1]));
                }
                foreach (var level in whole.Asks ?? new List<string[]>())
                {
                    OrderBook.UpdateAskRow(ParseDecimal(level[0]), ParseDecimal(level[1]));
                }

                _lastWholeSequence = whole.SequenceId;
                _lastAppliedSequence = whole.SequenceId;

                PruneBuffer();
                foreach (var diff in _buffer.OrderBy(d => d.Sequence))
                {
                    ApplyDiff(diff);
                    _lastAppliedSequence = diff.Sequence;
                }
            }
        }

        private void PruneBuffer()
        {
            _buffer.RemoveAll(d => d.Sequence <= _lastWholeSequence);
        }

        private void ApplyDiff(BitbankDepthDiff diff)
        {
            foreach (var level in diff.Bids ?? new List<string[]>())
            {
                var price = ParseDecimal(level[0]);
                var amount = ParseDecimal(level[1]);
                if (amount == 0)
                {
                    OrderBook.RemoveBidRow(price);
                }
                else
                {
                    OrderBook.UpdateBidRow(price, amount);
                }
            }
            foreach (var level in diff.Asks ?? new List<string[]>())
            {
                var price = ParseDecimal(level[0]);
                var amount = ParseDecimal(level[1]);
                if (amount == 0)
                {
                    OrderBook.RemoveAskRow(price);
                }
                else
                {
                    OrderBook.UpdateAskRow(price, amount);
                }
            }
        }

        private static decimal ParseDecimal(string value)
        {
            return decimal.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
