using System.Text.RegularExpressions;
using Arb.Core.Execution;
using Arb.Core.Primitives;
using Arb.Core.Tests.Execution;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// R-20 (S4) e: every C# enum mirrored by a SQL CHECK is contract-tested (v0.2 covered RejectReason, StrategyKind and the
/// transition table only; RepairMethod had no MANUAL and no DB code; Side / OrderType / TimeInForce / MarketType had no
/// mapping at all). SQL comments are stripped before parsing (a commented-out value must not count, see R-10).
/// </summary>
public class R20_EnumDbContractTests
{
    private static HashSet<string> CheckValues(string file, string column, int occurrence = 0)
    {
        string sql = TradeStateMachineTests.StripSqlComments(RepoFiles.ReadAll(file));
        MatchCollection ms = Regex.Matches(sql, $@"\b{column}\s+text\b[^,]*?CHECK \({column} IN \(([^)]*)\)\)", RegexOptions.Singleline);
        Assert.True(ms.Count > occurrence, $"CHECK on {column} not found in {file}");
        var set = new HashSet<string>();
        foreach (Match v in Regex.Matches(ms[occurrence].Groups[1].Value, @"'([A-Z_]+)'"))
        {
            set.Add(v.Groups[1].Value);
        }

        return set;
    }

    private static void AssertSame(HashSet<string> sql, IEnumerable<string> code, string what) =>
        Assert.True(sql.SetEquals(code), $"{what}: SQL [{string.Join(",", sql.OrderBy(x => x))}] vs C# [{string.Join(",", code.OrderBy(x => x))}]");

    [Fact]
    public void RepairMethod_MatchesTradeGroupRepairMethodCheck()
    {
        IEnumerable<string> code = Enum.GetValues<RepairMethod>().Where(m => m != RepairMethod.None).Select(m => m.ToDbCode());
        AssertSame(CheckValues("db/migrations/V002__trading.sql", "repair_method"), code, "repair_method");
        Assert.Throws<ArgumentOutOfRangeException>(() => RepairMethod.None.ToDbCode()); // None = NULL in SQL
    }

    [Fact]
    public void Side_MatchesOrderLegAndFillChecks()
    {
        IEnumerable<string> code = Enum.GetValues<Side>().Select(s => s.ToDbCode());
        AssertSame(CheckValues("db/migrations/V002__trading.sql", "side", 0), code, "order_leg.side");
        AssertSame(CheckValues("db/migrations/V002__trading.sql", "side", 1), code, "fill.side");
    }

    [Fact]
    public void OrderType_And_TimeInForce_MatchOrderLegChecks()
    {
        AssertSame(CheckValues("db/migrations/V002__trading.sql", "order_type"), Enum.GetValues<OrderType>().Select(t => t.ToDbCode()), "order_type");
        AssertSame(CheckValues("db/migrations/V002__trading.sql", "time_in_force"), Enum.GetValues<TimeInForce>().Select(t => t.ToDbCode()), "time_in_force");
    }

    [Fact]
    public void MarketType_MatchesInstrumentAndFeeScheduleChecks()
    {
        IEnumerable<string> code = Enum.GetValues<MarketType>().Where(m => m != MarketType.Unknown).Select(m => m.ToDbCode());
        AssertSame(CheckValues("db/migrations/V001__schemas_reference.sql", "market_type", 0), code, "ref.instrument.market_type");
        AssertSame(CheckValues("db/migrations/V001__schemas_reference.sql", "market_type", 1), code, "ref.fee_schedule.market_type");
    }
}
