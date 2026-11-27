/*
 * OpenSim-next Game Project service.
 *
 * This service owns creator authorization and manifest validation. It only
 * persists project definitions; it never creates or mutates live Regions.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using log4net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Services.Base;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.UserAccountService
{
    public sealed class GameProjectService : ServiceBase, IGameProjectService, IGameProjectRuntimeDefinitionService
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private const int MaximumPageSize = 100;
        private readonly IGameProjectData m_Database;
        public GameExperienceService ExperienceService { get; }
        public GameProjectV2Service ProjectV2Service { get; }

        public GameProjectService(IConfigSource config) : base(config)
        {
            string dllName = string.Empty;
            string connectionString = string.Empty;

            IConfig databaseConfig = config.Configs["DatabaseService"];
            if (databaseConfig != null)
            {
                dllName = databaseConfig.GetString("StorageProvider", string.Empty);
                connectionString = databaseConfig.GetString("ConnectionString", string.Empty);
            }

            IConfig projectConfig = config.Configs["GameProjectService"];
            if (projectConfig == null)
                throw new Exception("No GameProjectService configuration");

            dllName = projectConfig.GetString("StorageProvider", dllName);
            connectionString = projectConfig.GetString("ConnectionString", connectionString);

            if (string.IsNullOrWhiteSpace(dllName))
                throw new Exception("No GameProjectService StorageProvider configured");
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new Exception("No GameProjectService ConnectionString configured");

            m_Database = LoadPlugin<IGameProjectData>(dllName, new object[] { connectionString });
            if (m_Database == null)
                throw new Exception("Could not load an IGameProjectData provider");
            ExperienceService = new GameExperienceService(m_Database);
            ProjectV2Service = new GameProjectV2Service(m_Database);
        }

        // Injection constructor for provider-level tests and embedded hosts.
        public GameProjectService(IGameProjectData database) : base(null)
        {
            m_Database = database ?? throw new ArgumentNullException(nameof(database));
            ExperienceService = new GameExperienceService(m_Database);
            ProjectV2Service = new GameProjectV2Service(m_Database);
        }

        public GameProjectResult CreateProject(UUID authenticatedOwnerID, string name, string description)
        {
            if (authenticatedOwnerID == UUID.Zero)
                return Failure(GameProjectError.Forbidden, "An authenticated creator is required.");

            if (!IsValidText(name, 1, 80))
                return Failure(GameProjectError.InvalidData, "Project name must contain between 1 and 80 characters.");

            if (!IsValidText(description ?? string.Empty, 0, 2000))
                return Failure(GameProjectError.InvalidData, "Project description cannot exceed 2000 characters.");

            DateTime now = DateTime.UtcNow;
            GameProjectData data = new GameProjectData
            {
                ProjectID = UUID.Random(),
                OwnerID = authenticatedOwnerID,
                Name = name.Trim(),
                Description = (description ?? string.Empty).Trim(),
                Status = (int)GameProjectStatus.Draft,
                SchemaVersion = GameProjectManifestValidator.CurrentSchemaVersion,
                CurrentRevision = 0,
                CreatedUTC = now,
                UpdatedUTC = now
            };

            try
            {
                if (!m_Database.CreateProject(data))
                {
                    Console.Error.WriteLine(
                        "[GAME PROJECT DIAGNOSTIC] provider={0}; project={1}; owner={2}; projectZero={3}; ownerZero={4}; returned=false",
                        m_Database.GetType().AssemblyQualifiedName, data.ProjectID, data.OwnerID,
                        data.ProjectID == UUID.Zero, data.OwnerID == UUID.Zero);
                    m_log.ErrorFormat(
                        "[GAME PROJECT]: Storage provider {0} rejected project insert for {1}.",
                        m_Database.GetType().FullName, data.ProjectID);
                    return Failure(GameProjectError.StorageUnavailable, "The project could not be created.");
                }

                return Success(ToProject(data));
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    "[GAME PROJECT DIAGNOSTIC] provider={0}; project={1}; exception={2}",
                    m_Database.GetType().AssemblyQualifiedName, data.ProjectID, exception);
                m_log.ErrorFormat(
                    "[GAME PROJECT]: Storage provider {0} failed to create project {1}.",
                    m_Database.GetType().FullName, data.ProjectID, exception);
                return Failure(GameProjectError.StorageUnavailable, "Project storage is unavailable.");
            }
        }

        public GameProjectResult GetProject(UUID authenticatedOwnerID, UUID projectID)
        {
            if (authenticatedOwnerID == UUID.Zero)
                return Failure(GameProjectError.Forbidden, "An authenticated creator is required.");
            if (projectID == UUID.Zero)
                return Failure(GameProjectError.InvalidData, "Project ID is required.");

            try
            {
                GameProjectData data = m_Database.GetProject(projectID);
                if (data == null || data.OwnerID != authenticatedOwnerID)
                    return Failure(GameProjectError.NotFound, "Project was not found.");
                return Success(ToProject(data));
            }
            catch
            {
                return Failure(GameProjectError.StorageUnavailable, "Project storage is unavailable.");
            }
        }

        public List<GameProject> GetOwnedProjects(UUID authenticatedOwnerID, int offset, int limit)
        {
            if (authenticatedOwnerID == UUID.Zero)
                return new List<GameProject>();

            if (offset < 0) offset = 0;
            if (limit < 1) limit = 1;
            if (limit > MaximumPageSize) limit = MaximumPageSize;

            List<GameProject> result = new List<GameProject>();
            try
            {
                List<GameProjectData> records = m_Database.GetProjectsByOwner(authenticatedOwnerID, offset, limit);
                if (records == null) return result;

                foreach (GameProjectData record in records)
                {
                    if (record != null && record.OwnerID == authenticatedOwnerID)
                        result.Add(ToProject(record));
                }
            }
            catch
            {
                // Listing has no typed error return in the current interface.
                // Return an empty page; callers should log provider failures.
            }
            return result;
        }

        public GameProjectResult SaveDraft(
            UUID authenticatedOwnerID,
            UUID projectID,
            long expectedCurrentRevision,
            GameProjectManifest manifest)
        {
            if (authenticatedOwnerID == UUID.Zero)
                return Failure(GameProjectError.Forbidden, "An authenticated creator is required.");
            if (projectID == UUID.Zero || expectedCurrentRevision < 0)
                return Failure(GameProjectError.InvalidData, "Project ID or expected revision is invalid.");

            GameProjectValidationResult validation = GameProjectManifestValidator.Validate(manifest);
            if (!validation.IsValid)
                return Failure(GameProjectError.InvalidData, string.Join(" ", validation.Errors));

            string json;
            try
            {
                JsonSerializerOptions options = new JsonSerializerOptions();
                options.Converters.Add(new OpenMetaverseUuidJsonConverter());
                json = JsonSerializer.Serialize(manifest, options);
            }
            catch
            {
                return Failure(GameProjectError.InvalidData, "Manifest could not be serialized.");
            }

            if (json.Length > GameProjectManifestValidator.MaximumManifestCharacters)
                return Failure(GameProjectError.InvalidData, "Manifest exceeds the maximum serialized size.");

            try
            {
                GameProjectData current = m_Database.GetProject(projectID);
                if (current == null)
                    return Failure(GameProjectError.NotFound, "Project was not found.");
                if (current.OwnerID != authenticatedOwnerID)
                    return Failure(GameProjectError.NotFound, "Project was not found.");
                if (current.Status != (int)GameProjectStatus.Draft)
                    return Failure(GameProjectError.Forbidden, "Only draft projects can be changed.");
                if (current.CurrentRevision != expectedCurrentRevision)
                    return Failure(GameProjectError.RevisionConflict, "Project revision has changed. Reload before saving.");

                GameProjectRevisionData revision = new GameProjectRevisionData
                {
                    ProjectID = projectID,
                    Revision = expectedCurrentRevision + 1,
                    SchemaVersion = manifest.SchemaVersion,
                    ManifestJson = json,
                    CreatedBy = authenticatedOwnerID,
                    CreatedUTC = DateTime.UtcNow
                };

                if (!m_Database.SaveRevision(revision, authenticatedOwnerID,
                    expectedCurrentRevision, DateTime.UtcNow))
                    return Failure(GameProjectError.RevisionConflict, "Project revision has changed. Reload before saving.");

                GameProjectData updated = m_Database.GetProject(projectID);
                return updated == null
                    ? Failure(GameProjectError.StorageUnavailable, "Saved project could not be reloaded.")
                    : Success(ToProject(updated));
            }
            catch
            {
                return Failure(GameProjectError.StorageUnavailable, "Project storage is unavailable.");
            }
        }

        public GameProjectRevision GetRevision(UUID authenticatedOwnerID, UUID projectID, long revision)
        {
            if (authenticatedOwnerID == UUID.Zero || projectID == UUID.Zero || revision < 1)
                return null;

            try
            {
                GameProjectData project = m_Database.GetProject(projectID);
                if (project == null || project.OwnerID != authenticatedOwnerID)
                    return null;

                GameProjectRevisionData data = m_Database.GetRevision(projectID, revision);
                if (data == null || data.CreatedBy != authenticatedOwnerID)
                    return null;

                return new GameProjectRevision
                {
                    ProjectID = data.ProjectID,
                    Revision = data.Revision,
                    SchemaVersion = data.SchemaVersion,
                    ManifestJson = data.ManifestJson,
                    CreatedBy = data.CreatedBy,
                    CreatedUTC = data.CreatedUTC
                };
            }
            catch
            {
                return null;
            }
        }

        public GameProjectResult UpdateMetadata(
            UUID authenticatedOwnerID,
            UUID projectID,
            long expectedCurrentRevision,
            string name,
            string description)
        {
            if (authenticatedOwnerID == UUID.Zero)
                return Failure(GameProjectError.Forbidden, "An authenticated creator is required.");
            if (projectID == UUID.Zero || expectedCurrentRevision < 0)
                return Failure(GameProjectError.InvalidData, "Project ID or expected revision is invalid.");
            if (!IsValidText(name, 1, 80) || !IsValidText(description ?? string.Empty, 0, 2000))
                return Failure(GameProjectError.InvalidData, "Project name or description is invalid.");

            try
            {
                GameProjectData current = m_Database.GetProject(projectID);
                if (current == null || current.OwnerID != authenticatedOwnerID)
                    return Failure(GameProjectError.NotFound, "Project was not found.");
                if (current.Status != (int)GameProjectStatus.Draft)
                    return Failure(GameProjectError.Forbidden, "Only draft projects can be changed.");
                if (current.CurrentRevision != expectedCurrentRevision)
                    return Failure(GameProjectError.RevisionConflict, "Project revision has changed.");

                if (!m_Database.UpdateProjectMetadata(projectID, authenticatedOwnerID,
                    expectedCurrentRevision, current.UpdatedUTC, name.Trim(), (description ?? string.Empty).Trim(), DateTime.UtcNow))
                    return Failure(GameProjectError.RevisionConflict, "Project revision has changed.");

                return GetProject(authenticatedOwnerID, projectID);
            }
            catch
            {
                return Failure(GameProjectError.StorageUnavailable, "Project storage is unavailable.");
            }
        }

        public GameProjectResult PublishProject(
            UUID authenticatedOwnerID,
            UUID projectID,
            long expectedCurrentRevision)
        {
            if (authenticatedOwnerID == UUID.Zero)
                return Failure(GameProjectError.Forbidden, "An authenticated creator is required.");
            if (projectID == UUID.Zero || expectedCurrentRevision < 1)
                return Failure(GameProjectError.InvalidData, "A project with at least one saved revision is required.");

            try
            {
                GameProjectData current = m_Database.GetProject(projectID);
                if (current == null || current.OwnerID != authenticatedOwnerID)
                    return Failure(GameProjectError.NotFound, "Project was not found.");
                if (current.Status != (int)GameProjectStatus.Draft)
                    return Failure(GameProjectError.RevisionConflict, "Only draft projects can be published; reload the project state.");
                if (current.SchemaVersion != GameProjectManifestValidator.CurrentSchemaVersion
                    && current.SchemaVersion != GameProjectV2Service.SchemaVersion)
                    return Failure(GameProjectError.InvalidData, "The project schema version is not supported.");
                if (current.CurrentRevision != expectedCurrentRevision)
                    return Failure(GameProjectError.RevisionConflict, "Project revision has changed. Reload before publishing.");

                GameProjectRevisionData revision = m_Database.GetRevision(projectID, current.CurrentRevision);
                if (revision == null || revision.ProjectID != projectID
                    || revision.Revision != current.CurrentRevision
                    || revision.CreatedBy != authenticatedOwnerID
                    || revision.SchemaVersion != current.SchemaVersion
                    || string.IsNullOrEmpty(revision.ManifestJson)
                    || revision.ManifestJson.Length > GameProjectManifestValidator.MaximumManifestCharacters)
                    return Failure(GameProjectError.InvalidData, "The current immutable revision is missing or invalid.");

                if (current.SchemaVersion == GameProjectV2Service.SchemaVersion)
                {
                    GameProjectV2Service v2 = new GameProjectV2Service(m_Database);
                    if (!v2.ValidateRevisionGraph(authenticatedOwnerID, projectID, revision))
                        return Failure(GameProjectError.InvalidData,
                            "The current v2 revision has an invalid Experience/Place graph.");
                }
                else
                {
                    GameProjectManifest manifest;
                    try
                    {
                        JsonSerializerOptions options = new JsonSerializerOptions();
                        options.Converters.Add(new OpenMetaverseUuidJsonConverter());
                        manifest = JsonSerializer.Deserialize<GameProjectManifest>(revision.ManifestJson, options);
                    }
                    catch
                    {
                        return Failure(GameProjectError.InvalidData, "The current revision contains invalid manifest data.");
                    }

                    GameProjectValidationResult validation = GameProjectManifestValidator.Validate(manifest);
                    if (!validation.IsValid)
                        return Failure(GameProjectError.InvalidData,
                            "The current revision failed publication validation: " + string.Join(" ", validation.Errors));
                }

                if (!m_Database.PublishProject(projectID, authenticatedOwnerID,
                    expectedCurrentRevision, current.UpdatedUTC, DateTime.UtcNow))
                    return Failure(GameProjectError.RevisionConflict,
                        "The project changed or is no longer eligible for publication.");

                GameProjectData published = m_Database.GetProject(projectID);
                if (published == null || published.OwnerID != authenticatedOwnerID
                    || published.Status != (int)GameProjectStatus.Published
                    || published.CurrentRevision != expectedCurrentRevision)
                    return Failure(GameProjectError.StorageUnavailable, "Published project could not be reloaded.");

                return Success(ToProject(published));
            }
            catch
            {
                return Failure(GameProjectError.StorageUnavailable, "Project storage is unavailable.");
            }
        }

        /// <summary>
        /// Resolves the v2 runtime start world only from the immutable graph pinned
        /// by the requested project revision. The initial runtime contract deliberately
        /// requires exactly one Experience until an explicit Experience selector exists.
        /// </summary>
        public GameWorldDefinition ResolveRuntimeWorld(UUID authenticatedOwnerID, UUID projectID, long projectRevision)
        {
            if (authenticatedOwnerID == UUID.Zero || projectID == UUID.Zero || projectRevision < 1)
                return null;

            try
            {
                GameProjectData project = m_Database.GetProject(projectID);
                if (project == null || project.OwnerID != authenticatedOwnerID
                    || project.Status == (int)GameProjectStatus.Archived)
                    return null;

                GameProjectRevisionData revision = m_Database.GetRevision(projectID, projectRevision);
                if (revision == null || revision.ProjectID != projectID
                    || revision.Revision != projectRevision || revision.CreatedBy != authenticatedOwnerID
                    || revision.SchemaVersion != GameProjectV2Service.SchemaVersion
                    || revision.Experiences == null || revision.Experiences.Count != 1)
                    return null;

                GameProjectV2Service v2 = new GameProjectV2Service(m_Database);
                if (!v2.ValidateRevisionGraph(authenticatedOwnerID, projectID, revision))
                    return null;

                GameProjectExperienceRevisionData experiencePin = revision.Experiences[0];
                GameExperienceRevisionData experience = m_Database.GetExperienceRevision(
                    projectID, experiencePin.ExperienceID, experiencePin.ExperienceRevision);
                if (experience == null || experience.CreatedBy != authenticatedOwnerID
                    || experience.Places == null || experience.Places.Count == 0)
                    return null;

                GameExperiencePlaceRevisionData startPin = experience.Places.Find(
                    x => x != null && x.PlaceID == experience.StartPlaceID);
                if (startPin == null)
                    return null;

                GamePlaceRevisionData placeRevision = m_Database.GetPlaceRevision(
                    projectID, experiencePin.ExperienceID, startPin.PlaceID, startPin.PlaceRevision);
                if (placeRevision == null || placeRevision.CreatedBy != authenticatedOwnerID
                    || placeRevision.SchemaVersion != 1
                    || string.IsNullOrWhiteSpace(placeRevision.DefinitionJson)
                    || placeRevision.DefinitionJson.Length > GameProjectManifestValidator.MaximumManifestCharacters)
                    return null;

                JsonSerializerOptions options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                options.Converters.Add(new OpenMetaverseUuidJsonConverter());
                GameWorldDefinition world = JsonSerializer.Deserialize<GameWorldDefinition>(
                    placeRevision.DefinitionJson, options);
                if (world == null || world.WorldID != startPin.PlaceID)
                    return null;

                GameProjectManifest validationManifest = new GameProjectManifest
                {
                    SchemaVersion = GameProjectManifestValidator.CurrentSchemaVersion,
                    Name = experience.Name,
                    Worlds = new List<GameWorldDefinition> { world },
                    Settings = new GameSettings
                    {
                        DefaultWorldID = world.WorldID,
                        MaxPlayers = 16,
                        Visibility = "private"
                    }
                };
                return GameProjectManifestValidator.Validate(validationManifest).IsValid ? world : null;
            }
            catch
            {
                return null;
            }
        }

        public bool ArchiveProject(UUID authenticatedOwnerID, UUID projectID)
        {
            if (authenticatedOwnerID == UUID.Zero || projectID == UUID.Zero)
                return false;

            try
            {
                return m_Database.ArchiveProject(projectID, authenticatedOwnerID, DateTime.UtcNow);
            }
            catch
            {
                return false;
            }
        }

        private sealed class OpenMetaverseUuidJsonConverter : JsonConverter<UUID>
        {
            public override UUID Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType != JsonTokenType.String)
                    throw new JsonException("UUID values must be strings.");
                string value = reader.GetString();
                UUID parsed;
                if (!UUID.TryParse(value, out parsed))
                    throw new JsonException("Invalid UUID value.");
                return parsed;
            }

            public override void Write(Utf8JsonWriter writer, UUID value, JsonSerializerOptions options)
            {
                writer.WriteStringValue(value.ToString());
            }
        }

        private static bool IsValidText(string value, int minimum, int maximum)
        {
            return value != null && value.Trim().Length >= minimum && value.Trim().Length <= maximum;
        }

        private static GameProject ToProject(GameProjectData data)
        {
            return new GameProject
            {
                ProjectID = data.ProjectID,
                OwnerID = data.OwnerID,
                Name = data.Name,
                Description = data.Description,
                Status = (GameProjectStatus)data.Status,
                SchemaVersion = data.SchemaVersion,
                CurrentRevision = data.CurrentRevision,
                CreatedUTC = data.CreatedUTC,
                UpdatedUTC = data.UpdatedUTC
            };
        }

        private static GameProjectResult Success(GameProject project)
        {
            return new GameProjectResult { Error = GameProjectError.None, Project = project };
        }

        private static GameProjectResult Failure(GameProjectError error, string message)
        {
            return new GameProjectResult { Error = error, Message = message };
        }
    }
}
