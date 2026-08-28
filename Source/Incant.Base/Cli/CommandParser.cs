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

    /// <summary>Gets the type name displayed in help output.</summary>
    /// <returns>The display type name.</returns>
    public string DumpTypeName();

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

    #region Config
    /// <summary>Gets or sets the full command name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional short command name.</summary>
    public char? ShortName { get; set; }

    /// <summary>Gets or sets the user-facing command help text.</summary>
    public string Help { get; set; } = string.Empty;

    /// <summary>Gets or sets the usage text displayed in command help.</summary>
    public string Usage { get; set; } = string.Empty;
    #endregion

    #region exec info

    /// <summary>Gets or sets the command executor.</summary>
    public ExecuteDelegate? Execute { get; set; }

    /// <summary>Gets or sets the command options.</summary>
    public List<IOption> Options { get; set; } = new();

    /// <summary>Gets or sets the receiver for remaining arguments.</summary>
    public IRestOption? RestOption { get; set; }

    /// <summary>Gets or sets the available subcommands.</summary>
    public List<Command> SubCommands { get; set; } = new();
    #endregion

    /// <summary>Validates that option names and short names are unique.</summary>
    /// <exception cref="InvalidOperationException">A long or short option name is duplicated.</exception>
    public void CheckOptions()
    {
        Dictionary<string, IOption> seenOptions = new();
        Dictionary<char, IOption> seenShortOptions = new();

        foreach (IOption option in Options)
        {
            if (seenOptions.ContainsKey(option.Name))
            {
                throw new InvalidOperationException($"Duplicate option name: {option.Name}");
            }
            seenOptions[option.Name] = option;

            if (option.ShortName != null)
            {
                if (seenShortOptions.ContainsKey(option.ShortName.Value))
                {
                    throw new InvalidOperationException($"Duplicate short option name: {option.ShortName}");
                }
                seenShortOptions[option.ShortName.Value] = option;
            }
        }
    }

    /// <summary>Validates that subcommand names and short names are unique.</summary>
    /// <exception cref="InvalidOperationException">A full or short subcommand name is duplicated.</exception>
    public void CheckSubCommands()
    {
        Dictionary<string, Command> seenSubCommands = new();
        Dictionary<char, Command> seenShortSubCommands = new();

        foreach (Command subCommand in SubCommands)
        {
            if (seenSubCommands.ContainsKey(subCommand.Name))
            {
                throw new InvalidOperationException($"Duplicate sub command name: {subCommand.Name}");
            }
            seenSubCommands[subCommand.Name] = subCommand;

            if (subCommand.ShortName != null)
            {
                if (seenShortSubCommands.ContainsKey(subCommand.ShortName.Value))
                {
                    throw new InvalidOperationException($"Duplicate short sub command name: {subCommand.ShortName}");
                }
                seenShortSubCommands[subCommand.ShortName.Value] = subCommand;
            }
        }
    }

    /// <summary>Writes help for the command, subcommands, options, and remaining arguments.</summary>
    /// <param name="writer">The writer that receives the formatted help text.</param>
    /// <param name="banner">The banner displayed at the start of the help text.</param>
    public void WriteHelp(Writer writer, string banner)
    {
        writer
            .StyleBold().StyleFrontGreen()
            .Write(banner)
            .StyleClear();
        writer
            .StyleFrontGreen()
            .StyleBold()
            .WriteKeepIndent(Help)
            .StyleClear()
            .NextLine();

        writer
            .StyleBold()
            .Write("Usage: ")
            .StyleClear()
            .StyleFrontBlue()
            .WriteKeepIndent(Usage)
            .StyleClear()
            .NextLine();

        if (SubCommands.Count != 0)
        {
            writer
                .StyleBold()
                .Write("Sub Commands:")
                .StyleClear()
                .NextLine();

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
                .WriteLine("Options:")
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

                int typeLength = option.DumpTypeName().Length;
                maxTypeLength = Math.Max(typeLength, maxTypeLength);

                int defaultLength = option.DefaultValue.Length;
                maxDefaultLength = Math.Max(defaultLength, maxDefaultLength);
            }

            foreach (IOption option in Options)
            {
                string typeText = $"<{option.DumpTypeName()}>".PadRight(maxTypeLength + 2);

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
                        selectionsText = "\n" + string.Join("\n", selections.Select(selection => $"  - {selection}"));
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
            writer
                .StyleBold()
                .WriteLine("Rest Options:")
                .StyleClear()
                .Write("  ")
                .WriteKeepIndent(RestOption.Help)
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
    public bool AnyError { get; private set; }

    /// <summary>Gets a value indicating whether a warning has been reported.</summary>
    public bool AnyWarning { get; private set; }

    /// <summary>Gets the options successfully visited during parsing.</summary>
    public HashSet<IOption> VisitedOptions { get; } = [];

    /// <summary>Reports an error and marks the parse operation as failed.</summary>
    /// <param name="error">The error message.</param>
    public void Error(string error)
    {
        AnyError = true;
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
        AnyWarning = true;
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
    /// <summary>Gets or sets the root command to parse.</summary>
    public required Command RootCommand { get; set; }

    /// <summary>Gets or sets whether an unrecognized argument prevents command execution.</summary>
    public bool FailOnUnrecognizedArgument { get; set; } = true;

    /// <summary>Gets or sets whether an unrecognized option prevents command execution.</summary>
    public bool FailOnUnrecognizedOption { get; set; } = true;

    /// <summary>Gets or sets whether a missing option argument prevents command execution.</summary>
    public bool TreatMissingArgumentAsError { get; set; } = true;

    /// <summary>Gets or sets whether help is printed when the selected command has no executor.</summary>
    public bool PrintHelpWhenCommandHasNoExecutor { get; set; } = true;

    /// <summary>Gets the command exit code, <c>0</c> for help, or <c>-1</c> when neither path ran.</summary>
    public int ExitCode { get; private set; } = -1;

    /// <summary>Gets or sets the banner displayed in help output.</summary>
    public string Banner { get; set; } = string.Empty;

    /// <summary>Parses arguments and invokes the selected command when validation succeeds.</summary>
    /// <param name="arguments">The command-line arguments to parse.</param>
    /// <returns>The selected command when parsing succeeds; otherwise, <see langword="null"/>.</returns>
    public Command? Invoke(IEnumerable<string> arguments)
    {
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
        Command currentCommand = RootCommand!;
        while (context.Tokens.HasMore())
        {
            Token token = context.Tokens.Peek();
            if (!token.IsArgument)
            {
                break;
            }

            string commandName = token.Argument;
            Command? nextCommand = currentCommand.SubCommands.Find(
                candidate => candidate.Name == commandName
                    || (candidate.ShortName != null && candidate.ShortName.ToString() == commandName));
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

            command.WriteHelp(context.Writer, Banner);
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
            if (command.Execute != null)
            {
                ExitCode = command.Execute.Invoke();
            }
            else if (PrintHelpWhenCommandHasNoExecutor)
            {
                command.WriteHelp(context.Writer, Banner);
                context.Writer.Dump();
            }

            return true;
        }
        else
        {
            command.WriteHelp(context.Writer, Banner);
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

        IOption? option = command.Options.Find(candidate => candidate.Name == optionName);
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

        if (optionNames.Count > 1)
        {
            if (optionValue != null)
            {
                context.Error($"Toggle group -{optionNames} in '{token.Raw}' cannot have a value");
            }

            foreach (char shortName in optionNames)
            {
                IOption? option = command.Options.Find(candidate => candidate.ShortName == shortName);
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
                        AssignToggle(option, null, token, context);
                    }
                }
            }
        }
        else
        {
            char shortName = optionNames[0];
            IOption? option = command.Options.Find(candidate => candidate.ShortName == shortName);
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
        if (optionValue == null)
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
