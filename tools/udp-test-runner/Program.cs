using System.Reflection;
using System.Runtime.Loader;
using OpenSim.Region.ClientStack.LindenUDP.Tests;

// OpenSim's generated assemblies live in the repository bin directory.
// Resolve their transitive dependencies there without copying the whole bin
// directory into this runner's output (which may contain stale/missing DLLs).
var binDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../bin"));
AssemblyLoadContext.Default.Resolving += (_, assemblyName) =>
{
    var candidate = Path.Combine(binDirectory, assemblyName.Name + ".dll");
    return File.Exists(candidate)
        ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate)
        : null;
};

var tests = new PacketHandlerTests();
Run(nameof(tests.FirstLocalPacketHandlerRegistrationSucceeds),
    tests.FirstLocalPacketHandlerRegistrationSucceeds);
Run(nameof(tests.DuplicateLocalPacketHandlerRegistrationIsRejected),
    tests.DuplicateLocalPacketHandlerRegistrationIsRejected);
Run(nameof(tests.LocalPacketHandlerDefaultsToAsynchronous),
    tests.LocalPacketHandlerDefaultsToAsynchronous);
Run(nameof(tests.LocalPacketHandlerCanBeRegisteredAsSynchronous),
    tests.LocalPacketHandlerCanBeRegisteredAsSynchronous);

Console.WriteLine("All 4 focused UDP packet-handler tests passed.");

static void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAIL {name}");
        Console.Error.WriteLine(ex);
        throw;
    }
}
