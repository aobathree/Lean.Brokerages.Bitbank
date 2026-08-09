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
using System.Security.Cryptography;
using System.Text;

namespace QuantConnect.Brokerages.Bitbank.Api
{
    /// <summary>
    /// Builds bitbank private REST API authentication headers using the ACCESS-TIME-WINDOW method.
    /// Signature = hex(HMAC-SHA256(secret, requestTime + timeWindow + payload)) where payload is
    /// the full request path including /v1 and query string for GET, or the raw JSON body for POST.
    /// See https://github.com/bitbankinc/bitbank-api-docs/blob/master/rest-api.md#authorization
    /// </summary>
    public class BitbankAuthenticator
    {
        private readonly string _apiKey;
        private readonly string _apiSecret;

        /// <summary>
        /// Request validity window in milliseconds sent as ACCESS-TIME-WINDOW
        /// </summary>
        public const string TimeWindow = "5000";

        /// <summary>
        /// Creates a new authenticator for the given credentials
        /// </summary>
        public BitbankAuthenticator(string apiKey, string apiSecret)
        {
            _apiKey = apiKey;
            _apiSecret = apiSecret;
        }

        /// <summary>
        /// True if credentials were provided
        /// </summary>
        public bool HasCredentials => !string.IsNullOrEmpty(_apiKey) && !string.IsNullOrEmpty(_apiSecret);

        /// <summary>
        /// Returns the authentication headers for a GET request
        /// </summary>
        /// <param name="pathWithQuery">Full request path including /v1 prefix and query string, e.g. /v1/user/assets</param>
        /// <param name="requestTimeMs">Optional request time override for testing</param>
        public Dictionary<string, string> GetHeaders(string pathWithQuery, long? requestTimeMs = null)
        {
            return BuildHeaders(pathWithQuery, requestTimeMs);
        }

        /// <summary>
        /// Returns the authentication headers for a POST request
        /// </summary>
        /// <param name="body">The raw JSON request body, exactly as sent</param>
        /// <param name="requestTimeMs">Optional request time override for testing</param>
        public Dictionary<string, string> GetHeadersForBody(string body, long? requestTimeMs = null)
        {
            return BuildHeaders(body, requestTimeMs);
        }

        private Dictionary<string, string> BuildHeaders(string payload, long? requestTimeMs)
        {
            var requestTime = (requestTimeMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToStringInvariant();
            var signature = Sign(_apiSecret, requestTime + TimeWindow + payload);
            return new Dictionary<string, string>
            {
                { "ACCESS-KEY", _apiKey },
                { "ACCESS-REQUEST-TIME", requestTime },
                { "ACCESS-TIME-WINDOW", TimeWindow },
                { "ACCESS-SIGNATURE", signature }
            };
        }

        /// <summary>
        /// Computes the lowercase hex HMAC-SHA256 signature of the given message
        /// </summary>
        public static string Sign(string secret, string message)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
