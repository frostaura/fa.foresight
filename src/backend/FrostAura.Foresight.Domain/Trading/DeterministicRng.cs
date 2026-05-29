namespace FrostAura.Foresight.Domain.Trading;

/// <summary>
/// Version-stable, deterministic pseudo-random number generator based on the splitmix64 algorithm.
/// Used exclusively by the chaos/bust test engine to generate reproducible random start offsets.
///
/// DETERMINISM CONTRACT (load-bearing):
///   same seed ⇒ same output sequence, byte-for-byte, across runs, machines, and runtime versions.
///   splitmix64 achieves this because its recurrence is pure integer arithmetic — no floating-point
///   instability, no CLR allocation paths, no platform-specific behaviour.
///
/// FORBIDDEN SUBSTITUTES: <see cref="System.Random"/>, <see cref="DateTime"/>, any wall-clock source.
///   These are explicitly banned in the chaos engine — their presence would break the faithfulness
///   guarantee (same seed ≠ same result).
///
/// Algorithm reference: https://prng.di.unimi.it/splitmix64.c
/// </summary>
public sealed class DeterministicRng
{
    private ulong _state;

    /// <param name="seed">64-bit seed; identical seeds produce identical sequences.</param>
    public DeterministicRng(long seed)
    {
        // Reinterpret as unsigned — splitmix64 state is a 64-bit unsigned integer.
        _state = (ulong)seed;
    }

    /// <summary>
    /// Advances the state by one step and returns the next pseudo-random value.
    /// Pure function of the state — no side effects beyond the state update.
    /// </summary>
    private ulong NextRaw()
    {
        _state += 0x9E3779B97F4A7C15UL;            // additive constant (golden-ratio mixing)
        ulong z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>
    /// Returns a uniformly distributed double in [0, 1).
    /// Uses the standard IEEE 754 bit-trick: set the exponent to [1,2) and subtract 1.0,
    /// giving 2^53 evenly spaced values in [0, 1).
    /// </summary>
    public double NextDouble()
    {
        // Take the top 53 bits and map to [0.0, 1.0).
        ulong raw = NextRaw();
        // The constant 0x3FF0000000000000UL is the IEEE 754 representation of 1.0.
        // Masking the top 11 bits (exponent + sign) and OR-ing with the 1.0 exponent gives
        // a value in [1.0, 2.0), then subtract 1.0 → [0.0, 1.0).
        ulong bits = (raw >> 11) | 0x3FF0000000000000UL;
        return BitConverter.Int64BitsToDouble((long)bits) - 1.0;
    }

    /// <summary>
    /// Returns a uniformly distributed integer in [0, <paramref name="maxExclusive"/>).
    /// Uses a rejection-free mapping via double arithmetic — exact for ranges up to 2^53.
    /// </summary>
    /// <param name="maxExclusive">Upper bound, exclusive. Must be &gt; 0.</param>
    public int NextInt(int maxExclusive)
    {
        if (maxExclusive <= 0) throw new ArgumentOutOfRangeException(nameof(maxExclusive), "Must be > 0.");
        return (int)(NextDouble() * maxExclusive);
    }
}
