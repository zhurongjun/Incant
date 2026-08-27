class EnvReader
{
    public static Dictionary<string, string> Load(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"The file '{filePath}' does not exist.");

        // Windows environment variable names are case-insensitive. When SB is
        // launched from a shell that uppercases PATH (e.g. git-bash / MSYS2),
        // cmd.exe's `set` dump writes `PATH=...`, but the rest of SB looks it
        // up as "Path". Use an OrdinalIgnoreCase comparer so either spelling
        // resolves.
        Dictionary<string, string> Result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                continue; // Skip empty lines and comments

            var parts = line.Split('=', 2);
            if (parts.Length != 2)
                continue; // Skip lines that are not key-value pairs

            var key = parts[0].Trim();
            var value = parts[1].Trim();
            Result.Add(key, value);
        }
        return Result;
    }
}