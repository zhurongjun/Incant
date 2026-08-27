using System.Diagnostics;
using System.Runtime.CompilerServices;
using Serilog;

namespace SB
{
    public interface ISetup
    {
        public abstract void Setup(BuildInstance instance);
    }

    public partial class BuildInstance
    {
        private readonly Dictionary<Type, ISetup> _setups = new();
        private readonly Dictionary<Type, SetupRegistrationInfo> _setupRegistrations = new();
        private readonly List<ISetup> _setupOrder = new();
        private readonly HashSet<Type> _completedSetups = new();
        public IReadOnlyList<ISetup> Setups => _setupOrder;
        public IReadOnlySet<Type> CompletedSetupTypes => _completedSetups;
        public IReadOnlyDictionary<Type, SetupRegistrationInfo> SetupRegistrations => _setupRegistrations;

        public void RunSetups()
        {
            using var trace = BuildTrace.Scope("BuildInstance.RunSetups");
            using (Profiler.BeginZone("RunSetups", color: (uint)Profiler.ColorType.WebMaroon))
            {
                var pendingSetups = _setupOrder
                    .Where(setup => !_completedSetups.Contains(setup.GetType()))
                    .ToArray();

                if (pendingSetups.Length == 0)
                {
                    BuildTrace.Mark("BuildInstance.RunSetups.pending", "count=0");
                    return;
                }

                BuildTrace.Mark("BuildInstance.RunSetups.pending", $"count={pendingSetups.Length}");

                Log.Verbose("Starting setups with {ProcessorCount} threads ...", Environment.ProcessorCount);
                Parallel.ForEach(pendingSetups,
                    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                    setup =>
                    {
                        using var setupTrace = BuildTrace.Scope($"Setup.{setup.GetType().Name}");
                        using (Profiler.BeginZone($"{setup.GetType().Name}", color: (uint)Profiler.ColorType.WebMaroon))
                        {
                            Stopwatch sw = new();
                            sw.Start();
                            Log.Verbose("Setup {Name} starts ...", setup.GetType().Name);
                            try
                            {
                                setup.Setup(this);
                                lock (_completedSetups)
                                {
                                    _completedSetups.Add(setup.GetType());
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Fatal("Setup {Name} failed: {Message}", setup.GetType().Name, ex.Message);
                                throw;
                            }
                            sw.Stop();
                            float Seconds = sw.ElapsedMilliseconds / 1000.0f;
                            Log.Verbose("Setup {Name} finished... cost {Seconds}s", setup.GetType().Name, Seconds);
                        }
                    });
            }
        }

        public T AddSetup<T>(
            [CallerFilePath] string? location = null,
            [CallerLineNumber] int lineNumber = 0)
            where T : ISetup, new()
        {
            return (T)AddSetup(new T(), SetupRegistrationInfo.FromCallSite(location, lineNumber));
        }

        public T? GetSetup<T>()
            where T : class, ISetup
        {
            return _setups.TryGetValue(typeof(T), out var setup) ? (T)setup : null;
        }

        public SetupRegistrationInfo? GetSetupRegistration(ISetup setup) =>
            _setupRegistrations.TryGetValue(setup.GetType(), out var registration) ? registration : null;

        private ISetup AddSetup(ISetup setup, SetupRegistrationInfo registration)
        {
            var setupType = setup.GetType();
            if (_setups.TryGetValue(setupType, out var existed))
                return existed;

            _setups.Add(setupType, setup);
            _setupRegistrations.Add(setupType, registration);
            _setupOrder.Add(setup);
            return setup;
        }
    }

    public sealed record SetupRegistrationInfo(
        string? Location,
        int LineNumber)
    {
        public static SetupRegistrationInfo FromCallSite(string? location, int lineNumber) =>
            new(location, lineNumber);
    }
}
