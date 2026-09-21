using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenLume.Core.Abstractions;
using OpenLume.Core.Domain;

namespace OpenLume.Infrastructure.Catalog;

public sealed class SqlitePhotoCatalog : IPhotoCatalog
{
    private const int CurrentSchemaVersion = 2;
    private const int MaximumPageSize = 10_000;
    private const string SelectColumns = """
        SELECT p.id, p.original_path, p.file_name, p.extension, p.file_size, p.imported_at, p.captured_at,
               p.width, p.height, p.rating, p.pick_state, p.edit_json, p.ai_summary,
               p.source_last_write_ticks, p.metadata_state
        FROM photos p
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;

    public SqlitePhotoCatalog(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Database path is required.", nameof(databasePath));
        }

        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Database path must have a parent directory.", nameof(databasePath)));
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        var schemaVersion = await GetSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (schemaVersion > CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Catalog schema {schemaVersion} is newer than this build supports ({CurrentSchemaVersion}).");
        }

        if (schemaVersion == 0)
        {
            await CreateCurrentSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        else if (schemaVersion == 1)
        {
            await MigrateVersionOneAsync(connection, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<ImportResult> ImportFolderAsync(
        string folder,
        bool includeSubfolders,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(folder))
        {
            return new ImportResult(0, 0, 1, [$"Folder does not exist: {folder}"]);
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var imported = 0;
        var alreadyPresent = 0;
        var failed = 0;
        var errors = new List<string>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubfolders,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*", options).Where(SupportedPhotoFormats.IsSupported))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var fullPath = Path.GetFullPath(path);
                    var file = new FileInfo(fullPath);
                    await using var command = connection.CreateCommand();
                    command.CommandText = """
                        INSERT INTO photos(
                            id, original_path, directory_path, file_name, extension, file_size,
                            imported_at, edit_json, source_last_write_ticks, metadata_state)
                        VALUES($id, $path, $directory, $name, $extension, $size, $importedAt, $edit, $lastWrite, $state)
                        ON CONFLICT(original_path) DO NOTHING;
                        """;
                    command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                    command.Parameters.AddWithValue("$path", fullPath);
                    command.Parameters.AddWithValue("$directory", file.DirectoryName ?? string.Empty);
                    command.Parameters.AddWithValue("$name", file.Name);
                    command.Parameters.AddWithValue("$extension", file.Extension.ToLowerInvariant());
                    command.Parameters.AddWithValue("$size", file.Length);
                    command.Parameters.AddWithValue("$importedAt", DateTimeOffset.UtcNow.ToString("O"));
                    command.Parameters.AddWithValue("$edit", JsonSerializer.Serialize(EditRecipe.Default, JsonOptions));
                    command.Parameters.AddWithValue("$lastWrite", file.LastWriteTimeUtc.Ticks);
                    command.Parameters.AddWithValue("$state", (int)MetadataIndexState.Pending);
                    if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
                    {
                        imported++;
                    }
                    else
                    {
                        alreadyPresent++;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
                {
                    failed++;
                    errors.Add($"{Path.GetFileName(path)}: {exception.Message}");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failed++;
            errors.Add($"Unable to enumerate part of the selected folder: {exception.Message}");
        }

        return new ImportResult(imported, alreadyPresent, failed, errors);
    }

    public Task<IReadOnlyList<PhotoAsset>> GetPhotosAsync(CancellationToken cancellationToken = default) =>
        QueryAsync(new CatalogFilter(Limit: MaximumPageSize), cancellationToken);

    public async Task<PhotoAsset?> GetPhotoAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE p.id=$id";
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPhoto(reader) : null;
    }

    public Task UpdateEditAsync(Guid id, EditRecipe edit, CancellationToken cancellationToken = default) =>
        ExecuteUpdateAsync(id, "UPDATE photos SET edit_json=$value WHERE id=$id",
            command => command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(edit.Normalize(), JsonOptions)),
            cancellationToken);

    public Task UpdateRatingAsync(Guid id, int rating, CancellationToken cancellationToken = default) =>
        ExecuteUpdateAsync(id, "UPDATE photos SET rating=$value WHERE id=$id",
            command => command.Parameters.AddWithValue("$value", Math.Clamp(rating, 0, 5)), cancellationToken);

    public Task UpdatePickStateAsync(Guid id, PickState state, CancellationToken cancellationToken = default) =>
        ExecuteUpdateAsync(id, "UPDATE photos SET pick_state=$value WHERE id=$id",
            command => command.Parameters.AddWithValue("$value", (int)state), cancellationToken);

    public Task UpdateAnalysisAsync(Guid id, PhotoAnalysis analysis, CancellationToken cancellationToken = default) =>
        ExecuteUpdateAsync(id, """
            UPDATE photos SET ai_summary=$summary, ai_technical=$technical, ai_aesthetic=$aesthetic,
                ai_suggested_pick=$suggestedPick, ai_tags_json=$tags, ai_edit_json=$edit
            WHERE id=$id
            """, command =>
        {
            command.Parameters.AddWithValue("$summary", analysis.Summary);
            command.Parameters.AddWithValue("$technical", Math.Clamp(analysis.TechnicalScore, 0, 1));
            command.Parameters.AddWithValue("$aesthetic", Math.Clamp(analysis.AestheticScore, 0, 1));
            command.Parameters.AddWithValue("$suggestedPick", analysis.SuggestedPick ? 1 : 0);
            command.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(analysis.Tags, JsonOptions));
            command.Parameters.AddWithValue("$edit", JsonSerializer.Serialize(analysis.SuggestedEdit.Normalize(), JsonOptions));
        }, cancellationToken);

    public Task UpdateMetadataAsync(Guid id, PhotoMetadata metadata, CancellationToken cancellationToken = default) =>
        ExecuteUpdateAsync(id, """
            UPDATE photos SET width=$width, height=$height, captured_at=$capturedAt,
                source_last_write_ticks=$lastWrite, metadata_state=$state
            WHERE id=$id
            """, command =>
        {
            command.Parameters.AddWithValue("$width", Math.Max(0, metadata.PixelWidth));
            command.Parameters.AddWithValue("$height", Math.Max(0, metadata.PixelHeight));
            command.Parameters.AddWithValue("$capturedAt", metadata.CapturedAt?.ToString("O") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$lastWrite", metadata.SourceLastWriteUtcTicks);
            command.Parameters.AddWithValue("$state", (int)MetadataIndexState.Complete);
        }, cancellationToken);

    public Task MarkMetadataFailedAsync(Guid id, CancellationToken cancellationToken = default) =>
        ExecuteUpdateAsync(id, "UPDATE photos SET metadata_state=$state WHERE id=$id",
            command => command.Parameters.AddWithValue("$state", (int)MetadataIndexState.Failed), cancellationToken);

    public async Task<IReadOnlyList<PhotoAsset>> GetPendingMetadataAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        limit = NormalizeLimit(limit);
        var photos = (await ReadPhotosAsync(
            SelectColumns + " WHERE p.metadata_state=$pending ORDER BY p.imported_at, p.id LIMIT $limit",
            command =>
            {
                command.Parameters.AddWithValue("$pending", (int)MetadataIndexState.Pending);
                command.Parameters.AddWithValue("$limit", limit);
            }, cancellationToken).ConfigureAwait(false)).ToList();

        if (photos.Count >= limit)
        {
            return photos;
        }

        var completed = await ReadPhotosAsync(
            SelectColumns + " WHERE p.metadata_state=$complete ORDER BY p.imported_at, p.id",
            command => command.Parameters.AddWithValue("$complete", (int)MetadataIndexState.Complete),
            cancellationToken).ConfigureAwait(false);
        foreach (var photo in completed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(photo.OriginalPath) &&
                File.GetLastWriteTimeUtc(photo.OriginalPath).Ticks != photo.SourceLastWriteUtcTicks)
            {
                await ExecuteUpdateAsync(
                    photo.Id,
                    "UPDATE photos SET metadata_state=$state WHERE id=$id",
                    command => command.Parameters.AddWithValue("$state", (int)MetadataIndexState.Pending),
                    cancellationToken).ConfigureAwait(false);
                photos.Add(photo with { MetadataState = MetadataIndexState.Pending });
                if (photos.Count == limit)
                {
                    break;
                }
            }
        }

        return photos;
    }

    public async Task<int> GetPhotoCountAsync(
        CatalogFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new CatalogFilter(Limit: MaximumPageSize);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = BuildFilteredSql(
            "SELECT COUNT(DISTINCT p.id) FROM photos p", filter, command, includePaging: false);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<CatalogFolder>> GetFoldersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT directory_path, COUNT(*), SUM(is_missing)
            FROM photos
            GROUP BY directory_path
            ORDER BY directory_path COLLATE NOCASE
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var folders = new List<CatalogFolder>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var path = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            folders.Add(new CatalogFolder(path, reader.GetInt32(1), reader.GetInt32(2)));
        }

        return folders;
    }

    public Task<IReadOnlyList<PhotoAsset>> QueryFolderAsync(
        FolderQuery query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query.Folder))
        {
            throw new ArgumentException("Folder is required.", nameof(query));
        }

        var folder = Path.GetFullPath(query.Folder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sql = SelectColumns + (query.IncludeSubfolders
            ? " WHERE p.directory_path=$folder OR p.directory_path LIKE $prefix ESCAPE '\\'"
            : " WHERE p.directory_path=$folder") +
            " ORDER BY p.imported_at, p.file_name COLLATE NOCASE LIMIT $limit OFFSET $offset";
        return ReadPhotosAsync(sql, command =>
        {
            command.Parameters.AddWithValue("$folder", folder);
            command.Parameters.AddWithValue(
                "$prefix", EscapeLike(folder + Path.DirectorySeparatorChar) + "%");
            command.Parameters.AddWithValue("$limit", NormalizeLimit(query.Limit));
            command.Parameters.AddWithValue("$offset", Math.Max(0, query.Offset));
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<PhotoAsset>> QueryAsync(
        CatalogFilter filter,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = BuildFilteredSql(SelectColumns, filter, command, includePaging: true);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var photos = new List<PhotoAsset>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            photos.Add(ReadPhoto(reader));
        }

        return photos;
    }

    public async Task<IReadOnlyList<PhotoSet>> GetCollectionsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.name, COUNT(cp.photo_id)
            FROM collections c
            LEFT JOIN collection_photos cp ON cp.collection_id=c.id
            GROUP BY c.id, c.name
            ORDER BY c.name COLLATE NOCASE
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<PhotoSet>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new PhotoSet(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt32(2)));
        }

        return result;
    }

    public Task<Guid> CreateCollectionAsync(string name, CancellationToken cancellationToken = default) =>
        CreateNamedEntityAsync("collections", name, cancellationToken);

    public Task RenameCollectionAsync(Guid collectionId, string name, CancellationToken cancellationToken = default) =>
        RenameNamedEntityAsync("collections", collectionId, name, cancellationToken);

    public Task DeleteCollectionAsync(Guid collectionId, CancellationToken cancellationToken = default) =>
        DeleteNamedEntityAsync("collections", collectionId, cancellationToken);

    public Task SetCollectionMembershipAsync(
        Guid collectionId,
        Guid photoId,
        bool included,
        CancellationToken cancellationToken = default) =>
        SetCollectionMembershipCoreAsync(collectionId, photoId, included, cancellationToken);

    public Task<Guid> CreateStackAsync(string name, CancellationToken cancellationToken = default) =>
        CreateNamedEntityAsync("stacks", name, cancellationToken);

    public Task RenameStackAsync(Guid stackId, string name, CancellationToken cancellationToken = default) =>
        RenameNamedEntityAsync("stacks", stackId, name, cancellationToken);

    public Task DeleteStackAsync(Guid stackId, CancellationToken cancellationToken = default) =>
        DeleteNamedEntityAsync("stacks", stackId, cancellationToken);

    public async Task SetStackMembershipAsync(
        Guid stackId,
        IReadOnlyList<Guid> photoIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photoIds);
        if (photoIds.Distinct().Count() != photoIds.Count)
        {
            throw new ArgumentException("A photo can appear only once in a stack.", nameof(photoIds));
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM stack_photos WHERE stack_id=$id";
            delete.Parameters.AddWithValue("$id", stackId.ToString());
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        for (var index = 0; index < photoIds.Count; index++)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO stack_photos(stack_id, photo_id, position)
                VALUES($stack, $photo, $position)
                ON CONFLICT(photo_id) DO UPDATE SET stack_id=$stack, position=$position
                """;
            insert.Parameters.AddWithValue("$stack", stackId.ToString());
            insert.Parameters.AddWithValue("$photo", photoIds[index].ToString());
            insert.Parameters.AddWithValue("$position", index);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PhotoStackGroup>> GetStacksAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.name, sp.photo_id
            FROM stacks s
            LEFT JOIN stack_photos sp ON sp.stack_id=s.id
            ORDER BY s.name COLLATE NOCASE, sp.position
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var stacks = new Dictionary<Guid, (string Name, List<Guid> PhotoIds)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = Guid.Parse(reader.GetString(0));
            if (!stacks.TryGetValue(id, out var stack))
            {
                stack = (reader.GetString(1), []);
                stacks[id] = stack;
            }

