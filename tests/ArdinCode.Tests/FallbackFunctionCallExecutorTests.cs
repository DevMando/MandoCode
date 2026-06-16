using Xunit;
using ArdinCode.Services;
using Microsoft.SemanticKernel;

namespace ArdinCode.Tests;

/// <summary>
/// Covers the text-based function-call fallback parser extracted from AIService:
/// JSON extraction across the three formats local models emit, function/parameter
/// name normalization, and call-JSON removal from the surrounding prose. These are
/// pure functions — no kernel or network needed.
/// </summary>
public class FallbackFunctionCallExecutorTests
{
    // ---- ExtractFunctionCallsFromText ----

    [Fact]
    public void Extract_StandardNameParametersFormat_FindsCall()
    {
        var text = @"I'll read it now. {""name"": ""read_file_contents"", ""parameters"": {""relativePath"": ""src/Program.cs""}}";

        var calls = FallbackFunctionCallExecutor.ExtractFunctionCallsFromText(text);

        var call = Assert.Single(calls);
        Assert.Equal("read_file_contents", call.FunctionName);
        Assert.Contains("src/Program.cs", call.ParametersJson);
    }

    [Fact]
    public void Extract_ArgumentsKeyInsteadOfParameters_FindsCall()
    {
        var text = @"{""name"": ""create_folder"", ""arguments"": {""relativePath"": ""docs""}}";

        var calls = FallbackFunctionCallExecutor.ExtractFunctionCallsFromText(text);

        var call = Assert.Single(calls);
        Assert.Equal("create_folder", call.FunctionName);
    }

    [Fact]
    public void Extract_FunctionCallWrapperFormat_FindsCall()
    {
        var text = @"{""function_call"": {""name"": ""write_file"", ""arguments"": {""relativePath"": ""a.txt"", ""content"": ""hi""}}}";

        var calls = FallbackFunctionCallExecutor.ExtractFunctionCallsFromText(text);

        var call = Assert.Single(calls);
        Assert.Equal("write_file", call.FunctionName);
        Assert.Contains("a.txt", call.ParametersJson);
    }

    [Fact]
    public void Extract_ToolCallsArrayFormat_FindsCall()
    {
        var text = @"{""tool_calls"": [{""function"": {""name"": ""list_files"", ""arguments"": {""pattern"": ""*.cs""}}}]}";

        var calls = FallbackFunctionCallExecutor.ExtractFunctionCallsFromText(text);

        var call = Assert.Single(calls);
        Assert.Equal("list_files", call.FunctionName);
        Assert.Contains("*.cs", call.ParametersJson);
    }

