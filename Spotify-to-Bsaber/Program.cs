using Spectre.Console.Cli;
using System;
using System.Collections.Generic;
using System.Text;
using Spotify_to_Bsaber;

var app = new CommandApp<Spotify2BS>();
await app.RunAsync(args);
