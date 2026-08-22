using System.ComponentModel;
using Microsoft.Extensions.AI;
using Xunit;

namespace MandoCode.Tests;

/// <summary>
/// Covers the one behavior AIService.BuildAgent's NamedTool helper relies on that the compiler
/// can't check: AIFunctionFactoryOptions.Name actually overriding the default method-name-derived
/// tool name. This mapping (C# method name -> snake_case [KernelFunction] name) now exists in two
/// places — the plugin's attribute and BuildAgent's NamedTool call — so a silent Name override
/// failure would desync the MAF-side tool name from the system prompt/skills that reference the
/// snake_case name, without any compile error to catch it. See feat/agent-framework-migration,
/// Phase 2.
/// </summary>
public class AgentToolNamingTests
{
    [Description("A method whose real name should never reach the model.")]
    private static string DoNotUseThisName() => "ok";

    [Fact]
    public void NamedTool_style_creation_overrides_the_default_method_derived_name()
    {
        AIFunction function = AIFunctionFactory.Create(
            DoNotUseThisName,
            new AIFunctionFactoryOptions { Name = "list_all_project_files" });

        Assert.Equal("list_all_project_files", function.Name);
        Assert.NotEqual(nameof(DoNotUseThisName), function.Name);
    }

    [Fact]
    public void Without_an_explicit_name_AIFunctionFactory_falls_back_to_the_method_name()
    {
        // Documents the failure mode NamedTool exists to prevent: omit the override and the
        // exposed tool name silently becomes the C# method name instead of the snake_case name
        // every prompt/skill actually references.
        AIFunction function = AIFunctionFactory.Create(DoNotUseThisName);

        Assert.Equal(nameof(DoNotUseThisName), function.Name);
    }
}
