# Changelog

All notable changes to MandoCode will be documented in this file.

## [0.9.6] - 2026-04-06

### Added
- **Unit test project** with 65 tests covering `InputStateMachine` and `DiffService`
  - Text input, cursor movement, backspace/delete, submit
  - Command autocomplete filtering, dropdown navigation, accept/dismiss
  - Command history navigation with saved input restoration
  - Paste handling with newline-to-space conversion
  - Static helper tests (`IsCommand`, `GetCommandName`) using parameterized `[Theory]` tests
  - Diff computation for new files, modifications, deletions, identical content
  - Line ending normalization (Windows `\r\n`, old Mac `\r`)
  - Large file fallback with sampled diffs
  - Context collapse with configurable context lines
- **Empty response recovery** — when the model returns blank (context overflow), MandoCode now shows a helpful message instead of freezing with a blank screen
- **Rendering timeout guard** — markdown rendering runs with a 30-second timeout and a "Rendering..." spinner after 1 second, preventing permanent freezes on large responses. Falls back to raw text if rendering exceeds the limit.

### Fixed
- **Timeout retry deadlock** — when a request timed out (5-minute limit), `RetryPolicy` was treating the timeout as a transient error and retrying the entire request including all file reads. With 2 retries, this could silently hang for up to 15 minutes. Now the cancellation token is properly passed to `RetryPolicy` so timeouts fail fast.
- **Silent exception suppression** — replaced 6 bare `catch { }` blocks with `catch (Exception ex)` logging to `Debug.WriteLine` across `TerminalThemeService`, `App.razor`, `FileSystemPlugin`, `FunctionInvocationFilter`, and `ShellCommandHandler`
- **Shell command injection surface** — `ShellCommandHandler` and `FileSystemPlugin` now use `ProcessStartInfo.ArgumentList` for proper argument escaping instead of manual string concatenation with `cmd.Replace("\"", "\\\"")`
- **Inconsistent config validation** — `Program.cs` accepted any `maxTokens > 0` while `ValidateAndClamp()` enforced `[256, 131072]`. All validation now uses centralized constants and helpers on `MandoCodeConfig`

### Changed
- Config validation constants (`MinTemperature`, `MaxTemperature`, `MinMaxTokens`, `MaxMaxTokens`) and helpers (`IsValidTemperature`, `IsValidMaxTokens`) are now defined once on `MandoCodeConfig` and referenced by `Program.cs`, `ConfigurationWizard.cs`, and `ValidateAndClamp()`
