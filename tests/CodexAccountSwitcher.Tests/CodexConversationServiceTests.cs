using System.Text.Json;
using CodexAccountSwitcher.Services;

namespace CodexAccountSwitcher.Tests;

public sealed class CodexConversationServiceTests
{
    [Fact]
    public void ParsePage_reads_titles_fallbacks_archive_state_and_cursor()
    {
        using var response = JsonDocument.Parse("""
            {
              "result": {
                "data": [
                  {
                    "id": "thread-1",
                    "name": "已命名对话",
                    "preview": "第一条消息",
                    "cwd": "C:\\work",
                    "updatedAt": 1785000000
                  },
                  {
                    "id": "thread-2",
                    "name": null,
                    "preview": "首行标题\n后续内容",
                    "cwd": "D:\\work",
                    "updatedAt": 1785000100
                  }
                ],
                "nextCursor": "next-page"
              }
            }
            """);

        var page = CodexConversationService.ParsePage(response.RootElement, archived: true);

        Assert.Equal("next-page", page.NextCursor);
        Assert.Equal(2, page.Conversations.Count);
        Assert.Equal("已命名对话", page.Conversations[0].Title);
        Assert.Equal("首行标题", page.Conversations[1].Title);
        Assert.All(page.Conversations, conversation => Assert.True(conversation.IsArchived));
    }

    [Fact]
    public void BuildThreadUri_uses_official_codex_deep_link()
    {
        Assert.Equal(
            "codex://threads/019f7f35-2bf3-7f40-ba1e-61edd61da5da",
            CodexConversationService.BuildThreadUri(
                "019f7f35-2bf3-7f40-ba1e-61edd61da5da"));
    }
}
