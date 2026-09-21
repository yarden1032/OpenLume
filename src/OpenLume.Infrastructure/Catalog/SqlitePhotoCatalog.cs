using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenLume.Core.Abstractions;
using OpenLume.Core.Domain;

namespace OpenLume.Infrastructure.Catalog;

public sealed class SqlitePhotoCatalog : IPhotoCatalog
{
    private const int CurrentSchemaVersion = 1;
    private const string SelectColumns = """
        SELECT id, original_path, file_name, extension, file_size, imported_at, captured_at,
               width, height, rating, pick_state, edit_json, ai_summary
        FROM photos
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
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
        var schemaVersion = await GetSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (schemaVersion > CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Catalog schema {schemaVersion} is newer than this build supports ({CurrentSchemaVersion}).");
        }

        if (schemaVersion == 0)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS photos (
                    id TEXT PRIMARY KEY,
                    original_path TEXT NOT NULL UNIQUE COLLATE NOCASE,
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
                    ai_edit_json TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_photos_imported_at ON photos(imported_at);
                PRAGMA user_version=1;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
                        INSERT INTO photos(id, original_path, file_name, extension, file_size, imported_at, edit_json)
                        VALUES($id, $path, $name, $extension, $size, $importedAt, $edit)
                        ON CONFLICT(original_path) DO NOTHING;
                        """;
                    command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                    command.Parameters.AddWithValue("$path", fullPath);
                    command.Parameters.AddWithValue("$name", file.Name);
                    command.Parameters.AddWithValue("$extension", file.Extension.ToLowerInvariant());
                    command.Parameters.AddWithValue("$size", file.Length);
                    command.Parameters.AddWithValue("$importedAt", DateTimeOffset.UtcNow.ToString("O"));
                    command.Parameters.AddWithValue("$edit", JsonSerializer.Serialize(EditRecipe.Default, JsonOptions));
                    if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1) imported++;
                    else alreadyPresent++;
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

    public async Task<IReadOnlyList<PhotoAsset>> GetPhotosAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " ORDER BY imported_at, file_name";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<PhotoAsset>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(ReadPhoto(reader));
        return result;
    }

    public async Task<PhotoAsset?> GetPhotoAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE id=$id";
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

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> GetSchemaVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private async Task ExecuteUpdateAsync(
        Guid id, string sql, Action<SqliteCommand> addParameters, CancellationToken cancellationToken)
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

    private static PhotoAsset ReadPhoto(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetInt64(4), DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
        reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
        reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9), (PickState)reader.GetInt32(10),
        JsonSerializer.Deserialize<EditRecipe>(reader.GetString(11), JsonOptions) ?? EditRecipe.Default,
        reader.IsDBNull(12) ? null : reader.GetString(12));
}
