using System.Numerics;

namespace Magicians.Native;

/// <summary>CPython random.Random integer-seed MT19937 and Python selection algorithms.</summary>
public sealed class PythonRandom
{
    private const int N = 624, M = 397;
    private readonly uint[] _state = new uint[N];
    private int _index;
    public sealed record State(uint[] words, int index);
    public PythonRandom(long seed = 0) : this(new BigInteger(seed)) { }
    public PythonRandom(BigInteger seed) { seed_integer(seed); }
    private PythonRandom(State state) { setstate(state); }
    public PythonRandom Clone() => new(getstate());
    public State getstate() => new(_state.ToArray(), _index);
    public void setstate(State state)
    {
        if (state.words.Length != N || state.index < 0 || state.index > N) throw new ArgumentException("Invalid MT19937 state");
        Array.Copy(state.words, _state, N); _index = state.index;
    }
    public void seed_integer(BigInteger seed)
    {
        seed = BigInteger.Abs(seed);
        var words = new List<uint>();
        do { words.Add((uint)(seed & uint.MaxValue)); seed >>= 32; } while (!seed.IsZero);
        Initialize(19650218U);
        unchecked
        {
            var i = 1; var j = 0;
            for (var count = Math.Max(N, words.Count); count > 0; count--)
            {
                _state[i] = (_state[i] ^ ((_state[i - 1] ^ (_state[i - 1] >> 30)) * 1664525U)) + words[j] + (uint)j;
                i++; j++;
                if (i >= N) { _state[0] = _state[N - 1]; i = 1; }
                if (j >= words.Count) j = 0;
            }
            for (var count = N - 1; count > 0; count--)
            {
                _state[i] = (_state[i] ^ ((_state[i - 1] ^ (_state[i - 1] >> 30)) * 1566083941U)) - (uint)i;
                i++;
                if (i >= N) { _state[0] = _state[N - 1]; i = 1; }
            }
            _state[0] = 0x80000000U;
        }
    }
    private void Initialize(uint seed)
    {
        _state[0] = seed;
        unchecked { for (var i = 1; i < N; i++) _state[i] = 1812433253U * (_state[i - 1] ^ (_state[i - 1] >> 30)) + (uint)i; }
        _index = N;
    }
    public uint NextUInt32()
    {
        if (_index >= N)
        {
            for (var i = 0; i < N; i++)
            {
                var value = (_state[i] & 0x80000000U) | (_state[(i + 1) % N] & 0x7fffffffU);
                _state[i] = _state[(i + M) % N] ^ (value >> 1) ^ ((value & 1) != 0 ? 0x9908b0dfU : 0U);
            }
            _index = 0;
        }
        var y = _state[_index++];
        y ^= y >> 11; y ^= (y << 7) & 0x9d2c5680U; y ^= (y << 15) & 0xefc60000U; y ^= y >> 18;
        return y;
    }
    public double random() => ((NextUInt32() >> 5) * 67108864.0 + (NextUInt32() >> 6)) * (1.0 / 9007199254740992.0);
    public BigInteger getrandbits(int bits)
    {
        if (bits < 0) throw new ArgumentOutOfRangeException(nameof(bits));
        if (bits == 0) return BigInteger.Zero;
        BigInteger result = 0;
        for (var i = 0; bits > 0; i++, bits -= 32)
        {
            var word = NextUInt32();
            if (bits < 32) word >>= 32 - bits;
            result |= new BigInteger(word) << (i * 32);
        }
        return result;
    }
    public BigInteger randbelow(BigInteger maximum)
    {
        if (maximum <= 0) throw new ArgumentOutOfRangeException(nameof(maximum));
        var bits = checked((int)maximum.GetBitLength());
        BigInteger result;
        do { result = getrandbits(bits); } while (result >= maximum);
        return result;
    }
    public int randrange(int stop) => checked((int)randbelow(stop));
    public int randrange(int start, int stop, int step = 1)
    {
        if (step == 0) throw new ArgumentOutOfRangeException(nameof(step));
        var width = (long)stop - start;
        var count = step > 0 ? (width + step - 1) / step : (width + step + 1) / step;
        if (count <= 0) throw new ArgumentException("empty range for randrange");
        return checked(start + step * (int)randbelow(count));
    }
    public long randrange(long stop) => checked((long)randbelow(stop));
    public int randint(int start, int stop) => checked(start + (int)randbelow((long)stop - start + 1));
    public T choice<T>(IReadOnlyList<T> sequence) => sequence.Count == 0 ? throw new ArgumentException("Cannot choose from an empty sequence") : sequence[randrange(sequence.Count)];
    public void shuffle<T>(IList<T> sequence)
    {
        for (var i = sequence.Count - 1; i > 0; i--) { var j = randrange(i + 1); (sequence[i], sequence[j]) = (sequence[j], sequence[i]); }
    }
    public List<T> sample<T>(IReadOnlyList<T> population, int k)
    {
        var n = population.Count;
        if (k < 0 || k > n) throw new ArgumentOutOfRangeException(nameof(k));
        var result = new List<T>(k);
        long setSize = 21;
        if (k > 5) setSize += (long)Math.Pow(4, Math.Ceiling(Math.Log(k * 3.0, 4)));
        if (n <= setSize)
        {
            var pool = population.ToArray();
            for (var i = 0; i < k; i++) { var j = randrange(n - i); result.Add(pool[j]); pool[j] = pool[n - i - 1]; }
        }
        else
        {
            var selected = new HashSet<int>();
            for (var i = 0; i < k; i++) { int j; do { j = randrange(n); } while (!selected.Add(j)); result.Add(population[j]); }
        }
        return result;
    }
    public List<T> choices<T>(IReadOnlyList<T> population, IReadOnlyList<double>? weights = null, int k = 1)
    {
        if (population.Count == 0) throw new ArgumentException("Cannot choose from an empty sequence");
        var result = new List<T>(Math.Max(k, 0));
        if (weights is null)
        {
            for (var i = 0; i < k; i++) result.Add(population[(int)Math.Floor(random() * population.Count)]);
            return result;
        }
        if (weights.Count != population.Count) throw new ArgumentException("The number of weights does not match the population");
        var cumulative = new double[weights.Count];
        double total = 0;
        for (var i = 0; i < weights.Count; i++) { total += weights[i]; cumulative[i] = total; }
        if (total <= 0 || !double.IsFinite(total)) throw new ArgumentException("Total of weights must be finite and positive");
        for (var i = 0; i < k; i++)
        {
            var target = random() * total;
            var low = 0; var high = population.Count - 1;
            while (low < high) { var middle = (low + high) >> 1; if (target < cumulative[middle]) high = middle; else low = middle + 1; }
            result.Add(population[low]);
        }
        return result;
    }
}
