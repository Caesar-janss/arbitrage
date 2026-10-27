using Arb.Core.Primitives;

namespace Arb.Core.Instruments;

/// <summary>Immutable canonical asset code ↔ dense <see cref="AssetId"/> map. Index 0 is reserved (none).</summary>
public sealed class AssetRegistry
{
    private readonly string[] _codes;
    private readonly Dictionary<string, AssetId> _byCode;

    private AssetRegistry(string[] codes)
    {
        _codes = codes;
        _byCode = new Dictionary<string, AssetId>(StringComparer.Ordinal);
        for (int i = 1; i < codes.Length; i++)
        {
            _byCode.Add(codes[i], new AssetId((ushort)i));
        }
    }

    /// <summary>Number of slots including the reserved index 0.</summary>
    public int Capacity => _codes.Length;

    public static AssetRegistry Create(params string[] canonicalCodes)
    {
        ArgumentNullException.ThrowIfNull(canonicalCodes);
        if (canonicalCodes.Length == 0 || canonicalCodes.Length >= ushort.MaxValue)
        {
            throw new ArgumentException("1..65534 assets required", nameof(canonicalCodes));
        }

        var codes = new string[canonicalCodes.Length + 1];
        codes[0] = string.Empty;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < canonicalCodes.Length; i++)
        {
            string c = canonicalCodes[i];
            if (string.IsNullOrWhiteSpace(c) || c.Length > 20 || !IsCanonical(c))
            {
                throw new ArgumentException($"invalid canonical asset code '{c}' (A-Z0-9, 2..20)", nameof(canonicalCodes));
            }

            if (!seen.Add(c))
            {
                throw new ArgumentException($"duplicate asset '{c}'", nameof(canonicalCodes));
            }

            codes[i + 1] = c;
        }

        return new AssetRegistry(codes);
    }

    public AssetId this[string code] =>
        _byCode.TryGetValue(code, out AssetId id) ? id : throw new KeyNotFoundException($"asset '{code}' not registered");

    public bool TryGet(string code, out AssetId id) => _byCode.TryGetValue(code, out id);

    public string CodeOf(AssetId id)
    {
        if (!id.IsValid || id.Value >= _codes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id.Value, "unknown asset id");
        }

        return _codes[id.Value];
    }

    private static bool IsCanonical(string code)
    {
        if (code.Length < 2)
        {
            return false;
        }

        foreach (char ch in code)
        {
            if (!(ch is >= 'A' and <= 'Z' or >= '0' and <= '9'))
            {
                return false;
            }
        }

        return true;
    }
}
