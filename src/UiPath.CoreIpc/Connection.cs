﻿using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
namespace UiPath.CoreIpc
{
    using RequestCompletionSource = TaskCompletionSource<Response>;
    using static IOHelpers;
    public sealed class Connection : IDisposable
    {
        private readonly ConcurrentDictionary<string, RequestCompletionSource> _requests = new();
        private long _requestCounter = -1;
        private readonly int _maxMessageSize;
        private readonly Lazy<Task> _receiveLoop;
        private readonly SemaphoreSlim _sendLock = new(1);
        private readonly Action<object> _onResponse;
        private readonly Action<object> _onRequest;
        private readonly Action<object> _onCancellation;
        private readonly Action<object> _cancelRequest;
        private readonly Action<object> _cancelUploadRequest;
        private readonly byte[] _buffer = new byte[sizeof(long)];
        private readonly NestedStream _nestedStream;
        public Connection(Stream network, ISerializer serializer, ILogger logger, string name, int maxMessageSize = int.MaxValue)
        {
            Network = network;
            _nestedStream = new NestedStream(network, 0);
            Serializer = serializer;
            Logger = logger;
            Name = $"{name} {GetHashCode()}";
            _maxMessageSize = maxMessageSize;
            _receiveLoop = new(ReceiveLoop);
            _onResponse = response => OnResponseReceived((Response)response);
            _onRequest = request => OnRequestReceived((Request)request);
            _onCancellation = requestId => OnCancellationReceived((string)requestId);
            _cancelRequest = requestId => CancelRequest((string)requestId);
            _cancelUploadRequest = requestId => CancelUploadRequest((string)requestId);
        }
        public Stream Network { get; }
        public ILogger Logger { get; internal set; }
        public bool LogEnabled => Logger.Enabled();
        public string Name { get; }
        public ISerializer Serializer { get; }
        public override string ToString() => Name;
        public string NewRequestId() => Interlocked.Increment(ref _requestCounter).ToString();
        public Task Listen() => _receiveLoop.Value;
        internal event Func<Request, Task> RequestReceived;
        internal event Action<string> CancellationReceived;
        public event EventHandler<EventArgs> Closed;
        internal async Task<Response> RemoteCall(Request request, CancellationToken token)
        {
            var requestCompletion = new RequestCompletionSource();
            _requests[request.Id] = requestCompletion;
            CancellationTokenRegistration tokenRegistration = default;
            try
            {
                await Send(request, token);
                tokenRegistration = token.Register(request.UploadStream == null ? _cancelRequest : _cancelUploadRequest, request.Id);
                return await requestCompletion.Task;
            }
            finally
            {
                tokenRegistration.Dispose();
                _requests.TryRemove(request.Id, out _);
            }
        }
        internal Task Send(Request request, CancellationToken token)
        {
            Debug.Assert(request.Parameters == null || request.ObjectParameters == null);
            var requestBytes = SerializeToStream(request);
            return request.UploadStream == null ?
                SendMessage(MessageType.Request, requestBytes, token) :
                SendStream(MessageType.UploadRequest, requestBytes, request.UploadStream, token);
        }
        void CancelRequest(string requestId)
        {
            CancelServerCall(requestId).LogException(Logger, this);
            TryCancelRequest(requestId);
            return;
            Task CancelServerCall(string requestId) =>
                SendMessage(MessageType.CancellationRequest, SerializeToStream(new CancellationRequest(requestId)), default);
        }
        void CancelUploadRequest(string requestId)
        {
            Dispose();
            TryCancelRequest(requestId);
        }
        private void TryCancelRequest(string requestId)
        {
            if (_requests.TryGetValue(requestId, out var requestCompletion))
            {
                requestCompletion.TrySetCanceled();
            }
        }
        internal Task Send(Response response, CancellationToken cancellationToken)
        {
            Debug.Assert(response.Data == null || response.ObjectData == null);
            var responseBytes = SerializeToStream(response);
            return response.DownloadStream == null ?
                SendMessage(MessageType.Response, responseBytes, cancellationToken) :
                SendDownloadStream(responseBytes, response.DownloadStream, cancellationToken);
        }
        private async Task SendDownloadStream(MemoryStream responseBytes, Stream downloadStream, CancellationToken cancellationToken)
        {
            using (downloadStream)
            {
                await SendStream(MessageType.DownloadResponse, responseBytes, downloadStream, cancellationToken);
            }
        }
        private async Task SendStream(MessageType messageType, Stream data, Stream userStream, CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken);
            CancellationTokenRegistration tokenRegistration = default;
            try
            {
                tokenRegistration = cancellationToken.Register(Dispose);
                await Network.WriteMessage(messageType, data, cancellationToken);
                await Network.WriteBuffer(BitConverter.GetBytes(userStream.Length), cancellationToken);
                const int DefaultCopyBufferSize = 81920;
                await userStream.CopyToAsync(Network, DefaultCopyBufferSize, cancellationToken);
            }
            finally
            {
                _sendLock.Release();
                tokenRegistration.Dispose();
            }
        }
        private async Task SendMessage(MessageType messageType, MemoryStream data, CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                await Network.WriteMessage(messageType, data, CancellationToken.None);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        public void Dispose()
        {
            var closedHandler = Closed;
            if (closedHandler == null || (closedHandler = Interlocked.CompareExchange(ref Closed, null, closedHandler)) == null)
            {
                return;
            }
            _sendLock.AssertDisposed();
            Network.Dispose();
            try
            {
                closedHandler.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, this);
            }
            CompleteRequests();
        }
        private void CompleteRequests()
        {
            foreach (var completionSource in _requests.Values)
            {
                completionSource.TrySetException(new IOException("Connection closed."));
            }
        }
        private async Task<bool> ReadBuffer(int length)
        {
            int offset = 0;
            int remaining = length;
            while (remaining > 0)
            {
                var read = await Network.ReadAsync(_buffer, offset, remaining);
                if (read == 0)
                {
                    return false;
                }
                remaining -= read;
                offset += read;
            }
            return true;
        }
        private async Task ReceiveLoop()
        {
            try
            {
                while (await ReadBuffer(HeaderLength))
                {
                    Debug.Assert(SynchronizationContext.Current == null);
                    var length = BitConverter.ToInt32(_buffer, startIndex: 1);
                    if (length > _maxMessageSize)
                    {
                        throw new InvalidDataException($"Message too large. The maximum message size is {_maxMessageSize / (1024 * 1024)} megabytes.");
                    }
                    _nestedStream.Reset(length);
                    await HandleMessage();
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, $"{nameof(ReceiveLoop)} {Name}");
            }
            if (LogEnabled)
            {
                Log($"{nameof(ReceiveLoop)} {Name} finished.");
            }
            Dispose();
            return;
            async Task HandleMessage()
            {
                var messageType = (MessageType)_buffer[0];
                object state = null;
                Action<object> callback = null;
                switch (messageType)
                {
                    case MessageType.Response:
                        state = await Deserialize<Response>();
                        callback = _onResponse;
                        break;
                    case MessageType.Request:
                        state = await Deserialize<Request>();
                        callback = _onRequest;
                        break;
                    case MessageType.CancellationRequest:
                        state = (await Deserialize<CancellationRequest>()).RequestId;
                        callback = _onCancellation;
                        break;
                    case MessageType.UploadRequest:
                        await OnUploadRequest();
                        return;
                    case MessageType.DownloadResponse:
                        await OnDownloadResponse();
                        return;
                    default:
                        if (LogEnabled)
                        {
                            Log("Unknown message type " + messageType);
                        }
                        break;
                };
                if (callback != null)
                {
                    _=Task.Factory.StartNew(callback, state, default, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
                }
            }
        }
        private async Task OnDownloadResponse()
        {
            var response = await Deserialize<Response>();
            await EnterStreamMode();
            var streamDisposed = new TaskCompletionSource<bool>();
            EventHandler disposedHandler = delegate { streamDisposed.TrySetResult(true); };
            _nestedStream.Disposed += disposedHandler;
            try
            {
                response.DownloadStream = _nestedStream;
                OnResponseReceived(response);
                await streamDisposed.Task;
            }
            finally
            {
                _nestedStream.Disposed -= disposedHandler;
            }
        }
        private async Task OnUploadRequest()
        {
            var request = await Deserialize<Request>();
            await EnterStreamMode();
            using (_nestedStream)
            {
                request.UploadStream = _nestedStream;
                await OnRequestReceived(request);
            }
        }
        private async Task EnterStreamMode()
        {
            if (!await ReadBuffer(sizeof(long)))
            {
                throw new IOException("Connection closed.");
            }
            var userStreamLength = BitConverter.ToInt64(_buffer, startIndex: 0);
            _nestedStream.Reset(userStreamLength);
        }
        private MemoryStream SerializeToStream(object value)
        {
            var stream = GetStream();
            try
            {
                stream.Position = HeaderLength;
                Serializer.Serialize(value, stream);
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        private Task<T> Deserialize<T>() => Serializer.DeserializeAsync<T>(_nestedStream);
        private void OnCancellationReceived(string requestId)
        {
            try
            {
                CancellationReceived(requestId);
            }
            catch(Exception ex)
            {
                Log(ex);
            }
        }
        private void Log(Exception ex) => Logger.LogException(ex, Name);
        private Task OnRequestReceived(Request request)
        {
            try
            {
                return RequestReceived(request);
            }
            catch (Exception ex)
            {
                Log(ex);
            }
            return Task.CompletedTask;
        }
        private void OnResponseReceived(Response response)
        {
            try
            {
                if (LogEnabled)
                {
                    Log($"Received response for request {response.RequestId} {Name}.");
                }
                if (_requests.TryGetValue(response.RequestId, out var completionSource))
                {
                    completionSource.TrySetResult(response);
                }
            }
            catch (Exception ex)
            {
                Log(ex);
            }
        }
        public void Log(string message) => Logger.LogInformation(message);
    }
}