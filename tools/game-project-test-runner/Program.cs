using System.Reflection;
using System.Runtime.Loader;
using System.Collections;
using System.Globalization;

var rootBin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../bin"));
if (!Directory.Exists(rootBin))
    throw new DirectoryNotFoundException(rootBin);

AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var path = Path.Combine(rootBin, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};

Assembly Load(string name)
{
    var path = Path.Combine(rootBin, name + ".dll");
    if (!File.Exists(path)) throw new FileNotFoundException(name, path);
    return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
}

var contracts = Load("OpenSim.Services.Interfaces");
var dataAssembly = Load("OpenSim.Data");
var sqliteAssembly = Load("OpenSim.Data.SQLite");
var uuidType = Load("OpenMetaverseTypes").GetType("OpenMetaverse.UUID", true)!;
object U(string value) => uuidType.GetMethod("Parse", new[] { typeof(string) })!.Invoke(null, new object[] { value })!;
object New(string typeName, Assembly assembly) => Activator.CreateInstance(assembly.GetType(typeName, true)!)!;
void Set(object target, string property, object? value) => target.GetType().GetProperty(property)!.SetValue(target, value);
object? Get(object target, string property) => target.GetType().GetProperty(property)!.GetValue(target);
object? Call(object target, string method, params object?[] args) =>
    target.GetType().GetMethod(method)!.Invoke(target, args);
void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}

var validatorType = contracts.GetType("OpenSim.Services.Interfaces.GameProjectManifestValidator", true)!;
var manifestType = contracts.GetType("OpenSim.Services.Interfaces.GameProjectManifest", true)!;
var worldType = contracts.GetType("OpenSim.Services.Interfaces.GameWorldDefinition", true)!;
var manifest = New(manifestType.FullName!, contracts);
var world = New(worldType.FullName!, contracts);
var worldId = U("11111111-1111-4111-8111-111111111111");
Set(manifest, "Name", "Integration Test");
Set(world, "WorldID", worldId);
Set(world, "Name", "Main World");
((IList)Get(manifest, "Worlds")!).Add(world);
Set(Get(manifest, "Settings")!, "DefaultWorldID", worldId);
var valid = validatorType.GetMethod("Validate")!.Invoke(null, new[] { manifest })!;
Assert((bool)Get(valid, "IsValid")!, "valid manifest is accepted");

var invalidManifest = New(manifestType.FullName!, contracts);
Set(invalidManifest, "Name", "Missing world");
var invalid = validatorType.GetMethod("Validate")!.Invoke(null, new[] { invalidManifest })!;
Assert(!(bool)Get(invalid, "IsValid")!, "manifest without a world is rejected");

var spawnType = contracts.GetType("OpenSim.Services.Interfaces.GameSpawnPoint", true)!;
var badSpawn = New(spawnType.FullName!, contracts);
Set(badSpawn, "RotationW", 2.0);
((IList)Get(world, "SpawnPoints")!).Add(badSpawn);
var badRotation = validatorType.GetMethod("Validate")!.Invoke(null, new[] { manifest })!;
Assert(!(bool)Get(badRotation, "IsValid")!, "non-normalized spawn rotation is rejected");

var tempDb = Path.Combine(Path.GetTempPath(), "opensim-game-project-" + Guid.NewGuid().ToString("N") + ".db");
try
{
    var providerType = sqliteAssembly.GetType("OpenSim.Data.SQLite.SQLiteGameProjectData", true)!;
    var connection = "URI=file:" + tempDb.Replace("\\", "/") + ";UseUTF16Encoding=True";
    var provider = Activator.CreateInstance(providerType, new object[] { connection })!;
    var owner = U("22222222-2222-4222-8222-222222222222");
    var otherOwner = U("33333333-3333-4333-8333-333333333333");
    var projectId = U("44444444-4444-4444-8444-444444444444");
    var now = DateTime.UtcNow;

    var project = New("OpenSim.Data.GameProjectData", dataAssembly);
    Set(project, "ProjectID", projectId);
    Set(project, "OwnerID", owner);
    Set(project, "Name", "Initial");
    Set(project, "Description", "");
    Set(project, "Status", 0);
    Set(project, "SchemaVersion", 1);
    Set(project, "CurrentRevision", 0L);
    Set(project, "CreatedUTC", now);
    Set(project, "UpdatedUTC", now);
    Assert((bool)Call(provider, "CreateProject", project)!, "SQLite creates a project");

    var loaded = Call(provider, "GetProject", projectId)!;
    Assert(Equals(Get(loaded, "OwnerID"), owner), "project owner is persisted");
    var ownerProjects = (IList)Call(provider, "GetProjectsByOwner", owner, 0, 20)!;
    var otherProjects = (IList)Call(provider, "GetProjectsByOwner", otherOwner, 0, 20)!;
    Assert(ownerProjects.Count == 1 && otherProjects.Count == 0, "owner-scoped listing isolates creators");

    var revision = New("OpenSim.Data.GameProjectRevisionData", dataAssembly);
    Set(revision, "ProjectID", projectId);
    Set(revision, "Revision", 1L);
    Set(revision, "SchemaVersion", 1);
    Set(revision, "ManifestJson", "{\"schemaVersion\":1}");
    Set(revision, "CreatedBy", owner);
    Set(revision, "CreatedUTC", DateTime.UtcNow);
    Assert((bool)Call(provider, "SaveRevision", revision, owner, 0L, DateTime.UtcNow)!, "first revision saves atomically");
    Assert(!(bool)Call(provider, "SaveRevision", revision, owner, 0L, DateTime.UtcNow)!, "stale revision write is rejected");
    Assert(Get(Call(provider, "GetProject", projectId)!, "CurrentRevision")!.Equals(1L), "revision counter advances exactly once");
    Assert(Get(Call(provider, "GetRevision", projectId, 1L)!, "ManifestJson")!.Equals("{\"schemaVersion\":1}"), "immutable revision can be read back");

    loaded = Call(provider, "GetProject", projectId)!;
    var updatedAt = (DateTime)Get(loaded, "UpdatedUTC")!;
    Assert((bool)Call(provider, "UpdateProjectMetadata", projectId, owner, 1L, updatedAt, "Renamed", "Updated", DateTime.UtcNow)!, "metadata compare-and-swap succeeds");
    Assert(!(bool)Call(provider, "UpdateProjectMetadata", projectId, owner, 1L, updatedAt, "Stale", "", DateTime.UtcNow)!, "stale metadata compare-and-swap is rejected");
    Assert(!(bool)Call(provider, "UpdateProjectMetadata", projectId, otherOwner, 1L, (DateTime)Get(Call(provider, "GetProject", projectId)!, "UpdatedUTC")!, "Intruder", "", DateTime.UtcNow)!, "metadata update rejects a different owner");

    Assert((bool)Call(provider, "ArchiveProject", projectId, owner, DateTime.UtcNow)!, "owner can archive project");
    Assert(!(bool)Call(provider, "SaveRevision", revision, owner, 1L, DateTime.UtcNow)!, "archived project rejects new revisions");
}
finally
{
    foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" })
        try { File.Delete(tempDb + suffix); } catch { }
}

Console.WriteLine("All Game Platform validation and SQLite integration tests passed.");
