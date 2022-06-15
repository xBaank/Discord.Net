namespace Discord.Audio;

using API;
using API.Voice;
using Net;
using Net.Converters;
using Net.Udp;
using Net.WebSockets;
using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public class DiscordVoiceAPIClient : IDisposable
{
    #region DiscordVoiceAPIClient

    public const int MaxBitrate = 128 * 1024;
    public const string Mode = "xsalsa20_poly1305";

    public event Func<string, string, double, Task> SentRequest
    {
        add => _sentRequestEvent.Add(value);
        remove => _sentRequestEvent.Remove(value);
    }

    private readonly AsyncEvent<Func<string, string, double, Task>> _sentRequestEvent = new();

    public event Func<VoiceOpCode, Task> SentGatewayMessage
    {
        add => _sentGatewayMessageEvent.Add(value);
        remove => _sentGatewayMessageEvent.Remove(value);
    }

    private readonly AsyncEvent<Func<VoiceOpCode, Task>> _sentGatewayMessageEvent = new();

    public event Func<Task> SentDiscovery
    {
        add => _sentDiscoveryEvent.Add(value);
        remove => _sentDiscoveryEvent.Remove(value);
    }

    private readonly AsyncEvent<Func<Task>> _sentDiscoveryEvent = new();

    public event Func<int, Task> SentData
    {
        add => _sentDataEvent.Add(value);
        remove => _sentDataEvent.Remove(value);
    }

    private readonly AsyncEvent<Func<int, Task>> _sentDataEvent = new();

    public event Func<VoiceOpCode, object, Task> ReceivedEvent
    {
        add => _receivedEvent.Add(value);
        remove => _receivedEvent.Remove(value);
    }

    private readonly AsyncEvent<Func<VoiceOpCode, object, Task>> _receivedEvent = new();

    public event Func<byte[], Task> ReceivedPacket
    {
        add => _receivedPacketEvent.Add(value);
        remove => _receivedPacketEvent.Remove(value);
    }

    private readonly AsyncEvent<Func<byte[], Task>> _receivedPacketEvent = new();

    public event Func<Exception, Task> Disconnected
    {
        add => _disconnectedEvent.Add(value);
        remove => _disconnectedEvent.Remove(value);
    }

    private readonly AsyncEvent<Func<Exception, Task>> _disconnectedEvent = new();

    private readonly JsonSerializer _serializer;
    private readonly SemaphoreSlim _connectionLock;
    private readonly IUdpSocket _udp;
    private CancellationTokenSource _connectCancelToken;
    private bool _isDisposed;
    private ulong _nextKeepalive;

    public ulong GuildId { get; }

    internal IWebSocketClient WebSocketClient { get; }

    public ConnectionState ConnectionState { get; private set; }

    public ushort UdpPort => _udp.Port;

    internal DiscordVoiceAPIClient(ulong guildId, WebSocketProvider webSocketProvider, UdpSocketProvider udpSocketProvider, JsonSerializer serializer = null)
    {
        GuildId = guildId;
        _connectionLock = new SemaphoreSlim(1, 1);
        _udp = udpSocketProvider();
        _udp.ReceivedDatagram += async (data, index, count) =>
        {
            if (index != 0 || count != data.Length)
            {
                var newData = new byte[count];
                Buffer.BlockCopy(data, index, newData, 0, count);
                data = newData;
            }
            await _receivedPacketEvent.InvokeAsync(data).ConfigureAwait(false);
        };

        WebSocketClient = webSocketProvider();
        //_gatewayClient.SetHeader("user-agent", DiscordConfig.UserAgent); //(Causes issues in .Net 4.6+)
        WebSocketClient.BinaryMessage += async (data, index, count) =>
        {
            using (var compressed = new MemoryStream(data, index + 2, count - 2))
            using (var decompressed = new MemoryStream())
            {
                using (var zlib = new DeflateStream(compressed, CompressionMode.Decompress))
                    zlib.CopyTo(decompressed);
                decompressed.Position = 0;
                using (var reader = new StreamReader(decompressed))
                {
                    var msg = JsonConvert.DeserializeObject<SocketFrame>(reader.ReadToEnd());
                    await _receivedEvent.InvokeAsync((VoiceOpCode)msg.Operation, msg.Payload).ConfigureAwait(false);
                }
            }
        };
        WebSocketClient.TextMessage += async text =>
        {
            var msg = JsonConvert.DeserializeObject<SocketFrame>(text);
            await _receivedEvent.InvokeAsync((VoiceOpCode)msg.Operation, msg.Payload).ConfigureAwait(false);
        };
        WebSocketClient.Closed += async ex =>
        {
            /*var ex2 = ex as WebSocketClosedException;

            if (ex2?.CloseCode == 4006) return;*/
            //await DisconnectAsync().ConfigureAwait(false);
            await _disconnectedEvent.InvokeAsync(ex).ConfigureAwait(false);
        };

        _serializer = serializer ?? new JsonSerializer { ContractResolver = new DiscordContractResolver() };
    }

    private void Dispose(bool disposing)
    {
        if (_isDisposed)
            return;
        if (disposing)
        {
            _connectCancelToken?.Dispose();
            _udp?.Dispose();
            WebSocketClient?.Dispose();
            _connectionLock?.Dispose();
        }
        _isDisposed = true;
    }

    public void Dispose() => Dispose(true);

    public async Task SendAsync(VoiceOpCode opCode, object payload, RequestOptions options = null)
    {
        byte[] bytes = null;
        payload = new SocketFrame { Operation = (int)opCode, Payload = payload };
        if (payload != null)
            bytes = Encoding.UTF8.GetBytes(SerializeJson(payload));
        await WebSocketClient.SendAsync(bytes, 0, bytes.Length, true).ConfigureAwait(false);
        await _sentGatewayMessageEvent.InvokeAsync(opCode).ConfigureAwait(false);
    }

    public async Task SendAsync(byte[] data, int offset, int bytes)
    {
        await _udp.SendAsync(data, offset, bytes).ConfigureAwait(false);
        await _sentDataEvent.InvokeAsync(bytes).ConfigureAwait(false);
    }

    #endregion

    #region WebSocket

    public async Task SendHeartbeatAsync(RequestOptions options = null) => await SendAsync(VoiceOpCode.Heartbeat, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), options).ConfigureAwait(false);

    public async Task SendIdentityAsync(ulong userId, string sessionId, string token) => await SendAsync(VoiceOpCode.Identify, new IdentifyParams
    {
        GuildId = GuildId, UserId = userId, SessionId = sessionId, Token = token,
    }).ConfigureAwait(false);

    public async Task SendSelectProtocol(string externalIp, int externalPort) => await SendAsync(VoiceOpCode.SelectProtocol, new SelectProtocolParams { Protocol = "udp", Data = new UdpProtocolInfo { Address = externalIp, Port = externalPort, Mode = Mode } }).ConfigureAwait(false);

    public async Task SendSetSpeaking(bool value) => await SendAsync(VoiceOpCode.Speaking, new SpeakingParams { IsSpeaking = value, Delay = 0 }).ConfigureAwait(false);

    public async Task ConnectAsync(string url)
    {
        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await ConnectInternalAsync(url).ConfigureAwait(false);
        }
        finally { _connectionLock.Release(); }
    }

    private async Task ConnectInternalAsync(string url)
    {
        ConnectionState = ConnectionState.Connecting;
        try
        {
            _connectCancelToken?.Dispose();
            _connectCancelToken = new CancellationTokenSource();
            var cancelToken = _connectCancelToken.Token;

            WebSocketClient.SetCancelToken(cancelToken);
            await WebSocketClient.ConnectAsync(url).ConfigureAwait(false);

            _udp.SetCancelToken(cancelToken);
            await _udp.StartAsync().ConfigureAwait(false);

            ConnectionState = ConnectionState.Connected;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            await DisconnectInternalAsync(false).ConfigureAwait(false);
            throw;
        }
    }

    public async Task DisconnectAsync(bool isChangingChannel = false)
    {
        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectInternalAsync(isChangingChannel).ConfigureAwait(false);
        }
        finally { _connectionLock.Release(); }
    }

    private async Task DisconnectInternalAsync(bool isChangingChannel)
    {
        if (ConnectionState == ConnectionState.Disconnected)
            return;

        ConnectionState = ConnectionState.Disconnecting;
        try
        {
            _connectCancelToken?.Cancel(false);
        }
        catch { }

        //Wait for tasks to complete

        await _udp.StopAsync().ConfigureAwait(false);

        if (isChangingChannel)
            return;

        await WebSocketClient.DisconnectAsync().ConfigureAwait(false);
        ConnectionState = ConnectionState.Disconnected;
    }

    #endregion

    #region Udp

    public async Task SendDiscoveryAsync(uint ssrc)
    {
        var packet = new byte[70];
        packet[0] = (byte)(ssrc >> 24);
        packet[1] = (byte)(ssrc >> 16);
        packet[2] = (byte)(ssrc >> 8);
        packet[3] = (byte)(ssrc >> 0);
        await SendAsync(packet, 0, 70).ConfigureAwait(false);
        await _sentDiscoveryEvent.InvokeAsync().ConfigureAwait(false);
    }

    public async Task<ulong> SendKeepaliveAsync()
    {
        var value = _nextKeepalive++;
        var packet = new byte[8];
        packet[0] = (byte)(value >> 0);
        packet[1] = (byte)(value >> 8);
        packet[2] = (byte)(value >> 16);
        packet[3] = (byte)(value >> 24);
        packet[4] = (byte)(value >> 32);
        packet[5] = (byte)(value >> 40);
        packet[6] = (byte)(value >> 48);
        packet[7] = (byte)(value >> 56);
        await SendAsync(packet, 0, 8).ConfigureAwait(false);
        return value;
    }

    public void SetUdpEndpoint(string ip, int port) => _udp.SetDestination(ip, port);

    #endregion

    #region Helpers

    private static double ToMilliseconds(Stopwatch stopwatch) => Math.Round(stopwatch.ElapsedTicks / (double)Stopwatch.Frequency * 1000.0, 2);

    private string SerializeJson(object value)
    {
        var sb = new StringBuilder(256);
        using (TextWriter text = new StringWriter(sb, CultureInfo.InvariantCulture))
        using (JsonWriter writer = new JsonTextWriter(text))
            _serializer.Serialize(writer, value);
        return sb.ToString();
    }

    private T DeserializeJson<T>(Stream jsonStream)
    {
        using (TextReader text = new StreamReader(jsonStream))
        using (JsonReader reader = new JsonTextReader(text))
            return _serializer.Deserialize<T>(reader);
    }

    #endregion
}
