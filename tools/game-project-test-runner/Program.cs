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

var adapterType = contracts.GetType("OpenSim.Services.Interfaces.GameProjectExperienceAdapter", true)!;
var experienceId = U("77777777-7777-4777-8777-777777777777");
var experience = adapterType.GetMethod("FromLegacyManifest")!.Invoke(null,
    new object?[] { manifest, experienceId, "Default Experience" })!;
Assert(Equals(Get(experience, "ExperienceID"), experienceId),
    "legacy manifest adapter preserves the supplied Experience ID");
Assert(Equals(Get(experience, "StartPlaceID"), worldId),
    "legacy default world maps to the Experience start Place");
var places = (IList)Get(experience, "Places")!;
Assert(places.Count == 1 && Equals(Get(places[0]!, "PlaceID"), worldId),
    "each legacy world maps to a stable Place ID without changing its ID");
Assert(((IList)Get(places[0]!, "Worlds")!).Count == 1,
    "legacy Place retains its declarative world definition");

var experienceValidator = contracts.GetType("OpenSim.Services.Interfaces.GameExperienceValidator", true)!;
var validExperience = experienceValidator.GetMethod("Validate")!.Invoke(null, new[] { experience })!;
Assert((bool)Get(validExperience, "IsValid")!,
    "Experience with unique Places and a valid start Place is accepted");
var invalidStartExperience = New(experience.GetType().FullName!, contracts);
Set(invalidStartExperience, "ExperienceID", experienceId);
Set(invalidStartExperience, "Name", "Invalid start");
Set(invalidStartExperience, "StartPlaceID", U("88888888-8888-4888-8888-888888888888"));
((IList)Get(invalidStartExperience, "Places")!).Add(places[0]);
var invalidStart = experienceValidator.GetMethod("Validate")!.Invoke(null, new[] { invalidStartExperience })!;
Assert(!(bool)Get(invalidStart, "IsValid")!,
    "Experience rejects a StartPlaceID that is not part of the Experience");
