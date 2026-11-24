using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace LocalDependencyCache.Query;

/// <summary>
/// Argument checks matching the .NET 8 ArgumentException/ArgumentNullException/ArgumentOutOfRangeException
/// helpers, which netstandard2.0 does not have.
/// </summary>
internal static class Guard
{
    public static void NotNull([NotNull] object? value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (value is null)
        {
            throw new ArgumentNullException(paramName);
        }
    }

    public static void NotNullOrWhiteSpace([NotNull] string? value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        NotNull(value, paramName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The value cannot be an empty string or composed entirely of whitespace.", paramName);
        }
    }

    public static void Positive(int value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "The value must be greater than zero.");
        }
    }
}
