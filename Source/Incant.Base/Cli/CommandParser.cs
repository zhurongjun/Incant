using System.Globalization;

namespace Incant.Base.Cli;

/// <summary>Represents one classified command-line token.</summary>
public class Token
{
    /// <summary>Identifies how a raw command-line token is interpreted.</summary>
    public enum TokenKind
    {
        /// <summary>A positional argument that does not start with a hyphen.</summary>
        Argument,

        /// <summary>A long option such as <c>--output</c> or <c>--output=value</c>.</summary>
        Option,

        /// <summary>A short option or short-option group such as <c>-o</c> or <c>-abc</c>.</summary>
        ShortOption,

        /// <summary>The <c>--</c> separator that ends option parsing.</summary>
        DoubleDash,

        /// <summary>A token after the <c>--</c> separator.</summary>
        Unparsed,
    }

    #region getters
    /// <summary>Gets the classified token kind.</summary>
    public TokenKind Kind => _kind;

    /// <summary>Gets the original token text.</summary>
    public string Raw => _raw;

    /// <summary>Gets a value indicating whether this token is a positional argument.</summary>
    public bool IsArgument => _kind == TokenKind.Argument;

    /// <summary>Gets a value indicating whether this token is a long option.</summary>
    public bool IsOption => _kind == TokenKind.Option;

    /// <summary>Gets a value indicating whether this token is a short option.</summary>
    public bool IsShortOption => _kind == TokenKind.ShortOption;

    /// <summary>Gets a value indicating whether this token is a long or short option.</summary>
    public bool IsAnyOption => _kind == TokenKind.Option || _kind == TokenKind.ShortOption;

    /// <summary>Gets a value indicating whether this token is the <c>--</c> separator.</summary>
    public bool IsDoubleDash => _kind == TokenKind.DoubleDash;

    /// <summary>Gets a value indicating whether this token follows the <c>--</c> separator.</summary>
    public bool IsUnparsed => _kind == TokenKind.Unparsed;

    /// <summary>Gets the positional argument text.</summary>
    /// <exception cref="InvalidOperationException">The token is not an argument.</exception>
    public string Argument
    {
        get
        {
            if (_kind != TokenKind.Argument)
            {
                throw new InvalidOperationException("Not an Argument token");
            }

            return _raw;
        }
    }

    /// <summary>Gets the long-option name without the leading hyphens or inline value.</summary>
    /// <exception cref="InvalidOperationException">The token is not a long option.</exception>
    public string OptionName
    {
        get
        {
            if (_kind != TokenKind.Option)
            {
                throw new InvalidOperationException("Not an Option token");
            }

            int equalsIndex = _raw.IndexOf('=');
            if (equalsIndex >= 0)
            {
                return _raw.Substring(2, equalsIndex - 2);
            }
            else
            {
                return _raw.Substring(2);
            }
        }
    }

    /// <summary>Gets the inline long-option value, or <see langword="null"/> when absent.</summary>
    /// <exception cref="InvalidOperationException">The token is not a long option.</exception>
    public string? OptionValue
    {
        get
        {
            if (_kind != TokenKind.Option)
            {
                throw new InvalidOperationException("Not an Option token");
            }

            int equalsIndex = _raw.IndexOf('=');
            if (equalsIndex >= 0)
            {
                return _raw.Substring(equalsIndex + 1);
            }
            else
            {
                return null;
            }
        }
    }

    /// <summary>Gets the names contained in a short option or short-option group.</summary>
    /// <exception cref="InvalidOperationException">The token is not a short option.</exception>
    public List<char> ShortOptionNames
    {
        get
        {
            if (_kind != TokenKind.ShortOption)
            {
                throw new InvalidOperationException("Not a ShortOption token");
            }

            int equalsIndex = _raw.IndexOf('=');
            string namesText = equalsIndex >= 0 ? _raw.Substring(1, equalsIndex - 1) : _raw.Substring(1);
            List<char> names = [];
            foreach (char shortOptionName in namesText)
            {
                names.Add(shortOptionName);
            }

            return names;
        }
    }

    /// <summary>Gets the inline short-option value, or <see langword="null"/> when absent.</summary>
    /// <exception cref="InvalidOperationException">The token is not a short option.</exception>
    public string? ShortOptionValue
    {
        get
        {
            if (_kind != TokenKind.ShortOption)
            {
                throw new InvalidOperationException("Not a ShortOption token");
            }

            int equalsIndex = _raw.IndexOf('=');
            if (equalsIndex >= 0)
            {
                return _raw.Substring(equalsIndex + 1);
            }
            else
            {
                return null;
            }
        }
    }

    /// <summary>Determines whether this token is the specified long option.</summary>
    /// <param name="name">The option name without leading hyphens.</param>
    /// <returns><see langword="true"/> when the option name matches.</returns>
    public bool IsOptionOf(string name)
    {
        return _kind == TokenKind.Option && OptionName == name;
    }

