using System.Buffers.Binary;

namespace Incant.Core.Cpp;

/// <summary>Reads binary headers without running a program or inferring its architecture from this process.</summary>
internal static class ExecutableArchitecture
{
    internal static IReadOnlyList<TargetArchitecture> Read(string path) =>
        ReadImages(path).Select(image => image.Architecture).Distinct().ToArray();

    internal static TargetArchitecture Select(IReadOnlyList<TargetArchitecture> architectures, TargetArchitecture? requested)
    {
        if (requested is TargetArchitecture architecture)
        {
            return architectures.Contains(architecture) ? architecture : TargetArchitecture.Unknown;
        }

        return architectures.Count == 1 ? architectures[0] : TargetArchitecture.Unknown;
    }

    // Null means the file format did not establish an ABI, as with a linker script or LLVM bitcode archive.
    internal static bool? MatchesTarget(string path, TargetIdentity target)
    {
        IReadOnlyList<BinaryImage> images = ReadImages(path);
        if (images.Count == 0)
        {
            return null;
        }

        int addressSize = target.Architecture is TargetArchitecture.X86 or TargetArchitecture.ARM or TargetArchitecture.Wasm32
            || target.Abi.EndsWith("x32", StringComparison.Ordinal)
            || target.Triple.StartsWith("arm64_32-", StringComparison.OrdinalIgnoreCase) ? 32 : 64;
        return images.Any(image => image.Architecture == target.Architecture && image.AddressSize == addressSize
            && image.Format switch
            {
                BinaryFormat.Elf => target.Platform is TargetPlatform.Linux or TargetPlatform.Android,
                BinaryFormat.Coff => target.Platform == TargetPlatform.Windows,
                BinaryFormat.MachO => target.Platform is TargetPlatform.MacOS or TargetPlatform.IOS or TargetPlatform.IOSSimulator
                    or TargetPlatform.TvOS or TargetPlatform.TvOSSimulator or TargetPlatform.WatchOS or TargetPlatform.WatchOSSimulator
                    or TargetPlatform.VisionOS or TargetPlatform.VisionOSSimulator,
                BinaryFormat.Wasm => target.Platform is TargetPlatform.Wasi or TargetPlatform.Emscripten,
                _ => false,
            });
    }

