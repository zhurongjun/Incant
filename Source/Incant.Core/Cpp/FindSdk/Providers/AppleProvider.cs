using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Incant.Base;
using Incant.Core.Cpp;

namespace Incant.Core.Cpp.FindSdk;

/// <summary>Discovers actual Apple SDKs in Xcode, CLT and explicit .sdk paths, retaining partial installations.</summary>
public sealed partial class AppleProvider : IDiscoveryProvider
{
    /// <inheritdoc />
    public string Name => "Apple SDK";

    /// <inheritdoc />
    public IReadOnlyCollection<Kind> Kinds { get; } = Array.AsReadOnly(new[] { Kind.Apple });

    /// <inheritdoc />
    public async Task<DiscoveryResult> DiscoverAsync(SdkQuery query, DiscoveryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS())
        {
            return new DiscoveryResult();
        }

        var candidates = new List<Candidate>();
        var diagnostics = new List<Diagnostic>();
        string? explicitRoot = query.RootPath is null ? null : Sdk.Absolute(query.RootPath);
        if (explicitRoot?.EndsWith(".sdk", StringComparison.OrdinalIgnoreCase) == true)
        {
            candidates.Add(new Candidate(explicitRoot, Source.Explicit));
        }
        else
        {
            IReadOnlyList<Candidate> developers = await AppleLocator.EnvironmentsAsync(explicitRoot, context, cancellationToken).ConfigureAwait(false);
            foreach (Candidate developer in developers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    IEnumerable<string> roots = SearchPaths.Directories(Path.Combine(developer.Path, "SDKs"))
                        .Concat(SearchPaths.Directories(Path.Combine(developer.Path, "Platforms"))
                            .SelectMany(platform => SearchPaths.Directories(Path.Combine(platform, "Developer", "SDKs"))));
                    foreach (string sdk in roots.Where(path => path.EndsWith(".sdk", StringComparison.OrdinalIgnoreCase)))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        foreach (Source source in developer.Sources)
                        {
                            candidates.Add(new Candidate(sdk, source));
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    diagnostics.Add(Resources.Missing(Name, exception.Message, developer.Path));
                }
            }

            if (explicitRoot is null)
            {
                await AddEnvironmentSdkAsync(candidates, diagnostics, context, cancellationToken).ConfigureAwait(false);
            }
        }

