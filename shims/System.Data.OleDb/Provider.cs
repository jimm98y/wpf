// What an OLE DB provider is off Windows: the managed provider that reaches the same server. SQL Server's OLE DB
// providers (SQLOLEDB, SQLNCLI*, MSOLEDBSQL*) are Microsoft.Data.SqlClient (the app's own, else System.Data.SqlClient):
// the connection string read as the OLE DB provider reads it and written as SqlClient takes it, OLE DB's positional ?
// markers made SqlClient's named ones, a stored procedure called as the provider calls it ({? = call p(?, ?)}),
// OleDbType carried over as the DbType SqlClient takes, the provider's errors an OleDbException with the provider's
// error records. A provider with no managed equivalent (Jet, ACE, a vendor's) is not registered here, as on a Windows
// machine without it; MSDASQL is refused as .NET refuses it everywhere.

using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace System.Data.OleDb
{
    internal sealed class Provider
    {
        public string Name { get; }
        public DbProviderFactory Factory { get; }
        private readonly bool _encryptByDefault;

        private Provider(string name, DbProviderFactory factory, bool encryptByDefault)
        {
            Name = name;
            Factory = factory;
            _encryptByDefault = encryptByDefault;
        }

        /// <summary>The OLE DB providers there is a managed equivalent of, with their descriptions (OleDbEnumerator).</summary>
        internal static readonly (string Name, string Description)[] Known =
        {
            ("SQLOLEDB", "Microsoft OLE DB Provider for SQL Server"),
            ("SQLNCLI11", "SQL Server Native Client 11.0"),
            ("MSOLEDBSQL", "Microsoft OLE DB Driver for SQL Server"),
            ("MSOLEDBSQL19", "Microsoft OLE DB Driver 19 for SQL Server"),
        };

        internal static bool IsSqlServer(string provider)
        {
            string p = Base(provider);
            return p is "SQLOLEDB" or "MSOLEDBSQL" or "MSOLEDBSQL19" || p.StartsWith("SQLNCLI", StringComparison.Ordinal);
        }

        // SQLOLEDB.1 is SQLOLEDB, as the registry's version-independent ProgID names it
        private static string Base(string provider)
        {
            string p = provider.Trim().ToUpperInvariant();
            int dot = p.IndexOf('.');
            return dot > 0 ? p[..dot] : p;
        }

        /// <summary>Checks a connection string as setting it checks it on Windows: a provider must be named, and not MSDASQL.</summary>
        internal static void Validate(OleDbConnectionStringBuilder builder)
        {
            string provider = builder.Provider;
            if (string.IsNullOrWhiteSpace(provider))
                throw new ArgumentException("An OLE DB Provider was not specified in the ConnectionString.  An example would be, 'Provider=SQLOLEDB;'.");
            if (Base(provider) == "MSDASQL")
                throw new ArgumentException("The .NET Framework Data Provider for OLEDB (System.Data.OleDb) does not support the Microsoft OLE DB Provider for ODBC Drivers (MSDASQL). Use the .NET Framework Data Provider for ODBC (System.Data.Odbc).");
        }

        internal static Provider Resolve(string provider)
        {
            if (IsSqlServer(provider) && SqlFactory() is { } factory)
                return new Provider(provider, factory, Base(provider) == "MSOLEDBSQL19");
            throw new InvalidOperationException($"The '{provider}' provider is not registered on the local machine.");
        }

        private static DbProviderFactory SqlFactory()
        {
            foreach (string name in new[] { "Microsoft.Data.SqlClient.SqlClientFactory, Microsoft.Data.SqlClient", "System.Data.SqlClient.SqlClientFactory, System.Data.SqlClient" })
            {
                if (DbProviderFactories.TryGetFactory(name.Split(',')[1].Trim(), out DbProviderFactory registered)) return registered;
                try
                {
                    if (Type.GetType(name, throwOnError: false)?.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is DbProviderFactory f) return f;
                }
                catch (Exception e) when (e is IO.FileLoadException or IO.FileNotFoundException or BadImageFormatException) { }
            }
            return null;
        }

        // ---- the connection string ---------------------------------------------------------------------------------

        // OLE DB's keywords (SQLOLEDB's and MSOLEDBSQL's, and the ODBC-style synonyms they take) -> SqlClient's
        private static readonly Dictionary<string, string> s_keywords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Data Source"] = "Data Source", ["Server"] = "Data Source", ["Address"] = "Data Source", ["Addr"] = "Data Source", ["Network Address"] = "Data Source",
            ["Initial Catalog"] = "Initial Catalog", ["Database"] = "Initial Catalog",
            ["User ID"] = "User ID", ["UID"] = "User ID", ["Password"] = "Password", ["PWD"] = "Password",
            ["Persist Security Info"] = "Persist Security Info",
            ["Connect Timeout"] = "Connect Timeout", ["Connection Timeout"] = "Connect Timeout",
            ["Application Name"] = "Application Name", ["APP"] = "Application Name",
            ["Workstation ID"] = "Workstation ID", ["WSID"] = "Workstation ID",
            ["Packet Size"] = "Packet Size", ["Current Language"] = "Current Language", ["Language"] = "Current Language",
            ["Failover Partner"] = "Failover Partner", ["FailoverPartner"] = "Failover Partner",
            ["Application Intent"] = "Application Intent", ["ApplicationIntent"] = "Application Intent",
            ["MultiSubnetFailover"] = "Multi Subnet Failover", ["Authentication"] = "Authentication",
            ["Host Name In Certificate"] = "Host Name In Certificate", ["HostNameInCertificate"] = "Host Name In Certificate",
            ["Initial File Name"] = "AttachDbFilename", ["AttachDBFileName"] = "AttachDbFilename",
        };

        /// <summary>The managed provider's connection string for an OLE DB one.</summary>
        internal string Translate(OleDbConnectionStringBuilder oledb)
        {
            var sql = new DbConnectionStringBuilder();
            string encrypt = null;
            foreach (string key in oledb.Keys.Cast<string>())
            {
                if (!oledb.TryGetValue(key, out object raw) || raw == null) continue;
                string value = Convert.ToString(raw, CultureInfo.InvariantCulture);
                if (s_keywords.TryGetValue(key, out string target)) { sql[target] = value; continue; }
                switch (key.ToUpperInvariant())
                {
                    case "INTEGRATED SECURITY":
                    case "TRUSTED_CONNECTION":
                        if (Truth(value) ?? value.Equals("SSPI", StringComparison.OrdinalIgnoreCase)) sql["Integrated Security"] = "true";
                        break;
                    case "USE ENCRYPTION FOR DATA":
                    case "ENCRYPT":
                        encrypt = value.ToUpperInvariant() switch
                        {
                            "STRICT" => "Strict",
                            "MANDATORY" or "YES" or "TRUE" => "Mandatory",
                            _ => "Optional",
                        };
                        break;
                    case "TRUST SERVER CERTIFICATE":
                    case "TRUSTSERVERCERTIFICATE":
                        sql["Trust Server Certificate"] = Truth(value) == true ? "true" : "false";
                        break;
                    case "MARS CONNECTION":
                    case "MULTIPLEACTIVERESULTSETS":
                        sql["Multiple Active Result Sets"] = Truth(value) == true ? "true" : "false";
                        break;
                }
            }
            // SQLOLEDB, SQLNCLI and MSOLEDBSQL 18 do not encrypt unless told to; SqlClient does, and checks the
            // server's certificate -- a server they reached with its self-signed certificate it would refuse
            sql["Encrypt"] = encrypt ?? (_encryptByDefault ? "Mandatory" : "Optional");
            return sql.ConnectionString;
        }

        private static bool? Truth(string value) => value.Trim().ToUpperInvariant() switch
        {
            "TRUE" or "YES" or "1" => true,
            "FALSE" or "NO" or "0" => false,
            _ => null,
        };

        // ---- commands ----------------------------------------------------------------------------------------------

        /// <summary>The name the i'th positional marker is given.</summary>
        internal static string Marker(int i) => "@P" + (i + 1).ToString(CultureInfo.InvariantCulture);

        /// <summary>OLE DB's ? markers made named ones, outside quoted text, bracketed names and comments; the number
        /// of markers.</summary>
        internal static string NameMarkers(string text, out int count)
        {
            count = 0;
            if (string.IsNullOrEmpty(text) || text.IndexOf('?') < 0) return text ?? "";
            var sb = new StringBuilder(text.Length + 16);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                char close = c switch { '\'' => '\'', '"' => '"', '[' => ']', _ => '\0' };
                if (close != '\0')
                {
                    int end = i + 1;
                    while (end < text.Length)
                    {
                        if (text[end] == close)
                        {
                            if (end + 1 < text.Length && text[end + 1] == close) { end += 2; continue; }   // '' "" ]] escape
                            break;
                        }
                        end++;
                    }
                    sb.Append(text, i, Math.Min(end + 1, text.Length) - i);
                    i = end;
                    continue;
                }
                if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
                {
                    int end = text.IndexOf('\n', i);
                    if (end < 0) end = text.Length - 1;
                    sb.Append(text, i, end - i + 1);
                    i = end;
                    continue;
                }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    end = end < 0 ? text.Length - 1 : end + 1;
                    sb.Append(text, i, end - i + 1);
                    i = end;
                    continue;
                }
                if (c == '?') { sb.Append(Marker(count++)); continue; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>A stored procedure's call, as the provider makes {? = call name(?, ...)} of it: the return value to
        /// the parameter that takes it (the first, where its direction is ReturnValue), the rest by position.</summary>
        internal static string ProcedureCall(string name, IList<OleDbParameter> parameters)
        {
            var sb = new StringBuilder("EXEC ");
            int first = 0;
            if (parameters.Count > 0 && parameters[0].Direction == ParameterDirection.ReturnValue)
            {
                sb.Append(Marker(0)).Append(" = ");
                first = 1;
            }
            sb.Append(name);
            for (int i = first; i < parameters.Count; i++)
            {
                sb.Append(i == first ? " " : ", ").Append(Marker(i));
                if (parameters[i].Direction is ParameterDirection.Output or ParameterDirection.InputOutput) sb.Append(" OUTPUT");
            }
            return sb.ToString();
        }

        /// <summary>The DbType SqlClient takes for an OleDbType (it has no unsigned or SByte types: the next wider).</summary>
        internal static DbType SqlDbTypeOf(OleDbType type) => type switch
        {
            OleDbType.TinyInt => DbType.Int16,
            OleDbType.UnsignedSmallInt => DbType.Int32,
            OleDbType.UnsignedInt => DbType.Int64,
            OleDbType.UnsignedBigInt or OleDbType.VarNumeric => DbType.Decimal,
            OleDbType.Filetime or OleDbType.Date => DbType.DateTime,
            OleDbType.Error => DbType.Int32,
            OleDbType.Variant or OleDbType.PropVariant or OleDbType.IDispatch or OleDbType.IUnknown or OleDbType.Empty => DbType.Object,
            _ => OleDbParameter.DbTypeOf(type),
        };

        // ---- errors ------------------------------------------------------------------------------------------------

        private const int DB_E_ERRORSINCOMMAND = unchecked((int)0x80040E14), E_FAIL = unchecked((int)0x80004005);

        /// <summary>The managed provider's error as OLE DB reports it: an OleDbException with an error record for each
        /// of the provider's.</summary>
        internal Exception Wrap(Exception e)
        {
            if (e is OleDbException or InvalidOperationException or ArgumentException or NotSupportedException or OperationCanceledException) return e;
            if (e is not DbException db) return e;
            string source = Known.FirstOrDefault(k => k.Name.Equals(Base(Name), StringComparison.OrdinalIgnoreCase)).Description ?? Name;
            var errors = new List<OleDbError>();
            if (e.GetType().GetProperty("Errors")?.GetValue(e) is Collections.IEnumerable records)
                foreach (object r in records)
                {
                    Type t = r.GetType();
                    string message = t.GetProperty("Message")?.GetValue(r) as string ?? e.Message;
                    int number = t.GetProperty("Number")?.GetValue(r) is int n ? n : 0;
                    int @class = t.GetProperty("Class")?.GetValue(r) is byte b ? b : 0;
                    errors.Add(new OleDbError(message, number, source, SqlState(number, @class)));
                }
            if (errors.Count == 0) errors.Add(new OleDbError(e.Message, 0, source, db.SqlState ?? "HY000"));
            return new OleDbException(errors[0].Message, errors.Count > 0 && errors[0].NativeError != 0 ? DB_E_ERRORSINCOMMAND : E_FAIL, new OleDbErrorCollection(errors), e);
        }

        // SQL Server's state for its error, as its OLE DB providers report it
        private static string SqlState(int number, int @class) => number switch
        {
            2627 or 2601 or 547 or 515 => "23000",
            208 or 207 or 156 or 102 => "42000",
            18456 => "28000",
            1205 => "40001",
            _ => @class >= 20 ? "08S01" : "42000",
        };

        // ---- schema rowsets ----------------------------------------------------------------------------------------

        /// <summary>An OLE DB schema rowset, from SQL Server's catalog views, in the columns the OLE DB provider gives it.</summary>
        internal DataTable Schema(DbConnection connection, DbTransaction transaction, Guid schema, object[] restrictions)
        {
            (string query, int restrictionCount)? q =
                schema == OleDbSchemaGuid.Tables ? (SchemaQueries.Tables, 4)
                : schema == OleDbSchemaGuid.Columns ? (SchemaQueries.Columns, 4)
                : schema == OleDbSchemaGuid.Views ? (SchemaQueries.Views, 3)
                : schema == OleDbSchemaGuid.Procedures ? (SchemaQueries.Procedures, 4)
                : schema == OleDbSchemaGuid.Catalogs ? (SchemaQueries.Catalogs, 1)
                : schema == OleDbSchemaGuid.Schemata ? (SchemaQueries.Schemata, 3)
                : schema == OleDbSchemaGuid.Primary_Keys ? (SchemaQueries.PrimaryKeys, 3)
                : schema == OleDbSchemaGuid.Procedure_Parameters ? (SchemaQueries.ProcedureParameters, 4)
                : null;
            if (q is not { } query)
                throw new ArgumentException($"The {Name} provider does not support the schema rowset {schema:B}.", nameof(schema));
            if (restrictions != null && restrictions.Length > query.restrictionCount)
                throw new ArgumentException($"The parameter is incorrect: {restrictions.Length} restrictions for a schema rowset that takes {query.restrictionCount}.", nameof(restrictions));
            using DbCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = query.query;
            for (int i = 0; i < query.restrictionCount; i++)
            {
                DbParameter p = command.CreateParameter();
                p.ParameterName = "@R" + (i + 1).ToString(CultureInfo.InvariantCulture);
                object value = restrictions != null && i < restrictions.Length ? restrictions[i] : null;
                p.Value = value is null or DBNull ? DBNull.Value : Convert.ToString(value, CultureInfo.InvariantCulture);
                p.DbType = DbType.String;
                command.Parameters.Add(p);
            }
            var table = new DataTable(schema == OleDbSchemaGuid.Tables ? "Tables" : "Table") { Locale = CultureInfo.InvariantCulture };
            using (DbDataReader reader = command.ExecuteReader()) table.Load(reader);
            foreach (DataColumn column in table.Columns) column.ReadOnly = false;
            return table;
        }
    }

    // SQL Server's catalog, in the OLE DB schema rowsets' columns and types (as SQLOLEDB and MSOLEDBSQL answer them)
    internal static class SchemaQueries
    {
        // the DBTYPE (OleDbType) of a SQL Server type name
        private const string DataType = @"CAST(CASE t.name
    WHEN 'int' THEN 3 WHEN 'smallint' THEN 2 WHEN 'tinyint' THEN 17 WHEN 'bigint' THEN 20 WHEN 'bit' THEN 11
    WHEN 'decimal' THEN 131 WHEN 'numeric' THEN 131 WHEN 'money' THEN 6 WHEN 'smallmoney' THEN 6
    WHEN 'float' THEN 5 WHEN 'real' THEN 4 WHEN 'datetime' THEN 135 WHEN 'smalldatetime' THEN 135 WHEN 'datetime2' THEN 135
    WHEN 'date' THEN 133 WHEN 'time' THEN 145 WHEN 'datetimeoffset' THEN 146
    WHEN 'char' THEN 129 WHEN 'varchar' THEN 129 WHEN 'text' THEN 129
    WHEN 'nchar' THEN 130 WHEN 'nvarchar' THEN 130 WHEN 'ntext' THEN 130 WHEN 'sysname' THEN 130
    WHEN 'binary' THEN 128 WHEN 'varbinary' THEN 128 WHEN 'image' THEN 128 WHEN 'timestamp' THEN 128
    WHEN 'uniqueidentifier' THEN 72 WHEN 'xml' THEN 141 WHEN 'sql_variant' THEN 12 ELSE 128 END AS int)";

        public const string Tables = @"SELECT DB_NAME() AS TABLE_CATALOG, s.name AS TABLE_SCHEMA, o.name AS TABLE_NAME,
    CASE WHEN o.type = 'V' THEN 'VIEW' WHEN o.is_ms_shipped = 1 OR s.name IN ('sys', 'INFORMATION_SCHEMA') THEN 'SYSTEM TABLE' ELSE 'TABLE' END AS TABLE_TYPE,
    CAST(NULL AS uniqueidentifier) AS TABLE_GUID, CAST(NULL AS nvarchar(1)) AS DESCRIPTION, CAST(NULL AS bigint) AS TABLE_PROPID,
    o.create_date AS DATE_CREATED, o.modify_date AS DATE_MODIFIED
FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE o.type IN ('U', 'V', 'S')
  AND (@R1 IS NULL OR @R1 = DB_NAME()) AND (@R2 IS NULL OR s.name = @R2) AND (@R3 IS NULL OR o.name = @R3)
  AND (@R4 IS NULL OR @R4 = CASE WHEN o.type = 'V' THEN 'VIEW' WHEN o.is_ms_shipped = 1 OR s.name IN ('sys', 'INFORMATION_SCHEMA') THEN 'SYSTEM TABLE' ELSE 'TABLE' END)
ORDER BY 4, 1, 2, 3";

        public const string Columns = @"SELECT DB_NAME() AS TABLE_CATALOG, s.name AS TABLE_SCHEMA, o.name AS TABLE_NAME, c.name AS COLUMN_NAME,
    CAST(NULL AS uniqueidentifier) AS COLUMN_GUID, CAST(NULL AS bigint) AS COLUMN_PROPID, CAST(c.column_id AS bigint) AS ORDINAL_POSITION,
    CAST(CASE WHEN c.default_object_id <> 0 THEN 1 ELSE 0 END AS bit) AS COLUMN_HASDEFAULT, OBJECT_DEFINITION(c.default_object_id) AS COLUMN_DEFAULT,
    CAST((CASE WHEN c.is_nullable = 1 THEN 0x20 + 0x40 ELSE 0 END) + (CASE WHEN t.name IN ('char', 'nchar', 'binary') THEN 0x10 ELSE 0 END)
       + (CASE WHEN t.name IN ('text', 'ntext', 'image', 'xml') OR c.max_length = -1 THEN 0x80 ELSE 0 END) + (CASE WHEN c.is_computed = 1 OR t.name = 'timestamp' THEN 0 ELSE 0x4 END) AS bigint) AS COLUMN_FLAGS,
    c.is_nullable AS IS_NULLABLE, " + DataType + @" AS DATA_TYPE, CAST(NULL AS uniqueidentifier) AS TYPE_GUID,
    CAST(CASE WHEN t.name IN ('char', 'varchar', 'binary', 'varbinary') THEN CASE WHEN c.max_length = -1 THEN 2147483647 ELSE c.max_length END
              WHEN t.name IN ('nchar', 'nvarchar') THEN CASE WHEN c.max_length = -1 THEN 1073741823 ELSE c.max_length / 2 END
              WHEN t.name IN ('text', 'image') THEN 2147483647 WHEN t.name = 'ntext' THEN 1073741823 END AS bigint) AS CHARACTER_MAXIMUM_LENGTH,
    CAST(CASE WHEN t.name IN ('char', 'varchar', 'binary', 'varbinary', 'nchar', 'nvarchar') THEN CASE WHEN c.max_length = -1 THEN 2147483647 ELSE c.max_length END
              WHEN t.name IN ('text', 'image', 'ntext') THEN 2147483647 END AS bigint) AS CHARACTER_OCTET_LENGTH,
    CAST(CASE WHEN t.name IN ('decimal', 'numeric', 'int', 'smallint', 'tinyint', 'bigint', 'float', 'real', 'money', 'smallmoney') THEN c.precision END AS int) AS NUMERIC_PRECISION,
    CAST(CASE WHEN t.name IN ('decimal', 'numeric') THEN c.scale END AS smallint) AS NUMERIC_SCALE,
    CAST(CASE WHEN t.name IN ('datetime', 'smalldatetime', 'datetime2', 'time', 'datetimeoffset') THEN c.scale END AS bigint) AS DATETIME_PRECISION,
    CAST(NULL AS nvarchar(1)) AS CHARACTER_SET_CATALOG, CAST(NULL AS nvarchar(1)) AS CHARACTER_SET_SCHEMA, CAST(NULL AS nvarchar(1)) AS CHARACTER_SET_NAME,
    CAST(NULL AS nvarchar(1)) AS COLLATION_CATALOG, CAST(NULL AS nvarchar(1)) AS COLLATION_SCHEMA, c.collation_name AS COLLATION_NAME,
    CAST(NULL AS nvarchar(1)) AS DOMAIN_CATALOG, CAST(NULL AS nvarchar(1)) AS DOMAIN_SCHEMA, CAST(NULL AS nvarchar(1)) AS DOMAIN_NAME, CAST(NULL AS nvarchar(1)) AS DESCRIPTION
FROM sys.columns c JOIN sys.objects o ON o.object_id = c.object_id JOIN sys.schemas s ON s.schema_id = o.schema_id
JOIN sys.types t ON t.user_type_id = c.system_type_id AND t.system_type_id = c.system_type_id
WHERE o.type IN ('U', 'V', 'S')
  AND (@R1 IS NULL OR @R1 = DB_NAME()) AND (@R2 IS NULL OR s.name = @R2) AND (@R3 IS NULL OR o.name = @R3) AND (@R4 IS NULL OR c.name = @R4)
ORDER BY 1, 2, 3, 7";

        public const string Views = @"SELECT DB_NAME() AS TABLE_CATALOG, s.name AS TABLE_SCHEMA, v.name AS TABLE_NAME, OBJECT_DEFINITION(v.object_id) AS VIEW_DEFINITION,
    CAST(CASE WHEN m.with_check_option = 1 THEN 1 ELSE 0 END AS bit) AS CHECK_OPTION, CAST(0 AS bit) AS IS_UPDATABLE,
    CAST(NULL AS nvarchar(1)) AS DESCRIPTION, v.create_date AS DATE_CREATED, v.modify_date AS DATE_MODIFIED
FROM sys.views v JOIN sys.schemas s ON s.schema_id = v.schema_id LEFT JOIN sys.sql_modules m ON m.object_id = v.object_id
WHERE (@R1 IS NULL OR @R1 = DB_NAME()) AND (@R2 IS NULL OR s.name = @R2) AND (@R3 IS NULL OR v.name = @R3)
ORDER BY 1, 2, 3";

        public const string Procedures = @"SELECT DB_NAME() AS PROCEDURE_CATALOG, s.name AS PROCEDURE_SCHEMA, p.name + ';1' AS PROCEDURE_NAME,
    CAST(CASE WHEN p.type IN ('FN', 'IF', 'TF', 'FS', 'FT') THEN 3 ELSE 2 END AS smallint) AS PROCEDURE_TYPE,
    OBJECT_DEFINITION(p.object_id) AS PROCEDURE_DEFINITION, CAST(NULL AS nvarchar(1)) AS DESCRIPTION,
    p.create_date AS DATE_CREATED, p.modify_date AS DATE_MODIFIED
FROM sys.objects p JOIN sys.schemas s ON s.schema_id = p.schema_id
WHERE p.type IN ('P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT')
  AND (@R1 IS NULL OR @R1 = DB_NAME()) AND (@R2 IS NULL OR s.name = @R2) AND (@R3 IS NULL OR p.name + ';1' = @R3 OR p.name = @R3)
  AND (@R4 IS NULL OR @R4 = CAST(CASE WHEN p.type IN ('FN', 'IF', 'TF', 'FS', 'FT') THEN 3 ELSE 2 END AS nvarchar(4)))
ORDER BY 1, 2, 3";

        public const string Catalogs = @"SELECT d.name AS CATALOG_NAME, CAST(NULL AS nvarchar(1)) AS DESCRIPTION
FROM sys.databases d WHERE (@R1 IS NULL OR d.name = @R1) ORDER BY 1";

        public const string Schemata = @"SELECT DB_NAME() AS CATALOG_NAME, s.name AS SCHEMA_NAME, USER_NAME(s.principal_id) AS SCHEMA_OWNER,
    CAST(NULL AS nvarchar(1)) AS DEFAULT_CHARACTER_SET_CATALOG, CAST(NULL AS nvarchar(1)) AS DEFAULT_CHARACTER_SET_SCHEMA, CAST(NULL AS nvarchar(1)) AS DEFAULT_CHARACTER_SET_NAME
FROM sys.schemas s
WHERE (@R1 IS NULL OR @R1 = DB_NAME()) AND (@R2 IS NULL OR s.name = @R2) AND (@R3 IS NULL OR USER_NAME(s.principal_id) = @R3)
ORDER BY 1, 2";

        public const string PrimaryKeys = @"SELECT DB_NAME() AS TABLE_CATALOG, s.name AS TABLE_SCHEMA, o.name AS TABLE_NAME, c.name AS COLUMN_NAME,
    CAST(NULL AS uniqueidentifier) AS COLUMN_GUID, CAST(NULL AS bigint) AS COLUMN_PROPID, CAST(ic.key_ordinal AS int) AS ORDINAL, k.name AS PK_NAME
FROM sys.key_constraints k JOIN sys.objects o ON o.object_id = k.parent_object_id JOIN sys.schemas s ON s.schema_id = o.schema_id
JOIN sys.index_columns ic ON ic.object_id = k.parent_object_id AND ic.index_id = k.unique_index_id
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE k.type = 'PK' AND (@R1 IS NULL OR @R1 = DB_NAME()) AND (@R2 IS NULL OR s.name = @R2) AND (@R3 IS NULL OR o.name = @R3)
ORDER BY 1, 2, 3, 7";

        // a procedure's return value as the providers report it: @RETURN_VALUE, an int, first (the catalog has a row for a
        // function's only)
        public const string ProcedureParameters = @"SELECT * FROM (
SELECT DB_NAME() AS PROCEDURE_CATALOG, s.name AS PROCEDURE_SCHEMA, p.name + ';1' AS PROCEDURE_NAME,
    CASE WHEN pr.parameter_id = 0 THEN '@RETURN_VALUE' ELSE pr.name END AS PARAMETER_NAME, CAST(pr.parameter_id AS smallint) AS ORDINAL_POSITION,
    CAST(CASE WHEN pr.parameter_id = 0 THEN 4 WHEN pr.is_output = 1 THEN 2 ELSE 1 END AS smallint) AS PARAMETER_TYPE,
    CAST(pr.has_default_value AS bit) AS PARAMETER_HASDEFAULT, CAST(NULL AS nvarchar(1)) AS PARAMETER_DEFAULT, CAST(1 AS bit) AS IS_NULLABLE,
    " + DataType + @" AS DATA_TYPE,
    CAST(CASE WHEN t.name IN ('char', 'varchar', 'binary', 'varbinary') THEN pr.max_length WHEN t.name IN ('nchar', 'nvarchar') THEN pr.max_length / 2 END AS bigint) AS CHARACTER_MAXIMUM_LENGTH,
    CAST(CASE WHEN t.name IN ('char', 'varchar', 'binary', 'varbinary', 'nchar', 'nvarchar') THEN pr.max_length END AS bigint) AS CHARACTER_OCTET_LENGTH,
    CAST(pr.precision AS smallint) AS NUMERIC_PRECISION, CAST(pr.scale AS smallint) AS NUMERIC_SCALE, CAST(NULL AS nvarchar(1)) AS DESCRIPTION,
    t.name AS TYPE_NAME, t.name AS LOCAL_TYPE_NAME
FROM sys.objects p JOIN sys.schemas s ON s.schema_id = p.schema_id JOIN sys.parameters pr ON pr.object_id = p.object_id
JOIN sys.types t ON t.user_type_id = pr.user_type_id
WHERE p.type IN ('P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT')
UNION ALL
SELECT DB_NAME(), s.name, p.name + ';1', '@RETURN_VALUE', CAST(0 AS smallint), CAST(4 AS smallint), CAST(0 AS bit), CAST(NULL AS nvarchar(1)), CAST(0 AS bit),
    CAST(3 AS int), CAST(NULL AS bigint), CAST(NULL AS bigint), CAST(10 AS smallint), CAST(0 AS smallint), CAST(NULL AS nvarchar(1)), 'int', 'int'
FROM sys.objects p JOIN sys.schemas s ON s.schema_id = p.schema_id
WHERE p.type IN ('P', 'PC')
) x
WHERE (@R1 IS NULL OR @R1 = PROCEDURE_CATALOG) AND (@R2 IS NULL OR PROCEDURE_SCHEMA = @R2)
  AND (@R3 IS NULL OR PROCEDURE_NAME = @R3 OR PROCEDURE_NAME = @R3 + ';1') AND (@R4 IS NULL OR PARAMETER_NAME = @R4)
ORDER BY 1, 2, 3, 5";
    }
}
