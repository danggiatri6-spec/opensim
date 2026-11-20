using OpenSim.Region.ClientStack.LindenUDP.Tests;

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
