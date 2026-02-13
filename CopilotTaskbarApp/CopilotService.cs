using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot.SDK;
using WinForms = System.Windows.Forms;

namespace CopilotTaskbarApp;

public class CopilotService : IAsyncDisposable
{
    private const int ExternalCliTimeoutSeconds = 300;

    private readonly CopilotClient _client;
    private object? _session; 
    private bool _isStarted;
    private string _cliCommand = "copilot";

    public CopilotService()
    {
        _client = new CopilotClient();
    }

    public void SetCliCommand(string cliCommand)
    {
        var normalized = string.IsNullOrWhiteSpace(cliCommand) ? "copilot" : cliCommand.Trim().ToLowerInvariant();
        _cliCommand = normalized is "copilot" or "claude" or "opencode" ? normalized : "copilot";
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        if (_cliCommand != "copilot")
        {
            return;
        }

        if (!_isStarted)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[CopilotService] Starting Copilot CLI...");
                var startTime = DateTime.UtcNow;
                await _client.StartAsync(cancellationToken);
                var elapsed = DateTime.UtcNow - startTime;
                System.Diagnostics.Debug.WriteLine($"[CopilotService] CLI started successfully in {elapsed.TotalSeconds:F2}s");
                _isStarted = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CopilotService] CLI startup failed: {ex.Message}");
                throw new Exception($"Failed to start Copilot. Ensure you're authenticated with GitHub.\n\nDetails: {ex.Message}", ex);
            }
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("[CopilotService] CLI already started, reusing connection");
        }
    }

    public async Task<string> GetResponseAsync(string prompt, string? context = null, string? imageBase64 = null, List<ChatMessage>? recentMessages = null, CancellationToken cancellationToken = default)
    {
        if (_cliCommand != "copilot")
        {
            return await GetExternalCliResponseAsync(prompt, context, recentMessages, cancellationToken);
        }

        var methodStartTime = DateTime.UtcNow;
        System.Diagnostics.Debug.WriteLine($"[CopilotService] ===== GetResponseAsync START at {methodStartTime:HH:mm:ss.fff} =====");
        
        try
        {
            var stageStart = DateTime.UtcNow;
            await EnsureStartedAsync(cancellationToken);
            System.Diagnostics.Debug.WriteLine($"[CopilotService] Stage 1 (CLI Start): {(DateTime.UtcNow - stageStart).TotalSeconds:F2}s");

            // Create a new session for this request
            stageStart = DateTime.UtcNow;
            System.Diagnostics.Debug.WriteLine($"[CopilotService] Creating SDK session...");
            dynamic session = await _client.CreateSessionAsync(new SessionConfig
            {
                Model = "gpt-4",
                Streaming = true
            }, cancellationToken);
            System.Diagnostics.Debug.WriteLine($"[CopilotService] Stage 2 (Session Create): {(DateTime.UtcNow - stageStart).TotalSeconds:F2}s");

            try
            {
                // Build the prompt with context
                var fullPrompt = prompt;
                if (!string.IsNullOrEmpty(context))
                {
                    if (context.Contains("[Active Focus]") || context.Contains("[Open Folders]"))
                    {
                        var conversationHistory = "";
                        if (recentMessages != null && recentMessages.Count > 0)
                        {
                            // Include last 5 exchanges (10 messages max) for context continuity
                            var recentCount = Math.Min(10, recentMessages.Count);
                            var relevant = recentMessages.Skip(Math.Max(0, recentMessages.Count - recentCount)).ToList();
                            
                            conversationHistory = "RECENT CONVERSATION:\n";
                            foreach (var msg in relevant)
                            {
                                var role = msg.Role == "user" ? "User" : "Assistant";
                                conversationHistory += $"{role}: {msg.Content}\n";
                            }
                            conversationHistory += "\n";
                        }
                        
                        fullPrompt = 
                            "You are a desktop assistant integrated into Windows 11. Answer questions based on what the user is actively doing.\n\n" +
                            conversationHistory +
                            "CURRENT CONTEXT:\n" +
                            context + "\n\n" +
                            "RESPONSE GUIDELINES:\n" +
                            "- Avoid markdown formatting (no bold, bullets, or headers) - use plain conversational text\n" +
                            "- When folders/projects are open: mention the specific path and tailor suggestions to that directory\n" +
                            "- Be curious: analyze [Active Focus], [Open Folders], [Open Applications], [Background Services], and [Environment Variables] to understand what the user is working on\n" +
                            "- Use presence of specific processes, services, and environment variables (e.g., PYTHONPATH, NODE_ENV, Docker, WSL) to provide relevant code suggestions and commands\n" +
                            "- MAINTAIN CONTEXT CONTINUITY: Review the RECENT CONVERSATION to understand what environment/tools the user has been working with. If they just installed or used something (e.g., podman in Fedora WSL), assume follow-up questions refer to that same environment unless explicitly stated otherwise\n" +
                            "- When [Active Focus] shows a WSL distribution (e.g., 'FedoraLinux-43 shell'), prioritize that environment for commands and suggestions even if other tools are also available on Windows\n" +
                            "- BE ACTIONABLE: When the user gives an imperative command (install, uninstall, remove, start, stop, restart, configure, create, delete, etc.), execute it immediately and report the result. Do not just provide instructions unless the command is ambiguous or risky without confirmation.\n" +
                            "- REPORT PARTIAL PROGRESS: For multi-step operations, report what succeeded even if a later step failed. Example: 'Successfully installed podman and started MySQL container, but port verification from Windows failed: <error message>'\n" +
                            "- Consistency: If you executed a similar command earlier in the conversation (e.g., installed software), use the same action-oriented approach for related commands (e.g., uninstall that software)\n" +
                            "- Give practical, immediate answers\n" +
                            "- Keep responses concise unless depth is needed\n" +
                            "- ACCURACY IS CRITICAL: Only state facts you are certain about. Do not conflate different people, projects, or events. If unsure, acknowledge uncertainty rather than guessing.\n\n" +
                            $"Current Question: {prompt}";
                    }
                    else
                    {
                        fullPrompt = $"Working directory: {context}\n\n{prompt}";
                    }
                }

                // Append image if provided (using Markdown Data URI syntax for Vision models)
                if (!string.IsNullOrEmpty(imageBase64))
                {
                    fullPrompt += $"\n\n![User Screenshot](data:image/jpeg;base64,{imageBase64})";
                    System.Diagnostics.Debug.WriteLine($"[CopilotService] Appended screenshot ({imageBase64.Length} chars, ~{imageBase64.Length / 1024}KB)");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[CopilotService] No screenshot attached");
                }

                System.Diagnostics.Debug.WriteLine($"[CopilotService] ===== FULL PROMPT ({fullPrompt.Length} chars) =====");
                foreach (var line in fullPrompt.Split(new[] { '\r', '\n' }, StringSplitOptions.None))
                {
                    System.Diagnostics.Debug.WriteLine($"[CopilotService] {line}");
                }
                System.Diagnostics.Debug.WriteLine($"[CopilotService] ===== END PROMPT =====");
                
                var sendStart = DateTime.UtcNow;
                System.Diagnostics.Debug.WriteLine($"[CopilotService] Stage 3 (Sending to model): Starting at {sendStart:HH:mm:ss.fff}...");
                
                // Increase timeout to 5 minutes for complex multi-step operations
                dynamic responseEvent = await session.SendAndWaitAsync(
                    new MessageOptions { Prompt = fullPrompt }, 
                    TimeSpan.FromSeconds(300), 
                    cancellationToken
                );

                var sendElapsed = DateTime.UtcNow - sendStart;
                var totalElapsed = DateTime.UtcNow - methodStartTime;
                System.Diagnostics.Debug.WriteLine($"[CopilotService] Stage 3 (Model Response): {sendElapsed.TotalSeconds:F2}s");
                System.Diagnostics.Debug.WriteLine($"[CopilotService] Total request time: {totalElapsed.TotalSeconds:F2}s");
                
                // Pattern matching for type-safe null checking with dynamic types
                if (responseEvent?.Data?.Content is string content)
                {
                    System.Diagnostics.Debug.WriteLine($"[CopilotService] ===== RESPONSE ({content.Length} chars) =====");
                    foreach (var line in content.Split(new[] { '\r', '\n' }, StringSplitOptions.None))
                    {
                        System.Diagnostics.Debug.WriteLine($"[CopilotService] {line}");
                    }
                    System.Diagnostics.Debug.WriteLine($"[CopilotService] ===== END RESPONSE =====");
                    return content;
                }
                
                System.Diagnostics.Debug.WriteLine($"[CopilotService] WARNING: No content in response event");
                
                return "No response received from GitHub Copilot.";
            }
            finally
            {
                if (session is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync();
                }
            }
        }
        catch (TimeoutException tex)
        {
            var totalElapsed = DateTime.UtcNow - methodStartTime;
            System.Diagnostics.Debug.WriteLine($"[CopilotService] TIMEOUT after {totalElapsed.TotalSeconds:F2}s!");
            System.Diagnostics.Debug.WriteLine($"[CopilotService] Timeout details: {tex.Message}");
            System.Diagnostics.Debug.WriteLine($"[CopilotService] Stack trace: {tex.StackTrace}");
            return $"Request timed out after {totalElapsed.TotalSeconds:F0} seconds. This usually means the Copilot CLI is not responding. Check if 'copilot' command works in your terminal.";
        }
        catch (Exception ex)
        {
            var totalElapsed = DateTime.UtcNow - methodStartTime;
            System.Diagnostics.Debug.WriteLine($"[CopilotService] ERROR after {totalElapsed.TotalSeconds:F2}s: {ex.GetType().Name}");
            System.Diagnostics.Debug.WriteLine($"[CopilotService] Error message: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[CopilotService] Stack trace: {ex.StackTrace}");
            
            // Handle specific errors as before
            var message = ex.Message.ToLower();
            if (message.Contains("auth") || message.Contains("login") || message.Contains("unauthorized"))
            {
                return "Authentication required.\n\n" +
                       "Please authenticate with GitHub:\n" +
                       "Run: gh auth login\n" +
                       "Or visit: https://docs.github.com/en/copilot/cli\n\n" +
                       "Then restart this application.";
            }
            
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> GetExternalCliResponseAsync(string prompt, string? context, List<ChatMessage>? recentMessages, CancellationToken cancellationToken)
    {
        var fullPrompt = prompt;
        if (!string.IsNullOrWhiteSpace(context))
        {
            fullPrompt = $"Context:\n{context}\n\nQuestion:\n{prompt}";
        }

        if (recentMessages != null && recentMessages.Count > 0)
        {
            var relevant = recentMessages.Skip(Math.Max(0, recentMessages.Count - 6));
            var history = string.Join("\n", relevant.Select(m => $"{(m.Role == "user" ? "User" : "Assistant")}: {m.Content}"));
            fullPrompt = $"Recent conversation:\n{history}\n\n{fullPrompt}";
        }

        var psi = new ProcessStartInfo
        {
            FileName = _cliCommand,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (_cliCommand == "claude")
        {
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(fullPrompt);
        }
        else if (_cliCommand == "opencode")
        {
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add(fullPrompt);
        }
        else
        {
            return $"Unsupported CLI provider: {_cliCommand}";
        }

        using var process = Process.Start(psi);
        if (process == null)
        {
            return $"Failed to start {_cliCommand}.";
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(ExternalCliTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
            return $"Request timed out after {ExternalCliTimeoutSeconds} seconds while waiting for {_cliCommand}.";
        }

        var output = (await outputTask).Trim();
        var error = (await errorTask).Trim();

        if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
        {
            return output;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            return $"Error from {_cliCommand}: {error}";
        }

        return $"No response received from {_cliCommand}.";
    }

    // Checks if the Copilot CLI is ready and authenticated
    public async Task<bool> CheckAuthenticationAsync()
    {
        if (_cliCommand != "copilot")
        {
            return true;
        }

        try
        {
            await EnsureStartedAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
            _session = null;
        }
        
        if (_isStarted)
        {
            await _client.StopAsync();
            _isStarted = false;
        }
    }
}
