namespace Erp.SharedKernel.Domain;

/// <summary>
/// Generates GUIDs that sort sequentially under SQL Server's uniqueidentifier ordering (bytes 10–15 compared first),
/// which keeps clustered primary-key inserts append-only. Plain UUIDv7 is time-ordered in its first bytes and is
/// therefore NOT sequential in SQL Server (see ADR-008).
/// </summary>
public static class SequentialGuid
{
    private static long _counter = DateTime.UtcNow.Ticks;

    public static Guid New()
    {
        Span<byte> guidBytes = stackalloc byte[16];
        Guid.NewGuid().TryWriteBytes(guidBytes);

        Span<byte> counterBytes = stackalloc byte[8];
        BitConverter.TryWriteBytes(counterBytes, Interlocked.Increment(ref _counter));
        if (!BitConverter.IsLittleEndian)
        {
            counterBytes.Reverse();
        }

        guidBytes[08] = counterBytes[1];
        guidBytes[09] = counterBytes[0];
        guidBytes[10] = counterBytes[7];
        guidBytes[11] = counterBytes[6];
        guidBytes[12] = counterBytes[5];
        guidBytes[13] = counterBytes[4];
        guidBytes[14] = counterBytes[3];
        guidBytes[15] = counterBytes[2];

        return new Guid(guidBytes);
    }
}
