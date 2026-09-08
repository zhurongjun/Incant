using static Incant.Core.Cpp.Arguments.CppArguments;

namespace Incant.Core.Cpp.Arguments;

internal static class CppArchiveArguments
{
    internal static void Generate(CppGenerationContext context)
    {
        CppArchiveMode mode = context.Get(ArchiveMode);
        CppArchiveDialect dialect = context.Get(ArchiveDialect);
        IReadOnlyList<string> inputs = context.List(Inputs);
        context.Reject(Inputs, mode is CppArchiveMode.List or CppArchiveMode.Index && inputs.Count > 0,
            "Listing and indexing do not accept new archive members.");
        CppLto lto = context.Get(Lto);
        if (lto != CppLto.None && mode != CppArchiveMode.List)
        {
            context.Reject(ArchiveSupportsLto, !context.Get(ArchiveSupportsLto),
                "LTO archiving requires an explicitly confirmed compatible archiver and indexer.");
            context.Reject(Lto, context.Dialect == CppDialect.Gnu && lto == CppLto.Thin);
        }

        if (context.Get(DynamicDebug) && mode != CppArchiveMode.List)
        {
            CppFeatureValidation.DynamicDebug(context);
            context.Reject(DynamicDebug, dialect != CppArchiveDialect.Msvc);
            context.Add("/dynamicdeopt");
        }

        string archive = context.Required(Output);
        if (dialect == CppArchiveDialect.Ranlib)
        {
            context.Reject(ArchiveMode, mode != CppArchiveMode.Index, "A ranlib command only indexes an existing archive.");
            if (context.Has(DeterministicArchive))
            {
                context.Add(context.Get(DeterministicArchive) ? "-D" : "-U");
            }

            CppCommonArguments.Raw(context, CppRawPosition.BeforeInputs);
            context.Add(archive);
            return;
        }

        if (dialect == CppArchiveDialect.Msvc)
        {
            context.Add("/NOLOGO");
            context.Reject(ArchiveMode, mode == CppArchiveMode.Index,
                "MSVC lib indexes while writing; it has no standalone index operation.");
            context.Reject(DeterministicArchive, context.Has(DeterministicArchive),
                "This library-manager interface has no stable deterministic-mode switch.");
            if (mode == CppArchiveMode.List)
            {
                context.Add("/LIST", archive);
            }
            else
            {
                context.Add("/OUT:" + archive);
                if (mode == CppArchiveMode.Append)
                {
                    context.Add(archive);
                }

                if (lto != CppLto.None && context.Dialect == CppDialect.Msvc)
                {
                    context.Add("/LTCG");
                }
            }
        }
        else if (dialect == CppArchiveDialect.AppleLibtool)
        {
            context.Reject(ArchiveMode, mode is CppArchiveMode.List or CppArchiveMode.Index,
                "Use ar for listing and ranlib for indexing Apple archives.");
            context.Reject(DeterministicArchive, context.Has(DeterministicArchive),
                "Apple archive determinism requires the caller's ZERO_AR_DATE execution environment.");
            context.Add("-static", "-o", archive);
            if (mode == CppArchiveMode.Append)
            {
                context.Add(archive);
            }
        }
        else
        {
            string flags = mode switch
            {
                CppArchiveMode.Create => "rcs",
                CppArchiveMode.Append => "qs",
                CppArchiveMode.List => "t",
                _ => "s",
            };
            if (context.Has(DeterministicArchive) && mode != CppArchiveMode.List)
            {
                flags += context.Get(DeterministicArchive) ? "D" : "U";
            }

            context.Add(flags, archive);
        }

        CppCommonArguments.Raw(context, CppRawPosition.BeforeInputs);
        if (mode is CppArchiveMode.Create or CppArchiveMode.Append)
        {
            context.Add(inputs.ToArray());
        }
    }
}
