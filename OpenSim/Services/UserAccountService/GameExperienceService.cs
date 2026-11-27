/*
 * OpenSim-next Experience and Place authoring service.
 *
 * All operations are scoped to the authenticated owner and a Draft project.
 * Revisions are immutable; the provider performs atomic compare-and-save.
 */
using System;
using System.Collections.Generic;
using System.Text.Json;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.UserAccountService
{
    public sealed class GameExperienceService
    {
        public const int MaximumDefinitionCharacters = 2 * 1024 * 1024;
        private readonly IGameProjectData m_Database;

        public GameExperienceService(IGameProjectData database)
        {
            m_Database = database ?? throw new ArgumentNullException(nameof(database));
        }

        public GameExperienceData CreateExperience(UUID ownerID, UUID projectID)
        {
            if (ownerID == UUID.Zero || projectID == UUID.Zero)
                return null;
            if (!TryGetDraftProject(ownerID, projectID))
                return null;

            DateTime now = DateTime.UtcNow;
            GameExperienceData record = new GameExperienceData
            {
                ProjectID = projectID,
                ExperienceID = UUID.Random(),
                Name = string.Empty,
                StartPlaceID = UUID.Zero,
                CurrentRevision = 0,
                CreatedUTC = now,
                UpdatedUTC = now
            };

            try
            {
                if (!m_Database.CreateExperience(record, ownerID))
                    return null;
                // Name and StartPlaceID are revision data. The identity row is intentionally
                // empty until its first immutable revision is saved.
                return m_Database.GetExperience(projectID, record.ExperienceID);
            }
            catch { return null; }
        }

        public List<GameExperienceData> GetExperiences(UUID ownerID, UUID projectID)
        {
            if (ownerID == UUID.Zero || projectID == UUID.Zero || !TryGetProject(ownerID, projectID))
                return new List<GameExperienceData>();
            try { return m_Database.GetExperiences(projectID, ownerID) ?? new List<GameExperienceData>(); }
            catch { return new List<GameExperienceData>(); }
        }

        public GamePlaceData CreatePlace(UUID ownerID, UUID projectID, UUID experienceID)
        {
            if (ownerID == UUID.Zero || projectID == UUID.Zero || experienceID == UUID.Zero
                || !TryGetDraftProject(ownerID, projectID))
                return null;

            try
            {
                if (m_Database.GetExperience(projectID, experienceID) == null)
                    return null;
                DateTime now = DateTime.UtcNow;
                GamePlaceData record = new GamePlaceData
                {
                    ProjectID = projectID,
                    ExperienceID = experienceID,
                    PlaceID = UUID.Random(),
                    CurrentRevision = 0,
                    CreatedUTC = now,
                    UpdatedUTC = now
                };
                return m_Database.CreatePlace(record, ownerID)
                    ? m_Database.GetPlace(projectID, experienceID, record.PlaceID)
                    : null;
            }
            catch { return null; }
        }

        public List<GamePlaceData> GetPlaces(UUID ownerID, UUID projectID, UUID experienceID)
        {
            if (ownerID == UUID.Zero || projectID == UUID.Zero || experienceID == UUID.Zero
                || !TryGetProject(ownerID, projectID))
                return new List<GamePlaceData>();
            try
            {
                if (m_Database.GetExperience(projectID, experienceID) == null)
                    return new List<GamePlaceData>();
                return m_Database.GetPlaces(projectID, experienceID, ownerID) ?? new List<GamePlaceData>();
            }
            catch { return new List<GamePlaceData>(); }
        }

        public bool SavePlaceRevision(UUID ownerID, UUID projectID, UUID experienceID, UUID placeID,
            long expectedCurrentRevision, int schemaVersion, string name, string description, string definitionJson)
        {
            if (ownerID == UUID.Zero || projectID == UUID.Zero || experienceID == UUID.Zero || placeID == UUID.Zero
                || expectedCurrentRevision < 0 || schemaVersion != 1
                || !ValidText(name, 1, 80) || !ValidText(description ?? string.Empty, 0, 2000)
                || string.IsNullOrWhiteSpace(definitionJson) || definitionJson.Length > MaximumDefinitionCharacters)
                return false;
            if (!TryGetDraftProject(ownerID, projectID))
                return false;

            try
            {
                if (m_Database.GetPlace(projectID, experienceID, placeID) == null)
                    return false;
                using (JsonDocument.Parse(definitionJson)) { }
                DateTime now = DateTime.UtcNow;
                GamePlaceRevisionData revision = new GamePlaceRevisionData
                {
                    ProjectID = projectID,
                    ExperienceID = experienceID,
                    PlaceID = placeID,
                    Revision = expectedCurrentRevision + 1,
                    SchemaVersion = schemaVersion,
                    Name = name.Trim(),
                    Description = (description ?? string.Empty).Trim(),
                    DefinitionJson = definitionJson,
                    CreatedBy = ownerID,
                    CreatedUTC = now
                };
                return m_Database.SavePlaceRevision(revision, ownerID, expectedCurrentRevision, now);
            }
            catch { return false; }
        }

        public GamePlaceRevisionData GetPlaceRevision(UUID ownerID, UUID projectID, UUID experienceID,
            UUID placeID, long revision)
        {
            if (ownerID == UUID.Zero || projectID == UUID.Zero || experienceID == UUID.Zero
                || placeID == UUID.Zero || revision < 1 || !TryGetProject(ownerID, projectID))
                return null;
            try
            {
                GamePlaceData place = m_Database.GetPlace(projectID, experienceID, placeID);
                if (place == null) return null;
                GamePlaceRevisionData data = m_Database.GetPlaceRevision(projectID, experienceID, placeID, revision);
                return data != null && data.CreatedBy == ownerID ? data : null;
            }
            catch { return null; }
        }

        public bool SaveExperienceRevision(UUID ownerID, UUID projectID, UUID experienceID,
            long expectedCurrentRevision, int schemaVersion, string name, UUID startPlaceID,
            IList<GameExperiencePlacePin> places)
        {
            if (ownerID == UUID.Zero || projectID == UUID.Zero || experienceID == UUID.Zero
                || expectedCurrentRevision < 0 || schemaVersion != 1 || !ValidText(name, 1, 80)
                || places == null || places.Count == 0 || places.Count > GameProjectManifestValidator.MaximumWorlds)
                return false;
            if (!TryGetDraftProject(ownerID, projectID))
                return false;

            try
            {
                if (m_Database.GetExperience(projectID, experienceID) == null)
                    return false;

                HashSet<UUID> ids = new HashSet<UUID>();
                HashSet<int> orders = new HashSet<int>();
                bool startFound = false;
                List<GameExperiencePlaceRevisionData> pins = new List<GameExperiencePlaceRevisionData>();
                foreach (GameExperiencePlacePin pin in places)
                {
                    if (pin == null || pin.PlaceID == UUID.Zero || pin.PlaceRevision < 1 || pin.SortOrder < 0
                        || !ids.Add(pin.PlaceID) || !orders.Add(pin.SortOrder))
                        return false;
                    if (pin.PlaceID == startPlaceID) startFound = true;

                    GamePlaceData place = m_Database.GetPlace(projectID, experienceID, pin.PlaceID);
                    GamePlaceRevisionData exact = m_Database.GetPlaceRevision(projectID, experienceID, pin.PlaceID, pin.PlaceRevision);
                    if (place == null || exact == null || exact.CreatedBy != ownerID
                        || place.CurrentRevision < pin.PlaceRevision)
                        return false;

                    pins.Add(new GameExperiencePlaceRevisionData
                    {
                        PlaceID = pin.PlaceID,
                        PlaceRevision = pin.PlaceRevision,
                        SortOrder = pin.SortOrder
                    });
                }
                if (!startFound || startPlaceID == UUID.Zero)
                    return false;

                DateTime now = DateTime.UtcNow;
                GameExperienceRevisionData revision = new GameExperienceRevisionData
                {
                    ProjectID = projectID,
                    ExperienceID = experienceID,
                    Revision = expectedCurrentRevision + 1,
                    SchemaVersion = schemaVersion,
                    Name = name.Trim(),
                    StartPlaceID = startPlaceID,
                    Places = pins,
                    CreatedBy = ownerID,
                    CreatedUTC = now
                };
                return m_Database.SaveExperienceRevision(revision, ownerID, expectedCurrentRevision, now);
            }
            catch { return false; }
        }

        public GameExperienceRevisionData GetExperienceRevision(UUID ownerID, UUID projectID,
            UUID experienceID, long revision)
        {
            if (ownerID == UUID.Zero || projectID == UUID.Zero || experienceID == UUID.Zero
                || revision < 1 || !TryGetProject(ownerID, projectID))
                return null;
            try
            {
                if (m_Database.GetExperience(projectID, experienceID) == null)
                    return null;
                GameExperienceRevisionData data = m_Database.GetExperienceRevision(projectID, experienceID, revision);
                return data != null && data.CreatedBy == ownerID ? data : null;
            }
            catch { return null; }
        }

        private bool TryGetProject(UUID ownerID, UUID projectID)
        {
            GameProjectData project = m_Database.GetProject(projectID);
            return project != null && project.OwnerID == ownerID;
        }

        private bool TryGetDraftProject(UUID ownerID, UUID projectID)
        {
            GameProjectData project = m_Database.GetProject(projectID);
            return project != null && project.OwnerID == ownerID
                && project.Status == (int)GameProjectStatus.Draft;
        }

        private static bool ValidText(string value, int minimum, int maximum)
        {
            return value != null && value.Trim().Length >= minimum && value.Trim().Length <= maximum;
        }
    }
}
