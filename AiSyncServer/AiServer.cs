﻿using AiSync;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

using WatsonTcp;
using LibVLCSharp.Shared;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;

namespace AiSyncServer {
    internal enum AiConnectionState {
        Closed,
        Connected,
        SentHasFile,
    }

    internal class AiClientConnection {
        public AiConnectionState State { get; set; } = AiConnectionState.Closed;
    }

    internal class PlayingChangedEventArgs : EventArgs {
        public bool IsPlaying { get; }

        public PlayingChangedEventArgs(bool new_status) : base() {
            IsPlaying = new_status;
        }
    }

    internal class PositionChangedEvent : EventArgs {
        public long Position { get; }

        public PositionChangedEvent(long position) :base() {
            Position = position;
        }
    }

    internal class AiServer : IDisposable {
        
        public event EventHandler? ServerStarted;
        public event EventHandler? ClientConnected;
        public event EventHandler? PlaybackStopped;

        public event EventHandler<PlayingChangedEventArgs>? PlayingChanged;
        public event EventHandler<PositionChangedEvent>? PositionChanged;

        private readonly ILogger _logger;

        private readonly WatsonTcpServer server;

        public AiServer(ILoggerFactory fact, IPAddress addr, ushort port) : this(fact, new IPEndPoint(addr, port)) { }

        private readonly ConcurrentDictionary<Guid, AiClientConnection> clients = new();

        private LibVLC? _vlc;
        private LibVLC VLC { get => _vlc ??= new(); }

        private Media? media;

        [MemberNotNullWhen(true, nameof(Duration))]
        public bool HasMedia => media is not null;

        public long? Duration => media?.Duration;

        private readonly object control_lock = new();

        public bool Playing { get; private set; } = false;

        private DateTime? latest_start;
        private long latest_start_pos = 0;
        private bool during_resync = false;

        public long Position {
            get {
                if (latest_start is not null) {
                    /* If playing, calculate, else latest_start_pos already contains right value */
                    if (Playing) {
                        TimeSpan? elapsed = DateTime.UtcNow - latest_start;

                        return (long)Math.Round(elapsed.Value.TotalMilliseconds) + latest_start_pos;
                    } else {
                        return latest_start_pos;
                    }
                }

                /* Playback hasn't started yet */
                return 0;
            }

            private set {
                latest_start = DateTime.UtcNow;
                latest_start_pos = value;
            }
        }

        public IEnumerable<Guid> Clients { get => clients.Keys; }
        public int ClientCount { get => clients.Count; }

        public static long CloseEnoughValue { get => 1500; }

        private Thread? poll_thread;

        private CancellationTokenSource cancel_poll = new();

