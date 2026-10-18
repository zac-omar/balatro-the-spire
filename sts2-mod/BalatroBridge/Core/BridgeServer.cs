using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BalatroBridge.Core;

/// <summary>
/// Localhost TCP server that Balatro connects to, speaking newline-delimited JSON.
/// Network I/O runs on background threads, but every event is raised from <see cref="Pump"/>,
/// so handlers run on whichever thread pumps (the Godot main thread in-game).
/// Only one client is kept; a new connection replaces the old one.
/// </summary>
public sealed class BridgeServer : IDisposable
{
    private readonly int _port;
    private readonly Action<string> _log;
    private readonly ConcurrentQueue<Action> _inbox = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _clientLock = new();
    private TcpListener? _listener;
    private TcpClient? _client;
    private StreamWriter? _writer;
    private int _generation;

    public BridgeServer(int port, Action<string> log)
    {
        _port = port;
        _log = log;
    }

    public int Port => _port;

    /// <summary>Connection state as seen by the pumping thread.</summary>
    public bool IsConnected { get; private set; }

    public event Action? Connected;
    public event Action<string>? Disconnected;
    public event Action<BridgeMessage>? MessageReceived;

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        _log($"listening on 127.0.0.1:{_port}");
        _ = AcceptLoopAsync(_cts.Token);
    }

    /// <summary>Raises queued connection events and messages on the calling thread.</summary>
    public void Pump()
    {
        while (_inbox.TryDequeue(out Action? action))
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                _log($"bridge handler threw: {e}");
            }
        }
    }

    /// <summary>Sends a message to Balatro. Returns false if no client is connected or the write failed.</summary>
    public bool Send(BridgeMessage message)
    {
        string line = message.ToJson();
        lock (_clientLock)
        {
            if (_writer == null) return false;
            try
            {
                _writer.Write(line);
                _writer.Write('\n');
                _writer.Flush();
                return true;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                _log($"send failed: {e.Message}");
                DropClientLocked();
                return false;
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException e)
            {
                _log($"accept failed: {e.Message}");
                continue;
            }
            catch (Exception e)
            {
                _log($"accept loop stopped: {e}");
                return;
            }

            client.NoDelay = true;
            int generation;
            lock (_clientLock)
            {
                if (_client != null) _log("new connection replaces the existing one");
                DropClientLocked();
                _client = client;
                _writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = false };
                generation = ++_generation;
            }
            _log($"client connected from {client.Client.RemoteEndPoint}");
            _inbox.Enqueue(() =>
            {
                IsConnected = true;
                Connected?.Invoke();
            });
            _ = ReadLoopAsync(client, generation, ct);
        }
    }

    private async Task ReadLoopAsync(TcpClient client, int generation, CancellationToken ct)
    {
        string reason = "connection closed by Balatro";
        try
        {
            using var reader = new StreamReader(client.GetStream(), new UTF8Encoding(false));
            while (!ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                if (line.Length == 0) continue;
                BridgeMessage message;
                try
                {
                    message = BridgeMessage.Parse(line);
                }
                catch (Exception e) when (e is FormatException or InvalidOperationException or System.Text.Json.JsonException)
                {
                    _log($"ignored malformed message ({e.Message}): {Truncate(line, 200)}");
                    continue;
                }
                _inbox.Enqueue(() => MessageReceived?.Invoke(message));
            }
        }
        catch (Exception e)
        {
            // Any failure ends this connection; it must still be reported as a disconnect below.
            reason = e.Message;
        }

        lock (_clientLock)
        {
            if (generation != _generation) return; // already replaced by a newer connection
            DropClientLocked();
        }
        _inbox.Enqueue(() =>
        {
            IsConnected = false;
            Disconnected?.Invoke(reason);
        });
    }

    private void DropClientLocked()
    {
        _writer = null;
        _client?.Dispose();
        _client = null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    public void Dispose()
    {
        _cts.Cancel();
        _listener?.Stop();
        lock (_clientLock) DropClientLocked();
    }
}