        DiscoveryResult[] results = await Task.WhenAll(Candidate.Merge(candidates).Select(candidate => Task.Run(
            () => InspectAsync(candidate, context, cancellationToken), cancellationToken))).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new DiscoveryResult(results.SelectMany(result => result.Sdks), diagnostics.Concat(results.SelectMany(result => result.Diagnostics)));
    }

    private async Task AddEnvironmentSdkAsync(List<Candidate> candidates, List<Diagnostic> diagnostics,
        DiscoveryContext context, CancellationToken cancellationToken)
    {
        string? sdkRoot = context.GetEnvironmentVariable("SDKROOT");
        if (string.IsNullOrWhiteSpace(sdkRoot))
        {
            return;
        }

        try
        {
            if (Path.IsPathFullyQualified(sdkRoot))
            {
                if (Directory.Exists(sdkRoot))
                {
                    candidates.Add(new Candidate(sdkRoot, Source.Environment));
                }
                else
                {
                    diagnostics.Add(Resources.Missing(Name, "The SDKROOT directory does not exist.", sdkRoot));
                }

                return;
            }

            if (!SdkIdentifier().IsMatch(sdkRoot))
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "invalid-sdkroot", Name,
                    "SDKROOT must be an absolute SDK directory or a recognized Apple SDK name.", sdkRoot));
                return;
            }

            string? developer = await AppleLocator.ResolveDeveloperAsync(context, cancellationToken).ConfigureAwait(false);
            ProcessResult? result = await context.ProbeAsync("/usr/bin/xcrun",
                ["--no-cache", "--sdk", sdkRoot, "--show-sdk-path"], cancellationToken,
                AppleLocator.Environment(developer)).ConfigureAwait(false);
            string? reported = result?.StandardOutput.Trim();
            if (!string.IsNullOrWhiteSpace(reported) && Path.IsPathFullyQualified(reported) && Directory.Exists(reported))
            {
                candidates.Add(new Candidate(reported, Source.Environment));
            }
            else
            {
                diagnostics.Add(Resources.Missing(Name, "The SDKROOT name could not be resolved to an installed SDK.", sdkRoot));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "invalid-sdkroot", Name, exception.Message, sdkRoot));
        }
    }

    private async Task<DiscoveryResult> InspectAsync(Candidate candidate, DiscoveryContext context, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string root = candidate.Path;
            if (!Directory.Exists(root))
            {
                return new DiscoveryResult(diagnostics: [Resources.Missing(Name, "The SDK directory no longer exists.", root)]);
            }

            var diagnostics = new List<Diagnostic>();
            var layoutDiagnostics = new List<Diagnostic>();
            JsonElement metadata = await ReadMetadataAsync(root, diagnostics, cancellationToken).ConfigureAwait(false);
            string canonical = GetString(metadata, "CanonicalName") ?? Path.GetFileNameWithoutExtension(root);
            TargetPlatform platform = ReadPlatform(metadata, canonical);
            string? versionText = GetString(metadata, "Version");
            Version? version = ReadVersion(versionText);
            if (versionText is not null && version is null)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "invalid-sdk-metadata", Name,
                    "The SDK Version field is not a valid version.", root));
            }

            version ??= SdkIdentifier().IsMatch(canonical) ? SearchPaths.Version(canonical) : null;
            var architectures = new HashSet<TargetArchitecture>();
            (Version? minimumDeployment, Version? defaultDeployment) = ReadTargetSupport(
                metadata, platform, architectures, layoutDiagnostics, root, cancellationToken);

            string? developer = AppleLocator.FindDeveloper(root);
            if (architectures.Count == 0)
            {
                await ReadSystemArchitecturesAsync(root, platform, architectures, layoutDiagnostics,
                    developer, context, cancellationToken).ConfigureAwait(false);
            }

            if (version is null)
            {
                // An independent SDK can use the active tools to read its own metadata without acquiring their product identity.
                string? probeDeveloper = developer ?? await AppleLocator.ResolveDeveloperAsync(context, cancellationToken).ConfigureAwait(false);
                if (probeDeveloper is not null)
                {
                    ProcessResult? result = await context.ProbeAsync("/usr/bin/xcrun",
                        ["--no-cache", "--sdk", root, "--show-sdk-version"], cancellationToken,
                        AppleLocator.Environment(probeDeveloper)).ConfigureAwait(false);
                    version = ReadVersion(result?.StandardOutput);
                }

                if (version is null)
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "unknown-sdk-version", Name,
                        "The SDK version could not be established.", root));
                }
            }

            ProcessResult? product = developer is not null && Directory.Exists(Path.Combine(developer, "Platforms"))
                ? await context.ProbeAsync("/usr/bin/xcodebuild", ["-version"], cancellationToken,
                    AppleLocator.Environment(developer)).ConfigureAwait(false) : null;
            IReadOnlyList<Resource> resources = ReadResources(root, layoutDiagnostics, cancellationToken);
            if (platform == TargetPlatform.Unknown)
            {
                layoutDiagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "unknown-target-platform", Name,
                    "The SDK platform could not be established from its canonical identity.", root));
            }

            if (architectures.Count == 0)
            {
                layoutDiagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "unknown-target-architecture", Name,
                    "SDK architectures could not be established from metadata or the system library.", root));
            }

            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<TargetArchitecture> confirmed = architectures.Count == 0 ? [TargetArchitecture.Unknown] : architectures.Order();
            TargetLayout[] layouts = confirmed.Select(architecture => new TargetLayout(platform, architecture, resources,
                sysrootPath: root, minimumDeploymentVersion: minimumDeployment,
                defaultDeploymentVersion: defaultDeployment, diagnostics: layoutDiagnostics)).ToArray();
            var sdk = new Sdk(Kind.Apple, root, layouts, version, developer,
                SearchPaths.Version(product?.StandardOutput), channel: candidate.Channel ?? SearchPaths.Channel(
                    string.Join(" ", developer, root, canonical, versionText)), sources: candidate.Sources, diagnostics: diagnostics);
            return new DiscoveryResult([sdk]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new DiscoveryResult(diagnostics: [Resources.Missing(Name, exception.Message, candidate.Path)]);
        }
    }

    private (Version? Minimum, Version? Default) ReadTargetSupport(JsonElement metadata, TargetPlatform platform,
        HashSet<TargetArchitecture> architectures, List<Diagnostic> diagnostics, string root, CancellationToken cancellationToken)
    {
        if (metadata.ValueKind != JsonValueKind.Object || !metadata.TryGetProperty("SupportedTargets", out JsonElement targets))
        {
            return (null, null);
        }

        if (targets.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "invalid-target-metadata", Name,
                "SupportedTargets must be an object.", root));
            return (null, null);
        }

        Version? minimumDeployment = null;
        Version? defaultDeployment = null;
        foreach (JsonProperty target in targets.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (platform == TargetPlatform.Unknown || AppleLocator.Platform(target.Name) != platform)
            {
                continue;
            }

            if (target.Value.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "invalid-target-metadata", Name,
                    "The SDK target description must be an object.", root));
                continue;
            }

            minimumDeployment = SearchPaths.Version(GetString(target.Value, "MinimumDeploymentTarget"));
            defaultDeployment = SearchPaths.Version(GetString(target.Value, "DefaultDeploymentTarget"));
            foreach (string property in new[] { "Archs", "ValidArchs", "SupportedArchitectures" })
            {
                if (!target.Value.TryGetProperty(property, out JsonElement values))
                {
                    continue;
                }

                if (values.ValueKind != JsonValueKind.Array)
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "invalid-target-metadata", Name,
                        $"{property} must be an array of architecture names.", root));
                    continue;
                }

                foreach (JsonElement value in values.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TargetArchitecture architecture = value.ValueKind == JsonValueKind.String
                        ? SearchPaths.Architecture(value.GetString()) : TargetArchitecture.Unknown;
                    if (architecture == TargetArchitecture.Unknown)
                    {
                        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "unknown-target-architecture", Name,
                            $"An entry in {property} does not identify a supported architecture.", root));
                    }
                    else
                    {
                        architectures.Add(architecture);
                    }
                }
            }
        }

        return (minimumDeployment, defaultDeployment);
    }

    private async Task ReadSystemArchitecturesAsync(string root, TargetPlatform platform,
        HashSet<TargetArchitecture> architectures, List<Diagnostic> diagnostics, string? developer,
        DiscoveryContext context, CancellationToken cancellationToken)
    {
        foreach (string name in new[] { "libSystem.tbd", "libSystem.B.tbd" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.Combine(root, "usr", "lib", name);
            try
            {
                string? stub = await SearchPaths.ReadTextAsync(path, cancellationToken).ConfigureAwait(false);
                if (stub is null)
                {
                    continue;
                }

                // Only the main library's top-level header describes all supported targets; export subsets do not.
                Match match = StubArchitectures().Match(stub);
                if (match.Success)
                {
                    foreach (string item in match.Groups[2].Value.Split([',', '\n'],
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        string value = item.Trim('\'', '"');
                        if (match.Groups[1].Value == "targets" && StubPlatform(value) != platform)
                        {
                            continue;
                        }

                        TargetArchitecture architecture = SearchPaths.Architecture(value);
                        if (architecture != TargetArchitecture.Unknown)
                        {
                            architectures.Add(architecture);
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(Resources.Missing(Name, exception.Message, path));
            }

            if (architectures.Count > 0)
            {
                return;
            }
        }

        string? library = new[] { "libSystem.dylib", "libSystem.B.dylib" }
            .Select(name => Path.Combine(root, "usr", "lib", name)).FirstOrDefault(File.Exists);
        if (library is null)
        {
            return;
        }

        string? probeDeveloper = developer ?? await AppleLocator.ResolveDeveloperAsync(context, cancellationToken).ConfigureAwait(false);
        if (probeDeveloper is null)
        {
            return;
        }

        ProcessResult? result = await context.ProbeAsync("/usr/bin/lipo", ["-archs", library], cancellationToken,
            AppleLocator.Environment(probeDeveloper)).ConfigureAwait(false);
        foreach (string value in (result?.StandardOutput ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            TargetArchitecture architecture = SearchPaths.Architecture(value);
            if (architecture != TargetArchitecture.Unknown)
            {
                architectures.Add(architecture);
            }
        }
    }

    private IReadOnlyList<Resource> ReadResources(string root, List<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<Resource> resources;
        try
        {
            resources = Resources.Sysroot(root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Resources.Missing(Name, exception.Message, root));
            var available = new ResourceCollector(root);
            Resources.Headers(available, Path.Combine(root, "usr", "include"));
            Resources.Headers(available, Path.Combine(root, "include"));
            available.Add(ResourcePurpose.CppInclude, Path.Combine(root, "usr", "include", "c++", "v1"));
            available.Add(ResourcePurpose.LibraryDirectory, Path.Combine(root, "usr", "lib"));
            available.Add(ResourcePurpose.Framework, Path.Combine(root, "System", "Library", "Frameworks"));
            foreach (string name in new[] { "libSystem.tbd", "libSystem.B.tbd", "libSystem.dylib", "libSystem.B.dylib" })
            {
                available.Add(ResourcePurpose.Library, Path.Combine(root, "usr", "lib", name));
            }

            resources = available.Build();
        }

        if (!File.Exists(Path.Combine(root, "usr", "include", "stdio.h")) && !File.Exists(Path.Combine(root, "include", "stdio.h")))
        {
            diagnostics.Add(Resources.Missing(Name, "The SDK's C system headers are missing (stdio.h was not found).",
                Path.Combine(root, "usr", "include")));
        }

        if (!resources.Any(resource => resource.Purpose == ResourcePurpose.Library
            && Path.GetFileName(resource.Path) is "libSystem.tbd" or "libSystem.B.tbd" or "libSystem.dylib" or "libSystem.B.dylib"))
        {
            diagnostics.Add(Resources.Missing(Name, "The SDK's system library or text stub is missing.", Path.Combine(root, "usr", "lib")));
        }

        string frameworks = Path.Combine(root, "System", "Library", "Frameworks");
        try
        {
            if (!SearchPaths.Directories(frameworks).Any(path => path.EndsWith(".framework", StringComparison.Ordinal)))
            {
                diagnostics.Add(Resources.Missing(Name, "The SDK's system frameworks are missing.", frameworks));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Resources.Missing(Name, exception.Message, frameworks));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return resources;
    }

    private async Task<JsonElement> ReadMetadataAsync(string root, List<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        bool hasMetadata = false;
        foreach (string name in new[] { "SDKSettings.json", "SDKSettings.plist" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.Combine(root, name);
            try
            {
                string? text = await SearchPaths.ReadTextAsync(path, cancellationToken).ConfigureAwait(false);
                if (text is null)
                {
                    continue;
                }

                hasMetadata = true;
                JsonElement metadata;
                if (name.EndsWith(".json", StringComparison.Ordinal))
                {
                    using JsonDocument document = JsonDocument.Parse(text);
                    metadata = document.RootElement.Clone();
                }
                else
                {
                    using XmlReader reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Ignore,
                        XmlResolver = null,
                    });
                    XDocument xml = XDocument.Load(reader);
                    XElement? value = xml.Root?.Elements().FirstOrDefault();
                    metadata = value is null ? default : JsonSerializer.SerializeToElement(ReadPlistValue(value));
                }

                if (metadata.ValueKind == JsonValueKind.Object)
                {
                    return metadata;
                }

                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "invalid-sdk-metadata", Name,
                    "SDK settings must contain a metadata dictionary.", path));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or XmlException)
            {
                hasMetadata = true;
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "invalid-sdk-metadata", Name, exception.Message, path));
            }
        }

        if (!hasMetadata)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "missing-sdk-metadata", Name,
                "SDK settings are missing; the directory identity and installed resources were retained.", root));
        }

        return default;
    }

    private static object? ReadPlistValue(XElement element)
    {
        if (element.Name.LocalName == "dict")
        {
            XElement[] entries = element.Elements().ToArray();
            if (entries.Length % 2 != 0)
            {
                throw new XmlException("A plist dictionary must contain key/value pairs.");
            }

            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (int index = 0; index < entries.Length; index += 2)
            {
                if (entries[index].Name.LocalName != "key"
                    || !values.TryAdd(entries[index].Value, ReadPlistValue(entries[index + 1])))
                {
                    throw new XmlException("A plist dictionary contains an invalid or duplicate key.");
                }
            }

            return values;
        }

        return element.Name.LocalName == "array"
            ? element.Elements().Select(ReadPlistValue).ToArray() : element.Value;
    }

    private static Version? ReadVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string text = value.Trim();
        return Version.TryParse(text.Contains('.') ? text : text + ".0", out Version? version) ? version : null;
    }

    private static TargetPlatform ReadPlatform(JsonElement metadata, string canonical)
    {
        TargetPlatform platform = AppleLocator.Platform(Letters().Match(canonical).Value);
        if (platform != TargetPlatform.Unknown || metadata.ValueKind != JsonValueKind.Object
            || !metadata.TryGetProperty("SupportedTargets", out JsonElement targets) || targets.ValueKind != JsonValueKind.Object)
        {
            return platform;
        }

        TargetPlatform[] supported = targets.EnumerateObject().Select(target => AppleLocator.Platform(target.Name))
            .Where(target => target != TargetPlatform.Unknown).Distinct().ToArray();
        return supported.Length == 1 ? supported[0] : TargetPlatform.Unknown;
    }

    private static TargetPlatform StubPlatform(string target)
    {
        int separator = target.IndexOf('-');
        return separator < 0 ? TargetPlatform.Unknown : target[(separator + 1)..] switch
        {
            "macos" or "macosx" => TargetPlatform.MacOS,
            "ios" => TargetPlatform.IOS,
            "ios-simulator" => TargetPlatform.IOSSimulator,
            "tvos" => TargetPlatform.TvOS,
            "tvos-simulator" => TargetPlatform.TvOSSimulator,
            "watchos" => TargetPlatform.WatchOS,
            "watchos-simulator" => TargetPlatform.WatchOSSimulator,
            "xros" => TargetPlatform.VisionOS,
            "xros-simulator" => TargetPlatform.VisionOSSimulator,
            _ => TargetPlatform.Unknown,
        };
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex(@"^[a-zA-Z]+", RegexOptions.CultureInvariant)]
    private static partial Regex Letters();

    [GeneratedRegex(@"^(?:macosx|iphoneos|iphonesimulator|appletvos|appletvsimulator|watchos|watchsimulator|xros|xrsimulator)(?:\d+(?:\.\d+)*)?(?:\.internal)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SdkIdentifier();

    [GeneratedRegex(@"^(archs|targets)[ \t]*:[ \t]*\[([^\]]+)\]", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex StubArchitectures();
}
