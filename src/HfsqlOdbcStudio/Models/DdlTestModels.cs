using CommunityToolkit.Mvvm.ComponentModel;

namespace HfsqlOdbcStudio.Models;

public enum DdlStepStatus
{
    Pending,
    Running,
    Passed,
    Failed,
    Skipped,
}

/// <summary>One SQL statement (or schema check) attempted within a DDL test step.</summary>
public sealed record DdlAttempt(string Sql, bool Success, string Result);

/// <summary>A checklist entry of the DDL capability test.</summary>
public sealed partial class DdlTestStep : ObservableObject
{
    public DdlTestStep(int number, string title)
    {
        Number = number;
        Title = title;
    }

    public int Number { get; }

    public string Title { get; }

    [ObservableProperty]
    private DdlStepStatus _status = DdlStepStatus.Pending;

    [ObservableProperty]
    private string _detail = "";

    [ObservableProperty]
    private double _elapsedMs;

    public List<DdlAttempt> Attempts { get; } = [];

    public void AddAttempt(DdlAttempt attempt)
    {
        Attempts.Add(attempt);
        Detail = string.Join(Environment.NewLine + Environment.NewLine, Attempts.Select(a =>
            (a.Success ? "OK    " : "FAIL  ") + a.Sql + Environment.NewLine + "      " + a.Result.Replace("\n", "\n      ")));
    }

    public void Reset()
    {
        Attempts.Clear();
        Status = DdlStepStatus.Pending;
        Detail = "";
        ElapsedMs = 0;
    }
}

/// <summary>The outcome of a whole DDL test run, used for the report.</summary>
public sealed class DdlTestRun
{
    public DateTime Started { get; set; }

    public DateTime Finished { get; set; }

    public ConnectionInfo? Info { get; set; }

    public string MaskedConnectionString { get; set; } = "";

    public string ServerTarget { get; set; } = "";

    public string? AbortReason { get; set; }

    public List<DdlAttempt> PreCleanup { get; } = [];

    public IReadOnlyList<DdlTestStep> Steps { get; set; } = [];
}
