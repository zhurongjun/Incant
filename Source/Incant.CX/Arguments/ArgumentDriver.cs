namespace Incant.CX.Arguments;

/// <summary>Interprets one C-family command dialect and operation, without discovery, environment access or I/O.</summary>
public sealed class ArgumentDriver
{
    /// <summary>Creates an interpreter. Tool versions and platform settings are supplied as fixed configuration properties.</summary>
    public ArgumentDriver(Dialect dialect, Operation operation)
    {
        if (!Enum.IsDefined(dialect))
        {
            throw new ArgumentOutOfRangeException(nameof(dialect));
        }

        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        Dialect = dialect;
        Operation = operation;
    }

    /// <summary>Gets the compiler family and command syntax.</summary>
    public Dialect Dialect { get; }

    /// <summary>Gets the single operation being interpreted.</summary>
    public Operation Operation { get; }

    /// <summary>Generates ordered tokens; configuration errors suppress the entire argument list.</summary>
    public ArgumentGenerationResult Generate(ArgumentSet arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var context = new GenerationContext(arguments, Dialect, Operation);
        if (!context.ValidateValues())
        {
            return context.Failure();
        }

        CommonArguments.Raw(context, RawPosition.BeforeOptions);
        switch (Operation)
        {
            case Operation.Compile:
                CompileArguments.Generate(context);
                break;
            case Operation.Link:
                LinkArguments.Generate(context);
                break;
            case Operation.Archive:
                ArchiveArguments.Generate(context);
                break;
            case Operation.Resource:
                ResourceArguments.Generate(context);
                break;
        }

        CommonArguments.Raw(context, RawPosition.AfterInputs);
        return context.Finish();
    }
}
