namespace EliteSIP.PanelLink;

public sealed record UpdateReceipt(Guid OperationId, Version From, Version To)
{
    public const string PendingName = "update-operation";
    public const string ConfirmedName = "update-confirmed";

    public override string ToString() => $"{OperationId:N};{From.ToString(3)};{To.ToString(3)}";

    public static UpdateReceipt? Parse(string? text)
    {
        if (text?.Trim().Split(';') is not [var id, var before, var after]
            || !Guid.TryParseExact(id, "N", out var operation) || operation == Guid.Empty
            || !Version.TryParse(before, out var from) || from.Build < 0
            || !Version.TryParse(after, out var to) || to.Build < 0 || to <= from)
        {
            return null;
        }
        return new UpdateReceipt(operation, from, to);
    }
}
