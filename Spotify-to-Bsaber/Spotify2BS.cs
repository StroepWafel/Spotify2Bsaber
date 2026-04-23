using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Spotify_to_Bsaber {
    public class Spotify2BS : AsyncCommand<Spotify2BS.Settings> {
        private static readonly HttpClient _client = new();

        // ── Spotify models ──────────────────────────────────────────────
        public record PlaylistResponse(PlaylistTracks Tracks);
        public record PlaylistTracks(string? Next, List<PlaylistTrackItem> Items);
        public record PlaylistTrackItem(TrackInfo Track);
        public record TrackInfo(string Id, string Name, List<Artist> Artists);
        public record Artist(string Id, string Name);

        // ── BeatSaver models ────────────────────────────────────────────
        public record BsSearchResponse(List<BsMap> Docs);
        public record BsMap(string Id, string Name, string SongName, string SongAuthorName, string LevelAuthorName, List<BsVersion> Versions);
        public record BsVersion(string DownloadURL, string Hash, string State);

        // ── Settings ────────────────────────────────────────────────────
        public class Settings : CommandSettings {
            [CommandArgument(0, "<token>")]
            [Description("Spotify Bearer access token")]
            public string Token { get; init; } = string.Empty;

            [CommandArgument(1, "<playlist>")]
            [Description("Spotify playlist URL or ID")]
            public string Url { get; init; } = string.Empty;

            [CommandArgument(2, "[destination]")]
            [Description("Download destination path (defaults to current directory)")]
            public string Destination { get; init; } = Directory.GetCurrentDirectory();

            [CommandOption("-s|--sensitivity")]
            [Description("1 = exact, 2 = case-insensitive, 3 = case+punctuation insensitive")]
            [DefaultValue(2)]
            public int Sensitivity { get; init; } = 2;

            [CommandOption("-d|--depth")]
            [Description("How many pages deep to search BeatSaver")]
            [DefaultValue(1)]
            public int Depth { get; init; } = 1;

            [CommandOption("-m|--manual")]
            [Description("Manually pick from top results for each song (Bool)")]
            [DefaultValue(false)]
            public bool Manual { get; init; } = false;

            [CommandOption("-l|--literality")]
            [Description("Include artist name in BeatSaver search query (Bool)")]
            [DefaultValue(false)]
            public bool Literality { get; init; } = false;

            [CommandOption("-p|--params")]
            [Description("Extra query parameters for BeatSaver API (String, see https://api.beatsaver.com/docs/index.html for info)")]
            public string ExtraParameters { get; init; } = "";

            [CommandOption("-D|--dryrun")]
            [Description("Print download URLs without downloading (Bool)")]
            [DefaultValue(false)]
            public bool DryRun { get; init; } = false;
        }

        // ── Entry point ─────────────────────────────────────────────────
        protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken) {
            var options = new JsonSerializerOptions {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            // 1. Fetch Spotify playlist
            AnsiConsole.MarkupLine("[bold]Fetching Spotify playlist...[/]");
            var tracks = await FetchSpotifyTracks(settings.Token, NormalisePlaylistId(settings.Url), options);
            if (tracks == null) return 1;

            AnsiConsole.MarkupLine($"[green]Found {tracks.Count} tracks[/]");

            // 2. Search BeatSaver for each track
            var downloadUrls = new List<string>();
            var notFound = new List<string>();

            foreach (var track in tracks) {
                var artistName = track.Artists.FirstOrDefault()?.Name ?? "";
                var query = settings.Literality
                    ? $"{track.Name} {artistName}"
                    : track.Name;

                AnsiConsole.MarkupLine($"Searching: [cyan]{Markup.Escape(query)}[/]");

                var maps = await FindBeatSaverMaps(query, settings.Depth, settings.ExtraParameters, options);
                var match = FindBestMatch(maps, track.Name, artistName, settings.Sensitivity);

                if (match == null) {
                    AnsiConsole.MarkupLine($"  [red]No match found[/]");
                    notFound.Add($"{track.Name} - {artistName}");
                    continue;
                }

                // Manual selection
                if (settings.Manual && maps.Count > 0) {
                    match = PromptManualSelection(maps, track.Name);
                }

                var downloadUrl = match.Versions.FirstOrDefault()?.DownloadURL;
                if (downloadUrl == null) {
                    notFound.Add($"{track.Name} - {artistName}");
                    continue;
                }

                AnsiConsole.MarkupLine($"  [green]Matched:[/] {Markup.Escape(match.SongName)} by {Markup.Escape(match.LevelAuthorName)}");
                downloadUrls.Add(downloadUrl);
            }

            // 3. Download or print
            AnsiConsole.MarkupLine($"\n[bold]Found {downloadUrls.Count}/{tracks.Count} maps[/]");

            if (settings.DryRun) {
                AnsiConsole.MarkupLine("[yellow]Dry run — URLs only:[/]");
                foreach (var url in downloadUrls)
                    Console.WriteLine(url);
            } else {
                Directory.CreateDirectory(settings.Destination);
                await DownloadMaps(downloadUrls, settings.Destination);
            }

            if (notFound.Count > 0) {
                AnsiConsole.MarkupLine($"\n[red]Not found ({notFound.Count}):[/]");
                foreach (var s in notFound)
                    AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(s)}[/]");
            }

            return 0;
        }

        // ── Spotify ─────────────────────────────────────────────────────
        private async Task<List<TrackInfo>?> FetchSpotifyTracks(string token, string playlistId, JsonSerializerOptions options) {
            var tracks = new List<TrackInfo>();
            string? nextUrl = $"https://api.spotify.com/v1/playlists/{playlistId}";

            // First request gets the full playlist, subsequent ones page through tracks
            bool firstRequest = true;
            while (nextUrl != null) {
                var request = new HttpRequestMessage(HttpMethod.Get, nextUrl);
                request.Headers.Add("Authorization", $"Bearer {token}");

                HttpResponseMessage response;
                try {
                    response = await _client.SendAsync(request);
                    if (!response.IsSuccessStatusCode) {
                        var error = await response.Content.ReadAsStringAsync();
                        AnsiConsole.MarkupLine($"[red]Spotify error {response.StatusCode}: {Markup.Escape(error)}[/]");
                        return null;
                    }
                } catch (HttpRequestException ex) {
                    AnsiConsole.MarkupLine($"[red]Request failed: {Markup.Escape(ex.Message)}[/]");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();

                if (firstRequest) {
                    var playlist = JsonSerializer.Deserialize<PlaylistResponse>(json, options);
                    if (playlist == null) return tracks;
                    tracks.AddRange(playlist.Tracks.Items
                        .Where(i => i.Track != null)
                        .Select(i => i.Track));
                    nextUrl = playlist.Tracks.Next;
                    firstRequest = false;
                } else {
                    var page = JsonSerializer.Deserialize<PlaylistTracks>(json, options);
                    if (page == null) break;
                    tracks.AddRange(page.Items
                        .Where(i => i.Track != null)
                        .Select(i => i.Track));
                    nextUrl = page.Next;
                }
            }

            return tracks;
        }

        // ── BeatSaver search ────────────────────────────────────────────
        private async Task<List<BsMap>> FindBeatSaverMaps(string query, int pages, string extraParams, JsonSerializerOptions options) {
            var results = new List<BsMap>();

            for (int i = 0; i < pages; i++) {
                var url = $"https://api.beatsaver.com/search/text/{i}?q={Uri.EscapeDataString(query)}&sortOrder=Relevance";
                if (!string.IsNullOrEmpty(extraParams))
                    url += $"&{extraParams.TrimStart('&')}";

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Accept", "application/json");
                request.Headers.Add("User-Agent", "Spotify2BSaber/1.0");

                try {
                    var response = await _client.SendAsync(request);
                    if (!response.IsSuccessStatusCode) break;

                    var json = await response.Content.ReadAsStringAsync();
                    var searchResult = JsonSerializer.Deserialize<BsSearchResponse>(json, options);
                    if (searchResult?.Docs == null || searchResult.Docs.Count == 0) break;

                    results.AddRange(searchResult.Docs);
                } catch {
                    break;
                }
            }

            return results;
        }

        // ── Matching ─────────────────────────────────────────────────────
        private static BsMap? FindBestMatch(List<BsMap> maps, string songName, string artistName, int sensitivity) {
            foreach (var map in maps) {
                if (IsMatch(map.SongName, songName, sensitivity))
                    return map;
            }
            return null;
        }

        private static bool IsMatch(string candidate, string target, int sensitivity) {
            return sensitivity switch {
                1 => candidate == target,
                2 => string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase),
                _ => Normalise(candidate) == Normalise(target)
            };
        }

        private static string Normalise(string s) =>
            Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9\s]", "").Trim();

        // ── Manual selection ─────────────────────────────────────────────
        private static BsMap PromptManualSelection(List<BsMap> maps, string trackName) {
            var choices = maps.Take(5).Select(m =>
                $"{m.SongName} — mapped by {m.LevelAuthorName}").ToList();

            var selected = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"Select map for [cyan]{Markup.Escape(trackName)}[/]:")
                    .AddChoices(choices));

            var index = choices.IndexOf(selected);
            return maps[index];
        }

        // ── Downloading ──────────────────────────────────────────────────
        private async Task DownloadMaps(List<string> urls, string destination) {
            await AnsiConsole.Progress().StartAsync(async ctx => {
                var task = ctx.AddTask("Downloading maps", maxValue: urls.Count);

                foreach (var url in urls) {
                    var fileName = Path.Combine(destination, Path.GetFileName(new Uri(url).LocalPath));
                    if (!fileName.EndsWith(".zip")) fileName += ".zip";

                    try {
                        var bytes = await _client.GetByteArrayAsync(url);
                        await File.WriteAllBytesAsync(fileName, bytes);
                        AnsiConsole.MarkupLine($"  [green]✓[/] {Path.GetFileName(fileName)}");
                    } catch (Exception ex) {
                        AnsiConsole.MarkupLine($"  [red]✗[/] Failed: {Markup.Escape(ex.Message)}");
                    }

                    task.Increment(1);
                }
            });
        }

        // ── Helpers ──────────────────────────────────────────────────────
        private static string NormalisePlaylistId(string input) {
            if (!input.Contains('/') && !input.Contains('?'))
                return input;
            var uri = new Uri(input);
            return uri.Segments.Last().TrimEnd('/');
        }
    }
}