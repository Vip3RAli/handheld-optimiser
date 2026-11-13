using System;
using System.Collections.Generic;
using HandheldOptimiser.PowerService;
using Windows.Data.Json;
using Fields = HandheldOptimiser.PowerService.AutoTdpProtocol.Music.Fields;

namespace HandheldOptimiser.GameBar
{
    /// <summary>A song or playlist the music player listed.</summary>
    internal sealed class MusicItem
    {
        public string Kind;
        public string Id;
        public string Title;
        public string Subtitle;
    }

    /// <summary>What the music player said, or that it is not running.</summary>
    internal sealed class MusicStatus
    {
        /// <summary>False when the player is not running, so all there is to offer is starting it.</summary>
        public bool Running;
        public bool SignedIn;
        public bool Playing;
        public string Title = "";
        public string Artist = "";
        public string Art = "";
        public double Position;
        public double Duration;

        /// <summary>The songs or playlists asked for, or null when none were asked for.</summary>
        public List<MusicItem> Items;
        public string Error;

        /// <summary>A new one each time, as the card keeps its list on the status it shows.</summary>
        public static MusicStatus Off => new MusicStatus();
    }

    /// <summary>
    /// Talks to the YouTube Music player (the launcher's --music mode) over its own pipe, one line of JSON
    /// each way, as EngineClient does to the service. Every call blocks, so call from a background thread.
    /// </summary>
    internal static class MusicClient
    {
        private const string PipePath = @"\\.\pipe\" + AutoTdpProtocol.Music.PipeName;

        public static MusicStatus Ask(string command, string kind = null, string id = null, string query = null)
        {
            var request = new JsonObject { [Fields.Command] = JsonValue.CreateStringValue(command) };
            if (kind != null)
            {
                request[Fields.Kind] = JsonValue.CreateStringValue(kind);
            }

            if (id != null)
            {
                request[Fields.Id] = JsonValue.CreateStringValue(id);
            }

            if (query != null)
            {
                request[Fields.Query] = JsonValue.CreateStringValue(query);
            }

            // Called from async void handlers, where anything thrown would take the widget, and Game Bar's
            // panel with it, down. A fault shows on the card instead.
            try
            {
                var answer = EngineClient.Send(request.Stringify(), PipePath);
                return answer == null ? MusicStatus.Off : Read(answer);
            }
            catch (Exception ex)
            {
                return new MusicStatus { Error = "The music player's answer could not be read (" + ex.GetType().Name + ")" };
            }
        }

        // Windows' JSON strings can never be null: GetNamedString with a null default throws, so a missing
        // field is read here instead. That took the widget down on 2026-11-14.
        private static string Text(JsonObject json, string name, string fallback = "")
        {
            IJsonValue value;
            return json.TryGetValue(name, out value) && value.ValueType == JsonValueType.String ? value.GetString() : fallback;
        }

        private static double Number(JsonObject json, string name)
        {
            IJsonValue value;
            return json.TryGetValue(name, out value) && value.ValueType == JsonValueType.Number ? value.GetNumber() : 0;
        }

        private static bool Flag(JsonObject json, string name)
        {
            IJsonValue value;
            return json.TryGetValue(name, out value) && value.ValueType == JsonValueType.Boolean && value.GetBoolean();
        }

        private static MusicStatus Read(string answer)
        {
            if (!JsonObject.TryParse(answer, out var json))
            {
                return MusicStatus.Off;
            }

            var status = new MusicStatus
            {
                Running = true,
                SignedIn = Flag(json, Fields.SignedIn),
                Playing = Flag(json, Fields.Playing),
                Title = Text(json, Fields.Title),
                Artist = Text(json, Fields.Artist),
                Art = Text(json, Fields.Art),
                Position = Number(json, Fields.Position),
                Duration = Number(json, Fields.Duration),
                Error = Text(json, Fields.Error, null)
            };

            if (json.TryGetValue(Fields.Items, out var items) && items.ValueType == JsonValueType.Array)
            {
                status.Items = new List<MusicItem>();
                foreach (var value in items.GetArray())
                {
                    if (value.ValueType == JsonValueType.Object)
                    {
                        var item = value.GetObject();
                        status.Items.Add(new MusicItem
                        {
                            Kind = Text(item, Fields.Kind),
                            Id = Text(item, Fields.Id),
                            Title = Text(item, Fields.Title),
                            Subtitle = Text(item, Fields.Subtitle)
                        });
                    }
                }
            }

            return status;
        }
    }
}
