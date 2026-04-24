using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Terminal.Gui;

namespace Spotify2BS.Gui;

// ───────────────────────────────────────────────
// ENTRY POINT
// ───────────────────────────────────────────────
public static class Program {
    public static void Main() {
        Application.Init();
        var win = new MainWindow();
        Application.Run(win);
        Application.Shutdown();
    }
}

// ───────────────────────────────────────────────
// MODELS
// ───────────────────────────────────────────────
public class AppConfig {
    public string? Playlist { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    // Run settings
    public string Destination { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public int Sensitivity { get; set; } = 2;
    public int Depth { get; set; } = 1;
    public bool Manual { get; set; } = false;
    public bool Literality { get; set; } = false;
    public string ExtraParams { get; set; } = "";
    public bool DryRun { get; set; } = false;
    public bool Debug { get; set; } = false;
}

public record TokenResponse(
    string AccessToken,
    string TokenType,
    int ExpiresIn,
    string? RefreshToken,
    string? Scope,
    DateTime CreatedAt
);

// ───────────────────────────────────────────────
// CONFIG SERVICE
// ───────────────────────────────────────────────
public class ConfigService {
    private readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify2BS");
    private string ConfigFile => Path.Combine(_dir, "config.json");
    private string TokenFile => Path.Combine(_dir, "token.json");

    public AppConfig Load() {
        if (!File.Exists(ConfigFile)) return new AppConfig();
        return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigFile)) ?? new AppConfig();
    }

    public void Save(AppConfig cfg) {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigFile, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void SaveToken(TokenResponse t) {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(TokenFile, JsonSerializer.Serialize(t));
    }

    public TokenResponse? GetValidToken() {
        if (!File.Exists(TokenFile)) return null;
        var t = JsonSerializer.Deserialize<TokenResponse>(File.ReadAllText(TokenFile));
        if (t == null) return null;
        if (DateTime.UtcNow > t.CreatedAt.AddSeconds(t.ExpiresIn - 60)) return null;
        return t;
    }

    public void ClearToken() {
        if (File.Exists(TokenFile)) File.Delete(TokenFile);
    }
}

// ───────────────────────────────────────────────
// OAUTH SERVICE
// ───────────────────────────────────────────────
public class OAuthService {
    public async Task<TokenResponse> Authenticate(AppConfig cfg, Action<string> log) {
        var state = RandomState();
        var redirect = "http://127.0.0.1:5000/callback/";

        var url = "https://accounts.spotify.com/authorize" +
                  $"?client_id={cfg.ClientId}" +
                  $"&response_type=code" +
                  $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
                  $"&scope=playlist-read-private" +
                  $"&state={state}";

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        log("Browser opened — waiting for callback...");

        var (code, returnedState) = await Task.Run(() => Listen(redirect, log));

        if (returnedState != state)
            throw new Exception("OAuth state mismatch — possible security issue");

        using var client = new HttpClient();
        var form = new FormUrlEncodedContent(new Dictionary<string, string> {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["redirect_uri"] = redirect,
            ["client_id"] = cfg.ClientId!,
            ["client_secret"] = cfg.ClientSecret!
        });

        var res = await client.PostAsync("https://accounts.spotify.com/api/token", form);
        var json = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
            throw new Exception($"Token exchange failed: {json}");

        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var token = JsonSerializer.Deserialize<TokenResponse>(json, opts)!;
        return token with { CreatedAt = DateTime.UtcNow };
    }

    private (string?, string?) Listen(string redirect, Action<string> log) {
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        listener.Start();

        // 30 second timeout
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var contextTask = listener.GetContextAsync();

        try {
            contextTask.Wait(cts.Token);
        } catch (OperationCanceledException) {
            listener.Stop();
            return (string.Empty, string.Empty);
            throw new TimeoutException("OAuth timed out after 30 seconds — no callback received.");
        }

        var ctx = contextTask.Result;
        var code = ctx.Request.QueryString["code"];
        var state = ctx.Request.QueryString["state"];

        var html = "<html><body><h2>Done! You can close this tab.</h2></body></html>";
        var buf = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentLength64 = buf.Length;
        ctx.Response.OutputStream.Write(buf);
        ctx.Response.Close();

        return (code, state);
    }

