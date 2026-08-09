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

using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Brokerages.Bitbank.Streaming;

namespace QuantConnect.Brokerages.Bitbank.Tests
{
    [TestFixture]
    public class BitbankSocketIoProtocolTests
    {
        [TestCase("0{\"sid\":\"abc\",\"pingInterval\":25000}", BitbankSocketIoFrameType.Handshake)]
        [TestCase("2", BitbankSocketIoFrameType.Ping)]
        [TestCase("40{\"sid\":\"xyz\"}", BitbankSocketIoFrameType.NamespaceConnected)]
        [TestCase("41", BitbankSocketIoFrameType.NamespaceDisconnected)]
        [TestCase("42[\"message\",{}]", BitbankSocketIoFrameType.Event)]
        [TestCase("3", BitbankSocketIoFrameType.Other)]
        public void ClassifiesFrames(string frame, BitbankSocketIoFrameType expected)
        {
            Assert.AreEqual(expected, BitbankSocketIoClient.GetFrameType(frame));
        }

        [Test]
        public void ParsesTickerEventFromOfficialDocs()
        {
            // sample from https://github.com/bitbankinc/bitbank-api-docs/blob/master/public-stream.md
            const string frame = "42[\"message\",{\"room_name\":\"ticker_btc_jpy\",\"message\":{\"pid\":851203833," +
                "\"data\":{\"sell\":\"896490\",\"buy\":\"896489\",\"open\":\"896489\",\"high\":\"905002\"," +
                "\"low\":\"881500\",\"last\":\"896489\",\"vol\":\"650.2026\",\"timestamp\":1570080042822}}}]";

            Assert.IsTrue(BitbankSocketIoClient.TryParseEvent(frame, out var room, out var data));
            Assert.AreEqual("ticker_btc_jpy", room);
            Assert.AreEqual("896489", data["last"].ToString());
            Assert.AreEqual(1570080042822L, data["timestamp"].ToObject<long>());
        }

        [Test]
        public void ParsesDepthDiffEvent()
        {
            const string frame = "42[\"message\",{\"room_name\":\"depth_diff_xrp_jpy\",\"message\":{\"data\":" +
                "{\"a\":[],\"b\":[[\"26.758\",\"20000.0000\"],[\"26.212\",\"0\"]],\"t\":1570080269609,\"s\":\"1234567890\"}}}]";

            Assert.IsTrue(BitbankSocketIoClient.TryParseEvent(frame, out var room, out var data));
            Assert.AreEqual("depth_diff_xrp_jpy", room);
            var diff = data.ToObject<Messages.BitbankDepthDiff>();
            Assert.AreEqual(1234567890L, diff.Sequence);
            Assert.AreEqual(2, diff.Bids.Count);
            Assert.AreEqual(0, diff.Asks.Count);
        }

        [Test]
        public void BuildsJoinRoomFrame()
        {
            Assert.AreEqual("42[\"join-room\",\"ticker_btc_jpy\"]", BitbankSocketIoClient.BuildJoinRoomFrame("ticker_btc_jpy"));
        }

        [Test]
        public void IgnoresNonMessageEvents()
        {
            Assert.IsFalse(BitbankSocketIoClient.TryParseEvent("42[\"other-event\",{\"x\":1}]", out _, out _));
        }

        [Test]
        public void ExtractsPubNubPayloadWithAndWithoutWrapper()
        {
            var raw = JObject.Parse("{\"method\":\"spot_order\",\"params\":[]}");
            var wrapped = JObject.Parse("{\"message\":{\"method\":\"spot_trade\",\"params\":[]}}");
            var unrelated = JObject.Parse("{\"foo\":1}");

            Assert.AreEqual("spot_order", BitbankPrivateStreamClient.ExtractPayload(raw)["method"].ToString());
            Assert.AreEqual("spot_trade", BitbankPrivateStreamClient.ExtractPayload(wrapped)["method"].ToString());
            Assert.IsNull(BitbankPrivateStreamClient.ExtractPayload(unrelated));
        }
    }
}