    /// <summary>Determines whether this token contains only the specified short option.</summary>
    /// <param name="shortName">The short-option character.</param>
    /// <returns><see langword="true"/> when this is a single matching short option.</returns>
    public bool IsShortOptionOf(char shortName)
    {
        return _kind == TokenKind.ShortOption && ShortOptionNames.Count == 1 && ShortOptionNames.Contains(shortName);
    }

    /// <summary>Determines whether this token contains the specified short option.</summary>
    /// <param name="shortName">The short-option character.</param>
    /// <returns><see langword="true"/> when the short option is present.</returns>
    public bool HasShortOption(char shortName)
    {
        return _kind == TokenKind.ShortOption && ShortOptionNames.Contains(shortName);
    }
    #endregion

    private Token()
    {
    }

    /// <summary>Classifies a raw command-line token.</summary>
    /// <param name="raw">The original token text.</param>
    /// <returns>The classified token.</returns>
    public static Token Parse(string raw)
    {
        Token result = new();

        result._raw = raw;

        if (raw == "--")
        {
            result._kind = TokenKind.DoubleDash;
        }
        else if (raw.StartsWith("--"))
        {
            result._kind = TokenKind.Option;
        }
        else if (raw.Length >= 2
            && raw[0] == '-'
            && raw[1] >= '0'
            && raw[1] <= '9'
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            result._kind = TokenKind.Argument;
        }
        else if (raw.StartsWith("-") && raw.Length >= 2)
        {
            result._kind = TokenKind.ShortOption;
        }
        else
        {
            result._kind = TokenKind.Argument;
        }

        return result;
    }

    /// <summary>Creates a token that must not be parsed as an option.</summary>
    /// <param name="raw">The original token text.</param>
    /// <returns>An unparsed token.</returns>
    public static Token Unparsed(string raw)
    {
        Token result = new();
        result._raw = raw;
        result._kind = TokenKind.Unparsed;
        return result;
    }

    private TokenKind _kind;
    private string _raw = string.Empty;
}

/// <summary>Tracks matched, remaining, and rest command-line tokens.</summary>
public class TokenList
{
    /// <summary>Initializes a token list from preclassified tokens.</summary>
    /// <param name="tokens">The tokens to track.</param>
    public TokenList(IEnumerable<Token> tokens)
    {
        _tokens.AddRange(tokens);
    }

    /// <summary>Initializes a token list from raw command-line arguments.</summary>
    /// <param name="arguments">The arguments to classify and track.</param>
    public TokenList(IEnumerable<string> arguments)
    {
        bool doNotParse = false;
        foreach (string argument in arguments)
        {
            if (doNotParse)
            {
                _tokens.Add(Token.Unparsed(argument));
            }
            else
            {
                Token token = Token.Parse(argument);
                if (token.IsDoubleDash)
                {
                    doNotParse = true;
                }
                _tokens.Add(token);
            }
        }
    }

    /// <summary>Determines whether an unused token remains.</summary>
    /// <returns><see langword="true"/> when another token can be read.</returns>
    public bool HasMore() => _index < _tokens.Count;

    /// <summary>Returns the next unused token without consuming it.</summary>
    /// <returns>The next unused token.</returns>
    /// <exception cref="InvalidOperationException">No unused token remains.</exception>
    public Token Peek()
    {
        if (!HasMore())
        {
            throw new InvalidOperationException("No more tokens");
        }

        return _tokens[_index];
    }

    /// <summary>Consumes the next token as a parser match.</summary>
    /// <returns>The consumed token.</returns>
    /// <exception cref="InvalidOperationException">No unused token remains.</exception>
    public Token Match()
    {
        if (!HasMore())
        {
            throw new InvalidOperationException("No more tokens");
        }

        Token token = _tokens[_index];
        _matchedTokens.Add(token);
        ++_index;
        return token;
    }

    /// <summary>Consumes the next token as a remaining argument.</summary>
    /// <returns>The consumed token.</returns>
    /// <exception cref="InvalidOperationException">No unused token remains.</exception>
    public Token Rest()
    {
        if (!HasMore())
        {
            throw new InvalidOperationException("No more tokens");
        }

        Token token = _tokens[_index];
        _restTokens.Add(token);
        ++_index;
        return token;
    }

    /// <summary>Moves every unused token except the <c>--</c> separator to the rest collection.</summary>
    public void ResetAllUnused()
    {
        for (int i = _index; i < _tokens.Count; ++i)
        {
            if (_tokens[i].IsDoubleDash)
            {
                continue;
            }

            _restTokens.Add(_tokens[i]);
        }
        _index = _tokens.Count;
    }

    /// <summary>Resets traversal and clears the matched and rest collections.</summary>
    public void ResetToHead()
    {
        _index = 0;
        _matchedTokens.Clear();
        _restTokens.Clear();
    }

    /// <summary>Consumes the next token when it is a positional argument.</summary>
    /// <returns>The argument token, or <see langword="null"/> when the next token is not an argument.</returns>
    public Token? TryTakeArgument()
    {
        if (HasMore() && Peek().IsArgument)
        {
            return Match();
        }
        return null;
    }

