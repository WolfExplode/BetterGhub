namespace BetterGhub.Services;

/// <summary>
/// Undo and redo over snapshots of the editable settings. Each recorded change is one step, except that
/// changes from the same source in quick succession (typing in one box) are merged into one.
/// </summary>
internal sealed class EditHistory(string initial)
{
    private const int Limit = 200;
    private static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(1.5);
    private readonly List<string> undo = [], redo = [];
    private string current = initial;
    private object? mergeSource;
    private DateTime mergeUntil;

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    /// <summary>Records <paramref name="state"/> after an edit; nothing when it didn't change anything.</summary>
    public void Record(string state, object? source)
    {
        if (state == current) return;
        DateTime now = DateTime.UtcNow;
        bool merge = source is not null && ReferenceEquals(source, mergeSource) && now < mergeUntil && undo.Count > 0;
        if (!merge)
        {
            undo.Add(current);
            if (undo.Count > Limit) undo.RemoveAt(0);
        }
        redo.Clear();
        current = state;
        mergeSource = source;
        mergeUntil = now + MergeWindow;
    }

    /// <summary>Adopts <paramref name="state"/> without an undo step, for changes that aren't edits (the mouse's DPI button, a fresh read).</summary>
    public void Rebase(string state)
    {
        current = state;
        mergeSource = null;
    }

    /// <summary>The state to go back to, or null when there's nothing to undo.</summary>
    public string? Undo() => Step(undo, redo);

    /// <summary>The state to go forward to, or null when there's nothing to redo.</summary>
    public string? Redo() => Step(redo, undo);

    private string? Step(List<string> from, List<string> to)
    {
        if (from.Count == 0) return null;
        to.Add(current);
        current = from[^1];
        from.RemoveAt(from.Count - 1);
        mergeSource = null;
        return current;
    }
}