    private static string RandomState() {
        var b = new byte[32];
        RandomNumberGenerator.Fill(b);
        return Convert.ToBase64String(b).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }
}

// ───────────────────────────────────────────────
// CLI SERVICE
// ───────────────────────────────────────────────
public class CliService {
    public Task Run(string token, string playlist, string destination,
        int sensitivity, int depth, bool manual, bool literality,
        string extraParams, bool dryRun, bool debug,
        Action<string> log, Action onFinish) {
        var exePath = Path.Combine(AppContext.BaseDirectory, "spotify2bs.exe");
        if (!File.Exists(exePath)) {
            // Try without .exe for Linux/macOS
            exePath = Path.Combine(AppContext.BaseDirectory, "spotify2bs");
        }
        if (!File.Exists(exePath)) {
            log("ERROR: spotify2bs CLI binary not found next to GUI exe");
            onFinish();
            return Task.CompletedTask;
        }

        var args = new StringBuilder();
        args.Append($"\"{token}\" \"{playlist}\" \"{destination}\"");
        args.Append($" -s {sensitivity}");
        args.Append($" -d {depth}");
        if (manual) args.Append(" -m");
        if (literality) args.Append(" -l");
        if (dryRun) args.Append(" -D");
        if (debug) args.Append(" --debug");
        if (!string.IsNullOrWhiteSpace(extraParams)) args.Append($" -p \"{extraParams}\"");

        var psi = new ProcessStartInfo {
            FileName = exePath,
            Arguments = args.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

        p.OutputDataReceived += (_, e) => {
            if (e.Data == null) return;
            Application.Invoke(() => log(e.Data));
        };

        p.ErrorDataReceived += (_, e) => {
            if (e.Data == null) return;
            Application.Invoke(() => log("ERR: " + e.Data));
        };

        p.Exited += (_, _) => Application.Invoke(onFinish);

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        return Task.CompletedTask;
    }
}

// ───────────────────────────────────────────────
// MAIN WINDOW
// ───────────────────────────────────────────────
public class MainWindow : Window {
    private readonly ConfigService _config = new();
    private readonly OAuthService _oauth = new();
    private readonly CliService _cli = new();

    private readonly TextView _log;
    private readonly Label _statusLabel;
    private readonly Button _authBtn;
    private readonly Button _runBtn;
    private readonly Button _configBtn;
    private bool _running = false;

    public MainWindow() : base() {
        Title = "Spotify → BeatSaber  |  Ctrl+A=Auth  Ctrl+R=Run  Ctrl+C=Config  Ctrl+Q=Quit";
        X = 0; Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();

        // ── Status bar ──
        _statusLabel = new Label() {
            Text = "● Ready",
            X = 1,
            Y = 0,
            Width = Dim.Fill(1),
            CanFocus = false
        };

        // ── Buttons ──
        _authBtn = new Button() {
            Text = "Auth Spotify",
            X = 1,
            Y = 2,
            CanFocus = true
        };
        _authBtn.Accepting += (_, _) => _ = DoAuth();

        _runBtn = new Button() {
            Text = "Run",
            X = Pos.Right(_authBtn) + 2,
            Y = 2,
            CanFocus = true
        };
        _runBtn.Accepting += (_, _) => _ = DoRun();

        _configBtn = new Button() {
            Text = "Config",
            X = Pos.Right(_runBtn) + 2,
            Y = 2,
            CanFocus = true
        };
        _configBtn.Accepting += (_, _) => ShowConfig();

        var clearBtn = new Button() {
            Text = "Clear Log",
            X = Pos.Right(_configBtn) + 2,
            Y = 2,
            CanFocus = true
        };
        clearBtn.Accepting += (_, _) => _log.Text = "";

        // ── Log area ──
        var logFrame = new FrameView() {
            Title = "Output Log  (Ctrl+L to focus)",
            X = 1,
            Y = 4,
            Width = Dim.Fill(1),
            Height = Dim.Fill(1),
            CanFocus = true
        };

        _log = new TextView() {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            CanFocus = true,
            TabStop = TabBehavior.TabStop
        };

        logFrame.Add(_log);

        Add(_statusLabel, _authBtn, _runBtn, _configBtn, clearBtn, logFrame);

        // ── Global keybinds ──
        KeyBindings.Add(Key.R.WithCtrl, Command.HotKey);
        AddCommand(Command.HotKey, () => {
            _ = DoRun();
            return true;
        });

        // Use KeyDown for multi-key global shortcuts
        KeyDown += (_, e) => {
            if (e.KeyCode == Key.A.WithCtrl) { _ = DoAuth(); e.Handled = true; } else if (e.KeyCode == Key.C.WithCtrl) { ShowConfig(); e.Handled = true; } else if (e.KeyCode == Key.L.WithCtrl) { _log.SetFocus(); e.Handled = true; } else if (e.KeyCode == Key.Q.WithCtrl) { Application.RequestStop(); e.Handled = true; }
        };

        SetStatus("Ready");
        Log("Spotify2BS GUI started. Use Auth to get a token, then Run.");
    }