    /// <summary>Gets all tracked tokens.</summary>
    public IEnumerable<Token> AllTokens => _tokens;

    /// <summary>Gets tokens consumed as parser matches.</summary>
    public IEnumerable<Token> MatchedTokens => _matchedTokens;

    /// <summary>Gets tokens consumed as remaining arguments.</summary>
    public IEnumerable<Token> RestTokens => _restTokens;

    /// <summary>Gets tokens that have not yet been consumed.</summary>
    public IEnumerable<Token> UnusedTokens
    {
        get
        {
            for (int i = _index; i < _tokens.Count; ++i)
            {
                yield return _tokens[i];
            }
        }
    }

    private readonly List<Token> _tokens = [];

    private readonly List<Token> _matchedTokens = [];
    private readonly List<Token> _restTokens = [];
    private int _index;
}

/// <summary>Defines a command-line option consumed by <see cref="CommandParser"/>.</summary>
public interface IOption
{
    /// <summary>Gets the long-option name without leading hyphens.</summary>
    public string Name { get; }

    /// <summary>Gets the optional short-option character.</summary>
    public char? ShortName { get; }

    /// <summary>Gets the user-facing option help text.</summary>
    public string Help { get; }

    /// <summary>Gets a value indicating whether the option must be provided.</summary>
    public bool IsRequired { get; }

    /// <summary>Gets the accepted values, or <see langword="null"/> when values are unrestricted.</summary>
    public IEnumerable<string>? Selections { get; }

    /// <summary>Gets a value indicating whether the option is a value-less toggle.</summary>
    public bool IsToggle { get; }

    /// <summary>Gets the default value displayed in help output.</summary>
    public string DefaultValue { get; }

    /// <summary>Gets the value type name displayed in help output.</summary>
    public string ValueTypeName { get; }

    /// <summary>Assigns a parsed value to the option.</summary>
    /// <param name="context">The active parse context.</param>
    /// <param name="value">The parsed option value.</param>
    void Assign(ParseContext context, string value);

    /// <summary>Activates the toggle option.</summary>
    /// <param name="context">The active parse context.</param>
    void Toggle(ParseContext context);
}

/// <summary>Defines a receiver for command-line arguments not consumed as options.</summary>
public interface IRestOption
{
    /// <summary>Gets the user-facing help text for remaining arguments.</summary>
    public string Help { get; }

    /// <summary>Gets a value indicating whether at least one remaining argument must be provided.</summary>
    public bool IsRequired { get; }

    /// <summary>Gets a value indicating whether options may appear between remaining arguments.</summary>
    public bool AllowMixed { get; }

    /// <summary>Gets a value indicating whether remaining arguments require a preceding <c>--</c>.</summary>
    public bool RequireDoubleDash { get; }

    /// <summary>Assigns the collected remaining arguments.</summary>
    /// <param name="context">The active parse context.</param>
    /// <param name="values">The collected raw argument values.</param>
    void Assign(ParseContext context, List<string> values);
}

/// <summary>Describes an executable command, its options, and its subcommands.</summary>
public class Command
{
    /// <summary>Executes a parsed command.</summary>
    /// <returns>The command exit code.</returns>
    public delegate int ExecuteDelegate();

    private readonly List<IOption> _options = [];
    private readonly List<Command> _subCommands = [];
    private IRestOption? _restOption;

    #region Config
    /// <summary>Gets the full command name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the optional short command name.</summary>
    public char? ShortName { get; init; }

    /// <summary>Gets the user-facing command help text.</summary>
    public required string Help { get; init; }

    /// <summary>Gets the usage text displayed in command help.</summary>
    public required string Usage { get; init; }

    /// <summary>Gets the banner used instead of the parser's default banner when nonempty.</summary>
    public string CustomBanner { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether invoking this command prints help instead of executing.</summary>
    public bool IsHelpCommand { get; init; }
    #endregion

    #region exec info

    /// <summary>Gets the command executor used when this is not a help command.</summary>
    public ExecuteDelegate? Execute { get; init; }

    /// <summary>Gets the command options.</summary>
    /// <exception cref="ArgumentNullException">The configured option collection is <see langword="null"/>.</exception>
    public IReadOnlyList<IOption> Options
    {
        get => _options;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _options.Clear();
            foreach (IOption option in value)
            {
                AddOption(option);
            }
        }
    }

    /// <summary>Gets the receiver for remaining arguments.</summary>
    public IRestOption? RestOption
    {
        get => _restOption;
        init => _restOption = value;
    }

