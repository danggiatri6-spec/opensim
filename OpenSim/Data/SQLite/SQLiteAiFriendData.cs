/*
 * OpenSim-next AI Friend SQLite persistence provider.
 */
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Globalization;
using OpenSim.Framework;
using OpenSim.Data;

namespace OpenSim.Data.SQLite
{
    /// <summary>
    /// Durable AI Friend storage. Private records are always queried through
    /// the authenticated user and relationship pair, never by relationship ID alone.
    /// </summary>
    public sealed class SQLiteAiFriendData : OpenSim.Data.IAiFriendData
    {
        private readonly string _connectionString;
        private readonly object _migrationLock = new object();

        public SQLiteAiFriendData(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("A database connection string is required.", nameof(connectionString));
            DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly);
            _connectionString = connectionString;
            InitializeSchema();
        }

        private SQLiteConnection OpenConnection()
        {
            SQLiteConnection connection = new SQLiteConnection(_connectionString);
            connection.Open();
            using (SQLiteCommand pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys=ON";
                pragma.ExecuteNonQuery();
            }
            return connection;
        }

        private void InitializeSchema()
        {
            lock (_migrationLock)
            using (SQLiteConnection connection = OpenConnection())
            {
                Migration migration = new Migration(connection, GetType().Assembly, "AiFriends");
                migration.Update();
            }
        }

        public OpenSim.Data.AiFriendCharacterData GetCharacter(Guid characterId)
        {
            using (SQLiteConnection c = OpenConnection())
            using (SQLiteCommand q = c.CreateCommand())
            {
                q.CommandText = "SELECT * FROM AiFriendCharacters WHERE CharacterID=@id";
                q.Parameters.AddWithValue("@id", Id(characterId));
                using (SQLiteDataReader r = q.ExecuteReader())
                    return r.Read() ? ReadCharacter(r) : null;
            }
        }

        public bool SaveCharacter(OpenSim.Data.AiFriendCharacterData x)
        {
            if (x == null || x.CharacterId == Guid.Empty || string.IsNullOrWhiteSpace(x.DisplayName)
                || x.DisplayName.Length > 64 || x.ProfileDescription == null || x.ProfileDescription.Length > 1000
                || x.PersonaVersion < 1 || x.CreatedUtc.Kind != DateTimeKind.Utc
                || x.UpdatedUtc.Kind != DateTimeKind.Utc || x.Status < 0 || x.Status > 3)
                return false;

            using (SQLiteConnection c = OpenConnection())
            using (SQLiteTransaction transaction = c.BeginTransaction(IsolationLevel.Serializable))
            {
                int updated;
                using (SQLiteCommand q = c.CreateCommand())
                {
                    q.Transaction = transaction;
                    q.CommandText = @"UPDATE AiFriendCharacters SET
DisplayName=@name,ProfileDescription=@profile,PersonaVersion=@persona,
AppearanceAssetID=@appearance,VoiceProfileID=@voice,PublisherID=@publisher,
Status=@status,UpdatedUTC=@updated
WHERE CharacterID=@id";
                    q.Parameters.AddWithValue("@id", Id(x.CharacterId));
                    q.Parameters.AddWithValue("@name", x.DisplayName);
                    q.Parameters.AddWithValue("@profile", x.ProfileDescription);
                    q.Parameters.AddWithValue("@persona", x.PersonaVersion);
                    q.Parameters.AddWithValue("@appearance", Id(x.AppearanceAssetId));
                    q.Parameters.AddWithValue("@voice", Id(x.VoiceProfileId));
                    q.Parameters.AddWithValue("@publisher", Id(x.PublisherId));
                    q.Parameters.AddWithValue("@status", x.Status);
                    q.Parameters.AddWithValue("@updated", Date(x.UpdatedUtc));
                    updated = q.ExecuteNonQuery();
                }

                if (updated == 0)
                {
                    using (SQLiteCommand q = c.CreateCommand())
                    {
                        q.Transaction = transaction;
                        q.CommandText = @"INSERT INTO AiFriendCharacters
(CharacterID,DisplayName,ProfileDescription,PersonaVersion,AppearanceAssetID,VoiceProfileID,PublisherID,Status,CreatedUTC,UpdatedUTC)
VALUES (@id,@name,@profile,@persona,@appearance,@voice,@publisher,@status,@created,@updated)";
                        q.Parameters.AddWithValue("@id", Id(x.CharacterId));
                        q.Parameters.AddWithValue("@name", x.DisplayName);
                        q.Parameters.AddWithValue("@profile", x.ProfileDescription);
                        q.Parameters.AddWithValue("@persona", x.PersonaVersion);
                        q.Parameters.AddWithValue("@appearance", Id(x.AppearanceAssetId));
                        q.Parameters.AddWithValue("@voice", Id(x.VoiceProfileId));
                        q.Parameters.AddWithValue("@publisher", Id(x.PublisherId));
                        q.Parameters.AddWithValue("@status", x.Status);
                        q.Parameters.AddWithValue("@created", Date(x.CreatedUtc));
                        q.Parameters.AddWithValue("@updated", Date(x.UpdatedUtc));
                        if (q.ExecuteNonQuery() != 1)
                        {
                            transaction.Rollback();
                            return false;
                        }
                    }
                }

                transaction.Commit();
                return true;
            }
        }

