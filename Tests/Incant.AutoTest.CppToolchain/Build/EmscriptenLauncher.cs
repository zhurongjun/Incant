namespace Incant.AutoTest.CppToolchain;

internal static class EmscriptenLauncher
{
    internal static (string Executable, IReadOnlyList<string> Arguments) ResolveWrapper(
        AutoTestContext context,
        ResolvedToolchain toolchain,
        BuildAction action)
    {
        string extension = Path.GetExtension(action.ExecutablePath);
        if (extension.Equals(".py", StringComparison.OrdinalIgnoreCase))
        {
            return (FindPython(context, toolchain), new[]
            {
                action.ExecutablePath,
            }.Concat(action.Arguments).ToArray());
        }

        if (OperatingSystem.IsWindows()
            && (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)))
        {
            string pythonWrapper = Path.ChangeExtension(
                action.ExecutablePath, ".py");
            if (!File.Exists(pythonWrapper))
            {
                throw new InvalidOperationException(
                    $"Wrapper '{action.ExecutablePath}' has no direct Python entry point.");
            }

            return (FindPython(context, toolchain), new[]
            {
                pythonWrapper,
            }.Concat(action.Arguments).ToArray());
        }

        return (action.ExecutablePath, action.Arguments);
    }

    private static string FindPython(
        AutoTestContext context,
        ResolvedToolchain toolchain)
    {
        if (toolchain.Environment.GetValueOrDefault("EMSDK_PYTHON") is string configured
            && File.Exists(configured))
        {
            return configured;
        }

        RuntimeManifest? runtime = context.Manifest!.Runtimes.FirstOrDefault(
            item => item.Kind == RuntimeKind.Python
                && item.InstallationId is not null
                && toolchain.InstallationIds.Contains(
                    item.InstallationId, StringComparer.Ordinal));
        if (runtime is not null && File.Exists(runtime.Path))
        {
            return runtime.Path;
        }

        string pathValue = toolchain.Environment.GetValueOrDefault("PATH")
            ?? System.Environment.GetEnvironmentVariable("PATH")
            ?? string.Empty;
        string[] extensions = OperatingSystem.IsWindows()
            ? [".exe", ".cmd", ".bat", string.Empty]
            : [string.Empty];
        foreach (string directory in pathValue.Split(
            Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string name in new[] { "python", "python3" })
            {
                foreach (string candidateExtension in extensions)
                {
                    string path = Path.Combine(
                        directory.Trim().Trim('"'),
                        name + candidateExtension);
                    if (File.Exists(path)
                        && !path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                        && !path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                    {
                        return path;
                    }
                }
            }
        }

        throw new InvalidOperationException(
            "A direct Python executable is required to run this wrapper.");
    }
}