    /// <summary>Gets the available subcommands.</summary>
    /// <exception cref="ArgumentNullException">The configured subcommand collection is <see langword="null"/>.</exception>
    public IReadOnlyList<Command> SubCommands
    {
        get => _subCommands;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _subCommands.Clear();
            foreach (Command command in value)
            {
                AddSubCommand(command);
            }
        }
    }
    #endregion

    /// <summary>Finds an option by its full name, or by short name when given one character.</summary>
    /// <param name="name">The full option name or one-character short name.</param>
    /// <returns>The matching option, or <see langword="null"/> when no option matches.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public IOption? FindOption(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 1)
        {
            return FindOption(name[0]);
        }

        return Options.FirstOrDefault(option => string.Equals(option.Name, name, StringComparison.Ordinal));
    }

    /// <summary>Finds an option by its short name.</summary>
    /// <param name="shortName">The short-option character.</param>
    /// <returns>The matching option, or <see langword="null"/> when no option matches.</returns>
    public IOption? FindOption(char shortName)
    {
        return Options.FirstOrDefault(option => option.ShortName == shortName);
    }

    /// <summary>Finds a typed option by its full name, or by short name when given one character.</summary>
    /// <typeparam name="TOption">The expected option type.</typeparam>
    /// <param name="name">The full option name or one-character short name.</param>
    /// <returns>The matching typed option, or <see langword="null"/> when its name or type does not match.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public TOption? FindOption<TOption>(string name)
        where TOption : class, IOption
    {
        return FindOption(name) as TOption;
    }

    /// <summary>Finds a typed option by its short name.</summary>
    /// <typeparam name="TOption">The expected option type.</typeparam>
    /// <param name="shortName">The short-option character.</param>
    /// <returns>The matching typed option, or <see langword="null"/> when its name or type does not match.</returns>
    public TOption? FindOption<TOption>(char shortName)
        where TOption : class, IOption
    {
        return FindOption(shortName) as TOption;
    }

    /// <summary>Finds a subcommand by its full name, or by short name when given one character.</summary>
    /// <param name="name">The full subcommand name or one-character short name.</param>
    /// <returns>The matching subcommand, or <see langword="null"/> when no subcommand matches.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public Command? FindSubCommand(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 1)
        {
            return FindSubCommand(name[0]);
        }

        return SubCommands.FirstOrDefault(
            command => string.Equals(command.Name, name, StringComparison.Ordinal));
    }

    /// <summary>Finds a subcommand by its short name.</summary>
    /// <param name="shortName">The short subcommand name.</param>
    /// <returns>The matching subcommand, or <see langword="null"/> when no subcommand matches.</returns>
    public Command? FindSubCommand(char shortName)
    {
        return SubCommands.FirstOrDefault(command => command.ShortName == shortName);
    }

    /// <summary>Adds an option to this command.</summary>
    /// <param name="option">The option to add.</param>
    /// <exception cref="ArgumentNullException"><paramref name="option"/> is <see langword="null"/>.</exception>
    public void AddOption(IOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        _options.Add(option);
    }

    /// <summary>Sets the receiver for remaining arguments.</summary>
    /// <param name="restOption">The receiver to set.</param>
    /// <exception cref="ArgumentNullException"><paramref name="restOption"/> is <see langword="null"/>.</exception>
    public void SetRestOption(IRestOption restOption)
    {
        ArgumentNullException.ThrowIfNull(restOption);
        _restOption = restOption;
    }

    /// <summary>Removes the first occurrence of an option instance.</summary>
    /// <param name="option">The option instance to remove.</param>
    /// <exception cref="ArgumentNullException"><paramref name="option"/> is <see langword="null"/>.</exception>
    public void RemoveOption(IOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        int index = _options.FindIndex(candidate => ReferenceEquals(candidate, option));
        if (index >= 0)
        {
            _options.RemoveAt(index);
        }
    }

    /// <summary>Removes every option with the specified full name.</summary>
    /// <param name="name">The full option name to remove.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public void RemoveOption(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        _options.RemoveAll(option => string.Equals(option.Name, name, StringComparison.Ordinal));
    }

    /// <summary>Removes all options from this command.</summary>
    public void ClearOptions()
    {
        _options.Clear();
    }

    /// <summary>Removes the receiver for remaining arguments.</summary>
    public void ClearRestOption()
    {
        _restOption = null;
    }

    /// <summary>Adds a subcommand to this command.</summary>
    /// <param name="command">The subcommand to add.</param>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    public void AddSubCommand(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _subCommands.Add(command);
    }

    /// <summary>Removes the first occurrence of a subcommand instance.</summary>
    /// <param name="command">The subcommand instance to remove.</param>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    public void RemoveSubCommand(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);
        int index = _subCommands.FindIndex(candidate => ReferenceEquals(candidate, command));
        if (index >= 0)
        {
            _subCommands.RemoveAt(index);
        }
    }

    /// <summary>Removes every subcommand with the specified full name.</summary>
    /// <param name="name">The full subcommand name to remove.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public void RemoveSubCommand(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        _subCommands.RemoveAll(command => string.Equals(command.Name, name, StringComparison.Ordinal));
    }

    /// <summary>Removes all subcommands from this command.</summary>
    public void ClearSubCommands()
    {
        _subCommands.Clear();
    }

    /// <summary>Checks option names on this command and, optionally, all descendants.</summary>
    /// <param name="recursive">Whether to check options on descendant commands.</param>
    /// <exception cref="InvalidOperationException">
    /// An option name is too short, or a full or short option name is duplicated.
    /// </exception>
    public void CheckOptions(bool recursive = false)
    {
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        HashSet<char> seenShortNames = [];

        foreach (IOption option in Options)
        {
            if (string.IsNullOrEmpty(option.Name) || option.Name.Length <= 1)
            {
                throw new InvalidOperationException($"Option name too short: --{option.Name}");
            }

            if (!seenNames.Add(option.Name))
            {
                throw new InvalidOperationException($"Duplicate option name: --{option.Name}");
            }

            if (option.ShortName is char shortName && !seenShortNames.Add(shortName))
            {
                throw new InvalidOperationException($"Duplicate option short name: -{shortName}");
            }
        }

        if (recursive)
        {
            foreach (Command subCommand in SubCommands)
            {
                subCommand.CheckOptions(true);
            }
        }
    }

    /// <summary>Checks subcommand names on this command and, optionally, all descendants.</summary>
    /// <param name="recursive">Whether to check subcommands on descendant commands.</param>
    /// <exception cref="InvalidOperationException">
    /// A subcommand name is too short, or a full or short subcommand name is duplicated.
    /// </exception>
    public void CheckSubCommands(bool recursive = false)
    {
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        HashSet<char> seenShortNames = [];

        foreach (Command subCommand in SubCommands)
        {
            if (string.IsNullOrEmpty(subCommand.Name) || subCommand.Name.Length <= 1)
            {
                throw new InvalidOperationException($"Sub-command name too short: {subCommand.Name}");
            }

            if (!seenNames.Add(subCommand.Name))
            {
                throw new InvalidOperationException($"Duplicate sub-command name: {subCommand.Name}");
            }

            if (subCommand.ShortName is char shortName && !seenShortNames.Add(shortName))
            {
                throw new InvalidOperationException($"Duplicate sub-command short name: -{shortName}");
            }
        }

        if (recursive)
        {
            foreach (Command subCommand in SubCommands)
            {
                subCommand.CheckSubCommands(true);
            }
        }
    }

    /// <summary>Checks options and subcommands on this command and, optionally, all descendants.</summary>
    /// <param name="recursive">Whether to check the complete descendant command tree.</param>
    /// <exception cref="InvalidOperationException">
    /// An option or subcommand name is too short, or a full or short name is duplicated.
    /// </exception>
    public void CheckAll(bool recursive = false)
    {
        CheckOptions(recursive);
        CheckSubCommands(recursive);
    }

    /// <summary>Writes help for the command, subcommands, options, and remaining arguments.</summary>
    /// <param name="writer">The writer that receives the formatted help text.</param>
    /// <param name="defaultBanner">The default banner used when <see cref="CustomBanner"/> is empty.</param>
    public void WriteHelp(Writer writer, string defaultBanner)
    {
        string selectedBanner = string.IsNullOrEmpty(CustomBanner) ? defaultBanner : CustomBanner;
        if (!string.IsNullOrEmpty(selectedBanner))
        {
            writer
                .StyleBold().StyleFrontGreen()
                .Write(selectedBanner)
                .StyleClear();
        }

        if (!string.IsNullOrEmpty(Help))
        {
            writer
                .StyleFrontGreen()
                .StyleBold()
                .WriteKeepIndent(Help)
                .StyleClear()
                .NextLine();
        }

        if (!string.IsNullOrEmpty(Usage))
        {
            writer
                .StyleBold()
                .Write("Usage: ")
                .StyleClear()
                .StyleFrontBlue()
                .WriteKeepIndent(Usage)
                .StyleClear()
                .NextLine();
        }

        if (SubCommands.Count != 0)
        {
            writer
                .StyleBold()
                .WriteLine("Sub-Commands:")
                .StyleClear();

            int maxSubCommandLength = 0;
            foreach (Command subCommand in SubCommands)
            {
                int subCommandLength = 0;
                subCommandLength += 3;
                subCommandLength += subCommand.Name.Length;
                maxSubCommandLength = Math.Max(subCommandLength, maxSubCommandLength);
            }

            foreach (Command subCommand in SubCommands)
            {
                string subCommandText;
                if (subCommand.ShortName != null)
                {
                    subCommandText = $"{subCommand.ShortName}, {subCommand.Name}";
                }
                else
                {
                    subCommandText = $"   {subCommand.Name}";
                }
                subCommandText = subCommandText.PadRight(maxSubCommandLength);

                writer
                    .StyleFrontMagenta()
                    .StyleBold()
                    .Write($"  {subCommandText} : ")
                    .StyleClear()
                    .StyleClear()
                    .WriteKeepIndent(subCommand.Help)
                    .NextLine();
            }
        }

        if (Options.Count != 0)
        {
            writer
                .StyleBold()
                .WriteLine("Options: ")
                .StyleClear();

            int maxOptionLength = 0;
            int maxTypeLength = 0;
            int maxDefaultLength = 0;
            foreach (IOption option in Options)
            {
                int optionLength = 0;
                optionLength += 4;
                optionLength += 2 + option.Name.Length;
                maxOptionLength = Math.Max(optionLength, maxOptionLength);

                int typeLength = option.ValueTypeName.Length;
                maxTypeLength = Math.Max(typeLength, maxTypeLength);

                int defaultLength = option.DefaultValue.Length;
                maxDefaultLength = Math.Max(defaultLength, maxDefaultLength);
            }

            foreach (IOption option in Options)
            {
                string typeText = $"<{option.ValueTypeName}>".PadRight(maxTypeLength + 2);

                string defaultText = $"[{option.DefaultValue}]".PadRight(maxDefaultLength + 2);

                string optionText;
                if (option.ShortName != null)
                {
                    optionText = $"-{option.ShortName}, --{option.Name}";
                }
                else
                {
                    optionText = $"    --{option.Name}";
                }
                optionText = optionText.PadRight(maxOptionLength);

                string selectionsText = string.Empty;
                {
                    IEnumerable<string>? selections = option.Selections;
                    if (selections != null)
                    {
                        string[] selectionLines = selections
                            .Select(selection => $"  - {selection}")
                            .ToArray();
                        if (selectionLines.Length != 0)
                        {
                            selectionsText = "\n\n" + string.Join("\n", selectionLines);
                        }
                    }
                }

                string requiredText = option.IsRequired ? "[REQUIRED] " : "";

                writer
                    .Write("  ")
                    .StyleFrontYellow()
                    .Write($"  {typeText} ")
                    .StyleClear()
                    .StyleFrontGreen()
                    .StyleBold()
                    .Write($"{optionText} ")
                    .StyleClear()
                    .StyleFrontGray()
                    .StyleBold()
                    .Write($"{defaultText}: ")
                    .StyleClear()
                    .StyleClear()
                    .WriteKeepIndent($"{requiredText}{option.Help}{selectionsText}")
                    .NextLine();
            }
        }

        if (RestOption != null)
        {
            string requiredText = RestOption.IsRequired ? "[REQUIRED] " : string.Empty;
            writer
                .StyleBold()
                .WriteLine("Rest-Option: ")
                .StyleClear()
                .Write("  ")
                .WriteKeepIndent($"{requiredText}{RestOption.Help}")
                .NextLine();
        }
    }
}
/// <summary>Holds the mutable state and diagnostics for one parse operation.</summary>
public class ParseContext
{
    /// <summary>Gets the tokens being consumed by the current parse operation.</summary>
    public required TokenList Tokens { get; init; }

