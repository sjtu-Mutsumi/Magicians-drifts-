using System.Globalization;
using System.Numerics;

namespace Magicians.Native;

/// <summary>Canonical arbitrary precision rational, matching fractions.Fraction.</summary>
public readonly struct Q : IComparable<Q>, IEquatable<Q>
{
    private readonly BigInteger _n;
    private readonly BigInteger _d;
    public BigInteger numerator => _n;
    public BigInteger denominator => _d.IsZero ? BigInteger.One : _d;
    public static Q Zero => default;
    public static Q One => new(1);
    public Q(long numerator, long denominator = 1) : this(new BigInteger(numerator), new BigInteger(denominator)) { }
    public Q(BigInteger numerator) : this(numerator, BigInteger.One) { }
    public Q(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsZero) throw new DivideByZeroException();
        if (denominator.Sign < 0) { numerator = -numerator; denominator = -denominator; }
        var gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        _n = numerator / gcd; _d = denominator / gcd;
    }
    public Q(Q numerator, Q denominator) : this(numerator.numerator * denominator.denominator, numerator.denominator * denominator.numerator) { }
    public Q(double value) { this = FromDouble(value); }
    public Q(string value) { this = Parse(value); }
    public static Q Parse(string text)
    {
        text = text.Trim();
        var slash = text.IndexOf('/');
        if (slash >= 0) return new(BigInteger.Parse(text[..slash], CultureInfo.InvariantCulture), BigInteger.Parse(text[(slash + 1)..], CultureInfo.InvariantCulture));
        var exponentAt = text.IndexOfAny(new[] { 'e', 'E' });
        var exponent = exponentAt < 0 ? 0 : int.Parse(text[(exponentAt + 1)..], CultureInfo.InvariantCulture);
        if (exponentAt >= 0) text = text[..exponentAt];
        var dot = text.IndexOf('.');
        if (dot >= 0) { exponent -= text.Length - dot - 1; text = text.Remove(dot, 1); }
        var n = BigInteger.Parse(text, CultureInfo.InvariantCulture);
        return exponent >= 0 ? new(n * BigInteger.Pow(10, exponent), BigInteger.One) : new(n, BigInteger.Pow(10, -exponent));
    }
    public static Q FromDouble(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        if (value == 0) return Zero;
        var bits = BitConverter.DoubleToUInt64Bits(value);
        var exponent = (int)((bits >> 52) & 0x7ff);
        var mantissa = new BigInteger(bits & 0x000fffffffffffffUL);
        if (exponent != 0) mantissa += BigInteger.One << 52;
        var shift = (exponent == 0 ? 1 : exponent) - 1023 - 52;
        if ((bits >> 63) != 0) mantissa = -mantissa;
        return shift >= 0 ? new(mantissa << shift, BigInteger.One) : new(mantissa, BigInteger.One << -shift);
    }
    public BigInteger Floor()
    {
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        return remainder.Sign < 0 ? quotient - 1 : quotient;
    }
    public BigInteger Ceiling()
    {
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        return remainder.Sign > 0 ? quotient + 1 : quotient;
    }
    public int floor() => checked((int)Floor());
    public int ceil() => checked((int)Ceiling());
    public static Q Abs(Q value) => value < Zero ? -value : value;
    public Q Pow(int power) => power >= 0 ? new(BigInteger.Pow(numerator, power), BigInteger.Pow(denominator, power)) : new(BigInteger.Pow(denominator, checked(-power)), BigInteger.Pow(numerator, checked(-power)));
    public static Q Pow(Q value, int power) => value.Pow(power);
    public static Q Min(Q a, Q b) => a <= b ? a : b;
    public static Q Max(Q a, Q b) => a >= b ? a : b;
    public static BigInteger FloorDiv(Q a, Q b) => (a / b).Floor();
    public static Q operator +(Q a, Q b) => new(a.numerator * b.denominator + b.numerator * a.denominator, a.denominator * b.denominator);
    public static Q operator -(Q a, Q b) => new(a.numerator * b.denominator - b.numerator * a.denominator, a.denominator * b.denominator);
    public static Q operator -(Q value) => new(-value.numerator, value.denominator);
    public static Q operator +(Q value) => value;
    public static Q operator *(Q a, Q b) => new(a.numerator * b.numerator, a.denominator * b.denominator);
    public static Q operator /(Q a, Q b) => new(a.numerator * b.denominator, a.denominator * b.numerator);
    public static Q operator %(Q a, Q b) => a - new Q(FloorDiv(a, b)) * b;
    public static Q operator ++(Q value) => value + One;
    public static Q operator --(Q value) => value - One;
    public static bool operator ==(Q a, Q b) => a.Equals(b);
    public static bool operator !=(Q a, Q b) => !a.Equals(b);
    public static bool operator <(Q a, Q b) => a.CompareTo(b) < 0;
    public static bool operator >(Q a, Q b) => a.CompareTo(b) > 0;
    public static bool operator <=(Q a, Q b) => a.CompareTo(b) <= 0;
    public static bool operator >=(Q a, Q b) => a.CompareTo(b) >= 0;
    public static implicit operator Q(int value) => new(value);
    public static implicit operator Q(long value) => new(value);
    public static implicit operator Q(BigInteger value) => new(value);
    public static explicit operator int(Q value) => checked((int)(value.numerator / value.denominator));
    public static explicit operator long(Q value) => checked((long)(value.numerator / value.denominator));
    public static explicit operator double(Q value)
    {
        if (value.numerator.IsZero) return 0.0;
        var negative = value.numerator.Sign < 0;
        var numerator = BigInteger.Abs(value.numerator);
        var denominator = value.denominator;
        var exponent = checked((int)(numerator.GetBitLength() - denominator.GetBitLength()));
        if (exponent >= 0 ? numerator < (denominator << exponent) : (numerator << -exponent) < denominator) exponent--;
        BigInteger mantissa;
        ulong bits;
        if (exponent < -1022)
        {
            mantissa = RoundEven(numerator << 1074, denominator);
            bits = checked((ulong)mantissa); // includes a round up to minimum normal
        }
        else
        {
            var shift = 52 - exponent;
            mantissa = shift >= 0 ? RoundEven(numerator << shift, denominator) : RoundEven(numerator, denominator << -shift);
            if (mantissa >= (BigInteger.One << 53)) { mantissa >>= 1; exponent++; }
            if (exponent > 1023) throw new OverflowException("Fraction too large to convert to float");
            bits = ((ulong)(exponent + 1023) << 52) | checked((ulong)(mantissa - (BigInteger.One << 52)));
        }
        if (negative) bits |= 1UL << 63;
        return BitConverter.UInt64BitsToDouble(bits);
    }
    private static BigInteger RoundEven(BigInteger numerator, BigInteger denominator)
    {
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        var comparison = (remainder * 2).CompareTo(denominator);
        return comparison > 0 || comparison == 0 && !quotient.IsEven ? quotient + 1 : quotient;
    }
    public int CompareTo(Q other) => (numerator * other.denominator).CompareTo(other.numerator * denominator);
    public bool Equals(Q other) => numerator == other.numerator && denominator == other.denominator;
    public override bool Equals(object? obj) => obj is Q q && Equals(q);
    public override int GetHashCode() => HashCode.Combine(numerator, denominator);
    public override string ToString() => denominator.IsOne ? numerator.ToString(CultureInfo.InvariantCulture) : numerator.ToString(CultureInfo.InvariantCulture) + "/" + denominator.ToString(CultureInfo.InvariantCulture);
}
