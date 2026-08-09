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
using System.Collections.Concurrent;
using System.Threading;
using Newtonsoft.Json.Linq;
using QuantConnect.Logging;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.Bitbank.Streaming
{
    /// <summary>
    /// Minimal Socket.IO 4 (Engine.IO protocol v4) client over Lean's <see cref="WebSocketClientWrapper"/>,
    /// implementing exactly the subset the bitbank public stream requires:
    /// handshake (0 → 40), ping/pong (2 → 3), join-room emits and "message" events.
    /// See https://github.com/bitbankinc/bitbank-api-docs/blob/master/public-stream.md
    /// </summary>
    public class BitbankSocketIoClient : IDisposable
    {
        private readonly WebSocketClientWrapper _webSocket = new();
        private readonly ConcurrentDictionary<string, byte> _rooms = new();
        private readonly ManualResetEventSlim _namespaceConnectedEvent = new(false);
        private volatile bool _namespaceConnected;

        /// <summary>
        /// Fired for each room message: (room name, message data payload)
        /// </summary>
        public event EventHandler<(string Room, JToken Data)> MessageReceived;

        /// <summary>
        /// True when the websocket is open and the Socket.IO namespace handshake completed
        /// </summary>
        public bool IsConnected => _webSocket.IsOpen && _namespaceConnected;

        /// <summary>
        /// Creates a client for the given base url, e.g. wss://stream.bitbank.cc
        /// </summary>
        public BitbankSocketIoClient(string baseUrl)
        {
            _webSocket.Initialize(baseUrl.TrimEnd('/') + "/socket.io/?EIO=4&transport=websocket");
            _webSocket.Message += (_, e) => OnFrame((e.Data as WebSocketClientWrapper.TextMessage)?.Message);
            _webSocket.Closed += (_, _) =>
            {
                _namespaceConnected = false;
                _namespaceConnectedEvent.Reset();
            };
        }

        /// <summary>
        /// Connects and waits for the Socket.IO namespace handshake to complete
        /// </summary>
        public void Connect(TimeSpan? timeout = null)
        {
            _webSocket.Connect();
            if (!_namespaceConnectedEvent.Wait(timeout ?? TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("BitbankSocketIoClient: Socket.IO handshake timeout.");
            }
        }

        /// <summary>
        /// Joins a room (e.g. transactions_btc_jpy); re-joined automatically on reconnect
        /// </summary>
        public void JoinRoom(string room)
        {
            _rooms.TryAdd(room, 0);
            if (IsConnected)
            {
                _webSocket.Send(BuildJoinRoomFrame(room));
            }
        }

        /// <summary>
        /// Forgets a room. bitbank has no documented leave-room event, so messages may keep
        /// arriving until reconnect; callers are expected to filter by subscription state.
        /// </summary>
        public void ForgetRoom(string room)
        {
            _rooms.TryRemove(room, out _);
        }

        private void OnFrame(string frame)
        {
            if (string.IsNullOrEmpty(frame))
            {
                return;
            }

            try
            {
                switch (GetFrameType(frame))
                {
                    case BitbankSocketIoFrameType.Handshake:
                        // Engine.IO open packet: reply with a Socket.IO namespace connect
                        _webSocket.Send("40");
                        break;

                    case BitbankSocketIoFrameType.Ping:
                        _webSocket.Send("3");
                        break;

                    case BitbankSocketIoFrameType.NamespaceConnected:
                        _namespaceConnected = true;
                        Log.Trace($"BitbankSocketIoClient: namespace connected, re-joining {_rooms.Count} room(s)");
                        // re-join all tracked rooms (initial connect and every reconnect)
                        foreach (var trackedRoom in _rooms.Keys)
                        {
                            _webSocket.Send(BuildJoinRoomFrame(trackedRoom));
                        }
                        _namespaceConnectedEvent.Set();
                        break;

                    case BitbankSocketIoFrameType.NamespaceDisconnected:
                        _namespaceConnected = false;
                        _namespaceConnectedEvent.Reset();
                        break;

                    case BitbankSocketIoFrameType.Event:
                        if (TryParseEvent(frame, out var room, out var data))
                        {
                            MessageReceived?.Invoke(this, (room, data));
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                Log.Error(e, $"BitbankSocketIoClient.OnFrame(): failed to process frame: {frame}");
            }
        }

        /// <summary>
        /// Classifies an Engine.IO/Socket.IO text frame
        /// </summary>
        public static BitbankSocketIoFrameType GetFrameType(string frame)
        {
            if (frame.StartsWith("42", StringComparison.Ordinal))
            {
                return BitbankSocketIoFrameType.Event;
            }
            if (frame.StartsWith("40", StringComparison.Ordinal))
            {
                return BitbankSocketIoFrameType.NamespaceConnected;
            }
            if (frame.StartsWith("41", StringComparison.Ordinal))
            {
                return BitbankSocketIoFrameType.NamespaceDisconnected;
            }
            if (frame == "2")
            {
                return BitbankSocketIoFrameType.Ping;
            }
            if (frame.StartsWith("0", StringComparison.Ordinal))
            {
                return BitbankSocketIoFrameType.Handshake;
            }
            return BitbankSocketIoFrameType.Other;
        }

        /// <summary>
        /// Parses a 42["message",{"room_name":...,"message":{"data":...}}] event frame
        /// </summary>
        public static bool TryParseEvent(string frame, out string room, out JToken data)
        {
            room = null;
            data = null;

            var payload = JArray.Parse(frame.Substring(2));
            if (payload.Count < 2 || payload[0].ToString() != "message")
            {
                return false;
            }

            var body = payload[1];
            room = body["room_name"]?.ToString();
            data = body["message"]?["data"];
            return room != null && data != null;
        }

        /// <summary>
        /// Builds the join-room emit frame
        /// </summary>
        public static string BuildJoinRoomFrame(string room)
        {
            return "42" + new JArray("join-room", room).ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// Closes the websocket
        /// </summary>
        public void Dispose()
        {
            _webSocket.Close();
            _namespaceConnectedEvent.DisposeSafely();
        }
    }

    /// <summary>
    /// Frame types of the bitbank Socket.IO stream
    /// </summary>
    public enum BitbankSocketIoFrameType
    {
#pragma warning disable 1591
        Handshake,
        Ping,
        NamespaceConnected,
        NamespaceDisconnected,
        Event,
        Other
#pragma warning restore 1591
    }
}