    /// <summary>Gets the snapshot of tokens that belong to the selected command.</summary>
    public TokenList? CommandTokens { get; private set; }

    /// <summary>Gets the writer that collects diagnostics and help output.</summary>
    public Writer Writer { get; init; } = new();

    /// <summary>Gets a value indicating whether an error has been reported.</summary>
    public bool AnyError => ErrorCount > 0;

    /// <summary>Gets a value indicating whether a warning has been reported.</summary>
    public bool AnyWarning => WarningCount > 0;

    /// <summary>Gets the number of reported errors.</summary>
    public int ErrorCount { get; private set; }

    /// <summary>Gets the number of reported warnings.</summary>
    public int WarningCount { get; private set; }

    /// <summary>Gets the options successfully visited during parsing.</summary>
    public HashSet<IOption> VisitedOptions { get; } = [];

    /// <summary>Reports an error and marks the parse operation as failed.</summary>
    /// <param name="error">The error message.</param>
    public void Error(string error)
    {
        ErrorCount++;
        Writer
            .StyleFrontRed()
            .StyleBold()
            .Write("Error: ")
            .StyleClear()
            .WriteLine(error);
    }

    /// <summary>Reports a warning without marking the parse operation as failed.</summary>
    /// <param name="warning">The warning message.</param>
    public void Warning(string warning)
    {
        WarningCount++;
        Writer
            .StyleFrontYellow()
            .StyleBold()
            .Write("Warning: ")
            .StyleClear()
            .WriteLine(warning);
    }

