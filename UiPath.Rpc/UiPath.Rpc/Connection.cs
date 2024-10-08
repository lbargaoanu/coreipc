﻿using MessagePack;
using Microsoft.IO;
namespace UiPath.Rpc;
using static IOHelpers;
public sealed class Connection : IDisposable
{
    internal static readonly MessagePackSerializerOptions Contractless = MessagePack.Resolvers.ContractlessStandardResolver.Options;
    private static readonly IOException ClosedException = new("Connection closed.");
    private readonly ConcurrentDictionary<int, OutgoingRequest> _requests = new();
    private int _requestCounter;
    private readonly int _maxMessageSize;
    private readonly Lazy<Task> _receiveLoop;
    private readonly SemaphoreSlim _sendLock = new(1);
    private readonly Action<object> _cancelRequest;
    private readonly byte[] _header = new byte[sizeof(long)];
    private readonly NestedStream _nestedStream;
    private RecyclableMemoryStream _messageStream;
    private int _requestIdToCancel;
    internal Connection(Stream network, ILogger logger, string name, int maxMessageSize = int.MaxValue)
    {
        Network = network;
        _nestedStream = new NestedStream(network, 0);
        Logger = logger;
        Name = $"{name} {GetHashCode()}";
        _maxMessageSize = maxMessageSize;
        _cancelRequest = CancelRequest;
        _receiveLoop = new(ReceiveLoop);
        void CancelRequest(object state)
        {
            var requestId = state == null ? _requestIdToCancel : (int)state;
            var data = SerializeMessage(new CancellationRequest(requestId), Serialize);
            SendMessage(MessageType.CancellationRequest, data, default).AsTask().LogException(Logger, this);
            Completion(requestId)?.SetCanceled();
        }
    }
    internal Server Server { get; private set; }
    Stream Network { get; }
    internal ILogger Logger { get; set; }
    internal bool LogEnabled => Logger.Enabled();
    internal string Name { get; }
    public override string ToString() => Name;
    internal int NewRequestId() => Interlocked.Increment(ref _requestCounter);
    internal Task Listen() => _receiveLoop.Value;
    public event EventHandler<EventArgs> Closed = delegate{};
    internal void SetServer(ListenerSettings settings, IClient client = null) => Server = new(settings, this, client);
    internal async ValueTask<TResult> RemoteCall<TResult>(Request request, CancellationToken token)
    {
        var requestCompletion = TaskCompletionPool<TResult>.Rent();
        var requestId = request.Id;
        Deserializer deserializer = typeof(TResult) == typeof(Stream) ? null : DeserializeResult<TResult>;
        _requests[requestId] = new(requestCompletion, deserializer);
        object cancellationState = Interlocked.CompareExchange(ref _requestIdToCancel, requestId, 0) == 0 ? null : requestId;
        var tokenRegistration = token.UnsafeRegister(_cancelRequest, cancellationState);
        try
        {
            await Send(request, token).ConfigureAwait(false);
        }
        catch
        {
            Completion(requestId)?.Return();
            Cleanup(cancellationState, tokenRegistration);
            throw;
        }
        try
        {
            return await requestCompletion.ValueTask().ConfigureAwait(false);
        }
        finally
        {
            _requests.TryRemove(requestId, out _);
            requestCompletion.Return();
            Cleanup(cancellationState, tokenRegistration);
        }
        void Cleanup(object cancellationState, CancellationTokenRegistration tokenRegistration)
        {
            tokenRegistration.Dispose();
            if (cancellationState == null)
            {
                _requestIdToCancel = 0;
            }
        }
    }
    internal ValueTask Send(in Request request, CancellationToken token)
    {
        if (request.Parameters is [Stream uploadStream, ..])
        {
            request.Parameters[0] = Contractless;
        }
        else
        {
            uploadStream = null;
        }
        var bytes = SerializeMessage(request, static(in Request request, ref MessagePackWriter writer)=>
        {
            Serialize(request, ref writer);
            foreach (var arg in request.Parameters)
            {
                if (arg != Contractless)
                {
                    Serialize(arg, ref writer);
                }
            }
        });
        return uploadStream == null ? SendMessage(MessageType.Request, bytes, token) : SendStream(MessageType.Request, bytes, uploadStream, token);
    }
    IErrorCompletion Completion(int requestId) => _requests.TryRemove(requestId, out var outgoingRequest) ? outgoingRequest.Completion : null;
    internal async ValueTask SendStream(MessageType messageType, RecyclableMemoryStream data, Stream userStream, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        CancellationTokenRegistration tokenRegistration = default;
        try
        {
            tokenRegistration = cancellationToken.UnsafeRegister(state => ((Connection)state).Dispose(), this);
            await Network.WriteMessage(messageType, data, cancellationToken).ConfigureAwait(false);
            var lengthBytes = BitConverter.GetBytes(userStream.Length);
            await
#if NET462
                Network.WriteAsync(lengthBytes, 0, lengthBytes.Length, cancellationToken)
#else
                Network.WriteAsync(lengthBytes, cancellationToken)
#endif
                .ConfigureAwait(false);
            const int DefaultCopyBufferSize = 81920;
            await userStream.CopyToAsync(Network, DefaultCopyBufferSize, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
            tokenRegistration.Dispose();
        }
    }
#if !NET462
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
#endif
    internal async ValueTask SendMessage(MessageType messageType, RecyclableMemoryStream data, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Network.WriteMessage(messageType, data, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }
    public void Dispose()
    {
        var closedHandler = Volatile.Read(ref Closed);
        if (closedHandler == null || Interlocked.Exchange(ref Closed, null) == null)
        {
            return;
        }
        _sendLock.AssertDisposed();
        Network.Dispose();
        Server?.CancelRequests();
        try
        {
            closedHandler.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log(ex);
        }
        foreach (var requestId in _requests.Keys)
        {
            Completion(requestId)?.SetException(ClosedException);
        }
    }
#if !NET462
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<bool> ReadHeader(int length)
    {
        int offset = 0;
        int toRead = length;
        do
        {
            var read = await Network.ReadAsync(
#if NET462
                _header, offset, toRead)
#else
                _header.AsMemory(offset, toRead))
#endif
                .ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }
            offset += read;
            toRead -= read;
        }
        while (toRead > 0);
        return true;
    }
    private async Task ReceiveLoop()
    {
        try
        {
            while (await ReadHeader(HeaderLength).ConfigureAwait(false))
            {
                ValueTask messageTask;
                using (_messageStream = NewMessage())
                {
#if NET462
                    await _nestedStream.CopyToAsync(_messageStream).ConfigureAwait(false);
#else
                    int read;
                    Memory<byte> memory;
                    while (true)
                    {
                        memory = _messageStream.GetMemory();
                        if ((read = await _nestedStream.ReadAsync(memory).ConfigureAwait(false)) == 0)
                        {
                            break;
                        }
                        _messageStream.Advance(read);
                    }
#endif
                    _messageStream.Position = 0;
                    messageTask = HandleMessage((MessageType)_header[0]);
                }
                await messageTask.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log(ex);
        }
        if (LogEnabled)
        {
            Log($"{nameof(ReceiveLoop)} {Name} finished.");
        }
        Dispose();
        return;
        ValueTask HandleMessage(MessageType messageType) => messageType switch
        {
            MessageType.Response => OnResponse(),
            MessageType.Request => Server.OnRequest(_nestedStream),
            MessageType.CancellationRequest => Server.OnCancel(),
            _ => Unknown(messageType),
        };
        ValueTask Unknown(MessageType messageType)
        {
            if (LogEnabled)
            {
                Log("Unknown message type " + messageType);
            }
            return default;
        }
        RecyclableMemoryStream NewMessage()
        {
            var length = BitConverter.ToInt32(_header, startIndex: 1);
            if (length > _maxMessageSize)
            {
                throw new InvalidDataException($"Message too large. The maximum message size is {_maxMessageSize / (1024 * 1024)} megabytes.");
            }
            _nestedStream.Reset(length);
            return GetStream(length);
        }
    }
    private ValueTask OnResponse()
    {
        var response = DeserializeMessage<Response>(out var reader);
        if (LogEnabled)
        {
            Log($"Received response for request {response.RequestId} {Name}.");
        }
        if (!_requests.TryRemove(response.RequestId, out var outgoingRequest))
        {
            return default;
        }
        var completion = outgoingRequest.Completion;
        if (response.Error == null)
        {
            var deserializer = outgoingRequest.Deserializer;
            if (deserializer == null)
            {
                return OnDownloadResponse(response, completion);
            }
            deserializer.Invoke(ref reader, completion);
        }
        else
        {
            completion.SetException(new RemoteException(response.Error));
        }
        return default;
        async ValueTask OnDownloadResponse(Response response, IErrorCompletion completion)
        {
            await EnterStreamMode().ConfigureAwait(false);
            var streamDisposed = new TaskCompletionSource<bool>();
            EventHandler disposedHandler = delegate { streamDisposed.TrySetResult(true); };
            _nestedStream.Disposed += disposedHandler;
            ((TaskCompletionPool<Stream>.ManualResetValueTaskSource)completion).SetResult(_nestedStream);
            await streamDisposed.Task.ConfigureAwait(false);
            _nestedStream.Disposed -= disposedHandler;
        }
    }
    internal async ValueTask EnterStreamMode()
    {
        if (!await ReadHeader(sizeof(long)).ConfigureAwait(false))
        {
            throw ClosedException;
        }
        var userStreamLength = BitConverter.ToInt64(_header, startIndex: 0);
        _nestedStream.Reset(userStreamLength);
    }
    internal static RecyclableMemoryStream SerializeMessage<T>(in T value, Serializer<T> serializer)
    {
        var stream = GetStream();
        try
        {
            stream.Position = HeaderLength;
            var writer = new MessagePackWriter(stream);
            serializer(value, ref writer);
            writer.Flush();
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
    internal static void Serialize<T>(in T value, ref MessagePackWriter writer) => MessagePackSerializer.Serialize(ref writer, value, Contractless);
    internal T DeserializeMessage<T>(out MessagePackReader reader)
    {
        reader = new(_messageStream.GetReadOnlySequence());
        return Deserialize<T>(ref reader);
    }
    internal static T Deserialize<T>(ref MessagePackReader reader) => MessagePackSerializer.Deserialize<T>(ref reader, Contractless);
    private void Log(Exception ex) => Logger.LogException(ex, Name);
    internal void Log(string message) => Logger.LogInformation(message);
    delegate void Deserializer(ref MessagePackReader reader, IErrorCompletion completion);
    static void DeserializeResult<T>(ref MessagePackReader reader, IErrorCompletion completion)
    {
        try
        {
            var result = Deserialize<T>(ref reader);
            ((TaskCompletionPool<T>.ManualResetValueTaskSource)completion).SetResult(result);
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
            throw;
        }
    }
    readonly record struct OutgoingRequest(IErrorCompletion Completion, Deserializer Deserializer);
}
delegate void Serializer<T>(in T value, ref MessagePackWriter writer);