        public OpenSim.Data.AiFriendRelationshipData GetRelationship(Guid authenticatedUserId, Guid characterId)
        {
            if (authenticatedUserId == Guid.Empty || characterId == Guid.Empty) return null;
            using (SQLiteConnection c = OpenConnection())
            using (SQLiteCommand q = c.CreateCommand())
            {
                q.CommandText = "SELECT * FROM AiFriendRelationships WHERE UserID=@user AND CharacterID=@character";
                q.Parameters.AddWithValue("@user", Id(authenticatedUserId));
                q.Parameters.AddWithValue("@character", Id(characterId));
                using (SQLiteDataReader r = q.ExecuteReader())
                    return r.Read() ? ReadRelationship(r) : null;
            }
        }

        public bool CreateRelationship(OpenSim.Data.AiFriendRelationshipData x, Guid authenticatedUserId)
        {
            if (!ValidRelationship(x) || x.UserId != authenticatedUserId || x.Version != 1) return false;
            using (SQLiteConnection c = OpenConnection())
            using (SQLiteCommand q = c.CreateCommand())
            {
                q.CommandText = @"INSERT INTO AiFriendRelationships
(RelationshipID,UserID,CharacterID,State,RelationshipLevel,FirstMetUTC,FriendsSinceUTC,LastInteractionUTC,AllowProactiveGreeting,AllowFollow,AllowVoice,Version)
SELECT @id,@user,@character,@state,@level,@first,@friends,@last,@greeting,@follow,@voice,@version
WHERE EXISTS (SELECT 1 FROM AiFriendCharacters WHERE CharacterID=@character AND Status=1)";
                AddRelationshipParameters(q, x);
                return q.ExecuteNonQuery() == 1;
            }
        }

        public bool UpdateRelationship(OpenSim.Data.AiFriendRelationshipData x, Guid authenticatedUserId, long expectedVersion)
        {
            if (!ValidRelationship(x) || x.UserId != authenticatedUserId || expectedVersion < 1
                || x.Version != expectedVersion + 1) return false;
            using (SQLiteConnection c = OpenConnection())
            using (SQLiteCommand q = c.CreateCommand())
            {
                q.CommandText = @"UPDATE AiFriendRelationships SET State=@state,RelationshipLevel=@level,
FriendsSinceUTC=@friends,LastInteractionUTC=@last,AllowProactiveGreeting=@greeting,
AllowFollow=@follow,AllowVoice=@voice,Version=@version
WHERE RelationshipID=@id AND UserID=@user AND CharacterID=@character AND Version=@expected";
                AddRelationshipParameters(q, x);
                q.Parameters.AddWithValue("@expected", expectedVersion);
                return q.ExecuteNonQuery() == 1;
            }
        }

