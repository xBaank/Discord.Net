namespace Discord.Audio;

using System;
using System.Threading.Tasks;

public partial class AudioClient
{
    private readonly AsyncEvent<Func<Task>> _connectedEvent = new();
    private readonly AsyncEvent<Func<Exception, Task>> _disconnectedEvent = new();
    private readonly AsyncEvent<Func<int, int, Task>> _latencyUpdatedEvent = new();
    private readonly AsyncEvent<Func<ulong, bool, Task>> _speakingUpdatedEvent = new();
    private readonly AsyncEvent<Func<ulong, AudioInStream, Task>> _streamCreatedEvent = new();
    private readonly AsyncEvent<Func<ulong, Task>> _streamDestroyedEvent = new();
    private readonly AsyncEvent<Func<int, int, Task>> _udpLatencyUpdatedEvent = new();

    public event Func<Task> Connected
    {
        add => _connectedEvent.Add(value);
        remove => _connectedEvent.Remove(value);
    }

    public event Func<Exception, Task> Disconnected
    {
        add => _disconnectedEvent.Add(value);
        remove => _disconnectedEvent.Remove(value);
    }

    public event Func<int, int, Task> LatencyUpdated
    {
        add => _latencyUpdatedEvent.Add(value);
        remove => _latencyUpdatedEvent.Remove(value);
    }

    public event Func<int, int, Task> UdpLatencyUpdated
    {
        add => _udpLatencyUpdatedEvent.Add(value);
        remove => _udpLatencyUpdatedEvent.Remove(value);
    }

    public event Func<ulong, AudioInStream, Task> StreamCreated
    {
        add => _streamCreatedEvent.Add(value);
        remove => _streamCreatedEvent.Remove(value);
    }

    public event Func<ulong, Task> StreamDestroyed
    {
        add => _streamDestroyedEvent.Add(value);
        remove => _streamDestroyedEvent.Remove(value);
    }

    public event Func<ulong, bool, Task> SpeakingUpdated
    {
        add => _speakingUpdatedEvent.Add(value);
        remove => _speakingUpdatedEvent.Remove(value);
    }
}
