using System.Text.RegularExpressions;
using Arb.Core.Execution;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Execution;

/// <summary>The in-memory state machine and the PostgreSQL trigger table must be the same contract.</summary>
public class TradeStateMachineTests
{
    private const string TradingMigration = "db/migrations/V002__trading.sql";

    /// <summary>Effective table = every INSERT into trd.trade_state_transition across ALL migrations (V002 + V007 …).</summary>
    [Fact]
    public void TransitionTable_IsIdenticalToSqlMigrations()
    {
        var fromSql = new HashSet<(string, string)>();
        int blocks = 0;
        foreach (string file in RepoFiles.Migrations())
        {
            string sql = StripSqlComments(File.ReadAllText(file)); // a commented-out row must not count
            Assert.False(Regex.IsMatch(sql, @"DELETE\s+FROM\s+trd\.trade_state_transition", RegexOptions.IgnoreCase), $"{file}: DELETE not modelled by this test");
            foreach (Match block in Regex.Matches(sql, @"INSERT INTO trd\.trade_state_transition[^;]*;", RegexOptions.Singleline))
            {
                blocks++;
                foreach (Match m in Regex.Matches(block.Value, @"\('([A-Z_]+)',\s*'([A-Z_]+)'\)"))
                {
                    Assert.True(fromSql.Add((m.Groups[1].Value, m.Groups[2].Value)), $"duplicate transition {m.Value}");
                }
            }
        }

        Assert.True(blocks >= 2, "expected transition INSERTs in V002 and V007");

        var fromCode = new HashSet<(string, string)>();
        foreach ((TradeState f, TradeState t) in TradeStateMachine.Transitions)
        {
            fromCode.Add((f.ToDbCode(), t.ToDbCode()));
        }

        Assert.Equal(23, fromSql.Count);
        Assert.Equal(23, TradeStateMachine.Transitions.Length);
        Assert.True(fromSql.SetEquals(fromCode), "C# TradeStateMachine and trd.trade_state_transition diverged");
    }

    [Fact]
    public void StatesAndTerminalFlags_AreIdenticalToSql()
    {
        string sql = RepoFiles.ReadAll(TradingMigration);
        Match block = Regex.Match(sql, @"INSERT INTO trd\.trade_state \(state, is_terminal, description\) VALUES[^;]*;", RegexOptions.Singleline);
        Assert.True(block.Success, "trade_state INSERT not found");
        int n = 0;
        foreach (Match m in Regex.Matches(block.Value, @"\('([A-Z_]+)',\s*(true|false),"))
        {
            Assert.True(TradeStateInfo.TryParseDbCode(m.Groups[1].Value, out TradeState s), $"unknown state {m.Groups[1].Value}");
            Assert.Equal(m.Groups[2].Value == "true", s.IsTerminal());
            n++;
        }

        Assert.Equal(TradeStateInfo.Count, n);
    }

    [Fact]
    public void EveryPairOutsideTable_IsRejected()
    {
        var legal = new HashSet<(TradeState, TradeState)>();
        foreach ((TradeState f, TradeState t) in TradeStateMachine.Transitions)
        {
            legal.Add((f, t));
        }

        for (int f = 1; f <= TradeStateInfo.Count; f++)
        {
            for (int t = 1; t <= TradeStateInfo.Count; t++)
            {
                var from = (TradeState)f;
                var to = (TradeState)t;
                Assert.Equal(legal.Contains((from, to)), TradeStateMachine.IsAllowed(from, to));
            }
        }

        Assert.False(TradeStateMachine.IsAllowed((TradeState)0, TradeState.InFlight));
        Assert.False(TradeStateMachine.IsAllowed(TradeState.Created, (TradeState)99));
    }

    [Fact]
    public void Spatial_HappyPath_And_LeggingRepair()
    {
        TradeStateRecord r = TradeStateRecord.Create(100);
        Assert.Equal(TransitionError.None, TradeStateMachine.TryTransition(ref r, TradeState.InFlight, StrategyKind.Spatial, 101));
        Assert.Equal(TransitionError.None, TradeStateMachine.TryTransition(ref r, TradeState.Residual, StrategyKind.Spatial, 140));
        Assert.Equal(TransitionError.None, TradeStateMachine.TryTransition(ref r, TradeState.Repairing, StrategyKind.Spatial, 145));
        Assert.Equal(TransitionError.None, TradeStateMachine.TryTransition(ref r, TradeState.Repaired, StrategyKind.Spatial, 360));
        Assert.Equal(TradeState.Repaired, r.State);
        Assert.Equal(4, r.Version);
        Assert.Equal(360L, r.ChangedAtMicros);
        Assert.True(r.State.IsTerminal());
        Assert.Equal(TransitionError.IllegalTransition, TradeStateMachine.TryTransition(ref r, TradeState.Repairing, StrategyKind.Spatial, 400));
        Assert.Equal(4, r.Version);
    }