    /// <summary>Snapshots the currently unused tokens when no command-token snapshot exists.</summary>
    public void StoreCommandTokens()
    {
        if (CommandTokens == null)
        {
            CommandTokens = new TokenList(Tokens.UnusedTokens);
        }
    }
}

/// <summary>Parses command-line arguments and invokes a configured command.</summary>
public class CommandParser
{
    /// <summary>Gets the root command to parse.</summary>
    public required Command RootCommand { get; init; }

    /// <summary>Gets or sets whether an unrecognized argument prevents command execution.</summary>
    public bool FailOnUnrecognizedArgument { get; set; } = true;

    /// <summary>Gets or sets whether an unrecognized option prevents command execution.</summary>
    public bool FailOnUnrecognizedOption { get; set; } = true;

    /// <summary>Gets or sets whether a missing option argument prevents command execution.</summary>
    public bool TreatMissingArgumentAsError { get; set; } = true;

    /// <summary>
    /// Gets the command exit code, <c>0</c> for help or a command without an executor,
    /// or <c>-1</c> when parsing failed or invocation has not started.
    /// </summary>
    public int ExitCode { get; private set; } = -1;

    /// <summary>Gets or sets the default banner displayed in help output.</summary>
    public string DefaultBanner { get; set; } = string.Empty;

