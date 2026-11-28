/*
 * OpenSim-next schema-v2 project revision service.
 *
 * A v2 project revision pins an immutable Experience/Place graph. The provider
 * commits the project revision, all Experience pins and the project schema
 * version in one SQLite transaction.
 */
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.UserAccountService
{
    public sealed class GameProjectV2Service
    {
        public const int SchemaVersion = 2;
        private readonly IGameProjectData m_Database;

        public GameProjectV2Service(IGameProjectData database)
        {
            m_Database = database ?? throw new ArgumentNullException(nameof(database));
        }

        /// <summary>
        /// Explicitly promotes the current immutable v1 project revision into a
        /// schema-v2 Experience/Place graph. Every intermediate identity and
        /// revision uses deterministic IDs/content so an interrupted or
        /// concurrent retry can safely resume without rewriting v1 history.
        /// </summary>
        public bool PromoteLegacyProject(UUID ownerID, UUID projectID, long expectedCurrentRevision)
        {
            if (ownerID == UUID.Zero || projectID == UUID.Zero || expectedCurrentRevision < 1)
                return false;

            try
            {
                GameProjectData project = m_Database.GetProject(projectID);
                if (project == null || project.OwnerID != ownerID
                    || project.Status != (int)GameProjectStatus.Draft)
                    return false;

                // An exact retry after a successful promotion is successful,
                // but never treats an unrelated newer revision as our result.
                if (project.CurrentRevision == expectedCurrentRevision + 1
                    && project.SchemaVersion == SchemaVersion)
                {
                    GameProjectRevisionData promoted = m_Database.GetRevision(projectID, project.CurrentRevision);
                    return IsExpectedPromotion(ownerID, projectID, expectedCurrentRevision, promoted);
                }

                if (project.SchemaVersion != GameProjectManifestValidator.CurrentSchemaVersion
                    || project.CurrentRevision != expectedCurrentRevision)
                    return false;

                GameProjectRevisionData legacyRevision =
                    m_Database.GetRevision(projectID, expectedCurrentRevision);
                if (legacyRevision == null || legacyRevision.CreatedBy != ownerID
                    || legacyRevision.SchemaVersion != GameProjectManifestValidator.CurrentSchemaVersion)
                    return false;

                JsonSerializerOptions jsonOptions = CreateJsonOptions();
                GameProjectManifest legacyManifest =
                    JsonSerializer.Deserialize<GameProjectManifest>(legacyRevision.ManifestJson, jsonOptions);
                if (!GameProjectManifestValidator.Validate(legacyManifest).IsValid)
                    return false;

                UUID experienceID = GameProjectExperienceAdapter.GetLegacyDefaultExperienceID(projectID);
                GameExperienceDefinition experience =
                    GameProjectExperienceAdapter.FromLegacyManifest(legacyManifest, experienceID);
                if (!GameExperienceValidator.Validate(experience).IsValid)
                    return false;

                DateTime now = DateTime.UtcNow;
                GameExperienceData experienceIdentity = m_Database.GetExperience(projectID, experienceID);
                if (experienceIdentity == null)
                {
                    m_Database.CreateExperience(new GameExperienceData
                    {
                        ProjectID = projectID, ExperienceID = experienceID,
                        CreatedUTC = now, UpdatedUTC = now
                    }, ownerID);
                    experienceIdentity = m_Database.GetExperience(projectID, experienceID);
                }
                if (experienceIdentity == null)
                    return false;

                List<GameExperiencePlacePin> placePins = new List<GameExperiencePlacePin>();
                for (int index = 0; index < legacyManifest.Worlds.Count; index++)
                {
                    GameWorldDefinition world = legacyManifest.Worlds[index];
                    UUID placeID = world.WorldID;
                    GamePlaceData placeIdentity = m_Database.GetPlace(projectID, experienceID, placeID);
                    if (placeIdentity == null)
                    {
                        m_Database.CreatePlace(new GamePlaceData
                        {
                            ProjectID = projectID, ExperienceID = experienceID, PlaceID = placeID,
                            CreatedUTC = now, UpdatedUTC = now
                        }, ownerID);
                        placeIdentity = m_Database.GetPlace(projectID, experienceID, placeID);
                    }
                    if (placeIdentity == null)
                        return false;

                    string definitionJson = JsonSerializer.Serialize(world, jsonOptions);
                    GamePlaceRevisionData placeRevision = m_Database.GetPlaceRevision(
                        projectID, experienceID, placeID, 1);
                    if (placeRevision == null && placeIdentity.CurrentRevision == 0)
                    {
                        var candidate = new GamePlaceRevisionData
                        {
                            ProjectID = projectID, ExperienceID = experienceID, PlaceID = placeID,
                            Revision = 1, SchemaVersion = 1, Name = world.Name,
                            Description = world.Description ?? string.Empty,
                            DefinitionJson = definitionJson, CreatedBy = ownerID, CreatedUTC = now
                        };
                        m_Database.SavePlaceRevision(candidate, ownerID, 0, now);
                        placeRevision = m_Database.GetPlaceRevision(projectID, experienceID, placeID, 1);
                        placeIdentity = m_Database.GetPlace(projectID, experienceID, placeID);
                    }
                    if (placeRevision == null || placeRevision.CreatedBy != ownerID
                        || placeRevision.SchemaVersion != 1 || placeRevision.Name != world.Name
                        || placeRevision.Description != (world.Description ?? string.Empty)
                        || placeRevision.DefinitionJson != definitionJson
                        || placeIdentity.CurrentRevision < 1)
                        return false;

                    placePins.Add(new GameExperiencePlacePin
                    {
                        PlaceID = placeID, PlaceRevision = 1, SortOrder = index
                    });
                }

                GameExperienceRevisionData experienceRevision =
                    m_Database.GetExperienceRevision(projectID, experienceID, 1);
                if (experienceRevision == null && experienceIdentity.CurrentRevision == 0)
                {
                    var candidate = new GameExperienceRevisionData
                    {
                        ProjectID = projectID, ExperienceID = experienceID, Revision = 1,
                        SchemaVersion = 1, Name = legacyManifest.Name,
                        StartPlaceID = legacyManifest.Settings.DefaultWorldID,
                        CreatedBy = ownerID, CreatedUTC = now
                    };
                    foreach (GameExperiencePlacePin pin in placePins)
                        candidate.Places.Add(new GameExperiencePlaceRevisionData
                        {
                            PlaceID = pin.PlaceID, PlaceRevision = pin.PlaceRevision,
                            SortOrder = pin.SortOrder
                        });
                    m_Database.SaveExperienceRevision(candidate, ownerID, 0, now);
                    experienceRevision = m_Database.GetExperienceRevision(projectID, experienceID, 1);
                    experienceIdentity = m_Database.GetExperience(projectID, experienceID);
                }
                if (!MatchesLegacyExperience(experienceRevision, ownerID, projectID,
                    experienceID, legacyManifest, placePins)
                    || experienceIdentity.CurrentRevision < 1)
                    return false;

                string v2ManifestJson = JsonSerializer.Serialize(new
                {
                    schemaVersion = SchemaVersion,
                    name = legacyManifest.Name,
                    legacySourceRevision = expectedCurrentRevision
                }, jsonOptions);
                var projectPins = new List<GameProjectExperienceRevisionData>
                {
                    new GameProjectExperienceRevisionData
                    {
                        ExperienceID = experienceID, ExperienceRevision = 1, SortOrder = 0
                    }
                };

                if (SaveDraftRevision(ownerID, projectID, expectedCurrentRevision,
                    v2ManifestJson, projectPins))
                    return true;

                // Another retry may have committed the exact same project
                // revision while this caller was preparing its graph.
                project = m_Database.GetProject(projectID);
                if (project == null || project.CurrentRevision != expectedCurrentRevision + 1
                    || project.SchemaVersion != SchemaVersion)
                    return false;
                return IsExpectedPromotion(ownerID, projectID, expectedCurrentRevision,
                    m_Database.GetRevision(projectID, project.CurrentRevision));
            }
            catch
            {
                return false;
            }
        }

        private bool IsExpectedPromotion(UUID ownerID, UUID projectID, long sourceRevision,
            GameProjectRevisionData revision)
        {
            if (revision == null || revision.SchemaVersion != SchemaVersion
                || revision.CreatedBy != ownerID || revision.Revision != sourceRevision + 1
                || revision.Experiences == null || revision.Experiences.Count != 1
                || revision.Experiences[0].ExperienceID
                    != GameProjectExperienceAdapter.GetLegacyDefaultExperienceID(projectID)
                || revision.Experiences[0].ExperienceRevision != 1
                || revision.Experiences[0].SortOrder != 0)
                return false;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(revision.ManifestJson);
                JsonElement source;
                return doc.RootElement.TryGetProperty("legacySourceRevision", out source)
                    && source.ValueKind == JsonValueKind.Number
                    && source.GetInt64() == sourceRevision
                    && ValidateRevisionGraph(ownerID, projectID, revision);
            }
            catch { return false; }
        }

        private static bool MatchesLegacyExperience(GameExperienceRevisionData revision,
            UUID ownerID, UUID projectID, UUID experienceID, GameProjectManifest manifest,
            IList<GameExperiencePlacePin> pins)
        {
            if (revision == null || revision.ProjectID != projectID || revision.ExperienceID != experienceID
                || revision.Revision != 1 || revision.CreatedBy != ownerID || revision.SchemaVersion != 1
                || revision.Name != manifest.Name || revision.StartPlaceID != manifest.Settings.DefaultWorldID
                || revision.Places == null || revision.Places.Count != pins.Count)
                return false;
            for (int i = 0; i < pins.Count; i++)
                if (revision.Places[i].PlaceID != pins[i].PlaceID
                    || revision.Places[i].PlaceRevision != pins[i].PlaceRevision
                    || revision.Places[i].SortOrder != pins[i].SortOrder)
                    return false;
            return true;
        }

        private static JsonSerializerOptions CreateJsonOptions()
        {
            var options = new JsonSerializerOptions();
            options.Converters.Add(new LegacyUuidJsonConverter());
            return options;
        }

        private sealed class LegacyUuidJsonConverter : JsonConverter<UUID>
        {
            public override UUID Read(ref Utf8JsonReader reader, Type typeToConvert,
                JsonSerializerOptions options)
            {
                if (reader.TokenType != JsonTokenType.String
                    || !UUID.TryParse(reader.GetString(), out UUID value))
                    throw new JsonException("UUID values must be valid strings.");
                return value;
            }

            public override void Write(Utf8JsonWriter writer, UUID value,
                JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
        }

        public bool ValidateRevisionGraph(UUID ownerID, UUID projectID, GameProjectRevisionData revision)
        {
            if (ownerID == UUID.Zero || projectID == UUID.Zero || revision == null
                || revision.ProjectID != projectID || revision.CreatedBy != ownerID
                || revision.SchemaVersion != SchemaVersion
                || string.IsNullOrWhiteSpace(revision.ManifestJson)
                || revision.ManifestJson.Length > GameProjectManifestValidator.MaximumManifestCharacters
                || revision.Experiences == null || revision.Experiences.Count == 0)
                return false;

            try
            {
                using (JsonDocument document = JsonDocument.Parse(revision.ManifestJson))
                {
                    JsonElement schema;
                    if (document.RootElement.ValueKind != JsonValueKind.Object
                        || (!document.RootElement.TryGetProperty("schemaVersion", out schema)
                            && !document.RootElement.TryGetProperty("SchemaVersion", out schema))
                        || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != SchemaVersion)
                        return false;
                }

                HashSet<UUID> experienceIDs = new HashSet<UUID>();
                HashSet<int> sortOrders = new HashSet<int>();
                foreach (GameProjectExperienceRevisionData pin in revision.Experiences)
                {
                    if (pin == null || pin.ExperienceID == UUID.Zero || pin.ExperienceRevision < 1
                        || pin.SortOrder < 0 || !experienceIDs.Add(pin.ExperienceID)
                        || !sortOrders.Add(pin.SortOrder))
                        return false;

                    GameExperienceData identity = m_Database.GetExperience(projectID, pin.ExperienceID);
                    GameExperienceRevisionData exact =
                        m_Database.GetExperienceRevision(projectID, pin.ExperienceID, pin.ExperienceRevision);
                    if (identity == null || exact == null || exact.CreatedBy != ownerID
                        || exact.ProjectID != projectID || exact.Revision != pin.ExperienceRevision
                        || exact.SchemaVersion != 1 || exact.Places == null || exact.Places.Count == 0
                        || exact.StartPlaceID == UUID.Zero || identity.CurrentRevision < pin.ExperienceRevision)
                        return false;

                    HashSet<UUID> placeIDs = new HashSet<UUID>();
                    HashSet<int> placeOrders = new HashSet<int>();
                    bool startFound = false;
                    foreach (GameExperiencePlaceRevisionData placePin in exact.Places)
                    {
                        if (placePin == null || placePin.PlaceID == UUID.Zero || placePin.PlaceRevision < 1
                            || placePin.SortOrder < 0 || !placeIDs.Add(placePin.PlaceID)
                            || !placeOrders.Add(placePin.SortOrder))
                            return false;
                        if (placePin.PlaceID == exact.StartPlaceID) startFound = true;

                        GamePlaceData place = m_Database.GetPlace(projectID, pin.ExperienceID, placePin.PlaceID);
                        GamePlaceRevisionData placeRevision = m_Database.GetPlaceRevision(
                            projectID, pin.ExperienceID, placePin.PlaceID, placePin.PlaceRevision);
                        if (place == null || placeRevision == null || placeRevision.CreatedBy != ownerID
                            || placeRevision.ProjectID != projectID || placeRevision.ExperienceID != pin.ExperienceID
                            || placeRevision.PlaceID != placePin.PlaceID || placeRevision.Revision != placePin.PlaceRevision
                            || placeRevision.SchemaVersion != 1 || place.CurrentRevision < placePin.PlaceRevision)
                            return false;
                        using (JsonDocument definition = JsonDocument.Parse(placeRevision.DefinitionJson))
                            if (definition.RootElement.ValueKind != JsonValueKind.Object) return false;
                    }
                    if (!startFound) return false;
                }
                return true;
            }
            catch { return false; }
        }

        public bool SaveDraftRevision(UUID ownerID, UUID projectID, long expectedCurrentRevision,
            string manifestJson, IList<GameProjectExperienceRevisionData> experiences)
        {
            if (ownerID == UUID.Zero || projectID == UUID.Zero || expectedCurrentRevision < 0
                || string.IsNullOrWhiteSpace(manifestJson)
                || manifestJson.Length > GameProjectManifestValidator.MaximumManifestCharacters
                || experiences == null || experiences.Count == 0
                || experiences.Count > GameProjectManifestValidator.MaximumWorlds)
                return false;

            try
            {
                using (JsonDocument document = JsonDocument.Parse(manifestJson))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                        return false;
                    JsonElement schema;
                    if ((!document.RootElement.TryGetProperty("schemaVersion", out schema)
                        && !document.RootElement.TryGetProperty("SchemaVersion", out schema))
                        || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != SchemaVersion)
                        return false;
                }

                GameProjectData project = m_Database.GetProject(projectID);
                if (project == null || project.OwnerID != ownerID
                    || project.Status != (int)GameProjectStatus.Draft
                    || project.CurrentRevision != expectedCurrentRevision
                    || project.SchemaVersion > SchemaVersion)
                    return false;

                HashSet<UUID> experienceIDs = new HashSet<UUID>();
                HashSet<int> sortOrders = new HashSet<int>();
                List<GameProjectExperienceRevisionData> pins =
                    new List<GameProjectExperienceRevisionData>();

                foreach (GameProjectExperienceRevisionData pin in experiences)
                {
                    if (pin == null || pin.ExperienceID == UUID.Zero
                        || pin.ExperienceRevision < 1 || pin.SortOrder < 0
                        || !experienceIDs.Add(pin.ExperienceID) || !sortOrders.Add(pin.SortOrder))
                        return false;

                    GameExperienceData identity =
                        m_Database.GetExperience(projectID, pin.ExperienceID);
                    GameExperienceRevisionData exact =
                        m_Database.GetExperienceRevision(projectID, pin.ExperienceID, pin.ExperienceRevision);
                    if (identity == null || exact == null || exact.ProjectID != projectID
                        || exact.ExperienceID != pin.ExperienceID
                        || exact.Revision != pin.ExperienceRevision || exact.CreatedBy != ownerID
                        || exact.SchemaVersion != 1 || string.IsNullOrWhiteSpace(exact.Name)
                        || exact.StartPlaceID == UUID.Zero || exact.Places == null
                        || exact.Places.Count == 0
                        || identity.CurrentRevision < pin.ExperienceRevision)
                        return false;

                    HashSet<UUID> placeIDs = new HashSet<UUID>();
                    HashSet<int> placeOrders = new HashSet<int>();
                    bool startPlaceFound = false;
                    foreach (GameExperiencePlaceRevisionData placePin in exact.Places)
                    {
                        if (placePin == null || placePin.PlaceID == UUID.Zero
                            || placePin.PlaceRevision < 1 || placePin.SortOrder < 0
                            || !placeIDs.Add(placePin.PlaceID) || !placeOrders.Add(placePin.SortOrder))
                            return false;
                        if (placePin.PlaceID == exact.StartPlaceID)
                            startPlaceFound = true;

                        GamePlaceData place =
                            m_Database.GetPlace(projectID, pin.ExperienceID, placePin.PlaceID);
                        GamePlaceRevisionData placeRevision = m_Database.GetPlaceRevision(
                            projectID, pin.ExperienceID, placePin.PlaceID, placePin.PlaceRevision);
                        if (place == null || placeRevision == null
                            || placeRevision.ProjectID != projectID
                            || placeRevision.ExperienceID != pin.ExperienceID
                            || placeRevision.PlaceID != placePin.PlaceID
                            || placeRevision.Revision != placePin.PlaceRevision
                            || placeRevision.CreatedBy != ownerID
                            || placeRevision.SchemaVersion != 1
                            || string.IsNullOrWhiteSpace(placeRevision.DefinitionJson)
                            || place.CurrentRevision < placePin.PlaceRevision)
                            return false;

                        using (JsonDocument placeDefinition = JsonDocument.Parse(placeRevision.DefinitionJson))
                        {
                            if (placeDefinition.RootElement.ValueKind != JsonValueKind.Object)
                                return false;
                        }
                    }
                    if (!startPlaceFound)
                        return false;

                    pins.Add(new GameProjectExperienceRevisionData
                    {
                        ExperienceID = pin.ExperienceID,
                        ExperienceRevision = pin.ExperienceRevision,
                        SortOrder = pin.SortOrder
                    });
                }

                DateTime now = DateTime.UtcNow;
                GameProjectRevisionData revision = new GameProjectRevisionData
                {
                    ProjectID = projectID,
                    Revision = expectedCurrentRevision + 1,
                    SchemaVersion = SchemaVersion,
                    ManifestJson = manifestJson,
                    CreatedBy = ownerID,
                    CreatedUTC = now,
                    Experiences = pins
                };
                return m_Database.SaveRevision(revision, ownerID, expectedCurrentRevision, now);
            }
            catch
            {
                return false;
            }
        }
    }
}
