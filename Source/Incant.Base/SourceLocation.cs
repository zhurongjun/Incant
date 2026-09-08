using System.Runtime.CompilerServices;

namespace Incant.Base;

/// <summary>Provides compiler-supplied caller locations without stack inspection or filesystem access.</summary>
public static class SourceLocation
{
    /// <summary>Gets the caller's source directory, or an empty string when the path has no directory.</summary>
    /// <param name="path">The compiler-supplied source path, or an explicitly supplied path.</param>
    /// <remarks>The path is interpreted using the current platform's path rules; it is not made absolute.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public static string Directory([CallerFilePath] string path = "")
    {
        ArgumentNullException.ThrowIfNull(path);
        return Path.GetDirectoryName(path) ?? string.Empty;
    }

    /// <summary>Gets the caller's source path unchanged, or an empty string when caller information is unavailable.</summary>
    /// <param name="path">The compiler-supplied source path, or an explicit override.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public static string File([CallerFilePath] string path = "")
    {
        ArgumentNullException.ThrowIfNull(path);
        return path;
    }

    /// <summary>Gets the caller's one-based source line, or zero when caller information is unavailable.</summary>
    /// <param name="line">The compiler-supplied line number, or an explicit override.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="line"/> is negative.</exception>
    public static int Line([CallerLineNumber] int line = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(line);
        return line;
    }

    /// <summary>Gets the calling member's name, or an empty string when caller information is unavailable.</summary>
    /// <param name="member">The compiler-supplied member name, or an explicit override.</param>
    /// <exception cref="ArgumentNullException"><paramref name="member"/> is null.</exception>
    public static string MemberName([CallerMemberName] string member = "")
    {
        ArgumentNullException.ThrowIfNull(member);
        return member;
    }
}