        public bool AddMemory(OpenSim.Data.AiFriendMemoryData x, Guid authenticatedUserId)
        {
            if (x == null || authenticatedUserId == Guid.Empty || x.MemoryId == Guid.Empty
                || x.RelationshipId == Guid.Empty || string.IsNullOrWhiteSpace(x.Content)
                || x.Content.Length > 2000 || double.IsNaN(x.Confidence)
                || double.IsInfinity(x.Confidence) || x.Confidence < 0 || x.Confidence > 1
                || x.CreatedUtc.Kind != DateTimeKind.Utc
                || (x.LastConfirmedUtc.HasValue && x.LastConfirmedUtc.Value.Kind != DateTimeKind.Utc))
                return false;
            using (SQLiteConnection c = OpenConnection())
            using (SQLiteCommand q = c.CreateCommand())
            {
                q.CommandText = @"INSERT INTO AiFriendMemories
(MemoryID,RelationshipID,MemoryType,Content,Confidence,SourceConversationID,CreatedUTC,LastConfirmedUTC,IsDeleted)
SELECT @id,@relationship,@type,@content,@confidence,@source,@created,@confirmed,0
WHERE EXISTS (SELECT 1 FROM AiFriendRelationships WHERE RelationshipID=@relationship AND UserID=@user)";
                q.Parameters.AddWithValue("@id", Id(x.MemoryId));
                q.Parameters.AddWithValue("@relationship", Id(x.RelationshipId));
                q.Parameters.AddWithValue("@type", x.MemoryType);
                q.Parameters.AddWithValue("@content", x.Content);
                q.Parameters.AddWithValue("@confidence", x.Confidence);
                q.Parameters.AddWithValue("@source", Id(x.SourceConversationId));
                q.Parameters.AddWithValue("@created", Date(x.CreatedUtc));
                q.Parameters.AddWithValue("@confirmed", NullableDate(x.LastConfirmedUtc));
                q.Parameters.AddWithValue("@user", Id(authenticatedUserId));
                return q.ExecuteNonQuery() == 1;
            }
        }

        public List<OpenSim.Data.AiFriendMemoryData> GetMemories(Guid authenticatedUserId, Guid relationshipId, int limit)
        {
            List<OpenSim.Data.AiFriendMemoryData> result = new List<OpenSim.Data.AiFriendMemoryData>();
            if (authenticatedUserId == Guid.Empty || relationshipId == Guid.Empty) return result;
            if (limit < 1) limit = 1;
            if (limit > 100) limit = 100;
            using (SQLiteConnection c = OpenConnection())
            using (SQLiteCommand q = c.CreateCommand())
            {
                q.CommandText = @"SELECT m.* FROM AiFriendMemories m
INNER JOIN AiFriendRelationships r ON r.RelationshipID=m.RelationshipID
WHERE r.UserID=@user AND r.RelationshipID=@relationship AND m.IsDeleted=0
ORDER BY m.CreatedUTC DESC LIMIT @limit";
                q.Parameters.AddWithValue("@user", Id(authenticatedUserId));
                q.Parameters.AddWithValue("@relationship", Id(relationshipId));
                q.Parameters.AddWithValue("@limit", limit);
                using (SQLiteDataReader r = q.ExecuteReader())
                    while (r.Read()) result.Add(ReadMemory(r));
            }
            return result;
        }

        public bool DeleteMemory(Guid authenticatedUserId, Guid relationshipId, Guid memoryId)
        {
            using (SQLiteConnection c = OpenConnection())
            using (SQLiteCommand q = c.CreateCommand())
            {
                q.CommandText = @"UPDATE AiFriendMemories SET IsDeleted=1
WHERE MemoryID=@memory AND RelationshipID=@relationship AND EXISTS
(SELECT 1 FROM AiFriendRelationships WHERE RelationshipID=@relationship AND UserID=@user) AND IsDeleted=0";
                q.Parameters.AddWithValue("@memory", Id(memoryId));
                q.Parameters.AddWithValue("@relationship", Id(relationshipId));
                q.Parameters.AddWithValue("@user", Id(authenticatedUserId));
                return q.ExecuteNonQuery() == 1;
            }
        }

        private static bool ValidRelationship(OpenSim.Data.AiFriendRelationshipData x)
        {
            return x != null && x.RelationshipId != Guid.Empty && x.UserId != Guid.Empty
                && x.CharacterId != Guid.Empty && x.RelationshipLevel >= 0 && x.RelationshipLevel <= 100
                && x.Version >= 1 && x.FirstMetUtc.Kind == DateTimeKind.Utc
                && (!x.FriendsSinceUtc.HasValue || x.FriendsSinceUtc.Value.Kind == DateTimeKind.Utc)
                && (!x.LastInteractionUtc.HasValue || x.LastInteractionUtc.Value.Kind == DateTimeKind.Utc);
        }

