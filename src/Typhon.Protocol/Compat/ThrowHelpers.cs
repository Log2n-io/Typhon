// netstandard2.1 only (#1034): the .NET 6–8 argument guards and HashCode.AddBytes, as C# 14 extension members on the BCL types, so call sites read the
// same on both targets. Messages are the runtime's in substance; the parameter name comes from the caller's expression, as the runtime's does.
using System.Runtime.CompilerServices;

namespace System
{
    internal static class ArgumentGuards
    {
        extension(ArgumentNullException)
        {
            public static void ThrowIfNull(object argument, [CallerArgumentExpression(nameof(argument))] string paramName = null)
            {
                if (argument is null)
                {
                    throw new ArgumentNullException(paramName);
                }
            }
        }

        extension(ArgumentException)
        {
            public static void ThrowIfNullOrEmpty(string argument, [CallerArgumentExpression(nameof(argument))] string paramName = null)
            {
                if (argument is null)
                {
                    throw new ArgumentNullException(paramName);
                }

                if (argument.Length == 0)
                {
                    throw new ArgumentException("The value cannot be an empty string.", paramName);
                }
            }

            public static void ThrowIfNullOrWhiteSpace(string argument, [CallerArgumentExpression(nameof(argument))] string paramName = null)
            {
                if (argument is null)
                {
                    throw new ArgumentNullException(paramName);
                }

                if (string.IsNullOrWhiteSpace(argument))
                {
                    throw new ArgumentException("The value cannot be an empty string or composed entirely of whitespace.", paramName);
                }
            }
        }

        extension(ArgumentOutOfRangeException)
        {
            public static void ThrowIfNegative(int value, [CallerArgumentExpression(nameof(value))] string paramName = null)
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(paramName, value, "The value must be non-negative.");
                }
            }

            public static void ThrowIfNegative(long value, [CallerArgumentExpression(nameof(value))] string paramName = null)
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(paramName, value, "The value must be non-negative.");
                }
            }

            public static void ThrowIfNegative(double value, [CallerArgumentExpression(nameof(value))] string paramName = null)
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(paramName, value, "The value must be non-negative.");
                }
            }

            public static void ThrowIfNegativeOrZero(int value, [CallerArgumentExpression(nameof(value))] string paramName = null)
            {
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException(paramName, value, "The value must be positive.");
                }
            }

            public static void ThrowIfNegativeOrZero(long value, [CallerArgumentExpression(nameof(value))] string paramName = null)
            {
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException(paramName, value, "The value must be positive.");
                }
            }

            public static void ThrowIfNegativeOrZero(double value, [CallerArgumentExpression(nameof(value))] string paramName = null)
            {
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException(paramName, value, "The value must be positive.");
                }
            }

            public static void ThrowIfLessThan(int value, int other, [CallerArgumentExpression(nameof(value))] string paramName = null)
            {
                if (value < other)
                {
                    throw new ArgumentOutOfRangeException(paramName, value, $"The value must be greater than or equal to {other}.");
                }
            }

            public static void ThrowIfLessThan(double value, double other, [CallerArgumentExpression(nameof(value))] string paramName = null)
            {
                if (value < other)
                {
                    throw new ArgumentOutOfRangeException(paramName, value, $"The value must be greater than or equal to {other}.");
                }
            }

            public static void ThrowIfGreaterThan(int value, int other, [CallerArgumentExpression(nameof(value))] string paramName = null)
            {
                if (value > other)
                {
                    throw new ArgumentOutOfRangeException(paramName, value, $"The value must be less than or equal to {other}.");
                }
            }

            public static void ThrowIfGreaterThan(double value, double other, [CallerArgumentExpression(nameof(value))] string paramName = null)
            {
                if (value > other)
                {
                    throw new ArgumentOutOfRangeException(paramName, value, $"The value must be less than or equal to {other}.");
                }
            }
        }

        extension(ref HashCode hash)
        {
            public void AddBytes(ReadOnlySpan<byte> value)
            {
                foreach (var b in value)
                {
                    hash.Add(b);
                }
            }
        }
    }
}
