/*
 * OpenSim-next Game Platform data contracts.
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace OpenSim.Data
{
    /// <summary>
    /// Storage representation for a creator-owned game project.
    /// ManifestJson contains declarative data only, never executable code.
    /// </summary>
    public sealed class GameProjectData
    {
        public UUID ProjectID { get; set; }
        public UUID OwnerID { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int Status { get; set; }
        public int SchemaVersion { get; set; }
        public long CurrentRevision { get; set; }
        public DateTime CreatedUTC { get; set; }
        public DateTime UpdatedUTC { get; set; }
    }

    public sealed class GameProjectExperienceRevisionData
    {
        public UUID ExperienceID { get; set; }
        public long ExperienceRevision { get; set; }
        public int SortOrder { get; set; }
    }

    public sealed class GameProjectRevisionData
    {
        public UUID ProjectID { get; set; }
        public long Revision { get; set; }
        public int SchemaVersion { get; set; }
        public string ManifestJson { get; set; } = string.Empty;
        public UUID CreatedBy { get; set; }
        public DateTime CreatedUTC { get; set; }
        public List<GameProjectExperienceRevisionData> Experiences { get; set; } = new List<GameProjectExperienceRevisionData>();
    }

    public sealed class GameExperienceData
    {
        public UUID ProjectID { get; set; }
        public UUID ExperienceID { get; set; }
        public string Name { get; set; } = string.Empty;
        public UUID StartPlaceID { get; set; }
        public long CurrentRevision { get; set; }
        public DateTime CreatedUTC { get; set; }
        public DateTime UpdatedUTC { get; set; }
    }

    public sealed class GamePlaceData
    {
        public UUID ProjectID { get; set; }
        public UUID ExperienceID { get; set; }
        public UUID PlaceID { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public long CurrentRevision { get; set; }
        public DateTime CreatedUTC { get; set; }
        public DateTime UpdatedUTC { get; set; }
    }

    public sealed class GamePlaceRevisionData
    {
        public UUID ProjectID { get; set; }
        public UUID ExperienceID { get; set; }
        public UUID PlaceID { get; set; }
        public long Revision { get; set; }
        public int SchemaVersion { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string DefinitionJson { get; set; } = string.Empty;
        public UUID CreatedBy { get; set; }
        public DateTime CreatedUTC { get; set; }
    }

    public sealed class GameExperiencePlaceRevisionData
    {
        public UUID PlaceID { get; set; }
        public long PlaceRevision { get; set; }
        public int SortOrder { get; set; }
    }

    public sealed class GameExperienceRevisionData
    {
        public UUID ProjectID { get; set; }
        public UUID ExperienceID { get; set; }
        public long Revision { get; set; }
        public int SchemaVersion { get; set; }
        public string Name { get; set; } = string.Empty;
        public UUID StartPlaceID { get; set; }
        public List<GameExperiencePlaceRevisionData> Places { get; set; } = new List<GameExperiencePlaceRevisionData>();
        public UUID CreatedBy { get; set; }
        public DateTime CreatedUTC { get; set; }
    }

    /// <summary>
    /// Provider contract for durable Game Platform storage.
    /// Database providers must implement atomic revision compare-and-save.
    /// </summary>
    public interface IGameProjectData
    {
        GameExperienceData GetExperience(UUID projectID, UUID experienceID);
        List<GameExperienceData> GetExperiences(UUID projectID, UUID ownerID);
        bool CreateExperience(GameExperienceData experience, UUID ownerID);
        GamePlaceData GetPlace(UUID projectID, UUID experienceID, UUID placeID);
        List<GamePlaceData> GetPlaces(UUID projectID, UUID experienceID, UUID ownerID);
        bool CreatePlace(GamePlaceData place, UUID ownerID);
        bool SavePlaceRevision(GamePlaceRevisionData revision, UUID ownerID, long expectedCurrentRevision, DateTime updatedUTC);
        GamePlaceRevisionData GetPlaceRevision(UUID projectID, UUID experienceID, UUID placeID, long revision);
        bool SaveExperienceRevision(GameExperienceRevisionData revision, UUID ownerID, long expectedCurrentRevision, DateTime updatedUTC);
        GameExperienceRevisionData GetExperienceRevision(UUID projectID, UUID experienceID, long revision);

        GameProjectData GetProject(UUID projectID);

        List<GameProjectData> GetProjectsByOwner(UUID ownerID, int offset, int limit);

        bool CreateProject(GameProjectData project);

        bool UpdateProjectMetadata(
            UUID projectID,
            UUID ownerID,
            long expectedCurrentRevision,
            DateTime expectedUpdatedUTC,
            string name,
            string description,
            DateTime updatedUTC);

        /// <summary>
        /// Atomically inserts the revision and advances CurrentRevision only when
        /// the stored value equals expectedCurrentRevision.
        /// Returns false for missing projects, ownership mismatch or revision conflict.
        /// </summary>
        bool SaveRevision(
            GameProjectRevisionData revision,
            UUID ownerID,
            long expectedCurrentRevision,
            DateTime updatedUTC);

        GameProjectRevisionData GetRevision(UUID projectID, long revision);

        /// <summary>
        /// Atomically transitions a draft to Published only when its current
        /// revision still matches and that immutable revision exists.
        /// </summary>
        bool PublishProject(UUID projectID, UUID ownerID, long expectedCurrentRevision,
            DateTime expectedUpdatedUTC, DateTime updatedUTC);

        bool ArchiveProject(UUID projectID, UUID ownerID, DateTime updatedUTC);
    }
}
