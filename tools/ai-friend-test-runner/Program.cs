using OpenSim.Region.OptionalModules.World.NPC.AIFriend;

static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL " + name);
    Console.WriteLine("PASS " + name);
}

static void Expect<T>(Action action, string name) where T : Exception
{
    try { action(); }
    catch (T) { Console.WriteLine("PASS " + name); return; }
    throw new InvalidOperationException("FAIL " + name);
}

var user = Guid.NewGuid();
var otherUser = Guid.NewGuid();
var relationship = new AiFriendRelationship
{
    UserId = user,
    CharacterId = Guid.NewGuid(),
    State = AiFriendRelationshipState.Met
};

Expect<UnauthorizedAccessException>(() =>
    AiFriendRelationshipRules.Transition(relationship, otherUser,
        AiFriendRelationshipState.Requested, DateTime.UtcNow),
    "another user cannot change this relationship");

Expect<InvalidOperationException>(() =>
    AiFriendRelationshipRules.Transition(relationship, user,
        AiFriendRelationshipState.Friends, DateTime.UtcNow),
    "friendship cannot skip the request step");

AiFriendRelationshipRules.Transition(relationship, user,
    AiFriendRelationshipState.Requested, DateTime.UtcNow);
AiFriendRelationshipRules.Transition(relationship, user,
    AiFriendRelationshipState.Friends, DateTime.UtcNow);
Check(relationship.State == AiFriendRelationshipState.Friends &&
      relationship.FriendsSinceUtc.HasValue,
    "explicit request and acceptance establish friendship");

relationship.AllowProactiveGreeting = true;
relationship.AllowFollow = true;
relationship.AllowVoice = true;
AiFriendRelationshipRules.Transition(relationship, user,
    AiFriendRelationshipState.Blocked, DateTime.UtcNow);
Check(!relationship.AllowProactiveGreeting && !relationship.AllowFollow &&
      !relationship.AllowVoice,
    "blocking disables proactive contact, follow, and voice");

var character = new AiCharacterDefinition { DisplayName = "Mira" };
character.Validate();
character.IsAiDisclosureEnabled = false;
Expect<ArgumentException>(() => character.Validate(),
    "AI characters must disclose their AI identity");

var memory = new AiFriendMemory
{
    RelationshipId = Guid.NewGuid(),
    Content = new string('x', AiFriendLimits.MaxMemoryLength + 1)
};
Expect<ArgumentException>(() => memory.Validate(),
    "memory content length is bounded");

var localTimeRelationship = new AiFriendRelationship
{
    UserId = Guid.NewGuid(),
    CharacterId = Guid.NewGuid(),
    FirstMetUtc = DateTime.Now
};
Expect<ArgumentException>(() => localTimeRelationship.Validate(),
    "relationship timestamps must be UTC");

Console.WriteLine("AI Friend domain checks passed.");
