using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HadramoutNET.Core.Data;

/// <summary>
/// Non-secret historic card metadata. Voucher passwords are deliberately
/// excluded; the old database remains untouched by this read-only view.
/// </summary>
public sealed record LegacyHistoryCard(
    long Id, string Username, string Batch, string Profile,
    string Service, string Status, string CreatedAt);

public sealed record LegacyHistoryPage(
    bool Imported, long Total, IReadOnlyList<LegacyHistoryCard> Cards);

/// <summary>
/// Strictly read-only viewer for the independent .NET legacy_* archive.
/// Neither the original Python SQLite file nor any router is opened here.
/// </summary>
public sealed class LegacyHistoryReader(string targetPath)
{
    private readonly string _targetPath = Path.GetFullPath(
        !string.IsNullOrWhiteSpace(targetPath) ? targetPath :
        throw new ArgumentException("Target path is required.", nameof(targetPath)));

    public LegacyHistoryPage ReadPage(int offset, int pageSize = 100, string? usernameSearch = null)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (pageSize is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        // Treat the entered username as literal data: no LIKE wildcards or SQL expressions.
        string search = (usernameSearch ?? "").Trim();
        if (search.Length > 128)
            throw new ArgumentException("Username search exceeds 128 characters.", nameof(usernameSearch));

        if (!File.Exists(_targetPath))
            return new LegacyHistoryPage(false, 0, []);

        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _targetPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 10
        }.ToString());
        db.Open();

        using (var exists = db.CreateCommand())
        {
            exists.CommandText = """
                SELECT COUNT(*) FROM sqlite_master
                WHERE type='table' AND name='legacy_import_info'
                """;
            if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                return new LegacyHistoryPage(false, 0, []);
        }

        long total;
        using (var count = db.CreateCommand())
        {
            count.CommandText = """
                SELECT COUNT(*) FROM legacy_cards AS c
                WHERE $search = '' OR instr(COALESCE(c.username, ''), $search) > 0
                """;
            count.Parameters.AddWithValue("$search", search);
            total = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT c.id, COALESCE(c.username, ''),
                   COALESCE(b.batch_no, ''), COALESCE(c.profile, ''),
                   COALESCE(c.service, ''), COALESCE(c.status, ''),
                   COALESCE(c.created_at, '')
            FROM legacy_cards AS c
            LEFT JOIN legacy_batches AS b ON b.id = c.batch_id
            WHERE $search = '' OR instr(COALESCE(c.username, ''), $search) > 0
            ORDER BY c.id DESC LIMIT $limit OFFSET $offset
            """;
        command.Parameters.AddWithValue("$search", search);
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", offset);
        using var reader = command.ExecuteReader();
        var rows = new List<LegacyHistoryCard>();
        while (reader.Read())
        {
            rows.Add(new LegacyHistoryCard(
                reader.GetInt64(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5),
                reader.GetString(6)));
        }
        return new LegacyHistoryPage(true, total, rows);
    }
}
