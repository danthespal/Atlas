namespace OriathHub.Plugins.Atlas
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Text;
    using ImGuiNET;
    using Newtonsoft.Json;
    using OriathHub;
    using OriathHub.Plugin;
    using OriathHub.RemoteEnums;
    using OriathHub.RemoteObjects.States.InGameStateObjects;
    using OriathHub.RemoteObjects.FilesStructures;
    using OriathHub.RemoteObjects.UiElement;
    using OriathHub.Utils;

    /// <summary>
    ///     Endgame atlas map overlay: labels, biome borders, and group colours.
    ///     Phase 2 adds content badges; Phase 4 adds connection grid and A* routing lines.
    /// </summary>
    public sealed class AtlasPlugin : PluginBase
    {
        /// <inheritdoc />
        public override string Name => "Atlas";

        /// <inheritdoc />
        public override string Description => "Draws labels and biome borders on the endgame atlas map. Supports named map groups, search, and filtering.";

        private AtlasSettings settings = new();
        private readonly Dictionary<byte, BiomeInfo> biomes = new();
        private IntPtr lockedRouteStartAddress = IntPtr.Zero;
        private IDisposable? atlasMapNodesLease;

        // Canonical content display names from EndgameMapContent.dat, used for the content picker.
        // Loaded lazily once the game data is available; [DNT] / hidden entries are filtered out.
        private IReadOnlyList<string> contentNames = Array.Empty<string>();

        private string newGroupName = string.Empty;
        private string routePreferContentInput = string.Empty;
        private string routeAvoidContentInput = string.Empty;
        private string routeAvoidMapInput = string.Empty;

        private string SettingsPath => Path.Combine(this.DllDirectory, "config", "settings.txt");

        /// <inheritdoc />
        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(this.SettingsPath))
            {
                var json = File.ReadAllText(this.SettingsPath);
                var opts = new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace };
                this.settings = JsonConvert.DeserializeObject<AtlasSettings>(json, opts) ?? new AtlasSettings();
            }

            this.settings.Normalize();
            this.atlasMapNodesLease = ImportantUiElements.RequestAtlasMapNodes();
        }

        /// <inheritdoc />
        public override void OnDisable()
        {
            this.atlasMapNodesLease?.Dispose();
            this.atlasMapNodesLease = null;
        }

        /// <inheritdoc />
        public override void SaveSettings()
        {
            var dir = Path.GetDirectoryName(this.SettingsPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(this.SettingsPath, JsonConvert.SerializeObject(this.settings, Formatting.Indented));
        }

        /// <inheritdoc />
        public override void DrawSettings()
        {
            ImGui.SeparatorText("Search Maps");
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 60);
            ImGui.InputTextWithHint("##search", "Search (comma-separate for multiple)", ref this.settings.SearchQuery, 256);
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear"))
                this.settings.SearchQuery = string.Empty;

            ImGui.SeparatorText("Search Content");
            this.DrawContentSearchControls();

            ImGui.SeparatorText("Filters");
            ImGui.Checkbox("Hide Completed Maps", ref this.settings.HideCompletedMaps);
            ImGui.Checkbox("Hide Not Accessible Maps", ref this.settings.HideNotAccessibleMaps);
            ImGui.Checkbox("Hide nodes outside Map Groups", ref this.settings.HideNodesOutsideMapGroups);

            ImGui.SeparatorText("Display");
            ImGui.Checkbox("Show Biome Border", ref this.settings.ShowBiomeBorder);
            if (this.settings.ShowBiomeBorder)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(150);
                ImGui.SliderFloat("Thickness##bth", ref this.settings.BiomeBorderThickness, 1f, 6f);
            }

            ImGui.Checkbox("Show Connections", ref this.settings.ShowConnections);
            if (this.settings.ShowConnections)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(150);
                ImGui.SliderFloat("Thickness##cth", ref this.settings.ConnectionThickness, 1f, 6f);
                ColorSwatch("##ccol", ref this.settings.ConnectionColor);
                ImGui.SameLine();
                ImGui.Text("Line Colour");
            }

            ImGui.SeparatorText("GPS");
            this.DrawGpsSettings();

            ImGui.SeparatorText("Map Groups");
            this.DrawMapGroupsControls();
        }

        private void DrawMapGroupsControls()
        {
            ImGui.SetNextItemWidth(200);
            ImGui.InputTextWithHint("##gname", "group name", ref this.settings.GroupNameInput, 256);
            ImGui.SameLine();
            if (ImGui.Button("Add Group") && !string.IsNullOrWhiteSpace(this.settings.GroupNameInput))
            {
                this.settings.MapGroups.Add(new MapGroupSettings(
                    this.settings.GroupNameInput,
                    this.settings.DefaultBackgroundColor,
                    this.settings.DefaultFontColor));
                this.settings.GroupNameInput = string.Empty;
            }

            for (int i = 0; i < this.settings.MapGroups.Count; i++)
            {
                var group = this.settings.MapGroups[i];

                ImGui.Checkbox($"##gen{i}", ref group.Enabled);
                ImGui.SameLine();
                if (!ImGui.TreeNode($"{group.Name}##g{i}"))
                    continue;

                float h = ImGui.GetFrameHeight();
                if (TriangleButton($"##up{i}", h, Vector4.One, isUp: true))
                    this.MoveGroup(i, -1);
                ImGui.SameLine();
                if (TriangleButton($"##dn{i}", h, Vector4.One, isUp: false))
                    this.MoveGroup(i, 1);
                ImGui.SameLine();
                if (ImGui.Button($"Rename##rn{i}"))
                {
                    this.newGroupName = group.Name;
                    ImGui.OpenPopup($"RenameGroup##{i}");
                }
                ImGui.SameLine();
                if (ImGui.Button($"Delete##del{i}"))
                {
                    this.DeleteGroup(i);
                    ImGui.TreePop();
                    break;
                }
                ImGui.SameLine();
                ColorSwatch($"##gbg{i}", ref group.BackgroundColor);
                ImGui.SameLine();
                ImGui.Text("BG");
                ImGui.SameLine();
                ColorSwatch($"##gfg{i}", ref group.FontColor);
                ImGui.SameLine();
                ImGui.Text("FG");

                for (int j = 0; j < group.Entries.Count; j++)
                {
                    var entry = group.Entries[j];
                    var name = entry.Name;

                    ImGui.Checkbox($"##men{entry.Id}", ref entry.Enabled);
                    ImGui.SameLine();

                    ImGui.SetNextItemWidth(220);
                    if (ImGui.InputTextWithHint($"##m{entry.Id}", "map name", ref name, 256))
                        entry.Name = name;
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"X##dm{entry.Id}"))
                    {
                        group.Entries.RemoveAt(j);
                        break;
                    }
                }

                if (ImGui.SmallButton($"+ Map##am{i}"))
                    group.AddEntry(string.Empty);

                this.EnsureContentLoaded();
                if (this.contentNames.Count > 0)
                {
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(160);
                    if (ImGui.BeginCombo($"##addcontent{i}", "+ Content"))
                    {
                        foreach (var name in this.contentNames)
                        {
                            if (ImGui.Selectable($"{name}##c{i}") &&
                                !group.Entries.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
                            {
                                group.AddEntry(name);
                            }
                        }
                        ImGui.EndCombo();
                    }
                }

                if (ImGui.BeginPopupModal($"RenameGroup##{i}", ImGuiWindowFlags.AlwaysAutoResize))
                {
                    ImGui.InputText("Name##rni", ref this.newGroupName, 256);
                    if (ImGui.Button("OK"))
                    {
                        group.Name = this.newGroupName;
                        ImGui.CloseCurrentPopup();
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel"))
                        ImGui.CloseCurrentPopup();
                    ImGui.EndPopup();
                }

                ImGui.TreePop();
            }
        }

        private void DrawContentSearchControls()
        {
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 60);
            ImGui.InputTextWithHint("##csearch", "Content (comma-separate, e.g. Breach, Map Boss)", ref this.settings.ContentSearchQuery, 256);
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear##cs"))
                this.settings.ContentSearchQuery = string.Empty;

            this.EnsureContentLoaded();
            if (this.contentNames.Count > 0 && ImGui.BeginCombo("##caddsearch", "Add content..."))
            {
                foreach (var name in this.contentNames)
                {
                    if (ImGui.Selectable(name))
                        this.settings.ContentSearchQuery = AppendTerm(this.settings.ContentSearchQuery, name);
                }
                ImGui.EndCombo();
            }
        }

        private void DrawGpsSettings()
        {
            ImGui.Checkbox("Show Search Route", ref this.settings.ShowSearchRoute);
            if (!this.settings.ShowSearchRoute)
                return;

            ImGui.SameLine();
            ImGui.SetNextItemWidth(150);
            ImGui.SliderFloat("Thickness##rth", ref this.settings.SearchRouteThickness, 1f, 10f);
            ColorSwatch("##rcol", ref this.settings.SearchRouteColor);
            ImGui.SameLine();
            ImGui.Text("Route Colour");

            ImGui.SetNextItemWidth(150);
            ImGui.SliderInt("Max Hops##maxhops", ref this.settings.RouteMaxHops, 0, 30);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Maximum hops allowed from start to target. 0 = unlimited.");
            ImGui.SameLine();
            ImGui.TextDisabled(this.settings.RouteMaxHops == 0 ? "(unlimited)" : $"({this.settings.RouteMaxHops} hops max)");

            ImGui.Checkbox("Only route through runnable nodes##onlyrunnable", ref this.settings.RouteOnlyThroughRunnable);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Strongly penalises locked nodes as intermediate waypoints.\nA path through them is still shown if there is no other way.");

            ImGui.Spacing();

            ImGui.Text("Prefer en route:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(220);
            ImGui.InputTextWithHint("##prefercontent", "content, comma-separated", ref this.routePreferContentInput, 512);
            ImGui.SameLine();
            if (ImGui.SmallButton("Add##preferadd") && !string.IsNullOrWhiteSpace(this.routePreferContentInput))
            {
                foreach (var term in this.routePreferContentInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!this.settings.RoutePreferContent.Any(s => s.Equals(term, StringComparison.OrdinalIgnoreCase)))
                        this.settings.RoutePreferContent.Add(term);
                }

                this.routePreferContentInput = string.Empty;
            }

            for (int i = this.settings.RoutePreferContent.Count - 1; i >= 0; i--)
            {
                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.Text(this.settings.RoutePreferContent[i]);
                ImGui.SameLine();
                if (ImGui.SmallButton($"X##rp{i}"))
                    this.settings.RoutePreferContent.RemoveAt(i);
            }

            if (this.settings.RoutePreferContent.Count > 0)
            {
                ImGui.SetNextItemWidth(200);
                ImGui.SliderFloat("Attraction##prefw", ref this.settings.RoutePreferContentCostReduction, 0f, 0.99f, "%.2f");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("How much cheaper it is to pass through a node with preferred content.\n0 = no effect, 0.99 = nearly free.");
            }

            ImGui.Spacing();

            ImGui.Text("Avoid en route:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(220);
            ImGui.InputTextWithHint("##avoidcontent", "content, comma-separated", ref this.routeAvoidContentInput, 512);
            ImGui.SameLine();
            if (ImGui.SmallButton("Add##avoidadd") && !string.IsNullOrWhiteSpace(this.routeAvoidContentInput))
            {
                foreach (var term in this.routeAvoidContentInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!this.settings.RouteAvoidContent.Any(s => s.Equals(term, StringComparison.OrdinalIgnoreCase)))
                        this.settings.RouteAvoidContent.Add(term);
                }

                this.routeAvoidContentInput = string.Empty;
            }

            for (int i = this.settings.RouteAvoidContent.Count - 1; i >= 0; i--)
            {
                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.Text(this.settings.RouteAvoidContent[i]);
                ImGui.SameLine();
                if (ImGui.SmallButton($"X##ra{i}"))
                    this.settings.RouteAvoidContent.RemoveAt(i);
            }

            if (this.settings.RouteAvoidContent.Count > 0)
            {
                ImGui.SetNextItemWidth(200);
                ImGui.SliderFloat("Penalty##avoidw", ref this.settings.RouteAvoidContentCostPenalty, 1f, 20f, "%.1f hops");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Extra hop-equivalent cost added for nodes with avoided content.");
            }

            ImGui.Spacing();

            ImGui.Text("Avoid map types:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(180);
            ImGui.InputTextWithHint("##avoidmapinput", "map name", ref this.routeAvoidMapInput, 256);
            ImGui.SameLine();
            if (ImGui.SmallButton("Add##avoidmapadd") && !string.IsNullOrWhiteSpace(this.routeAvoidMapInput))
            {
                var trimmed = this.routeAvoidMapInput.Trim();
                if (!this.settings.RouteAvoidMapNames.Any(m => m.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
                    this.settings.RouteAvoidMapNames.Add(trimmed);
                this.routeAvoidMapInput = string.Empty;
            }

            for (int i = this.settings.RouteAvoidMapNames.Count - 1; i >= 0; i--)
            {
                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.Text(this.settings.RouteAvoidMapNames[i]);
                ImGui.SameLine();
                if (ImGui.SmallButton($"X##am{i}"))
                    this.settings.RouteAvoidMapNames.RemoveAt(i);
            }

            if (this.settings.RouteAvoidMapNames.Count > 0)
            {
                ImGui.SetNextItemWidth(200);
                ImGui.SliderFloat("Penalty##avoidmapw", ref this.settings.RouteAvoidMapNameCostPenalty, 1f, 20f, "%.1f hops");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Extra hop-equivalent cost added for nodes matching avoided map types.");
            }
        }

        /// <summary>
        ///     Populates <see cref="contentNames"/> from <c>EndgameMapContent.dat</c> in game memory the
        ///     first time the data is available, dropping internal ([DNT]) and blank entries. No-op once loaded.
        /// </summary>
        private void EnsureContentLoaded()
        {
            if (this.contentNames.Count > 0)
                return;

            if (EndgameMapContent.TryGetNames(out var names) && names.Count > 0)
            {
                this.contentNames = names
                    .Where(n => !string.IsNullOrWhiteSpace(n) && !n.StartsWith("[DNT]", StringComparison.Ordinal))
                    .Distinct()
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        private static string AppendTerm(string query, string term)
        {
            if (string.IsNullOrWhiteSpace(query))
                return term;
            return query.TrimEnd().TrimEnd(',') + ", " + term;
        }

        /// <inheritdoc />
        public override void DrawAdvancedSettings()
        {
            this.EnsureBiomesLoaded();

            if (ImGui.CollapsingHeader("Layout"))
            {
                this.DrawLayoutControls();
            }

            if (ImGui.CollapsingHeader("Biome Colours"))
            {
                this.DrawBiomeColoursControls();
            }
        }

        private void DrawLayoutControls()
        {
            ImGui.SetNextItemWidth(300);
            var nudge = this.settings.AnchorNudge;
            if (ImGui.SliderFloat2("Label Nudge (px)", ref nudge, -60f, 60f))
                this.settings.AnchorNudge = nudge;
            ImGui.SetNextItemWidth(150);
            ImGui.SliderFloat("Scale Multiplier", ref this.settings.ScaleMultiplier, 0.5f, 3f);
        }

        private void DrawBiomeColoursControls()
        {
            if (ImGui.BeginTable("biometbl", 3))
                {
                    foreach (var kv in this.biomes)
                    {
                        ImGui.TableNextColumn();
                        var id = kv.Key;
                        var info = kv.Value;

                        if (!this.settings.BiomeOverrides.TryGetValue(id, out var ov))
                        {
                            ov = new ContentOverride();
                            this.settings.BiomeOverrides[id] = ov;
                        }

                        bool show = ov.Show ?? info.Show;
                        if (ImGui.Checkbox($"##bshow{id}", ref show))
                        {
                            ov.Show = show;
                            this.ApplyBiomeOverrides();
                        }

                        ImGui.SameLine();
                        var border = ov.BorderColor ?? info.BdColor;
                        ColorSwatch($"##bcol{id}", ref border);
                        if (!ColorsEqual(border, ov.BorderColor ?? info.BdColor))
                        {
                            ov.BorderColor = border;
                            this.ApplyBiomeOverrides();
                        }

                        ImGui.SameLine();
                        ImGui.Text(string.IsNullOrWhiteSpace(info.Label) ? $"Biome {id}" : info.Label);
                    }
                    ImGui.EndTable();
                }
        }

        /// <inheritdoc />
        public override IEnumerable<SettingSearchEntry> GetSearchableSettings() => new[]
        {
            new SettingSearchEntry("Search Maps", "Search Maps", () =>
            {
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 60);
                ImGui.InputTextWithHint("##search", "Search (comma-separate for multiple)", ref this.settings.SearchQuery, 256);
                ImGui.SameLine();
                if (ImGui.SmallButton("Clear"))
                    this.settings.SearchQuery = string.Empty;
            }, "search maps query filter name"),

            new SettingSearchEntry("Search Content", "Search Content", this.DrawContentSearchControls,
                "search content breach expedition delirium ritual map boss abyss query filter"),

            new SettingSearchEntry("Filters", "Hide Completed Maps",
                () => ImGui.Checkbox("Hide Completed Maps", ref this.settings.HideCompletedMaps)),
            new SettingSearchEntry("Filters", "Hide Not Accessible Maps",
                () => ImGui.Checkbox("Hide Not Accessible Maps", ref this.settings.HideNotAccessibleMaps)),
            new SettingSearchEntry("Filters", "Hide nodes outside Map Groups",
                () => ImGui.Checkbox("Hide nodes outside Map Groups", ref this.settings.HideNodesOutsideMapGroups)),

            new SettingSearchEntry("Display", "Show Biome Border", () =>
            {
                ImGui.Checkbox("Show Biome Border", ref this.settings.ShowBiomeBorder);
                if (this.settings.ShowBiomeBorder)
                {
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(150);
                    ImGui.SliderFloat("Thickness##bth", ref this.settings.BiomeBorderThickness, 1f, 6f);
                }
            }, "biome border thickness display"),

            new SettingSearchEntry("GPS", "GPS Route Settings", this.DrawGpsSettings,
                "gps route search path hops runnable prefer avoid content map types weight penalty reduction thickness colour"),

            new SettingSearchEntry("Map Groups", "Map Groups", this.DrawMapGroupsControls,
                "map groups add rename delete background font color"),

            new SettingSearchEntry("Advanced / Layout", "Layout", this.DrawLayoutControls,
                "label nudge scale multiplier offset position"),
            new SettingSearchEntry("Advanced / Biome Colours", "Biome Colours", this.DrawBiomeColoursControls,
                "biome colours colors border show override"),
        };

        /// <inheritdoc />
        public override void DrawUI()
        {
            if (Core.States.GameCurrentState != GameStateTypes.InGameState)
                return;

            if (!FocusHelper.IsGameOrOverlayForeground())
                return;

            var gameUi = Core.States.InGameStateObject.GameUi;
            if (!gameUi.IsAtlasMapOpen)
                return;

            if (gameUi.LeftPanel.IsVisible || gameUi.RightPanel.IsVisible)
                return;

            var nodes = gameUi.AtlasMapsNodesUiElements;
            if (nodes.Count == 0)
                return;

            this.EnsureBiomesLoaded();

            var drawList = ImGui.GetBackgroundDrawList();
            var foregroundDrawList = ImGui.GetForegroundDrawList();
            var display = ImGui.GetIO().DisplaySize;

            var rawQuery = NormalizeName(this.settings.SearchQuery);
            bool doSearch = !string.IsNullOrWhiteSpace(rawQuery);
            var searchTerms = doSearch
                ? rawQuery.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(NormalizeName)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList()
                : new List<string>();

            bool doContentSearch = !string.IsNullOrWhiteSpace(this.settings.ContentSearchQuery);
            var contentTerms = doContentSearch
                ? this.settings.ContentSearchQuery.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .ToList()
                : new List<string>();

            if (this.settings.ShowConnections)
            {
                var connColor = ImGuiHelper.Color(this.settings.ConnectionColor);
                foreach (var conn in gameUi.AtlasMapConnections)
                {
                    var from = conn.From;
                    var to = conn.To;
                    // Same uninitialized-node guard as the label loop; revealed endpoints have valid size.
                    if (from.Size == Vector2.Zero || to.Size == Vector2.Zero)
                        continue;
                    var a = from.Position + from.Size * 0.5f;
                    var b = to.Position + to.Size * 0.5f;
                    // Clip to the visible viewport (+margin) so off-screen nodes don't draw a web
                    // of lines across the whole panned/zoomed-out atlas.
                    if (!OnScreen(a, display) || !OnScreen(b, display))
                        continue;
                    drawList.AddLine(a, b, connColor, this.settings.ConnectionThickness);
                }
            }

            if (this.settings.ShowSearchRoute)
            {
                this.DrawSearchRoute(
                    drawList,
                    foregroundDrawList,
                    nodes,
                    gameUi.AtlasMapConnections,
                    searchTerms,
                    contentTerms,
                    doSearch,
                    doContentSearch,
                    display);
            }

            foreach (var node in nodes)
            {
                // Skip truly invalid nodes (zero size means uninitialized UI element).
                // Deliberately do NOT check node.IsVisible — fogged nodes are hidden by the
                // game's UI visibility flag but still have valid positions in memory.
                if (node.Size == Vector2.Zero)
                    continue;

                // Skip non-map entries (e.g. the player marker) mixed in among the node children.
                // Their WorldAreas row pointer is bogus, so the area id decodes to non-ASCII garbage.
                // Real map ids are ASCII identifiers (and the id column, unlike the name, is never
                // localized), so anything non-ASCII is not a real map node.
                if (!IsAsciiMapAreaId(node.MapAreaId))
                    continue;

                var mapName = NormalizeName(node.MapName);
                if (string.IsNullOrWhiteSpace(mapName))
                    continue;

                if (!MatchesSearch(node, mapName, searchTerms, contentTerms, doSearch, doContentSearch))
                    continue;

                if (this.settings.HideCompletedMaps && node.IsCompleted)
                    continue;

                // StatusState == 0 means unavailable (neither runnable nor completed)
                if (this.settings.HideNotAccessibleMaps && node.StatusState == 0x00)
                    continue;

                var group = this.ResolveMapGroup(mapName, node.Content, out var disabledMatch);

                if (group == null && (this.settings.HideNodesOutsideMapGroups || disabledMatch))
                    continue;

                var center = node.Position + node.Size * 0.5f;
                if (!OnScreen(center, display))
                    continue;

                var textSize = ImGui.CalcTextSize(mapName);
                var drawPos = center - textSize * 0.5f + this.settings.AnchorNudge;

                var bgColor = group?.BackgroundColor ?? this.settings.DefaultBackgroundColor;
                var fontColor = group?.FontColor ?? this.settings.DefaultFontColor;

                if (node.IsCompleted)
                    bgColor.W *= 0.4f;

                var padding = new Vector2(5, 2);
                var bgMin = drawPos - padding;
                var bgMax = drawPos + textSize + padding;

                if (this.settings.ShowBiomeBorder
                    && this.biomes.TryGetValue(node.EndgameMapBiomeId, out var biome)
                    && biome.Show)
                {
                    var biomeColor = biome.BdColor;
                    if (node.IsCompleted)
                        biomeColor.W *= 0.4f;
                    drawList.AddRect(bgMin, bgMax, ImGuiHelper.Color(biomeColor),
                        3f, ImDrawFlags.RoundCornersAll, this.settings.BiomeBorderThickness);
                }

                drawList.AddRectFilled(bgMin, bgMax, ImGuiHelper.Color(bgColor), 3f);
                drawList.AddText(drawPos, ImGuiHelper.Color(fontColor), mapName);
            }
        }

        private void DrawSearchRoute(
            ImDrawListPtr drawList,
            ImDrawListPtr markerDrawList,
            IReadOnlyList<AtlasMapsNodeUiElement> nodes,
            IReadOnlyList<AtlasMapNodeConnection> connections,
            IReadOnlyList<string> searchTerms,
            IReadOnlyList<string> contentTerms,
            bool doSearch,
            bool doContentSearch,
            Vector2 display)
        {
            if (!doSearch && !doContentSearch)
                return;

            var mapNodes = nodes
                .Where(IsValidMapNode)
                .ToList();
            if (mapNodes.Count == 0)
                return;

            var targets = mapNodes
                .Where(n => !n.IsCompleted && MatchesSearch(n, NormalizeName(n.MapName), searchTerms, contentTerms, doSearch, doContentSearch))
                .ToList();
            if (targets.Count == 0)
                return;

            var start = FindRouteStart(mapNodes, display);
            if (start == null)
                return;

            var routeColor = ImGuiHelper.Color(this.settings.SearchRouteColor);
            DrawRouteStartMarker(markerDrawList, NodeCenter(start), this.lockedRouteStartAddress == start.Address);
            this.DrawRouteStartLockButton(markerDrawList, start);

            var paths = this.FindShortestPaths(start, targets, connections);
            var endMarkerColor = ImGuiHelper.Color(new Vector4(1f, 0.05f, 0.05f, 1f));
            var drawnEnds = new HashSet<AtlasMapsNodeUiElement>();
            var drawnNumbers = new HashSet<AtlasMapsNodeUiElement>();

            foreach (var path in paths)
            {
                for (var i = 0; i < path.Count - 1; i++)
                {
                    var from = path[i];
                    var to = path[i + 1];
                    var dotted = IsCompletedRouteSegment(from, to, i + 1, path.Count);
                    this.DrawRouteSegment(drawList, NodeCenter(from), NodeCenter(to), routeColor, display, dotted);
                }

                if (path.Count > 0 && drawnEnds.Add(path[^1]))
                    DrawRouteEndMarker(markerDrawList, NodeCenter(path[^1]), endMarkerColor);

                // Number each non-completed node in path order so the user knows the run sequence.
                // Completed maps are skipped and do not consume a step number.
                int stepNumber = 1;
                foreach (var node in path)
                {
                    if (node.IsCompleted)
                        continue;
                    if (drawnNumbers.Add(node))
                        DrawRouteNodeNumber(markerDrawList, NodeCenter(node), stepNumber);
                    stepNumber++;
                }
            }

            foreach (var target in targets.Where(t => !drawnEnds.Contains(t)))
            {
                DrawRouteEndMarker(markerDrawList, NodeCenter(target), endMarkerColor);
            }
        }

        private static bool IsValidMapNode(AtlasMapsNodeUiElement node) =>
            node.Size != Vector2.Zero &&
            IsAsciiMapAreaId(node.MapAreaId) &&
            !string.IsNullOrWhiteSpace(NormalizeName(node.MapName));

        private static bool MatchesSearch(
            AtlasMapsNodeUiElement node,
            string mapName,
            IReadOnlyList<string> searchTerms,
            IReadOnlyList<string> contentTerms,
            bool doSearch,
            bool doContentSearch)
        {
            if (doSearch && !searchTerms.Any(t => mapName.Contains(t, StringComparison.OrdinalIgnoreCase)))
                return false;

            return !doContentSearch ||
                node.Content.Any(c => contentTerms.Any(t => c.Contains(t, StringComparison.OrdinalIgnoreCase)));
        }

        private AtlasMapsNodeUiElement? FindRouteStart(
            IReadOnlyList<AtlasMapsNodeUiElement> mapNodes,
            Vector2 display)
        {
            if (this.lockedRouteStartAddress != IntPtr.Zero)
            {
                var locked = mapNodes.FirstOrDefault(n => n.Address == this.lockedRouteStartAddress);
                if (locked != null)
                    return locked;

                this.lockedRouteStartAddress = IntPtr.Zero;
            }

            return FindRouteStartFromScreenCenter(mapNodes, display);
        }

        private static AtlasMapsNodeUiElement? FindRouteStartFromScreenCenter(
            IReadOnlyList<AtlasMapsNodeUiElement> mapNodes,
            Vector2 display)
        {
            var screenCenter = display * 0.5f;
            return mapNodes
                .Where(IsRoutePassable)
                .OrderBy(n => Vector2.DistanceSquared(NodeCenter(n), screenCenter))
                .FirstOrDefault();
        }

        private List<List<AtlasMapsNodeUiElement>> FindShortestPaths(
            AtlasMapsNodeUiElement start,
            IReadOnlyList<AtlasMapsNodeUiElement> targets,
            IReadOnlyList<AtlasMapNodeConnection> connections)
        {
            if (targets.Count == 0)
                return [];

            var adjacency = new Dictionary<AtlasMapsNodeUiElement, List<AtlasMapsNodeUiElement>>();
            foreach (var conn in connections)
            {
                // Include inaccessible nodes so the graph reflects the full topology.
                // The shortest path may cut through locked nodes (they render with dotted
                // segments to signal the user they need to unlock them first).
                if (!IsValidMapNode(conn.From) || !IsValidMapNode(conn.To))
                    continue;

                AddEdge(adjacency, conn.From, conn.To);
                AddEdge(adjacency, conn.To, conn.From);
            }

            var targetSet = targets.ToHashSet();
            if (targetSet.Contains(start))
                targetSet.Remove(start);

            var distances = new Dictionary<AtlasMapsNodeUiElement, float>
            {
                [start] = 0f,
            };
            var previous = new Dictionary<AtlasMapsNodeUiElement, AtlasMapsNodeUiElement?>
            {
                [start] = null,
            };
            var queue = new PriorityQueue<AtlasMapsNodeUiElement, float>();
            queue.Enqueue(start, 0f);

            while (queue.Count > 0 && targetSet.Count > 0)
            {
                var current = queue.Dequeue();
                var currentDistance = distances[current];
                targetSet.Remove(current);

                if (!adjacency.TryGetValue(current, out var nextNodes))
                    continue;

                foreach (var next in nextNodes)
                {
                    var distance = currentDistance + this.NodeRouteCost(next);
                    if (distances.TryGetValue(next, out var knownDistance) && knownDistance <= distance)
                        continue;

                    distances[next] = distance;
                    previous[next] = current;
                    queue.Enqueue(next, distance);
                }
            }

            var maxHops = this.settings.RouteMaxHops;
            return targets
                .Where(t => previous.ContainsKey(t))
                .Select(t => ReconstructPath(t, previous))
                .Where(p => p.Count > 0 && (maxHops == 0 || p.Count - 1 <= maxHops))
                .ToList();
        }

        private void DrawRouteSegment(
            ImDrawListPtr drawList,
            Vector2 from,
            Vector2 to,
            uint color,
            Vector2 display,
            bool dotted)
        {
            if (!OnScreen(from, display) && !OnScreen(to, display))
                return;

            if (!dotted)
            {
                drawList.AddLine(from, to, color, this.settings.SearchRouteThickness);
                return;
            }

            DrawDottedLine(drawList, from, to, color, this.settings.SearchRouteThickness);
        }

        private static void DrawDottedLine(ImDrawListPtr drawList, Vector2 from, Vector2 to, uint color, float thickness)
        {
            var delta = to - from;
            var length = delta.Length();
            if (length <= 0.01f)
                return;

            var direction = delta / length;
            const float dash = 8f;
            const float gap = 6f;
            for (var distance = 0f; distance < length; distance += dash + gap)
            {
                var a = from + direction * distance;
                var b = from + direction * Math.Min(distance + dash, length);
                drawList.AddLine(a, b, color, thickness);
            }
        }

        private static bool IsRoutePassable(AtlasMapsNodeUiElement node) =>
            IsValidMapNode(node) && (node.IsCompleted || node.CanRun);

        private static bool IsCompletedRouteSegment(
            AtlasMapsNodeUiElement from,
            AtlasMapsNodeUiElement to,
            int toIndex,
            int pathCount)
        {
            var toIsFinalTarget = toIndex == pathCount - 1;
            return from.IsCompleted || (to.IsCompleted && !toIsFinalTarget);
        }

        private static void AddEdge(
            Dictionary<AtlasMapsNodeUiElement, List<AtlasMapsNodeUiElement>> adjacency,
            AtlasMapsNodeUiElement from,
            AtlasMapsNodeUiElement to)
        {
            if (!adjacency.TryGetValue(from, out var list))
            {
                list = [];
                adjacency[from] = list;
            }

            list.Add(to);
        }

        private static List<AtlasMapsNodeUiElement> ReconstructPath(
            AtlasMapsNodeUiElement target,
            Dictionary<AtlasMapsNodeUiElement, AtlasMapsNodeUiElement?> previous)
        {
            var path = new List<AtlasMapsNodeUiElement>();
            for (AtlasMapsNodeUiElement? current = target; current != null; current = previous[current])
            {
                path.Add(current);
            }

            path.Reverse();
            return path;
        }

        private static Vector2 NodeCenter(AtlasMapsNodeUiElement node) =>
            node.Position + node.Size * 0.5f;

        private void DrawRouteStartLockButton(ImDrawListPtr drawList, AtlasMapsNodeUiElement start)
        {
            var locked = this.lockedRouteStartAddress == start.Address;
            var buttonPos = NodeCenter(start) + new Vector2(16f, -26f);
            var buttonSize = new Vector2(22f, 18f);

            // Use a tiny transparent ImGui window so InvisibleButton gets proper hit-testing
            // and click delivery — manual WantCaptureMouse checks are unreliable in an overlay.
            ImGui.SetNextWindowPos(buttonPos);
            ImGui.SetNextWindowSize(buttonSize);
            ImGui.SetNextWindowBgAlpha(0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.Zero);
            bool clicked = false;
            bool hovered = false;
            if (ImGui.Begin(
                "##lockbtn_atlas",
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus |
                ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoFocusOnAppearing))
            {
                clicked = ImGui.InvisibleButton("##lock", buttonSize);
                hovered = ImGui.IsItemHovered();
            }

            ImGui.End();
            ImGui.PopStyleVar(2);

            if (clicked)
            {
                this.lockedRouteStartAddress = locked ? IntPtr.Zero : start.Address;
                locked = !locked;
            }

            var buttonMax = buttonPos + buttonSize;
            var fill = locked
                ? new Vector4(0.05f, 0.75f, 0.18f, 0.95f)
                : new Vector4(0f, 0f, 0f, hovered ? 0.90f : 0.70f);
            var border = new Vector4(0.05f, 1f, 0.15f, 1f);
            drawList.AddRectFilled(buttonPos, buttonMax, ImGuiHelper.Color(fill), 3f);
            drawList.AddRect(buttonPos, buttonMax, ImGuiHelper.Color(border), 3f, ImDrawFlags.RoundCornersAll, 1.5f);
            drawList.AddText(buttonPos + new Vector2(6f, 1f), ImGuiHelper.Color(border), locked ? "U" : "L");
        }

        private static void DrawRouteStartMarker(ImDrawListPtr drawList, Vector2 center, bool locked)
        {
            var color = ImGuiHelper.Color(new Vector4(0.05f, 1f, 0.15f, 1f));
            const float radius = 5f;
            const float arm = 10f;
            const float thickness = 2f;
            drawList.AddCircleFilled(center, radius, color);
            drawList.AddLine(center - new Vector2(arm, 0f), center + new Vector2(arm, 0f), color, thickness);
            drawList.AddLine(center - new Vector2(0f, arm), center + new Vector2(0f, arm), color, thickness);
            if (locked)
                drawList.AddCircle(center, 13f, color, 16, 2f);
        }

        private static void DrawRouteEndMarker(ImDrawListPtr drawList, Vector2 center, uint color)
        {
            const float radius = 6f;
            drawList.AddCircleFilled(center, radius, color);
            drawList.AddCircle(center, radius + 3f, color, 16, 2f);
        }

        private static void DrawRouteNodeNumber(ImDrawListPtr drawList, Vector2 center, int number)
        {
            var label = number.ToString();
            var textSize = ImGui.CalcTextSize(label);
            var textPos = center + new Vector2(-textSize.X * 0.5f, -textSize.Y - 10f);
            var bgPad = new Vector2(3f, 1f);
            drawList.AddRectFilled(
                textPos - bgPad,
                textPos + textSize + bgPad,
                ImGuiHelper.Color(new Vector4(0f, 0f, 0f, 0.80f)),
                2f);
            drawList.AddText(textPos, ImGuiHelper.Color(new Vector4(1f, 1f, 1f, 1f)), label);
        }

        private float NodeRouteCost(AtlasMapsNodeUiElement node)
        {
            float cost = 1.0f;

            // Non-runnable intermediate waypoints get a heavy penalty so the router avoids them
            // unless there is literally no other path. Not infinite so targets remain reachable.
            if (this.settings.RouteOnlyThroughRunnable && !IsRoutePassable(node))
                cost += 20f;

            if (this.settings.RoutePreferContent.Count > 0 &&
                node.Content.Any(c => this.settings.RoutePreferContent.Any(
                    p => c.Contains(p, StringComparison.OrdinalIgnoreCase))))
                cost = Math.Max(0.1f, cost - this.settings.RoutePreferContentCostReduction);

            if (this.settings.RouteAvoidContent.Count > 0 &&
                node.Content.Any(c => this.settings.RouteAvoidContent.Any(
                    p => c.Contains(p, StringComparison.OrdinalIgnoreCase))))
                cost += this.settings.RouteAvoidContentCostPenalty;

            if (this.settings.RouteAvoidMapNames.Count > 0)
            {
                var normalized = NormalizeName(node.MapName);
                if (this.settings.RouteAvoidMapNames.Any(
                    m => normalized.Contains(NormalizeName(m), StringComparison.OrdinalIgnoreCase)))
                    cost += this.settings.RouteAvoidMapNameCostPenalty;
            }

            return cost;
        }

        private MapGroupSettings? ResolveMapGroup(string mapName, IReadOnlyList<string> content, out bool disabledMatch)
        {
            disabledMatch = false;

            foreach (var group in this.settings.MapGroups)
            {
                foreach (var entry in group.Entries)
                {
                    if (!EntryMatches(entry.Name, mapName, content))
                        continue;

                    if (!group.Enabled || !entry.Enabled)
                    {
                        disabledMatch = true;
                        continue;
                    }

                    return group;
                }
            }

            return null;
        }

        private static bool EntryMatches(string entryName, string mapName, IReadOnlyList<string> content)
        {
            var normalizedEntry = NormalizeName(entryName);
            if (string.IsNullOrWhiteSpace(normalizedEntry))
                return false;

            return normalizedEntry.Equals(mapName, StringComparison.OrdinalIgnoreCase)
                || content.Any(c => c.Contains(normalizedEntry, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        ///     Populates the biome map from <c>EndgameMapBiomes.dat</c> in game memory the first
        ///     time the data is available. The biome id is the row index; colours default until the
        ///     user overrides them. No-op once loaded.
        /// </summary>
        private void EnsureBiomesLoaded()
        {
            if (this.biomes.Count > 0)
                return;

            if (!EndgameMapBiomes.TryGetNames(out var names) || names.Count == 0)
                return;

            this.biomes.Clear();
            for (var id = 0; id < names.Count; id++)
            {
                var info = new BiomeInfo { Label = names[id] };
                if (DefaultBiomeColors.TryGetValue(names[id], out var rgba))
                    info.BorderColor = rgba;
                this.biomes[(byte)id] = info;
            }

            this.ApplyBiomeOverrides();
        }

        // Representative border colours per biome (RGBA). The game has no biome colour data, so these
        // are chosen to evoke what each biome is: terrain by its natural palette, cities by their
        // faction/mechanic. Keyed by the in-game display name; unknown biomes fall back to white.
        private static readonly Dictionary<string, float[]> DefaultBiomeColors = new()
        {
            ["Water"] = [0.10f, 0.45f, 0.95f, 0.9f],
            ["Mountain"] = [0.60f, 0.60f, 0.62f, 0.9f],
            ["Grass"] = [0.25f, 0.80f, 0.30f, 0.9f],
            ["Forest"] = [0.05f, 0.45f, 0.15f, 0.9f],
            ["Swamp"] = [0.42f, 0.45f, 0.20f, 0.9f],
            ["Desert"] = [0.85f, 0.70f, 0.30f, 0.9f],
            ["Ezomyte City"] = [0.90f, 0.80f, 0.50f, 0.9f],
            ["Faridun City"] = [0.90f, 0.45f, 0.15f, 0.9f],
            ["Vaal City"] = [0.80f, 0.12f, 0.15f, 0.9f],
            ["Breach Stronghold"] = [0.60f, 0.20f, 0.85f, 0.9f],
            ["Ocean"] = [0.05f, 0.20f, 0.62f, 0.9f],
            ["Island"] = [0.10f, 0.72f, 0.65f, 0.9f],
            ["Oriath"] = [0.95f, 0.90f, 0.70f, 0.9f],
        };

        private void ApplyBiomeOverrides()
        {
            foreach (var kv in this.settings.BiomeOverrides)
            {
                if (!this.biomes.TryGetValue(kv.Key, out var info))
                    continue;
                var ov = kv.Value;
                if (ov.BorderColor.HasValue)
                    info.BorderColor = [ov.BorderColor.Value.X, ov.BorderColor.Value.Y, ov.BorderColor.Value.Z, ov.BorderColor.Value.W];
                if (ov.Show.HasValue)
                    info.Show = ov.Show.Value;
            }
        }

        private void MoveGroup(int i, int dir)
        {
            var to = i + dir;
            if ((uint)to >= (uint)this.settings.MapGroups.Count)
                return;
            var item = this.settings.MapGroups[i];
            this.settings.MapGroups.RemoveAt(i);
            this.settings.MapGroups.Insert(to, item);
        }

        private void DeleteGroup(int i)
        {
            if ((uint)i < (uint)this.settings.MapGroups.Count)
                this.settings.MapGroups.RemoveAt(i);
        }

        private static void ColorSwatch(string label, ref Vector4 color)
        {
            if (ImGui.ColorButton(label, color))
                ImGui.OpenPopup(label);
            if (ImGui.BeginPopup(label))
            {
                ImGui.ColorPicker4(label, ref color);
                ImGui.EndPopup();
            }
        }

        private static bool TriangleButton(string id, float size, Vector4 color, bool isUp)
        {
            var pressed = ImGui.Button(id, new Vector2(size, size));
            var draw = ImGui.GetWindowDrawList();
            var pos = ImGui.GetItemRectMin();
            float half = size * 0.25f;
            var c = new Vector2(pos.X + size * 0.5f, pos.Y + size * 0.5f);
            Vector2 p1, p2, p3;
            if (isUp)
            {
                p1 = new Vector2(c.X, c.Y - half);
                p2 = new Vector2(c.X - half, c.Y + half);
                p3 = new Vector2(c.X + half, c.Y + half);
            }
            else
            {
                p1 = new Vector2(c.X - half, c.Y - half);
                p2 = new Vector2(c.X + half, c.Y - half);
                p3 = new Vector2(c.X, c.Y + half);
            }
            draw.AddTriangleFilled(p1, p2, p3, ImGuiHelper.Color(color));
            return pressed;
        }

        // True when a point lies within the screen, expanded by a margin so connections to nodes
        // just past the edge still draw. Keeps the connection overlay to what's actually on screen.
        private static bool OnScreen(Vector2 p, Vector2 display, float margin = 96f) =>
            p.X >= -margin && p.Y >= -margin && p.X <= display.X + margin && p.Y <= display.Y + margin;

        // A real WorldAreas id is a non-empty ASCII identifier (letters/digits/underscore), e.g.
        // "MapVaalFactory". The player marker's id decodes to non-ASCII garbage, so it fails this.
        private static bool IsAsciiMapAreaId(string id)
        {
            if (string.IsNullOrEmpty(id))
                return false;
            foreach (var c in id)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                    return false;
            }
            return true;
        }

        private static string NormalizeName(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return string.Empty;
            return CollapseWhitespace(s.Replace(' ', ' ').Trim());
        }

        private static string CollapseWhitespace(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool prevSpace = false;
            foreach (var ch in s)
            {
                bool sp = char.IsWhiteSpace(ch);
                if (sp) { if (!prevSpace) sb.Append(' '); }
                else sb.Append(ch);
                prevSpace = sp;
            }
            return sb.ToString();
        }

        private static bool ColorsEqual(Vector4 a, Vector4 b, float eps = 0.001f) =>
            Math.Abs(a.X - b.X) < eps && Math.Abs(a.Y - b.Y) < eps &&
            Math.Abs(a.Z - b.Z) < eps && Math.Abs(a.W - b.W) < eps;
    }
}
