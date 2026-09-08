namespace Incant.AutoTest.CXToolchain;

internal static class AutoTestWorkspace
{
    internal static void ResetRunDirectories(AutoTestContext context)
    {
        ResetCategoryDirectory(context.Options.WorkRoot, "cases");
        ResetCategoryDirectory(context.Options.WorkRoot, "logs");
    }

    internal static string ResetCaseDirectory(
        AutoTestContext context,
        string candidateId) =>
        ResetChildDirectory(context.Options.WorkRoot, "cases", candidateId);

    internal static string ResetLogDirectory(
        AutoTestContext context,
        string candidateId) =>
        ResetChildDirectory(context.Options.WorkRoot, "logs", candidateId);

    internal static void DeleteDirectory(string rootPath, string path)
    {
        string root = Normalize(rootPath);
        RejectReparsePoint(root);
        string candidate = Normalize(path);
        EnsureDescendant(root, candidate);
        if (!Directory.Exists(candidate))
        {
            return;
        }

        RejectReparsePoint(candidate);
        Directory.Delete(candidate, recursive: true);
    }

    private static void ResetCategoryDirectory(
        string rootPath,
        string category)
    {
        ValidateSegment(category, nameof(category));

        string root = Normalize(rootPath);
        Directory.CreateDirectory(root);
        RejectReparsePoint(root);
        string categoryPath = Path.Combine(root, category);
        EnsureDescendant(root, categoryPath);
        if (File.Exists(categoryPath))
        {
            throw new InvalidOperationException(
                $"Workspace category '{categoryPath}' is not a directory.");
        }

        if (Directory.Exists(categoryPath))
        {
            RejectReparsePoint(categoryPath);
            Directory.Delete(categoryPath, recursive: true);
        }

        Directory.CreateDirectory(categoryPath);
    }

    private static string ResetChildDirectory(
        string rootPath,
        string category,
        string candidateId)
    {
        ValidateSegment(category, nameof(category));
        ValidateSegment(candidateId, nameof(candidateId));

        string root = Normalize(rootPath);
        Directory.CreateDirectory(root);
        RejectReparsePoint(root);
        string categoryPath = Path.Combine(root, category);
        EnsureDescendant(root, categoryPath);
        if (File.Exists(categoryPath))
        {
            throw new InvalidOperationException(
                $"Workspace category '{categoryPath}' is not a directory.");
        }

        if (Directory.Exists(categoryPath))
        {
            RejectReparsePoint(categoryPath);
        }
        else
        {
            Directory.CreateDirectory(categoryPath);
        }

        string candidatePath = Path.Combine(categoryPath, candidateId);
        EnsureDescendant(root, candidatePath);
        if (File.Exists(candidatePath))
        {
            throw new InvalidOperationException(
                $"Candidate workspace '{candidatePath}' is not a directory.");
        }

        if (Directory.Exists(candidatePath))
        {
            RejectReparsePoint(candidatePath);
            Directory.Delete(candidatePath, recursive: true);
        }

        Directory.CreateDirectory(candidatePath);
        return candidatePath;
    }

    private static void EnsureDescendant(string root, string path)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidOperationException(
                $"Workspace path '{path}' lies outside '{root}'.");
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Workspace directory '{path}' is a reparse point.");
        }
    }

    private static void ValidateSegment(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Path.IsPathFullyQualified(value)
            || value.IndexOfAny(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new ArgumentException(
                "Workspace path segments must be nonempty names.",
                parameterName);
        }
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
