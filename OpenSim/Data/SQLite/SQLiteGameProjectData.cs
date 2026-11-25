/*
 * OpenSim-next Game Platform SQLite storage provider.
 * Additive storage; does not alter existing OpenSim tables.
 */
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Globalization;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Data;

namespace OpenSim.Data.SQLite
{
    /// <summary>
    /// SQLite persistence for creator-owned game projects and immutable revisions.
    /// Uses a private connection per operation and parameterized SQL.
    /// </summary>
    public sealed class SQLiteGameProjectData : IGameProjectData
    {
        private readonly string _connectionString;
        private readonly object _migrationLock = new object();

        public SQLiteGameProjectData(string connectionString)
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
            return connection;
        }

        private void InitializeSchema()
        {
            lock (_migrationLock)
            {
                using (SQLiteConnection connection = OpenConnection())
                {
                    Migration migration = new Migration(connection, GetType().Assembly, "GameProjects");
                    migration.Update();
                }
            }
        }

        public GameProjectData GetProject(UUID projectID)
        {
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT ProjectID, OwnerID, Name, Description, Status, SchemaVersion, CurrentRevision, CreatedUTC, UpdatedUTC FROM GameProjects WHERE ProjectID = @ProjectID";
                command.Parameters.AddWithValue("@ProjectID", projectID.ToString());
                using (SQLiteDataReader reader = command.ExecuteReader())
                    return reader.Read() ? ReadProject(reader) : null;
            }
        }

        public List<GameProjectData> GetProjectsByOwner(UUID ownerID, int offset, int limit)
        {
            if (offset < 0) offset = 0;
            if (limit < 1) limit = 1;
            if (limit > 100) limit = 100;

            List<GameProjectData> projects = new List<GameProjectData>();
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT ProjectID, OwnerID, Name, Description, Status, SchemaVersion, CurrentRevision, CreatedUTC, UpdatedUTC FROM GameProjects WHERE OwnerID = @OwnerID AND Status <> 2 ORDER BY UpdatedUTC DESC, ProjectID LIMIT @Limit OFFSET @Offset";
                command.Parameters.AddWithValue("@OwnerID", ownerID.ToString());
                command.Parameters.AddWithValue("@Limit", limit);
                command.Parameters.AddWithValue("@Offset", offset);
                using (SQLiteDataReader reader = command.ExecuteReader())
                    while (reader.Read()) projects.Add(ReadProject(reader));
            }
            return projects;
        }

        public bool CreateProject(GameProjectData project)
        {
            if (project == null || project.ProjectID == UUID.Zero || project.OwnerID == UUID.Zero)
                return false;

            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"INSERT INTO GameProjects
(ProjectID, OwnerID, Name, Description, Status, SchemaVersion, CurrentRevision, CreatedUTC, UpdatedUTC)
VALUES (@ProjectID, @OwnerID, @Name, @Description, @Status, @SchemaVersion, @CurrentRevision, @CreatedUTC, @UpdatedUTC)";
                AddProjectParameters(command, project);
                return command.ExecuteNonQuery() == 1;
            }
        }

        public bool UpdateProjectMetadata(UUID projectID, UUID ownerID, long expectedCurrentRevision,
            DateTime expectedUpdatedUTC, string name, string description, DateTime updatedUTC)
        {
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"UPDATE GameProjects SET Name = @Name, Description = @Description, UpdatedUTC = @UpdatedUTC
WHERE ProjectID = @ProjectID AND OwnerID = @OwnerID AND CurrentRevision = @ExpectedRevision AND UpdatedUTC = @ExpectedUpdatedUTC AND Status <> 2";
                command.Parameters.AddWithValue("@Name", name ?? string.Empty);
                command.Parameters.AddWithValue("@Description", description ?? string.Empty);
                command.Parameters.AddWithValue("@UpdatedUTC", FormatDate(updatedUTC));
                command.Parameters.AddWithValue("@ProjectID", projectID.ToString());
                command.Parameters.AddWithValue("@OwnerID", ownerID.ToString());
                command.Parameters.AddWithValue("@ExpectedRevision", expectedCurrentRevision);
                command.Parameters.AddWithValue("@ExpectedUpdatedUTC", FormatDate(expectedUpdatedUTC));
                return command.ExecuteNonQuery() == 1;
            }
        }

        public bool SaveRevision(GameProjectRevisionData revision, UUID ownerID,
            long expectedCurrentRevision, DateTime updatedUTC)
        {
            if (revision == null || revision.ProjectID == UUID.Zero || revision.CreatedBy != ownerID
                || revision.Revision != expectedCurrentRevision + 1
                || string.IsNullOrEmpty(revision.ManifestJson))
                return false;

            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                try
                {
                    using (SQLiteCommand insert = connection.CreateCommand())
                    {
                        insert.Transaction = transaction;
                        insert.CommandText = @"INSERT INTO GameProjectRevisions
(ProjectID, Revision, SchemaVersion, ManifestJson, CreatedBy, CreatedUTC)
SELECT @ProjectID, @Revision, @SchemaVersion, @ManifestJson, @CreatedBy, @CreatedUTC
WHERE EXISTS (SELECT 1 FROM GameProjects WHERE ProjectID = @ProjectID AND OwnerID = @OwnerID AND CurrentRevision = @ExpectedRevision AND Status = 0)";
                        insert.Parameters.AddWithValue("@ProjectID", revision.ProjectID.ToString());
                        insert.Parameters.AddWithValue("@Revision", revision.Revision);
                        insert.Parameters.AddWithValue("@SchemaVersion", revision.SchemaVersion);
                        insert.Parameters.AddWithValue("@ManifestJson", revision.ManifestJson);
                        insert.Parameters.AddWithValue("@CreatedBy", revision.CreatedBy.ToString());
                        insert.Parameters.AddWithValue("@CreatedUTC", FormatDate(revision.CreatedUTC));
                        insert.Parameters.AddWithValue("@OwnerID", ownerID.ToString());
                        insert.Parameters.AddWithValue("@ExpectedRevision", expectedCurrentRevision);
                        if (insert.ExecuteNonQuery() != 1)
                        {
                            transaction.Rollback();
                            return false;
                        }
                    }

                    using (SQLiteCommand update = connection.CreateCommand())
                    {
                        update.Transaction = transaction;
                        update.CommandText = @"UPDATE GameProjects SET CurrentRevision = @NewRevision, UpdatedUTC = @UpdatedUTC
WHERE ProjectID = @ProjectID AND OwnerID = @OwnerID AND CurrentRevision = @ExpectedRevision AND Status = 0";
                        update.Parameters.AddWithValue("@NewRevision", revision.Revision);
                        update.Parameters.AddWithValue("@UpdatedUTC", FormatDate(updatedUTC));
                        update.Parameters.AddWithValue("@ProjectID", revision.ProjectID.ToString());
                        update.Parameters.AddWithValue("@OwnerID", ownerID.ToString());
                        update.Parameters.AddWithValue("@ExpectedRevision", expectedCurrentRevision);
                        if (update.ExecuteNonQuery() != 1)
                        {
                            transaction.Rollback();
                            return false;
                        }
                    }

                    transaction.Commit();
                    return true;
                }
                catch
                {
                    try { transaction.Rollback(); } catch { }
                    throw;
                }
            }
        }

        public GameProjectRevisionData GetRevision(UUID projectID, long revision)
        {
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT ProjectID, Revision, SchemaVersion, ManifestJson, CreatedBy, CreatedUTC
FROM GameProjectRevisions WHERE ProjectID = @ProjectID AND Revision = @Revision";
                command.Parameters.AddWithValue("@ProjectID", projectID.ToString());
                command.Parameters.AddWithValue("@Revision", revision);
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    if (!reader.Read()) return null;
                    return new GameProjectRevisionData
                    {
                        ProjectID = UUID.Parse(reader["ProjectID"].ToString()),
                        Revision = Convert.ToInt64(reader["Revision"], CultureInfo.InvariantCulture),
                        SchemaVersion = Convert.ToInt32(reader["SchemaVersion"], CultureInfo.InvariantCulture),
                        ManifestJson = reader["ManifestJson"].ToString(),
                        CreatedBy = UUID.Parse(reader["CreatedBy"].ToString()),
                        CreatedUTC = ParseDate(reader["CreatedUTC"].ToString())
                    };
                }
            }
        }

        public bool ArchiveProject(UUID projectID, UUID ownerID, DateTime updatedUTC)
        {
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE GameProjects SET Status = 2, UpdatedUTC = @UpdatedUTC WHERE ProjectID = @ProjectID AND OwnerID = @OwnerID AND Status <> 2";
                command.Parameters.AddWithValue("@UpdatedUTC", FormatDate(updatedUTC));
                command.Parameters.AddWithValue("@ProjectID", projectID.ToString());
                command.Parameters.AddWithValue("@OwnerID", ownerID.ToString());
                return command.ExecuteNonQuery() == 1;
            }
        }

        private static void AddProjectParameters(SQLiteCommand command, GameProjectData project)
        {
            command.Parameters.AddWithValue("@ProjectID", project.ProjectID.ToString());
            command.Parameters.AddWithValue("@OwnerID", project.OwnerID.ToString());
            command.Parameters.AddWithValue("@Name", project.Name ?? string.Empty);
            command.Parameters.AddWithValue("@Description", project.Description ?? string.Empty);
            command.Parameters.AddWithValue("@Status", project.Status);
            command.Parameters.AddWithValue("@SchemaVersion", project.SchemaVersion);
            command.Parameters.AddWithValue("@CurrentRevision", project.CurrentRevision);
            command.Parameters.AddWithValue("@CreatedUTC", FormatDate(project.CreatedUTC));
            command.Parameters.AddWithValue("@UpdatedUTC", FormatDate(project.UpdatedUTC));
        }

        private static GameProjectData ReadProject(IDataRecord reader)
        {
            return new GameProjectData
            {
                ProjectID = UUID.Parse(reader["ProjectID"].ToString()),
                OwnerID = UUID.Parse(reader["OwnerID"].ToString()),
                Name = reader["Name"].ToString(),
                Description = reader["Description"].ToString(),
                Status = Convert.ToInt32(reader["Status"], CultureInfo.InvariantCulture),
                SchemaVersion = Convert.ToInt32(reader["SchemaVersion"], CultureInfo.InvariantCulture),
                CurrentRevision = Convert.ToInt64(reader["CurrentRevision"], CultureInfo.InvariantCulture),
                CreatedUTC = ParseDate(reader["CreatedUTC"].ToString()),
                UpdatedUTC = ParseDate(reader["UpdatedUTC"].ToString())
            };
        }

        private static string FormatDate(DateTime value)
        {
            return value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        }

        private static DateTime ParseDate(string value)
        {
            return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
        }
    }
}