namespace Incant.CXLegacy.Arguments;

internal static class ArchiveArguments
{
    internal static void Generate(GenerationContext context)
    {
        ArchiveMode mode = context.ArchiveMode ?? ArchiveMode.Create;
        ArchiveDialect dialect = context.ArchiveDialect ?? ArchiveDialect.Gnu;
        IReadOnlyList<string> inputs = context.Inputs ?? [];
        context.Reject(ArgumentField.Inputs, mode is ArchiveMode.List or ArchiveMode.Index && inputs.Count > 0,
            "Listing and indexing do not accept new archive members.");
        Lto lto = context.Lto ?? Lto.None;
        if (lto != Lto.None && mode != ArchiveMode.List)
        {
            context.Reject(ArgumentField.ArchiveSupportsLto, !(context.ArchiveSupportsLto ?? false),
                "LTO archiving requires an explicitly confirmed compatible archiver and indexer.");
            context.Reject(ArgumentField.Lto, context.Dialect == Dialect.Gnu && lto == Lto.Thin);
        }

        if ((context.DynamicDebug ?? false) && mode != ArchiveMode.List)
        {
            FeatureValidation.DynamicDebug(context);
            context.Reject(ArgumentField.DynamicDebug, dialect != ArchiveDialect.Msvc);
            context.Add("/dynamicdeopt");
        }

        string archive = context.Required(ArgumentField.Output, context.Output);
        if (dialect == ArchiveDialect.Ranlib)
        {
            context.Reject(ArgumentField.ArchiveMode, mode != ArchiveMode.Index, "A ranlib command only indexes an existing archive.");
            if (context.Has(ArgumentField.DeterministicArchive))
            {
                context.Add((context.DeterministicArchive ?? false) ? "-D" : "-U");
            }

            CommonArguments.Raw(context, RawPosition.BeforeInputs);
            context.Add(archive);
            return;
        }

        if (dialect == ArchiveDialect.Msvc)
        {
            context.Add("/NOLOGO");
            context.Reject(ArgumentField.ArchiveMode, mode == ArchiveMode.Index,
                "MSVC lib indexes while writing; it has no standalone index operation.");
            context.Reject(ArgumentField.DeterministicArchive, context.Has(ArgumentField.DeterministicArchive),
                "This library-manager interface has no stable deterministic-mode switch.");
            if (mode == ArchiveMode.List)
            {
                context.Add("/LIST", archive);
            }
            else
            {
                context.Add("/OUT:" + archive);
                if (mode == ArchiveMode.Append)
                {
                    context.Add(archive);
                }

                if (lto != Lto.None && context.Dialect == Dialect.Msvc)
                {
                    context.Add("/LTCG");
                }
            }
        }
        else if (dialect == ArchiveDialect.AppleLibtool)
        {
            context.Reject(ArgumentField.ArchiveMode, mode is ArchiveMode.List or ArchiveMode.Index,
                "Use ar for listing and ranlib for indexing Apple archives.");
            context.Reject(ArgumentField.DeterministicArchive, context.Has(ArgumentField.DeterministicArchive),
                "Apple archive determinism requires the caller's ZERO_AR_DATE execution environment.");
            context.Add("-static", "-o", archive);
            if (mode == ArchiveMode.Append)
            {
                context.Add(archive);
            }
        }
        else
        {
            string flags = mode switch
            {
                ArchiveMode.Create => "rcs",
                ArchiveMode.Append => "qs",
                ArchiveMode.List => "t",
                _ => "s",
            };
            if (context.Has(ArgumentField.DeterministicArchive) && mode != ArchiveMode.List)
            {
                flags += (context.DeterministicArchive ?? false) ? "D" : "U";
            }

            context.Add(flags, archive);
        }

        CommonArguments.Raw(context, RawPosition.BeforeInputs);
        if (mode is ArchiveMode.Create or ArchiveMode.Append)
        {
            context.Add(inputs.ToArray());
        }
    }
}