    /// <summary>Checks the command tree, parses arguments, and invokes the selected command.</summary>
    /// <param name="arguments">The command-line arguments to parse.</param>
    /// <returns>The selected command when parsing succeeds; otherwise, <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arguments"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The configured command tree is invalid.</exception>
    public Command? Invoke(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ExitCode = -1;
        RootCommand.CheckAll(true);

        ParseContext context = new()
        {
            Tokens = new TokenList(arguments)
        };

        Command command = SolveCommand(context);
        context.StoreCommandTokens();

        if (InvokeCommand(command, context))
        {
            return command;
        }

        return null;
    }

    private Command SolveCommand(ParseContext context)
    {
        Command currentCommand = RootCommand;
        while (context.Tokens.HasMore())
        {
            Token token = context.Tokens.Peek();
            if (!token.IsArgument)
            {
                break;
            }

            string commandName = token.Argument;
            Command? nextCommand = currentCommand.FindSubCommand(commandName);
            if (nextCommand == null)
            {
                break;
            }

            currentCommand = nextCommand;
            context.Tokens.Match();
        }

        return currentCommand;
    }

    private bool PrintHelpIfRequested(Command command, Token token, ParseContext context)
    {
        if (token.IsOptionOf("help") || token.IsShortOptionOf('h'))
        {
            List<Token> unusedTokens = context.CommandTokens!.AllTokens
                .Where(candidateToken => candidateToken != token)
                .ToList();
            if (unusedTokens.Count > 0)
            {
                string unusedText = string.Join(", ", unusedTokens
                    .Select(candidateToken => $"'{candidateToken.Raw}'"));
                context.Warning($"These tokens are ignored when printing help: {unusedText}");
            }

            command.WriteHelp(context.Writer, DefaultBanner);
            context.Writer.Dump();
            return true;
        }

        return false;
    }

    private bool InvokeCommand(Command command, ParseContext context)
    {
        TokenList tokenList = context.Tokens;

        while (tokenList.HasMore())
        {
            Token token = tokenList.Peek();

            switch (token.Kind)
            {
                case Token.TokenKind.Argument:
                    if (command.RestOption != null)
                    {
                        if (command.RestOption.AllowMixed)
                        {
                            tokenList.Rest();
                        }
                        else if (!command.RestOption.RequireDoubleDash)
                        {
                            tokenList.ResetAllUnused();
                        }
                        else
                        {
                            HandleUnrecognizedArgument(token, context);
                            tokenList.Match();
                        }
                    }
                    else
                    {
                        HandleUnrecognizedArgument(token, context);
                        tokenList.Match();
                    }
                    break;

                case Token.TokenKind.DoubleDash:
                    if (command.RestOption != null)
                    {
                        tokenList.Match();
                        tokenList.ResetAllUnused();
                    }
                    else
                    {
                        tokenList.Match();
                        HandleUnrecognizedArgument(token, context);
                        while (tokenList.HasMore())
                        {
                            HandleUnrecognizedArgument(tokenList.Match(), context);
                        }
                    }
                    break;
                case Token.TokenKind.Option:
                    if (PrintHelpIfRequested(command, token, context))
                    {
                        ExitCode = 0;
                        return false;
                    }
                    else
                    {
                        ParseOption(command, token, context);
                    }
                    break;
                case Token.TokenKind.ShortOption:
                    if (PrintHelpIfRequested(command, token, context))
                    {
                        ExitCode = 0;
                        return false;
                    }
                    else
                    {
                        ParseShortOption(command, token, context);
                    }
                    break;
            }
        }

        if (command.RestOption != null)
        {
            List<string> restArguments = tokenList.RestTokens.Select(restToken => restToken.Raw).ToList();
            if (command.RestOption.IsRequired && restArguments.Count == 0)
            {
                context.Error("Lost REQUIRED remaining arguments");
            }

            command.RestOption.Assign(context, restArguments);
        }

        {
            List<IOption> lostRequiredOptions = new();
            foreach (IOption option in command.Options)
            {
                if (option.IsRequired && !context.VisitedOptions.Contains(option))
                {
                    lostRequiredOptions.Add(option);
                }
            }

            if (lostRequiredOptions.Count > 0)
            {
                string lostOptionsText = string.Join(", ", lostRequiredOptions.Select(option => $"--{option.Name}"));
                context.Error($"Lost REQUIRED options: [{lostOptionsText}]");
            }
        }

        if (!context.AnyError)
        {
            if (command.IsHelpCommand)
            {
                command.WriteHelp(context.Writer, DefaultBanner);
                context.Writer.Dump();
                ExitCode = 0;
            }
            else
            {
                ExitCode = command.Execute?.Invoke() ?? 0;
            }

            return true;
        }
        else
        {
            command.WriteHelp(context.Writer, DefaultBanner);
            context.Writer.Dump();
        }

        return false;
    }