        public AiServer(ILoggerFactory fact, IPEndPoint endpoint) {
            _logger = fact.CreateLogger("AiServer");

            server = new WatsonTcpServer(endpoint.Address.ToString(), endpoint.Port) ;

            server.Events.MessageReceived += OnMessageReceived;
            server.Events.ClientConnected += OnClientConnected;
            server.Events.ClientDisconnected += OnClientDisconnected;

            server.Callbacks.SyncRequestReceived = OnSyncRequestReceived;

            server.Events.ServerStarted += (_, _) =>
                ServerStarted?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose() {
            if (server.IsListening) {
                server.Stop();
            }

            if (!cancel_poll.IsCancellationRequested) {
                cancel_poll.Cancel();
                poll_thread?.Join();
            }

            server.Dispose();
            cancel_poll.Dispose();
            media?.Dispose();
            _vlc?.Dispose();
        }

        public void Start() {
            _logger.LogInformation("Starting server");
            server.Start();
        }

        public void Stop() {
            cancel_poll.Cancel();
            poll_thread?.Join();
        }

        public async void Play(long pos) {
            lock (control_lock) {
                if (Playing) {
                    /* Ignore if already playing */
                    return;
                }

                Playing = true;

                PlayingChanged?.Invoke(this, new PlayingChangedEventArgs(Playing));
            }

            AiServerRequestsPlay new_msg = new() { Position = pos };

            await Task.WhenAll(Clients.Select(guid => SendMessage(guid, new_msg)).ToArray());

            Position = pos;
        }

        public async void Pause(long pos) {
            lock (control_lock) {
                if (!Playing) {
                    /* Ignore if not playing */
                    return;
                }

                Playing = false;

                PlayingChanged?.Invoke(this, new PlayingChangedEventArgs(Playing));
            }

            AiServerRequestsPause new_msg = new() { Position = pos };

            await Task.WhenAll(Clients.Select(guid => SendMessage(guid, new_msg)).ToArray());

            Position = pos;
        }

        public async void StopPlayback() {
            if (media is null) {
                _logger.LogWarning("Attempt to stop playback with no file present");
                return;
            }

            _logger.LogInformation("Stopping playback");
            _logger.LogDebug("Cancelled from poll thread: {}", poll_thread == Thread.CurrentThread);

            if (poll_thread != Thread.CurrentThread && poll_thread is not null && poll_thread.IsAlive) {
                _logger.LogDebug("Cancelling polling thread");
                cancel_poll.Cancel();
                poll_thread.Join();
                poll_thread = null;

                /* Reset needed */
                cancel_poll.Dispose();
                cancel_poll = new CancellationTokenSource();
            }

            media?.Dispose();
            media = null;

            lock (control_lock) {
                Playing = false;
                Position = 0;

                PlaybackStopped?.Invoke(this, EventArgs.Empty);
            }

            AiFileClosed msg = new();
            await Task.WhenAll(Clients.Select(guid => SendMessage(guid, msg)).ToArray());
        }

        public async void SetFile(string path) {
            if (media is not null) {
                _logger.LogCritical("Tried to open a file while one is already open");
                throw new InvalidOperationException("SetFile has already been called");
            }

            _logger.LogInformation("New file received: {}", path);
            media = new Media(VLC, new Uri(path));
            MediaParsedStatus status = await media.Parse();

            if (status != MediaParsedStatus.Done) {
                media?.Dispose();
                media = null;
                PlaybackStopped?.Invoke(this, EventArgs.Empty);
            }

            Task<(Guid, bool)>[] tasks = Clients
                .Select(SendAndWaitNewFileAsync)
                .ToArray();

            await Task.WhenAll(tasks);
                    
            /* Remove all failed clients */
            foreach ((Guid guid, bool _) in tasks.Select(t => t.Result).Where(result => !result.Item2)) {
                _logger.LogInformation("Removing {} for failing to reply", guid);
                server.DisconnectClient(guid);
            }

            _logger.LogInformation("All clients ready to play");

            /* Tell all clients the server is ready */
            await Task.WhenAll(
                Clients
                    .Select(guid => SendMessage<AiServerReady>(guid))
                    .ToArray()
            );

            /* Start status polling thread */
            poll_thread = new Thread(() => PlaybackSyncPoll(cancel_poll.Token));
            poll_thread.Start();
        }

        private void PlaybackSyncPoll(CancellationToken ct) {
            const int poll_interval = 100;

            _logger.LogInformation("Starting polling thread");

            Random rnd = new();

            DateTime start = DateTime.Now;
            while (!ct.IsCancellationRequested) {
                while (latest_start is null) {
                    Thread.Sleep(poll_interval);
                    if (ct.IsCancellationRequested) {
                        return;
                    }
                }

                /* Add some randomness because it looks better */
                Thread.Sleep(poll_interval - rnd.Next(0, 25));
                PositionChanged?.Invoke(this, new PositionChangedEvent(Position));

                if (Position >= media?.Duration) {
                    /* EOF */
                    StopPlayback();
                    return;
                }
            }
        }

        private Resp SendAndExpectMsg<Msg, Resp>(int ms, Guid guid, Msg src)
            where Msg : AiProtocolMessage
            where Resp : AiProtocolMessage {
            SyncResponse response = server.SendAndWait(ms, guid, src.Serialize());

            return response.Data.ToUtf8().AiDeserialize<Resp>();
        }

        private Task SendMessage<T>(Guid guid, T? msg = null) where T : AiProtocolMessage, new() {
            return server.SendAsync(guid, (msg ?? new T()).Serialize());
        }

        private (Guid, bool) SendAndWaitNewFile(Guid guid) {
            /* Notify client of new file, wait at most 1 minute for a response */
            try {
                SendAndExpectMsg<AiFileReady, AiFileParsed>(60 * 1000, guid, AiFileReady.FromCloseEnough(CloseEnoughValue));

                /* No exception means this client is ready */
                return (guid, true);

            /* Consume exceptions that indicate failure */
            } catch (TimeoutException) {
                
            } catch (InvalidMessageException) {

            }

            return (guid, false);
        }

        private Task<(Guid, bool)> SendAndWaitNewFileAsync(Guid guid) {
            return Task.Run(() => SendAndWaitNewFile(guid));
        }

        private AiClientStatus? GetClientStatus(Guid guid) {
            try {
                return SendAndExpectMsg<AiServerRequestsStatus, AiClientStatus>(5 * 1000, guid, new());
            } catch (TimeoutException) {
                
            } catch (InvalidMessageException) {

            }

            return null;
        }

        private void OnMessageReceived(object? sender, MessageReceivedEventArgs e) {
            string str = e.Data.ToUtf8();
            AiMessageType type = str.AiJsonMessageType();

            Delegate handler = str.AiJsonMessageType() switch {
                AiMessageType.ClientRequestsPause => HandleClientRequestsPause,
                AiMessageType.ClientRequestsPlay => HandleClientRequestsPlay,
                AiMessageType.ClientRequestsSeek => HandleClientRequestsSeek,
                AiMessageType.PauseResync => HandlePauseRsync,
                _ => HandleDefault,
            };

            ParameterInfo[] handler_params = handler.GetMethodInfo().GetParameters();

            /* Should never happen */
            if (handler_params.Length != 1 || !handler_params[0].ParameterType.IsAssignableTo(typeof(AiProtocolMessage))) {
                throw new ArgumentException("invalid delegate");
            }

            handler.DynamicInvoke(str.AiDeserialize(handler_params[0].ParameterType));
        }

        private async void OnClientConnected(object? sender, ConnectionEventArgs e) {
            _logger.LogInformation("Client connected: {}", e.Client.Guid);

            clients.TryAdd(e.Client.Guid, new AiClientConnection() { State = AiConnectionState.Connected });

            ClientConnected?.Invoke(this, EventArgs.Empty);

            if (media is not null) {
                _logger.LogInformation("Sending status to {}", e.Client.Guid);

                (Guid _, bool success) = await SendAndWaitNewFileAsync(e.Client.Guid);
                if (success) {
                    await SendMessage<AiServerReady>(e.Client.Guid);
                } else if (success) {
                    _logger.LogInformation("Client rejected: {}", e.Client.Guid);
                    server.DisconnectClient(e.Client.Guid);
                    return;

                }
            }
        }

        private void OnClientDisconnected(object? sender, DisconnectionEventArgs e) {
            _logger.LogInformation("Client disconnected: {}, reason: {}", e.Client.Guid, e.Reason);
            clients.TryRemove(e.Client.Guid, out AiClientConnection _);
        }

        private SyncResponse OnSyncRequestReceived(SyncRequest req) {
            string str = req.Data.ToUtf8();
            AiMessageType type = str.AiJsonMessageType();

            switch (type) {
                case AiMessageType.GetStatus: {
                    return req.ReplyWith(new AiServerStatus() {
                        IsPlaying = Playing,
                        Position = Position,
                    });
                }

                default:
                    throw new InvalidMessageException(str.AiDeserialize<AiProtocolMessage>());
            }
        }

        private void HandleClientRequestsPause(AiClientRequestsPause msg) {
            _logger.LogDebug("ClientRequestsPause: {}", msg.Position);

            long pos = (msg.Position < 0) ? Position : msg.Position; // Int64.Clamp(msg.Position, 0, Int64.MaxValue);

            _logger.LogInformation("Pause requested at {}", AiSync.Utils.FormatTime(pos));

            Pause(pos);
        }

        private void HandleClientRequestsPlay(AiClientRequestsPlay msg) {
            _logger.LogDebug("ClientRequestsPlay: {}", msg.Position);

            long pos = (msg.Position < 0) ? Position : msg.Position; // Int64.Clamp(msg.Position, 0, Int64.MaxValue);

            _logger.LogInformation("Play requested at {}", AiSync.Utils.FormatTime(pos));

            Play(pos);
        }

        private async void HandleClientRequestsSeek(AiClientRequestSeek msg) {
            _logger.LogDebug("ClientRequestsSeek: {}", msg.Target);
            long target = (msg.Target < 0) ? Position : msg.Target; // Int64.Clamp(msg.Target, 0, Int64.MaxValue);

            _logger.LogInformation("Seek requested to {}", AiSync.Utils.FormatTime(msg.Target));

            lock (control_lock) {
                Position = target;
                PositionChanged?.Invoke(this, new PositionChangedEvent(Position));
            }

            await Task.WhenAll(Clients.Select(guid => SendMessage(guid, new AiServerRequestSeek() { Target = Position })).ToArray());
        }

        private async void HandlePauseRsync(AiPauseResync msg) {
            _logger.LogDebug("PauseResync");
            if (during_resync) {
                return;
            }

            during_resync = true;
            DateTime start = DateTime.Now;
            long target_pos = Position;

            Task<AiClientStatus?>[] tasks = Clients.Select(async guid => {
                return await Task.Run(() => GetClientStatus(guid));
            }).ToArray();

            await Task.WhenAll(tasks);

            foreach (AiClientStatus status in tasks.Select(t => t.Result).WhereNotNull()) {
                /* TODO do stuff with `playing`, maybe? */

                /* Adjust position to same start time */
                double delta = (start - status.Timestamp).TotalMilliseconds;

                target_pos = Int64.Min(target_pos, (long)Math.Round(status.Position + delta));
            }

            _logger.LogInformation("Resync pausing at {}", AiSync.Utils.FormatTime(target_pos));

            Pause(target_pos);

            during_resync = false;
        }

        private void HandleDefault(Guid client, AiProtocolMessage msg) {
            _logger.LogCritical("Unexpected message of type {} from {}", msg.Type, client);
            throw new InvalidMessageException(msg);
        }
    }
}
