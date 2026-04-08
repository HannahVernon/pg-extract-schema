using Npgsql;

namespace pg_extract_schema;

public class SchemaExtractor
{
    private readonly string _connString;
    private readonly string _outputDir;
    private readonly string? _schemaFilter;
    private readonly bool _includeSystemObjects;

    public SchemaExtractor(string connString, string outputDir, string? schemaFilter, bool includeSystemObjects = false)
    {
        _connString = connString;
        _outputDir = outputDir;
        _schemaFilter = schemaFilter;
        _includeSystemObjects = includeSystemObjects;
    }

    public async Task ExtractAllAsync()
    {
        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync();

        await ExtractExtensionsAsync(conn);
        await ExtractSchemasAsync(conn);
        await ExtractSequencesAsync(conn);
        await ExtractTypesAsync(conn);
        await ExtractTablesAsync(conn);
        await ExtractIndexesAsync(conn);
        await ExtractForeignKeysAsync(conn);
        await ExtractViewsAsync(conn);
        await ExtractMaterializedViewsAsync(conn);
        await ExtractFunctionsAsync(conn);
        await ExtractTriggersAsync(conn);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private string SchemaWhereClause(string alias = "n.nspname")
    {
        if (_schemaFilter != null)
            return $"{alias} = '{Sanitize(_schemaFilter)}'";
        if (_includeSystemObjects)
            return "TRUE";
        return $"{alias} NOT IN ('pg_catalog','information_schema','pg_toast') AND {alias} NOT LIKE 'pg_temp%'";
    }

    private static string Sanitize(string s) => s.Replace("'", "''");

    private static string SafeFileName(string name) =>
        string.Join("_", name.Split(Path.GetInvalidFileNameChars()));

    private async Task WriteFileAsync(string subdir, string fileName, string content)
    {
        var dir = Path.Combine(_outputDir, subdir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        await File.WriteAllTextAsync(path, content);
    }

    private async Task<List<T>> QueryAsync<T>(NpgsqlConnection conn, string sql, Func<NpgsqlDataReader, T> map)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var results = new List<T>();
        while (await reader.ReadAsync())
            results.Add(map(reader));
        return results;
    }

    // ── Extensions ───────────────────────────────────────────────────

    private async Task ExtractExtensionsAsync(NpgsqlConnection conn)
    {
        var whereClause = _includeSystemObjects ? "" : "WHERE e.extname <> 'plpgsql'";
        var sql = $@"
            SELECT e.extname, n.nspname
            FROM pg_extension e
            JOIN pg_namespace n ON n.oid = e.extnamespace
            {whereClause}
            ORDER BY e.extname";

        var rows = await QueryAsync(conn, sql, r => (
            name: r.GetString(0),
            schema: r.GetString(1)
        ));

        foreach (var (name, schema) in rows)
        {
            var ddl = $"CREATE EXTENSION IF NOT EXISTS \"{name}\" SCHEMA \"{schema}\";\n";
            await WriteFileAsync("extensions", $"{SafeFileName(name)}.sql", ddl);
        }

        Console.WriteLine($"  Extensions: {rows.Count}");
    }

    // ── Schemas ──────────────────────────────────────────────────────

    private async Task ExtractSchemasAsync(NpgsqlConnection conn)
    {
        var sql = $@"
            SELECT nspname, pg_catalog.pg_get_userbyid(nspowner) AS owner
            FROM pg_namespace
            WHERE {SchemaWhereClause("nspname")}
            ORDER BY nspname";

        var rows = await QueryAsync(conn, sql, r => (
            name: r.GetString(0),
            owner: r.GetString(1)
        ));

        foreach (var (name, owner) in rows)
        {
            var ddl = $"CREATE SCHEMA IF NOT EXISTS \"{name}\";\n\nALTER SCHEMA \"{name}\" OWNER TO \"{owner}\";\n";
            await WriteFileAsync("schemas", $"{SafeFileName(name)}.sql", ddl);
        }

        Console.WriteLine($"  Schemas:    {rows.Count}");
    }

    // ── Sequences ────────────────────────────────────────────────────

    private async Task ExtractSequencesAsync(NpgsqlConnection conn)
    {
        var sql = $@"
            SELECT n.nspname, c.relname
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind = 'S' AND {SchemaWhereClause()}
            ORDER BY n.nspname, c.relname";

        var seqs = await QueryAsync(conn, sql, r => (
            schema: r.GetString(0),
            name: r.GetString(1)
        ));

        foreach (var (schema, name) in seqs)
        {
            var detailSql = $@"
                SELECT start_value, minimum_value, maximum_value, increment, cycle_option, data_type
                FROM information_schema.sequences
                WHERE sequence_schema = '{Sanitize(schema)}' AND sequence_name = '{Sanitize(name)}'";

            var details = await QueryAsync(conn, detailSql, r => (
                start: r.GetString(0),
                min: r.GetString(1),
                max: r.GetString(2),
                inc: r.GetString(3),
                cycle: r.GetString(4),
                dataType: r.GetString(5)
            ));

            var ddl = $"CREATE SEQUENCE \"{schema}\".\"{name}\"";
            if (details.Count > 0)
            {
                var d = details[0];
                ddl += $"\n    AS {d.dataType}"
                     + $"\n    INCREMENT BY {d.inc}"
                     + $"\n    MINVALUE {d.min}"
                     + $"\n    MAXVALUE {d.max}"
                     + $"\n    START WITH {d.start}"
                     + $"\n    {(d.cycle == "YES" ? "CYCLE" : "NO CYCLE")}";
            }
            ddl += ";\n";
            await WriteFileAsync("sequences", $"{SafeFileName(schema)}.{SafeFileName(name)}.sql", ddl);
        }

        Console.WriteLine($"  Sequences:  {seqs.Count}");
    }

    // ── Types (enums, composites, domains) ───────────────────────────

    private async Task ExtractTypesAsync(NpgsqlConnection conn)
    {
        int count = 0;

        // Enums
        var enumSql = $@"
            SELECT n.nspname, t.typname,
                   array_to_string(array_agg('''' || e.enumlabel || '''' ORDER BY e.enumsortorder), ', ') AS labels
            FROM pg_type t
            JOIN pg_namespace n ON n.oid = t.typnamespace
            JOIN pg_enum e ON e.enumtypid = t.oid
            WHERE {SchemaWhereClause()}
            GROUP BY n.nspname, t.typname
            ORDER BY n.nspname, t.typname";

        var enums = await QueryAsync(conn, enumSql, r => (
            schema: r.GetString(0),
            name: r.GetString(1),
            labels: r.GetString(2)
        ));

        foreach (var (schema, name, labels) in enums)
        {
            var ddl = $"CREATE TYPE \"{schema}\".\"{name}\" AS ENUM ({labels});\n";
            await WriteFileAsync("types", $"{SafeFileName(schema)}.{SafeFileName(name)}.sql", ddl);
            count++;
        }

        // Composite types
        var compSql = $@"
            SELECT n.nspname, t.typname,
                   string_agg('    ""' || a.attname || '"" ' || pg_catalog.format_type(a.atttypid, a.atttypmod), E',\n' ORDER BY a.attnum) AS attrs
            FROM pg_type t
            JOIN pg_namespace n ON n.oid = t.typnamespace
            JOIN pg_class c ON c.oid = t.typrelid
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
            WHERE t.typtype = 'c' AND c.relkind = 'c' AND {SchemaWhereClause()}
            GROUP BY n.nspname, t.typname
            ORDER BY n.nspname, t.typname";

        var composites = await QueryAsync(conn, compSql, r => (
            schema: r.GetString(0),
            name: r.GetString(1),
            attrs: r.GetString(2)
        ));

        foreach (var (schema, name, attrs) in composites)
        {
            var ddl = $"CREATE TYPE \"{schema}\".\"{name}\" AS (\n{attrs}\n);\n";
            await WriteFileAsync("types", $"{SafeFileName(schema)}.{SafeFileName(name)}.sql", ddl);
            count++;
        }

        // Domains
        var domSql = $@"
            SELECT n.nspname, t.typname,
                   pg_catalog.format_type(t.typbasetype, t.typtypmod) AS base_type,
                   t.typnotnull,
                   t.typdefault,
                   (SELECT string_agg(pg_get_constraintdef(co.oid), ' ') 
                    FROM pg_constraint co WHERE co.contypid = t.oid) AS checks
            FROM pg_type t
            JOIN pg_namespace n ON n.oid = t.typnamespace
            WHERE t.typtype = 'd' AND {SchemaWhereClause()}
            ORDER BY n.nspname, t.typname";

        var domains = await QueryAsync(conn, domSql, r => (
            schema: r.GetString(0),
            name: r.GetString(1),
            baseType: r.GetString(2),
            notNull: r.GetBoolean(3),
            defaultVal: r.IsDBNull(4) ? null : r.GetString(4),
            checks: r.IsDBNull(5) ? null : r.GetString(5)
        ));

        foreach (var (schema, name, baseType, notNull, defaultVal, checks) in domains)
        {
            var ddl = $"CREATE DOMAIN \"{schema}\".\"{name}\" AS {baseType}";
            if (defaultVal != null) ddl += $"\n    DEFAULT {defaultVal}";
            if (notNull) ddl += "\n    NOT NULL";
            if (checks != null) ddl += $"\n    {checks}";
            ddl += ";\n";
            await WriteFileAsync("types", $"{SafeFileName(schema)}.{SafeFileName(name)}.sql", ddl);
            count++;
        }

        Console.WriteLine($"  Types:      {count}");
    }

    // ── Tables ───────────────────────────────────────────────────────

    private async Task ExtractTablesAsync(NpgsqlConnection conn)
    {
        var tableFilter = _includeSystemObjects ? "" : "AND c.relname NOT LIKE 'pg_%'";
        var sql = $@"
            SELECT n.nspname, c.relname
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind = 'r' AND {SchemaWhereClause()}
              {tableFilter}
            ORDER BY n.nspname, c.relname";

        var tables = await QueryAsync(conn, sql, r => (
            schema: r.GetString(0),
            name: r.GetString(1)
        ));

        foreach (var (schema, name) in tables)
        {
            var ddl = await BuildTableDdlAsync(conn, schema, name);
            await WriteFileAsync("tables", $"{SafeFileName(schema)}.{SafeFileName(name)}.sql", ddl);
        }

        Console.WriteLine($"  Tables:     {tables.Count}");
    }

    private async Task<string> BuildTableDdlAsync(NpgsqlConnection conn, string schema, string table)
    {
        // Columns
        var colSql = $@"
            SELECT a.attname,
                   pg_catalog.format_type(a.atttypid, a.atttypmod) AS data_type,
                   a.attnotnull,
                   pg_get_expr(d.adbin, d.adrelid) AS default_val,
                   col_description(a.attrelid, a.attnum) AS comment,
                   CASE WHEN a.attidentity = 'a' THEN 'ALWAYS'
                        WHEN a.attidentity = 'd' THEN 'BY DEFAULT'
                        ELSE NULL END AS identity_type,
                   CASE WHEN a.attgenerated = 's' THEN pg_get_expr(d.adbin, d.adrelid) ELSE NULL END AS generated_expr
            FROM pg_attribute a
            LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            WHERE a.attrelid = '""{ Sanitize(schema) }"".""{ Sanitize(table) }""'::regclass
              AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attnum";

        var cols = await QueryAsync(conn, colSql, r => (
            name: r.GetString(0),
            type: r.GetString(1),
            notNull: r.GetBoolean(2),
            defaultVal: r.IsDBNull(3) ? null : r.GetString(3),
            comment: r.IsDBNull(4) ? null : r.GetString(4),
            identity: r.IsDBNull(5) ? null : r.GetString(5),
            generated: r.IsDBNull(6) ? null : r.GetString(6)
        ));

        var lines = new List<string>();
        foreach (var col in cols)
        {
            var line = $"    \"{col.name}\" {col.type}";
            if (col.identity != null)
                line += $" GENERATED {col.identity} AS IDENTITY";
            else if (col.generated != null)
                line += $" GENERATED ALWAYS AS ({col.generated}) STORED";
            else if (col.defaultVal != null)
                line += $" DEFAULT {col.defaultVal}";
            if (col.notNull && col.identity == null)
                line += " NOT NULL";
            lines.Add(line);
        }

        // Primary key
        var pkSql = $@"
            SELECT conname,
                   string_agg('""' || a.attname || '""', ', ' ORDER BY array_position(con.conkey, a.attnum))
            FROM pg_constraint con
            JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = ANY(con.conkey)
            WHERE con.conrelid = '""{Sanitize(schema)}"".""{Sanitize(table)}""'::regclass
              AND con.contype = 'p'
            GROUP BY conname";

        var pks = await QueryAsync(conn, pkSql, r => (
            name: r.GetString(0),
            cols: r.GetString(1)
        ));

        foreach (var pk in pks)
            lines.Add($"    CONSTRAINT \"{pk.name}\" PRIMARY KEY ({pk.cols})");

        // Unique constraints
        var uqSql = $@"
            SELECT conname,
                   string_agg('""' || a.attname || '""', ', ' ORDER BY array_position(con.conkey, a.attnum))
            FROM pg_constraint con
            JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = ANY(con.conkey)
            WHERE con.conrelid = '""{Sanitize(schema)}"".""{Sanitize(table)}""'::regclass
              AND con.contype = 'u'
            GROUP BY conname
            ORDER BY conname";

        var uqs = await QueryAsync(conn, uqSql, r => (
            name: r.GetString(0),
            cols: r.GetString(1)
        ));

        foreach (var uq in uqs)
            lines.Add($"    CONSTRAINT \"{uq.name}\" UNIQUE ({uq.cols})");

        // Check constraints
        var ckSql = $@"
            SELECT conname, pg_get_constraintdef(oid)
            FROM pg_constraint
            WHERE conrelid = '""{Sanitize(schema)}"".""{Sanitize(table)}""'::regclass
              AND contype = 'c'
            ORDER BY conname";

        var cks = await QueryAsync(conn, ckSql, r => (
            name: r.GetString(0),
            def: r.GetString(1)
        ));

        foreach (var ck in cks)
            lines.Add($"    CONSTRAINT \"{ck.name}\" {ck.def}");

        var ddl = $"CREATE TABLE \"{schema}\".\"{table}\" (\n{string.Join(",\n", lines)}\n);\n";

        // Column comments
        foreach (var col in cols.Where(c => c.comment != null))
            ddl += $"\nCOMMENT ON COLUMN \"{schema}\".\"{table}\".\"{col.name}\" IS '{Sanitize(col.comment!)}';\n";

        // Table comment
        var tblCommentSql = $@"
            SELECT obj_description('""{Sanitize(schema)}"".""{Sanitize(table)}""'::regclass, 'pg_class')";

        var tblComments = await QueryAsync(conn, tblCommentSql, r => r.IsDBNull(0) ? null : r.GetString(0));
        if (tblComments.Count > 0 && tblComments[0] != null)
            ddl += $"\nCOMMENT ON TABLE \"{schema}\".\"{table}\" IS '{Sanitize(tblComments[0]!)}';\n";

        return ddl;
    }

    // ── Indexes (non-PK, non-unique-constraint) ──────────────────────

    private async Task ExtractIndexesAsync(NpgsqlConnection conn)
    {
        var sql = $@"
            SELECT n.nspname, c.relname AS table_name, i.relname AS index_name,
                   pg_get_indexdef(ix.indexrelid) AS indexdef
            FROM pg_index ix
            JOIN pg_class i ON i.oid = ix.indexrelid
            JOIN pg_class c ON c.oid = ix.indrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE {SchemaWhereClause()}
              AND NOT ix.indisprimary
              AND NOT ix.indisunique
              AND i.relkind = 'i'
            ORDER BY n.nspname, c.relname, i.relname";

        var rows = await QueryAsync(conn, sql, r => (
            schema: r.GetString(0),
            table: r.GetString(1),
            index: r.GetString(2),
            def: r.GetString(3)
        ));

        foreach (var row in rows)
        {
            var ddl = $"{row.def};\n";
            await WriteFileAsync("indexes", $"{SafeFileName(row.schema)}.{SafeFileName(row.index)}.sql", ddl);
        }

        Console.WriteLine($"  Indexes:    {rows.Count}");
    }

    // ── Foreign Keys ─────────────────────────────────────────────────

    private async Task ExtractForeignKeysAsync(NpgsqlConnection conn)
    {
        var sql = $@"
            SELECT n.nspname, c.relname, con.conname, pg_get_constraintdef(con.oid)
            FROM pg_constraint con
            JOIN pg_class c ON c.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE con.contype = 'f' AND {SchemaWhereClause()}
            ORDER BY n.nspname, c.relname, con.conname";

        var rows = await QueryAsync(conn, sql, r => (
            schema: r.GetString(0),
            table: r.GetString(1),
            name: r.GetString(2),
            def: r.GetString(3)
        ));

        foreach (var row in rows)
        {
            var ddl = $"ALTER TABLE \"{row.schema}\".\"{row.table}\"\n    ADD CONSTRAINT \"{row.name}\" {row.def};\n";
            await WriteFileAsync("foreign_keys", $"{SafeFileName(row.schema)}.{SafeFileName(row.name)}.sql", ddl);
        }

        Console.WriteLine($"  ForeignKeys:{rows.Count}");
    }

    // ── Views ────────────────────────────────────────────────────────

    private async Task ExtractViewsAsync(NpgsqlConnection conn)
    {
        var sql = $@"
            SELECT n.nspname, c.relname, pg_get_viewdef(c.oid, true)
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind = 'v' AND {SchemaWhereClause()}
            ORDER BY n.nspname, c.relname";

        var rows = await QueryAsync(conn, sql, r => (
            schema: r.GetString(0),
            name: r.GetString(1),
            def: r.GetString(2)
        ));

        foreach (var row in rows)
        {
            var ddl = $"CREATE OR REPLACE VIEW \"{row.schema}\".\"{row.name}\" AS\n{row.def};\n";
            await WriteFileAsync("views", $"{SafeFileName(row.schema)}.{SafeFileName(row.name)}.sql", ddl);
        }

        Console.WriteLine($"  Views:      {rows.Count}");
    }

    // ── Materialized Views ───────────────────────────────────────────

    private async Task ExtractMaterializedViewsAsync(NpgsqlConnection conn)
    {
        var sql = $@"
            SELECT n.nspname, c.relname, pg_get_viewdef(c.oid, true)
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind = 'm' AND {SchemaWhereClause()}
            ORDER BY n.nspname, c.relname";

        var rows = await QueryAsync(conn, sql, r => (
            schema: r.GetString(0),
            name: r.GetString(1),
            def: r.GetString(2)
        ));

        foreach (var row in rows)
        {
            var ddl = $"CREATE MATERIALIZED VIEW \"{row.schema}\".\"{row.name}\" AS\n{row.def}\nWITH NO DATA;\n";
            await WriteFileAsync("materialized_views", $"{SafeFileName(row.schema)}.{SafeFileName(row.name)}.sql", ddl);
        }

        Console.WriteLine($"  MatViews:   {rows.Count}");
    }

    // ── Functions & Procedures ───────────────────────────────────────

    private async Task ExtractFunctionsAsync(NpgsqlConnection conn)
    {
        var sql = $@"
            SELECT n.nspname, p.proname,
                   pg_get_function_identity_arguments(p.oid) AS identity_args,
                   pg_get_functiondef(p.oid) AS funcdef,
                   CASE p.prokind WHEN 'p' THEN 'procedure' ELSE 'function' END AS kind
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE {SchemaWhereClause()}
              AND p.prokind IN ('f','p','w')
            ORDER BY n.nspname, p.proname, identity_args";

        var rows = await QueryAsync(conn, sql, r => (
            schema: r.GetString(0),
            name: r.GetString(1),
            args: r.GetString(2),
            def: r.GetString(3),
            kind: r.GetString(4)
        ));

        foreach (var row in rows)
        {
            var safeName = $"{SafeFileName(row.schema)}.{SafeFileName(row.name)}";
            // Handle overloads by including a hash of the argument signature
            if (rows.Count(r => r.schema == row.schema && r.name == row.name) > 1)
                safeName += $"_{Math.Abs(row.args.GetHashCode()):x8}";

            var ddl = row.def + ";\n";
            await WriteFileAsync(row.kind == "procedure" ? "procedures" : "functions", $"{safeName}.sql", ddl);
        }

        Console.WriteLine($"  Functions:  {rows.Count}");
    }

    // ── Triggers ─────────────────────────────────────────────────────

    private async Task ExtractTriggersAsync(NpgsqlConnection conn)
    {
        var internalFilter = _includeSystemObjects ? "" : "NOT t.tgisinternal AND";
        var sql = $@"
            SELECT n.nspname, c.relname, t.tgname, pg_get_triggerdef(t.oid, true)
            FROM pg_trigger t
            JOIN pg_class c ON c.oid = t.tgrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE {internalFilter} {SchemaWhereClause()}
            ORDER BY n.nspname, c.relname, t.tgname";

        var rows = await QueryAsync(conn, sql, r => (
            schema: r.GetString(0),
            table: r.GetString(1),
            name: r.GetString(2),
            def: r.GetString(3)
        ));

        foreach (var row in rows)
        {
            var ddl = $"{row.def};\n";
            await WriteFileAsync("triggers", $"{SafeFileName(row.schema)}.{SafeFileName(row.name)}.sql", ddl);
        }

        Console.WriteLine($"  Triggers:   {rows.Count}");
    }
}