    [Fact]
    public void StrategyConstraints_MirrorDbTrigger()
    {
        TradeStateRecord spatial = TradeStateRecord.Create(0);
        TradeStateMachine.TryTransition(ref spatial, TradeState.InFlight, StrategyKind.Spatial, 1);
        Assert.Equal(TransitionError.StrategyNotAllowed, TradeStateMachine.TryTransition(ref spatial, TradeState.PositionOpen, StrategyKind.Spatial, 2));

        TradeStateRecord funding = TradeStateRecord.Create(0);
        TradeStateMachine.TryTransition(ref funding, TradeState.InFlight, StrategyKind.FundingPerpPerp, 1);
        Assert.Equal(TransitionError.StrategyNotAllowed, TradeStateMachine.TryTransition(ref funding, TradeState.Completed, StrategyKind.FundingPerpPerp, 2));
        Assert.Equal(TransitionError.None, TradeStateMachine.TryTransition(ref funding, TradeState.PositionOpen, StrategyKind.FundingPerpPerp, 2));
        Assert.Equal(TransitionError.None, TradeStateMachine.TryTransition(ref funding, TradeState.Exiting, StrategyKind.FundingPerpPerp, 3));
        Assert.Equal(TransitionError.None, TradeStateMachine.TryTransition(ref funding, TradeState.Closed, StrategyKind.FundingPerpPerp, 4));
    }

    [Fact]
    public void TimeGoingBackwards_IsRejected_StateUnchanged()
    {
        TradeStateRecord r = TradeStateRecord.Create(1_000);
        Assert.Equal(TransitionError.TimeWentBackwards, TradeStateMachine.TryTransition(ref r, TradeState.InFlight, StrategyKind.Spatial, 999));
        Assert.Equal(TradeState.Created, r.State);
        Assert.Equal(0, r.Version);

        TradeStateRecord blank = default;
        Assert.Equal(TransitionError.UninitializedState, TradeStateMachine.TryTransition(ref blank, TradeState.InFlight, StrategyKind.Spatial, 1));
    }

    [Fact]
    public void StuckCanBeRepairedOrResolvedManually()
    {
        TradeStateRecord r = TradeStateRecord.Create(0);
        foreach (TradeState s in new[] { TradeState.InFlight, TradeState.Residual, TradeState.Stuck, TradeState.Repairing, TradeState.Stuck, TradeState.ManualResolved })
        {
            Assert.Equal(TransitionError.None, TradeStateMachine.TryTransition(ref r, s, StrategyKind.Spatial, r.ChangedAtMicros + 1));
        }

        Assert.True(r.State.IsTerminal());
        Assert.False(TradeState.Stuck.IsTerminal());
    }

    internal static string StripSqlComments(string sql) => Regex.Replace(sql, @"--[^\n]*", string.Empty);
}

public class DbContractTests
{
    [Fact]
    public void RejectReasons_MatchOpportunityDecisionCheck()
    {
        string sql = RepoFiles.ReadAll("db/migrations/V002__trading.sql");
        Match m = Regex.Match(sql, @"decision\s+text\s+NOT NULL CHECK \(decision IN \(([^)]*)\)\)", RegexOptions.Singleline);
        Assert.True(m.Success, "decision CHECK not found");
        var fromSql = new HashSet<string>();
        foreach (Match c in Regex.Matches(m.Groups[1].Value, @"'([A-Z_]+)'"))
        {
            fromSql.Add(c.Groups[1].Value);
        }

        var fromCode = new HashSet<string>();
        foreach (RejectReason r in Enum.GetValues<RejectReason>())
        {
            fromCode.Add(r.ToDbCode());
        }

        Assert.True(fromSql.SetEquals(fromCode), "RejectReason and trd.opportunity.decision diverged");
    }

    [Fact]
    public void StrategyCodes_MatchSchema()
    {
        string sql = RepoFiles.ReadAll("db/migrations/V002__trading.sql");
        Match m = Regex.Match(sql, @"strategy\s+text\s+NOT NULL CHECK \(strategy IN \(([^)]*)\)\)");
        Assert.True(m.Success, "strategy CHECK not found");
        var fromSql = new HashSet<string>();
        foreach (Match c in Regex.Matches(m.Groups[1].Value, @"'([A-Z_]+)'"))
        {
            fromSql.Add(c.Groups[1].Value);
        }

        var fromCode = new HashSet<string>();
        foreach (StrategyKind s in Enum.GetValues<StrategyKind>())
        {
            fromCode.Add(s.ToDbCode());
        }

        Assert.True(fromSql.SetEquals(fromCode));
    }
}
