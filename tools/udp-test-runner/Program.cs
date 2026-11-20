using System.Reflection;
using System.Runtime.Loader;

// OpenSim assemblies are generated into the repository bin directory.
// Load the test assembly by path and resolve its dependencies from that directory.
// Keeping the runner independent of OpenSim/NUnit compile-time references prevents
// MSBuild from trying to copy every transitive simulator dependency.
var binDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../bin"));
var testAssemblyPath = Path.Combine(binDirectory, "OpenSim.Region.ClientStack.LindenUDP.Tests.dll");

if (!File.Exists(testAssemblyPath))
    throw new FileNotFoundException("UDP test assembly was not built.", testAssemblyPath);

AssemblyLoadContext.Default.Resolving += (_, assemblyName) =>
{
    var candidate = Path.Combine(binDirectory, assemblyName.Name + ".dll");
    return File.Exists(candidate)
        ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate)
        : null;
};

var testAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(testAssemblyPath);
var testType = testAssembly.GetType(
    "OpenSim.Region.ClientStack.LindenUDP.Tests.PacketHandlerTests",
    throwOnError: true)!;
var instance = Activator.CreateInstance(testType)
    ?? throw new InvalidOperationException("Could not create PacketHandlerTests.");

var testNames = new[]
{
    "FirstLocalPacketHandlerRegistrationSucceeds",
    "DuplicateLocalPacketHandlerRegistrationIsRejected",
    "LocalPacketHandlerDefaultsToAsynchronous",
    "LocalPacketHandlerCanBeRegisteredAsSynchronous",
    "StaticPacketHandlerTakesPrecedenceOverLocalHandler",
    "UnknownPacketWithoutRegisteredHandlerReturnsFalse",
    "GenericPacketHandlerNormalizesMethodAndRejectsDuplicates"
};

foreach (var name in testNames)
{
    var method = testType.GetMethod(name, BindingFlags.Instance | BindingFlags.Public)
        ?? throw new MissingMethodException(testType.FullName, name);

    try
    {
        method.Invoke(instance, null);
        Console.WriteLine($"PASS {name}");
    }
    catch (TargetInvocationException ex) when (ex.InnerException is not null)
    {
        Console.Error.WriteLine($"FAIL {name}");
        Console.Error.WriteLine(ex.InnerException);
        throw ex.InnerException;
    }
}

Console.WriteLine($"All {testNames.Length} focused UDP packet-handler tests passed.");
