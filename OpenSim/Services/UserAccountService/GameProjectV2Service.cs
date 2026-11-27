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
