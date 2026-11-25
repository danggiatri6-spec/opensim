using System.Numerics;
using System.Text.Json;
using OpenSim.GamePhysics.WorkerProtocol;

namespace OpenSim.GamePhysics.Worker;

public sealed class FakePhysicsWorldAdapter : IPhysicsWorldAdapter
{
    private const int HardBodyLimit = 128;
    private const int HardTotalBodyLimit = 384;
    private const int MaxStepsPerCommand = 3;
    private const float FixedTimeStep = 1f / 30f;
    private static readonly HashSet<string> SupportedShapes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Box", "Sphere", "Capsule", "ConvexHull"
    };

    private readonly Dictionary<Guid, BodyState> _bodies = new();
    private readonly Queue<JsonElement> _pendingEvents = new();
    private readonly HashSet<(Guid A, Guid B)> _activePairs = new();
    private Guid _worldId;
    private Vector3 _gravity = new(0, -9.81f, 0);
    private int _maxDynamicBodies = HardBodyLimit;
    private long _tick;
    private bool _created;
    private bool _stopped;
    private bool _disposed;

    public JsonElement CreateWorld(Guid worldId, JsonElement configuration)
    {
        ThrowIfDisposed();
        if (_created)
            throw new WorkerCommandException(WorkerErrorCode.InvalidWorldState, "A worker process can own only one world.");
        if (worldId == Guid.Empty)
            throw new WorkerCommandException(WorkerErrorCode.InvalidArgument, "World ID is required.");

        string profile = GetString(configuration, "profile", "Balanced");
        if (profile is not ("Lightweight" or "Balanced" or "Advanced"))
            throw new WorkerCommandException(WorkerErrorCode.InvalidArgument, "Unknown physics profile.");

        if (TryGet(configuration, "gravity", out JsonElement gravity))
            _gravity = ReadVector(gravity, "gravity");

        if (TryGet(configuration, "maxDynamicBodies", out JsonElement maxBodies))
        {
            if (!maxBodies.TryGetInt32(out int requested) || requested < 1 || requested > HardBodyLimit)
                throw new WorkerCommandException(WorkerErrorCode.CapacityExceeded, $"maxDynamicBodies must be between 1 and {HardBodyLimit}.");
            _maxDynamicBodies = requested;
        }

        _worldId = worldId;
        _created = true;
        _stopped = false;
        return JsonSerializer.SerializeToElement(new
        {
            state = "Created",
            worldId = _worldId,
            profile,
            fixedTimeStep = FixedTimeStep,
            maxDynamicBodies = _maxDynamicBodies
        });
    }

    public JsonElement CreateBody(JsonElement body)
    {
        EnsureRunning();
        Guid bodyId = ReadGuid(body, "bodyId");
        if (_bodies.ContainsKey(bodyId))
            throw new WorkerCommandException(WorkerErrorCode.ObjectAlreadyExists, "Body ID already exists.");

        bool isStatic = false;
        if (TryGet(body, "isStatic", out JsonElement staticValue))
        {
            if (staticValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new WorkerCommandException(WorkerErrorCode.InvalidArgument, "isStatic must be a boolean.");
            isStatic = staticValue.GetBoolean();
        }
        string shape = GetString(body, "shape", "Box");
        if (!SupportedShapes.Contains(shape))
            throw new WorkerCommandException(WorkerErrorCode.InvalidArgument, "Unsupported body shape.");

        float mass = isStatic ? 0 : ReadFiniteFloat(body, "mass", 1f, 0.0001f, 100000f);
        Vector3 position = TryGet(body, "position", out JsonElement pos) ? ReadVector(pos, "position") : Vector3.Zero;

        if (_bodies.Count >= HardTotalBodyLimit)
            throw new WorkerCommandException(WorkerErrorCode.CapacityExceeded, "Total body capacity has been reached.");
        if (!isStatic && _bodies.Values.Count(x => !x.IsStatic) >= _maxDynamicBodies)
            throw new WorkerCommandException(WorkerErrorCode.CapacityExceeded, "Dynamic body capacity has been reached.");

        bool isTrigger = false;
        if (TryGet(body, "isTrigger", out JsonElement triggerValue))
        {
            if (triggerValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new WorkerCommandException(WorkerErrorCode.InvalidArgument, "isTrigger must be a boolean.");
            isTrigger = triggerValue.GetBoolean();
        }
        _bodies.Add(bodyId, new BodyState(bodyId, mass, isStatic, isTrigger, shape, position));
        return JsonSerializer.SerializeToElement(new { bodyId, mass, isStatic, isTrigger, shape, position = ToJson(position) });
    }

    public JsonElement SetVelocity(JsonElement command)
    {
        EnsureRunning();
        BodyState body = FindBody(command);
        if (body.IsStatic)
            throw new WorkerCommandException(WorkerErrorCode.InvalidArgument, "Static bodies cannot receive velocity.");
        body.Velocity = ReadVector(GetRequired(command, "velocity"), "velocity");
        return JsonSerializer.SerializeToElement(new { bodyId = body.Id, velocity = ToJson(body.Velocity) });
    }

    public JsonElement ApplyImpulse(JsonElement command)
    {
        EnsureRunning();
        BodyState body = FindBody(command);
        if (body.IsStatic)
            throw new WorkerCommandException(WorkerErrorCode.InvalidArgument, "Static bodies cannot receive impulses.");
        Vector3 impulse = ReadVector(GetRequired(command, "impulse"), "impulse");
        Vector3 nextVelocity = body.Velocity + impulse / body.Mass;
        if (!IsValidVector(nextVelocity))
            throw new WorkerCommandException(WorkerErrorCode.InvalidArgument, "Impulse would exceed the permitted velocity range.");
        body.Velocity = nextVelocity;
        return JsonSerializer.SerializeToElement(new { bodyId = body.Id, velocity = ToJson(body.Velocity) });
    }

    public JsonElement Step(JsonElement command)
    {
        EnsureRunning();
        int steps = 1;
        if (TryGet(command, "steps", out JsonElement stepValue) &&
            (!stepValue.TryGetInt32(out steps) || steps < 1 || steps > MaxStepsPerCommand))
            throw new WorkerCommandException(WorkerErrorCode.InvalidArgument, $"steps must be between 1 and {MaxStepsPerCommand}.");

        for (int i = 0; i < steps; i++)
        {
            var updates = new List<(BodyState Body, Vector3 Position, Vector3 Velocity)>();
            foreach (BodyState body in _bodies.Values)
            {
                if (body.IsStatic)
                    continue;
                Vector3 nextVelocity = body.Velocity + _gravity * FixedTimeStep;
                Vector3 nextPosition = body.Position + nextVelocity * FixedTimeStep;
                if (!IsValidVector(nextVelocity) || !IsValidVector(nextPosition))
                    throw new WorkerCommandException(WorkerErrorCode.InvalidArgument, "Simulation state exceeded permitted world bounds.");
                updates.Add((body, nextPosition, nextVelocity));
            }

            foreach (var update in updates)
            {
                update.Body.Velocity = update.Velocity;
                update.Body.Position = update.Position;
            }
            _tick++;
            UpdateContacts();
        }

        return GetSnapshot();
    }

    public IReadOnlyList<JsonElement> DrainEvents()
    {
        var events = new List<JsonElement>(_pendingEvents.Count);
        while (_pendingEvents.Count > 0) events.Add(_pendingEvents.Dequeue());
        return events;
    }

    public JsonElement GetSnapshot()
    {
        EnsureRunning();
        return JsonSerializer.SerializeToElement(new
        {
            worldId = _worldId,
            tick = _tick,
            bodies = _bodies.Values.OrderBy(x => x.Id).Select(x => new
            {
                bodyId = x.Id,
                position = ToJson(x.Position),
                velocity = ToJson(x.Velocity),
                isStatic = x.IsStatic,
                isTrigger = x.IsTrigger,
                shape = x.Shape
            }).ToArray()
        });
    }

    public JsonElement DestroyBody(JsonElement command)
    {
        EnsureRunning();
        Guid bodyId = ReadGuid(command, "bodyId");
        if (!_bodies.Remove(bodyId, out BodyState? removed))
            throw new WorkerCommandException(WorkerErrorCode.ObjectNotFound, "Body ID was not found.");
        foreach (var pair in _activePairs.Where(x => x.A == bodyId || x.B == bodyId).ToArray())
        {
            _activePairs.Remove(pair);
            bool trigger = removed.IsTrigger ||
                (_bodies.GetValueOrDefault(pair.A == bodyId ? pair.B : pair.A)?.IsTrigger ?? false);
            _pendingEvents.Enqueue(JsonSerializer.SerializeToElement(new
            {
                type = trigger ? "TriggerExit" : "ContactEnd", bodyA = pair.A, bodyB = pair.B, tick = _tick
            }));
        }
        return JsonSerializer.SerializeToElement(new { bodyId, destroyed = true });
    }

    private void UpdateContacts()
    {
        var current = new HashSet<(Guid A, Guid B)>();
        BodyState[] bodies = _bodies.Values.OrderBy(x => x.Id).ToArray();
        for (int i = 0; i < bodies.Length; i++)
        for (int j = i + 1; j < bodies.Length; j++)
        {
            BodyState a = bodies[i], b = bodies[j];
            if (a.IsStatic && b.IsStatic) continue;
            if (Vector3.DistanceSquared(a.Position, b.Position) > 1f) continue;
            var pair = (a.Id, b.Id);
            current.Add(pair);
            if (!_activePairs.Contains(pair))
            {
                string type = a.IsTrigger || b.IsTrigger ? "TriggerEnter" : "ContactBegin";
                _pendingEvents.Enqueue(JsonSerializer.SerializeToElement(new
                {
                    type, bodyA = a.Id, bodyB = b.Id, tick = _tick
                }));
            }
        }
        foreach (var pair in _activePairs.Except(current).OrderBy(x => x.A).ThenBy(x => x.B))
        {
            BodyState? a = _bodies.GetValueOrDefault(pair.A);
            BodyState? b = _bodies.GetValueOrDefault(pair.B);
            bool trigger = (a?.IsTrigger ?? false) || (b?.IsTrigger ?? false);
            _pendingEvents.Enqueue(JsonSerializer.SerializeToElement(new
            {
                type = trigger ? "TriggerExit" : "ContactEnd", bodyA = pair.A, bodyB = pair.B, tick = _tick
            }));
        }
        _activePairs.Clear();
        foreach (var pair in current) _activePairs.Add(pair);
    }

    public JsonElement StopWorld()
    {
        EnsureCreated();
        if (_stopped)
            return JsonSerializer.SerializeToElement(new { state = "Stopped" });
        _bodies.Clear();
        _activePairs.Clear();
        _pendingEvents.Clear();
        _stopped = true;
        return JsonSerializer.SerializeToElement(new { state = "Stopped", tick = _tick });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _bodies.Clear();
        _disposed = true;
    }

    private void EnsureCreated()
    {
        ThrowIfDisposed();
        if (!_created)
            throw new WorkerCommandException(WorkerErrorCode.InvalidWorldState, "World has not been created.");
    }

    private void EnsureRunning()
    {
        EnsureCreated();
        if (_stopped)
            throw new WorkerCommandException(WorkerErrorCode.InvalidWorldState, "World is stopped.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FakePhysicsWorldAdapter));
    }

    private BodyState FindBody(JsonElement command)
    {
        Guid id = ReadGuid(command, "bodyId");
        if (!_bodies.TryGetValue(id, out BodyState? body))
            throw new WorkerCommandException(WorkerErrorCode.ObjectNotFound, "Body ID was not found.");
        return body;
    }

    private static Guid ReadGuid(JsonElement element, string name)
    {
        JsonElement value = GetRequired(element, name);
        if (value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out Guid id) || id == Guid.Empty)
            throw new WorkerCommandException(WorkerErrorCode.InvalidArgument, $"{name} must be a non-empty UUID.");
        return id;
    }

    private static float ReadFiniteFloat(JsonElement element, string name, float defaultValue, float minimum, float maximum)
    {
        if (!TryGet(element, name, out JsonElement value))
            return defaultValue;
        if (!value.TryGetSingle(out float number) || !float.IsFinite(number) || number < minimum || number > maximum)
            throw new WorkerCommandException(WorkerErrorCode.InvalidArgument, $"{name} is outside its permitted range.");
        return number;
    }

    private static Vector3 ReadVector(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new WorkerCommandException(WorkerErrorCode.InvalidArgument, $"{name} must be a vector object.");
        return new Vector3(
            ReadComponent(value, "x", name),
            ReadComponent(value, "y", name),
            ReadComponent(value, "z", name));
    }

    private static float ReadComponent(JsonElement vector, string component, string name)
    {
        if (!TryGet(vector, component, out JsonElement value) ||
            !value.TryGetSingle(out float number) ||
            !float.IsFinite(number) ||
            MathF.Abs(number) > 1_000_000f)
            throw new WorkerCommandException(WorkerErrorCode.InvalidArgument, $"{name}.{component} must be finite and within world bounds.");
        return number;
    }

    private static JsonElement GetRequired(JsonElement element, string name)
    {
        if (!TryGet(element, name, out JsonElement value))
            throw new WorkerCommandException(WorkerErrorCode.InvalidArgument, $"Missing required field '{name}'.");
        return value;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
            return true;
        value = default;
        return false;
    }

    private static string GetString(JsonElement element, string name, string defaultValue)
    {
        if (!TryGet(element, name, out JsonElement value))
            return defaultValue;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new WorkerCommandException(WorkerErrorCode.InvalidArgument, $"{name} must be a non-empty string.");
        return value.GetString()!;
    }

    private static bool IsValidVector(Vector3 vector) =>
        float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z) &&
        MathF.Abs(vector.X) <= 1_000_000f && MathF.Abs(vector.Y) <= 1_000_000f && MathF.Abs(vector.Z) <= 1_000_000f;

    private static object ToJson(Vector3 vector) => new { x = vector.X, y = vector.Y, z = vector.Z };

    private sealed class BodyState(Guid id, float mass, bool isStatic, bool isTrigger, string shape, Vector3 position)
    {
        public Guid Id { get; } = id;
        public float Mass { get; } = mass;
        public bool IsStatic { get; } = isStatic;
        public bool IsTrigger { get; } = isTrigger;
        public string Shape { get; } = shape;
        public Vector3 Position { get; set; } = position;
        public Vector3 Velocity { get; set; }
    }
}
