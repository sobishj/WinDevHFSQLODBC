namespace HfsqlOdbcStudio.Models;

/// <summary>A table (or view) returned by GetSchema("Tables").</summary>
public sealed record DbTableInfo(string Name, string Type, string? Catalog, string? Schema, bool IsSystem);

/// <summary>A column returned by GetSchema("Columns").</summary>
public sealed record DbColumnInfo(
    string Name,
    string TypeName,
    int? Size,
    int? DecimalDigits,
    bool? IsNullable,
    int Ordinal)
{
    private static readonly string[] SizedTypes = ["CHAR", "VARCHAR", "NCHAR", "NVARCHAR", "VARBINARY", "BINARY", "CHARACTER", "STRING"];
    private static readonly string[] ScaledTypes = ["DECIMAL", "NUMERIC", "NUMBER"];

    /// <summary>SQL type expression, e.g. VARCHAR(50) or DECIMAL(10,2).</summary>
    public string TypeDisplay
    {
        get
        {
            var t = string.IsNullOrWhiteSpace(TypeName) ? "?" : TypeName.Trim();
            var upper = t.ToUpperInvariant();
            if (t.Contains('(')) return t;
            if (Size is > 0 && ScaledTypes.Contains(upper))
                return DecimalDigits is > 0 ? $"{t}({Size},{DecimalDigits})" : $"{t}({Size})";
            if (Size is > 0 && SizedTypes.Contains(upper))
                return $"{t}({Size})";
            return t;
        }
    }

    public string NullableDisplay => IsNullable switch
    {
        true => "NULL",
        false => "NOT NULL",
        _ => "",
    };

    public bool IsNumeric
    {
        get
        {
            var u = TypeName.ToUpperInvariant();
            return u.Contains("INT") || u.Contains("DEC") || u.Contains("NUM") || u.Contains("FLOAT")
                || u.Contains("DOUBLE") || u.Contains("REAL") || u.Contains("MONEY") || u.Contains("CURRENCY");
        }
    }

    public bool IsDateLike
    {
        get
        {
            var u = TypeName.ToUpperInvariant();
            return u.Contains("DATE") || u.Contains("TIME");
        }
    }
}

/// <summary>A configured ODBC data source name.</summary>
public sealed record DsnInfo(string Name, string Driver, string Scope)
{
    public string Display => $"{Name}  ({Scope} · {Driver})";

    public override string ToString() => Display;
}

/// <summary>Information gathered after a successful connection.</summary>
public sealed record ConnectionInfo(
    string DriverName,
    string DriverFile,
    string DriverVersion,
    string DbmsName,
    string DbmsVersion,
    string ServerVersion,
    string Database,
    string DataSource,
    bool UseNativeLayer = false,
    string AccessLayerNote = "",
    string DriverPath = "")
{
    public string AccessLayerDisplay => UseNativeLayer ? "Native ODBC (odbc32.dll)" : "System.Data.Odbc";
}
