namespace DuplicateVideoCleaner;

internal sealed class AccessibleTextCell : DataGridViewTextBoxCell
{
    protected override AccessibleObject CreateAccessibilityInstance() => new CellAccessibleObject(this);

    private sealed class CellAccessibleObject(DataGridViewCell cell) : DataGridViewCellAccessibleObject(cell)
    {
        private readonly DataGridViewCell _cell = cell;

        public override string? Name => BuildCellName(_cell);
    }

    internal static string BuildCellName(DataGridViewCell cell)
    {
        var header = cell.OwningColumn?.HeaderText ?? "Value";
        var value = Convert.ToString(cell.FormattedValue) ?? string.Empty;
        var file = cell.OwningRow?.Tag is ReviewedVideo reviewed ? reviewed.Candidate.RelativePath : "unknown file";
        var group = Convert.ToString(cell.OwningRow?.Cells["Group"].Value) ?? "unknown";
        return $"{header}: {value}; file {file}; group {group}";
    }
}

internal sealed class AccessibleCheckBoxCell : DataGridViewCheckBoxCell
{
    protected override AccessibleObject CreateAccessibilityInstance() => new CellAccessibleObject(this);

    private sealed class CellAccessibleObject(DataGridViewCell cell) : DataGridViewCellAccessibleObject(cell)
    {
        private readonly DataGridViewCell _cell = cell;

        public override string? Name
        {
            get
            {
                var selected = Convert.ToBoolean(_cell.Value);
                var file = _cell.OwningRow?.Tag is ReviewedVideo reviewed ? reviewed.Candidate.RelativePath : "unknown file";
                var group = Convert.ToString(_cell.OwningRow?.Cells["Group"].Value) ?? "unknown";
                var suggestion = Convert.ToString(_cell.OwningRow?.Cells["Action"].Value) ?? string.Empty;
                return $"Remove selection: {(selected ? "selected" : "not selected")}; {suggestion}; file {file}; group {group}";
            }
        }
    }
}