var duplicatePlaceExperience = New(experience.GetType().FullName!, contracts);
Set(duplicatePlaceExperience, "ExperienceID", experienceId);
Set(duplicatePlaceExperience, "Name", "Duplicate Place");
Set(duplicatePlaceExperience, "StartPlaceID", worldId);
((IList)Get(duplicatePlaceExperience, "Places")!).Add(places[0]);
((IList)Get(duplicatePlaceExperience, "Places")!).Add(places[0]);
var duplicatePlace = experienceValidator.GetMethod("Validate")!.Invoke(null, new[] { duplicatePlaceExperience })!;
Assert(!(bool)Get(duplicatePlace, "IsValid")!,
    "Experience rejects duplicate Place IDs");

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
((IList)Get(world, "SpawnPoints")!).Clear();

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
    Set(revision, "ManifestJson", "{\"SchemaVersion\":1,\"Name\":\"Integration Test\",\"Worlds\":[{\"WorldID\":\"11111111-1111-4111-8111-111111111111\",\"Name\":\"Main World\",\"SizeX\":256,\"SizeY\":256,\"SpawnPoints\":[],\"AssetReferences\":[]}],\"Settings\":{\"GameMode\":\"sandbox\",\"DefaultWorldID\":\"11111111-1111-4111-8111-111111111111\",\"Visibility\":\"private\",\"MaxPlayers\":16,\"Tags\":[]}}");
    Set(revision, "CreatedBy", owner);
    Set(revision, "CreatedUTC", DateTime.UtcNow);
    Assert((bool)Call(provider, "SaveRevision", revision, owner, 0L, DateTime.UtcNow)!, "first revision saves atomically");
    Assert(!(bool)Call(provider, "SaveRevision", revision, owner, 0L, DateTime.UtcNow)!, "stale revision write is rejected");
    Assert(Get(Call(provider, "GetProject", projectId)!, "CurrentRevision")!.Equals(1L), "revision counter advances exactly once");
    Assert(((string)Get(Call(provider, "GetRevision", projectId, 1L)!, "ManifestJson")!).Contains("Integration Test"), "immutable revision can be read back");
    var experienceId2 = U("77777777-7777-4777-8777-777777777777");
    var placeId2 = U("11111111-1111-4111-8111-111111111111");
    var experienceData = New("OpenSim.Data.GameExperienceData", dataAssembly);
    Set(experienceData, "ProjectID", projectId); Set(experienceData, "ExperienceID", experienceId2);
    Set(experienceData, "CreatedUTC", DateTime.UtcNow); Set(experienceData, "UpdatedUTC", DateTime.UtcNow);
    Assert((bool)Call(provider, "CreateExperience", experienceData, owner)!, "SQLite creates an Experience for its project owner");
    Assert(!(bool)Call(provider, "CreateExperience", experienceData, otherOwner)!, "SQLite rejects Experience creation by a different owner");
    var placeData = New("OpenSim.Data.GamePlaceData", dataAssembly);
    Set(placeData, "ProjectID", projectId); Set(placeData, "ExperienceID", experienceId2); Set(placeData, "PlaceID", placeId2);
    Set(placeData, "CreatedUTC", DateTime.UtcNow); Set(placeData, "UpdatedUTC", DateTime.UtcNow);
    Assert((bool)Call(provider, "CreatePlace", placeData, owner)!, "SQLite creates a Place within its Experience");
    Assert(!(bool)Call(provider, "CreatePlace", placeData, otherOwner)!, "SQLite rejects Place creation by a different owner");
    var placeRevision = New("OpenSim.Data.GamePlaceRevisionData", dataAssembly);
    Set(placeRevision, "ProjectID", projectId); Set(placeRevision, "ExperienceID", experienceId2); Set(placeRevision, "PlaceID", placeId2);
    Set(placeRevision, "Revision", 1L); Set(placeRevision, "SchemaVersion", 1); Set(placeRevision, "Name", "Main Place");
    Set(placeRevision, "Description", "Test scene"); Set(placeRevision, "DefinitionJson", "{\"SchemaVersion\":1}");
    Set(placeRevision, "CreatedBy", owner); Set(placeRevision, "CreatedUTC", DateTime.UtcNow);
    Assert((bool)Call(provider, "SavePlaceRevision", placeRevision, owner, 0L, DateTime.UtcNow)!, "Place revision saves with compare-and-swap");
    Assert(!(bool)Call(provider, "SavePlaceRevision", placeRevision, owner, 0L, DateTime.UtcNow)!, "stale Place revision write is rejected");
    Assert(!(bool)Call(provider, "SavePlaceRevision", placeRevision, otherOwner, 1L, DateTime.UtcNow)!, "Place revision write rejects a different owner");
    var experienceRevision = New("OpenSim.Data.GameExperienceRevisionData", dataAssembly);
    Set(experienceRevision, "ProjectID", projectId); Set(experienceRevision, "ExperienceID", experienceId2);
    Set(experienceRevision, "Revision", 1L); Set(experienceRevision, "SchemaVersion", 1); Set(experienceRevision, "Name", "Default Experience");
    Set(experienceRevision, "StartPlaceID", placeId2); Set(experienceRevision, "CreatedBy", owner); Set(experienceRevision, "CreatedUTC", DateTime.UtcNow);
    var membershipType = dataAssembly.GetType("OpenSim.Data.GameExperiencePlaceRevisionData", true)!;
    var membership = Activator.CreateInstance(membershipType)!; Set(membership, "PlaceID", placeId2); Set(membership, "PlaceRevision", 1L); Set(membership, "SortOrder", 0);
    ((IList)Get(experienceRevision, "Places")!).Add(membership);
    Assert((bool)Call(provider, "SaveExperienceRevision", experienceRevision, owner, 0L, DateTime.UtcNow)!, "Experience revision atomically pins an immutable Place revision");
    Assert(!(bool)Call(provider, "SaveExperienceRevision", experienceRevision, owner, 0L, DateTime.UtcNow)!, "stale Experience revision write is rejected");
    Assert(!(bool)Call(provider, "SaveExperienceRevision", experienceRevision, otherOwner, 1L, DateTime.UtcNow)!, "Experience revision write rejects a different owner");
    var storedExperienceRevision = Call(provider, "GetExperienceRevision", projectId, experienceId2, 1L)!;
    Assert(((IList)Get(storedExperienceRevision, "Places")!).Count == 1, "Experience revision returns its pinned Place revision membership");
    Assert(Equals(Get(Call(provider, "GetPlace", projectId, experienceId2, placeId2)!, "CurrentRevision"), 1L), "Place current revision pointer advances after commit");
    Assert(Equals(Get(Call(provider, "GetExperience", projectId, experienceId2)!, "CurrentRevision"), 1L), "Experience current revision pointer advances after commit");
    var invalidExperienceRevision = New("OpenSim.Data.GameExperienceRevisionData", dataAssembly);
    Set(invalidExperienceRevision, "ProjectID", projectId); Set(invalidExperienceRevision, "ExperienceID", experienceId2);
    Set(invalidExperienceRevision, "Revision", 2L); Set(invalidExperienceRevision, "SchemaVersion", 1); Set(invalidExperienceRevision, "Name", "Bad start");
    Set(invalidExperienceRevision, "StartPlaceID", U("88888888-8888-4888-8888-888888888888")); Set(invalidExperienceRevision, "CreatedBy", owner); Set(invalidExperienceRevision, "CreatedUTC", DateTime.UtcNow);
    ((IList)Get(invalidExperienceRevision, "Places")!).Add(membership);
    Assert(!(bool)Call(provider, "SaveExperienceRevision", invalidExperienceRevision, owner, 1L, DateTime.UtcNow)!, "Experience rejects a start Place absent from its revision membership");
    Assert(!(bool)Call(provider, "SavePlaceRevision", placeRevision, owner, 1L, DateTime.UtcNow)!, "immutable Place revision cannot be inserted twice");


    loaded = Call(provider, "GetProject", projectId)!;
    var updatedAt = (DateTime)Get(loaded, "UpdatedUTC")!;
    Assert((bool)Call(provider, "UpdateProjectMetadata", projectId, owner, 1L, updatedAt, "Renamed", "Updated", DateTime.UtcNow)!, "metadata compare-and-swap succeeds");
    Assert(!(bool)Call(provider, "UpdateProjectMetadata", projectId, owner, 1L, updatedAt, "Stale", "", DateTime.UtcNow)!, "stale metadata compare-and-swap is rejected");
    Assert(!(bool)Call(provider, "UpdateProjectMetadata", projectId, otherOwner, 1L, (DateTime)Get(Call(provider, "GetProject", projectId)!, "UpdatedUTC")!, "Intruder", "", DateTime.UtcNow)!, "metadata update rejects a different owner");

    loaded = Call(provider, "GetProject", projectId)!;
    updatedAt = (DateTime)Get(loaded, "UpdatedUTC")!;
    Assert(!(bool)Call(provider, "PublishProject", projectId, otherOwner, 1L, updatedAt, DateTime.UtcNow)!,
        "SQLite rejects publishing by a different owner");
    Assert(!(bool)Call(provider, "PublishProject", projectId, owner, 0L, updatedAt, DateTime.UtcNow)!,
        "SQLite rejects a stale publish revision");

    // A schema-v2 project revision must pin exact immutable Experience revisions.
    var graphProjectId = U("99999999-9999-4999-8999-999999999999");
    var graphExperienceId = U("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    var graphPlaceId = U("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    var graphProject = New("OpenSim.Data.GameProjectData", dataAssembly);
    Set(graphProject, "ProjectID", graphProjectId); Set(graphProject, "OwnerID", owner);
    Set(graphProject, "Name", "Pinned Graph"); Set(graphProject, "Description", "");
    Set(graphProject, "Status", 0); Set(graphProject, "SchemaVersion", 1);
    Set(graphProject, "CurrentRevision", 0L); Set(graphProject, "CreatedUTC", DateTime.UtcNow);
    Set(graphProject, "UpdatedUTC", DateTime.UtcNow);
    Assert((bool)Call(provider, "CreateProject", graphProject)!, "SQLite creates project for v2 revision graph");

    var graphExperience = New("OpenSim.Data.GameExperienceData", dataAssembly);
    Set(graphExperience, "ProjectID", graphProjectId); Set(graphExperience, "ExperienceID", graphExperienceId);
    Set(graphExperience, "CreatedUTC", DateTime.UtcNow); Set(graphExperience, "UpdatedUTC", DateTime.UtcNow);
    Assert((bool)Call(provider, "CreateExperience", graphExperience, owner)!, "v2 graph Experience is created");

    var graphPlace = New("OpenSim.Data.GamePlaceData", dataAssembly);
    Set(graphPlace, "ProjectID", graphProjectId); Set(graphPlace, "ExperienceID", graphExperienceId);
    Set(graphPlace, "PlaceID", graphPlaceId); Set(graphPlace, "CreatedUTC", DateTime.UtcNow);
    Set(graphPlace, "UpdatedUTC", DateTime.UtcNow);
    Assert((bool)Call(provider, "CreatePlace", graphPlace, owner)!, "v2 graph Place is created");

    var graphPlaceRevision = New("OpenSim.Data.GamePlaceRevisionData", dataAssembly);
    Set(graphPlaceRevision, "ProjectID", graphProjectId); Set(graphPlaceRevision, "ExperienceID", graphExperienceId);
    Set(graphPlaceRevision, "PlaceID", graphPlaceId); Set(graphPlaceRevision, "Revision", 1L);
    Set(graphPlaceRevision, "SchemaVersion", 1); Set(graphPlaceRevision, "Name", "Pinned Place");
    Set(graphPlaceRevision, "Description", ""); Set(graphPlaceRevision, "DefinitionJson", "{\"SchemaVersion\":1}");
    Set(graphPlaceRevision, "CreatedBy", owner); Set(graphPlaceRevision, "CreatedUTC", DateTime.UtcNow);
    Assert((bool)Call(provider, "SavePlaceRevision", graphPlaceRevision, owner, 0L, DateTime.UtcNow)!, "v2 graph Place revision saves");

    var graphExperienceRevision = New("OpenSim.Data.GameExperienceRevisionData", dataAssembly);
    Set(graphExperienceRevision, "ProjectID", graphProjectId); Set(graphExperienceRevision, "ExperienceID", graphExperienceId);
    Set(graphExperienceRevision, "Revision", 1L); Set(graphExperienceRevision, "SchemaVersion", 1);
    Set(graphExperienceRevision, "Name", "Pinned Experience"); Set(graphExperienceRevision, "StartPlaceID", graphPlaceId);
    Set(graphExperienceRevision, "CreatedBy", owner); Set(graphExperienceRevision, "CreatedUTC", DateTime.UtcNow);
    // Use a membership object scoped to this graph's Place ID.
    var graphMembership = Activator.CreateInstance(membershipType)!;
    Set(graphMembership, "PlaceID", graphPlaceId); Set(graphMembership, "PlaceRevision", 1L); Set(graphMembership, "SortOrder", 0);
    ((IList)Get(graphExperienceRevision, "Places")!).Add(graphMembership);
    Assert((bool)Call(provider, "SaveExperienceRevision", graphExperienceRevision, owner, 0L, DateTime.UtcNow)!, "v2 graph Experience revision pins its Place revision");

    var graphRevision = New("OpenSim.Data.GameProjectRevisionData", dataAssembly);
    Set(graphRevision, "ProjectID", graphProjectId); Set(graphRevision, "Revision", 1L);
    Set(graphRevision, "SchemaVersion", 2); Set(graphRevision, "ManifestJson", "{\"SchemaVersion\":2}");
    Set(graphRevision, "CreatedBy", owner); Set(graphRevision, "CreatedUTC", DateTime.UtcNow);
    var projectExperienceType = dataAssembly.GetType("OpenSim.Data.GameProjectExperienceRevisionData", true)!;
    var projectExperienceRef = Activator.CreateInstance(projectExperienceType)!;
    Set(projectExperienceRef, "ExperienceID", graphExperienceId);
    Set(projectExperienceRef, "ExperienceRevision", 1L); Set(projectExperienceRef, "SortOrder", 0);
    ((IList)Get(graphRevision, "Experiences")!).Add(projectExperienceRef);
    Assert((bool)Call(provider, "SaveRevision", graphRevision, owner, 0L, DateTime.UtcNow)!, "v2 project revision atomically pins Experience revision");
    var graphRevisionRead = Call(provider, "GetRevision", graphProjectId, 1L)!;
    var graphRefs = (IList)Get(graphRevisionRead, "Experiences")!;
    Assert(graphRefs.Count == 1 && Equals(Get(graphRefs[0]!, "ExperienceRevision"), 1L),
        "v2 project revision reads back its exact Experience revision pin");
    Assert((bool)Call(provider, "PublishProject", graphProjectId, owner, 1L,
        (DateTime)Get(Call(provider, "GetProject", graphProjectId)!, "UpdatedUTC")!, DateTime.UtcNow)!,
        "v2 project publishes only after its immutable Experience graph is pinned");

    var missingGraphProjectId = U("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    var missingGraphProject = New("OpenSim.Data.GameProjectData", dataAssembly);
    Set(missingGraphProject, "ProjectID", missingGraphProjectId); Set(missingGraphProject, "OwnerID", owner);
    Set(missingGraphProject, "Name", "Missing Graph"); Set(missingGraphProject, "Description", "");
    Set(missingGraphProject, "Status", 0); Set(missingGraphProject, "SchemaVersion", 1);
    Set(missingGraphProject, "CurrentRevision", 0L); Set(missingGraphProject, "CreatedUTC", DateTime.UtcNow);
    Set(missingGraphProject, "UpdatedUTC", DateTime.UtcNow);
    Assert((bool)Call(provider, "CreateProject", missingGraphProject)!, "SQLite creates project for missing graph rejection");
    var missingGraphRevision = New("OpenSim.Data.GameProjectRevisionData", dataAssembly);
    Set(missingGraphRevision, "ProjectID", missingGraphProjectId); Set(missingGraphRevision, "Revision", 1L);
    Set(missingGraphRevision, "SchemaVersion", 2); Set(missingGraphRevision, "ManifestJson", "{\"SchemaVersion\":2}");
    Set(missingGraphRevision, "CreatedBy", owner); Set(missingGraphRevision, "CreatedUTC", DateTime.UtcNow);
    Assert(!(bool)Call(provider, "SaveRevision", missingGraphRevision, owner, 0L, DateTime.UtcNow)!,
        "v2 project revision without Experience pins is rejected");
    var foreignExperienceRef = Activator.CreateInstance(projectExperienceType)!;
    Set(foreignExperienceRef, "ExperienceID", graphExperienceId);
    Set(foreignExperienceRef, "ExperienceRevision", 1L); Set(foreignExperienceRef, "SortOrder", 0);
    ((IList)Get(missingGraphRevision, "Experiences")!).Add(foreignExperienceRef);
    Assert(!(bool)Call(provider, "SaveRevision", missingGraphRevision, owner, 0L, DateTime.UtcNow)!,
        "v2 project revision cannot pin an Experience owned by another project");
    Assert(Equals(Get(Call(provider, "GetProject", missingGraphProjectId)!, "CurrentRevision"), 0L)
        && Call(provider, "GetRevision", missingGraphProjectId, 1L) == null,
        "failed Experience pin rolls back the entire project revision transaction");

    var servicesAssembly = Load("OpenSim.Services.UserAccountService");
    var projectServiceType = servicesAssembly.GetType("OpenSim.Services.UserAccountService.GameProjectService", true)!;
    var projectService = Activator.CreateInstance(projectServiceType, new[] { provider })!;
    // Service-layer tests exercise owner scoping, revision validation and exact Place pins.
    var serviceProjectId = U("dddddddd-dddd-4ddd-8ddd-dddddddddddd");
    var serviceProject = New("OpenSim.Data.GameProjectData", dataAssembly);
    Set(serviceProject, "ProjectID", serviceProjectId); Set(serviceProject, "OwnerID", owner);
    Set(serviceProject, "Name", "Experience Service Test"); Set(serviceProject, "Description", "");
    Set(serviceProject, "Status", 0); Set(serviceProject, "SchemaVersion", 1);
    Set(serviceProject, "CurrentRevision", 0L); Set(serviceProject, "CreatedUTC", DateTime.UtcNow);
    Set(serviceProject, "UpdatedUTC", DateTime.UtcNow);
    Assert((bool)Call(provider, "CreateProject", serviceProject)!, "SQLite creates a draft for Experience service tests");

    var experienceServiceType = servicesAssembly.GetType("OpenSim.Services.UserAccountService.GameExperienceService", true)!;
    var experienceService = Activator.CreateInstance(experienceServiceType, new[] { provider })!;
    var createdExperience = Call(experienceService, "CreateExperience", owner, serviceProjectId)!;
    Assert(createdExperience != null, "GameExperienceService creates an owner-scoped Experience identity");
    var serviceExperienceId = Get(createdExperience, "ExperienceID")!;
    var createdPlace = Call(experienceService, "CreatePlace", owner, serviceProjectId, serviceExperienceId)!;
    Assert(createdPlace != null, "GameExperienceService creates a Place inside its Experience");
    var servicePlaceId = Get(createdPlace, "PlaceID")!;
    Assert((bool)Call(experienceService, "SavePlaceRevision", owner, serviceProjectId, serviceExperienceId,
        servicePlaceId, 0L, 1, "Service Place", "Entry", @"{""format"":1}")!,
        "GameExperienceService saves a validated immutable Place revision");
    Assert(!(bool)Call(experienceService, "SavePlaceRevision", owner, serviceProjectId, serviceExperienceId,
        servicePlaceId, 0L, 1, "Stale", "", @"{""format"":1}")!,
        "GameExperienceService rejects a stale Place revision compare-and-swap");
    Assert(!(bool)Call(experienceService, "SavePlaceRevision", otherOwner, serviceProjectId, serviceExperienceId,
        servicePlaceId, 1L, 1, "Foreign", "", @"{""format"":1}")!,
        "GameExperienceService rejects Place writes by a different owner");

    var pinType = contracts.GetType("OpenSim.Services.Interfaces.GameExperiencePlacePin", true)!;
    var pinListType = typeof(List<>).MakeGenericType(pinType);
    var servicePins = (IList)Activator.CreateInstance(pinListType)!;
    var servicePin = Activator.CreateInstance(pinType)!;
    Set(servicePin, "PlaceID", servicePlaceId); Set(servicePin, "PlaceRevision", 1L); Set(servicePin, "SortOrder", 0);
    servicePins.Add(servicePin);
    Assert((bool)Call(experienceService, "SaveExperienceRevision", owner, serviceProjectId, serviceExperienceId,
        0L, 1, "Service Experience", servicePlaceId, servicePins)!,
        "GameExperienceService saves an Experience revision pinned to the exact Place revision");
    var serviceExperienceRevision = Call(experienceService, "GetExperienceRevision", owner,
        serviceProjectId, serviceExperienceId, 1L)!;
    Assert(serviceExperienceRevision != null && ((IList)Get(serviceExperienceRevision, "Places")!).Count == 1,
        "GameExperienceService reads back the immutable Experience-to-Place pin");
    Assert(Call(experienceService, "GetExperienceRevision", otherOwner, serviceProjectId,
        serviceExperienceId, 1L) == null,
        "GameExperienceService does not disclose Experience revisions to another owner");

    var projectV2ServiceType = servicesAssembly.GetType("OpenSim.Services.UserAccountService.GameProjectV2Service", true)!;
    var projectV2Service = Activator.CreateInstance(projectV2ServiceType, new[] { provider })!;
    var projectExperiencePinType = dataAssembly.GetType("OpenSim.Data.GameProjectExperienceRevisionData", true)!;
    var projectExperiencePinListType = typeof(List<>).MakeGenericType(projectExperiencePinType);
    var projectExperiencePins = (IList)Activator.CreateInstance(projectExperiencePinListType)!;
    var projectExperiencePin = Activator.CreateInstance(projectExperiencePinType)!;
    Set(projectExperiencePin, "ExperienceID", serviceExperienceId);
    Set(projectExperiencePin, "ExperienceRevision", 1L);
    Set(projectExperiencePin, "SortOrder", 0);
    projectExperiencePins.Add(projectExperiencePin);
    Assert((bool)Call(projectV2Service, "SaveDraftRevision", owner, serviceProjectId, 0L,
        "{\\"schemaVersion\\":2}", projectExperiencePins)!,
        "GameProjectV2Service saves a v2 project revision with an exact Experience revision pin");
    var savedV2Project = Call(provider, "GetProject", serviceProjectId)!;
    Assert((long)Get(savedV2Project, "CurrentRevision")! == 1L
        && (int)Get(savedV2Project, "SchemaVersion")! == 2,
        "v2 revision advances project revision and schema version atomically");
    var savedV2Revision = Call(provider, "GetRevision", serviceProjectId, 1L)!;
    var savedV2Pins = (IList)Get(savedV2Revision, "Experiences")!;
    Assert(savedV2Pins.Count == 1
        && Equals(Get(savedV2Pins[0]!, "ExperienceID"), serviceExperienceId)
        && Equals(Get(savedV2Pins[0]!, "ExperienceRevision"), 1L),
        "SQLite reads back the exact immutable Experience pin from the v2 project revision");
    Assert(!(bool)Call(projectV2Service, "SaveDraftRevision", otherOwner, serviceProjectId, 1L,
        "{\\"schemaVersion\\":2}", projectExperiencePins)!,
        "GameProjectV2Service rejects a project revision write by a different owner");
    Assert(!(bool)Call(projectV2Service, "SaveDraftRevision", owner, serviceProjectId, 0L,
        "{\\"schemaVersion\\":2}", projectExperiencePins)!,
        "GameProjectV2Service rejects a stale project revision compare-and-swap");

    var publishResult = Call(projectService, "PublishProject", owner, projectId, 1L)!;
    Assert((bool)Get(publishResult, "Success")!, "GameProjectService validates and publishes a valid immutable manifest; error=" + Get(publishResult, "Error") + "; message=" + Get(publishResult, "Message"));
    var foreignPublish = Call(projectService, "PublishProject", otherOwner, projectId, 1L)!;
    Assert(Get(foreignPublish, "Error")!.ToString() == "NotFound",
        "GameProjectService does not disclose projects to a different owner");

    var invalidProjectId = U("55555555-5555-4555-8555-555555555555");
    var invalidProject = New("OpenSim.Data.GameProjectData", dataAssembly);
    Set(invalidProject, "ProjectID", invalidProjectId);
    Set(invalidProject, "OwnerID", owner);
    Set(invalidProject, "Name", "Invalid Manifest");
    Set(invalidProject, "Description", "");
    Set(invalidProject, "Status", 0);
    Set(invalidProject, "SchemaVersion", 1);
    Set(invalidProject, "CurrentRevision", 0L);
    Set(invalidProject, "CreatedUTC", DateTime.UtcNow);
    Set(invalidProject, "UpdatedUTC", DateTime.UtcNow);
    Assert((bool)Call(provider, "CreateProject", invalidProject)!, "SQLite creates a second draft for validation rejection");
    var invalidRevision = New("OpenSim.Data.GameProjectRevisionData", dataAssembly);
    Set(invalidRevision, "ProjectID", invalidProjectId);
    Set(invalidRevision, "Revision", 1L);
    Set(invalidRevision, "SchemaVersion", 1);
    Set(invalidRevision, "ManifestJson", "{malformed");
    Set(invalidRevision, "CreatedBy", owner);
    Set(invalidRevision, "CreatedUTC", DateTime.UtcNow);
    Assert((bool)Call(provider, "SaveRevision", invalidRevision, owner, 0L, DateTime.UtcNow)!,
        "SQLite can persist a malformed historical fixture for service validation testing");
    var invalidPublish = Call(projectService, "PublishProject", owner, invalidProjectId, 1L)!;
    Assert(Get(invalidPublish, "Error")!.ToString() == "InvalidData",
        "GameProjectService refuses to publish malformed manifest JSON");
    Assert((int)Get(Call(provider, "GetProject", invalidProjectId)!, "Status")! == 0,
        "failed manifest validation leaves the project in Draft state");

    var noRevisionId = U("66666666-6666-4666-8666-666666666666");
    var noRevisionProject = New("OpenSim.Data.GameProjectData", dataAssembly);
    Set(noRevisionProject, "ProjectID", noRevisionId);
    Set(noRevisionProject, "OwnerID", owner);
    Set(noRevisionProject, "Name", "No Revision");
    Set(noRevisionProject, "Description", "");
    Set(noRevisionProject, "Status", 0);
    Set(noRevisionProject, "SchemaVersion", 1);
    Set(noRevisionProject, "CurrentRevision", 0L);
    Set(noRevisionProject, "CreatedUTC", DateTime.UtcNow);
    Set(noRevisionProject, "UpdatedUTC", DateTime.UtcNow);
    Assert((bool)Call(provider, "CreateProject", noRevisionProject)!, "SQLite creates a draft with no revisions");
    var noRevisionPublish = Call(projectService, "PublishProject", owner, noRevisionId, 0L)!;
    Assert(Get(noRevisionPublish, "Error")!.ToString() == "InvalidData",
        "GameProjectService refuses to publish a project with no saved revision");

    var published = Call(provider, "GetProject", projectId)!;
    Assert((int)Get(published, "Status")! == 1, "published status is persisted");
    updatedAt = (DateTime)Get(published, "UpdatedUTC")!;
    Assert(!(bool)Call(provider, "UpdateProjectMetadata", projectId, owner, 1L, updatedAt,
        "Published mutation", "", DateTime.UtcNow)!,
        "SQLite prevents metadata mutation after publication");
    Assert(!(bool)Call(provider, "PublishProject", projectId, owner, 1L, updatedAt, DateTime.UtcNow)!,
        "repeated publish is rejected because only Draft can transition");
    Assert(!(bool)Call(provider, "SaveRevision", revision, owner, 1L, DateTime.UtcNow)!,
        "published project rejects new revisions");
    Assert((bool)Call(provider, "ArchiveProject", projectId, owner, DateTime.UtcNow)!,
        "owner can archive a published project");
    Assert(!(bool)Call(provider, "SaveRevision", revision, owner, 1L, DateTime.UtcNow)!,
        "archived project rejects new revisions");
}
finally
{
    foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" })
        try { File.Delete(tempDb + suffix); } catch { }
}

Console.WriteLine("All Game Platform validation and SQLite integration tests passed.");
