using static System.Runtime.InteropServices.MemoryMarshal;

namespace DotNext.IO;

using Buffers;

internal abstract class ReadOnlyStream : Stream
{
    public sealed override bool CanRead => true;

    public sealed override bool CanWrite => false;

    public sealed override bool CanTimeout => false;

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken token)
        => token.IsCancellationRequested ? Task.FromCanceled(token) : Task.CompletedTask;

    public override abstract int Read(Span<byte> buffer);

    public sealed override int ReadByte()
    {
        byte result = 0;
        return Read(CreateSpan(ref result, 1)) is 1 ? result : -1;
    }

    public sealed override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);

        return Read(buffer.AsSpan(offset, count));
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token)
    {
        ValueTask<int> result;
        if (token.IsCancellationRequested)
        {
            result = ValueTask.FromCanceled<int>(token);
        }
        else
        {
            try
            {
                result = new(Read(buffer.Span));
            }
            catch (Exception e)
            {
                result = ValueTask.FromException<int>(e);
            }
        }

        return result;
    }

    public sealed override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();

    public sealed override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
        => TaskToAsyncResult.Begin(ReadAsync(buffer, offset, count), callback, state);

    public sealed override int EndRead(IAsyncResult ar) => TaskToAsyncResult.End<int>(ar);

    public sealed override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public sealed override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();

    public sealed override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        => token.IsCancellationRequested ? Task.FromCanceled(token) : Task.FromException(new NotSupportedException());

    public sealed override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token)
        => token.IsCancellationRequested ? ValueTask.FromCanceled(token) : ValueTask.FromException(new NotSupportedException());

    public sealed override void WriteByte(byte value) => throw new NotSupportedException();

    public sealed override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
        => throw new NotSupportedException();

    public sealed override void EndWrite(IAsyncResult ar) => throw new InvalidOperationException();
}

internal sealed class ReadOnlyStream<TArg>(Func<Memory<byte>, TArg, CancellationToken, ValueTask<int>> reader, TArg arg) : ReadOnlyStream
{
    private const int DefaultTimeout = 4000;
    private int timeout = DefaultTimeout;
    private byte[]? synchronousBuffer;
    private CancellationTokenSource? timeoutSource;

    public override int ReadTimeout
    {
        get => timeout;
        set => timeout = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override long Length => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

    public override bool CanSeek => false;

    public override void SetLength(long value)
        => throw new NotSupportedException();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token)
        => reader(buffer, arg, token);

    public override int Read(Span<byte> buffer)
    {
        int writtenCount;
        if (buffer.IsEmpty)
        {
            writtenCount = 0;
        }
        else
        {
            var tempBuffer = RentBuffer(buffer.Length);
            timeoutSource ??= new();
            timeoutSource.CancelAfter(timeout);
            var task = ReadAsync(tempBuffer, timeoutSource.Token).AsTask();
            try
            {
                task.Wait();
                writtenCount = task.Result;
            }
            finally
            {
                task.Dispose();

                if (!timeoutSource.TryReset())
                {
                    timeoutSource.Dispose();
                    timeoutSource = null;
                }
            }

            tempBuffer.AsSpan(0, writtenCount).CopyTo(buffer);
        }

        return writtenCount;
    }

    private ArraySegment<byte> RentBuffer(int length)
    {
        if (synchronousBuffer is null || synchronousBuffer.Length < length)
            synchronousBuffer = GC.AllocateUninitializedArray<byte>(length);

        return new(synchronousBuffer, 0, length);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timeoutSource?.Dispose();
        }

        base.Dispose(disposing);
    }
}