    // ── Status / Log helpers ──────────────────────────────────────────
    private void SetStatus(string msg) => _statusLabel.Text = $"● {msg}";

    private void Log(string msg) {
        _log.Text += $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
        _log.MoveEnd();
    }

    // ── Auth flow ─────────────────────────────────────────────────────
    private async Task DoAuth() {
        var cfg = _config.Load();

        if (string.IsNullOrWhiteSpace(cfg.ClientId) || string.IsNullOrWhiteSpace(cfg.ClientSecret)) {
            Log("No client credentials found — opening config...");
            ShowConfig();
            cfg = _config.Load();
            if (string.IsNullOrWhiteSpace(cfg.ClientId)) return;
        }

        SetStatus("Authenticating...");
        _authBtn.Enabled = false;

        try {
            var token = await _oauth.Authenticate(cfg, Log);
            _config.SaveToken(token);
            Log($"Authenticated! Token valid for {token.ExpiresIn / 60} minutes.");
            SetStatus("Authenticated ✓");
        } catch (Exception ex) {
            Log($"Auth failed: {ex.Message}");
            SetStatus("Auth failed");
        } finally {
            _authBtn.Enabled = true;
        }
    }

    // ── Run CLI ───────────────────────────────────────────────────────
    private async Task DoRun() {
        if (_running) {
            Log("Already running — please wait.");
            return;
        }

        var cfg = _config.Load();
        var token = _config.GetValidToken();

        if (token == null) {
            Log("No valid token — please authenticate first (Ctrl+A).");
            return;
        }

        if (string.IsNullOrWhiteSpace(cfg.Playlist)) {
            Log("No playlist set — opening config...");
            ShowConfig();
            cfg = _config.Load();
            if (string.IsNullOrWhiteSpace(cfg.Playlist)) return;
        }

        // Show run options dialog
        var opts = ShowRunOptions();
        if (opts == null) return;

        _running = true;
        _runBtn.Enabled = false;
        SetStatus("Running...");
        Log($"Starting: playlist={cfg.Playlist}  dest={opts.Destination}");

        await _cli.Run(
            token.AccessToken,
            cfg.Playlist!,
            opts.Destination,
            opts.Sensitivity,
            opts.Depth,
            opts.Manual,
            opts.Literality,
            opts.ExtraParams,
            opts.DryRun,
            opts.Debug,
            Log,
            () => {
                _running = false;
                _runBtn.Enabled = true;
                SetStatus("Done ✓");
                Log("Run complete.");
            });
    }

