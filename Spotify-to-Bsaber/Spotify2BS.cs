using Spectre.Console.Cli;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace Spotify_to_Bsaber {
    public class Spotify2BS : AsyncCommand<Spotify2BS.Settings>{
        private static readonly HttpClient _client = new();

        public record PlaylistResponse(
            PlaylistTracks Tracks
        );

        public record PlaylistTracks(
            string? Next,
            List<PlaylistTrackItem> Items
        );

        public record PlaylistTrackItem(
            TrackInfo Track
        );

        public record TrackInfo(
            string Id,
            string Name
        );



        public class Settings : CommandSettings {
            //Positional commands:
            //  Required:
            [CommandArgument(0, "<token>")]
            [Description("Your access token, for details on getting access token, see https://developer.spotify.com/documentation/web-api/tutorials/getting-started")]
            public string Token { get; init; } = string.Empty;

            [CommandArgument(1, "<playlist>")]
            [Description("Url of the playlist to fetch")]
            public string Url { get; init; } = string.Empty;

            //  Optional:
            [CommandArgument(2, "[destination]")]
            [Description("The destination path (defaults to current directory)")]
            public string? Destination { get; init; }

            //Argumental commands:
            [CommandOption("-s|--sensitivity")]
            [Description("How sensitive to be when detecting. 1 = exact, 2 = case insensitive, >2 = case and punctuation insensitive")]
            [DefaultValue(2)]
            public int sensitivity { get; init; } = 2;

            [CommandOption("-d|--depth")]
            [Description("How many pages deep to search (only consider increasing if many songs fail that should not)")]
            [DefaultValue(1)]
            public int depth { get; init; } = 1;

            [CommandOption("-m|--manual")]
            [Description("Enable manual selection")]
            [DefaultValue(false)]
            public bool manual { get; init; } = false;
        }

        protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken) {
            var options = new JsonSerializerOptions {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            };

            var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.spotify.com/v1/playlists/{NormalisePlaylistId(settings.Url)}");

            Console.WriteLine(NormalisePlaylistId(settings.Url));

            request.Headers.Add("Authorization", $"Bearer {settings.Token}");
            HttpResponseMessage? response = null;

            try {
                response = await _client.SendAsync(request);
                if (!response.IsSuccessStatusCode) {
                    var error = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Error {response.StatusCode}: {error}");
                    return 0;
                }
            } catch (HttpRequestException ex) {
                Console.WriteLine($"Failed Request: {ex.Message}");
                return 0;
            }

            var json = await response.Content.ReadAsStringAsync();

            var playlist = JsonSerializer.Deserialize<PlaylistResponse>(json, options);

            foreach (var item in playlist.Tracks.Items) {
                Console.WriteLine(item.Track.Name);
            }
            return 0;
        }

        private static string NormalisePlaylistId(string input) {
            if (!input.Contains('/') && !input.Contains('?'))
                return input;

            var uri = new Uri(input);
            return uri.Segments.Last().TrimEnd('/');
        }


    }
}
