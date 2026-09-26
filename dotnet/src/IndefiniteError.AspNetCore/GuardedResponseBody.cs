using System.IO.Pipelines;
using Microsoft.AspNetCore.Http.Features;

namespace IndefiniteError.AspNetCore;

/// <summary>
/// The response body of a seeded request. Until a fault ends the request it
/// forwards everything, unchanged. After one, it swallows every write, flush,
/// and start, so the lost response never reaches the client, and the
/// middleware can still answer for the fault.
/// </summary>
internal sealed class GuardedResponseBody(IHttpResponseBodyFeature inner, Injection injection) : IHttpResponseBodyFeature
{
    private GuardedStream? _stream;
    private GuardedWriter? _writer;

    private bool Lost => injection.Aborted is not null;

    public Stream Stream => _stream ??= new GuardedStream(inner.Stream, this);

    public PipeWriter Writer => _writer ??= new GuardedWriter(inner.Writer, this);

    public void DisableBuffering() => inner.DisableBuffering();

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        Lost ? Task.CompletedTask : inner.StartAsync(cancellationToken);

    public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) =>
        Lost ? Task.CompletedTask : inner.SendFileAsync(path, offset, count, cancellationToken);

    public Task CompleteAsync() => Lost ? Task.CompletedTask : inner.CompleteAsync();

    private sealed class GuardedStream(Stream inner, GuardedResponseBody body) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (!body.Lost)
            {
                inner.Write(buffer);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            body.Lost ? ValueTask.CompletedTask : inner.WriteAsync(buffer, cancellationToken);

        public override void Flush()
        {
            if (!body.Lost)
            {
                inner.Flush();
            }
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            body.Lost ? Task.CompletedTask : inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// Hands out the real pipe's memory until the request is lost, then a
    /// scratch buffer nobody reads.
    /// </summary>
    private sealed class GuardedWriter(PipeWriter inner, GuardedResponseBody body) : PipeWriter
    {
        private byte[] _scratch = [];
        private bool _lent; // the last GetMemory/GetSpan came from the scratch buffer

        public override bool CanGetUnflushedBytes => inner.CanGetUnflushedBytes;

        public override long UnflushedBytes => inner.UnflushedBytes;

        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            _lent = body.Lost;
            if (!_lent)
            {
                return inner.GetMemory(sizeHint);
            }

            if (_scratch.Length < Math.Max(sizeHint, 4096))
            {
                _scratch = new byte[Math.Max(sizeHint, 4096)];
            }

            return _scratch;
        }

        public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

        public override void Advance(int bytes)
        {
            if (!_lent)
            {
                inner.Advance(bytes);
            }
        }

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) =>
            body.Lost ? ValueTask.FromResult(new FlushResult(isCanceled: false, isCompleted: false)) : inner.FlushAsync(cancellationToken);

        public override void CancelPendingFlush() => inner.CancelPendingFlush();

        public override void Complete(Exception? exception = null)
        {
            if (!body.Lost)
            {
                inner.Complete(exception);
            }
        }

        public override ValueTask CompleteAsync(Exception? exception = null) =>
            body.Lost ? ValueTask.CompletedTask : inner.CompleteAsync(exception);
    }
}
