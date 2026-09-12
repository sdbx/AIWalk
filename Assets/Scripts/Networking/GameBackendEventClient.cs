using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace AIWalk.Networking
{
    public enum EventAudience
    {
        All,
        Others,
        Host,
        Clients,
        Api
    }

    /// <summary>
    /// Parsed envelope for a backend text event. PayloadJson remains independent of game logic.
    /// </summary>
    public sealed class GameBackendEvent
    {
        internal GameBackendEvent(WireEnvelope envelope, string payloadJson, string rawJson)
        {
            EventId = envelope.eventId;
            MessageId = envelope.messageId;
            SenderId = envelope.senderId;
            SenderRole = envelope.senderRole;
            RequestId = envelope.requestId;
            Sequence = envelope.sequence;
            SentAt = envelope.sentAt;
            ReceivedAt = envelope.receivedAt;
            PayloadJson = payloadJson;
            RawJson = rawJson;
        }

        public string EventId { get; }
        public string MessageId { get; }
        public string SenderId { get; }
        public string SenderRole { get; }
        public string RequestId { get; }
        public long Sequence { get; }
        public double SentAt { get; }
        public double ReceivedAt { get; }
        public string PayloadJson { get; }
        public string RawJson { get; }

        public T DeserializePayload<T>()
        {
            if (string.IsNullOrWhiteSpace(PayloadJson) || PayloadJson == "null")
                return default;

            var wrapper = JsonUtility.FromJson<PayloadWrapper<T>>("{\"value\":" + PayloadJson + "}");
            return wrapper == null ? default : wrapper.value;
        }
    }

    /// <summary>
    /// Plain C# event router over GameBackendWebSocketClient.
    /// Call GameBackendWebSocketClient.Pump from Unity's main thread to dispatch these callbacks safely.
    /// </summary>
    public sealed class GameBackendEventClient : IDisposable
    {
        private readonly object handlersLock = new();
        private readonly Dictionary<string, List<Action<GameBackendEvent>>> handlers = new(StringComparer.Ordinal);
        private readonly GameBackendWebSocketClient transport;
        private bool disposed;

        public GameBackendEventClient(GameBackendWebSocketClient transport)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            transport.TextMessageReceived += HandleTextMessage;
        }

        public event Action<GameBackendEvent> EventReceived;
        public event Action<Exception> EventError;

        public IDisposable Subscribe(string eventId, Action<GameBackendEvent> handler)
        {
            ThrowIfDisposed();
            ValidateEventId(eventId);
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            lock (handlersLock)
            {
                if (!handlers.TryGetValue(eventId, out var eventHandlers))
                {
                    eventHandlers = new List<Action<GameBackendEvent>>();
                    handlers.Add(eventId, eventHandlers);
                }
                eventHandlers.Add(handler);
            }

            return new Subscription(this, eventId, handler);
        }

        public void Unsubscribe(string eventId, Action<GameBackendEvent> handler)
        {
            if (string.IsNullOrEmpty(eventId) || handler == null)
                return;

            lock (handlersLock)
            {
                if (!handlers.TryGetValue(eventId, out var eventHandlers))
                    return;
                eventHandlers.Remove(handler);
                if (eventHandlers.Count == 0)
                    handlers.Remove(eventId);
            }
        }

        public Task PublishAsync<T>(
            string eventId,
            EventAudience audience,
            T payload,
            long? sequence = null,
            string requestId = null,
            CancellationToken cancellationToken = default)
        {
            string payloadJson = payload == null ? "null" : JsonUtility.ToJson(payload);
            return PublishRawAsync(eventId, audience, payloadJson, sequence, requestId, cancellationToken);
        }

        public Task PublishRawAsync(
            string eventId,
            EventAudience audience,
            string payloadJson = "null",
            long? sequence = null,
            string requestId = null,
            CancellationToken cancellationToken = default)
        {
            return SendAsync(
                eventId,
                Quote(GetAudienceName(audience)),
                payloadJson,
                sequence,
                requestId,
                cancellationToken);
        }

        public Task SendToPeerAsync<T>(
            string eventId,
            string peerId,
            T payload,
            long? sequence = null,
            string requestId = null,
            CancellationToken cancellationToken = default)
        {
            string payloadJson = payload == null ? "null" : JsonUtility.ToJson(payload);
            return SendToPeerRawAsync(eventId, peerId, payloadJson, sequence, requestId, cancellationToken);
        }

        public Task SendToPeerRawAsync(
            string eventId,
            string peerId,
            string payloadJson = "null",
            long? sequence = null,
            string requestId = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(peerId))
                throw new ArgumentException("Peer ID is required.", nameof(peerId));

            string targetJson = "{\"peerId\":" + Quote(peerId) + "}";
            return SendAsync(eventId, targetJson, payloadJson, sequence, requestId, cancellationToken);
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            transport.TextMessageReceived -= HandleTextMessage;
            lock (handlersLock)
                handlers.Clear();
        }

        private Task SendAsync(
            string eventId,
            string targetJson,
            string payloadJson,
            long? sequence,
            string requestId,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            ValidateEventId(eventId);
            if (string.IsNullOrWhiteSpace(payloadJson))
                payloadJson = "null";
            if (sequence < 0)
                throw new ArgumentOutOfRangeException(nameof(sequence));

            var json = new StringBuilder(128 + payloadJson.Length);
            json.Append("{\"eventId\":").Append(Quote(eventId));
            json.Append(",\"target\":").Append(targetJson);
            if (sequence.HasValue)
                json.Append(",\"sequence\":").Append(sequence.Value.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(requestId))
                json.Append(",\"requestId\":").Append(Quote(requestId));
            json.Append(",\"sentAt\":")
                .Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
            json.Append(",\"payload\":").Append(payloadJson).Append('}');
            return transport.SendTextAsync(json.ToString(), cancellationToken);
        }

        private void HandleTextMessage(string rawJson)
        {
            try
            {
                var envelope = JsonUtility.FromJson<WireEnvelope>(rawJson);
                if (envelope == null || string.IsNullOrWhiteSpace(envelope.eventId))
                    throw new FormatException("Backend event is missing eventId.");

                string payloadJson = TopLevelJson.GetRawProperty(rawJson, "payload") ?? "null";
                var gameEvent = new GameBackendEvent(envelope, payloadJson, rawJson);
                Dispatch(gameEvent);
            }
            catch (Exception exception)
            {
                DispatchError(exception);
            }
        }

        private void Dispatch(GameBackendEvent gameEvent)
        {
            Action<GameBackendEvent>[] eventHandlers = null;
            lock (handlersLock)
            {
                if (handlers.TryGetValue(gameEvent.EventId, out var registered))
                    eventHandlers = registered.ToArray();
            }

            InvokeHandlers(EventReceived, gameEvent);
            if (eventHandlers == null)
                return;
            foreach (var handler in eventHandlers)
                InvokeHandler(handler, gameEvent);
        }

        private void InvokeHandlers(Action<GameBackendEvent> callback, GameBackendEvent gameEvent)
        {
            if (callback == null)
                return;
            foreach (Action<GameBackendEvent> handler in callback.GetInvocationList())
                InvokeHandler(handler, gameEvent);
        }

        private void InvokeHandler(Action<GameBackendEvent> handler, GameBackendEvent gameEvent)
        {
            try { handler(gameEvent); }
            catch (Exception exception) { DispatchError(exception); }
        }

        private void DispatchError(Exception exception)
        {
            void Raise(object _)
            {
                var callback = EventError;
                if (callback == null)
                    return;
                foreach (Action<Exception> handler in callback.GetInvocationList())
                {
                    try { handler(exception); }
                    catch { }
                }
            }

            Raise(null);
        }

        private static string GetAudienceName(EventAudience audience)
        {
            return audience switch
            {
                EventAudience.All => "all",
                EventAudience.Others => "others",
                EventAudience.Host => "host",
                EventAudience.Clients => "clients",
                EventAudience.Api => "api",
                _ => throw new ArgumentOutOfRangeException(nameof(audience))
            };
        }

        private static string Quote(string value)
        {
            if (value == null)
                return "null";

            var output = new StringBuilder(value.Length + 2);
            output.Append('"');
            foreach (char character in value)
            {
                switch (character)
                {
                    case '"': output.Append("\\\""); break;
                    case '\\': output.Append("\\\\"); break;
                    case '\b': output.Append("\\b"); break;
                    case '\f': output.Append("\\f"); break;
                    case '\n': output.Append("\\n"); break;
                    case '\r': output.Append("\\r"); break;
                    case '\t': output.Append("\\t"); break;
                    default:
                        if (character < 0x20)
                            output.Append("\\u").Append(((int)character).ToString("x4"));
                        else
                            output.Append(character);
                        break;
                }
            }
            return output.Append('"').ToString();
        }

        private static void ValidateEventId(string eventId)
        {
            if (string.IsNullOrWhiteSpace(eventId) || eventId.Length > 128)
                throw new ArgumentException("Event ID must contain 1 to 128 characters.", nameof(eventId));
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(GameBackendEventClient));
        }

        private sealed class Subscription : IDisposable
        {
            private GameBackendEventClient owner;
            private readonly string eventId;
            private readonly Action<GameBackendEvent> handler;

            public Subscription(GameBackendEventClient owner, string eventId, Action<GameBackendEvent> handler)
            {
                this.owner = owner;
                this.eventId = eventId;
                this.handler = handler;
            }

            public void Dispose()
            {
                var current = Interlocked.Exchange(ref owner, null);
                current?.Unsubscribe(eventId, handler);
            }
        }
    }

