using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace AIWalk.Networking
{
    public sealed class GameNetworkManager : MonoBehaviour
    {
        [SerializeField] private string backendUrl = "ws://localhost:8080";
        [SerializeField] private int maxMessageBytes = 256 * 1024;

        public static GameNetworkManager Instance { get; private set; }

        public GameBackendWebSocketClient Socket { get; private set; }
        public GameBackendEventClient Events { get; private set; }

        private void Awake()
        {
            // 첫 번째 매니저를 기본값으로만 사용한다. 중복 매니저는 파괴하지 않는다.
            if (Instance == null)
                Instance = this;

            Socket = new GameBackendWebSocketClient(maxMessageBytes);
            Events = new GameBackendEventClient(Socket);
        }

        public Task ConnectAsync(
            string roomId,
            string peerId,
            bool isHost,
            string hostToken = null,
            CancellationToken cancellationToken = default)
        {
            Uri uri = GameBackendWebSocketClient.BuildRoomUri(
                backendUrl);

            return Socket.ConnectAsync(uri, cancellationToken);
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            return Socket.DisconnectAsync(cancellationToken);
        }

        private void OnDestroy()
        {
            Events?.Dispose();
            Socket?.Dispose();

            if (ReferenceEquals(Instance, this))
                Instance = null;
        }
    }
}
