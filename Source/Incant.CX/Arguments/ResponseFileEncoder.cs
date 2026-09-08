using System.Text;

namespace Incant.CX.Arguments;

/// <summary>The parser consuming a response file, independent of the host operating system.</summary>
public enum ResponseFileDialect
{
    /// <summary>GNU shell-like response tokenization, including GNU-style LLVM tools.</summary>
    Gnu,
    /// <summary>Microsoft quoting and UTF-16 response-file encoding.</summary>
    Msvc,
    /// <summary>LLVM Windows command-line quoting with UTF-8 encoding.</summary>
    LlvmWindows,
}

/// <summary>Encodes logical arguments for a selected response parser. It never creates files.</summary>
public static class ResponseFileEncoder
{
    /// <summary>Returns detached response bytes including the required encoding preamble.</summary>
    /// <remarks>NUL, CR, LF and nested response references are rejected; the command-preparation layer owns file creation.</remarks>
    public static byte[] Encode(IEnumerable<string> arguments, ResponseFileDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!Enum.IsDefined(dialect))
        {
            throw new ArgumentOutOfRangeException(nameof(dialect));
        }

        var text = new StringBuilder();
        foreach (string argument in arguments)
        {
            if (argument is null || argument.IndexOfAny(['\0', '\r', '\n']) >= 0 || argument.StartsWith('@'))
            {
                throw new ArgumentException("Response arguments cannot be null, contain NUL/newlines, or reference nested response files.", nameof(arguments));
            }

            text.Append(dialect == ResponseFileDialect.Gnu ? Gnu(argument) : Windows(argument));
            text.Append('\n');
        }

        Encoding encoding = dialect == ResponseFileDialect.Msvc ? new UnicodeEncoding(false, true, true) : new UTF8Encoding(false, true);
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(text.ToString())];
    }

    private static string Gnu(string argument) => "\"" + argument.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string Windows(string argument)
    {
        var result = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            result.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
            result.Append(character);
            backslashes = 0;
        }

        result.Append('\\', backslashes * 2);
        return result.Append('"').ToString();
    }
}
