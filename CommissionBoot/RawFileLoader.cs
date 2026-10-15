using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using MySqlConnector;

namespace CommissionBoot
{
    /// <summary>
    ///  Loads a downloaded ADR / SDR csv into the dailyCommission database.
    ///  Port of the Python loader (load_metro_file) - same tables, same row_hash / occ / batch logic,
    ///  so batches loaded by either tool line up.
    /// </summary>
    public sealed class RawFileLoader
    {
        // pandas read_csv turns these into NaN, and fillna("") turns NaN into "".
        private static readonly HashSet<string> PandasNaValues = new(StringComparer.Ordinal)
        {
            "", "#N/A", "#N/A N/A", "#NA", "-1.#IND", "-1.#QNAN", "-NaN", "-nan", "1.#IND", "1.#QNAN",
            "<NA>", "N/A", "NA", "NULL", "NaN", "None", "n/a", "nan", "null",
        };

        private const string TimestampFormat = "MM/dd/yyyy HH:mm:ss";

        private readonly string _connectionString;
        private readonly Action<string> _log;

        public RawFileLoader(string connectionString, Action<string> log)
        {
            // Bulk copy uses LOAD DATA LOCAL INFILE.
            _connectionString = new MySqlConnectionStringBuilder(connectionString)
            {
                AllowLoadLocalInfile = true,
                CharacterSet = "utf8mb4",
            }.ConnectionString;
            _log = log;
        }

        public sealed record LoadResult(
            string Source, string FileName, long BatchId, int RowsInFile, int RowsInserted, int RowsSkipped, bool AlreadyLoaded)
        {
            public override string ToString() => AlreadyLoaded
                ? $"{Source}: {FileName} - already loaded (batch {BatchId}), skipped"
                : $"{Source}: {FileName} - batch {BatchId}, {RowsInFile} rows ({RowsInserted} inserted, {RowsSkipped} duplicate)";
        }

        public void EnsureTablesExist()
        {
            using var conn = Open();
            using (var cmd = new MySqlCommand("SELECT DATABASE(), VERSION()", conn))
            using (var reader = cmd.ExecuteReader())
            {
                reader.Read();
                _log($"Connected to database {reader.GetString(0)} (MySQL {reader.GetString(1)})");
            }

            Execute(conn, null, """
                CREATE TABLE IF NOT EXISTS raw_load_batch (
                    batch_id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
                    source VARCHAR(100) NOT NULL,
                    file_name VARCHAR(255) NOT NULL,
                    file_sha256 CHAR(64) NOT NULL,
                    rows_in_file INT UNSIGNED NOT NULL DEFAULT 0,
                    rows_inserted INT UNSIGNED NOT NULL DEFAULT 0,
                    rows_skipped INT UNSIGNED NOT NULL DEFAULT 0,
                    min_ts DATETIME NULL,
                    max_ts DATETIME NULL,
                    loaded_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    PRIMARY KEY (batch_id),
                    UNIQUE KEY uk_raw_load_batch_file_sha256 (file_sha256)
                ) ENGINE=InnoDB
                """);

            Execute(conn, null, """
                CREATE TABLE IF NOT EXISTS raw_batch_row (
                    batch_id BIGINT UNSIGNED NOT NULL,
                    source VARCHAR(100) NOT NULL,
                    row_hash CHAR(64) NOT NULL,
                    occ INT UNSIGNED NOT NULL,
                    PRIMARY KEY (batch_id, source, row_hash, occ),
                    KEY ix_raw_batch_row_hash (source, row_hash),
                    CONSTRAINT fk_raw_batch_row_batch
                        FOREIGN KEY (batch_id) REFERENCES raw_load_batch(batch_id)
                        ON DELETE RESTRICT ON UPDATE CASCADE
                ) ENGINE=InnoDB
                """);
        }

