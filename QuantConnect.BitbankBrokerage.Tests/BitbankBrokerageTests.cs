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
using System.IO;
using NUnit.Framework;
using QuantConnect.Brokerages.Bitbank.Api;
using QuantConnect.Brokerages.Bitbank.Messages;
using QuantConnect.Configuration;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.Bitbank.Tests
{
    [TestFixture]
    public class BitbankBrokerageTests
    {
        private BitbankBrokerage _brokerage;
        private Symbol _btcJpy;

        [OneTimeSetUp]
        public void SetUp()
        {
            // point the symbol properties / market hours databases at the repo Data folder
            var dataFolder = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "Data"));
            Config.Set("data-folder", dataFolder);
            Globals.Reset();

            _brokerage = new BitbankBrokerage(string.Empty, string.Empty,
                "https://api.bitbank.cc", "https://public.bitbank.cc", "wss://stream.bitbank.cc", null, null);
            _btcJpy = Symbol.Create("BTCJPY", SecurityType.Crypto, BitbankMarket.Name);
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            _brokerage?.Dispose();
        }

        [Test]
        public void AccountBaseCurrencyIsJpy()
        {
            Assert.AreEqual(Currencies.JPY, _brokerage.AccountBaseCurrency);
        }

        [Test]
        public void BuildsMarketOrderRequest()
        {
            var request = _brokerage.BuildOrderRequest(new MarketOrder(_btcJpy, 0.01m, DateTime.UtcNow));

            Assert.AreEqual("btc_jpy", request.Pair);
            Assert.AreEqual("market", request.Type);
            Assert.AreEqual("buy", request.Side);
            Assert.AreEqual("0.01", request.Amount);
            Assert.IsNull(request.Price);
            Assert.IsNull(request.TriggerPrice);
        }

        [Test]
        public void BuildsPostOnlyLimitOrderRequest()
        {
            var properties = new BitbankOrderProperties { PostOnly = true };
            var request = _brokerage.BuildOrderRequest(
                new LimitOrder(_btcJpy, -0.5m, 9000000m, DateTime.UtcNow, properties: properties));

            Assert.AreEqual("limit", request.Type);
            Assert.AreEqual("sell", request.Side);
            Assert.AreEqual("0.5", request.Amount);
            Assert.AreEqual("9000000", request.Price);
            Assert.AreEqual(true, request.PostOnly);
        }

        [Test]
        public void BuildsStopLimitOrderRequest()
        {
            var request = _brokerage.BuildOrderRequest(
                new StopLimitOrder(_btcJpy, 0.1m, 10000000m, 10100000m, DateTime.UtcNow));

            Assert.AreEqual("stop_limit", request.Type);
            Assert.AreEqual("10000000", request.TriggerPrice);
            Assert.AreEqual("10100000", request.Price);
        }

        [Test]
        public void RejectsNonGtcTimeInForce()
        {
            var properties = new BitbankOrderProperties { TimeInForce = TimeInForce.Day };
            Assert.Throws<NotSupportedException>(() => _brokerage.BuildOrderRequest(
                new LimitOrder(_btcJpy, 1m, 100m, DateTime.UtcNow, properties: properties)));
        }

        [Test]
        public void ConvertsActiveLimitOrder()
        {
            var bitbankOrder = new BitbankOrder
            {
                OrderId = 12345,
                Pair = "btc_jpy",
                Side = "sell",
                Type = "limit",
                StartAmount = 1.0m,
                RemainingAmount = 0.4m,
                ExecutedAmount = 0.6m,
                Price = 9500000m,
                OrderedAt = 1700000000000,
                Status = BitbankOrderStatus.PartiallyFilled
            };

            var order = BitbankBrokerage.ConvertOrder(bitbankOrder, _btcJpy);

            Assert.IsInstanceOf<LimitOrder>(order);
            Assert.AreEqual(-0.4m, order.Quantity); // remaining amount, sell = negative
            Assert.AreEqual(9500000m, ((LimitOrder)order).LimitPrice);
            Assert.AreEqual(OrderStatus.PartiallyFilled, order.Status);
            Assert.AreEqual("12345", order.BrokerId[0]);
        }

        [Test]
        public void ConvertsStopOrderAndRejectsUnknownTypes()
        {
            var stop = new BitbankOrder
            {
                OrderId = 2,
                Side = "buy",
                Type = "stop",
                RemainingAmount = 0.2m,
                TriggerPrice = 10000000m,
                OrderedAt = 1700000000000,
                Status = BitbankOrderStatus.Unfilled
            };
            var converted = BitbankBrokerage.ConvertOrder(stop, _btcJpy);
            Assert.IsInstanceOf<StopMarketOrder>(converted);
            Assert.AreEqual(10000000m, ((StopMarketOrder)converted).StopPrice);

            var unsupported = new BitbankOrder { Type = "take_profit" };
            Assert.IsNull(BitbankBrokerage.ConvertOrder(unsupported, _btcJpy));
        }

        [Test]
        public void ParsesSuccessAndErrorEnvelopes()
        {
            var data = BitbankRestApiClient.ParseResponse("{\"success\":1,\"data\":{\"assets\":[]}}", 200);
            Assert.IsNotNull(data["assets"]);

            var exception = Assert.Throws<BitbankApiException>(() =>
                BitbankRestApiClient.ParseResponse("{\"success\":0,\"data\":{\"code\":20001}}", 401));
            Assert.AreEqual(20001, exception.ErrorCode);
            StringAssert.Contains("Authentication failed", exception.Message);
        }

        [Test]
        public void HistoryDateKeysCoverJstRange()
        {
            // 2026-01-01 20:00 UTC = 2026-01-02 05:00 JST: minute keys must include both JST dates
            var start = new DateTime(2026, 1, 1, 20, 0, 0, DateTimeKind.Utc);
            var end = new DateTime(2026, 1, 2, 2, 0, 0, DateTimeKind.Utc);

            var keys = new System.Collections.Generic.List<string>(
                BitbankBrokerage.GetDateKeys(start, end, Resolution.Minute));

            CollectionAssert.AreEqual(new[] { "20260102" }, keys);

            var dailyKeys = new System.Collections.Generic.List<string>(
                BitbankBrokerage.GetDateKeys(new DateTime(2025, 12, 30), new DateTime(2026, 1, 5), Resolution.Daily));
            CollectionAssert.AreEqual(new[] { "2025", "2026" }, dailyKeys);
        }
    }
}
