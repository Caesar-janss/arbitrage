using Arb.Core.Primitives;

namespace Arb.Core.Tests.Primitives;

public class SnowflakeIdGeneratorTests
{
    private static readonly long T0 = (SnowflakeIdGenerator.EpochUnixMs + 1_000_000) * 1000;

    [Fact]
    public void Ids_AreStrictlyIncreasing_AndCarryNodeAndTime()
    {
        var clock = new ManualClock(0, T0);
        var gen = new SnowflakeIdGenerator(17, clock);
        long prev = 0;
        for (int i = 0; i < 10_000; i++)
        {
            long id = gen.Next();
            Assert.True(id > prev, "ids must be strictly increasing");
            prev = id;
        }

        Assert.Equal(17, SnowflakeIdGenerator.NodeOf(prev));
        Assert.InRange(SnowflakeIdGenerator.UnixMillisOf(prev), T0 / 1000, (T0 / 1000) + 3);
    }

    [Fact]
    public void ClockStepBack_DoesNotBreakMonotonicity()
    {
        var clock = new ManualClock(0, T0);
        var gen = new SnowflakeIdGenerator(1, clock);
        long a = gen.Next();
        clock.UnixMicros -= 5_000_000; // NTP step back 5 s
        long b = gen.Next();
        Assert.True(b > a);
    }

    [Fact]
    public void ConcurrentGeneration_ProducesUniqueIds()
    {
        var gen = new SnowflakeIdGenerator(3, SystemClock.Instance);
        const int Threads = 4;
        const int PerThread = 25_000;
        var all = new long[Threads][];
        Parallel.For(0, Threads, t =>
        {
            var local = new long[PerThread];
            for (int i = 0; i < PerThread; i++)
            {
                local[i] = gen.Next();
            }

            all[t] = local;
        });
        var set = new HashSet<long>();
        foreach (long[] chunk in all)
        {
            foreach (long id in chunk)
            {
                Assert.True(set.Add(id), "duplicate id");
            }
        }

        Assert.Equal(Threads * PerThread, set.Count);
    }

    [Fact]
    public void ClientOrderId_RoundTrips_WithinVenueLimits()
    {
        var gen = new SnowflakeIdGenerator(1023, new ManualClock(0, T0));
        long id = gen.Next();
        Span<byte> buf = stackalloc byte[ClientOrderId.MaxLength];
        Assert.True(ClientOrderId.TryFormat(id, buf, out int n));
        Assert.InRange(n, 2, ClientOrderId.MaxLength);
        Assert.Equal((byte)'a', buf[0]);
        Assert.True(ClientOrderId.TryParse(buf[..n], out long back));
        Assert.Equal(id, back);
        Assert.False(ClientOrderId.TryParse("x123"u8, out _));
        Assert.False(ClientOrderId.TryFormat(0, buf, out _));
    }

    [Fact]
    public void InvalidNode_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnowflakeIdGenerator(1024, SystemClock.Instance));
    }
}
