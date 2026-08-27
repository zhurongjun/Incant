using Serilog;
using System.Text.RegularExpressions;

namespace SB.Core
{
    using BS = BuildInstance;

    public class EmscriptenSetup : ISetup
    {
        public Emscripten Emscripten { get; } = new Emscripten();

        // Minimum version — matches the 5.0.5 SDK installed on the primary
        // workstation. Bumping this is safe; lowering requires re-verification.
        public static readonly Version MinimumEmscriptenVersion = new Version(5, 0, 5);

        public void Setup(BuildInstance instance)
        {
            if (instance.TargetOS != OSPlatform.Emscripten)
                return;

            var emsdkRoot = ProbeEmsdkRoot();
            if (string.IsNullOrEmpty(emsdkRoot))
            {
                throw new Exception(
                    "Emscripten SDK not found. Install emsdk, run `emsdk install latest && emsdk activate latest`, " +
                    "then set EMSDK (and optionally EM_CONFIG) environment variables. " +
                    "Alternatively place `emcc` on PATH.");
            }
            Emscripten.EmsdkRoot = emsdkRoot;
            Emscripten.EmscriptenRoot = Path.Combine(emsdkRoot, "upstream", "emscripten");
            if (!Directory.Exists(Emscripten.EmscriptenRoot))
                throw new Exception($"Emscripten root '{Emscripten.EmscriptenRoot}' does not exist (EMSDK points at '{emsdkRoot}').");

            // On Windows emcc / em++ / emar are dispatched through .bat wrappers that
            // invoke Python. On Unix they are shell scripts with no extension.
            string batExt = BS.HostOS == OSPlatform.Windows ? ".bat" : "";
            Emscripten.EmccPath     = RequireFile(Path.Combine(Emscripten.EmscriptenRoot, "emcc"     + batExt));
            Emscripten.EmppPath     = RequireFile(Path.Combine(Emscripten.EmscriptenRoot, "em++"     + batExt));
            Emscripten.EmarPath     = RequireFile(Path.Combine(Emscripten.EmscriptenRoot, "emar"     + batExt));
            Emscripten.EmranlibPath = RequireFile(Path.Combine(Emscripten.EmscriptenRoot, "emranlib" + batExt));

            // Optional: node and python. emsdk exports these via env vars.
            Emscripten.NodePath   = Environment.GetEnvironmentVariable("EMSDK_NODE");
            Emscripten.PythonPath = Environment.GetEnvironmentVariable("EMSDK_PYTHON");

            // Prime the parent process environment so spawned emcc.bat child
            // processes inherit EMSDK / EM_CONFIG even if the calling shell did
            // not have them set (e.g. a shell launched before `setx`).
            Environment.SetEnvironmentVariable("EMSDK", emsdkRoot);
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EM_CONFIG")))
            {
                var emConfig = Path.Combine(emsdkRoot, ".emscripten");
                if (File.Exists(emConfig))
                    Environment.SetEnvironmentVariable("EM_CONFIG", emConfig);
            }

            // Prepend emscripten bin dir to PATH so recursive tool lookups inside
            // emcc.bat resolve correctly.
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            char sep = BS.HostOS == OSPlatform.Windows ? ';' : ':';
            if (!currentPath.Split(sep).Any(p => string.Equals(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Emscripten.EmscriptenRoot, StringComparison.OrdinalIgnoreCase)))
            {
                Environment.SetEnvironmentVariable("PATH", Emscripten.EmscriptenRoot + sep + currentPath);
            }

            // Probe version via `emcc --version`. Use null env so child inherits
            // the just-updated parent env.
            ProbeEmccVersion();

            // Now that paths + version are known, create tool instances.
            Emscripten.Emcc = new EmccCompiler(Emscripten);
            Emscripten.Emar = new EmarArchiver(Emscripten);

            Log.Information("emcc version ... {Version} at {Path}", Emscripten.EmccVersion, Emscripten.EmccPath);
        }

        // --- Probing ---