    private static IReadOnlyList<BinaryImage> ReadImages(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            byte[] header = new byte[64];
            if (stream.Length < 20)
            {
                return [];
            }

            stream.ReadExactly(header.AsSpan(0, (int)Math.Min(header.Length, stream.Length)));
            if (header.AsSpan(0, 8).SequenceEqual("!<arch>\n"u8))
            {
                return ReadArchive(stream);
            }

            if (header[0] == 'M' && header[1] == 'Z')
            {
                if (stream.Length < 64)
                {
                    return [];
                }

                int offset = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(60));
                if (offset < 0 || offset > stream.Length - 6)
                {
                    return [];
                }

                stream.Position = offset;
                stream.ReadExactly(header.AsSpan(0, 6));
                return header.AsSpan(0, 4).SequenceEqual("PE\0\0"u8)
                    ? One(CoffImage(BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)))) : [];
            }

            uint magic = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (magic is 0xcafebabe or 0xcafebabf or 0xbebafeca or 0xbfbafeca)
            {
                bool isLittleEndian = magic is 0xbebafeca or 0xbfbafeca;
                uint ReadUInt32(ReadOnlySpan<byte> bytes) => isLittleEndian
                    ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);
                uint count = ReadUInt32(header.AsSpan(4));
                int entrySize = magic is 0xcafebabf or 0xbfbafeca ? 32 : 20;
                if (count > 64 || 8L + count * entrySize > stream.Length)
                {
                    return [];
                }

                var images = new List<BinaryImage>();
                for (int index = 0; index < count; index++)
                {
                    stream.Position = 8L + index * entrySize;
                    stream.ReadExactly(header.AsSpan(0, 4));
                    if (MachImage(ReadUInt32(header)) is BinaryImage image)
                    {
                        images.Add(image);
                    }
                }

                return images.Distinct().ToArray();
            }

            return One(ObjectImage(header));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IReadOnlyList<BinaryImage> ReadArchive(FileStream stream)
    {
        long offset = 8;
        var images = new HashSet<BinaryImage>();
        byte[] header = new byte[60];
        byte[] objectHeader = new byte[20];
        while (offset <= stream.Length - header.Length)
        {
            stream.Position = offset;
            stream.ReadExactly(header);
            if (!header.AsSpan(58, 2).SequenceEqual("`\n"u8)
                || !long.TryParse(System.Text.Encoding.ASCII.GetString(header, 48, 10).Trim(), out long size)
                || size < 0 || size > stream.Length - offset - header.Length)
            {
                return [];
            }

            string name = System.Text.Encoding.ASCII.GetString(header, 0, 16).Trim();
            int nameLength = 0;
            if (name.StartsWith("#1/", StringComparison.Ordinal)
                && (!int.TryParse(name.AsSpan(3), out nameLength) || nameLength < 0 || nameLength > size))
            {
                return [];
            }

            if (size - nameLength >= objectHeader.Length && name is not "/" and not "//" and not "/SYM64/"
                && !name.Contains("SYMDEF", StringComparison.Ordinal))
            {
                stream.Position = offset + header.Length + nameLength;
                stream.ReadExactly(objectHeader);
                if (ObjectImage(objectHeader) is BinaryImage image)
                {
                    images.Add(image);
                }
            }

            offset += header.Length + size + (size & 1);
        }

        // Ordinary archives cannot advertise mixed object ABIs as universal support.
        return images.Count == 1 ? images.ToArray() : [];
    }

    private static BinaryImage? ObjectImage(ReadOnlySpan<byte> header) => BinaryPrimitives.ReadUInt32BigEndian(header) switch
    {
        0xfeedface or 0xfeedfacf => MachImage(BinaryPrimitives.ReadUInt32BigEndian(header[4..])),
        0xcefaedfe or 0xcffaedfe => MachImage(BinaryPrimitives.ReadUInt32LittleEndian(header[4..])),
        0x7f454c46 => ElfImage(header),
        0x0061736d when BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) == 1 =>
            new BinaryImage(TargetArchitecture.Wasm32, 32, BinaryFormat.Wasm),
        _ => CoffImage(BinaryPrimitives.ReadUInt16LittleEndian(header)),
    };

    private static BinaryImage? ElfImage(ReadOnlySpan<byte> header)
    {
        if (header[4] is not 1 and not 2 || header[5] is not 1 and not 2)
        {
            return null;
        }

        ushort machine = header[5] == 2 ? BinaryPrimitives.ReadUInt16BigEndian(header[18..])
            : BinaryPrimitives.ReadUInt16LittleEndian(header[18..]);
        TargetArchitecture architecture = machine switch
        {
            3 => TargetArchitecture.X86,
            62 => TargetArchitecture.X64,
            40 => TargetArchitecture.ARM,
            183 => TargetArchitecture.ARM64,
            _ => TargetArchitecture.Unknown,
        };
        return architecture == TargetArchitecture.Unknown ? null
            : new BinaryImage(architecture, header[4] == 1 ? 32 : 64, BinaryFormat.Elf);
    }

    private static BinaryImage? CoffImage(ushort machine) => machine switch
    {
        0x014c => new BinaryImage(TargetArchitecture.X86, 32, BinaryFormat.Coff),
        0x8664 => new BinaryImage(TargetArchitecture.X64, 64, BinaryFormat.Coff),
        0x01c4 => new BinaryImage(TargetArchitecture.ARM, 32, BinaryFormat.Coff),
        0xaa64 => new BinaryImage(TargetArchitecture.ARM64, 64, BinaryFormat.Coff),
        _ => null,
    };

    private static BinaryImage? MachImage(uint value) => value switch
    {
        7 => new BinaryImage(TargetArchitecture.X86, 32, BinaryFormat.MachO),
        0x01000007 => new BinaryImage(TargetArchitecture.X64, 64, BinaryFormat.MachO),
        12 => new BinaryImage(TargetArchitecture.ARM, 32, BinaryFormat.MachO),
        0x0100000c => new BinaryImage(TargetArchitecture.ARM64, 64, BinaryFormat.MachO),
        0x0200000c => new BinaryImage(TargetArchitecture.ARM64, 32, BinaryFormat.MachO),
        _ => null,
    };

    private static IReadOnlyList<BinaryImage> One(BinaryImage? image) => image is null ? [] : [image.Value];

    private readonly record struct BinaryImage(TargetArchitecture Architecture, int AddressSize, BinaryFormat Format);

    private enum BinaryFormat
    {
        Elf,
        Coff,
        MachO,
        Wasm,
    }
}