    // ── Run options dialog ────────────────────────────────────────────
    private RunOptions? ShowRunOptions() {
        var cfg = _config.Load();
        RunOptions? result = null;

        var d = new Dialog() { Title = "Run Options", Width = 60, Height = 22 };

        int y = 1;

        var destLabel = new Label() { Text = "Destination:", X = 1, Y = y };
        var destField = new TextField() { Text = cfg.Destination, X = 15, Y = y, Width = 40 };
        y += 2;

        var sensLabel = new Label() { Text = "Sensitivity:", X = 1, Y = y };
        var sensField = new TextField() { Text = cfg.Sensitivity.ToString(), X = 15, Y = y, Width = 5 };
        var sensHint = new Label() { Text = "(1=exact 2=nocase 3=fuzzy)", X = 21, Y = y, CanFocus = false };
        y += 2;

        var depthLabel = new Label() { Text = "Depth:", X = 1, Y = y };
        var depthField = new TextField() { Text = cfg.Depth.ToString(), X = 15, Y = y, Width = 5 };
        y += 2;

        var paramLabel = new Label() { Text = "Extra params:", X = 1, Y = y };
        var paramField = new TextField() { Text = cfg.ExtraParams, X = 15, Y = y, Width = 40 };
        y += 2;

        var manualCheck = new CheckBox() { Text = "Manual mode (-m)", X = 1, Y = y, CheckedState = cfg.Manual ? CheckState.Checked : CheckState.UnChecked };
        y += 1;
        var litCheck = new CheckBox() { Text = "Literality (-l)", X = 1, Y = y, CheckedState = cfg.Literality ? CheckState.Checked : CheckState.UnChecked };
        y += 1;
        var dryCheck = new CheckBox() { Text = "Dry run (-D)", X = 1, Y = y, CheckedState = cfg.DryRun ? CheckState.Checked : CheckState.UnChecked };
        y += 1;
        var debugCheck = new CheckBox() { Text = "Debug (--debug)", X = 1, Y = y, CheckedState = cfg.Debug ? CheckState.Checked : CheckState.UnChecked };
        y += 2;

        var runBtn = new Button() { Text = "Run", X = 10, Y = y, IsDefault = true };
        var cancelBtn = new Button() { Text = "Cancel", X = 20, Y = y };

        runBtn.Accepting += (_, _) =>
        {
            result = new RunOptions {
                Destination = destField.Text?.ToString() ?? cfg.Destination,
                Sensitivity = int.TryParse(sensField.Text?.ToString(), out var s) ? s : cfg.Sensitivity,
                Depth = int.TryParse(depthField.Text?.ToString(), out var dep) ? dep : cfg.Depth,
                ExtraParams = paramField.Text?.ToString() ?? "",
                Manual = manualCheck.CheckedState == CheckState.Checked,
                Literality = litCheck.CheckedState == CheckState.Checked,
                DryRun = dryCheck.CheckedState == CheckState.Checked,
                Debug = debugCheck.CheckedState == CheckState.Checked
            };
            Application.RequestStop();
        };

        cancelBtn.Accepting += (_, _) => Application.RequestStop();

        d.Add(destLabel, destField, sensLabel, sensField, sensHint,
              depthLabel, depthField, paramLabel, paramField,
              manualCheck, litCheck, dryCheck, debugCheck,
              runBtn, cancelBtn);

        Application.Run(d);
        d.Dispose();

        return result;
    }

