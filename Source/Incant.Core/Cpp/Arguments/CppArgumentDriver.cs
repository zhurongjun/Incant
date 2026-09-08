using Incant.Core.Arguments;

namespace Incant.Core.Cpp.Arguments;

/// <summary>Interprets one C/C++ command dialect and operation, without discovery, environment access or I/O.</summary>
public sealed class CppArgumentDriver : IArgumentDriver
{
    /// <summary>Creates an interpreter. Tool versions and platform settings are supplied as configuration keys.</summary>
    public CppArgumentDriver(CppDialect dialect, CppOperation operation)
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
    public CppDialect Dialect { get; }

    /// <summary>Gets the single operation being interpreted.</summary>
    public CppOperation Operation { get; }

    /// <inheritdoc />
    public ArgumentGenerationResult Generate(ArgumentSet arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var context = new CppGenerationContext(arguments, Dialect, Operation);
        if (!context.ValidateValues())
        {
            return context.Failure();
        }

        CppCommonArguments.Raw(context, CppRawPosition.BeforeOptions);
        switch (Operation)
        {
            case CppOperation.Compile:
                CppCompileArguments.Generate(context);
                break;
            case CppOperation.Link:
                CppLinkArguments.Generate(context);
                break;
            case CppOperation.Archive:
                CppArchiveArguments.Generate(context);
                break;
            case CppOperation.Resource:
                CppResourceArguments.Generate(context);
                break;
        }

        CppCommonArguments.Raw(context, CppRawPosition.AfterInputs);
        return context.Finish();
    }
}
