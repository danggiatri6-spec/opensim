/*
 * OpenSim-next Game Platform contracts.
 *
 * These types are additive and intentionally do not depend on Scene,
 * Region or Viewer implementation details.
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace OpenSim.Services.Interfaces
{
    public enum GameProjectStatus
    {
        Draft = 0,
        Published = 1,
        Archived = 2
    }

    public enum GameProjectError
    {
        None = 0,
        NotFound = 1,
        Forbidden = 2,
        InvalidData = 3,
        RevisionConflict = 4,
        QuotaExceeded = 5,
        StorageUnavailable = 6
    }

    /// <summary>
    /// Creator-owned game project metadata.
    /// OwnerID is assigned by the authenticated service, never trusted from a client.
    /// </summary>
    public sealed class GameProject
    {
        public UUID ProjectID { get; set; }
        public UUID OwnerID { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public GameProjectStatus Status { get; set; } = GameProjectStatus.Draft;
        public int SchemaVersion { get; set; } = 1;
        public long CurrentRevision { get; set; }
        public DateTime CreatedUTC { get; set; }
        public DateTime UpdatedUTC { get; set; }
    }

    /// <summary>
    /// A declarative world definition. This is not a live OpenSim Region.
    /// </summary>
    public sealed class GameWorldDefinition
    {
        public UUID WorldID { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int SizeX { get; set; } = 256;
        public int SizeY { get; set; } = 256;
        public List<GameSpawnPoint> SpawnPoints { get; set; } = new();
        public List<GameAssetReference> AssetReferences { get; set; } = new();
    }

    public sealed class GameSpawnPoint
    {
        public string Name { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public double RotationX { get; set; }
        public double RotationY { get; set; }
        public double RotationZ { get; set; }
        public double RotationW { get; set; } = 1.0;
    }

    /// <summary>
    /// Reference to an existing OpenSim asset; the project does not duplicate asset data.
    /// </summary>
    public sealed class GameAssetReference
    {
        public UUID AssetID { get; set; }
        public string AssetType { get; set; } = string.Empty;
        public string Usage { get; set; } = string.Empty;
    }

    public sealed class GameSettings
    {
        public string GameMode { get; set; } = "sandbox";
        public UUID DefaultWorldID { get; set; }
        public string Visibility { get; set; } = "private";
        public int MaxPlayers { get; set; } = 16;
        public List<string> Tags { get; set; } = new();
    }

    /// <summary>
    /// Serializable project manifest. Executable code is deliberately excluded.
    /// </summary>
    public sealed class GameProjectManifest
    {
        public int SchemaVersion { get; set; } = 1;
        public string Name { get; set; } = string.Empty;
        public List<GameWorldDefinition> Worlds { get; set; } = new();
        public GameSettings Settings { get; set; } = new();
    }

    /// <summary>
    /// Immutable snapshot created when a draft is saved.
    /// </summary>
    public sealed class GameProjectRevision
    {
        public UUID ProjectID { get; set; }
        public long Revision { get; set; }
        public int SchemaVersion { get; set; }
        public string ManifestJson { get; set; } = string.Empty;
        public UUID CreatedBy { get; set; }
        public DateTime CreatedUTC { get; set; }
    }

    public sealed class GameProjectResult
    {
        public GameProjectError Error { get; set; }
        public string Message { get; set; } = string.Empty;
        public GameProject Project { get; set; }

        public bool Success
        {
            get { return Error == GameProjectError.None; }
        }
    }

    public sealed class GameProjectValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new();

        public static GameProjectValidationResult Valid()
        {
            return new GameProjectValidationResult { IsValid = true };
        }

        public static GameProjectValidationResult Invalid(params string[] errors)
        {
            GameProjectValidationResult result = new() { IsValid = false };
            result.Errors.AddRange(errors);
            return result;
        }
    }

    /// <summary>
    /// Validation for untrusted Creator/AI-authored project manifests.
    /// This validator does not execute scripts or access the live Scene.
    /// </summary>
    public static class GameProjectManifestValidator
    {
        public const int CurrentSchemaVersion = 1;
        public const int MaximumWorlds = 64;
        public const int MaximumSpawnPointsPerWorld = 128;
        public const int MaximumAssetsPerWorld = 4096;
        public const int MaximumTags = 32;
        public const int MaximumManifestCharacters = 2 * 1024 * 1024;

        public static GameProjectValidationResult Validate(GameProjectManifest manifest)
        {
            List<string> errors = new();

            if (manifest == null)
                return GameProjectValidationResult.Invalid("Manifest is required.");

            if (manifest.SchemaVersion != CurrentSchemaVersion)
                errors.Add("Unsupported manifest schema version.");

            if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Trim().Length > 80)
                errors.Add("Project name must contain between 1 and 80 characters.");

            if (manifest.Worlds == null || manifest.Worlds.Count == 0)
                errors.Add("At least one world is required.");
            else if (manifest.Worlds.Count > MaximumWorlds)
                errors.Add("Project exceeds the maximum number of worlds.");

            if (manifest.Settings == null)
            {
                errors.Add("Game settings are required.");
            }
            else
            {
                if (manifest.Settings.MaxPlayers < 1 || manifest.Settings.MaxPlayers > 256)
                    errors.Add("MaxPlayers must be between 1 and 256.");

                if (manifest.Settings.Tags == null || manifest.Settings.Tags.Count > MaximumTags)
                    errors.Add("Project tags exceed the allowed limit.");

                if (manifest.Settings.Visibility != "private"
                    && manifest.Settings.Visibility != "unlisted"
                    && manifest.Settings.Visibility != "public")
                    errors.Add("Visibility must be private, unlisted, or public.");
            }

            if (manifest.Worlds != null)
            {
                HashSet<UUID> worldIDs = new();

                foreach (GameWorldDefinition world in manifest.Worlds)
                {
                    if (world == null)
                    {
                        errors.Add("World entries cannot be null.");
                        continue;
                    }

                    if (world.WorldID == UUID.Zero || !worldIDs.Add(world.WorldID))
                        errors.Add("World IDs must be non-zero and unique within the project.");

                    if (string.IsNullOrWhiteSpace(world.Name) || world.Name.Trim().Length > 80)
                        errors.Add("World names must contain between 1 and 80 characters.");

                    if (world.SizeX < 16 || world.SizeX > 4096
                        || world.SizeY < 16 || world.SizeY > 4096)
                        errors.Add("World dimensions must be between 16 and 4096 meters.");

                    if (world.SpawnPoints == null || world.SpawnPoints.Count > MaximumSpawnPointsPerWorld)
                        errors.Add("World spawn points exceed the allowed limit.");

                    if (world.AssetReferences == null || world.AssetReferences.Count > MaximumAssetsPerWorld)
                        errors.Add("World asset references exceed the allowed limit.");

                    if (world.SpawnPoints != null)
                    {
                        foreach (GameSpawnPoint spawn in world.SpawnPoints)
                        {
                            if (spawn == null
                                || double.IsNaN(spawn.X) || double.IsInfinity(spawn.X)
                                || double.IsNaN(spawn.Y) || double.IsInfinity(spawn.Y)
                                || double.IsNaN(spawn.Z) || double.IsInfinity(spawn.Z)
                                || spawn.X < 0 || spawn.X > world.SizeX
                                || spawn.Y < 0 || spawn.Y > world.SizeY
                                || spawn.Z < -4096 || spawn.Z > 4096)
                            {
                                errors.Add("Spawn point coordinates are invalid or outside world bounds.");
                                break;
                            }
                        }
                    }

                    if (world.AssetReferences != null)
                    {
                        foreach (GameAssetReference asset in world.AssetReferences)
                        {
                            if (asset == null || asset.AssetID == UUID.Zero
                                || string.IsNullOrWhiteSpace(asset.AssetType)
                                || asset.AssetType.Length > 64)
                            {
                                errors.Add("Asset references must contain a valid asset ID and type.");
                                break;
                            }
                        }
                    }
                }

                if (manifest.Settings != null
                    && manifest.Settings.DefaultWorldID != UUID.Zero
                    && !worldIDs.Contains(manifest.Settings.DefaultWorldID))
                    errors.Add("DefaultWorldID must reference a world in this project.");
            }

            return errors.Count == 0
                ? GameProjectValidationResult.Valid()
                : new GameProjectValidationResult { IsValid = false, Errors = errors };
        }
    }

    /// <summary>
    /// Storage/service boundary for creator-owned projects.
    /// Implementations must enforce ownership and optimistic concurrency server-side.
    /// </summary>
    public interface IGameProjectService
    {
        GameProjectResult CreateProject(UUID authenticatedOwnerID, string name, string description);

        GameProjectResult GetProject(UUID authenticatedOwnerID, UUID projectID);

        List<GameProject> GetOwnedProjects(UUID authenticatedOwnerID, int offset, int limit);

        GameProjectResult SaveDraft(
            UUID authenticatedOwnerID,
            UUID projectID,
            long expectedCurrentRevision,
            GameProjectManifest manifest);

        GameProjectRevision GetRevision(
            UUID authenticatedOwnerID,
            UUID projectID,
            long revision);

        GameProjectResult UpdateMetadata(
            UUID authenticatedOwnerID,
            UUID projectID,
            long expectedCurrentRevision,
            string name,
            string description);

        bool ArchiveProject(UUID authenticatedOwnerID, UUID projectID);
    }
}