    // ── Config dialog ─────────────────────────────────────────────────
    private void ShowConfig() {
        var cfg = _config.Load();
        var d = new Dialog() { Title = "Configuration", Width = 70, Height = 26 };

        int y = 1;

        // Spotify
        var playlistLabel = new Label() { Text = "Playlist URL:", X = 1, Y = y };
        var playlistField = new TextField() { Text = cfg.Playlist ?? "", X = 18, Y = y, Width = 46 };
        y += 2;

        var idLabel = new Label() { Text = "Client ID:", X = 1, Y = y };
        var idField = new TextField() { Text = cfg.ClientId ?? "", X = 18, Y = y, Width = 46 };
        y += 2;

        var secretLabel = new Label() { Text = "Client Secret:", X = 1, Y = y };
        var secretField = new TextField() { Text = cfg.ClientSecret ?? "", X = 18, Y = y, Width = 46, Secret = true };
        y += 2;

        var sep1 = new Label() { Text = "── Run Defaults ──────────────────────────────────────", X = 1, Y = y, CanFocus = false };
        y += 2;

        var destLabel = new Label() { Text = "Destination:", X = 1, Y = y };
        var destField = new TextField() { Text = cfg.Destination, X = 18, Y = y, Width = 46 };
        y += 2;

        var sensLabel = new Label() { Text = "Sensitivity:", X = 1, Y = y };
        var sensField = new TextField() { Text = cfg.Sensitivity.ToString(), X = 18, Y = y, Width = 5 };
        var sensHint = new Label() { Text = "(1=exact 2=nocase 3=fuzzy)", X = 24, Y = y, CanFocus = false };
        y += 2;

        var depthLabel = new Label() { Text = "Depth:", X = 1, Y = y };
        var depthField = new TextField() { Text = cfg.Depth.ToString(), X = 18, Y = y, Width = 5 };
        y += 2;

        var paramLabel = new Label() { Text = "Extra params:", X = 1, Y = y };
        var paramField = new TextField() { Text = cfg.ExtraParams, X = 18, Y = y, Width = 46 };
        y += 2;

        var manualCheck = new CheckBox() { Text = "Manual mode (-m)", X = 1, Y = y, CheckedState = cfg.Manual ? CheckState.Checked : CheckState.UnChecked };
        y += 1;
        var litCheck = new CheckBox() { Text = "Literality / match artist (-l)", X = 1, Y = y, CheckedState = cfg.Literality ? CheckState.Checked : CheckState.UnChecked };
        y += 1;
        var dryCheck = new CheckBox() { Text = "Dry run (-D)", X = 1, Y = y, CheckedState = cfg.DryRun ? CheckState.Checked : CheckState.UnChecked };
        y += 1;
        var debugCheck = new CheckBox() { Text = "Debug output (--debug)", X = 1, Y = y, CheckedState = cfg.Debug ? CheckState.Checked : CheckState.UnChecked };
        y += 2;

        var tokenStatus = new Label() { Text = _config.GetValidToken() != null ? "Token: ✓ valid" : "Token: ✗ none", X = 1, Y = y, CanFocus = false };

        var saveBtn = new Button() { Text = "Save", X = 5, Y = y + 2, IsDefault = true };
        var clearTokenBtn = new Button() { Text = "Clear Token", X = 15, Y = y + 2 };
        var cancelBtn = new Button() { Text = "Cancel", X = 32, Y = y + 2 };

        saveBtn.Accepting += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(idField.Text?.ToString()) || string.IsNullOrWhiteSpace(secretField.Text?.ToString())) {
                MessageBox.ErrorQuery("Invalid", "Client ID and Secret are required.", "OK");
                return;
            }

            cfg.Playlist = playlistField.Text?.ToString();
            cfg.ClientId = idField.Text?.ToString();
            cfg.ClientSecret = secretField.Text?.ToString();
            cfg.Destination = destField.Text?.ToString() ?? cfg.Destination;
            cfg.Sensitivity = int.TryParse(sensField.Text?.ToString(), out var s) ? s : 2;
            cfg.Depth = int.TryParse(depthField.Text?.ToString(), out var dep) ? dep : 1;
            cfg.ExtraParams = paramField.Text?.ToString() ?? "";
            cfg.Manual = manualCheck.CheckedState == CheckState.Checked;
            cfg.Literality = litCheck.CheckedState == CheckState.Checked;
            cfg.DryRun = dryCheck.CheckedState == CheckState.Checked;
            cfg.Debug = debugCheck.CheckedState == CheckState.Checked;

            _config.Save(cfg);
            Log("Config saved.");
            Application.RequestStop();
        };

        clearTokenBtn.Accepting += (_, _) =>
        {
            if (MessageBox.Query("Clear Token", "Delete saved token?", "Yes", "No") == 0) {
                _config.ClearToken();
                tokenStatus.Text = "Token: ✗ cleared";
                Log("Token cleared.");
            }
        };

        cancelBtn.Accepting += (_, _) => Application.RequestStop();

        d.Add(playlistLabel, playlistField, idLabel, idField, secretLabel, secretField,
              sep1, destLabel, destField, sensLabel, sensField, sensHint,
              depthLabel, depthField, paramLabel, paramField,
              manualCheck, litCheck, dryCheck, debugCheck,
              tokenStatus, saveBtn, clearTokenBtn, cancelBtn);

        playlistField.SetFocus();
        Application.Run(d);
        d.Dispose();
    }
}

// ───────────────────────────────────────────────
// RUN OPTIONS MODEL
// ───────────────────────────────────────────────
public class RunOptions {
    public string Destination { get; set; } = Directory.GetCurrentDirectory();
    public int Sensitivity { get; set; } = 2;
    public int Depth { get; set; } = 1;
    public bool Manual { get; set; }
    public bool Literality { get; set; }
    public string ExtraParams { get; set; } = "";
    public bool DryRun { get; set; }
    public bool Debug { get; set; }
}