        /// <param name="source">"ADR" (Activation Detail) or "SDR" (Sales Detail).</param>
        public LoadResult Load(string filePath, string source)
        {
            source = source.ToUpperInvariant();
            var dateColumn = source switch
            {
                "ADR" => "actdate",
                "SDR" => "adddate",
                _ => throw new ArgumentException("source must be ADR or SDR", nameof(source)),
            };
            var fileName = Path.GetFileName(filePath);
            var targetTable = "raw_" + source.ToLowerInvariant();

            _log($"Loading {source} from {fileName}...");
            var fileSha256 = Sha256File(filePath);

            var (columns, rows) = ReadCsv(filePath);
            _log($"Rows read: {rows.Count}");

            // row_hash covers only the source columns; occ numbers repeats of the same row inside this file.
            var hashes = new string[rows.Count];
            var occs = new int[rows.Count];
            var seen = new Dictionary<string, int>();
            for (var i = 0; i < rows.Count; i++)
            {
                hashes[i] = RowHash(rows[i]);
                occs[i] = seen[hashes[i]] = seen.GetValueOrDefault(hashes[i]) + 1;
            }
            var duplicateRows = occs.Count(o => o > 1);
            var insertedRows = rows.Count - duplicateRows;

            DateTime? minTs = null, maxTs = null;
            var dateIndex = Array.IndexOf(columns, dateColumn);
            if (dateIndex >= 0)
            {
                foreach (var row in rows)
                {
                    if (DateTime.TryParseExact(row[dateIndex], TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts))
                    {
                        if (minTs == null || ts < minTs) minTs = ts;
                        if (maxTs == null || ts > maxTs) maxTs = ts;
                    }
                }
            }

            using var conn = Open();
            using var tx = conn.BeginTransaction();

            var existing = Scalar(conn, tx, "SELECT batch_id FROM raw_load_batch WHERE file_sha256 = @sha LIMIT 1",
                ("@sha", fileSha256));
            if (existing != null)
            {
                var existingId = Convert.ToInt64(existing);
                _log($"File already loaded. Batch ID: {existingId}");
                return new LoadResult(source, fileName, existingId, rows.Count, 0, 0, AlreadyLoaded: true);
            }

            var tableColumns = GetTableColumns(conn, tx, targetTable);
            var missing = columns.Where(c => !tableColumns.Contains(c)).ToList();
            if (missing.Count > 0)
            {
                throw new InvalidOperationException($"{targetTable} has no column(s): {string.Join(", ", missing)}. The report layout changed.");
            }

            Execute(conn, tx, """
                INSERT INTO raw_load_batch (source, file_name, file_sha256, rows_in_file, rows_inserted, rows_skipped, min_ts, max_ts)
                VALUES (@source, @file_name, @sha, @rows, 0, 0, @min_ts, @max_ts)
                """,
                ("@source", source), ("@file_name", fileName), ("@sha", fileSha256), ("@rows", rows.Count),
                ("@min_ts", minTs), ("@max_ts", maxTs));
            var batchId = Convert.ToInt64(Scalar(conn, tx, "SELECT LAST_INSERT_ID()"));
            _log($"Batch ID: {batchId}");

            // raw_adr / raw_sdr: source columns + row_num, row_hash, occ, batch_id
            var data = new DataTable();
            foreach (var c in columns) data.Columns.Add(c, typeof(string));
            data.Columns.Add("row_num", typeof(long));
            data.Columns.Add("row_hash", typeof(string));
            data.Columns.Add("occ", typeof(long));
            data.Columns.Add("batch_id", typeof(long));
            for (var i = 0; i < rows.Count; i++)
            {
                var values = new object[columns.Length + 4];
                Array.Copy(rows[i], values, columns.Length);
                values[columns.Length] = (long)(i + 1);
                values[columns.Length + 1] = hashes[i];
                values[columns.Length + 2] = (long)occs[i];
                values[columns.Length + 3] = batchId;
                data.Rows.Add(values);
            }
            _log($"Inserting {rows.Count} rows into {targetTable}...");
            BulkInsert(conn, tx, targetTable, data);

            var batchRows = new DataTable();
            batchRows.Columns.Add("batch_id", typeof(long));
            batchRows.Columns.Add("source", typeof(string));
            batchRows.Columns.Add("row_hash", typeof(string));
            batchRows.Columns.Add("occ", typeof(long));
            for (var i = 0; i < rows.Count; i++)
            {
                batchRows.Rows.Add(batchId, source, hashes[i], (long)occs[i]);
            }
            BulkInsert(conn, tx, "raw_batch_row", batchRows);

            Execute(conn, tx, "UPDATE raw_load_batch SET rows_inserted = @ins, rows_skipped = @skip WHERE batch_id = @id",
                ("@ins", insertedRows), ("@skip", duplicateRows), ("@id", batchId));

            tx.Commit();

            var result = new LoadResult(source, fileName, batchId, rows.Count, insertedRows, duplicateRows, AlreadyLoaded: false);
            _log("Loaded " + result);
            return result;
        }

