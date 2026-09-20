using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace AIWalk.Networking
{
    /// <summary>
    /// Plain C# WebSocket transport for the AIWalk game backend.
    /// Text frames are used by GameBackendEventClient; binary frames are left opaque for physics.
    /// This class does not access Unity objects and does not require a MonoBehaviour.
    /// </summary>
    public sealed class GameBackendWebSocketClient : IDisposable
    {
        private const byte ImportantMarker = (byte)'!';
        private const byte RealtimeProtocolVersion = 1;
        private const byte PlayerInputKind = 1;
        private const byte PhysicsSnapshotKind = 2;
        private const byte VoicePcmKind = 3;
        private const int RealtimeHeaderBytes = 16;
        public const int NetworkIdBytes = 32;
        public const int PhysicsObjectIdBytes = NetworkIdBytes;
        private readonly object stateLock = new();
        private readonly object physicsHandlersLock = new();
        private readonly SemaphoreSlim lifecycleLock = new(1, 1);
        private readonly SemaphoreSlim sendLock = new(1, 1);
        private readonly ConcurrentQueue<QueuedEvent> receivedEvents = new();
        private readonly Dictionary<string, List<Action<byte[]>>> physicsSnapshotHandlers = new(StringComparer.Ordinal);
        private readonly int maxMessageBytes;
        private readonly long normalEventLifetimeTicks;
        private readonly long importantEventLifetimeTicks;

        private ClientWebSocket socket;
        private CancellationTokenSource receiveCancellation;
        private Task receiveTask;
        private bool disposed;

        public GameBackendWebSocketClient(
            int maxMessageBytes = 256 * 1024,
            double normalEventLifetimeSeconds = 1.0,
            double importantEventLifetimeSeconds = 5.0)
        {
            if (maxMessageBytes < 1024)
                throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
            if (normalEventLifetimeSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(normalEventLifetimeSeconds));
            if (importantEventLifetimeSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(importantEventLifetimeSeconds));

            this.maxMessageBytes = maxMessageBytes;
            normalEventLifetimeTicks = SecondsToStopwatchTicks(normalEventLifetimeSeconds);
            importantEventLifetimeTicks = SecondsToStopwatchTicks(importantEventLifetimeSeconds);
        }

        public event Action Connected;
        public event Action<string> TextMessageReceived;
        public event Action<byte[]> BinaryMessageReceived;
        public event Action<string, byte[]> VoicePcmReceived;
        /// <summary>
        /// Raised directly from the WebSocket receive thread for low-latency voice playback.
        /// Handlers must only copy/process plain data and must not access Unity objects.
        /// </summary>
        public event Action<string, byte[]> RealtimeVoicePcmReceived;
        public event Action<WebSocketCloseStatus?, string> Disconnected;
        public event Action<Exception> TransportError;

        public bool IsConnected
        {
            get
            {
                lock (stateLock)
                    return socket != null && socket.State == WebSocketState.Open;
            }
        }

        public int PendingEventCount => receivedEvents.Count;

        public IDisposable SubscribePhysicsSnapshot(string objectId, Action<byte[]> handler)
        {
            ThrowIfDisposed();
            ValidatePhysicsObjectId(objectId);
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            lock (physicsHandlersLock)
            {
                if (!physicsSnapshotHandlers.TryGetValue(objectId, out var handlers))
                {
                    handlers = new List<Action<byte[]>>();
                    physicsSnapshotHandlers.Add(objectId, handlers);
                }
                handlers.Add(handler);
            }

            return new PhysicsSnapshotSubscription(this, objectId, handler);
        }

        public void UnsubscribePhysicsSnapshot(string objectId, Action<byte[]> handler)
        {
            if (string.IsNullOrEmpty(objectId) || handler == null)
                return;

            lock (physicsHandlersLock)
            {
                if (!physicsSnapshotHandlers.TryGetValue(objectId, out var handlers))
                    return;
                handlers.Remove(handler);
                if (handlers.Count == 0)
                    physicsSnapshotHandlers.Remove(objectId);
            }
        }

        public static Uri BuildServerUri(string backendWebSocketUrl)
        {
            if (string.IsNullOrWhiteSpace(backendWebSocketUrl))
                throw new ArgumentException("Backend WebSocket URL is required.", nameof(backendWebSocketUrl));

            var builder = new UriBuilder(backendWebSocketUrl);
            if (builder.Scheme != "ws" && builder.Scheme != "wss")
                throw new ArgumentException("Backend URL must use ws or wss.", nameof(backendWebSocketUrl));

            string path = builder.Path.TrimEnd('/');
            builder.Path = path.EndsWith("/ws", StringComparison.OrdinalIgnoreCase) ? path : path + "/ws";
            builder.Query = string.Empty;
            return builder.Uri;
        }

        // Kept temporarily so existing prototype call sites still compile. The arguments are no longer used.
        public static Uri BuildRoomUri(
            string backendWebSocketUrl)
        {
            return BuildServerUri(backendWebSocketUrl);
        }

        public async Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken = default)
        {
            if (endpoint == null)
                throw new ArgumentNullException(nameof(endpoint));
            if (endpoint.Scheme != "ws" && endpoint.Scheme != "wss")
                throw new ArgumentException("Endpoint must use ws or wss.", nameof(endpoint));

            await lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (IsConnected)
                    throw new InvalidOperationException("The WebSocket is already connected.");

                CleanupConnection();

                var newSocket = new ClientWebSocket();
                newSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                try
                {
                    await newSocket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    newSocket.Dispose();
                    throw;
                }

                var cancellation = new CancellationTokenSource();
                lock (stateLock)
                {
                    socket = newSocket;
                    receiveCancellation = cancellation;
                    receivedEvents.Enqueue(QueuedEvent.ForConnected());
                    receiveTask = ReceiveLoopAsync(newSocket, cancellation.Token);
                }
            }
            finally
            {
                lifecycleLock.Release();
            }
        }

        public Task SendTextAsync(string message, CancellationToken cancellationToken = default)
        {
            return SendTextAsync(message, false, cancellationToken);
        }

        public Task SendTextAsync(
            string message,
            bool importantSend,
            CancellationToken cancellationToken = default)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(message);
            return SendAsync(bytes, WebSocketMessageType.Text, importantSend, cancellationToken);
        }

        public Task SendBinaryAsync(byte[] message, CancellationToken cancellationToken = default)
        {
            return SendBinaryAsync(message, false, cancellationToken);
        }

        public Task SendBinaryAsync(
            byte[] message,
            bool importantSend,
            CancellationToken cancellationToken = default)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            return SendAsync(message, WebSocketMessageType.Binary, importantSend, cancellationToken);
        }

        public Task SendPlayerInputAsync(
            byte[] payload,
            uint sequence,
            bool importantSend = false,
            CancellationToken cancellationToken = default)
        {
            return SendRealtimeAsync(
                PlayerInputKind,
                payload,
                sequence,
                importantSend,
                cancellationToken);
        }

        public Task SendPhysicsSnapshotAsync(
            string objectId,
            byte[] payload,
            uint sequence,
            bool importantSend = false,
            CancellationToken cancellationToken = default)
        {
            if (objectId == null)
                throw new ArgumentNullException(nameof(objectId));
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));

            byte[] objectIdBytes = System.Text.Encoding.UTF8.GetBytes(objectId);
            if (objectIdBytes.Length != PhysicsObjectIdBytes)
            {
                throw new ArgumentException(
                    $"Physics object ID must be exactly {PhysicsObjectIdBytes} UTF-8 bytes.",
                    nameof(objectId));
            }

            var snapshotPayload = new byte[PhysicsObjectIdBytes + payload.Length];
            Buffer.BlockCopy(objectIdBytes, 0, snapshotPayload, 0, PhysicsObjectIdBytes);
            Buffer.BlockCopy(payload, 0, snapshotPayload, PhysicsObjectIdBytes, payload.Length);

            return SendRealtimeAsync(
                PhysicsSnapshotKind,
                snapshotPayload,
                sequence,
                importantSend,
                cancellationToken);
        }

        public Task SendVoicePcmAsync(
            string networkId,
            byte[] pcm16,
            uint sequence,
            CancellationToken cancellationToken = default)
        {
            ValidateNetworkId(networkId);
            if (pcm16 == null)
                throw new ArgumentNullException(nameof(pcm16));
            if (pcm16.Length == 0 || pcm16.Length % 2 != 0)
                throw new ArgumentException("Voice payload must contain PCM16 samples.", nameof(pcm16));

            byte[] networkIdBytes = System.Text.Encoding.UTF8.GetBytes(networkId);
            var voicePayload = new byte[NetworkIdBytes + pcm16.Length];
            Buffer.BlockCopy(networkIdBytes, 0, voicePayload, 0, NetworkIdBytes);
            Buffer.BlockCopy(pcm16, 0, voicePayload, NetworkIdBytes, pcm16.Length);

            return SendRealtimeAsync(
                VoicePcmKind,
                voicePayload,
                sequence,
                false,
                cancellationToken);
        }

        /// <summary>
        /// Dispatches queued network events on the calling thread.
        /// Call this from the owning MonoBehaviour's Update method.
        /// </summary>
        public int Pump(int maxEvents = 256)
        {
            if (maxEvents <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxEvents));

            int processed = 0;
            while (processed < maxEvents && receivedEvents.TryDequeue(out QueuedEvent queued))
            {
                processed++;
                long lifetime = queued.Important ? importantEventLifetimeTicks : normalEventLifetimeTicks;
                if (Stopwatch.GetTimestamp() - queued.EnqueuedAt > lifetime)
                    continue;

                switch (queued.Type)
                {
                    case QueuedEventType.Connected:
                        InvokeSafely(Connected);
                        break;
                    case QueuedEventType.Text:
                        InvokeSafely(TextMessageReceived, queued.Text);
                        break;
                    case QueuedEventType.Binary:
                        DispatchBinary(queued.Binary);
                        break;
                    case QueuedEventType.Disconnected:
                        InvokeSafely(Disconnected, queued.CloseStatus, queued.CloseDescription);
                        break;
                    case QueuedEventType.Error:
                        InvokeTransportErrorSafely(queued.Error);
                        break;
                }
            }

            return processed;
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            await lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ClientWebSocket current;
                Task currentReceiveTask;
                lock (stateLock)
                {
                    current = socket;
                    currentReceiveTask = receiveTask;
                }

                if (current == null)
                    return;

                if (current.State == WebSocketState.Open || current.State == WebSocketState.CloseReceived)
                {
                    try
                    {
                        await current.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "client_disconnect",
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (WebSocketException)
                    {
                        current.Abort();
                    }
                }

                lock (stateLock)
                    receiveCancellation?.Cancel();

                if (currentReceiveTask != null)
                {
                    try
                    {
                        await currentReceiveTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }
            finally
            {
                CleanupConnection();
                lifecycleLock.Release();
            }
        }

        public void Dispose()
        {
            lock (stateLock)
            {
                if (disposed)
                    return;
                disposed = true;
                receiveCancellation?.Cancel();
                socket?.Abort();
            }

            CleanupConnection();
            while (receivedEvents.TryDequeue(out _)) { }
            lock (physicsHandlersLock)
                physicsSnapshotHandlers.Clear();
            lifecycleLock.Dispose();
            sendLock.Dispose();
        }

        private async Task SendAsync(
            byte[] message,
            WebSocketMessageType messageType,
            bool importantSend,
            CancellationToken cancellationToken)
        {
            int outgoingLength = message.Length + (importantSend ? 1 : 0);
            if (outgoingLength > maxMessageBytes)
                throw new InvalidOperationException($"Message exceeds the {maxMessageBytes}-byte limit.");

            byte[] outgoing = message;
            if (importantSend)
            {
                outgoing = new byte[outgoingLength];
                outgoing[0] = ImportantMarker;
                Buffer.BlockCopy(message, 0, outgoing, 1, message.Length);
            }

            await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                ClientWebSocket current;
                lock (stateLock)
                    current = socket;

                if (current == null || current.State != WebSocketState.Open)
                    throw new InvalidOperationException("The WebSocket is not connected.");

                await current.SendAsync(
                    new ArraySegment<byte>(outgoing),
                    messageType,
                    true,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                sendLock.Release();
            }
        }

        private Task SendRealtimeAsync(
            byte kind,
            byte[] payload,
            uint sequence,
            bool importantSend,
            CancellationToken cancellationToken)
        {
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));

            var frame = new byte[RealtimeHeaderBytes + payload.Length];
            frame[0] = RealtimeProtocolVersion;
            frame[1] = kind;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2, 2), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4, 4), sequence);
            BinaryPrimitives.WriteInt64LittleEndian(
                frame.AsSpan(8, 8),
                BitConverter.DoubleToInt64Bits(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            Buffer.BlockCopy(payload, 0, frame, RealtimeHeaderBytes, payload.Length);

            return SendBinaryAsync(frame, importantSend, cancellationToken);
        }

        private async Task ReceiveLoopAsync(ClientWebSocket current, CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            WebSocketCloseStatus? closeStatus = null;
            string closeDescription = null;

            try
            {
                while (!cancellationToken.IsCancellationRequested && current.State == WebSocketState.Open)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await current.ReceiveAsync(
                            new ArraySegment<byte>(buffer),
                            cancellationToken).ConfigureAwait(false);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            closeStatus = result.CloseStatus;
                            closeDescription = result.CloseStatusDescription;
                            break;
                        }

                        if (message.Length + result.Count > maxMessageBytes)
                            throw new InvalidDataException($"Received message exceeds the {maxMessageBytes}-byte limit.");

                        message.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;
                    
                    byte[] payload = message.ToArray();
                    bool important = payload.Length > 0 && payload[0] == ImportantMarker;
                    int payloadOffset = important ? 1 : 0;
                    if (payloadOffset == payload.Length)
                        throw new InvalidDataException("Received an empty important message.");

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string text = System.Text.Encoding.UTF8.GetString(
                            payload,
                            payloadOffset,
                            payload.Length - payloadOffset);
                        receivedEvents.Enqueue(QueuedEvent.ForText(text, important));
                    }
                    else if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        if (important)
                        {
                            var binary = new byte[payload.Length - 1];
                            Buffer.BlockCopy(payload, 1, binary, 0, binary.Length);
                            payload = binary;
                        }

                        if (TryDispatchRealtimeVoice(payload))
                            continue;

                        receivedEvents.Enqueue(QueuedEvent.ForBinary(payload, important));
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                receivedEvents.Enqueue(QueuedEvent.ForError(exception));
            }
            finally
            {
                bool ownsConnection;
                lock (stateLock)
                {
                    ownsConnection = ReferenceEquals(socket, current);
                    if (ownsConnection)
                    {
                        socket = null;
                        receiveTask = null;
                        receiveCancellation?.Dispose();
                        receiveCancellation = null;
                    }
                }

                current.Dispose();
                if (ownsConnection)
                    receivedEvents.Enqueue(QueuedEvent.ForDisconnected(closeStatus, closeDescription));
            }
        }

        private void CleanupConnection()
        {
            ClientWebSocket oldSocket;
            CancellationTokenSource oldCancellation;
            lock (stateLock)
            {
                oldSocket = socket;
                oldCancellation = receiveCancellation;
                socket = null;
                receiveTask = null;
                receiveCancellation = null;
            }

            oldCancellation?.Cancel();
            oldCancellation?.Dispose();
            oldSocket?.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(GameBackendWebSocketClient));
        }

        private void DispatchBinary(byte[] frame)
        {
            InvokeSafely(BinaryMessageReceived, frame);

            if (frame.Length < RealtimeHeaderBytes)
                return;

            if (frame[1] == VoicePcmKind)
            {
                if (frame.Length < RealtimeHeaderBytes + NetworkIdBytes)
                    return;

                string networkId = System.Text.Encoding.UTF8.GetString(
                    frame,
                    RealtimeHeaderBytes,
                    NetworkIdBytes);
                int voicePayloadOffset = RealtimeHeaderBytes + NetworkIdBytes;
                int payloadLength = frame.Length - voicePayloadOffset;
                if (payloadLength <= 0 || payloadLength % 2 != 0)
                    return;

                var pcm16 = new byte[payloadLength];
                Buffer.BlockCopy(frame, voicePayloadOffset, pcm16, 0, payloadLength);
                InvokeSafely(VoicePcmReceived, networkId, pcm16);
                return;
            }

            if (frame.Length < RealtimeHeaderBytes + PhysicsObjectIdBytes || frame[1] != PhysicsSnapshotKind)
                return;

            string objectId = System.Text.Encoding.UTF8.GetString(
                frame,
                RealtimeHeaderBytes,
                PhysicsObjectIdBytes);

            Action<byte[]>[] handlers = null;
            lock (physicsHandlersLock)
            {
                if (physicsSnapshotHandlers.TryGetValue(objectId, out var registered))
                    handlers = registered.ToArray();
            }

            if (handlers == null)
                return;

            int payloadOffset = RealtimeHeaderBytes + PhysicsObjectIdBytes;
            var payload = new byte[frame.Length - payloadOffset];
            Buffer.BlockCopy(frame, payloadOffset, payload, 0, payload.Length);
            foreach (var handler in handlers)
                InvokeSafely(handler, payload);
        }

        private bool TryDispatchRealtimeVoice(byte[] frame)
        {
            var callback = RealtimeVoicePcmReceived;
            if (callback == null ||
                frame.Length < RealtimeHeaderBytes + NetworkIdBytes ||
                frame[0] != RealtimeProtocolVersion ||
                frame[1] != VoicePcmKind)
            {
                return false;
            }

            string networkId = System.Text.Encoding.UTF8.GetString(
                frame,
                RealtimeHeaderBytes,
                NetworkIdBytes);
            int payloadOffset = RealtimeHeaderBytes + NetworkIdBytes;
            int payloadLength = frame.Length - payloadOffset;
            if (payloadLength <= 0 || payloadLength % 2 != 0)
                return true;

            var pcm16 = new byte[payloadLength];
            Buffer.BlockCopy(frame, payloadOffset, pcm16, 0, payloadLength);
            InvokeSafely(callback, networkId, pcm16);
            return true;
        }

        private static void ValidateNetworkId(string networkId)
        {
            if (networkId == null)
                throw new ArgumentNullException(nameof(networkId));
            if (System.Text.Encoding.UTF8.GetByteCount(networkId) != NetworkIdBytes)
            {
                throw new ArgumentException(
                    $"Network ID must be exactly {NetworkIdBytes} UTF-8 bytes.",
                    nameof(networkId));
            }
        }

        private static void ValidatePhysicsObjectId(string objectId)
        {
            if (objectId == null)
                throw new ArgumentNullException(nameof(objectId));
            if (System.Text.Encoding.UTF8.GetByteCount(objectId) != PhysicsObjectIdBytes)
            {
                throw new ArgumentException(
                    $"Physics object ID must be exactly {PhysicsObjectIdBytes} UTF-8 bytes.",
                    nameof(objectId));
            }
        }

        private static long SecondsToStopwatchTicks(double seconds)
        {
            return checked((long)(seconds * Stopwatch.Frequency));
        }

        private sealed class PhysicsSnapshotSubscription : IDisposable
        {
            private GameBackendWebSocketClient owner;
            private readonly string objectId;
            private readonly Action<byte[]> handler;

            public PhysicsSnapshotSubscription(
                GameBackendWebSocketClient owner,
                string objectId,
                Action<byte[]> handler)
            {
                this.owner = owner;
                this.objectId = objectId;
                this.handler = handler;
            }

            public void Dispose()
            {
                var current = Interlocked.Exchange(ref owner, null);
                current?.UnsubscribePhysicsSnapshot(objectId, handler);
            }
        }

        private void InvokeSafely(Action callback)
        {
            if (callback == null)
                return;
            foreach (Action handler in callback.GetInvocationList())
            {
                try { handler(); }
                catch (Exception exception) { InvokeTransportErrorSafely(exception); }
            }
        }

        private void InvokeSafely<T>(Action<T> callback, T value)
        {
            if (callback == null)
                return;
            foreach (Action<T> handler in callback.GetInvocationList())
            {
                try { handler(value); }
                catch (Exception exception) { InvokeTransportErrorSafely(exception); }
            }
        }

        private void InvokeSafely<T1, T2>(Action<T1, T2> callback, T1 first, T2 second)
        {
            if (callback == null)
                return;
            foreach (Action<T1, T2> handler in callback.GetInvocationList())
            {
                try { handler(first, second); }
                catch (Exception exception) { InvokeTransportErrorSafely(exception); }
            }
        }

        private void InvokeTransportErrorSafely(Exception exception)
        {
            var callback = TransportError;
            if (callback == null)
                return;
            foreach (Action<Exception> handler in callback.GetInvocationList())
            {
                try { handler(exception); }
                catch { }
            }
        }

        private enum QueuedEventType
        {
            Connected,
            Text,
            Binary,
            Disconnected,
            Error
        }

        private sealed class QueuedEvent
        {
            public QueuedEventType Type;
            public string Text;
            public byte[] Binary;
            public WebSocketCloseStatus? CloseStatus;
            public string CloseDescription;
            public Exception Error;
            public bool Important;
            public long EnqueuedAt = Stopwatch.GetTimestamp();

            public static QueuedEvent ForConnected() => new() { Type = QueuedEventType.Connected, Important = true };
            public static QueuedEvent ForText(string text, bool important) =>
                new() { Type = QueuedEventType.Text, Text = text, Important = important };
            public static QueuedEvent ForBinary(byte[] binary, bool important) =>
                new() { Type = QueuedEventType.Binary, Binary = binary, Important = important };
            public static QueuedEvent ForDisconnected(WebSocketCloseStatus? status, string description) =>
                new() { Type = QueuedEventType.Disconnected, CloseStatus = status, CloseDescription = description, Important = true };
            public static QueuedEvent ForError(Exception error) =>
                new() { Type = QueuedEventType.Error, Error = error, Important = true };
        }
    }
}
