using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Spotify2Bs {
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
        public record BsMap(string Id, BsMetadata Metadata, List<BsVersion> Versions);
        public record BsMetadata(string SongName, string SongAuthorName, string LevelAuthorName);
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

            [CommandOption("--debug")]
            [Description("Print raw BeatSaver API responses for troubleshooting")]
            [DefaultValue(false)]
            public bool Debug { get; init; } = false;
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

                // When literality is on, still search by song name only — artist matching
                // happens on the returned results, not the query
                var query = track.Name + ", " + artistName;

                AnsiConsole.MarkupLine($"Searching: [cyan]{Markup.Escape(query)}[/]");

                var maps = await FindBeatSaverMaps(query, settings.Depth, settings.ExtraParameters, options, settings.Debug);

                if (maps.Count == 0) {
                    AnsiConsole.MarkupLine($"  [red]No results from BeatSaver[/]");
                    notFound.Add($"{track.Name} - {artistName}");
                    continue;
                }

                BsMap? match;
                if (settings.Manual) {
                    match = PromptManualSelection(maps, track.Name);
                } else {
                    match = FindBestMatch(maps, track.Name, artistName, settings.Sensitivity, settings.Literality);
                }

                if (match == null) {
                    AnsiConsole.MarkupLine($"  [red]No match found[/]");
                    notFound.Add($"{track.Name} - {artistName}");
                    continue;
                }

                var downloadUrl = match.Versions.FirstOrDefault()?.DownloadURL;
                if (downloadUrl == null) {
                    AnsiConsole.MarkupLine($"  [red]No download URL on matched map[/]");
                    notFound.Add($"{track.Name} - {artistName}");
                    continue;
                }

                AnsiConsole.MarkupLine($"  [green]Matched:[/] {Markup.Escape(match.Metadata.SongName)} by {Markup.Escape(match.Metadata.SongAuthorName)} (mapped by {Markup.Escape(match.Metadata.LevelAuthorName)})");
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
                        .Where(i => i?.Track != null)
                        .Select(i => i.Track));
                    nextUrl = playlist.Tracks.Next;
                    firstRequest = false;
                } else {
                    var page = JsonSerializer.Deserialize<PlaylistTracks>(json, options);
                    if (page == null) break;
                    tracks.AddRange(page.Items
                        .Where(i => i?.Track != null)
                        .Select(i => i.Track));
                    nextUrl = page.Next;
                }
            }

            return tracks;
        }

        // ── BeatSaver search ────────────────────────────────────────────
        private async Task<List<BsMap>> FindBeatSaverMaps(string query, int pages, string extraParams, JsonSerializerOptions options, bool debug = false) {
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
                    if (!response.IsSuccessStatusCode) {
                        AnsiConsole.MarkupLine($"  [red]BeatSaver returned {response.StatusCode}[/]");
                        break;
                    }

                    var json = await response.Content.ReadAsStringAsync();

                    if (debug) {
                        AnsiConsole.MarkupLine($"[grey]Raw ({i}): {Markup.Escape(json[..Math.Min(500, json.Length)])}[/]");
                    }

                    var searchResult = JsonSerializer.Deserialize<BsSearchResponse>(json, options);

                    if (debug) {
                        AnsiConsole.MarkupLine($"[grey]Deserialized {searchResult?.Docs?.Count ?? 0} docs[/]");
                    }

                    if (searchResult?.Docs == null || searchResult.Docs.Count == 0) break;
                    results.AddRange(searchResult.Docs);
                } catch (Exception ex) {
                    AnsiConsole.MarkupLine($"  [red]BeatSaver error: {Markup.Escape(ex.Message)}[/]");
                    break;
                }
            }

            return results;
        }

        // ── Matching ─────────────────────────────────────────────────────
        private static BsMap? FindBestMatch(List<BsMap> maps, string songName, string artistName, int sensitivity, bool literality) {
            foreach (var map in maps) {
                var meta = map.Metadata;
                bool songMatches = IsMatch(meta.SongName, songName, sensitivity);
                bool artistMatches = !literality || IsMatch(meta.SongAuthorName, artistName, sensitivity);

                if (songMatches && artistMatches)
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
        private static BsMap? PromptManualSelection(List<BsMap> maps, string trackName) {
            var top = maps.Take(5).ToList();

            // Use index-based choices to avoid markup parsing of song names
            var indexChoices = Enumerable.Range(0, top.Count)
                .Select(i => i.ToString())
                .Append("skip")
                .ToList();

            var prompt = new SelectionPrompt<string>()
                .Title($"Select map for [cyan]{Markup.Escape(trackName)}[/]:")
                .UseConverter(s => {
                    if (s == "skip")
                        return Markup.Escape("[ skip this song ]");

                    var i = int.Parse(s);
                    var m = top[i];

                    return Markup.Escape(
                        $"{m.Metadata.SongName} — {m.Metadata.SongAuthorName} (mapped by {m.Metadata.LevelAuthorName})"
                    );
                })
                .AddChoices(indexChoices);

            var selected = AnsiConsole.Prompt(prompt);
            if (selected == "skip") return null;

            return top[int.Parse(selected)];
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
                        AnsiConsole.MarkupLine($"  [red]✗[/] Failed: {Markup.Escape(ex.Message)}[/]");
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