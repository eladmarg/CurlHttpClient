using System.Security.Cryptography;

namespace CurlHttp.IntegrationTests.Infrastructure;

/// <summary>
/// Deterministic payload generation + verification. All large-transfer tests
/// assert SHA-256 over the exact bytes, not just byte counts, so duplicated,
/// dropped, or reordered chunks are always detected.
/// </summary>
internal static class DeterministicPayload
{
    /// <summary>Fills a buffer deterministically from (seed, absolute offset)
    /// so any window of the logical stream can be regenerated independently.</summary>
    public static void Fill(Span<byte> buffer, int seed, long offset)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            long position = offset + i;
            // xorshift-style mix of seed and position: cheap, stable, and
            // position-addressable (unlike Random, which is sequential-only).
            ulong x = (ulong)position * 0x9E3779B97F4A7C15UL ^ (uint)seed * 0xBF58476D1CE4E5B9UL;
            x ^= x >> 31;
            buffer[i] = (byte)(x * 0x94D049BB133111EBUL >> 56);
        }
    }

    public static byte[] Create(int length, int seed)
    {
        byte[] data = new byte[length];
        Fill(data, seed, 0);
        return data;
    }

    public static string Sha256(ReadOnlySpan<byte> data)
        => Convert.ToHexString(SHA256.HashData(data));

    /// <summary>Expected hash of the logical stream (seed, totalLength)
    /// without materializing it.</summary>
    public static string ExpectedSha256(long totalLength, int seed)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] block = new byte[64 * 1024];
        long offset = 0;
        while (offset < totalLength)
        {
            int size = (int)Math.Min(block.Length, totalLength - offset);
            Fill(block.AsSpan(0, size), seed, offset);
            sha.AppendData(block, 0, size);
            offset += size;
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    /// <summary>Consumes a stream, returning (byteCount, sha256hex).</summary>
    public static async Task<(long Length, string Sha256)> ConsumeAsync(
        Stream stream, CancellationToken cancellationToken = default)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            total += read;
        }
        return (total, Convert.ToHexString(sha.GetHashAndReset()));
    }

    /// <summary>Read-only, forward-only stream producing the deterministic
    /// payload on demand — streams arbitrarily large uploads without
    /// materializing them. Optionally seekable, slow, or tiny-chunked to
    /// model awkward producers.</summary>
    public sealed class Stream2(long length, int seed, bool seekable = true,
        int? maxChunk = null, TimeSpan? delayPerRead = null) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => seekable;
        public override bool CanWrite => false;
        public override long Length => seekable ? length : throw new NotSupportedException();

        public override long Position
        {
            get => seekable ? _position : throw new NotSupportedException();
            set
            {
                if (!seekable)
                {
                    throw new NotSupportedException();
                }
                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (delayPerRead is { } delay)
            {
                Thread.Sleep(delay);
            }
            return ReadCore(buffer);
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (delayPerRead is { } delay)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            return ReadCore(buffer.Span);
        }

        private int ReadCore(Span<byte> buffer)
        {
            long remaining = length - _position;
            if (remaining <= 0)
            {
                return 0;
            }
            int size = (int)Math.Min(buffer.Length, remaining);
            if (maxChunk is { } cap)
            {
                size = Math.Min(size, cap);
            }
            Fill(buffer[..size], seed, _position);
            _position += size;
            return size;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (!seekable)
            {
                throw new NotSupportedException();
            }
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