            if (!reader.IsDBNull(2))
            {
                stack.PhotoIds.Add(Guid.Parse(reader.GetString(2)));
            }
        }

        return stacks.Select(pair => new PhotoStackGroup(pair.Key, pair.Value.Name, pair.Value.PhotoIds)).ToArray();
    }

    public async Task<IReadOnlyList<Guid>> FindMissingAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, original_path FROM photos ORDER BY imported_at, id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var missing = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(reader.GetString(1)))
            {
                missing.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        await reader.DisposeAsync().ConfigureAwait(false);

        await using var updateConnection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await updateConnection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var reset = updateConnection.CreateCommand())
        {
            reset.Transaction = (SqliteTransaction)transaction;
            reset.CommandText = "UPDATE photos SET is_missing=0 WHERE is_missing<>0";
            await reset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var id in missing)
        {
            await using var mark = updateConnection.CreateCommand();
            mark.Transaction = (SqliteTransaction)transaction;
            mark.CommandText = "UPDATE photos SET is_missing=1 WHERE id=$id";
            mark.Parameters.AddWithValue("$id", id.ToString());
            await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return missing;
    }

    public async Task<bool> RelinkAsync(Guid id, string newPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newPath))
        {
            throw new ArgumentException("A replacement path is required.", nameof(newPath));
        }

        var fullPath = Path.GetFullPath(newPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The replacement photo was not found.", fullPath);
        }

        if (!SupportedPhotoFormats.IsSupported(fullPath))
        {
            throw new NotSupportedException($"{Path.GetExtension(fullPath)} is not a supported photo format.");
        }

        var file = new FileInfo(fullPath);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE photos SET original_path=$path, directory_path=$directory, file_name=$name,
                extension=$extension, file_size=$size, source_last_write_ticks=$lastWrite,
                metadata_state=$state, is_missing=0
            WHERE id=$id
              AND NOT EXISTS(SELECT 1 FROM photos other WHERE other.original_path=$path AND other.id<>$id)
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$path", fullPath);
        command.Parameters.AddWithValue("$directory", file.DirectoryName ?? string.Empty);
        command.Parameters.AddWithValue("$name", file.Name);
        command.Parameters.AddWithValue("$extension", file.Extension.ToLowerInvariant());
        command.Parameters.AddWithValue("$size", file.Length);
        command.Parameters.AddWithValue("$lastWrite", file.LastWriteTimeUtc.Ticks);
        command.Parameters.AddWithValue("$state", (int)MetadataIndexState.Pending);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task CreateCurrentSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = CurrentSchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MigrateVersionOneAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = MigrationOneToTwoSql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var paths = new List<(string Id, string Path)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = (SqliteTransaction)transaction;
            select.CommandText = "SELECT id, original_path FROM photos";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                paths.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var item in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(item.Path);
            await using var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = """
                UPDATE photos SET directory_path=$directory, source_last_write_ticks=$lastWrite WHERE id=$id
                """;
            update.Parameters.AddWithValue("$directory", info.DirectoryName ?? string.Empty);
            update.Parameters.AddWithValue("$lastWrite", info.Exists ? info.LastWriteTimeUtc.Ticks : 0L);
            update.Parameters.AddWithValue("$id", item.Id);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var version = connection.CreateCommand())
        {
            version.Transaction = (SqliteTransaction)transaction;
            version.CommandText = "PRAGMA user_version=2";
            await version.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;", cancellationToken)
            .ConfigureAwait(false);
        return connection;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> GetSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private async Task ExecuteUpdateAsync(
        Guid id,
        string sql,
        Action<SqliteCommand> addParameters,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id.ToString());
        addParameters(command);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            throw new KeyNotFoundException($"Photo {id} does not exist in this catalog.");
        }
    }

    private async Task<IReadOnlyList<PhotoAsset>> ReadPhotosAsync(
        string sql,
        Action<SqliteCommand> addParameters,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        addParameters(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<PhotoAsset>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadPhoto(reader));
        }

        return result;
    }

    private static string BuildFilteredSql(
        string select,
        CatalogFilter filter,
        SqliteCommand command,
        bool includePaging)
    {
        var joins = new List<string>();
        var conditions = new List<string>();
        if (filter.CollectionId is not null)
        {
            joins.Add("INNER JOIN collection_photos cp ON cp.photo_id=p.id");
            conditions.Add("cp.collection_id=$collection");
            command.Parameters.AddWithValue("$collection", filter.CollectionId.Value.ToString());
        }

        if (filter.StackId is not null)
        {
            joins.Add("INNER JOIN stack_photos sp ON sp.photo_id=p.id");
            conditions.Add("sp.stack_id=$stack");
            command.Parameters.AddWithValue("$stack", filter.StackId.Value.ToString());
        }

        if (filter.MinimumRating is not null)
        {
            conditions.Add("p.rating >= $rating");
            command.Parameters.AddWithValue("$rating", Math.Clamp(filter.MinimumRating.Value, 0, 5));
        }

        if (filter.PickState is not null)
        {
            conditions.Add("p.pick_state = $pick");
            command.Parameters.AddWithValue("$pick", (int)filter.PickState.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.Extension))
        {
            var extension = filter.Extension.StartsWith('.') ? filter.Extension : "." + filter.Extension;
            conditions.Add("p.extension = $extension COLLATE NOCASE");
            command.Parameters.AddWithValue("$extension", extension.ToLowerInvariant());
        }

        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            conditions.Add("(p.file_name LIKE $search ESCAPE '\\' OR p.original_path LIKE $search ESCAPE '\\')");
            command.Parameters.AddWithValue("$search", "%" + EscapeLike(filter.SearchText.Trim()) + "%");
        }

        if (filter.Missing is not null)
        {
            conditions.Add("p.is_missing = $missing");
            command.Parameters.AddWithValue("$missing", filter.Missing.Value ? 1 : 0);
        }

        if (!string.IsNullOrWhiteSpace(filter.Folder))
        {
            var folder = Path.GetFullPath(filter.Folder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (filter.IncludeSubfolders)
            {
                conditions.Add("(p.directory_path=$folder OR p.directory_path LIKE $folderPrefix ESCAPE '\\')");
                command.Parameters.AddWithValue(
                    "$folderPrefix", EscapeLike(folder + Path.DirectorySeparatorChar) + "%");
            }
            else
            {
                conditions.Add("p.directory_path=$folder");
            }

            command.Parameters.AddWithValue("$folder", folder);
        }

        var sql = select;
        if (joins.Count > 0)
        {
            sql += " " + string.Join(' ', joins);
        }

        if (conditions.Count > 0)
        {
            sql += " WHERE " + string.Join(" AND ", conditions);
        }

        if (includePaging)
        {
            sql += " ORDER BY p.imported_at, p.file_name COLLATE NOCASE LIMIT $limit OFFSET $offset";
            command.Parameters.AddWithValue("$limit", NormalizeLimit(filter.Limit));
            command.Parameters.AddWithValue("$offset", Math.Max(0, filter.Offset));
        }

        return sql;
    }

    private async Task<Guid> CreateNamedEntityAsync(
        string table,
        string name,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeName(name);
        var id = Guid.NewGuid();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO {table}(id, name, created_at) VALUES($id, $name, $createdAt)";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$name", normalized);
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    private async Task RenameNamedEntityAsync(
        string table,
        Guid id,
        string name,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeName(name);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE {table} SET name=$name WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$name", normalized);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            throw new KeyNotFoundException($"{table.TrimEnd('s')} {id} does not exist.");
        }
    }

    private async Task DeleteNamedEntityAsync(
        string table,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {table} WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.ToString());
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            throw new KeyNotFoundException($"{table.TrimEnd('s')} {id} does not exist.");
        }
    }

    private async Task SetCollectionMembershipCoreAsync(
        Guid collectionId,
        Guid photoId,
        bool included,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        if (included)
        {
            command.CommandText = """
                INSERT INTO collection_photos(collection_id, photo_id, added_at)
                VALUES($collection, $photo, $addedAt)
                ON CONFLICT(collection_id, photo_id) DO NOTHING
                """;
            command.Parameters.AddWithValue("$addedAt", DateTimeOffset.UtcNow.ToString("O"));
        }
        else
        {
            command.CommandText = "DELETE FROM collection_photos WHERE collection_id=$collection AND photo_id=$photo";
        }

        command.Parameters.AddWithValue("$collection", collectionId.ToString());
        command.Parameters.AddWithValue("$photo", photoId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A non-empty name is required.", nameof(name));
        }

        var normalized = name.Trim();
        if (normalized.Length > 200)
        {
            throw new ArgumentException("Names cannot exceed 200 characters.", nameof(name));
        }

        return normalized;
    }

    private static int NormalizeLimit(int limit) => Math.Clamp(limit, 1, MaximumPageSize);

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static PhotoAsset ReadPhoto(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt64(4),
        DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
        reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
        reader.GetInt32(7),
        reader.GetInt32(8),
        reader.GetInt32(9),
        (PickState)reader.GetInt32(10),
        JsonSerializer.Deserialize<EditRecipe>(reader.GetString(11), JsonOptions) ?? EditRecipe.Default,
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.GetInt64(13),
        (MetadataIndexState)reader.GetInt32(14));

    private const string CurrentSchemaSql = """
        CREATE TABLE photos (
            id TEXT PRIMARY KEY,
            original_path TEXT NOT NULL UNIQUE COLLATE NOCASE,
            directory_path TEXT NOT NULL COLLATE NOCASE,
            file_name TEXT NOT NULL,
            extension TEXT NOT NULL,
            file_size INTEGER NOT NULL,
            imported_at TEXT NOT NULL,
            captured_at TEXT NULL,
            width INTEGER NOT NULL DEFAULT 0,
            height INTEGER NOT NULL DEFAULT 0,
            rating INTEGER NOT NULL DEFAULT 0 CHECK(rating BETWEEN 0 AND 5),
            pick_state INTEGER NOT NULL DEFAULT 0,
            edit_json TEXT NOT NULL,
            ai_summary TEXT NULL,
            ai_technical REAL NULL,
            ai_aesthetic REAL NULL,
            ai_suggested_pick INTEGER NULL,
            ai_tags_json TEXT NULL,
            ai_edit_json TEXT NULL,
            source_last_write_ticks INTEGER NOT NULL DEFAULT 0,
            metadata_state INTEGER NOT NULL DEFAULT 0,
            is_missing INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE collections (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL UNIQUE COLLATE NOCASE,
            created_at TEXT NOT NULL
        );
        CREATE TABLE collection_photos (
            collection_id TEXT NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
            photo_id TEXT NOT NULL REFERENCES photos(id) ON DELETE CASCADE,
            added_at TEXT NOT NULL,
            PRIMARY KEY(collection_id, photo_id)
        );
        CREATE TABLE stacks (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL UNIQUE COLLATE NOCASE,
            created_at TEXT NOT NULL
        );
        CREATE TABLE stack_photos (
            stack_id TEXT NOT NULL REFERENCES stacks(id) ON DELETE CASCADE,
            photo_id TEXT NOT NULL UNIQUE REFERENCES photos(id) ON DELETE CASCADE,
            position INTEGER NOT NULL,
            PRIMARY KEY(stack_id, photo_id)
        );
        CREATE INDEX ix_photos_imported_at ON photos(imported_at);
        CREATE INDEX ix_photos_directory ON photos(directory_path);
        CREATE INDEX ix_photos_filter ON photos(rating, pick_state, extension);
        CREATE INDEX ix_photos_missing ON photos(is_missing);
        CREATE INDEX ix_collection_photos_photo ON collection_photos(photo_id);
        CREATE INDEX ix_stack_photos_stack_position ON stack_photos(stack_id, position);
        PRAGMA user_version=2;
        """;

    private const string MigrationOneToTwoSql = """
        ALTER TABLE photos ADD COLUMN directory_path TEXT NULL COLLATE NOCASE;
        ALTER TABLE photos ADD COLUMN source_last_write_ticks INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE photos ADD COLUMN metadata_state INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE photos ADD COLUMN is_missing INTEGER NOT NULL DEFAULT 0;
        CREATE TABLE collections (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL UNIQUE COLLATE NOCASE,
            created_at TEXT NOT NULL
        );
        CREATE TABLE collection_photos (
            collection_id TEXT NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
            photo_id TEXT NOT NULL REFERENCES photos(id) ON DELETE CASCADE,
            added_at TEXT NOT NULL,
            PRIMARY KEY(collection_id, photo_id)
        );
        CREATE TABLE stacks (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL UNIQUE COLLATE NOCASE,
            created_at TEXT NOT NULL
        );
        CREATE TABLE stack_photos (
            stack_id TEXT NOT NULL REFERENCES stacks(id) ON DELETE CASCADE,
            photo_id TEXT NOT NULL UNIQUE REFERENCES photos(id) ON DELETE CASCADE,
            position INTEGER NOT NULL,
            PRIMARY KEY(stack_id, photo_id)
        );
        CREATE INDEX ix_photos_directory ON photos(directory_path);
        CREATE INDEX ix_photos_filter ON photos(rating, pick_state, extension);
        CREATE INDEX ix_photos_missing ON photos(is_missing);
        CREATE INDEX ix_collection_photos_photo ON collection_photos(photo_id);
        CREATE INDEX ix_stack_photos_stack_position ON stack_photos(stack_id, position);
        """;
}
