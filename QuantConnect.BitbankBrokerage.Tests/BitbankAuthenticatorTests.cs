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

using NUnit.Framework;
using QuantConnect.Brokerages.Bitbank.Api;

namespace QuantConnect.Brokerages.Bitbank.Tests
{
    [TestFixture]
    public class BitbankAuthenticatorTests
    {
        [Test]
        public void SignatureMatchesOfficialDocumentationVector()
        {
            // https://github.com/bitbankinc/bitbank-api-docs/blob/master/rest-api.md#sample
            // ACCESS-REQUEST-TIME=1721121776490, ACCESS-TIME-WINDOW=1000, GET /v1/user/assets, secret "hoge"
            var signature = BitbankAuthenticator.Sign("hoge", "17211217764901000/v1/user/assets");
            Assert.AreEqual("9ec5745960d05573c8fb047cdd9191bd0c6ede26f07700bb40ecf1a3920abae8", signature);
        }

        [Test]
        public void GetHeadersProducesTimeWindowMethodHeaders()
        {
            var authenticator = new BitbankAuthenticator("my-key", "hoge");
            var headers = authenticator.GetHeaders("/v1/user/assets", 1721121776490);

            Assert.AreEqual("my-key", headers["ACCESS-KEY"]);
            Assert.AreEqual("1721121776490", headers["ACCESS-REQUEST-TIME"]);
            Assert.AreEqual(BitbankAuthenticator.TimeWindow, headers["ACCESS-TIME-WINDOW"]);
            Assert.AreEqual(
                BitbankAuthenticator.Sign("hoge", "1721121776490" + BitbankAuthenticator.TimeWindow + "/v1/user/assets"),
                headers["ACCESS-SIGNATURE"]);
        }

        [Test]
        public void PostSignatureSignsRawBody()
        {
            var authenticator = new BitbankAuthenticator("my-key", "hoge");
            const string body = "{\"pair\": \"xrp_jpy\", \"price\": \"20\", \"amount\": \"1\",\"side\": \"buy\", \"type\": \"limit\"}";
            var headers = authenticator.GetHeadersForBody(body, 1721121776490);

            Assert.AreEqual(
                BitbankAuthenticator.Sign("hoge", "1721121776490" + BitbankAuthenticator.TimeWindow + body),
                headers["ACCESS-SIGNATURE"]);
        }

        [Test]
        public void HasCredentialsReflectsInput()
        {
            Assert.IsTrue(new BitbankAuthenticator("k", "s").HasCredentials);
            Assert.IsFalse(new BitbankAuthenticator("", "").HasCredentials);
            Assert.IsFalse(new BitbankAuthenticator(null, null).HasCredentials);
        }
    }
}
