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
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using QuantConnect.Logging;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.Bitbank.Streaming
{
    /// <summary>
    /// bitbank private stream client. bitbank publishes order/trade/asset events over PubNub;
    /// this client consumes them via PubNub's HTTP long-polling subscribe v2 wire protocol,
    /// avoiding an SDK dependency. Credentials (channel + 12h token) come from the signed
    /// GET /v1/user/subscribe endpoint and are refreshed automatically on access-denied.
    /// See https://github.com/bitbankinc/bitbank-api-docs/blob/master/private-stream.md
    /// </summary>
    public class BitbankPrivateStreamClient : IDisposable
    {
        /// <summary>
        /// bitbank's public PubNub subscribe key (from the official private stream docs)
        /// </summary>
        public const string SubscribeKey = "sub-c-ecebae8e-dd60-11e6-b6b1-02ee2ddab7fe";

        private readonly Func<(string Channel, string Token)> _credentialsProvider;
        private readonly HttpClient _httpClient;
        private readonly string _origin;
        private CancellationTokenSource _cts;
        private Task _pollTask;
        private volatile bool _isRunning;

        /// <summary>
        /// Fired for each private stream message payload: {"method": "...", "params": ...}
        /// </summary>
        public event EventHandler<JObject> MessageReceived;

        /// <summary>
        /// True while the polling loop is active
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Creates a new private stream client
        /// </summary>
        /// <param name="credentialsProvider">Returns a fresh (channel, token) pair, normally by calling GET /v1/user/subscribe</param>
        /// <param name="httpClient">Optional HttpClient override for testing</param>
        /// <param name="origin">Optional PubNub origin override, default https://ps.pndsn.com</param>
        public BitbankPrivateStreamClient(Func<(string Channel, string Token)> credentialsProvider,
            HttpClient httpClient = null, string origin = "https://ps.pndsn.com")
        {
            _credentialsProvider = credentialsProvider;
            _origin = origin.TrimEnd('/');
            // PubNub holds subscribe requests up to ~280 seconds
            _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(310) };
        }

        /// <summary>
        /// Starts the long-polling loop
        /// </summary>
        public void Start()
        {
            if (_isRunning)
            {
                return;
            }
            _cts = new CancellationTokenSource();
            _isRunning = true;
            _pollTask = Task.Factory.StartNew(() => PollLoop(_cts.Token), _cts.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>
        /// Stops the long-polling loop
        /// </summary>
        public void Stop()
        {
            _isRunning = false;
            _cts?.Cancel();
            try
            {
                _pollTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // ignored: cancellation surfaces as AggregateException
            }
            _cts.DisposeSafely();
            _cts = null;
        }

        private void PollLoop(CancellationToken token)
        {
            var channel = string.Empty;
            var authToken = string.Empty;
            var timetoken = "0";
            string region = null;
            var haveCredentials = false;
            var consecutiveErrors = 0;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!haveCredentials)
                    {
                        (channel, authToken) = _credentialsProvider();
                        haveCredentials = true;
                        Log.Trace("BitbankPrivateStreamClient: obtained private stream credentials");
                    }

                    var url = $"{_origin}/v2/subscribe/{SubscribeKey}/{Uri.EscapeDataString(channel)}/0" +
                              $"?uuid={Uri.EscapeDataString(channel)}&auth={Uri.EscapeDataString(authToken)}&tt={timetoken}";
                    if (region != null)
                    {
                        url += $"&tr={region}";
                    }

                    using var response = _httpClient.GetAsync(url, token).SynchronouslyAwaitTaskResult();
                    var content = response.Content.ReadAsStringAsync(token).SynchronouslyAwaitTaskResult();

                    if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        // token expired (12h TTL): refresh credentials and resubscribe
                        Log.Trace("BitbankPrivateStreamClient: access denied, refreshing token");
                        haveCredentials = false;
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException($"PubNub subscribe failed: HTTP {(int)response.StatusCode}: {content}");
                    }

                    var json = JObject.Parse(content);
                    timetoken = json["t"]["t"].ToString();
                    region = json["t"]["r"]?.ToString();
                    consecutiveErrors = 0;

                    foreach (var envelope in (JArray)json["m"] ?? new JArray())
                    {
                        var payload = ExtractPayload(envelope["d"]);
                        if (payload != null)
                        {
                            MessageReceived?.Invoke(this, payload);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    consecutiveErrors++;
                    Log.Error(e, "BitbankPrivateStreamClient.PollLoop()");
                    // exponential backoff capped at 30s; also refresh credentials on repeated failures
                    if (consecutiveErrors % 3 == 0)
                    {
                        haveCredentials = false;
                    }
                    var backoff = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(consecutiveErrors, 5))));
                    if (token.WaitHandle.WaitOne(backoff))
                    {
                        break;
                    }
                }
            }

            _isRunning = false;
        }

        /// <summary>
        /// Extracts the {"method":...,"params":...} payload from a PubNub message body,
        /// tolerating both the raw payload and the {"message": {...}} SDK-style wrapper
        /// </summary>
        public static JObject ExtractPayload(JToken data)
        {
            if (data is not JObject obj)
            {
                return null;
            }
            if (obj["method"] != null)
            {
                return obj;
            }
            if (obj["message"] is JObject inner && inner["method"] != null)
            {
                return inner;
            }
            return null;
        }

        /// <summary>
        /// Stops polling and disposes resources
        /// </summary>
        public void Dispose()
        {
            Stop();
            _httpClient.DisposeSafely();
        }
    }
}
