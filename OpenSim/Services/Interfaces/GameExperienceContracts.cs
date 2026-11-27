/*
 * OpenSim-next Experience and Place contracts.
 *
 * These are additive domain contracts. The legacy GameProjectManifest remains
 * the persisted v1 format until a separately versioned migration is introduced.
 */
using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace OpenSim.Services.Interfaces
{
    /// <summary>
    /// A player-facing game experience containing one or more authored Places.
    /// </summary>
    public sealed class GameExperienceDefinition
    {
        public UUID ExperienceID { get; set; }
        public string Name { get; set; } = string.Empty;
        public UUID StartPlaceID { get; set; }
        public List<GamePlaceDefinition> Places { get; set; } = new();
    }

    /// <summary>
    /// An authored scene/map. It is not a live Region or simulation process.
    /// The initial compatibility model maps one legacy world to one Place.
    /// </summary>
    public sealed class GamePlaceDefinition
    {
        public UUID PlaceID { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<GameWorldDefinition> Worlds { get; set; } = new();
    }

    /// <summary>
    /// Projects the existing v1 manifest into the new Experience/Place domain
    /// without changing persisted JSON, revision numbers, or existing IDs.
    /// Each legacy World becomes one Place; all Places are grouped into a
    /// default Experience. The WorldID is reused as PlaceID for stable links.
    /// </summary>
    public static class GameProjectExperienceAdapter
    {
        public static GameExperienceDefinition FromLegacyManifest(
            GameProjectManifest manifest,
            UUID experienceID,
            string experienceName = null)
        {
            if (manifest == null)
                throw new ArgumentNullException(nameof(manifest));
            if (experienceID == UUID.Zero)
                throw new ArgumentException("Experience ID must be non-zero.", nameof(experienceID));
            if (manifest.Worlds == null || manifest.Worlds.Count == 0)
                throw new ArgumentException("A legacy manifest must contain at least one world.", nameof(manifest));

            GameExperienceDefinition experience = new GameExperienceDefinition
            {
                ExperienceID = experienceID,
                Name = string.IsNullOrWhiteSpace(experienceName) ? manifest.Name : experienceName.Trim(),
                StartPlaceID = manifest.Settings?.DefaultWorldID ?? UUID.Zero
            };

            foreach (GameWorldDefinition world in manifest.Worlds)
            {
                if (world == null)
                    throw new ArgumentException("Legacy worlds cannot contain null entries.", nameof(manifest));

                GamePlaceDefinition place = new GamePlaceDefinition
                {
                    PlaceID = world.WorldID,
                    Name = world.Name,
                    Description = world.Description
                };
                place.Worlds.Add(world);
                experience.Places.Add(place);
            }

            return experience;
        }
    }
}
