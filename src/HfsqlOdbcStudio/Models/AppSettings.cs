namespace HfsqlOdbcStudio.Models;

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>Persisted UI settings (settings.json).</summary>
public sealed class AppSettings
{
    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }

    public bool IsMaximized { get; set; }

    public ThemeMode Theme { get; set; } = ThemeMode.System;

    public int MaxRows { get; set; } = 1000;

    public bool ShowSystemTables { get; set; }
}
