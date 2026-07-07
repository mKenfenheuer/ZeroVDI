using KSol.ZeroVDI.RDP;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KSol.ZeroVDI.Data;

/// <summary>
/// One-time, idempotent backfill that encrypts previously-plaintext sensitive columns
/// (<c>AspNetUsers.NtHash</c>, <c>ProxmoxBackends.ApiTokenSecret</c>) now that those columns are
/// transparently encrypted at rest via <see cref="EncryptedStringConverter"/>.
///
/// It reads each column's RAW stored value (bypassing the EF converter), and if that value is NOT
/// decryptable by the current keyring — i.e. it is still plaintext from before encryption was enabled —
/// it re-stores the value encrypted. Already-encrypted values decrypt cleanly and are skipped, so this
/// is safe to run on every startup.
/// </summary>
public sealed class SecretEncryptionMigrator
{
    private readonly ApplicationDbContext _db;
    private readonly CredentialProtector _protector;
    private readonly ILogger<SecretEncryptionMigrator> _logger;

    public SecretEncryptionMigrator(ApplicationDbContext db, CredentialProtector protector,
        ILogger<SecretEncryptionMigrator> logger)
    {
        _db = db;
        _protector = protector;
        _logger = logger;
    }

    public void EncryptPlaintextSecrets()
    {
        var connection = (SqliteConnection)_db.Database.GetDbConnection();
        var opened = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
            opened = true;
        }
        try
        {
            var users = BackfillColumn(connection, "AspNetUsers", "Id", "NtHash");
            var backends = BackfillColumn(connection, "ProxmoxBackends", "Id", "ApiTokenSecret");
            if (users + backends > 0)
                _logger.LogWarning(
                    "Encrypted {Users} plaintext NtHash value(s) and {Backends} plaintext Proxmox API secret(s) at rest.",
                    users, backends);
        }
        finally
        {
            if (opened) connection.Close();
        }
    }

    /// <summary>
    /// Encrypts any rows whose raw column value is not decryptable (still plaintext). Returns the count
    /// of rows updated. Uses parameterized SQL; table/column names are compile-time constants only.
    /// </summary>
    private int BackfillColumn(SqliteConnection connection, string table, string keyColumn, string column)
    {
        // Collect candidates first (read), then update — Sqlite doesn't allow a write while a reader is open.
        var toEncrypt = new List<(string Key, string Plaintext)>();

        using (var read = connection.CreateCommand())
        {
            read.CommandText = $"SELECT \"{keyColumn}\", \"{column}\" FROM \"{table}\" WHERE \"{column}\" IS NOT NULL";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(1)) continue;
                var key = reader.GetValue(0)?.ToString();
                var stored = reader.GetString(1);
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(stored)) continue;

                // If it decrypts, it's already encrypted — leave it. If not, it's legacy plaintext.
                if (_protector.Unprotect(stored) == null)
                    toEncrypt.Add((key, stored));
            }
        }

        var updated = 0;
        foreach (var (key, plaintext) in toEncrypt)
        {
            var ciphertext = _protector.Protect(plaintext);
            using var write = connection.CreateCommand();
            write.CommandText = $"UPDATE \"{table}\" SET \"{column}\" = $val WHERE \"{keyColumn}\" = $key";
            write.Parameters.Add(new SqliteParameter("$val", ciphertext));
            write.Parameters.Add(new SqliteParameter("$key", key));
            updated += write.ExecuteNonQuery();
        }
        return updated;
    }
}
