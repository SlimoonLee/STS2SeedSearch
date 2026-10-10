using System.IO.Hashing;
using System.Text;

namespace SeedSearch;

/// <summary>Xoshiro256** + Rng 包装层，逐位转写 MegaCrit MegaRandom/Rng (v0.111.0)。</summary>
public sealed class Rng
{
    ulong _s0, _s1, _s2, _s3;

    public Rng(ulong seed) => Reinit(seed);

    public Rng(ulong seed, string name) : this(seed + Hash(name)) { }

    public static ulong HashString(string s) => Hash(s);

    /// <summary>Hash 已经是 ASCII/UTF-8 字节时的无分配入口。</summary>
    public static ulong HashBytes(ReadOnlySpan<byte> bytes) => XxHash64.HashToUInt64(bytes, 0L);

    static ulong Hash(string s)
    {
        int length = Encoding.UTF8.GetByteCount(s);
        Span<byte> bytes = length <= 256 ? stackalloc byte[length] : new byte[length];
        int n = Encoding.UTF8.GetBytes(s, bytes);
        return HashBytes(bytes.Slice(0, n));
    }

    void Reinit(ulong seed)
    {
        Counter = 0;
        _s0 = Splitmix64(ref seed);
        _s1 = Splitmix64(ref seed);
        _s2 = Splitmix64(ref seed);
        _s3 = Splitmix64(ref seed);
    }

    static ulong Splitmix64(ref ulong x)
    {
        ulong num = x += 11400714819323198485UL;
        num = (num ^ (num >> 30)) * 13787848793156543929UL;
        num = (num ^ (num >> 27)) * 10723151780598845931UL;
        return num ^ (num >> 31);
    }

    public ulong NextULong()
    {
        Counter++;
        ulong s = _s0, s2 = _s1, s3 = _s2, s4 = _s3;
        ulong result = System.Numerics.BitOperations.RotateLeft(s2 * 5, 7) * 9;
        ulong num = s2 << 17;
        s3 ^= s; s4 ^= s2; s2 ^= s3; s ^= s4; s3 ^= num;
        s4 = System.Numerics.BitOperations.RotateLeft(s4, 45);
        _s0 = s; _s1 = s2; _s2 = s3; _s3 = s4;
        return result;
    }

    public ulong S0 => _s0;
    public long Counter { get; private set; }
    public ulong S1 => _s1;
    public ulong S2 => _s2;
    public ulong S3 => _s3;

    public Rng Clone()
    {
        var c = (Rng)MemberwiseClone();
        return c;
    }

    public double NextDouble() => (NextULong() >> 11) * 1.1102230246251565E-16; // 2^-53，与 Python 版一致

    public float NextFloat() => (float)(NextULong() >> 40) * 5.9604645E-08f;

    public int NextInt(int maxExclusive) => (int)(NextDouble() * maxExclusive);

    public int NextInt(int minInclusive, int maxExclusive) => minInclusive + (int)(NextDouble() * (double)(maxExclusive - minInclusive));

    public bool NextBool() => NextInt(2) == 0;

    public int NextItem(int count) => NextInt(0, count);

    /// <summary>Box-Muller，Sin 分支（NextGaussianDouble 用 Cos，勿混）。</summary>
    public int NextGaussianInt(int mean, int stdDev, int min, int max)
    {
        int num3;
        do
        {
            double d = 1.0 - NextDouble();
            double num = 1.0 - NextDouble();
            double num2 = Math.Sqrt(-2.0 * Math.Log(d)) * Math.Sin(Math.PI * 2.0 * num);
            num3 = (int)Math.Round(mean + stdDev * num2);
        } while (num3 < min || num3 > max);
        return num3;
    }
}

public static class Shuffles
{
    /// <summary>Fisher-Yates 自尾向头，NextInt(i+1)。</summary>
    public static List<T> UnstableShuffle<T>(this List<T> list, Rng rng)
    {
        int n = list.Count;
        while (n > 1)
        {
            n--;
            int k = rng.NextInt(n + 1);
            (list[n], list[k]) = (list[k], list[n]);
        }
        return list;
    }

    /// <summary>先 Sort（IComparable）再洗牌，输出与输入序无关。</summary>
    public static List<T> StableShuffle<T>(this List<T> list, Rng rng) where T : IComparable<T>
    {
        list.Sort();
        return list.UnstableShuffle(rng);
    }
}
