using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components.Web;
using MandoCode.Models;
using MandoCode.Services;
using Spectre.Console;
using System.Text.RegularExpressions;

namespace MandoCode.Components;

public partial class App
{
    private async Task ProcessDirectRequestAsync(string input, string? hostInstruction = null, CancellationToken cancellationToken = default)
    {
        _lastRequestError = null;
        _lastCompletedReply = null;
        (Pane?.Session ?? TuiConsole.Current)?.BeginToolTurn();
        // A proposal belongs to the turn that produced it. Without this, a plan proposed during a
        // turn the user then cancelled would sit in the single slot and execute at the end of some
        // later, unrelated turn.
        PlanHandoff.ClearPendingProposal();

        // Reset operation tracking for the new request
        _recentReadCount = 0;
        _recentReadFiles.Clear();
        _lastOperationType = null;

        TuiConsole.WriteLine();

        _requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _requestCts.Token;
        StartCancelKeyListener();

        OpenReply();
        try
        {
            var receivedFirstChunk = false;
            var stream = string.IsNullOrWhiteSpace(hostInstruction)
                ? AI.ChatStreamAsync(input, token)
                : AI.ChatStreamWithHostInstructionAsync(input, hostInstruction, token);
            var enumerator = stream.GetAsyncEnumerator(token);

            try
            {
                // Show our controllable spinner while waiting for first chunk
                // This spinner can be stopped by HandleDiffApproval when it needs user input
                Spinner.Start("Thinking...");

                if (await enumerator.MoveNextAsync())
                {
                    receivedFirstChunk = true;
                    Snapshots.ClearImports();
                }

                Spinner.Stop();

                if (!receivedFirstChunk)
                {
                    TuiConsole.MarkupLine("[yellow]No response from model. The request may have exceeded the model's context window.[/]");
                    TuiConsole.MarkupLine("[dim]Try a smaller request, or switch to a model with a larger context window via /config.[/]");
                }

                // Buffer all chunks, then render as rich markdown
                if (receivedFirstChunk)
                {
                    var responseBuffer = new System.Text.StringBuilder();
                    var lineBuffer = new System.Text.StringBuilder();
                    var showSpinnerBeforeNextChunk = false;

                    // Each chunk is one finished turn. Print what of it wasn't already printed at a
                    // tool call, so each turn reads in the order it happened.
                    void AccumulateChunk(string chunk)
                    {
                        // Peer reports carry the final completed assistant turn, rather than all
                        // of the narration preceding tool calls. The receiver keeps the full transcript.
                        if (!string.IsNullOrWhiteSpace(chunk)) _lastCompletedReply = chunk.Trim();
                        var unprinted = SettleReply(chunk);
                        if (unprinted.Length > 0)
                        {
                            PrintReplyHeading();
                            RenderMarkdownGuarded(unprinted);
                        }

                        responseBuffer.Append(chunk);
                        foreach (char c in chunk)
                        {
                            if (c == '\n')
                            {
                                if (lineBuffer.ToString().Contains('\u2699'))
                                    showSpinnerBeforeNextChunk = true;
                                lineBuffer.Clear();
                            }
                            else if (c != '\r')
                            {
                                lineBuffer.Append(c);
                            }
                        }
                    }

                    AccumulateChunk(enumerator.Current);

                    var hasMore = true;
                    while (hasMore)
                    {
                        if (showSpinnerBeforeNextChunk)
                        {
                            showSpinnerBeforeNextChunk = false;
                            Spinner.Start();
                            hasMore = await enumerator.MoveNextAsync();
                            Spinner.Stop();
                        }
                        else
                        {
                            hasMore = await enumerator.MoveNextAsync();
                        }

                        if (hasMore)
                            AccumulateChunk(enumerator.Current);
                    }

                    // Every turn is on screen by now; the joined text is for history and checks.
                    var responseText = responseBuffer.ToString().Trim();
                    if (string.IsNullOrEmpty(responseText))
                    {
                        _lastRequestError = "The model returned no answer.";
                        TuiConsole.MarkupLine("[yellow]Model returned an empty response. The context may be too large for this model.[/]");
                        TuiConsole.MarkupLine("[dim]Try a smaller request, or switch to a model with a larger context window via /config.[/]");
                    }
                    else
                    {
                        _lastAiResponse = responseText;

                        // Add to chat history (ANSI capture deferred to Phase 2)
                        _messages.Add(new ChatMsg
                        {
                            Role = "assistant",
                            Text = responseText
                        });
                        if (responseText.Contains("Error: Connection to Ollama failed.", StringComparison.Ordinal)
                            && !await CheckOllamaConnectionAsync())
                        {
                            _lastRequestError = "Connection to Ollama failed.";
                            CloseReply();
                            await HandleRetryCommandAsync();
                            if (_isConnected) TuiConsole.MarkupLine("[dim]Connection restored. Send your request again to continue.[/]");
                        }

                        // 401 auto-recovery: if the response is a 401 sign-in error,
                        // immediately launch the cloud sign-in walkthrough so the user
                        // can hit "Sign me in now" without typing /setup. Saves a step
                        // every single time this hits.
                        if (Looks401(responseText))
                        {
                            _lastRequestError = "The model requires sign-in.";
                            await TryAutoSigninAfter401Async();
                        }

                        // Show per-response token summary on the left, matching the footer text style.
                        if (Config.EnableTokenTracking)
                        {
                            var lastOp = TokenTracker.LastOperation;
                            if (lastOp != null)
                            {
                                var inLabel = $"In {TokenTrackingService.FormatTokenCount(lastOp.PromptTokens)}";
                                var outLabel = $"Out {TokenTrackingService.FormatTokenCount(lastOp.CompletionTokens)}";
                                var tpsLabel = lastOp.TokensPerSecond.HasValue
                                    ? $" · {lastOp.TokensPerSecond.Value:0.#} tok/s"
                                    : "";
                                var tokenSummary = $"{inLabel} · {outLabel}{tpsLabel}";
                                TuiConsole.WriteSpaced(new Text(tokenSummary, new Style(Color.Grey62)));
                            }
                        }

                    }

                    // Update title bar with latest model/project/token info
                    ThemeService.UpdateStatusTitle();
                }
            }
            finally
            {
                Spinner.Stop();
                await enumerator.DisposeAsync();
            }

            TuiConsole.WriteLine();

            // Spinner ownership note: ExecuteAgentModelCallAsync starts a spinner per model call
            // and never stops it — it has always relied on the caller. Before plans were deferred
            // they ran inside the enumerator drain above, so that finally cleaned up. Now they run
            // out here, so the plan run needs its own guarantee or the last step's spinner keeps
            // animating after everything has finished.
            //
            // The model's turn is fully drained. Only now do we run any plan it proposed — the
            // plan is a peer of the chat turn, not a child of the propose_plan tool call. Running
            // it here is what lets the outer stall watchdog and request ceiling stay untouched
            // (they've already completed), lets the prompt gate be held normally, and leaves the
            // model with no open turn afterwards to redo the work in.
            try
            {
                await RunPendingPlanAsync();
            }
            finally
            {
                Spinner.Stop();
            }
        }
        catch (OperationCanceledException)
        {
            _lastRequestError = "Request cancelled.";
            Spinner.Stop();
            TuiConsole.WriteLine();
            TuiConsole.MarkupLine("[yellow]Request cancelled.[/]");
            TuiConsole.WriteLine();
        }
        catch (Exception ex)
        {
            _lastRequestError = ex.Message;
            Spinner.Stop();
            TuiConsole.WriteLine($"Error: {ex.Message}");
            TuiConsole.WriteLine();
            if (IsOllamaTransportFailure(ex))
            {
                CloseReply();
                await HandleRetryCommandAsync();
                if (_isConnected) TuiConsole.MarkupLine("[dim]Connection restored. Send your request again to continue.[/]");
            }
        }
        finally
        {
            CloseReply();
            (Pane?.Session ?? TuiConsole.Current)?.CompleteToolTurn();
            StopCancelKeyListener();
            var oldCts = Interlocked.Exchange(ref _requestCts, null);
            oldCts?.Dispose();

            // Persist the conversation as of this turn for `mandocode --continue`.
            // Runs on cancel too — whatever the history holds now is what "continue" means.
            try { if (Pane is null || Pane.Id == 1) SessionResumeStore.Save(ProjectRoot.ProjectRoot, AI.ExportHistoryJson()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.TraceWarning("Could not save compacted context: {0}", ex.Message); }
            SaveAgentHistory(false);
        }
    }

    /// <summary>
    /// Renders <paramref name="text"/> as markdown, guarded against rendering hangs on
    /// malformed/huge content. The expensive part — building the renderable (Markdig parse,
    /// HTML walk, regex linkify) — runs on a background task; a 1s grace passes before a
    /// "Rendering..." spinner shows, with a hard cap of
    /// <see cref="MandoCodeConfig.MarkdownRenderTimeoutSeconds"/>. On timeout or failure it
    /// falls back to raw text. The console write happens ONLY on the calling thread (never
    /// on the background task), so a build that overruns the timeout can't later dump a
    /// second, garbled copy on top of the raw-text fallback. Shared by the direct-chat
    /// response path and the per-step plan path so neither can freeze the interactive loop.
    /// </summary>
    private void RenderMarkdownGuarded(string text)
    {
        try
        {
            // Build off-thread but DON'T write from there. Building is the expensive,
            // occasionally-pathological work; the write is fast. Keeping the write on the
            // calling thread means that if the build overruns the timeout and we fall back
            // to raw text, the orphaned task just finishes computing a renderable nobody
            // reads and exits — it can't write to the console out of band and corrupt the
            // view (or interleave with the next step's output). It also serializes the write
            // with the surrounding imperative AnsiConsole output on this same thread.
            // Large responses with many tables/code blocks can take 10+ seconds to build.
            Spectre.Console.Rendering.IRenderable? renderable = null;
            var buildTask = Task.Run(() =>
                renderable = MarkdownHtmlRenderer.BuildRenderable(text, ProjectRoot.ProjectRoot, Pane is null ? null : new CliAgentDirectory(Pane).All.Select(p => p.Name).ToArray()));

            // Show a spinner if the build takes more than 1 second. The remaining
            // budget comes from config — big tool-grounded responses (MCP, many
            // tables) can legitimately take 30+ seconds to build.
            if (!buildTask.Wait(TimeSpan.FromSeconds(1)))
            {
                Spinner.Start("Rendering...");
                var remainingSeconds = Math.Max(1, Config.MarkdownRenderTimeoutSeconds - 1);
                if (!buildTask.Wait(TimeSpan.FromSeconds(remainingSeconds)))
                {
                    Spinner.Stop();
                    // Build exceeded the configured budget — fall back to raw text. The
                    // orphaned task finishes computing into a local nobody reads, then exits
                    // without ever touching the console.
                    Console.WriteLine();
                    Console.Write(text);
                    Console.WriteLine();
                    TuiConsole.MarkupLine($"[dim](markdown rendering timed out after {Config.MarkdownRenderTimeoutSeconds}s — showing raw text. Raise with /config or mandocode --config set renderTimeout 120)[/]");
                    return;
                }
                Spinner.Stop();
            }

            // Build finished within budget (a faulted build rethrows out of Wait above and
            // lands in the catch below). Write the rendered markdown on this thread.
            if (renderable != null)
                TuiConsole.Write(renderable);
        }
        catch (Exception renderEx)
        {
            // A faulted Wait throws straight to here, skipping the Spinner.Stop() calls
            // above — stop it so the "Rendering..." spinner doesn't leak. Stop() is idempotent.
            Spinner.Stop();
            System.Diagnostics.Debug.WriteLine($"[MandoCode] Render failed: {renderEx.Message}");
            // Fallback to raw text if markdown rendering fails
            Console.Write(text);
        }
    }

    /// <summary>
    /// Called by <see cref="PlanHandoff"/> when the model invokes propose_plan.
    /// Shows the approval UI, runs execution if approved, and returns a summary
    /// string that the model will see as the tool result.
    /// </summary>
    // Plain labels — ApprovalSelect carries the palette (green = proceed, warm gold =
    // redirect/stop), so these stay markup-free for clean comparisons and scrollback echo.
}
