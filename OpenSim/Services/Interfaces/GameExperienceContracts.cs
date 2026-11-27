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
        // UUIDv5-shaped stable identity using SHA-256. The namespace string and
        // canonical ProjectID text are part of the persisted migration contract.
        // Input bytes are UTF-8 for:
        // "opensim-next:legacy-default-experience:v1:" + ProjectID in lowercase D format.
        public static UUID GetLegacyDefaultExperienceID(UUID projectID)
        {
            if (projectID == UUID.Zero)
                throw new ArgumentException("Project ID must be non-zero.", nameof(projectID));

            string input = "opensim-next:legacy-default-experience:v1:"
                + projectID.ToString().ToLowerInvariant();
            byte[] hash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));

            // Set RFC 4122 variant and version 5 bits, then format explicitly
            // so UUID byte ordering is independent of Guid's mixed-endian layout.
            hash[6] = (byte)((hash[6] & 0x0f) | 0x50);
            hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
            string hex = BitConverter.ToString(hash, 0, 16).Replace("-", "").ToLowerInvariant();
            string value = hex.Substring(0, 8) + "-" + hex.Substring(8, 4) + "-"
                + hex.Substring(12, 4) + "-" + hex.Substring(16, 4) + "-" + hex.Substring(20, 12);
            return UUID.Parse(value);
        }

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

    /// <summary>
    /// Validates the Experience/Place graph before it is persisted or used to
    /// provision a runtime. IDs are stable domain identifiers, not Region IDs.
    /// </summary>
    public static class GameExperienceValidator
    {
        public static GameProjectValidationResult Validate(GameExperienceDefinition experience)
        {
            if (experience == null)
                return GameProjectValidationResult.Invalid("Experience is required.");
            if (experience.ExperienceID == UUID.Zero)
                return GameProjectValidationResult.Invalid("Experience ID must be non-zero.");
            if (string.IsNullOrWhiteSpace(experience.Name) || experience.Name.Trim().Length > 80)
                return GameProjectValidationResult.Invalid("Experience name must contain between 1 and 80 characters.");
            if (experience.Places == null || experience.Places.Count == 0 || experience.Places.Count > GameProjectManifestValidator.MaximumWorlds)
                return GameProjectValidationResult.Invalid("Experience must contain between 1 and 64 Places.");

            var placeIDs = new HashSet<UUID>();
            foreach (GamePlaceDefinition place in experience.Places)
            {
                if (place == null || place.PlaceID == UUID.Zero || !placeIDs.Add(place.PlaceID))
                    return GameProjectValidationResult.Invalid("Place IDs must be non-zero and unique within an Experience.");
                if (string.IsNullOrWhiteSpace(place.Name) || place.Name.Trim().Length > 80)
                    return GameProjectValidationResult.Invalid("Place names must contain between 1 and 80 characters.");
                if (place.Description != null && place.Description.Length > 2000)
                    return GameProjectValidationResult.Invalid("Place descriptions cannot exceed 2000 characters.");
                if (place.Worlds == null || place.Worlds.Count > 1)
                    return GameProjectValidationResult.Invalid("Each Place must map to at most one legacy World in the compatibility schema.");
                if (place.Worlds.Count == 1 && (place.Worlds[0] == null || place.Worlds[0].WorldID != place.PlaceID))
                    return GameProjectValidationResult.Invalid("A legacy World mapped to a Place must retain the same stable ID.");
            }

            if (experience.StartPlaceID == UUID.Zero || !placeIDs.Contains(experience.StartPlaceID))
                return GameProjectValidationResult.Invalid("StartPlaceID must reference a Place in this Experience.");

            return GameProjectValidationResult.Valid();
        }
    }

    /// <summary>Exact immutable Place revision selected by an Experience revision.</summary>
    public sealed class GameExperiencePlacePin
    {
        public UUID PlaceID { get; set; }
        public long PlaceRevision { get; set; }
        public int SortOrder { get; set; }
    }

}
