namespace OriathHub.Plugins.Atlas
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using System.Text;

    /// <summary>
    ///     Persisted settings for the Atlas plugin.
    /// </summary>
    public sealed class AtlasSettings
    {
        /// <summary>Default background colour for map labels that don't match any group.</summary>
        public Vector4 DefaultBackgroundColor = new(0f, 0f, 0f, 0.85f);

        /// <summary>Default font colour for map labels that don't match any group.</summary>
        public Vector4 DefaultFontColor = new(1f, 1f, 1f, 1f);

        /// <summary>Map-name search query; comma-separate multiple terms.</summary>
        public string SearchQuery = string.Empty;

        /// <summary>
        ///     Content search query; comma-separate multiple terms (e.g. <c>Breach, Map Boss</c>).
        ///     Applied as a separate AND filter: a node must match the map-name search (if any) and
        ///     have content matching this query (if any). Matched against the node's content-icon stems.
        /// </summary>
        public string ContentSearchQuery = string.Empty;

        /// <summary>Hide completed map nodes.</summary>
        public bool HideCompletedMaps = true;

        /// <summary>Hide nodes that are not currently accessible.</summary>
        public bool HideNotAccessibleMaps = false;

        /// <summary>Hide nodes that do not match any configured map group.</summary>
        public bool HideNodesOutsideMapGroups = false;

        /// <summary>Draw a coloured border around each label indicating its biome.</summary>
        public bool ShowBiomeBorder = true;

        /// <summary>Thickness of the biome border in pixels.</summary>
        public float BiomeBorderThickness = 2.5f;

        /// <summary>Draw lines between connected revealed atlas nodes.</summary>
        public bool ShowConnections = false;

        /// <summary>Colour of atlas connection lines.</summary>
        public Vector4 ConnectionColor = new(0.40f, 0.70f, 1f, 0.50f);

        /// <summary>Thickness of atlas connection lines in pixels.</summary>
        public float ConnectionThickness = 2f;

        /// <summary>Draw the shortest route from the player marker to the current search result.</summary>
        public bool ShowSearchRoute = true;

        /// <summary>Colour of the search route line.</summary>
        public Vector4 SearchRouteColor = new(1f, 0.65f, 0.15f, 0.95f);

        /// <summary>Thickness of the search route line in pixels.</summary>
        public float SearchRouteThickness = 4f;

        // ── GPS routing weights ────────────────────────────────────────────────────

        /// <summary>Content types to prefer as intermediate waypoints; nodes with matching content get reduced edge cost.</summary>
        public List<string> RoutePreferContent = [];

        /// <summary>Cost reduction (0–0.99) applied per hop through a preferred-content node. 0 = no effect, 0.99 = nearly free.</summary>
        public float RoutePreferContentCostReduction = 0.5f;

        /// <summary>Content types to avoid as intermediate waypoints; nodes with matching content get increased edge cost.</summary>
        public List<string> RouteAvoidContent = [];

        /// <summary>Extra hop-equivalent cost added for each hop through an avoided-content node.</summary>
        public float RouteAvoidContentCostPenalty = 5f;

        /// <summary>Map names to avoid as intermediate waypoints; matched by normalised name substring.</summary>
        public List<string> RouteAvoidMapNames = [];

        /// <summary>Extra hop-equivalent cost added for each hop through an avoided map-type node.</summary>
        public float RouteAvoidMapNameCostPenalty = 5f;

        /// <summary>Maximum hops allowed in a route; 0 = unlimited.</summary>
        public int RouteMaxHops = 0;

        /// <summary>When true, non-runnable intermediate waypoints incur a large cost penalty.</summary>
        public bool RouteOnlyThroughRunnable = false;

        /// <summary>Reference resolution width used to compute the relative UI scale.</summary>
        public float BaseWidth = 1920f;

        /// <summary>Reference resolution height used to compute the relative UI scale.</summary>
        public float BaseHeight = 1080f;

        /// <summary>Pixel offset applied to every label position.</summary>
        public Vector2 AnchorNudge = Vector2.Zero;

        /// <summary>Global scale multiplier applied to label text.</summary>
        public float ScaleMultiplier = 1.1f;

        /// <summary>Named map groups with custom background and font colours.</summary>
        public List<MapGroupSettings> MapGroups = [];

        /// <summary>Staging field for the new-group name input widget.</summary>
        public string GroupNameInput = string.Empty;

        /// <summary>Per-biome-id colour and visibility overrides.</summary>
        public Dictionary<byte, ContentOverride> BiomeOverrides = [];

        /// <summary>
        ///     Initializes a new instance of <see cref="AtlasSettings"/> with the default map groups.
        /// </summary>
        public AtlasSettings()
        {
            var citadels = new MapGroupSettings("Citadels", new Vector4(1f, 1f, 1f, 0.85f), new Vector4(1f, 0f, 0f, 1f));
            citadels.AddEntry("The Copper Citadel");
            citadels.AddEntry("The Iron Citadel");
            citadels.AddEntry("The Stone Citadel");

            var pinnacle = new MapGroupSettings("Pinnacle Boss", new Vector4(0.471f, 0.196f, 0.471f, 0.85f), new Vector4(1f, 1f, 1f, 1f));
            pinnacle.AddEntry("The Burning Monolith");

            var special = new MapGroupSettings("Special", new Vector4(0.737f, 0.376f, 0.145f, 0.85f), new Vector4(0f, 0f, 0f, 1f));
            foreach (var name in new[] { "Untainted Paradise", "Vaults of Kamasa", "Moment of Zen",
                "The Ezomyte Megaliths", "Derelict Mansion", "The Viridian Wildwood",
                "The Jade Isles", "Castaway", "The Fractured Lake", "Ice Cave" })
            {
                special.AddEntry(name);
            }

            var good = new MapGroupSettings("Good", new Vector4(0.157f, 0.157f, 0f, 0.85f), new Vector4(1f, 1f, 0f, 1f));
            foreach (var name in new[] { "Burial Bog", "Creek", "Rustbowl", "Sandspit",
                "Savannah", "Steaming Springs", "Steppe", "Wetlands", "Willow" })
            {
                good.AddEntry(name);
            }

            var towers = new MapGroupSettings("Towers", new Vector4(0.863f, 0f, 0.882f, 0.85f), new Vector4(0f, 0f, 0f, 1f));
            foreach (var name in new[] { "Bluff", "Lost Towers", "Mesa", "Sinking Spire", "Alpine Ridge" })
            {
                towers.AddEntry(name);
            }

            this.MapGroups.AddRange([citadels, towers, pinnacle, good, special]);
        }

        /// <summary>
        ///     Migrates legacy map-name settings into entry settings after deserialization.
        /// </summary>
        public void Normalize()
        {
            foreach (var group in this.MapGroups)
            {
                group.NormalizeEntries();
            }
        }
    }

    /// <summary>
    ///     A named group of map nodes that share a background and font colour.
    /// </summary>
    public class MapGroupSettings(string name, Vector4 backgroundColor, Vector4 fontColor)
    {
        /// <summary>Display name of this group.</summary>
        public string Name = name;

        /// <summary>Whether this group's maps are drawn. When false, none of its maps show.</summary>
        public bool Enabled = true;

        /// <summary>Background colour for nodes in this group.</summary>
        public Vector4 BackgroundColor = backgroundColor;

        /// <summary>Font colour for nodes in this group.</summary>
        public Vector4 FontColor = fontColor;

        /// <summary>Map names belonging to this group.</summary>
        public List<string> Maps = [];

        /// <summary>Map names within this group that are individually disabled (not drawn).</summary>
        public HashSet<string> DisabledMaps = [];

        /// <summary>Map or content entries belonging to this group.</summary>
        public List<MapGroupEntry> Entries = [];

        /// <summary>Staging field for the add-map input widget.</summary>
        public string MapNameInput = string.Empty;

        /// <summary>Adds a map or content entry to this group.</summary>
        public void AddEntry(string name, bool enabled = true)
        {
            this.Entries.Add(new MapGroupEntry
            {
                Id = NewEntryId(),
                Name = name,
                Enabled = enabled,
            });
        }

        /// <summary>Suppresses legacy map-name serialization after migration.</summary>
        public bool ShouldSerializeMaps() => false;

        /// <summary>Suppresses legacy disabled-map serialization after migration.</summary>
        public bool ShouldSerializeDisabledMaps() => false;

        /// <summary>Migrates legacy map-name settings into entry settings after deserialization.</summary>
        public void NormalizeEntries()
        {
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in this.Entries)
            {
                entry.Name ??= string.Empty;

                if (string.IsNullOrWhiteSpace(entry.Id) || !usedIds.Add(entry.Id))
                    entry.Id = NewEntryId();

                usedIds.Add(entry.Id);
            }

            if (this.Entries.Count > 0 || this.Maps.Count == 0)
                return;

            foreach (var map in this.Maps)
            {
                this.AddEntry(map, !this.IsLegacyMapDisabled(map));
            }
        }

        private static string NewEntryId() => Guid.NewGuid().ToString("N");

        private bool IsLegacyMapDisabled(string map) =>
            this.DisabledMaps.Contains(map) ||
            this.DisabledMaps.Any(disabled => NormalizeLegacyName(disabled).Equals(NormalizeLegacyName(map), StringComparison.OrdinalIgnoreCase));

        private static string NormalizeLegacyName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            var trimmed = name.Replace('\u00A0', ' ').Trim();
            var sb = new StringBuilder(trimmed.Length);
            var previousWasSpace = false;
            foreach (var ch in trimmed)
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (!previousWasSpace)
                        sb.Append(' ');
                    previousWasSpace = true;
                    continue;
                }

                sb.Append(ch);
                previousWasSpace = false;
            }

            return sb.ToString();
        }
    }

    /// <summary>
    ///     One map or content row inside a map group.
    /// </summary>
    public class MapGroupEntry
    {
        /// <summary>Stable hidden row identity used by the settings UI.</summary>
        public string Id = string.Empty;

        /// <summary>Map or content name used for runtime atlas matching.</summary>
        public string Name = string.Empty;

        /// <summary>Whether this entry is drawn when matched.</summary>
        public bool Enabled = true;
    }

    /// <summary>
    ///     Optional per-biome overrides for border colour and visibility.
    /// </summary>
    public class ContentOverride
    {
        /// <summary>Override border colour; <c>null</c> means use the default.</summary>
        public Vector4? BorderColor { get; set; }

        /// <summary>Override visibility; <c>null</c> means use the default.</summary>
        public bool? Show { get; set; }
    }
}
