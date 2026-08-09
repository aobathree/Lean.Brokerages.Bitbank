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
using Newtonsoft.Json;
using NUnit.Framework;
using QuantConnect.Brokerages.Bitbank.Messages;
using QuantConnect.Configuration;
using QuantConnect.Orders;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Bitbank.Tests
{
    [TestFixture]
    public class BitbankMarginTests
    {
        private BitbankBrokerage _marginBrokerage;
        private BitbankBrokerage _cashBrokerage;
        private Symbol _btcJpy;

        [OneTimeSetUp]
        public void SetUp()
        {
            var dataFolder = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "Data"));
            Config.Set("data-folder", dataFolder);
            Globals.Reset();

            _marginBrokerage = new BitbankBrokerage(string.Empty, string.Empty,
                "https://api.bitbank.cc", "https://public.bitbank.cc", "wss://stream.bitbank.cc", null, null,
                AccountType.Margin);
            _cashBrokerage = new BitbankBrokerage(string.Empty, string.Empty,
                "https://api.bitbank.cc", "https://public.bitbank.cc", "wss://stream.bitbank.cc", null, null);
            _btcJpy = Symbol.Create("BTCJPY", SecurityType.Crypto, BitbankMarket.Name);
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            _marginBrokerage?.Dispose();
            _cashBrokerage?.Dispose();
        }

        [Test]
        public void BuildsMarginOrderRequestWithExplicitPositionSide()
        {
            var properties = new BitbankOrderProperties { PositionSide = BitbankPositionSide.Short };
            var request = _marginBrokerage.BuildOrderRequest(
                new LimitOrder(_btcJpy, -0.01m, 9000000m, DateTime.UtcNow, properties: properties));

            Assert.AreEqual("short", request.PositionSide);
            Assert.AreEqual("sell", request.Side);

            properties = new BitbankOrderProperties { PositionSide = BitbankPositionSide.Long };
            request = _marginBrokerage.BuildOrderRequest(
                new MarketOrder(_btcJpy, 0.01m, DateTime.UtcNow, properties: properties));
            Assert.AreEqual("long", request.PositionSide);
        }

        [Test]
        public void SpotOrderRequestHasNoPositionSide()
        {
            var request = _cashBrokerage.BuildOrderRequest(new MarketOrder(_btcJpy, 0.01m, DateTime.UtcNow));
            Assert.IsNull(request.PositionSide);

            var json = JsonConvert.SerializeObject(request);
            StringAssert.DoesNotContain("position_side", json);
        }

        [Test]
        public void CashAccountRejectsExplicitPositionSide()
        {
            var properties = new BitbankOrderProperties { PositionSide = BitbankPositionSide.Long };
            Assert.Throws<NotSupportedException>(() => _cashBrokerage.BuildOrderRequest(
                new MarketOrder(_btcJpy, 0.01m, DateTime.UtcNow, properties: properties)));
        }

        [Test]
        public void MarginOrderRequestSerializesPositionSide()
        {
            var properties = new BitbankOrderProperties { PositionSide = BitbankPositionSide.Short };
            var request = _marginBrokerage.BuildOrderRequest(
                new MarketOrder(_btcJpy, 0.01m, DateTime.UtcNow, properties: properties));

            var json = JsonConvert.SerializeObject(request);
            StringAssert.Contains("\"position_side\":\"short\"", json);
        }

        [TestCase(OrderDirection.Buy, 0, 0, 0.5, "long")]       // no positions: buy opens long
        [TestCase(OrderDirection.Sell, 0, 0, 0.5, "short")]     // no positions: sell opens short
        [TestCase(OrderDirection.Buy, 0, 0.5, 0.5, "short")]    // open short: buy closes it
        [TestCase(OrderDirection.Buy, 0, 0.5, 0.3, "short")]    // partial close of a short
        [TestCase(OrderDirection.Sell, 0.5, 0, 0.5, "long")]    // open long: sell closes it
        [TestCase(OrderDirection.Sell, 0.5, 0, 0.2, "long")]    // partial close of a long
        [TestCase(OrderDirection.Buy, 0.5, 0, 0.3, "long")]     // adding to a long buys long
        [TestCase(OrderDirection.Sell, 0, 0.5, 0.3, "short")]   // adding to a short sells short
        public void DerivesPositionSideFromOpenPositions(OrderDirection direction,
            double openLong, double openShort, double quantity, string expected)
        {
            Assert.AreEqual(expected, BitbankBrokerage.DerivePositionSide(
                direction, (decimal)openLong, (decimal)openShort, (decimal)quantity));
        }

        [Test]
        public void RejectsOrderCrossingPositionSides()
        {
            // closing 0.5 short and opening 0.3 long cannot be a single bitbank order
            var exception = Assert.Throws<NotSupportedException>(() =>
                BitbankBrokerage.DerivePositionSide(OrderDirection.Buy, 0, 0.5m, 0.8m));
            StringAssert.Contains("PositionSide", exception.Message);

            Assert.Throws<NotSupportedException>(() =>
                BitbankBrokerage.DerivePositionSide(OrderDirection.Sell, 0.5m, 0, 0.8m));
        }

        [Test]
        public void ConvertOrderPreservesPositionSide()
        {
            var bitbankOrder = new BitbankOrder
            {
                OrderId = 777,
                Pair = "btc_jpy",
                Side = "sell",
                Type = "limit",
                RemainingAmount = 0.2m,
                Price = 9500000m,
                PositionSide = "short",
                OrderedAt = 1700000000000,
                Status = BitbankOrderStatus.Unfilled
            };

            var order = BitbankBrokerage.ConvertOrder(bitbankOrder, _btcJpy);

            var properties = order.Properties as BitbankOrderProperties;
            Assert.IsNotNull(properties);
            Assert.AreEqual(BitbankPositionSide.Short, properties.PositionSide);

            // spot orders keep default properties
            bitbankOrder.PositionSide = null;
            order = BitbankBrokerage.ConvertOrder(bitbankOrder, _btcJpy);
            Assert.IsNull((order.Properties as BitbankOrderProperties)?.PositionSide);
        }

        [Test]
        public void DeserializesMarginPositions()
        {
            // field names verified against a live /v1/user/margin/positions response (2026-08-09)
            var json = @"{
                ""pair"": ""btc_jpy"",
                ""position_side"": ""short"",
                ""open_amount"": ""0.0500"",
                ""product"": ""450000.0"",
                ""average_price"": ""9000000.0"",
                ""unrealized_fee_amount"": ""-12.5"",
                ""unrealized_interest_amount"": ""36.0""
            }";

            var position = JsonConvert.DeserializeObject<BitbankMarginPosition>(json);

            Assert.AreEqual("btc_jpy", position.Pair);
            Assert.AreEqual("short", position.PositionSide);
            Assert.AreEqual(0.05m, position.OpenAmount);
            Assert.AreEqual(9000000m, position.AveragePrice);
            Assert.AreEqual(36.0m, position.UnrealizedInterestAmount);
        }

        [Test]
        public void DeserializesMarginTradeStreamFields()
        {
            var json = @"{
                ""trade_id"": 1,
                ""order_id"": 2,
                ""pair"": ""btc_jpy"",
                ""side"": ""sell"",
                ""type"": ""market"",
                ""price"": ""9100000"",
                ""amount"": ""0.01"",
                ""maker_taker"": ""taker"",
                ""fee_amount_base"": ""0"",
                ""fee_amount_quote"": ""109.2"",
                ""position_side"": ""long"",
                ""profit_loss"": ""1000.0"",
                ""interest"": ""3.6"",
                ""executed_at"": 1700000000000
            }";

            var trade = JsonConvert.DeserializeObject<BitbankStreamTrade>(json);

            Assert.AreEqual("long", trade.PositionSide);
            Assert.AreEqual(1000.0m, trade.ProfitLoss);
            Assert.AreEqual(3.6m, trade.Interest);
        }

        [Test]
        public void ModelSupportsMarginAccountType()
        {
            var marginModel = new BitbankBrokerageModel(AccountType.Margin);
            var cashModel = new BitbankBrokerageModel();

            Assert.AreEqual(AccountType.Margin, marginModel.AccountType);
            Assert.AreEqual(AccountType.Cash, cashModel.AccountType);
            Assert.AreEqual(2m, marginModel.GetLeverage(null));
            Assert.AreEqual(1m, cashModel.GetLeverage(null));
            Assert.IsInstanceOf<SecurityMarginModel>(marginModel.GetBuyingPowerModel(null));
            Assert.IsInstanceOf<CashBuyingPowerModel>(cashModel.GetBuyingPowerModel(null));
        }

        [Test]
        public void FactoryParsesAccountType()
        {
            Assert.AreEqual(AccountType.Margin, BitbankBrokerageFactory.ParseAccountType("margin"));
            Assert.AreEqual(AccountType.Margin, BitbankBrokerageFactory.ParseAccountType(" Margin "));
            Assert.AreEqual(AccountType.Cash, BitbankBrokerageFactory.ParseAccountType("cash"));
            Assert.AreEqual(AccountType.Cash, BitbankBrokerageFactory.ParseAccountType(""));
            Assert.AreEqual(AccountType.Cash, BitbankBrokerageFactory.ParseAccountType(null));
        }

        [Test]
        public void DescribesMarginErrorCodes()
        {
            StringAssert.Contains("position side", Api.BitbankRestApiClient.GetErrorDescription(40164).ToLowerInvariant());
            StringAssert.Contains("margin", Api.BitbankRestApiClient.GetErrorDescription(40165).ToLowerInvariant());
            StringAssert.Contains("margin", Api.BitbankRestApiClient.GetErrorDescription(50058).ToLowerInvariant());
        }
    }
}
