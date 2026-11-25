using System.Buffers.Binary;
using System.Text.Json;

namespace OpenSim.GamePhysics.WorkerProtocol;

/// <summary>
/// Length-prefixed UTF-8 JSON frame codec. The four-byte prefix is unsigned big-endian.
/// </summary>
public static class WorkerFrameCodec
{
    public static async ValueTask<JsonDocument?> ReadAsync(
        Stream stream,
        int maxFrameBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateMaximum(maxFrameBytes);

        byte[] header = new byte[sizeof(uint)];
        int headerBytes = await ReadHeaderAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (headerBytes == 0)
            return null;

        if (headerBytes != header.Length)
            throw new EndOfStreamException("The worker frame header is truncated.");

        uint frameLength = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (frameLength == 0 || frameLength > maxFrameBytes)
            throw new InvalidDataException("The worker frame length is outside the permitted range.");

        byte[] payload = new byte[checked((int)frameLength)];
        await ReadPayloadAsync(stream, payload, cancellationToken).ConfigureAwait(false);

        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The worker frame does not contain valid JSON.", ex);
        }
    }

    public static async ValueTask WriteAsync(
        Stream stream,
        ReadOnlyMemory<byte> utf8Json,
        int maxFrameBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateMaximum(maxFrameBytes);

        if (utf8Json.Length == 0 || utf8Json.Length > maxFrameBytes)
            throw new InvalidDataException("The worker frame length is outside the permitted range.");

        try
        {
            using JsonDocument _ = JsonDocument.Parse(utf8Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The worker frame does not contain valid JSON.", ex);
        }

        byte[] header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)utf8Json.Length));
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(utf8Json, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int> ReadHeaderAsync(
        Stream stream,
        byte[] header,
        CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < header.Length)
        {
            int read = await stream.ReadAsync(header.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return total;
            total += read;
        }
        return total;
    }

    private static async ValueTask ReadPayloadAsync(
        Stream stream,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < payload.Length)
        {
            int read = await stream.ReadAsync(payload.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("The worker frame payload is truncated.");
            total += read;
        }
    }

    private static void ValidateMaximum(int maxFrameBytes)
    {
        if (maxFrameBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxFrameBytes));
    }
}
