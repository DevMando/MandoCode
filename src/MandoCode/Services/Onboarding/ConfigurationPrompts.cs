using System.Globalization;
using MandoCode.Models;
using Spectre.Console;

namespace MandoCode.Services;

/// <summary>Host-owned wizard input, with Spectre fallback for standalone callers.</summary>
public sealed class ConfigurationPrompts
{
    public Func<string, string[], Task<string>>? Select { get; init; }
    public Func<string[], Task<string?>>? PickModel { get; init; }
    public Func<string, string?, Func<string, string?>?, bool, Task<string>>? Text { get; init; }

    public async Task<T> SelectAsync<T>(string title, IEnumerable<T> choices, Func<T, string>? label = null) where T : notnull
    {
        var values = choices.ToArray();
        label ??= value => value?.ToString() ?? "";
        if (Select == null)
            return AnsiConsole.Prompt(new SelectionPrompt<T>().Title($"[deepskyblue1]{Markup.Escape(title)}[/]")
                .HighlightStyle(new Style(Color.Black, Color.DeepSkyBlue1)).PageSize(10).AddChoices(values).UseConverter(label));
        var labels = values.Select(label).ToArray();
        var chosen = await Select(title, labels);
        var index = Array.IndexOf(labels, chosen);
        if (index < 0) throw new InvalidOperationException("The selected wizard option is no longer available.");
        return values[index];
    }

    public async Task<string> TextAsync(string title, string? initial = null, Func<string, string?>? validate = null, bool secret = false, bool allowEmpty = false)
    {
        string? Validate(string value) => !allowEmpty && string.IsNullOrWhiteSpace(value)
            ? "Please enter a value." : validate?.Invoke(value);
        if (Text != null) return await Text(title, initial, Validate, secret);
        var prompt = new TextPrompt<string>($"[deepskyblue1]{Markup.Escape(title)}[/]")
            .Validate(value => Validate(value) is { } error ? ValidationResult.Error(Markup.Escape(error)) : ValidationResult.Success());
        if (initial != null) prompt.DefaultValue(initial);
        if (allowEmpty) prompt.AllowEmpty();
        if (secret) prompt.Secret('*');
        return AnsiConsole.Prompt(prompt);
    }

    public async Task<T> NumberAsync<T>(string title, T current, Func<T, bool> valid, string error) where T : struct, IParsable<T>
    {
        var text = await TextAsync(title, Convert.ToString(current, CultureInfo.CurrentCulture),
            value => T.TryParse(value, CultureInfo.CurrentCulture, out var number) && valid(number) ? null : error);
        return T.Parse(text, CultureInfo.CurrentCulture);
    }

    public async Task<bool> ConfirmAsync(string title, bool defaultYes) =>
        await SelectAsync(title, defaultYes ? new[] { "Yes", "No" } : new[] { "No", "Yes" }) == "Yes";

    public async Task WithStatusAsync(string title, Func<Task> action)
    {
        if (Select != null || Text != null)
        {
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(title)}[/]");
            await action();
        }
        else
            await AnsiConsole.Status().Spinner(LoadingMessages.GetRandomSpinner())
                .StartAsync(title, async _ => await action());
    }
}