        private static void AddRelationshipParameters(SQLiteCommand q, OpenSim.Data.AiFriendRelationshipData x)
        {
            q.Parameters.AddWithValue("@id", Id(x.RelationshipId));
            q.Parameters.AddWithValue("@user", Id(x.UserId));
            q.Parameters.AddWithValue("@character", Id(x.CharacterId));
            q.Parameters.AddWithValue("@state", x.State);
            q.Parameters.AddWithValue("@level", x.RelationshipLevel);
            q.Parameters.AddWithValue("@first", Date(x.FirstMetUtc));
            q.Parameters.AddWithValue("@friends", NullableDate(x.FriendsSinceUtc));
            q.Parameters.AddWithValue("@last", NullableDate(x.LastInteractionUtc));
            q.Parameters.AddWithValue("@greeting", x.AllowProactiveGreeting ? 1 : 0);
            q.Parameters.AddWithValue("@follow", x.AllowFollow ? 1 : 0);
            q.Parameters.AddWithValue("@voice", x.AllowVoice ? 1 : 0);
            q.Parameters.AddWithValue("@version", x.Version);
        }

        private static OpenSim.Data.AiFriendCharacterData ReadCharacter(IDataRecord r)
        {
            return new OpenSim.Data.AiFriendCharacterData {
                CharacterId = Guid.Parse(r["CharacterID"].ToString()),
                DisplayName = r["DisplayName"].ToString(),
                ProfileDescription = r["ProfileDescription"].ToString(),
                PersonaVersion = Convert.ToInt32(r["PersonaVersion"], CultureInfo.InvariantCulture),
                AppearanceAssetId = Guid.Parse(r["AppearanceAssetID"].ToString()),
                VoiceProfileId = Guid.Parse(r["VoiceProfileID"].ToString()),
                PublisherId = Guid.Parse(r["PublisherID"].ToString()),
                Status = Convert.ToInt32(r["Status"], CultureInfo.InvariantCulture),
                CreatedUtc = ParseDate(r["CreatedUTC"].ToString()),
                UpdatedUtc = ParseDate(r["UpdatedUTC"].ToString())
            };
        }

        private static OpenSim.Data.AiFriendRelationshipData ReadRelationship(IDataRecord r)
        {
            return new OpenSim.Data.AiFriendRelationshipData {
                RelationshipId = Guid.Parse(r["RelationshipID"].ToString()),
                UserId = Guid.Parse(r["UserID"].ToString()),
                CharacterId = Guid.Parse(r["CharacterID"].ToString()),
                State = Convert.ToInt32(r["State"], CultureInfo.InvariantCulture),
                RelationshipLevel = Convert.ToInt32(r["RelationshipLevel"], CultureInfo.InvariantCulture),
                FirstMetUtc = ParseDate(r["FirstMetUTC"].ToString()),
                FriendsSinceUtc = ParseNullableDate(r["FriendsSinceUTC"]),
                LastInteractionUtc = ParseNullableDate(r["LastInteractionUTC"]),
                AllowProactiveGreeting = Convert.ToInt32(r["AllowProactiveGreeting"], CultureInfo.InvariantCulture) != 0,
                AllowFollow = Convert.ToInt32(r["AllowFollow"], CultureInfo.InvariantCulture) != 0,
                AllowVoice = Convert.ToInt32(r["AllowVoice"], CultureInfo.InvariantCulture) != 0,
                Version = Convert.ToInt64(r["Version"], CultureInfo.InvariantCulture)
            };
        }

        private static OpenSim.Data.AiFriendMemoryData ReadMemory(IDataRecord r)
        {
            return new OpenSim.Data.AiFriendMemoryData {
                MemoryId = Guid.Parse(r["MemoryID"].ToString()),
                RelationshipId = Guid.Parse(r["RelationshipID"].ToString()),
                MemoryType = Convert.ToInt32(r["MemoryType"], CultureInfo.InvariantCulture),
                Content = r["Content"].ToString(),
                Confidence = Convert.ToDouble(r["Confidence"], CultureInfo.InvariantCulture),
                SourceConversationId = Guid.Parse(r["SourceConversationID"].ToString()),
                CreatedUtc = ParseDate(r["CreatedUTC"].ToString()),
                LastConfirmedUtc = ParseNullableDate(r["LastConfirmedUTC"])
            };
        }

        private static string Id(Guid id) { return id.ToString("D"); }
        private static string Date(DateTime value) { return value.ToString("o", CultureInfo.InvariantCulture); }
        private static object NullableDate(DateTime? value) { return value.HasValue ? (object)Date(value.Value) : DBNull.Value; }
        private static DateTime ParseDate(string value) { return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(); }
        private static DateTime? ParseNullableDate(object value) { return value == DBNull.Value ? (DateTime?)null : ParseDate(value.ToString()); }
    }
}