    [Fact]
    public void Extract_ToolCallsWithEscapedStringArguments_UnescapesJson()
    {
        // Some models emit arguments as an escaped JSON string instead of an object.
        var text = @"{""tool_calls"": [{""function"": {""name"": ""read_file"", ""arguments"": ""{\""relativePath\"": \""b.txt\""}""}}]}";

        var calls = FallbackFunctionCallExecutor.ExtractFunctionCallsFromText(text);

        var call = Assert.Single(calls);
        Assert.Equal("read_file", call.FunctionName);
        Assert.Contains(@"""relativePath"": ""b.txt""", call.ParametersJson);
    }

    [Fact]
    public void Extract_NestedBracesInParameters_CapturesWholeObject()
    {
        var text = @"{""name"": ""write_file"", ""parameters"": {""relativePath"": ""x.json"", ""content"": ""{ \""nested\"": { \""a\"": 1 } }""}}";

        var calls = FallbackFunctionCallExecutor.ExtractFunctionCallsFromText(text);

        var call = Assert.Single(calls);
        // Brace-counting must not stop at braces inside the string value.
        Assert.EndsWith("}", call.ParametersJson.TrimEnd());
        Assert.Contains("nested", call.ParametersJson);
    }

    [Fact]
    public void Extract_MultipleCalls_FindsAll()
    {
        var text = @"{""name"": ""create_folder"", ""parameters"": {""relativePath"": ""a""}}
                     {""name"": ""create_folder"", ""parameters"": {""relativePath"": ""b""}}";

        var calls = FallbackFunctionCallExecutor.ExtractFunctionCallsFromText(text);

        Assert.Equal(2, calls.Count);
    }

    [Theory]
    [InlineData("Just a normal explanation of the code.")]
    [InlineData("Here is some JSON: {\"key\": \"value\"}")]
    [InlineData("")]
    public void Extract_PlainTextOrUnrelatedJson_FindsNothing(string text)
    {
        Assert.Empty(FallbackFunctionCallExecutor.ExtractFunctionCallsFromText(text));
    }

    [Fact]
    public void Extract_UnterminatedJson_FindsNothing()
    {
        var text = @"{""name"": ""read_file"", ""parameters"": {""relativePath"": ""a.txt""";

        Assert.Empty(FallbackFunctionCallExecutor.ExtractFunctionCallsFromText(text));
    }

    // ---- NormalizeFunctionName ----

    [Theory]
    [InlineData("mkdir", "create_folder")]
    [InlineData("make_directory", "create_folder")]
    [InlineData("read_file", "read_file_contents")]
    [InlineData("rm", "delete_file")]
    [InlineData("list_files", "list_files_match_glob_pattern")]
    [InlineData("find_in_files", "search_text_in_files")]
    public void NormalizeFunctionName_KnownAliases_MapToPluginNames(string alias, string expected)
    {
        Assert.Equal(expected, FallbackFunctionCallExecutor.NormalizeFunctionName(alias));
    }

    [Theory]
    [InlineData("ReadFile", "read_file_contents")] // PascalCase → snake_case → alias
    [InlineData("CreateFolder", "create_folder")]
    [InlineData("writeFile", "write_file")] // camelCase → snake_case
    [InlineData("write_file", "write_file")] // already correct passes through
    public void NormalizeFunctionName_CaseConventions_ConvertToSnakeCase(string name, string expected)
    {
        Assert.Equal(expected, FallbackFunctionCallExecutor.NormalizeFunctionName(name));
    }

    [Theory]
    [InlineData("PascalCase", "pascal_case")]
    [InlineData("camelCase", "camel_case")]
    [InlineData("ALREADY_SNAKE", "already_snake")]
    [InlineData("lower", "lower")]
    [InlineData("", "")]
    public void ToSnakeCase_ConvertsConventions(string input, string expected)
    {
        Assert.Equal(expected, FallbackFunctionCallExecutor.ToSnakeCase(input));
    }

    // ---- NormalizeParameterName ----

    [Theory]
    [InlineData("path", "relativePath")]
    [InlineData("file_path", "relativePath")]
    [InlineData("filePath", "relativePath")]
    [InlineData("glob_pattern", "pattern")]
    [InlineData("query", "searchPattern")]
    [InlineData("file_content", "content")]
    [InlineData("unknown_param", "unknown_param")] // unmapped names pass through
    public void NormalizeParameterName_MapsCommonVariants(string input, string expected)
    {
        Assert.Equal(expected, FallbackFunctionCallExecutor.NormalizeParameterName(input));
    }

    // ---- RemoveFunctionCallJson ----

    [Fact]
    public void RemoveFunctionCallJson_StripsCallJson_KeepsProse()
    {
        var text = @"I'll create the folder. {""name"": ""create_folder"", ""parameters"": {""relativePath"": ""src""}} Done.";

        var cleaned = FallbackFunctionCallExecutor.RemoveFunctionCallJson(text);

        Assert.DoesNotContain("create_folder", cleaned);
        Assert.Contains("I'll create the folder.", cleaned);
        Assert.Contains("Done.", cleaned);
    }

    [Fact]
    public void RemoveFunctionCallJson_NoCallJson_ReturnsTextUnchanged()
    {
        var text = "Nothing to remove here.";

        Assert.Equal(text, FallbackFunctionCallExecutor.RemoveFunctionCallJson(text));
    }

    // ---- ProcessAsync (no-match fast path; needs only an empty kernel) ----

    [Fact]
    public async Task ProcessAsync_NoFunctionCalls_ReturnsResponseUnchanged()
    {
        var executor = new FallbackFunctionCallExecutor();
        var kernel = Kernel.CreateBuilder().Build();
        var response = "A plain answer with no tool calls.";

        Assert.Equal(response, await executor.ProcessAsync(response, kernel, "test-model"));
    }
}