        private static string? ProbeEmsdkRoot()
        {
            // 1. EMSDK env var directly points at the root
            var emsdk = Environment.GetEnvironmentVariable("EMSDK");
            if (!string.IsNullOrWhiteSpace(emsdk) && Directory.Exists(emsdk))
                return Path.GetFullPath(emsdk);

            // 2. EM_CONFIG points at a python-style config whose EMSCRIPTEN_ROOT
            //    lives under <emsdk>/upstream/emscripten
            var emConfig = Environment.GetEnvironmentVariable("EM_CONFIG");
            if (!string.IsNullOrWhiteSpace(emConfig) && File.Exists(emConfig))
            {
                var root = ParseEmscriptenRootFromConfig(emConfig);
                if (!string.IsNullOrEmpty(root))
                {
                    // root is <emsdk>/upstream/emscripten — go up two levels
                    var upstream = Directory.GetParent(root)?.FullName;
                    var emsdkFromConfig = upstream is null ? null : Directory.GetParent(upstream)?.FullName;
                    if (!string.IsNullOrEmpty(emsdkFromConfig) && Directory.Exists(emsdkFromConfig))
                        return emsdkFromConfig;
                }
            }

            // 3. emcc on PATH
            var emccOnPath = FindOnPath(BS.HostOS == OSPlatform.Windows ? "emcc.bat" : "emcc");
            if (!string.IsNullOrEmpty(emccOnPath))
            {
                // emcc lives at <emsdk>/upstream/emscripten/emcc(.bat)
                var emscriptenDir = Directory.GetParent(emccOnPath)?.FullName;
                var upstream = emscriptenDir is null ? null : Directory.GetParent(emscriptenDir)?.FullName;
                var emsdkFromPath = upstream is null ? null : Directory.GetParent(upstream)?.FullName;
                if (!string.IsNullOrEmpty(emsdkFromPath) && Directory.Exists(emsdkFromPath))
                    return emsdkFromPath;
            }

            return null;
        }

        private static string? ParseEmscriptenRootFromConfig(string configPath)
        {
            try
            {
                var text = File.ReadAllText(configPath);
                // Lines look like: EMSCRIPTEN_ROOT = '/path/to/upstream/emscripten'
                var match = Regex.Match(text, @"EMSCRIPTEN_ROOT\s*=\s*['""]([^'""]+)['""]");
                if (match.Success)
                {
                    var raw = match.Groups[1].Value;
                    if (Directory.Exists(raw))
                        return raw;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Failed to parse EM_CONFIG at {Path}: {Message}", configPath, ex.Message);
            }
            return null;
        }

        private static string? FindOnPath(string fileName)
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathEnv))
                return null;
            char sep = BS.HostOS == OSPlatform.Windows ? ';' : ':';
            foreach (var dir in pathEnv.Split(sep))
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;
                try
                {
                    var candidate = Path.Combine(dir, fileName);
                    if (File.Exists(candidate))
                        return Path.GetFullPath(candidate);
                }
                catch { /* invalid path char, skip */ }
            }
            return null;
        }

        private static string RequireFile(string path)
        {
            if (!File.Exists(path))
                throw new Exception($"Emscripten tool missing: {path}");
            return path;
        }

        private void ProbeEmccVersion()
        {
            int exitCode = BS.RunProcess(Emscripten.EmccPath!, "--version", out var output, out var error);
            if (exitCode != 0)
                throw new Exception($"`emcc --version` failed (exit {exitCode}). stderr: {error}");

            // "emcc (Emscripten ...) 5.0.5 (hash)"
            var m = Regex.Match(output, @"(\d+\.\d+\.\d+)");
            if (!m.Success)
                throw new Exception($"Failed to parse emcc version from output: {output}");

            Emscripten.EmccVersion = Version.Parse(m.Groups[1].Value);
            if (Emscripten.EmccVersion < MinimumEmscriptenVersion)
                throw new Exception($"emcc {Emscripten.EmccVersion} is too old; minimum supported version is {MinimumEmscriptenVersion}.");
        }
    }
}