#pragma warning disable CS0649 // Fields are assigned by JsonUtility reflection.
    [Serializable]
    internal sealed class WireEnvelope
    {
        public string eventId;
        public string messageId;
        public string senderId;
        public string senderRole;
        public string requestId;
        public long sequence;
        public double sentAt;
        public double receivedAt;
    }

    [Serializable]
    internal sealed class PayloadWrapper<T>
    {
        public T value;
    }
#pragma warning restore CS0649

    internal static class TopLevelJson
    {
        public static string GetRawProperty(string json, string propertyName)
        {
            int index = SkipWhitespace(json, 0);
            if (index >= json.Length || json[index] != '{')
                return null;
            index++;

            while (index < json.Length)
            {
                index = SkipWhitespace(json, index);
                if (index >= json.Length || json[index] == '}')
                    return null;

                int keyStart = index;
                index = SkipString(json, index);
                if (index < 0)
                    return null;
                string rawKey = json.Substring(keyStart, index - keyStart);

                index = SkipWhitespace(json, index);
                if (index >= json.Length || json[index++] != ':')
                    return null;

                index = SkipWhitespace(json, index);
                int valueStart = index;
                index = SkipValue(json, index);
                if (index < 0)
                    return null;

                if (rawKey == "\"" + propertyName + "\"")
                    return json.Substring(valueStart, index - valueStart);

                index = SkipWhitespace(json, index);
                if (index < json.Length && json[index] == ',')
                    index++;
            }

            return null;
        }

        private static int SkipValue(string json, int index)
        {
            if (index >= json.Length)
                return -1;
            if (json[index] == '"')
                return SkipString(json, index);
            if (json[index] == '{' || json[index] == '[')
            {
                char open = json[index];
                char close = open == '{' ? '}' : ']';
                int depth = 0;
                while (index < json.Length)
                {
                    char current = json[index];
                    if (current == '"')
                    {
                        index = SkipString(json, index);
                        if (index < 0)
                            return -1;
                        continue;
                    }
                    if (current == open)
                        depth++;
                    else if (current == close && --depth == 0)
                        return index + 1;
                    index++;
                }
                return -1;
            }

            while (index < json.Length && json[index] != ',' && json[index] != '}')
                index++;
            return index;
        }

        private static int SkipString(string json, int index)
        {
            if (index >= json.Length || json[index] != '"')
                return -1;
            index++;
            while (index < json.Length)
            {
                if (json[index] == '\\')
                {
                    index += 2;
                    continue;
                }
                if (json[index] == '"')
                    return index + 1;
                index++;
            }
            return -1;
        }

        private static int SkipWhitespace(string json, int index)
        {
            while (index < json.Length && char.IsWhiteSpace(json[index]))
                index++;
            return index;
        }
    }
}
