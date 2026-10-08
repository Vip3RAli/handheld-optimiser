using System.Runtime.InteropServices;
using System.Text;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Read-only queries on a SQLite database, through the copy of SQLite that ships with Windows 10 and 11
/// (winsqlite3.dll), so the library carries no database engine of its own. Used for GOG Galaxy's list of
/// owned games.
/// </summary>
internal static partial class Sqlite
{
    private const int OpenReadOnly = 0x00000001;
    private const int OpenUri = 0x00000040;
    private const int Row = 100;
    private const int Done = 101;

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2")]
    private static partial int Open(byte[] fileName, out nint database, int flags, nint vfs);

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_close")]
    private static partial int Close(nint database);

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare_v2")]
    private static partial int Prepare(nint database, byte[] sql, int length, out nint statement, nint tail);

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_step")]
    private static partial int Step(nint statement);

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_column_text")]
    private static partial nint ColumnText(nint statement, int column);

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_column_bytes")]
    private static partial int ColumnBytes(nint statement, int column);

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize")]
    private static partial int Finalize(nint statement);

    /// <summary>
    /// Every row of a query, each as its columns' text, or null when the database could not be opened or
    /// read: it is missing, locked, a different shape than expected, or Windows has no SQLite.
    /// </summary>
    public static List<string?[]>? Query(string path, string sql, int columns) =>
        // mode=ro never writes, not even the journal files beside the database. A database the program
        // that owns it keeps in WAL mode may still refuse that from an account that cannot write to its
        // folder, so it is then read as it is on disk, without taking part in its locking.
        Query(path, "mode=ro", sql, columns) ?? Query(path, "immutable=1", sql, columns);

    private static List<string?[]>? Query(string path, string options, string sql, int columns)
    {
        nint database = 0;
        nint statement = 0;
        try
        {
            var uri = "file:///" + Uri.EscapeDataString(path.Replace('\\', '/')).Replace("%2F", "/").Replace("%3A", ":") + "?" + options;
            if (Open(Utf8(uri), out database, OpenReadOnly | OpenUri, 0) != 0
                || Prepare(database, Utf8(sql), -1, out statement, 0) != 0)
            {
                return null;
            }

            var rows = new List<string?[]>();
            int result;
            while ((result = Step(statement)) == Row)
            {
                var row = new string?[columns];
                for (var i = 0; i < columns; i++)
                {
                    var text = ColumnText(statement, i);
                    row[i] = text == 0 ? null : Marshal.PtrToStringUTF8(text, ColumnBytes(statement, i));
                }

                rows.Add(row);
            }

            return result == Done ? rows : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (statement != 0)
            {
                Finalize(statement);
            }

            if (database != 0)
            {
                Close(database);
            }
        }
    }

    // SQLite takes null-terminated UTF-8.
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + '\0');
}
