using System.Text;
using System.Text.RegularExpressions;
using HfsqlOdbcStudio.Models;

namespace HfsqlOdbcStudio.Helpers;

/// <summary>
/// Builds ODBC connection strings for the three connection modes.
/// HFSQL keyword names ("Server Name", "Server Port") may vary between driver versions,
/// which is why the generated string is shown to the user and can be copied into raw mode.
/// </summary>
public static partial class ConnectionStringFactory
{
    public const string UserToken = "%USER%";
    public const string PasswordToken = "%PASSWORD%";
    public const string Mask = "********";

    [GeneratedRegex(@"(?<k>(?:^|;)\s*(?:PWD|Password)\s*=\s*)(?<v>\{(?:[^}]|\}\})*\}|[^;]*)", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordRegex();

    [GeneratedRegex(@"(?:^|;)\s*DRIVER\s*=\s*(?:\{(?<v>(?:[^}]|\}\})*)\}|(?<v>[^;]*))", RegexOptions.IgnoreCase)]
    private static partial Regex DriverRegex();

    [GeneratedRegex(@"(?:^|;)\s*DSN\s*=\s*(?:\{(?<v>(?:[^}]|\}\})*)\}|(?<v>[^;]*))", RegexOptions.IgnoreCase)]
    private static partial Regex DsnRegex();

    public static string Build(ConnectionProfile p, bool maskPassword)
    {
        var password = maskPassword ? Mask : Escape(p.Password);
        var sb = new StringBuilder();
        switch (p.Mode)
        {
            case ConnectionMode.Fields when p.Kind == HfsqlKind.Classic:
                // PC SOFT: DRIVER={HFSQL};ANA=<path of WDD file>;REP=<data directory>
                sb.Append("DRIVER={").Append(p.Driver.Replace("}", "}}")).Append("};");
                if (!string.IsNullOrWhiteSpace(p.ClassicAnalysisFile)) sb.Append("ANA=").Append(Escape(p.ClassicAnalysisFile.Trim())).Append(';');
                sb.Append("REP=").Append(Escape(p.ClassicDataDirectory.Trim())).Append(';');
                return sb.ToString();

            case ConnectionMode.Fields:
                sb.Append("DRIVER={").Append(p.Driver.Replace("}", "}}")).Append("};");
                sb.Append("Server Name=").Append(Escape(p.Server)).Append(';');
                sb.Append("Server Port=").Append(p.Port).Append(';');
                sb.Append("Database=").Append(Escape(p.Database)).Append(';');
                sb.Append("UID=").Append(Escape(p.UserName)).Append(';');
                sb.Append("PWD=").Append(password).Append(';');
                return sb.ToString();

            case ConnectionMode.Dsn:
                sb.Append("DSN=").Append(Escape(p.Dsn)).Append(';');
                if (!string.IsNullOrEmpty(p.UserName)) sb.Append("UID=").Append(Escape(p.UserName)).Append(';');
                if (!string.IsNullOrEmpty(p.Password)) sb.Append("PWD=").Append(password).Append(';');
                return sb.ToString();

            default:
                var raw = (p.RawConnectionString ?? "")
                    .Replace(UserToken, Escape(p.UserName), StringComparison.OrdinalIgnoreCase)
                    .Replace(PasswordToken, password, StringComparison.OrdinalIgnoreCase);
                return maskPassword ? MaskSecrets(raw) : raw;
        }
    }

    /// <summary>Builds the "fields" string with tokens instead of credentials, for editing in raw mode.</summary>
    public static string BuildTemplateForRaw(ConnectionProfile p)
    {
        var copy = p.Clone();
        var s = Build(copy, maskPassword: true);
        s = s.Replace("PWD=" + Mask, "PWD=" + PasswordToken, StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(p.UserName))
            s = s.Replace("UID=" + Escape(p.UserName), "UID=" + UserToken, StringComparison.Ordinal);
        return s;
    }

    public static string MaskSecrets(string connectionString) =>
        PasswordRegex().Replace(connectionString, m => m.Groups["v"].Value == PasswordToken ? m.Value : m.Groups["k"].Value + Mask);

    public static string? ExtractDriver(string connectionString)
    {
        var m = DriverRegex().Match(connectionString);
        return m.Success ? m.Groups["v"].Value.Replace("}}", "}").Trim() : null;
    }

    public static string? ExtractDsn(string connectionString)
    {
        var m = DsnRegex().Match(connectionString);
        return m.Success ? m.Groups["v"].Value.Replace("}}", "}").Trim() : null;
    }

    /// <summary>Wraps a value in braces when it contains characters that are special in ODBC connection strings.</summary>
    public static string Escape(string? value)
    {
        value ??= "";
        bool needsBraces = value.IndexOfAny([';', '{', '}']) >= 0 || (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])));
        return needsBraces ? "{" + value.Replace("}", "}}") + "}" : value;
    }
}