        private static (string[] Columns, List<string[]> Rows) ReadCsv(string filePath)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            using var reader = new StreamReader(filePath, Encoding.GetEncoding(1252));
            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                TrimOptions = TrimOptions.None,
                IgnoreBlankLines = true,
                BadDataFound = null,
            };
            using var parser = new CsvParser(reader, config);

            if (!parser.Read())
            {
                throw new InvalidOperationException("File is empty.");
            }
            var columns = parser.Record!.Select(c => c.Trim().ToLowerInvariant()).ToArray();

            var rows = new List<string[]>();
            while (parser.Read())
            {
                var record = parser.Record!;
                if (record.Length > columns.Length)
                {
                    throw new InvalidOperationException($"Row {rows.Count + 1} has {record.Length} fields, header has {columns.Length}.");
                }
                var row = new string[columns.Length];
                for (var i = 0; i < columns.Length; i++)
                {
                    var value = i < record.Length ? record[i] : "";
                    row[i] = PandasNaValues.Contains(value) ? "" : value;
                }
                rows.Add(row);
            }
            return (columns, rows);
        }

        // MD5 of the stripped values joined with "|" - same as generate_row_hash in Python.
        private static string RowHash(string[] row)
        {
            var joined = string.Join("|", row.Select(v => v.Trim()));
            return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
        }

        private static string Sha256File(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        private static void BulkInsert(MySqlConnection conn, MySqlTransaction tx, string table, DataTable data)
        {
            var bulk = new MySqlBulkCopy(conn, tx) { DestinationTableName = table, BulkCopyTimeout = 0 };
            for (var i = 0; i < data.Columns.Count; i++)
            {
                bulk.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(i, data.Columns[i].ColumnName));
            }
            var result = bulk.WriteToServer(data);
            if (result.RowsInserted != data.Rows.Count)
            {
                throw new InvalidOperationException($"{table}: expected {data.Rows.Count} rows, inserted {result.RowsInserted}.");
            }
        }

        private static HashSet<string> GetTableColumns(MySqlConnection conn, MySqlTransaction tx, string table)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = new MySqlCommand(
                "SELECT column_name FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = @t", conn, tx);
            cmd.Parameters.AddWithValue("@t", table);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) result.Add(reader.GetString(0));
            if (result.Count == 0)
            {
                throw new InvalidOperationException($"Table {table} does not exist.");
            }
            return result;
        }

        private MySqlConnection Open()
        {
            var conn = new MySqlConnection(_connectionString);
            conn.Open();
            return conn;
        }

        private static void Execute(MySqlConnection conn, MySqlTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
        {
            using var cmd = new MySqlCommand(sql, conn, tx);
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        private static object? Scalar(MySqlConnection conn, MySqlTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
        {
            using var cmd = new MySqlCommand(sql, conn, tx);
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return cmd.ExecuteScalar();
        }
    }
}
