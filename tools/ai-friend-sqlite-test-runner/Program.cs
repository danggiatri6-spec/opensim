using System.Reflection;
using System.Runtime.Loader;

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

object New(string typeName, Assembly assembly) =>
    Activator.CreateInstance(assembly.GetType(typeName, true)!)!;
void Set(object target, string property, object? value) =>
    target.GetType().GetProperty(property)!.SetValue(target, value);
object? Get(object target, string property) =>
    target.GetType().GetProperty(property)!.GetValue(target);
object? Call(object target, string method, params object?[] args) =>
    target.GetType().GetMethod(method)!.Invoke(target, args);
void Assert(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL " + message);
    Console.WriteLine("PASS " + message);
}

var dataAssembly = Load("OpenSim.Data");
var sqliteAssembly = Load("OpenSim.Data.SQLite");
var providerType = sqliteAssembly.GetType("OpenSim.Data.SQLite.SQLiteAiFriendData", true)!;
var tempDb = Path.Combine(Path.GetTempPath(), "opensim-ai-friend-" + Guid.NewGuid().ToString("N") + ".db");
var connection = "URI=file:" + tempDb.Replace("\\", "/") + ";UseUTF16Encoding=True";

try
{
    var userA = Guid.NewGuid();
    var userB = Guid.NewGuid();
    var characterId = Guid.NewGuid();
    var relationshipAId = Guid.NewGuid();
    var relationshipBId = Guid.NewGuid();
    var memoryAId = Guid.NewGuid();
    var memoryBId = Guid.NewGuid();
    var created = DateTime.UtcNow.AddDays(-3);
    var now = DateTime.UtcNow;

    var provider = Activator.CreateInstance(providerType, new object[] { connection })!;

    var character = New("OpenSim.Data.AiFriendCharacterData", dataAssembly);
    Set(character, "CharacterId", characterId);
    Set(character, "DisplayName", "Integration Friend");
    Set(character, "ProfileDescription", "SQLite persistence test");
    Set(character, "PersonaVersion", 1);
    Set(character, "Status", 1); // Active
    Set(character, "CreatedUtc", created);
    Set(character, "UpdatedUtc", now);
    Assert((bool)Call(provider, "SaveCharacter", character)!, "SQLite creates an active AI character");

    object Relationship(Guid relationshipId, Guid userId) 
    {
        var value = New("OpenSim.Data.AiFriendRelationshipData", dataAssembly);
        Set(value, "RelationshipId", relationshipId);
        Set(value, "UserId", userId);
        Set(value, "CharacterId", characterId);
        Set(value, "State", 0); // Met
        Set(value, "RelationshipLevel", 0);
        Set(value, "FirstMetUtc", now);
        Set(value, "AllowProactiveGreeting", true);
        Set(value, "AllowFollow", true);
        Set(value, "AllowVoice", true);
        Set(value, "Version", 1L);
        return value;
    }

    var relationshipA = Relationship(relationshipAId, userA);
    var relationshipB = Relationship(relationshipBId, userB);
    Assert((bool)Call(provider, "CreateRelationship", relationshipA, userA)!, "SQLite creates user A relationship");
    Assert((bool)Call(provider, "CreateRelationship", relationshipB, userB)!, "same AI character can have isolated user B relationship");
    Assert(!(bool)Call(provider, "CreateRelationship", relationshipA, userA)!, "duplicate user-character relationship is rejected");

    object Memory(Guid memoryId, Guid relationshipId, string text)
    {
        var value = New("OpenSim.Data.AiFriendMemoryData", dataAssembly);
        Set(value, "MemoryId", memoryId);
        Set(value, "RelationshipId", relationshipId);
        Set(value, "MemoryType", 0);
        Set(value, "Content", text);
        Set(value, "Confidence", 0.95);
        Set(value, "SourceConversationId", Guid.NewGuid());
        Set(value, "CreatedUtc", DateTime.UtcNow);
        return value;
    }

    var memoryA = Memory(memoryAId, relationshipAId, "User A likes classical music");
    var memoryB = Memory(memoryBId, relationshipBId, "User B likes hiking");
    Assert((bool)Call(provider, "AddMemory", memoryA, userA)!, "user A can add memory to own relationship");
    Assert((bool)Call(provider, "AddMemory", memoryB, userB)!, "user B can add memory to own relationship");
    Assert(!(bool)Call(provider, "AddMemory", Memory(Guid.NewGuid(), relationshipAId, "forbidden"), userB)!, "user B cannot add memory to user A relationship");

    var aMemories = (System.Collections.IList)Call(provider, "GetMemories", userA, relationshipAId, 20)!;
    var bMemories = (System.Collections.IList)Call(provider, "GetMemories", userB, relationshipBId, 20)!;
    var crossRead = (System.Collections.IList)Call(provider, "GetMemories", userB, relationshipAId, 20)!;
    Assert(aMemories.Count == 1 && bMemories.Count == 1, "each user reads only own relationship memories");
    Assert(crossRead.Count == 0, "user B cannot read user A memories");
    Assert(!(bool)Call(provider, "DeleteMemory", userB, relationshipAId, memoryAId)!, "user B cannot delete user A memory");

    var loadedA = Call(provider, "GetRelationship", userA, characterId)!;
    Set(loadedA, "Version", 2L);
    Set(loadedA, "RelationshipLevel", 5);
    Assert((bool)Call(provider, "UpdateRelationship", loadedA, userA, 1L)!, "relationship compare-and-swap update succeeds");
    Assert(!(bool)Call(provider, "UpdateRelationship", loadedA, userA, 1L)!, "stale relationship version is rejected");

    Assert((bool)Call(provider, "DeleteMemory", userA, relationshipAId, memoryAId)!, "owner can soft-delete own memory");
    Assert(((System.Collections.IList)Call(provider, "GetMemories", userA, relationshipAId, 20)!).Count == 0,
        "soft-deleted memory is excluded from reads");

    // Re-open the provider against the same database to verify durable state.
    var reopened = Activator.CreateInstance(providerType, new object[] { connection })!;
    var persistedCharacter = Call(reopened, "GetCharacter", characterId)!;
    Assert((string)Get(persistedCharacter, "DisplayName")! == "Integration Friend", "character survives provider restart");
    Assert((DateTime)Get(persistedCharacter, "CreatedUtc")! == created, "character creation timestamp is preserved");
    Assert((int)Get(persistedCharacter, "Status")! == 1, "active status survives provider restart");

    var renamed = New("OpenSim.Data.AiFriendCharacterData", dataAssembly);
    Set(renamed, "CharacterId", characterId);
    Set(renamed, "DisplayName", "Renamed Friend");
    Set(renamed, "ProfileDescription", "Updated profile");
    Set(renamed, "PersonaVersion", 2);
    Set(renamed, "Status", 1);
    Set(renamed, "CreatedUtc", created);
    Set(renamed, "UpdatedUtc", DateTime.UtcNow);
    Assert((bool)Call(reopened, "SaveCharacter", renamed)!, "existing character updates without replace semantics");
    Assert((string)Get(Call(reopened, "GetCharacter", characterId)!, "DisplayName")! == "Renamed Friend",
        "updated character profile is readable");
    Assert((DateTime)Get(Call(reopened, "GetCharacter", characterId)!, "CreatedUtc")! == created,
        "updating character does not reset CreatedUTC");
    Assert(Call(reopened, "GetRelationship", userA, characterId) != null,
        "updating character preserves existing friend relationships");
    Assert(((System.Collections.IList)Call(reopened, "GetMemories", userB, relationshipBId, 20)!).Count == 1,
        "user B memory remains isolated and persistent after provider restart");
}
finally
{
    foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" })
        try { File.Delete(tempDb + suffix); } catch { }
}

Console.WriteLine("All AI Friend SQLite integration tests passed.");
