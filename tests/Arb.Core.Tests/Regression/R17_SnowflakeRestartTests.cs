using Arb.Core.Primitives;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// R-17 (S3): monotonicity held only inside one process. After a wall-clock step back and a restart, the generator
/// re-issued IDs already used → order_id / clientOrderId collide (DB UNIQUE) and a QueryByClientId reconcile can match
/// the OLD venue order (P16). The host must resume after a persisted high-water mark (journal / max id in the DB).
/// </summary>
public class R17_SnowflakeRestartTests
{
    private static readonly long T = (SnowflakeIdGenerator.EpochUnixMs + 10_000_000) * 1000;

    [Fact]
    public void RestartAfterClockStepBack_ResumingAfterTheHighWaterMark_NeverReissues()
    {
        var clk = new ManualClock(0, T);
        var before = new SnowflakeIdGenerator(5, clk, resumeAfterUnixMs: 0);
        var issued = new HashSet<long>();
        long last = 0;
        for (int i = 0; i < 5_000; i++)
        {
            last = before.Next();
            issued.Add(last);
        }

        long hwm = before.LastIssuedUnixMs; // persisted by the host (journal) / max(order_id) of this node in the DB
        Assert.Equal(SnowflakeIdGenerator.UnixMillisOf(last), hwm);

        clk.UnixMicros -= 2_000_000;            // NTP step back 2 s, then the process restarts
        var after = new SnowflakeIdGenerator(5, clk, resumeAfterUnixMs: hwm);
        for (int i = 0; i < 5_000; i++)
        {
            long id = after.Next();
            Assert.True(id > last, $"id {id} not after the pre-restart id {last}");
            Assert.True(issued.Add(id), $"duplicate id {id}");
        }

        clk.UnixMicros += 2_000_000;            // wall clock catches up: still unique
        Assert.True(issued.Add(after.Next()));
    }

    [Fact]
    public void ResumeAfter_IsAStrictLowerBound_EvenWithAClockFarBehind()
    {
        var clk = new ManualClock(0, T);
        long resume = (T / 1000) + 60_000;      // high-water mark 60 s AHEAD of the wall clock
        var gen = new SnowflakeIdGenerator(9, clk, resume);
        long a = gen.Next();
        long b = gen.Next();
        Assert.True(SnowflakeIdGenerator.UnixMillisOf(a) > resume);
        Assert.True(b > a);
        Assert.Equal(9, SnowflakeIdGenerator.NodeOf(b));
    }

    [Fact]
    public void InvalidResumePoint_IsRejected()
    {
        var clk = new ManualClock(0, T);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnowflakeIdGenerator(1, clk, resumeAfterUnixMs: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnowflakeIdGenerator(1, clk, resumeAfterUnixMs: long.MaxValue));
    }
}
