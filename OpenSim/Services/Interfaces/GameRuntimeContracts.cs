/*
 * OpenSim-next Game Runtime contracts.
 *
 * Live Game Instances reference immutable project revisions and are not Regions.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenMetaverse;

namespace OpenSim.Services.Interfaces
{
    public enum GameInstanceState
    {
        Starting = 0,
        Running = 1,
        Stopping = 2,
        Stopped = 3,
        Faulted = 4
    }

    public enum GameInstanceError
    {
        None = 0,
        Forbidden = 1,
        NotFound = 2,
        InvalidRevision = 3,
        InvalidManifest = 4,
        AlreadyRunning = 5,
        CapacityExceeded = 6,
        WorkerUnavailable = 7,
        InvalidState = 8,
        InternalError = 9
    }

    /// <summary>
    /// Ephemeral live instance metadata. It references an immutable project revision
    /// and intentionally contains no process IDs, IPC endpoints, tokens, or native handles.
    /// </summary>
    public sealed class GameInstance
    {
        public UUID InstanceID { get; set; }
        public UUID OwnerID { get; set; }
        public UUID ProjectID { get; set; }
        public long ProjectRevision { get; set; }
        public UUID WorldID { get; set; }
        public GameInstanceState State { get; set; }
        public DateTime CreatedUTC { get; set; }
        public DateTime? StartedUTC { get; set; }
        public DateTime? StoppedUTC { get; set; }
        public string LastErrorCode { get; set; } = string.Empty;
        public string LastErrorMessage { get; set; } = string.Empty;
    }

    public sealed class GameInstanceResult
    {
        public GameInstanceError Error { get; set; }
        public string Message { get; set; } = string.Empty;
        public GameInstance Instance { get; set; }

        public bool Success
        {
            get { return Error == GameInstanceError.None; }
        }
    }

    /// <summary>
    /// Creator-authorized lifecycle for ephemeral Game Instances.
    /// Implementations must resolve projects/revisions through IGameProjectService
    /// and must never create or register an OpenSim Region or Scene.
    /// </summary>
    public interface IGameRuntimeService
    {
        Task<GameInstanceResult> StartInstanceAsync(
            UUID authenticatedOwnerID,
            UUID projectID,
            long revision,
            CancellationToken cancellationToken = default);

        Task<GameInstanceResult> StopInstanceAsync(
            UUID authenticatedOwnerID,
            UUID instanceID,
            CancellationToken cancellationToken = default);

        Task<GameInstanceResult> GetInstanceAsync(
            UUID authenticatedOwnerID,
            UUID instanceID,
            CancellationToken cancellationToken = default);

        Task<List<GameInstance>> GetOwnedInstancesAsync(
            UUID authenticatedOwnerID,
            int offset,
            int limit,
            CancellationToken cancellationToken = default);
    }
}
