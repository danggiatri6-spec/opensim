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

            _connectionString = connectionString;
            InitializeSchema();
        }

        private SQLiteConnection OpenConnection()
        {
            SQLiteConnection connection = new SQLiteConnection(_connectionString);
            connection.Open();
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA foreign_keys = ON";
                command.ExecuteNonQuery();
            }
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
WHERE ProjectID = @ProjectID AND OwnerID = @OwnerID AND CurrentRevision = @ExpectedRevision AND UpdatedUTC = @ExpectedUpdatedUTC AND Status = 0";
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

            if (revision.Experiences == null
                || (revision.SchemaVersion >= 2 && revision.Experiences.Count == 0)
                || (revision.SchemaVersion < 2 && revision.Experiences.Count != 0))
                return false;

            HashSet<UUID> experienceIDs = new HashSet<UUID>();
            HashSet<int> sortOrders = new HashSet<int>();
            foreach (GameProjectExperienceRevisionData experience in revision.Experiences)
            {
                if (experience == null || experience.ExperienceID == UUID.Zero
                    || experience.ExperienceRevision < 1 || experience.SortOrder < 0
                    || !experienceIDs.Add(experience.ExperienceID) || !sortOrders.Add(experience.SortOrder))
                    return false;
            }

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

                    foreach (GameProjectExperienceRevisionData experience in revision.Experiences)
                    {
                        using (SQLiteCommand link = connection.CreateCommand())
                        {
                            link.Transaction = transaction;
                            link.CommandText = @"INSERT INTO GameProjectRevisionExperiences
(ProjectID, ProjectRevision, ExperienceID, ExperienceRevision, SortOrder)
SELECT @ProjectID, @ProjectRevision, @ExperienceID, @ExperienceRevision, @SortOrder
WHERE EXISTS (
    SELECT 1 FROM GameExperienceRevisions r
    JOIN GameExperiences e ON e.ProjectID = r.ProjectID AND e.ExperienceID = r.ExperienceID
    JOIN GameProjects p ON p.ProjectID = e.ProjectID
    WHERE r.ProjectID = @ProjectID AND r.ExperienceID = @ExperienceID
      AND r.Revision = @ExperienceRevision AND p.OwnerID = @OwnerID AND p.Status = 0
)";
                            link.Parameters.AddWithValue("@ProjectID", revision.ProjectID.ToString());
                            link.Parameters.AddWithValue("@ProjectRevision", revision.Revision);
                            link.Parameters.AddWithValue("@ExperienceID", experience.ExperienceID.ToString());
                            link.Parameters.AddWithValue("@ExperienceRevision", experience.ExperienceRevision);
                            link.Parameters.AddWithValue("@SortOrder", experience.SortOrder);
                            link.Parameters.AddWithValue("@OwnerID", ownerID.ToString());
                            if (link.ExecuteNonQuery() != 1)
                            {
                                transaction.Rollback();
                                return false;
                            }
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
            {
                GameProjectRevisionData result = null;
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT ProjectID, Revision, SchemaVersion, ManifestJson, CreatedBy, CreatedUTC
FROM GameProjectRevisions WHERE ProjectID = @ProjectID AND Revision = @Revision";
                    command.Parameters.AddWithValue("@ProjectID", projectID.ToString());
                    command.Parameters.AddWithValue("@Revision", revision);
                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        if (!reader.Read()) return null;
                        result = new GameProjectRevisionData
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

                using (SQLiteCommand links = connection.CreateCommand())
                {
                    links.CommandText = @"SELECT ExperienceID, ExperienceRevision, SortOrder
FROM GameProjectRevisionExperiences
WHERE ProjectID = @ProjectID AND ProjectRevision = @Revision ORDER BY SortOrder";
                    links.Parameters.AddWithValue("@ProjectID", projectID.ToString());
                    links.Parameters.AddWithValue("@Revision", revision);
                    using (SQLiteDataReader reader = links.ExecuteReader())
                    {
                        while (reader.Read())
                            result.Experiences.Add(new GameProjectExperienceRevisionData
                            {
                                ExperienceID = UUID.Parse(reader["ExperienceID"].ToString()),
                                ExperienceRevision = Convert.ToInt64(reader["ExperienceRevision"], CultureInfo.InvariantCulture),
                                SortOrder = Convert.ToInt32(reader["SortOrder"], CultureInfo.InvariantCulture)
                            });
                    }
                }
                return result;
            }
        }

        public bool PublishProject(UUID projectID, UUID ownerID, long expectedCurrentRevision,
            DateTime expectedUpdatedUTC, DateTime updatedUTC)
        {
            if (projectID == UUID.Zero || ownerID == UUID.Zero || expectedCurrentRevision < 1)
                return false;

            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"UPDATE GameProjects
SET Status = 1, UpdatedUTC = @UpdatedUTC
WHERE ProjectID = @ProjectID AND OwnerID = @OwnerID
  AND Status = 0 AND CurrentRevision = @ExpectedRevision
  AND UpdatedUTC = @ExpectedUpdatedUTC
  AND EXISTS (SELECT 1 FROM GameProjectRevisions
              WHERE GameProjectRevisions.ProjectID = GameProjects.ProjectID
                AND GameProjectRevisions.Revision = GameProjects.CurrentRevision)
  AND (
      (SELECT SchemaVersion FROM GameProjectRevisions
       WHERE GameProjectRevisions.ProjectID = GameProjects.ProjectID
         AND GameProjectRevisions.Revision = GameProjects.CurrentRevision) < 2
      OR EXISTS (SELECT 1 FROM GameProjectRevisionExperiences
                 WHERE GameProjectRevisionExperiences.ProjectID = GameProjects.ProjectID
                   AND GameProjectRevisionExperiences.ProjectRevision = GameProjects.CurrentRevision)
  )";
                command.Parameters.AddWithValue("@UpdatedUTC", FormatDate(updatedUTC));
                command.Parameters.AddWithValue("@ProjectID", projectID.ToString());
                command.Parameters.AddWithValue("@OwnerID", ownerID.ToString());
                command.Parameters.AddWithValue("@ExpectedRevision", expectedCurrentRevision);
                command.Parameters.AddWithValue("@ExpectedUpdatedUTC", FormatDate(expectedUpdatedUTC));
                return command.ExecuteNonQuery() == 1;
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

        public GameExperienceData GetExperience(UUID projectID, UUID experienceID)
        {
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT e.ProjectID, e.ExperienceID, e.CurrentRevision, e.CreatedUTC, e.UpdatedUTC,
COALESCE(r.Name, '') AS Name, COALESCE(r.StartPlaceID, '') AS StartPlaceID
FROM GameExperiences e LEFT JOIN GameExperienceRevisions r
ON r.ProjectID = e.ProjectID AND r.ExperienceID = e.ExperienceID AND r.Revision = e.CurrentRevision
WHERE e.ProjectID = @ProjectID AND e.ExperienceID = @ExperienceID";
                command.Parameters.AddWithValue("@ProjectID", projectID.ToString());
                command.Parameters.AddWithValue("@ExperienceID", experienceID.ToString());
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    if (!reader.Read()) return null;
                    return new GameExperienceData {
                        ProjectID = UUID.Parse(reader["ProjectID"].ToString()),
                        ExperienceID = UUID.Parse(reader["ExperienceID"].ToString()),
                        CurrentRevision = Convert.ToInt64(reader["CurrentRevision"], CultureInfo.InvariantCulture),
                        Name = reader["Name"].ToString(),
                        StartPlaceID = string.IsNullOrEmpty(reader["StartPlaceID"].ToString()) ? UUID.Zero : UUID.Parse(reader["StartPlaceID"].ToString()),
                        CreatedUTC = ParseDate(reader["CreatedUTC"].ToString()), UpdatedUTC = ParseDate(reader["UpdatedUTC"].ToString())
                    };
                }
            }
        }

        public List<GameExperienceData> GetExperiences(UUID projectID, UUID ownerID)
        {
            List<GameExperienceData> result = new List<GameExperienceData>();
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT e.ProjectID, e.ExperienceID, e.CurrentRevision, e.CreatedUTC, e.UpdatedUTC,
COALESCE(r.Name, '') AS Name, COALESCE(r.StartPlaceID, '') AS StartPlaceID
FROM GameExperiences e JOIN GameProjects p ON p.ProjectID = e.ProjectID
LEFT JOIN GameExperienceRevisions r ON r.ProjectID = e.ProjectID AND r.ExperienceID = e.ExperienceID AND r.Revision = e.CurrentRevision
WHERE e.ProjectID = @ProjectID AND p.OwnerID = @OwnerID ORDER BY e.CreatedUTC, e.ExperienceID";
                command.Parameters.AddWithValue("@ProjectID", projectID.ToString());
                command.Parameters.AddWithValue("@OwnerID", ownerID.ToString());
                using (SQLiteDataReader reader = command.ExecuteReader())
                    while (reader.Read()) result.Add(new GameExperienceData {
                        ProjectID = UUID.Parse(reader["ProjectID"].ToString()), ExperienceID = UUID.Parse(reader["ExperienceID"].ToString()),
                        CurrentRevision = Convert.ToInt64(reader["CurrentRevision"], CultureInfo.InvariantCulture), Name = reader["Name"].ToString(),
                        StartPlaceID = string.IsNullOrEmpty(reader["StartPlaceID"].ToString()) ? UUID.Zero : UUID.Parse(reader["StartPlaceID"].ToString()),
                        CreatedUTC = ParseDate(reader["CreatedUTC"].ToString()), UpdatedUTC = ParseDate(reader["UpdatedUTC"].ToString())
                    });
            }
            return result;
        }

        public bool CreateExperience(GameExperienceData experience, UUID ownerID)
        {
            if (experience == null || experience.ProjectID == UUID.Zero || experience.ExperienceID == UUID.Zero || ownerID == UUID.Zero) return false;
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"INSERT INTO GameExperiences (ProjectID, ExperienceID, CreatedUTC, UpdatedUTC)
SELECT @ProjectID, @ExperienceID, @CreatedUTC, @UpdatedUTC WHERE EXISTS
(SELECT 1 FROM GameProjects WHERE ProjectID=@ProjectID AND OwnerID=@OwnerID AND Status=0)";
                command.Parameters.AddWithValue("@ProjectID", experience.ProjectID.ToString());
                command.Parameters.AddWithValue("@ExperienceID", experience.ExperienceID.ToString());
                command.Parameters.AddWithValue("@OwnerID", ownerID.ToString());
                command.Parameters.AddWithValue("@CreatedUTC", FormatDate(experience.CreatedUTC));
                command.Parameters.AddWithValue("@UpdatedUTC", FormatDate(experience.UpdatedUTC));
                return command.ExecuteNonQuery() == 1;
            }
        }

        public GamePlaceData GetPlace(UUID projectID, UUID experienceID, UUID placeID)
        {
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT p.ProjectID, p.ExperienceID, p.PlaceID, p.CurrentRevision, p.CreatedUTC, p.UpdatedUTC,
COALESCE(r.Name, '') AS Name, COALESCE(r.Description, '') AS Description
FROM GamePlaces p LEFT JOIN GamePlaceRevisions r ON r.ProjectID=p.ProjectID AND r.ExperienceID=p.ExperienceID
AND r.PlaceID=p.PlaceID AND r.Revision=p.CurrentRevision
WHERE p.ProjectID=@ProjectID AND p.ExperienceID=@ExperienceID AND p.PlaceID=@PlaceID";
                command.Parameters.AddWithValue("@ProjectID", projectID.ToString());
                command.Parameters.AddWithValue("@ExperienceID", experienceID.ToString());
                command.Parameters.AddWithValue("@PlaceID", placeID.ToString());
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    if (!reader.Read()) return null;
                    return new GamePlaceData { ProjectID=UUID.Parse(reader["ProjectID"].ToString()),
                        ExperienceID=UUID.Parse(reader["ExperienceID"].ToString()), PlaceID=UUID.Parse(reader["PlaceID"].ToString()),
                        CurrentRevision=Convert.ToInt64(reader["CurrentRevision"], CultureInfo.InvariantCulture),
                        Name=reader["Name"].ToString(), Description=reader["Description"].ToString(),
                        CreatedUTC=ParseDate(reader["CreatedUTC"].ToString()), UpdatedUTC=ParseDate(reader["UpdatedUTC"].ToString()) };
                }
            }
        }

        public List<GamePlaceData> GetPlaces(UUID projectID, UUID experienceID, UUID ownerID)
        {
            List<GamePlaceData> result = new List<GamePlaceData>();
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT p.ProjectID, p.ExperienceID, p.PlaceID, p.CurrentRevision, p.CreatedUTC, p.UpdatedUTC,
COALESCE(r.Name, '') AS Name, COALESCE(r.Description, '') AS Description
FROM GamePlaces p JOIN GameProjects g ON g.ProjectID=p.ProjectID
LEFT JOIN GamePlaceRevisions r ON r.ProjectID=p.ProjectID AND r.ExperienceID=p.ExperienceID AND r.PlaceID=p.PlaceID AND r.Revision=p.CurrentRevision
WHERE p.ProjectID=@ProjectID AND p.ExperienceID=@ExperienceID AND g.OwnerID=@OwnerID ORDER BY p.CreatedUTC, p.PlaceID";
                command.Parameters.AddWithValue("@ProjectID", projectID.ToString());
                command.Parameters.AddWithValue("@ExperienceID", experienceID.ToString());
                command.Parameters.AddWithValue("@OwnerID", ownerID.ToString());
                using (SQLiteDataReader reader = command.ExecuteReader()) while (reader.Read()) result.Add(new GamePlaceData {
                    ProjectID=UUID.Parse(reader["ProjectID"].ToString()), ExperienceID=UUID.Parse(reader["ExperienceID"].ToString()),
                    PlaceID=UUID.Parse(reader["PlaceID"].ToString()), CurrentRevision=Convert.ToInt64(reader["CurrentRevision"], CultureInfo.InvariantCulture),
                    Name=reader["Name"].ToString(), Description=reader["Description"].ToString(),
                    CreatedUTC=ParseDate(reader["CreatedUTC"].ToString()), UpdatedUTC=ParseDate(reader["UpdatedUTC"].ToString()) });
            }
            return result;
        }

        public bool CreatePlace(GamePlaceData place, UUID ownerID)
        {
            if (place == null || place.ProjectID == UUID.Zero || place.ExperienceID == UUID.Zero || place.PlaceID == UUID.Zero || ownerID == UUID.Zero) return false;
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"INSERT INTO GamePlaces (ProjectID, ExperienceID, PlaceID, CreatedUTC, UpdatedUTC)
SELECT @ProjectID, @ExperienceID, @PlaceID, @CreatedUTC, @UpdatedUTC WHERE EXISTS
(SELECT 1 FROM GameExperiences e JOIN GameProjects p ON p.ProjectID=e.ProjectID
WHERE e.ProjectID=@ProjectID AND e.ExperienceID=@ExperienceID AND p.OwnerID=@OwnerID AND p.Status=0)";
                command.Parameters.AddWithValue("@ProjectID", place.ProjectID.ToString());
                command.Parameters.AddWithValue("@ExperienceID", place.ExperienceID.ToString());
                command.Parameters.AddWithValue("@PlaceID", place.PlaceID.ToString());
                command.Parameters.AddWithValue("@CreatedUTC", FormatDate(place.CreatedUTC));
                command.Parameters.AddWithValue("@UpdatedUTC", FormatDate(place.UpdatedUTC));
                command.Parameters.AddWithValue("@OwnerID", ownerID.ToString());
                return command.ExecuteNonQuery() == 1;
            }
        }

        public bool SavePlaceRevision(GamePlaceRevisionData revision, UUID ownerID, long expectedCurrentRevision, DateTime updatedUTC)
        {
            if (revision == null || revision.ProjectID == UUID.Zero || revision.ExperienceID == UUID.Zero || revision.PlaceID == UUID.Zero
                || revision.CreatedBy != ownerID || revision.Revision != expectedCurrentRevision + 1 || string.IsNullOrEmpty(revision.DefinitionJson)) return false;
            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteTransaction tx = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                try
                {
                    using (SQLiteCommand insert = connection.CreateCommand())
                    {
                        insert.Transaction=tx; insert.CommandText = @"INSERT INTO GamePlaceRevisions
(ProjectID,ExperienceID,PlaceID,Revision,SchemaVersion,Name,Description,DefinitionJson,CreatedBy,CreatedUTC)
SELECT @ProjectID,@ExperienceID,@PlaceID,@Revision,@SchemaVersion,@Name,@Description,@DefinitionJson,@CreatedBy,@CreatedUTC
WHERE EXISTS (SELECT 1 FROM GamePlaces p JOIN GameProjects g ON g.ProjectID=p.ProjectID
WHERE p.ProjectID=@ProjectID AND p.ExperienceID=@ExperienceID AND p.PlaceID=@PlaceID AND p.CurrentRevision=@ExpectedRevision
AND g.OwnerID=@OwnerID AND g.Status=0)";
                        insert.Parameters.AddWithValue("@ProjectID",revision.ProjectID.ToString()); insert.Parameters.AddWithValue("@ExperienceID",revision.ExperienceID.ToString());
                        insert.Parameters.AddWithValue("@PlaceID",revision.PlaceID.ToString()); insert.Parameters.AddWithValue("@Revision",revision.Revision);
                        insert.Parameters.AddWithValue("@SchemaVersion",revision.SchemaVersion); insert.Parameters.AddWithValue("@Name",revision.Name??string.Empty);
                        insert.Parameters.AddWithValue("@Description",revision.Description??string.Empty); insert.Parameters.AddWithValue("@DefinitionJson",revision.DefinitionJson);
                        insert.Parameters.AddWithValue("@CreatedBy",revision.CreatedBy.ToString()); insert.Parameters.AddWithValue("@CreatedUTC",FormatDate(revision.CreatedUTC));
                        insert.Parameters.AddWithValue("@ExpectedRevision",expectedCurrentRevision); insert.Parameters.AddWithValue("@OwnerID",ownerID.ToString());
                        if(insert.ExecuteNonQuery()!=1){tx.Rollback();return false;}
                    }
                    using (SQLiteCommand update=connection.CreateCommand())
                    {
                        update.Transaction=tx; update.CommandText="UPDATE GamePlaces SET CurrentRevision=@Revision, UpdatedUTC=@UpdatedUTC WHERE ProjectID=@ProjectID AND ExperienceID=@ExperienceID AND PlaceID=@PlaceID AND CurrentRevision=@ExpectedRevision";
                        update.Parameters.AddWithValue("@Revision",revision.Revision); update.Parameters.AddWithValue("@UpdatedUTC",FormatDate(updatedUTC));
                        update.Parameters.AddWithValue("@ProjectID",revision.ProjectID.ToString()); update.Parameters.AddWithValue("@ExperienceID",revision.ExperienceID.ToString());
                        update.Parameters.AddWithValue("@PlaceID",revision.PlaceID.ToString()); update.Parameters.AddWithValue("@ExpectedRevision",expectedCurrentRevision);
                        if(update.ExecuteNonQuery()!=1){tx.Rollback();return false;}
                    }
                    tx.Commit(); return true;
                } catch { try{tx.Rollback();}catch{} throw; }
            }
        }

        public GamePlaceRevisionData GetPlaceRevision(UUID projectID, UUID experienceID, UUID placeID, long revision)
        {
            using (SQLiteConnection connection=OpenConnection()) using (SQLiteCommand command=connection.CreateCommand())
            {
                command.CommandText="SELECT * FROM GamePlaceRevisions WHERE ProjectID=@ProjectID AND ExperienceID=@ExperienceID AND PlaceID=@PlaceID AND Revision=@Revision";
                command.Parameters.AddWithValue("@ProjectID",projectID.ToString()); command.Parameters.AddWithValue("@ExperienceID",experienceID.ToString());
                command.Parameters.AddWithValue("@PlaceID",placeID.ToString()); command.Parameters.AddWithValue("@Revision",revision);
                using(SQLiteDataReader reader=command.ExecuteReader()){if(!reader.Read())return null; return new GamePlaceRevisionData{
                    ProjectID=UUID.Parse(reader["ProjectID"].ToString()),ExperienceID=UUID.Parse(reader["ExperienceID"].ToString()),PlaceID=UUID.Parse(reader["PlaceID"].ToString()),
                    Revision=Convert.ToInt64(reader["Revision"],CultureInfo.InvariantCulture),SchemaVersion=Convert.ToInt32(reader["SchemaVersion"],CultureInfo.InvariantCulture),
                    Name=reader["Name"].ToString(),Description=reader["Description"].ToString(),DefinitionJson=reader["DefinitionJson"].ToString(),
                    CreatedBy=UUID.Parse(reader["CreatedBy"].ToString()),CreatedUTC=ParseDate(reader["CreatedUTC"].ToString())};}
            }
        }

        public bool SaveExperienceRevision(GameExperienceRevisionData revision, UUID ownerID, long expectedCurrentRevision, DateTime updatedUTC)
        {
            if(revision==null || revision.ProjectID==UUID.Zero || revision.ExperienceID==UUID.Zero || revision.CreatedBy!=ownerID
                || revision.Revision!=expectedCurrentRevision+1 || revision.Places==null || revision.Places.Count==0 || revision.StartPlaceID==UUID.Zero) return false;
            var ids=new HashSet<UUID>(); var orders=new HashSet<int>(); bool hasStart=false;
            foreach(var place in revision.Places){if(place==null || place.PlaceID==UUID.Zero || place.PlaceRevision<1 || place.SortOrder<0 || !ids.Add(place.PlaceID) || !orders.Add(place.SortOrder))return false; if(place.PlaceID==revision.StartPlaceID)hasStart=true;}
            if(!hasStart)return false;
            using(SQLiteConnection connection=OpenConnection()) using(SQLiteTransaction tx=connection.BeginTransaction(IsolationLevel.Serializable))
            {
                try
                {
                    using(SQLiteCommand insert=connection.CreateCommand()){
                        insert.Transaction=tx; insert.CommandText=@"INSERT INTO GameExperienceRevisions (ProjectID,ExperienceID,Revision,SchemaVersion,Name,StartPlaceID,CreatedBy,CreatedUTC)
SELECT @ProjectID,@ExperienceID,@Revision,@SchemaVersion,@Name,@StartPlaceID,@CreatedBy,@CreatedUTC
WHERE EXISTS(SELECT 1 FROM GameExperiences e JOIN GameProjects p ON p.ProjectID=e.ProjectID WHERE e.ProjectID=@ProjectID AND e.ExperienceID=@ExperienceID
AND e.CurrentRevision=@ExpectedRevision AND p.OwnerID=@OwnerID AND p.Status=0)";
                        insert.Parameters.AddWithValue("@ProjectID",revision.ProjectID.ToString());insert.Parameters.AddWithValue("@ExperienceID",revision.ExperienceID.ToString());
                        insert.Parameters.AddWithValue("@Revision",revision.Revision);insert.Parameters.AddWithValue("@SchemaVersion",revision.SchemaVersion);insert.Parameters.AddWithValue("@Name",revision.Name??string.Empty);
                        insert.Parameters.AddWithValue("@StartPlaceID",revision.StartPlaceID.ToString());insert.Parameters.AddWithValue("@CreatedBy",revision.CreatedBy.ToString());
                        insert.Parameters.AddWithValue("@CreatedUTC",FormatDate(revision.CreatedUTC));insert.Parameters.AddWithValue("@ExpectedRevision",expectedCurrentRevision);insert.Parameters.AddWithValue("@OwnerID",ownerID.ToString());
                        if(insert.ExecuteNonQuery()!=1){tx.Rollback();return false;}}
                    foreach(var place in revision.Places){using(SQLiteCommand member=connection.CreateCommand()){
                        member.Transaction=tx;member.CommandText=@"INSERT INTO GameExperienceRevisionPlaces (ProjectID,ExperienceID,ExperienceRevision,PlaceID,PlaceRevision,SortOrder)
SELECT @ProjectID,@ExperienceID,@Revision,@PlaceID,@PlaceRevision,@SortOrder WHERE EXISTS
(SELECT 1 FROM GamePlaceRevisions r JOIN GamePlaces p ON p.ProjectID=r.ProjectID AND p.ExperienceID=r.ExperienceID AND p.PlaceID=r.PlaceID
JOIN GameProjects g ON g.ProjectID=r.ProjectID WHERE r.ProjectID=@ProjectID AND r.ExperienceID=@ExperienceID AND r.PlaceID=@PlaceID AND r.Revision=@PlaceRevision
AND p.CurrentRevision>=@PlaceRevision AND g.OwnerID=@OwnerID AND g.Status=0)";
                        member.Parameters.AddWithValue("@ProjectID",revision.ProjectID.ToString());member.Parameters.AddWithValue("@ExperienceID",revision.ExperienceID.ToString());
                        member.Parameters.AddWithValue("@Revision",revision.Revision);member.Parameters.AddWithValue("@PlaceID",place.PlaceID.ToString());
                        member.Parameters.AddWithValue("@PlaceRevision",place.PlaceRevision);member.Parameters.AddWithValue("@SortOrder",place.SortOrder);member.Parameters.AddWithValue("@OwnerID",ownerID.ToString());
                        if(member.ExecuteNonQuery()!=1){tx.Rollback();return false;}}}
                    using(SQLiteCommand update=connection.CreateCommand()){update.Transaction=tx;update.CommandText="UPDATE GameExperiences SET CurrentRevision=@Revision,UpdatedUTC=@UpdatedUTC WHERE ProjectID=@ProjectID AND ExperienceID=@ExperienceID AND CurrentRevision=@ExpectedRevision";
                        update.Parameters.AddWithValue("@Revision",revision.Revision);update.Parameters.AddWithValue("@UpdatedUTC",FormatDate(updatedUTC));update.Parameters.AddWithValue("@ProjectID",revision.ProjectID.ToString());
                        update.Parameters.AddWithValue("@ExperienceID",revision.ExperienceID.ToString());update.Parameters.AddWithValue("@ExpectedRevision",expectedCurrentRevision);if(update.ExecuteNonQuery()!=1){tx.Rollback();return false;}}
                    tx.Commit();return true;
                }catch{try{tx.Rollback();}catch{}throw;}
            }
        }

        public GameExperienceRevisionData GetExperienceRevision(UUID projectID, UUID experienceID, long revision)
        {
            using(SQLiteConnection connection=OpenConnection()) using(SQLiteCommand command=connection.CreateCommand()){
                command.CommandText="SELECT * FROM GameExperienceRevisions WHERE ProjectID=@ProjectID AND ExperienceID=@ExperienceID AND Revision=@Revision";
                command.Parameters.AddWithValue("@ProjectID",projectID.ToString());command.Parameters.AddWithValue("@ExperienceID",experienceID.ToString());command.Parameters.AddWithValue("@Revision",revision);
                GameExperienceRevisionData result=null;using(SQLiteDataReader reader=command.ExecuteReader()){if(!reader.Read())return null;result=new GameExperienceRevisionData{
                    ProjectID=UUID.Parse(reader["ProjectID"].ToString()),ExperienceID=UUID.Parse(reader["ExperienceID"].ToString()),Revision=Convert.ToInt64(reader["Revision"],CultureInfo.InvariantCulture),
                    SchemaVersion=Convert.ToInt32(reader["SchemaVersion"],CultureInfo.InvariantCulture),Name=reader["Name"].ToString(),StartPlaceID=UUID.Parse(reader["StartPlaceID"].ToString()),
                    CreatedBy=UUID.Parse(reader["CreatedBy"].ToString()),CreatedUTC=ParseDate(reader["CreatedUTC"].ToString())};}
                using(SQLiteCommand members=connection.CreateCommand()){members.CommandText="SELECT PlaceID,PlaceRevision,SortOrder FROM GameExperienceRevisionPlaces WHERE ProjectID=@ProjectID AND ExperienceID=@ExperienceID AND ExperienceRevision=@Revision ORDER BY SortOrder";
                    members.Parameters.AddWithValue("@ProjectID",projectID.ToString());members.Parameters.AddWithValue("@ExperienceID",experienceID.ToString());members.Parameters.AddWithValue("@Revision",revision);
                    using(SQLiteDataReader reader=members.ExecuteReader())while(reader.Read())result.Places.Add(new GameExperiencePlaceRevisionData{PlaceID=UUID.Parse(reader["PlaceID"].ToString()),
                        PlaceRevision=Convert.ToInt64(reader["PlaceRevision"],CultureInfo.InvariantCulture),SortOrder=Convert.ToInt32(reader["SortOrder"],CultureInfo.InvariantCulture)});}
                return result;
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