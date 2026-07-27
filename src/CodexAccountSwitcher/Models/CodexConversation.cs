namespace CodexAccountSwitcher.Models;

public sealed record CodexConversation(
    string Id,
    string Title,
    string Preview,
    string WorkingDirectory,
    DateTimeOffset UpdatedAt,
    bool IsArchived)
{
    public string UpdatedText => UpdatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    public string ArchiveText => IsArchived ? "已归档" : string.Empty;
}
