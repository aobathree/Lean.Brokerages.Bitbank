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
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Brokerages.Bitbank.Messages;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.Bitbank.Api
{
    /// <summary>
    /// REST client for the bitbank public and private APIs.
    /// Private endpoints are rate limited to 10 req/s for reads and 6 req/s for writes per user.
    /// All responses use the {"success": 0|1, "data": {...}} envelope.
    /// </summary>
    public class BitbankRestApiClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly BitbankAuthenticator _authenticator;
        private readonly string _restUrl;
        private readonly string _publicUrl;
        private readonly RateGate _queryRateGate = new(10, TimeSpan.FromSeconds(1));
        private readonly RateGate _updateRateGate = new(6, TimeSpan.FromSeconds(1));
        private readonly RateGate _publicRateGate = new(10, TimeSpan.FromSeconds(1));

        /// <summary>
        /// True if API credentials were provided
        /// </summary>
        public bool HasCredentials => _authenticator.HasCredentials;

        /// <summary>
        /// Creates a new REST client
        /// </summary>
        /// <param name="apiKey">bitbank API key, may be empty for public-data-only use</param>
        /// <param name="apiSecret">bitbank API secret</param>
        /// <param name="restUrl">Private REST host, e.g. https://api.bitbank.cc</param>
        /// <param name="publicUrl">Public data host, e.g. https://public.bitbank.cc</param>
        /// <param name="httpClient">Optional HttpClient override for testing</param>
        public BitbankRestApiClient(string apiKey, string apiSecret, string restUrl, string publicUrl, HttpClient httpClient = null)
        {
            _authenticator = new BitbankAuthenticator(apiKey, apiSecret);
            _restUrl = restUrl.TrimEnd('/');
            _publicUrl = publicUrl.TrimEnd('/');
            _httpClient = httpClient ?? new HttpClient();
        }

        /// <summary>
        /// GET /v1/user/assets
        /// </summary>
        public List<BitbankAsset> GetAssets()
        {
            var data = PrivateGet("/v1/user/assets");
            return data["assets"].ToObject<List<BitbankAsset>>();
        }

        /// <summary>
        /// GET /v1/user/spot/active_orders, optionally filtered by pair
        /// </summary>
        public List<BitbankOrder> GetActiveOrders(string pair = null)
        {
            var path = "/v1/user/spot/active_orders";
            if (!string.IsNullOrEmpty(pair))
            {
                path += "?pair=" + pair;
            }
            var data = PrivateGet(path);
            return data["orders"].ToObject<List<BitbankOrder>>();
        }

        /// <summary>
        /// GET /v1/user/spot/order
        /// </summary>
        public BitbankOrder GetOrder(string pair, long orderId)
        {
            var data = PrivateGet($"/v1/user/spot/order?pair={pair}&order_id={orderId.ToStringInvariant()}");
            return data.ToObject<BitbankOrder>();
        }

        /// <summary>
        /// POST /v1/user/spot/order
        /// </summary>
        public BitbankOrder CreateOrder(BitbankOrderRequest request)
        {
            var data = PrivatePost("/v1/user/spot/order", JsonConvert.SerializeObject(request));
            return data.ToObject<BitbankOrder>();
        }

        /// <summary>
        /// POST /v1/user/spot/cancel_order
        /// </summary>
        public BitbankOrder CancelOrder(string pair, long orderId)
        {
            var body = JsonConvert.SerializeObject(new { pair, order_id = orderId });
            var data = PrivatePost("/v1/user/spot/cancel_order", body);
            return data.ToObject<BitbankOrder>();
        }

        /// <summary>
        /// GET /v1/user/subscribe: returns the PubNub channel and token for the private stream.
        /// The token has a TTL of 12 hours.
        /// </summary>
        public (string Channel, string Token) GetPrivateStreamCredentials()
        {
            var data = PrivateGet("/v1/user/subscribe");
            return (data["pubnub_channel"].ToString(), data["pubnub_token"].ToString());
        }

        /// <summary>
        /// GET /v1/spot/pairs (no auth): pair trading specs
        /// </summary>
        public JArray GetPairs()
        {
            _queryRateGate.WaitToProceed();
            var response = SendRequest(new HttpRequestMessage(HttpMethod.Get, _restUrl + "/v1/spot/pairs"));
            return (JArray)response["pairs"];
        }

        /// <summary>
        /// GET {public}/{pair}/candlestick/{candleType}/{date}. Returns an empty list when
        /// bitbank has no data for the requested date (error code 10000).
        /// </summary>
        /// <param name="pair">bitbank pair code, e.g. btc_jpy</param>
        /// <param name="candleType">1min, 5min, 15min, 30min, 1hour, 4hour, 8hour, 12hour, 1day, 1week, 1month</param>
        /// <param name="date">YYYYMMDD for intraday candle types, YYYY for 4hour and above</param>
        public List<BitbankCandle> GetCandlesticks(string pair, string candleType, string date)
        {
            _publicRateGate.WaitToProceed();
            JToken data;
            try
            {
                data = SendRequest(new HttpRequestMessage(HttpMethod.Get, $"{_publicUrl}/{pair}/candlestick/{candleType}/{date}"));
            }
            catch (BitbankApiException e) when (e.ErrorCode == 10000)
            {
                // no data for this date
                return new List<BitbankCandle>();
            }

            var candles = new List<BitbankCandle>();
            foreach (var ohlcv in data["candlestick"][0]["ohlcv"])
            {
                candles.Add(new BitbankCandle
                {
                    Open = ohlcv[0].ToObject<decimal>(),
                    High = ohlcv[1].ToObject<decimal>(),
                    Low = ohlcv[2].ToObject<decimal>(),
                    Close = ohlcv[3].ToObject<decimal>(),
                    Volume = ohlcv[4].ToObject<decimal>(),
                    Timestamp = ohlcv[5].ToObject<long>()
                });
            }
            return candles;
        }

        private JToken PrivateGet(string pathWithQuery)
        {
            _queryRateGate.WaitToProceed();
            var request = new HttpRequestMessage(HttpMethod.Get, _restUrl + pathWithQuery);
            foreach (var header in _authenticator.GetHeaders(pathWithQuery))
            {
                request.Headers.Add(header.Key, header.Value);
            }
            return SendRequest(request);
        }

        private JToken PrivatePost(string path, string body)
        {
            _updateRateGate.WaitToProceed();
            var request = new HttpRequestMessage(HttpMethod.Post, _restUrl + path)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            foreach (var header in _authenticator.GetHeadersForBody(body))
            {
                request.Headers.Add(header.Key, header.Value);
            }
            return SendRequest(request);
        }

        private JToken SendRequest(HttpRequestMessage request)
        {
            using var response = _httpClient.SendAsync(request).SynchronouslyAwaitTaskResult();
            var content = response.Content.ReadAsStringAsync().SynchronouslyAwaitTaskResult();
            return ParseResponse(content, (int)response.StatusCode);
        }

        /// <summary>
        /// Parses the bitbank success/data envelope, throwing <see cref="BitbankApiException"/> on errors
        /// </summary>
        public static JToken ParseResponse(string content, int httpStatusCode)
        {
            JObject json;
            try
            {
                json = JObject.Parse(content);
            }
            catch (JsonException)
            {
                throw new BitbankApiException(0, $"Unexpected bitbank response (HTTP {httpStatusCode}): {content}");
            }

            if (json["success"]?.ToObject<int>() == 1)
            {
                return json["data"];
            }

            var code = json["data"]?["code"]?.ToObject<int>() ?? 0;
            throw new BitbankApiException(code, $"bitbank API error {code} (HTTP {httpStatusCode}): {GetErrorDescription(code)}");
        }

        /// <summary>
        /// Human readable description for common bitbank error codes,
        /// see https://github.com/bitbankinc/bitbank-api-docs/blob/master/errors.md
        /// </summary>
        public static string GetErrorDescription(int code)
        {
            switch (code)
            {
                case 10000: return "URL not found";
                case 10009: return "Request rate limit exceeded";
                case 20001: return "Authentication failed";
                case 20002: return "Invalid API key";
                case 20003: return "API key not found";
                case 20004: return "Missing ACCESS-NONCE / ACCESS-REQUEST-TIME";
                case 20005: return "Invalid signature";
                case 20033: return "Invalid ACCESS-REQUEST-TIME";
                case 20034: return "ACCESS-REQUEST-TIME out of accepted range";
                case 20035: return "Invalid ACCESS-TIME-WINDOW";
                case 30001: return "Missing order quantity";
                case 30006: return "Missing order id";
                case 30012: return "Missing order price";
                case 30013: return "Missing order side";
                case 40001: return "Invalid order quantity";
                case 40013: return "Invalid order id";
                case 40020: return "Invalid order price";
                case 40021: return "Invalid order side";
                case 50009: return "Order not found";
                case 50010: return "Order cannot be canceled";
                case 50026: return "Order already canceled";
                case 50027: return "Order already executed";
                case 50061: return "Amount exceeds available balance";
                case 50062: return "Amount exceeds available margin";
                case 60001: return "Insufficient funds";
                case 60002: return "Market order quantity above limit";
                case 60003: return "Order quantity above limit";
                case 70011: return "System temporarily overloaded, retry later";
                case 70020: return "Market orders restricted during circuit break";
                default: return "See https://github.com/bitbankinc/bitbank-api-docs/blob/master/errors.md";
            }
        }

        /// <summary>
        /// Disposes rate gates and the HTTP client
        /// </summary>
        public void Dispose()
        {
            _queryRateGate.DisposeSafely();
            _updateRateGate.DisposeSafely();
            _publicRateGate.DisposeSafely();
            _httpClient.DisposeSafely();
        }
    }
}