    private void ParseOption(Command command, Token token, ParseContext context)
    {
        TokenList tokenList = context.Tokens;
        string optionName = token.OptionName;
        string? optionValue = token.OptionValue;

        tokenList.Match();

        if (optionName.Length < 2)
        {
            context.Error(
                $"Long option name '--{optionName}' must contain at least two characters");
            return;
        }

        IOption? option = command.FindOption(optionName);
        if (option == null)
        {
            HandleUnrecognizedOption(token, optionName, context);
        }
        else
        {
            if (option.IsToggle)
            {
                AssignToggle(option, optionValue, token, context);
            }
            else
            {
                optionValue ??= tokenList.TryTakeArgument()?.Argument;
                AssignOption(option, optionValue, token, context);
            }
        }
    }

    private void ParseShortOption(Command command, Token token, ParseContext context)
    {
        TokenList tokenList = context.Tokens;
        List<char> optionNames = token.ShortOptionNames;
        string? optionValue = token.ShortOptionValue;

        tokenList.Match();

        if (optionNames.Count == 0)
        {
            context.Error($"Short option '{token.Raw}' must contain at least one name");
            return;
        }

        if (optionNames.Count > 1)
        {
            if (optionValue != null)
            {
                string optionNamesText = new(optionNames.ToArray());
                context.Error($"Toggle group -{optionNamesText} in '{token.Raw}' cannot have a value");
                return;
            }

            int errorCountBefore = context.ErrorCount;
            List<IOption> toggleOptions = [];
            foreach (char shortName in optionNames)
            {
                IOption? option = command.FindOption(shortName);
                if (option == null)
                {
                    HandleUnrecognizedShortOption(token, shortName, context);
                }
                else
                {
                    if (!option.IsToggle)
                    {
                        context.Error($"Short option -{shortName} in '{token.Raw}' is not a toggle option, cannot be used in toggle group");
                    }
                    else
                    {
                        toggleOptions.Add(option);
                    }
                }
            }

            if (context.ErrorCount != errorCountBefore)
            {
                return;
            }

            foreach (IOption option in toggleOptions)
            {
                AssignToggle(option, null, token, context);
            }
        }
        else
        {
            char shortName = optionNames[0];
            IOption? option = command.FindOption(shortName);
            if (option == null)
            {
                HandleUnrecognizedShortOption(token, shortName, context);
            }
            else
            {
                if (option.IsToggle)
                {
                    AssignToggle(option, optionValue, token, context);
                }
                else
                {
                    optionValue ??= tokenList.TryTakeArgument()?.Argument;
                    AssignOption(option, optionValue, token, context);
                }
            }
        }
    }

    private void AssignToggle(IOption option, string? optionValue, Token token, ParseContext context)
    {
        if (optionValue != null)
        {
            context.Error($"Short option '{token.Raw}' is a toggle option, cannot have a value");
        }
        else
        {
            context.VisitedOptions.Add(option);
            option.Toggle(context);
        }
    }

    private void AssignOption(IOption option, string? optionValue, Token token, ParseContext context)
    {
        if (string.IsNullOrEmpty(optionValue))
        {
            HandleOptionMissingArgument(option, token, context);
        }
        else
        {
            IEnumerable<string>? selections = option.Selections;
            if (selections != null && !selections.Contains(optionValue))
            {
                context.Error($"Option '{option.Name}' with value '{optionValue}' is not in selections");
                return;
            }

            context.VisitedOptions.Add(option);
            option.Assign(context, optionValue);
        }
    }

    private void HandleUnrecognizedArgument(Token token, ParseContext context)
    {
        string message = $"Unrecognized argument: {token.Raw}";
        if (FailOnUnrecognizedArgument)
        {
            context.Error(message);
        }
        else
        {
            context.Warning(message);
        }
    }

    private void HandleUnrecognizedShortOption(Token token, char shortOptionName, ParseContext context)
    {
        string message = $"Unrecognized short option: -{shortOptionName} in '{token.Raw}'";
        if (FailOnUnrecognizedOption)
        {
            context.Error(message);
        }
        else
        {
            context.Warning(message);
        }
    }

    private void HandleUnrecognizedOption(Token token, string optionName, ParseContext context)
    {
        string message = $"Unrecognized option: --{optionName} in '{token.Raw}'";
        if (FailOnUnrecognizedOption)
        {
            context.Error(message);
        }
        else
        {
            context.Warning(message);
        }
    }

    private void HandleOptionMissingArgument(IOption option, Token token, ParseContext context)
    {
        string message = $"Missing argument: --{option.Name} in '{token.Raw}'";
        if (TreatMissingArgumentAsError)
        {
            context.Error(message);
        }
        else
        {
            context.Warning(message);
        }
    }
}
