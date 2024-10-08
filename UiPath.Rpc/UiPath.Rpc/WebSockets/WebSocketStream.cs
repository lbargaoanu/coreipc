﻿using System.Net.WebSockets;
namespace UiPath.Rpc.WebSockets;
/// <summary>
/// Exposes a <see cref="WebSocket"/> as a <see cref="Stream"/>.
/// https://github.com/AArnott/Nerdbank.Streams/blob/main/src/Nerdbank.Streams/WebSocketStream.cs
/// </summary>
public class WebSocketStream : Stream
{
    /// <summary>
    /// The socket wrapped by this stream.
    /// </summary>
    private readonly WebSocket _webSocket;
    /// <summary>
    /// Initializes a new instance of the <see cref="WebSocketStream"/> class.
    /// </summary>
    /// <param name="webSocket">The web socket to wrap in a stream.</param>
    public WebSocketStream(WebSocket webSocket) => _webSocket = webSocket ?? throw new ArgumentNullException(nameof(webSocket));
    /// <inheritdoc />
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        (await _webSocket.ReceiveAsync(new(buffer, offset, count), cancellationToken).ConfigureAwait(false)).Count;
#if !NET462
    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var valueTask = _webSocket.ReceiveAsync(buffer, cancellationToken);
        return valueTask.IsCompletedSuccessfully ? new(valueTask.Result.Count) : CompleteAsync(valueTask);
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        static async ValueTask<int> CompleteAsync(ValueTask<ValueWebSocketReceiveResult > result) => (await result.ConfigureAwait(false)).Count;
    }
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _webSocket.SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
#endif
    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _webSocket.SendAsync(new(buffer, offset, count), WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();
    /// <summary>
    /// Does nothing, since web sockets do not need to be flushed.
    /// </summary>
    public override void Flush(){}
    /// <summary>
    /// Does nothing, since web sockets do not need to be flushed.
    /// </summary>
    /// <param name="cancellationToken">An ignored cancellation token.</param>
    /// <returns>A completed task.</returns>
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        _webSocket.Dispose();
        base.Dispose(disposing);
    }
    /// <inheritdoc />
    public override bool CanRead => true;
    /// <inheritdoc />
    public override bool CanSeek => false;
    /// <inheritdoc />
    public override bool CanWrite => true;
    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
}