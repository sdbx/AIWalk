using System;
using System.Collections.Concurrent;
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
        private readonly object stateLock = new();
        private readonly SemaphoreSlim lifecycleLock = new(1, 1);
        private readonly SemaphoreSlim sendLock = new(1, 1);
        private readonly ConcurrentQueue<QueuedEvent> receivedEvents = new();
        private readonly int maxMessageBytes;

        private ClientWebSocket socket;
        private CancellationTokenSource receiveCancellation;
        private Task receiveTask;
        private bool disposed;

        public GameBackendWebSocketClient(int maxMessageBytes = 256 * 1024)
        {
            if (maxMessageBytes < 1024)
                throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));

            this.maxMessageBytes = maxMessageBytes;
        }

        public event Action Connected;
        public event Action<string> TextMessageReceived;
        public event Action<byte[]> BinaryMessageReceived;
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
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(message);
            return SendAsync(bytes, WebSocketMessageType.Text, cancellationToken);
        }

        public Task SendBinaryAsync(byte[] message, CancellationToken cancellationToken = default)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            return SendAsync(message, WebSocketMessageType.Binary, cancellationToken);
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
                switch (queued.Type)
                {
                    case QueuedEventType.Connected:
                        InvokeSafely(Connected);
                        break;
                    case QueuedEventType.Text:
                        InvokeSafely(TextMessageReceived, queued.Text);
                        break;
                    case QueuedEventType.Binary:
                        InvokeSafely(BinaryMessageReceived, queued.Binary);
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
            lifecycleLock.Dispose();
            sendLock.Dispose();
        }

        private async Task SendAsync(
            byte[] message,
            WebSocketMessageType messageType,
            CancellationToken cancellationToken)
        {
            if (message.Length > maxMessageBytes)
                throw new InvalidOperationException($"Message exceeds the {maxMessageBytes}-byte limit.");

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
                    new ArraySegment<byte>(message),
                    messageType,
                    true,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                sendLock.Release();
            }
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
                    if (result.MessageType == WebSocketMessageType.Text)
                        receivedEvents.Enqueue(QueuedEvent.ForText(System.Text.Encoding.UTF8.GetString(payload)));
                    else if (result.MessageType == WebSocketMessageType.Binary)
                        receivedEvents.Enqueue(QueuedEvent.ForBinary(payload));
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

            public static QueuedEvent ForConnected() => new() { Type = QueuedEventType.Connected };
            public static QueuedEvent ForText(string text) => new() { Type = QueuedEventType.Text, Text = text };
            public static QueuedEvent ForBinary(byte[] binary) => new() { Type = QueuedEventType.Binary, Binary = binary };
            public static QueuedEvent ForDisconnected(WebSocketCloseStatus? status, string description) =>
                new() { Type = QueuedEventType.Disconnected, CloseStatus = status, CloseDescription = description };
            public static QueuedEvent ForError(Exception error) => new() { Type = QueuedEventType.Error, Error = error };
        }
    }
}
