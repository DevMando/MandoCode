using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

[Trait("Category", "Unit")]
public sealed class SkillAuthorTests
{
    [Fact]
    public void GeneratedFrontmatter_HandlesFencesQuotesAndMultilineDescriptions()
    {
        var draft = SkillAuthor.ParseDraft("```markdown\n---\nname: review-changes\ndescription: >-\n  Review code: check errors\n  and preserve intent.\n---\n\n# Review\nRead the diff.\n```");
        Assert.Equal("review-changes", draft.Name);
        Assert.Equal("Review code: check errors and preserve intent.", draft.Description);
        Assert.Equal("# Review\nRead the diff.", draft.Body);
    }

    [Theory]
    [InlineData("Review the code.")]
    [InlineData("---\nname: [broken\n---\nInstructions")]
    public void UnexpectedModelOutput_RemainsEditable(string raw)
    {
        var draft = SkillAuthor.ParseDraft(raw);
        Assert.Empty(draft.Name);
        Assert.Equal(raw, draft.Body);
    }
}
