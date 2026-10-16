namespace HfsqlOdbcStudio.Models;

/// <summary>Persisted UI settings (settings.json).</summary>
public sealed class AppSettings
{
    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }

    public bool IsMaximized { get; set; }

    public int MaxRows { get; set; } = 1000;
}
