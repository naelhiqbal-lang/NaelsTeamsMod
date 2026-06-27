using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Mirror;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ModernTeamMode
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "codex.sbg.modernteammode";
        public const string PluginName = "NaelsTeamsMod";
        public const string PluginVersion = "0.4.17";

        internal static Plugin Instance;

        private ConfigEntry<bool> modEnabled;
        private ConfigEntry<bool> teamModeEnabled;
        private ConfigEntry<bool> showOverlay;
        private ConfigEntry<bool> showOverlayOnlyOnScoreboard;
        private ConfigEntry<bool> useNativeScoreboardHeaders;
        private ConfigEntry<bool> blockFriendlyFire;
        private ConfigEntry<bool> colorCodeNametags;
        private ConfigEntry<bool> colorCodeScoreboard;
        private ConfigEntry<bool> colorCodePlayerSkins;
        private ConfigEntry<int> teamCount;
        private ConfigEntry<string> teamNames;
        private ConfigEntry<string> teamColors;
        private ConfigEntry<string> playerTeamOverrides;
        private ConfigEntry<KeyboardShortcut> toggleOverlayKey;

        private readonly Dictionary<ulong, int> explicitTeams = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, int> runtimeTeams = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, int> automaticTeams = new Dictionary<ulong, int>();
        private readonly Dictionary<int, NetworkConnectionToClient> moddedClientConnections = new Dictionary<int, NetworkConnectionToClient>();
        private readonly Dictionary<ulong, int> originalSkinColors = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, int> appliedSkinColors = new Dictionary<ulong, int>();
        private readonly HashSet<string> skinColorLogMessages = new HashSet<string>();
        private readonly List<TeamSnapshot> teams = new List<TeamSnapshot>();
        private readonly Dictionary<int, TMP_Dropdown> modeDropdowns = new Dictionary<int, TMP_Dropdown>();
        private readonly Dictionary<int, ReorderableList> setupTeamLists = new Dictionary<int, ReorderableList>();
        private readonly List<GameObject> setupTeamLabels = new List<GameObject>();
        private ReorderableListElement pendingDraggedSetupElement;
        private int pendingDraggedSetupTeamIndex = -1;
        private static readonly FieldInfo ReorderableListCurrentField = typeof(ReorderableList).GetField("<Current>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly FieldInfo ReorderableListElementParentField = typeof(ReorderableListElement).GetField("currentParent", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo CosmeticsSettingsField = typeof(PlayerCosmeticsSwitcher).GetField("cosmeticsSettings", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SkinColorsField = typeof(PlayerCosmeticsSettings).GetField("skinColors", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo GetSkinColorMethod = typeof(PlayerCosmeticsSwitcher).GetMethod("GetSkinColor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly Type SkinColorType = typeof(PlayerCosmeticsSettings).GetNestedType("SkinColor", BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo SkinColorBaseColorField = SkinColorType == null ? null : SkinColorType.GetField("baseColor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo CosmeticsPlayerMovementField = typeof(PlayerCosmeticsSwitcher).GetField("playerMovement", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo CosmeticsSkinColorPropsField = typeof(PlayerCosmeticsSwitcher).GetField("skinColorProps", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo CosmeticsHeadRendererField = typeof(PlayerCosmeticsSwitcher).GetField("headRenderer", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo CosmeticsBodyRendererField = typeof(PlayerCosmeticsSwitcher).GetField("bodyRenderer", BindingFlags.Instance | BindingFlags.NonPublic);
        private MatchSetupMenu currentSetupMenu;
        private GUIStyle boxStyle;
        private GUIStyle titleStyle;
        private GUIStyle rowStyle;
        private GUIStyle smallStyle;

        private bool overlayVisible;
        private float nextDropdownScanTime;
        private float nextPlayerColorRefreshTime;
        private float nextNetworkHelloTime;
        private float nextNetworkSyncHealTime;
        private bool networkHandlersRegistered;
        private int teamSyncRevision;
        private int receivedTeamSyncRevision;

        private void Awake()
        {
            Instance = this;
            modEnabled = Config.Bind("General", "Enabled", true, "Turns Modern Team Mode on or off.");
            teamModeEnabled = Config.Bind("General", "TeamModeEnabled", true, "Controls whether the team rules are active.");
            showOverlay = Config.Bind("Overlay", "ShowOverlay", true, "Shows the team scoreboard overlay.");
            showOverlayOnlyOnScoreboard = Config.Bind("Overlay", "ShowOverlayOnlyOnScoreboard", true, "Only shows team totals while the normal TAB scoreboard is visible.");
            useNativeScoreboardHeaders = Config.Bind("Overlay", "UseNativeScoreboardHeaders", true, "Adds team header rows directly inside the normal TAB scoreboard.");
            blockFriendlyFire = Config.Bind("Rules", "BlockFriendlyFire", true, "Blocks same-team hits against teammates and their balls.");
            colorCodeNametags = Config.Bind("Colors", "ColorCodeNametags", true, "Colors player name tags by team.");
            colorCodeScoreboard = Config.Bind("Colors", "ColorCodeScoreboard", true, "Colors player rows on the scoreboard by team.");
            colorCodePlayerSkins = Config.Bind("Colors", "ColorCodePlayerSkins", true, "Temporarily uses the game's own cosmetic skin color system to match each golfer to their team.");
            teamCount = Config.Bind("Teams", "TeamCount", 2, "Number of automatic teams. Values outside 2-8 are clamped.");
            teamNames = Config.Bind("Teams", "TeamNames", "Red,Blue,Green,Gold", "Comma-separated team names.");
            teamColors = Config.Bind("Teams", "TeamColors", "FF5555,5599FF,55CC66,FFD34D", "Comma-separated hex colors matching TeamNames.");
            playerTeamOverrides = Config.Bind("Teams", "PlayerTeamOverrides", "", "Optional Steam ID team overrides, example: 76561198000000000=Red;76561198000000001=Blue");
            toggleOverlayKey = Config.Bind("Overlay", "ToggleOverlayKey", new KeyboardShortcut(KeyCode.F8), "Key used to toggle the team overlay.");

            overlayVisible = showOverlay.Value;
            ParseExplicitTeams();

            playerTeamOverrides.SettingChanged += delegate { ParseExplicitTeams(); };
            Logger.LogInfo("NaelsTeamsMod loaded. Team mode is " + (IsTeamModeActive ? "enabled" : "disabled") + ".");

            new Harmony(PluginGuid).PatchAll();
            RegisterNetworkHandlers(true);
        }

        private void Update()
        {
            if (toggleOverlayKey.Value.IsDown())
            {
                overlayVisible = !overlayVisible;
            }

            TryForceCurrentTeamDropTarget();
            TrySendNetworkHello();
            TryHealTeamSync();

            if (Time.unscaledTime >= nextDropdownScanTime)
            {
                nextDropdownScanTime = Time.unscaledTime + 1f;
                ScanForModeDropdowns();
                TryRefreshTeamSetupUi();
            }

            if (Time.unscaledTime >= nextPlayerColorRefreshTime)
            {
                nextPlayerColorRefreshTime = Time.unscaledTime + 0.4f;
                ApplyTeamPlayerColors();
            }

        }

        private void OnGUI()
        {
            if (!IsTeamModeActive || useNativeScoreboardHeaders.Value || !showOverlay.Value || !overlayVisible)
            {
                return;
            }

            if (showOverlayOnlyOnScoreboard.Value && !IsScoreboardVisible())
            {
                return;
            }

            CourseManager courseManager = GetCourseManager();
            if (courseManager == null)
            {
                DrawWaitingOverlay();
                return;
            }

            BuildTeamSnapshots(courseManager);
            DrawTeamOverlay();
        }

        private CourseManager GetCourseManager()
        {
            try
            {
                if (!CourseManager.HasInstance)
                {
                    return null;
                }

                return CourseManager.Instance;
            }
            catch
            {
                return null;
            }
        }

        internal bool IsTeamModeActive
        {
            get { return modEnabled.Value && teamModeEnabled.Value && TeamCount > 1; }
        }

        internal int TeamCount
        {
            get { return Math.Max(2, Math.Min(8, teamCount.Value)); }
        }

        internal bool ShouldBlockFriendlyFire
        {
            get { return IsTeamModeActive && blockFriendlyFire.Value; }
        }

        internal bool ShouldColorNametags
        {
            get { return IsTeamModeActive && colorCodeNametags.Value; }
        }

        internal bool ShouldColorScoreboard
        {
            get { return IsTeamModeActive && colorCodeScoreboard.Value; }
        }

        internal bool ShouldColorPlayerSkins
        {
            get { return IsTeamModeActive && colorCodePlayerSkins.Value; }
        }

        internal bool ShouldUseNativeScoreboardHeaders
        {
            get { return IsTeamModeActive && useNativeScoreboardHeaders.Value; }
        }

        internal void SetTeamModeEnabled(bool value)
        {
            teamModeEnabled.Value = value;
            foreach (TMP_Dropdown dropdown in modeDropdowns.Values)
            {
                if (dropdown != null)
                {
                    int index = FindDropdownOption(dropdown, value ? "Teams" : "Free-for-all");
                    if (index >= 0)
                    {
                        dropdown.SetValueWithoutNotify(index);
                        dropdown.RefreshShownValue();
                    }
                }
            }

            ApplyTeamPlayerColors();
        }

        private bool IsScoreboardVisible()
        {
            try
            {
                return Scoreboard.HasInstance && Scoreboard.Instance != null && Scoreboard.IsVisible;
            }
            catch
            {
                return false;
            }
        }

        private void DrawWaitingOverlay()
        {
            EnsureStyles();
            GUI.Box(new Rect(18f, 120f, 280f, 58f), GUIContent.none, boxStyle);
            GUI.Label(new Rect(32f, 132f, 252f, 24f), "Modern Team Mode", titleStyle);
            GUI.Label(new Rect(32f, 156f, 252f, 20f), "Waiting for a match...", smallStyle);
        }

        private void DrawTeamOverlay()
        {
            EnsureStyles();

            float width = 340f;
            float height = 60f + Math.Max(1, teams.Count) * 46f;
            Rect panel = new Rect(18f, 120f, width, height);
            GUI.Box(panel, GUIContent.none, boxStyle);
            GUI.Label(new Rect(panel.x + 14f, panel.y + 10f, width - 28f, 24f), "Team Scoreboard", titleStyle);
            GUI.Label(new Rect(panel.x + 14f, panel.y + 34f, width - 28f, 20f), "Auto teams stay balanced. Override in setup if needed.", smallStyle);

            for (int i = 0; i < teams.Count; i++)
            {
                TeamSnapshot team = teams[i];
                float y = panel.y + 60f + i * 46f;

                Color oldColor = GUI.color;
                GUI.color = team.Color;
                GUI.DrawTexture(new Rect(panel.x + 14f, y + 7f, 10f, 30f), Texture2D.whiteTexture);
                GUI.color = oldColor;

                GUI.Label(new Rect(panel.x + 32f, y, 154f, 22f), team.Name + " (" + team.PlayerCount + ")", rowStyle);
                GUI.Label(new Rect(panel.x + 188f, y, 132f, 22f), "Match: " + team.MatchScore, rowStyle);
                GUI.Label(new Rect(panel.x + 32f, y + 22f, 144f, 18f), "Course: " + team.CourseScore, smallStyle);
                GUI.Label(new Rect(panel.x + 188f, y + 22f, 132f, 18f), "Strokes: " + team.Strokes, smallStyle);
            }
        }

        private void BuildTeamSnapshots(CourseManager courseManager)
        {
            teams.Clear();

            string[] names = SplitCsv(teamNames.Value);
            string[] colors = SplitCsv(teamColors.Value);
            int count = TeamCount;

            for (int i = 0; i < count; i++)
            {
                string name = i < names.Length && names[i].Length > 0 ? names[i] : "Team " + (i + 1);
                Color color = ParseColor(i < colors.Length ? colors[i] : "", FallbackColor(i));
                teams.Add(new TeamSnapshot(name, color));
            }

            foreach (CourseManager.PlayerState state in CourseManager.PlayerStates)
            {
                if (!state.isConnected || state.isSpectator || state.playerGuid == 0UL)
                {
                    continue;
                }

                int teamIndex = GetTeamIndex(state.playerGuid);
                TeamSnapshot team = teams[teamIndex];
                team.PlayerCount++;
                team.CourseScore += state.courseScore;
                team.MatchScore += state.matchScore;
                team.Strokes += state.matchStrokes;
            }

            teams.Sort(CompareTeams);
        }

        private int GetTeamIndex(CourseManager.PlayerState state, int count)
        {
            int explicitTeam;
            if (runtimeTeams.TryGetValue(state.playerGuid, out explicitTeam))
            {
                return Math.Max(0, Math.Min(count - 1, explicitTeam));
            }

            if (explicitTeams.TryGetValue(state.playerGuid, out explicitTeam))
            {
                return Math.Max(0, Math.Min(count - 1, explicitTeam));
            }

            RebuildAutomaticTeams(count);
            if (automaticTeams.TryGetValue(state.playerGuid, out explicitTeam))
            {
                return Math.Max(0, Math.Min(count - 1, explicitTeam));
            }

            return 0;
        }

        internal int GetTeamIndex(ulong playerGuid)
        {
            int explicitTeam;
            int count = TeamCount;
            if (runtimeTeams.TryGetValue(playerGuid, out explicitTeam))
            {
                return Math.Max(0, Math.Min(count - 1, explicitTeam));
            }

            if (explicitTeams.TryGetValue(playerGuid, out explicitTeam))
            {
                return Math.Max(0, Math.Min(count - 1, explicitTeam));
            }

            CourseManager.PlayerState state;
            if (TryGetPlayerState(playerGuid, out state))
            {
                return GetTeamIndex(state, count);
            }

            return (int)(playerGuid % (ulong)count);
        }

        private void RebuildAutomaticTeams(int count)
        {
            automaticTeams.Clear();
            if (!CourseManager.HasInstance)
            {
                return;
            }

            List<CourseManager.PlayerState> states = new List<CourseManager.PlayerState>();
            foreach (CourseManager.PlayerState state in CourseManager.PlayerStates)
            {
                if (state.isConnected && !state.isSpectator && state.playerGuid != 0UL)
                {
                    states.Add(state);
                }
            }

            states.Sort(ComparePlayerStatesForAutoTeams);

            int[] teamSizes = new int[count];
            for (int i = 0; i < states.Count; i++)
            {
                CourseManager.PlayerState state = states[i];
                int teamIndex;
                if (runtimeTeams.TryGetValue(state.playerGuid, out teamIndex) || explicitTeams.TryGetValue(state.playerGuid, out teamIndex))
                {
                    int clamped = Math.Max(0, Math.Min(count - 1, teamIndex));
                    automaticTeams[state.playerGuid] = clamped;
                    teamSizes[clamped]++;
                }
            }

            for (int i = 0; i < states.Count; i++)
            {
                CourseManager.PlayerState state = states[i];
                if (automaticTeams.ContainsKey(state.playerGuid))
                {
                    continue;
                }

                int smallestTeam = 0;
                for (int team = 1; team < count; team++)
                {
                    if (teamSizes[team] < teamSizes[smallestTeam])
                    {
                        smallestTeam = team;
                    }
                }

                automaticTeams[state.playerGuid] = smallestTeam;
                teamSizes[smallestTeam]++;
            }
        }

        private static int ComparePlayerStatesForAutoTeams(CourseManager.PlayerState left, CourseManager.PlayerState right)
        {
            int leftJoin = left.joinIndex < 0 ? int.MaxValue : left.joinIndex;
            int rightJoin = right.joinIndex < 0 ? int.MaxValue : right.joinIndex;
            int joinCompare = leftJoin.CompareTo(rightJoin);
            if (joinCompare != 0)
            {
                return joinCompare;
            }

            return left.playerGuid.CompareTo(right.playerGuid);
        }

        internal string GetTeamName(ulong playerGuid)
        {
            string[] names = SplitCsv(teamNames.Value);
            int index = GetTeamIndex(playerGuid);
            return index < names.Length && names[index].Length > 0 ? names[index] : "Team " + (index + 1);
        }

        internal Color GetTeamColor(ulong playerGuid)
        {
            string[] colors = SplitCsv(teamColors.Value);
            int index = GetTeamIndex(playerGuid);
            return ParseColor(index < colors.Length ? colors[index] : "", FallbackColor(index));
        }

        internal bool SameTeam(ulong leftGuid, ulong rightGuid)
        {
            if (leftGuid == 0UL || rightGuid == 0UL || leftGuid == rightGuid)
            {
                return false;
            }

            return GetTeamIndex(leftGuid) == GetTeamIndex(rightGuid);
        }

        internal bool TryGetPlayerState(ulong playerGuid, out CourseManager.PlayerState state)
        {
            if (CourseManager.HasInstance)
            {
                foreach (CourseManager.PlayerState candidate in CourseManager.PlayerStates)
                {
                    if (candidate.playerGuid == playerGuid)
                    {
                        state = candidate;
                        return true;
                    }
                }
            }

            state = default(CourseManager.PlayerState);
            return false;
        }

        internal bool ShouldBlockHit(Hittable target, object hitter)
        {
            if (!ShouldBlockFriendlyFire || target == null || hitter == null)
            {
                return false;
            }

            ulong targetGuid = GetTargetGuid(target);
            ulong hitterGuid = GetGuidFromObject(hitter);
            return SameTeam(targetGuid, hitterGuid);
        }

        internal static ulong GetTargetGuid(Hittable target)
        {
            if (target == null)
            {
                return 0UL;
            }

            GolfBall ball = target.GetComponent<GolfBall>();
            if (ball != null && ball.Owner != null)
            {
                return GetGuidFromObject(ball.Owner);
            }

            PlayerInfo playerInfo = target.GetComponent<PlayerInfo>();
            if (playerInfo != null)
            {
                return GetGuidFromObject(playerInfo);
            }

            PlayerGolfer golfer = target.GetComponent<PlayerGolfer>();
            if (golfer != null)
            {
                return GetGuidFromObject(golfer);
            }

            return GetGuidFromObject(target);
        }

        internal static ulong GetGuidFromObject(object source)
        {
            if (source == null)
            {
                return 0UL;
            }

            try
            {
                PlayerGolfer golfer = source as PlayerGolfer;
                if (golfer != null && golfer.PlayerInfo != null)
                {
                    return GetGuidFromObject(golfer.PlayerInfo);
                }

                PlayerInfo info = source as PlayerInfo;
                if (info != null && info.PlayerId != null)
                {
                    return info.PlayerId.Guid;
                }

                PlayerId id = source as PlayerId;
                if (id != null)
                {
                    return id.Guid;
                }

                Type type = source.GetType();
                PropertyInfo playerInfoProperty = type.GetProperty("PlayerInfo", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (playerInfoProperty != null)
                {
                    ulong guid = GetGuidFromObject(playerInfoProperty.GetValue(source, null));
                    if (guid != 0UL)
                    {
                        return guid;
                    }
                }

                PropertyInfo playerIdProperty = type.GetProperty("PlayerId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (playerIdProperty != null)
                {
                    ulong guid = GetGuidFromObject(playerIdProperty.GetValue(source, null));
                    if (guid != 0UL)
                    {
                        return guid;
                    }
                }

                FieldInfo playerInfoField = type.GetField("playerInfo", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (playerInfoField != null)
                {
                    ulong guid = GetGuidFromObject(playerInfoField.GetValue(source));
                    if (guid != 0UL)
                    {
                        return guid;
                    }
                }

                FieldInfo playerMovementField = type.GetField("playerMovement", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (playerMovementField != null)
                {
                    ulong guid = GetGuidFromObject(playerMovementField.GetValue(source));
                    if (guid != 0UL)
                    {
                        return guid;
                    }
                }
            }
            catch
            {
                return 0UL;
            }

            return 0UL;
        }

        private void ParseExplicitTeams()
        {
            explicitTeams.Clear();
            string[] names = SplitCsv(teamNames.Value);
            Dictionary<string, int> teamIndexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < names.Length; i++)
            {
                if (names[i].Length > 0 && !teamIndexByName.ContainsKey(names[i]))
                {
                    teamIndexByName.Add(names[i], i);
                }
            }

            string value = playerTeamOverrides.Value ?? "";
            string[] entries = value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < entries.Length; i++)
            {
                string[] parts = entries[i].Split(new[] { '=' }, 2);
                if (parts.Length != 2)
                {
                    continue;
                }

                ulong guid;
                if (!ulong.TryParse(parts[0].Trim(), out guid))
                {
                    continue;
                }

                int teamIndex;
                string teamName = parts[1].Trim();
                if (!teamIndexByName.TryGetValue(teamName, out teamIndex) && !int.TryParse(teamName, out teamIndex))
                {
                    continue;
                }

                if (teamIndex > 0)
                {
                    teamIndex--;
                }

                explicitTeams[guid] = teamIndex;
            }
        }

        private static string[] SplitCsv(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return new string[0];
            }

            string[] parts = value.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                parts[i] = parts[i].Trim();
            }

            return parts;
        }

        private static int CompareTeams(TeamSnapshot left, TeamSnapshot right)
        {
            int match = right.MatchScore.CompareTo(left.MatchScore);
            if (match != 0)
            {
                return match;
            }

            int course = right.CourseScore.CompareTo(left.CourseScore);
            if (course != 0)
            {
                return course;
            }

            return left.Name.CompareTo(right.Name);
        }

        private static Color ParseColor(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex))
            {
                return fallback;
            }

            if (hex.StartsWith("#"))
            {
                hex = hex.Substring(1);
            }

            int rgb;
            if (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out rgb))
            {
                return fallback;
            }

            return new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);
        }

        private static Color FallbackColor(int index)
        {
            Color[] colors =
            {
                new Color(1f, 0.25f, 0.25f),
                new Color(0.25f, 0.55f, 1f),
                new Color(0.25f, 0.85f, 0.35f),
                new Color(1f, 0.8f, 0.25f),
                new Color(0.8f, 0.4f, 1f),
                new Color(0.2f, 0.9f, 0.9f),
                new Color(1f, 0.55f, 0.15f),
                new Color(0.9f, 0.9f, 0.9f)
            };

            return colors[index % colors.Length];
        }

        private void EnsureStyles()
        {
            if (boxStyle != null)
            {
                return;
            }

            boxStyle = new GUIStyle(GUI.skin.box);
            boxStyle.normal.background = Texture2D.whiteTexture;
            boxStyle.normal.textColor = Color.white;

            titleStyle = new GUIStyle(GUI.skin.label);
            titleStyle.fontSize = 18;
            titleStyle.fontStyle = FontStyle.Bold;
            titleStyle.normal.textColor = Color.white;

            rowStyle = new GUIStyle(GUI.skin.label);
            rowStyle.fontSize = 15;
            rowStyle.fontStyle = FontStyle.Bold;
            rowStyle.normal.textColor = Color.white;

            smallStyle = new GUIStyle(GUI.skin.label);
            smallStyle.fontSize = 12;
            smallStyle.normal.textColor = new Color(0.82f, 0.86f, 0.92f, 1f);
        }

        internal void TrySetupModeDropdown(MatchSetupMenu menu)
        {
            if (menu == null)
            {
                return;
            }

            currentSetupMenu = menu;

            DropdownOption[] options = menu.GetComponentsInChildren<DropdownOption>(true);
            for (int i = 0; i < options.Length; i++)
            {
                TryPatchModeDropdownOption(options[i]);
            }

            TMP_Dropdown dropdown = FindModeDropdown(menu);
            if (dropdown == null)
            {
                return;
            }

            int id = dropdown.GetInstanceID();
            if (modeDropdowns.ContainsKey(id))
            {
                return;
            }

            if (FindDropdownOption(dropdown, "Teams") < 0)
            {
                dropdown.options.Add(new TMP_Dropdown.OptionData("Teams"));
            }

            modeDropdowns[id] = dropdown;
            dropdown.onValueChanged.AddListener(delegate(int value)
            {
                string selected = value >= 0 && value < dropdown.options.Count ? dropdown.options[value].text : "";
                if (string.Equals(selected, "Teams", StringComparison.OrdinalIgnoreCase))
                {
                    SetTeamModeEnabled(true);
                }
                else if (string.Equals(selected, "Free-for-all", StringComparison.OrdinalIgnoreCase))
                {
                    SetTeamModeEnabled(false);
                }
            });

            SetTeamModeEnabled(teamModeEnabled.Value);
            Logger.LogInfo("Added Teams to the real Match Setup Mode dropdown.");
        }

        internal void TryRefreshTeamSetupUi()
        {
            MatchSetupMenu menu = currentSetupMenu;
            if (menu == null)
            {
                try
                {
                    if (MatchSetupMenu.HasInstance)
                    {
                        menu = MatchSetupMenu.Instance;
                        currentSetupMenu = menu;
                    }
                }
                catch
                {
                    menu = null;
                }
            }

            if (menu == null || menu.playersList == null)
            {
                DestroyTeamSetupUi();
                return;
            }

            if (!IsTeamModeActive)
            {
                DestroyTeamSetupUi();
                return;
            }

            EnsureTeamSetupUi(menu);
            RefreshTeamSetupText();
            MoveAssignedPlayersToTeamLists(menu);
        }

        private void EnsureTeamSetupUi(MatchSetupMenu menu)
        {
            int count = Math.Min(2, TeamCount);
            if (setupTeamLists.Count == count && IsSetupUiAlive(menu))
            {
                return;
            }

            DestroyTeamSetupUi();

            Transform parent = menu.playersList.transform.parent;
            RectTransform sourceRect = menu.playersList.GetComponent<RectTransform>();
            TMP_Text labelTemplate = FindPlayersLabel(parent);

            AttachListHandler(menu.playersList, -1);

            int siblingIndex = menu.playersList.transform.GetSiblingIndex() + 1;
            for (int i = 0; i < count; i++)
            {
                string name = GetTeamNameForIndex(i);
                Color color = GetTeamColorForIndex(i);

                GameObject label = CreateTeamLabel(parent, labelTemplate, name, color, sourceRect, i);
                label.transform.SetSiblingIndex(siblingIndex++);
                setupTeamLabels.Add(label);

                ReorderableList list = CreateTeamList(menu.playersList, parent, sourceRect, i);
                list.transform.SetSiblingIndex(siblingIndex++);
                setupTeamLists[i] = list;
                SetTeamListStructuralText(list, FormatTeamDisplayName(name), color);
                AttachListHandler(list, i);
            }

            Logger.LogInfo("Created Red/Blue team drag lists.");
        }

        private bool IsSetupUiAlive(MatchSetupMenu menu)
        {
            foreach (ReorderableList list in setupTeamLists.Values)
            {
                if (list == null || list.gameObject == null || list.transform.parent != menu.playersList.transform.parent)
                {
                    return false;
                }
            }

            return setupTeamLabels.Count == setupTeamLists.Count;
        }

        private void MoveAssignedPlayersToTeamLists(MatchSetupMenu menu)
        {
            if (ReorderableListElement.Current != null)
            {
                return;
            }

            List<MatchSetupPlayer> players = new List<MatchSetupPlayer>();
            CollectSetupPlayers(menu.playersList, players);
            foreach (ReorderableList list in setupTeamLists.Values)
            {
                CollectSetupPlayers(list, players);
            }

            for (int i = 0; i < players.Count; i++)
            {
                MatchSetupPlayer player = players[i];
                if (player == null || player.Guid == 0UL)
                {
                    continue;
                }

                int teamIndex = GetTeamIndex(player.Guid);
                ReorderableList target;
                if (!setupTeamLists.TryGetValue(teamIndex, out target) || target == null)
                {
                    continue;
                }

                ReorderableListElement element = player.GetComponent<ReorderableListElement>();
                PlaceSetupElementInTeamList(element, target);
            }
        }

        private static void CollectSetupPlayers(ReorderableList list, List<MatchSetupPlayer> players)
        {
            if (list == null || list.contentRoot == null)
            {
                return;
            }

            MatchSetupPlayer[] found = list.contentRoot.GetComponentsInChildren<MatchSetupPlayer>(true);
            for (int i = 0; i < found.Length; i++)
            {
                if (!players.Contains(found[i]))
                {
                    players.Add(found[i]);
                }
            }
        }

        internal void TryHandleAssignedElement(ReorderableList list, ReorderableListElement element)
        {
            if (list == null || element == null)
            {
                return;
            }

            foreach (KeyValuePair<int, ReorderableList> pair in setupTeamLists)
            {
                if (pair.Value == list)
                {
                    RecordSetupDropTarget(pair.Key, element, pair.Value);
                    return;
                }
            }

            if (currentSetupMenu != null && list == currentSetupMenu.playersList)
            {
                HandleSetupPlayerMoved(-1, element, false);
            }
        }

        internal void TryForceCurrentTeamDropTarget()
        {
            if (!IsTeamModeActive || setupTeamLists.Count == 0)
            {
                ClearPendingSetupDrop();
                return;
            }

            ReorderableListElement current = ReorderableListElement.Current;
            if (current == null)
            {
                CommitPendingSetupDrop();
                return;
            }

            pendingDraggedSetupElement = current;
            pendingDraggedSetupTeamIndex = -1;

            foreach (KeyValuePair<int, ReorderableList> pair in setupTeamLists)
            {
                ReorderableList list = pair.Value;
                if (list == null || !list.gameObject.activeInHierarchy || !IsPointerOverList(list))
                {
                    continue;
                }

                SetCurrentReorderableList(list);
                pendingDraggedSetupTeamIndex = pair.Key;
                return;
            }
        }

        private void RecordSetupDropTarget(int teamIndex, ReorderableListElement element, ReorderableList target)
        {
            if (teamIndex < 0 || element == null)
            {
                return;
            }

            pendingDraggedSetupElement = element;
            pendingDraggedSetupTeamIndex = Math.Max(0, Math.Min(TeamCount - 1, teamIndex));

            if (ReorderableListElement.Current == element)
            {
                return;
            }

            PlaceSetupElementInTeamList(element, target);
            HandleSetupPlayerMoved(pendingDraggedSetupTeamIndex, element, true);
            ClearPendingSetupDrop();
        }

        internal void CommitPendingSetupDrop()
        {
            if (pendingDraggedSetupElement == null)
            {
                ClearPendingSetupDrop();
                return;
            }

            if (pendingDraggedSetupTeamIndex >= 0)
            {
                ReorderableList target;
                if (setupTeamLists.TryGetValue(pendingDraggedSetupTeamIndex, out target))
                {
                    PlaceSetupElementInTeamList(pendingDraggedSetupElement, target);
                }

                HandleSetupPlayerMoved(pendingDraggedSetupTeamIndex, pendingDraggedSetupElement, true);
            }

            ClearPendingSetupDrop();
        }

        private void ClearPendingSetupDrop()
        {
            pendingDraggedSetupElement = null;
            pendingDraggedSetupTeamIndex = -1;
        }

        private static void SetCurrentReorderableList(ReorderableList list)
        {
            if (ReorderableListCurrentField != null)
            {
                ReorderableListCurrentField.SetValue(null, list);
            }
        }

        private static bool IsPointerOverList(ReorderableList list)
        {
            RectTransform rect = list == null ? null : list.GetComponent<RectTransform>();
            if (rect == null)
            {
                return false;
            }

            Mouse mouse = Mouse.current;
            if (mouse == null)
            {
                return false;
            }

            Canvas canvas = rect.GetComponentInParent<Canvas>();
            Camera camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            return RectTransformUtility.RectangleContainsScreenPoint(rect, mouse.position.ReadValue(), camera);
        }

        private void AttachListHandler(ReorderableList list, int teamIndex)
        {
            if (list == null)
            {
                return;
            }

            list.OnElementMoved += delegate(ReorderableListElement element)
            {
                RecordSetupDropTarget(teamIndex, element, list);
            };
        }

        private void HandleSetupPlayerMoved(int teamIndex, ReorderableListElement element, bool forceWhileDragging)
        {
            MatchSetupPlayer player = element == null ? null : element.GetComponent<MatchSetupPlayer>();
            if (player == null || player.Guid == 0UL)
            {
                return;
            }

            if (!forceWhileDragging && ReorderableListElement.Current == element)
            {
                return;
            }

            if (teamIndex < 0)
            {
                // Reopening Match Setup briefly places everyone back in the default Players list.
                // Keep the assignment so the rehydrate pass can move them back to Red/Blue.
                return;
            }

            int clampedTeamIndex = Math.Max(0, Math.Min(TeamCount - 1, teamIndex));
            ApplyTeamAssignment(player.Guid, clampedTeamIndex, true, true);
        }

        private void PlaceSetupElementInTeamList(ReorderableListElement element, ReorderableList target)
        {
            if (element == null || target == null || target.contentRoot == null)
            {
                return;
            }

            if (element.transform.parent != target.contentRoot)
            {
                element.transform.SetParent(target.contentRoot, false);
            }

            element.transform.SetAsLastSibling();
            if (ReorderableListElementParentField != null)
            {
                ReorderableListElementParentField.SetValue(element, target);
            }
        }

        private void ApplyTeamAssignment(ulong playerGuid, int teamIndex, bool saveLocalConfig, bool sendNetworkUpdate)
        {
            if (playerGuid == 0UL)
            {
                return;
            }

            int clampedTeamIndex = Math.Max(0, Math.Min(TeamCount - 1, teamIndex));
            int previousTeamIndex = GetTeamIndex(playerGuid);
            int runtimeTeamIndex;
            int explicitTeamIndex;
            bool runtimeAlreadySet = runtimeTeams.TryGetValue(playerGuid, out runtimeTeamIndex) && runtimeTeamIndex == clampedTeamIndex;
            bool explicitAlreadySet = explicitTeams.TryGetValue(playerGuid, out explicitTeamIndex) && explicitTeamIndex == clampedTeamIndex;
            if (runtimeAlreadySet && explicitAlreadySet)
            {
                return;
            }

            runtimeTeams[playerGuid] = clampedTeamIndex;
            explicitTeams[playerGuid] = clampedTeamIndex;

            if (saveLocalConfig && !explicitAlreadySet)
            {
                SaveTeamOverrides();
            }

            ApplyTeamPlayerColors();

            if (previousTeamIndex != clampedTeamIndex)
            {
                Logger.LogInfo("Assigned " + playerGuid + " to " + GetTeamName(playerGuid) + ".");
            }

            if (sendNetworkUpdate)
            {
                SendTeamSyncToModdedClients();
            }
        }

        private void SaveTeamOverrides()
        {
            List<ulong> playerGuids = new List<ulong>(explicitTeams.Keys);
            playerGuids.Sort();

            List<string> entries = new List<string>();
            for (int i = 0; i < playerGuids.Count; i++)
            {
                ulong guid = playerGuids[i];
                int teamIndex = Math.Max(0, Math.Min(TeamCount - 1, explicitTeams[guid]));
                entries.Add(guid + "=" + GetTeamNameForIndex(teamIndex));
            }

            playerTeamOverrides.Value = string.Join(";", entries.ToArray());
            Config.Save();
        }

        internal void RegisterNetworkHandlers(bool force)
        {
            if (networkHandlersRegistered && !force)
            {
                return;
            }

            try
            {
                Writer<TeamHelloMessage>.write = WriteTeamHelloMessage;
                Reader<TeamHelloMessage>.read = ReadTeamHelloMessage;
                Writer<TeamSyncMessage>.write = WriteTeamSyncMessage;
                Reader<TeamSyncMessage>.read = ReadTeamSyncMessage;

                NetworkServer.RegisterHandler<TeamHelloMessage>(OnServerTeamHello, true);
                NetworkClient.RegisterHandler<TeamSyncMessage>(OnClientTeamSync, true);
                networkHandlersRegistered = true;
            }
            catch (Exception exception)
            {
                Logger.LogWarning("Team sync handler registration failed: " + exception.Message);
            }
        }

        internal void ClearModdedClientConnections()
        {
            moddedClientConnections.Clear();
            teamSyncRevision = 0;
            receivedTeamSyncRevision = 0;
            nextNetworkHelloTime = 0f;
            nextNetworkSyncHealTime = 0f;
        }

        internal void RemoveModdedClientConnection(NetworkConnectionToClient connection)
        {
            if (connection != null)
            {
                moddedClientConnections.Remove(connection.connectionId);
            }
        }

        private void TrySendNetworkHello()
        {
            if (!IsTeamModeActive || NetworkServer.active || !NetworkClient.active || !NetworkClient.isConnected)
            {
                return;
            }

            if (Time.unscaledTime < nextNetworkHelloTime)
            {
                return;
            }

            nextNetworkHelloTime = Time.unscaledTime + 5f;
            ulong localGuid = GetLocalPlayerGuid();
            if (localGuid == 0UL)
            {
                return;
            }

            try
            {
                RegisterNetworkHandlers(false);
                NetworkClient.Send(new TeamHelloMessage { playerGuid = localGuid, version = PluginVersion }, 0);
            }
            catch (Exception exception)
            {
                Logger.LogWarning("Team sync hello failed: " + exception.Message);
            }
        }

        private void TryHealTeamSync()
        {
            if (!IsTeamModeActive || !NetworkServer.active || moddedClientConnections.Count == 0)
            {
                return;
            }

            if (Time.unscaledTime < nextNetworkSyncHealTime)
            {
                return;
            }

            nextNetworkSyncHealTime = Time.unscaledTime + 4f;
            SendTeamSyncToModdedClients();
        }

        private void OnServerTeamHello(NetworkConnectionToClient connection, TeamHelloMessage message)
        {
            if (connection == null)
            {
                return;
            }

            RegisterNetworkHandlers(false);
            moddedClientConnections[connection.connectionId] = connection;
            Logger.LogInfo("Registered modded NaelsTeamsMod client " + message.playerGuid + " on connection " + connection.connectionId + ".");
            SendTeamSyncToClient(connection);
        }

        private void OnClientTeamSync(TeamSyncMessage message)
        {
            if (NetworkServer.active)
            {
                return;
            }

            if (message.revision < receivedTeamSyncRevision)
            {
                return;
            }

            Dictionary<ulong, int> incoming = ParseTeamSyncPayload(message.payload);
            runtimeTeams.Clear();
            foreach (KeyValuePair<ulong, int> pair in incoming)
            {
                runtimeTeams[pair.Key] = pair.Value;
            }

            receivedTeamSyncRevision = message.revision;
            ApplyTeamPlayerColors();
            TryRefreshTeamSetupUi();
        }

        private void SendTeamSyncToModdedClients()
        {
            if (!NetworkServer.active || moddedClientConnections.Count == 0)
            {
                return;
            }

            TeamSyncMessage message = new TeamSyncMessage
            {
                revision = ++teamSyncRevision,
                payload = SerializeTeamSyncPayload()
            };

            List<int> deadConnections = new List<int>();
            foreach (KeyValuePair<int, NetworkConnectionToClient> pair in moddedClientConnections)
            {
                NetworkConnectionToClient connection = pair.Value;
                if (connection == null || !connection.isReady)
                {
                    deadConnections.Add(pair.Key);
                    continue;
                }

                try
                {
                    connection.Send(message, 0);
                }
                catch (Exception exception)
                {
                    Logger.LogWarning("Team sync send failed for connection " + pair.Key + ": " + exception.Message);
                    deadConnections.Add(pair.Key);
                }
            }

            for (int i = 0; i < deadConnections.Count; i++)
            {
                moddedClientConnections.Remove(deadConnections[i]);
            }
        }

        private void SendTeamSyncToClient(NetworkConnectionToClient connection)
        {
            if (!NetworkServer.active || connection == null || !connection.isReady)
            {
                return;
            }

            try
            {
                connection.Send(new TeamSyncMessage
                {
                    revision = ++teamSyncRevision,
                    payload = SerializeTeamSyncPayload()
                }, 0);
            }
            catch (Exception exception)
            {
                Logger.LogWarning("Team sync send failed for new client: " + exception.Message);
            }
        }

        private string SerializeTeamSyncPayload()
        {
            Dictionary<ulong, int> assignments = new Dictionary<ulong, int>();

            if (CourseManager.HasInstance)
            {
                foreach (CourseManager.PlayerState state in CourseManager.PlayerStates)
                {
                    if (state.isConnected && !state.isSpectator && state.playerGuid != 0UL)
                    {
                        assignments[state.playerGuid] = GetTeamIndex(state.playerGuid);
                    }
                }
            }

            foreach (KeyValuePair<ulong, int> pair in explicitTeams)
            {
                assignments[pair.Key] = Math.Max(0, Math.Min(TeamCount - 1, pair.Value));
            }

            foreach (KeyValuePair<ulong, int> pair in runtimeTeams)
            {
                assignments[pair.Key] = Math.Max(0, Math.Min(TeamCount - 1, pair.Value));
            }

            List<ulong> playerGuids = new List<ulong>(assignments.Keys);
            playerGuids.Sort();

            List<string> entries = new List<string>();
            for (int i = 0; i < playerGuids.Count; i++)
            {
                ulong guid = playerGuids[i];
                entries.Add(guid + "=" + assignments[guid]);
            }

            return string.Join(";", entries.ToArray());
        }

        private Dictionary<ulong, int> ParseTeamSyncPayload(string payload)
        {
            Dictionary<ulong, int> result = new Dictionary<ulong, int>();
            if (string.IsNullOrEmpty(payload))
            {
                return result;
            }

            string[] entries = payload.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < entries.Length; i++)
            {
                string[] parts = entries[i].Split(new[] { '=' }, 2);
                if (parts.Length != 2)
                {
                    continue;
                }

                ulong guid;
                int teamIndex;
                if (ulong.TryParse(parts[0], out guid) && int.TryParse(parts[1], out teamIndex))
                {
                    result[guid] = Math.Max(0, Math.Min(TeamCount - 1, teamIndex));
                }
            }

            return result;
        }

        private static ulong GetLocalPlayerGuid()
        {
            try
            {
                PlayerId playerId = GameManager.LocalPlayerId;
                if (playerId != null)
                {
                    return playerId.Guid;
                }

                PlayerInfo playerInfo = GameManager.LocalPlayerInfo;
                if (playerInfo != null && playerInfo.PlayerId != null)
                {
                    return playerInfo.PlayerId.Guid;
                }
            }
            catch
            {
                return 0UL;
            }

            return 0UL;
        }

        private static void WriteTeamHelloMessage(NetworkWriter writer, TeamHelloMessage message)
        {
            writer.WriteULong(message.playerGuid);
            writer.WriteString(message.version);
        }

        private static TeamHelloMessage ReadTeamHelloMessage(NetworkReader reader)
        {
            return new TeamHelloMessage
            {
                playerGuid = reader.ReadULong(),
                version = reader.ReadString()
            };
        }

        private static void WriteTeamSyncMessage(NetworkWriter writer, TeamSyncMessage message)
        {
            writer.WriteInt(message.revision);
            writer.WriteString(message.payload);
        }

        private static TeamSyncMessage ReadTeamSyncMessage(NetworkReader reader)
        {
            return new TeamSyncMessage
            {
                revision = reader.ReadInt(),
                payload = reader.ReadString()
            };
        }

        internal void TryMakeSetupPlayerDraggable(MatchSetupPlayer player)
        {
            if (!IsTeamModeActive || player == null)
            {
                return;
            }

            if (!IsHostLike())
            {
                return;
            }

            ReorderableListElement element = player.GetComponent<ReorderableListElement>();
            if (element != null && element.Button != null)
            {
                element.Button.interactable = true;
            }
        }

        private static bool IsHostLike()
        {
            try
            {
                Type networkServer = Type.GetType("Mirror.NetworkServer, Mirror");
                PropertyInfo active = networkServer == null ? null : networkServer.GetProperty("active", BindingFlags.Static | BindingFlags.Public);
                object value = active == null ? null : active.GetValue(null, null);
                return value is bool && (bool)value;
            }
            catch
            {
                return false;
            }
        }

        private void DestroyTeamSetupUi()
        {
            foreach (ReorderableList list in setupTeamLists.Values)
            {
                if (list != null && list.gameObject != null)
                {
                    Destroy(list.gameObject);
                }
            }

            setupTeamLists.Clear();

            for (int i = 0; i < setupTeamLabels.Count; i++)
            {
                if (setupTeamLabels[i] != null)
                {
                    Destroy(setupTeamLabels[i]);
                }
            }

            setupTeamLabels.Clear();
        }

        private static TMP_Text FindPlayersLabel(Transform parent)
        {
            if (parent == null)
            {
                return null;
            }

            TMP_Text[] labels = parent.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < labels.Length; i++)
            {
                if (string.Equals(labels[i].text, "Players", StringComparison.OrdinalIgnoreCase))
                {
                    return labels[i];
                }
            }

            return labels.Length > 0 ? labels[0] : null;
        }

        private GameObject CreateTeamLabel(Transform parent, TMP_Text template, string teamName, Color color, RectTransform sourceRect, int index)
        {
            GameObject label;
            TMP_Text text;
            if (template != null)
            {
                label = Instantiate(template.gameObject, parent);
                text = label.GetComponentInChildren<TMP_Text>(true);
            }
            else
            {
                label = new GameObject("ModernTeamMode_" + teamName + "_Label", typeof(RectTransform), typeof(TextMeshProUGUI));
                label.transform.SetParent(parent, false);
                text = label.GetComponent<TMP_Text>();
            }

            label.name = "ModernTeamMode_" + teamName + "_Label";
            TMP_Text[] labelTexts = label.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < labelTexts.Length; i++)
            {
                labelTexts[i].text = FormatTeamDisplayName(teamName);
                labelTexts[i].color = color;
            }

            RectTransform rect = label.GetComponent<RectTransform>();
            if (rect != null && sourceRect != null)
            {
                rect.anchorMin = sourceRect.anchorMin;
                rect.anchorMax = sourceRect.anchorMax;
                rect.pivot = sourceRect.pivot;
                rect.sizeDelta = new Vector2(sourceRect.sizeDelta.x, 28f);
            }

            return label;
        }

        private ReorderableList CreateTeamList(ReorderableList template, Transform parent, RectTransform sourceRect, int index)
        {
            ReorderableList list = Instantiate(template, parent);
            list.name = "ModernTeamMode_" + GetTeamNameForIndex(index) + "_List";

            RectTransform rect = list.GetComponent<RectTransform>();
            if (rect != null && sourceRect != null)
            {
                rect.anchorMin = sourceRect.anchorMin;
                rect.anchorMax = sourceRect.anchorMax;
                rect.pivot = sourceRect.pivot;
                rect.sizeDelta = new Vector2(sourceRect.sizeDelta.x, 54f);
            }

            ResetReorderableListRuntimeReferences(list);
            ClearListContent(list);
            list.RefreshList();
            return list;
        }

        private void RefreshTeamSetupText()
        {
            for (int i = 0; i < setupTeamLabels.Count; i++)
            {
                if (setupTeamLabels[i] == null)
                {
                    continue;
                }

                string teamName = GetTeamNameForIndex(i);
                Color color = GetTeamColorForIndex(i);
                TMP_Text[] texts = setupTeamLabels[i].GetComponentsInChildren<TMP_Text>(true);
                for (int textIndex = 0; textIndex < texts.Length; textIndex++)
                {
                    texts[textIndex].text = FormatTeamDisplayName(teamName);
                    texts[textIndex].color = color;
                }

                ReorderableList list;
                if (setupTeamLists.TryGetValue(i, out list))
                {
                    SetTeamListStructuralText(list, FormatTeamDisplayName(teamName), color);
                }
            }
        }

        private static void SetTeamListStructuralText(ReorderableList list, string text, Color color)
        {
            if (list == null)
            {
                return;
            }

            TMP_Text[] texts = list.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                if (texts[i].GetComponentInParent<MatchSetupPlayer>() != null)
                {
                    continue;
                }

                texts[i].text = text;
                texts[i].color = color;
            }
        }

        private static void ResetReorderableListRuntimeReferences(ReorderableList list)
        {
            if (list == null)
            {
                return;
            }

            FieldInfo rectField = typeof(ReorderableList).GetField("rectTransform", BindingFlags.Instance | BindingFlags.NonPublic);
            if (rectField != null)
            {
                RectTransform contentRect = list.contentRoot == null ? null : list.contentRoot.GetComponent<RectTransform>();
                rectField.SetValue(list, contentRect != null ? contentRect : list.GetComponent<RectTransform>());
            }

            FieldInfo targetIndexField = typeof(ReorderableList).GetField("targetIndex", BindingFlags.Instance | BindingFlags.NonPublic);
            if (targetIndexField != null)
            {
                targetIndexField.SetValue(list, 0);
            }
        }

        private static void ClearListContent(ReorderableList list)
        {
            if (list == null || list.contentRoot == null)
            {
                return;
            }

            RectTransform dummy = GetListDummy(list);
            for (int i = list.contentRoot.childCount - 1; i >= 0; i--)
            {
                Transform child = list.contentRoot.GetChild(i);
                if (child != null && child != dummy && child.GetComponent<ReorderableListElement>() != null)
                {
                    Destroy(child.gameObject);
                }
            }
        }

        private static RectTransform GetListDummy(ReorderableList list)
        {
            FieldInfo dummyField = typeof(ReorderableList).GetField("dummy", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return dummyField == null ? null : dummyField.GetValue(list) as RectTransform;
        }

        private string GetTeamNameForIndex(int index)
        {
            string[] names = SplitCsv(teamNames.Value);
            return index < names.Length && names[index].Length > 0 ? names[index] : "Team " + (index + 1);
        }

        internal static string FormatTeamDisplayName(string teamName)
        {
            if (string.IsNullOrEmpty(teamName))
            {
                return "TEAM";
            }

            string normalized = teamName.Trim();
            if (normalized.StartsWith("Team ", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(5).Trim();
            }

            return normalized.ToUpperInvariant() + " TEAM";
        }

        private Color GetTeamColorForIndex(int index)
        {
            string[] colors = SplitCsv(teamColors.Value);
            return ParseColor(index < colors.Length ? colors[index] : "", FallbackColor(index));
        }

        internal void ScanForModeDropdowns()
        {
            if (!modEnabled.Value)
            {
                return;
            }

            TMP_Dropdown[] dropdowns = FindObjectsOfType<TMP_Dropdown>(true);
            for (int i = 0; i < dropdowns.Length; i++)
            {
                TryPatchModeDropdown(dropdowns[i], "scan");
            }
        }

        internal void TryPatchModeDropdownOption(DropdownOption option)
        {
            TMP_Dropdown dropdown = GetInnerDropdown(option);
            TryPatchModeDropdown(dropdown, "DropdownOption");
        }

        internal void TryPatchModeDropdown(TMP_Dropdown dropdown, string source)
        {
            if (!LooksLikeModeDropdown(dropdown))
            {
                return;
            }

            int id = dropdown.GetInstanceID();
            if (FindDropdownOption(dropdown, "Teams") < 0)
            {
                dropdown.options.Add(new TMP_Dropdown.OptionData("Teams"));
                dropdown.RefreshShownValue();
            }

            if (!modeDropdowns.ContainsKey(id))
            {
                modeDropdowns[id] = dropdown;
                dropdown.onValueChanged.AddListener(delegate(int value)
                {
                    HandleModeDropdownChanged(dropdown);
                });
                Logger.LogInfo("Added Teams to a Mode dropdown via " + source + ".");
            }

            SetTeamModeEnabled(teamModeEnabled.Value);
        }

        private static bool LooksLikeModeDropdown(TMP_Dropdown dropdown)
        {
            if (dropdown == null || dropdown.options == null)
            {
                return false;
            }

            if (FindDropdownOption(dropdown, "Teams") >= 0)
            {
                return true;
            }

            if (FindDropdownOption(dropdown, "Free-for-all") >= 0)
            {
                return true;
            }

            TMP_Text caption = dropdown.captionText;
            if (caption != null && string.Equals(caption.text, "Free-for-all", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return HasNearbyLabel(dropdown.transform, "Mode");
        }

        private static bool HasNearbyLabel(Transform start, string label)
        {
            Transform cursor = start;
            for (int depth = 0; depth < 4 && cursor != null; depth++)
            {
                TMP_Text[] texts = cursor.GetComponentsInChildren<TMP_Text>(true);
                for (int i = 0; i < texts.Length; i++)
                {
                    if (string.Equals(texts[i].text, label, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                cursor = cursor.parent;
            }

            return false;
        }

        internal void HandleModeDropdownChanged(DropdownOption option)
        {
            HandleModeDropdownChanged(GetInnerDropdown(option));
        }

        private void HandleModeDropdownChanged(TMP_Dropdown dropdown)
        {
            if (dropdown == null || dropdown.value < 0 || dropdown.value >= dropdown.options.Count)
            {
                return;
            }

            string selected = dropdown.options[dropdown.value].text ?? "";
            if (string.Equals(selected, "Teams", StringComparison.OrdinalIgnoreCase))
            {
                teamModeEnabled.Value = true;
            }
            else if (string.Equals(selected, "Free-for-all", StringComparison.OrdinalIgnoreCase))
            {
                teamModeEnabled.Value = false;
            }
        }

        private static TMP_Dropdown GetInnerDropdown(DropdownOption option)
        {
            if (option == null)
            {
                return null;
            }

            FieldInfo dropdownField = typeof(DropdownOption).GetField("dropdown", BindingFlags.Instance | BindingFlags.NonPublic);
            TMP_Dropdown dropdown = dropdownField == null ? null : dropdownField.GetValue(option) as TMP_Dropdown;
            if (dropdown != null)
            {
                return dropdown;
            }

            return option.GetComponentInChildren<TMP_Dropdown>(true);
        }

        private static TMP_Dropdown FindModeDropdown(MatchSetupMenu menu)
        {
            TMP_Dropdown[] dropdowns = menu.GetComponentsInChildren<TMP_Dropdown>(true);
            for (int i = 0; i < dropdowns.Length; i++)
            {
                if (FindDropdownOption(dropdowns[i], "Free-for-all") >= 0)
                {
                    return dropdowns[i];
                }
            }

            try
            {
                Transform cursor = menu.transform;
                int[] oldModPath = { 0, 1, 2, 0, 2, 0, 0, 2, 0 };
                for (int i = 0; i < oldModPath.Length; i++)
                {
                    cursor = cursor.GetChild(oldModPath[i]);
                }

                return cursor.GetComponent<TMP_Dropdown>();
            }
            catch
            {
                return null;
            }
        }

        private static int FindDropdownOption(TMP_Dropdown dropdown, string text)
        {
            if (dropdown == null)
            {
                return -1;
            }

            for (int i = 0; i < dropdown.options.Count; i++)
            {
                if (string.Equals(dropdown.options[i].text, text, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        internal static bool TrySetTextField(object instance, string fieldName, string value, Color? color)
        {
            if (instance == null)
            {
                return false;
            }

            FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null)
            {
                return false;
            }

            TMP_Text text = field.GetValue(instance) as TMP_Text;
            if (text == null)
            {
                return false;
            }

            text.text = value;
            if (color.HasValue)
            {
                text.color = color.Value;
            }

            return true;
        }

        internal static void TryTintImages(GameObject gameObject, Color color, float alpha)
        {
            if (gameObject == null)
            {
                return;
            }

            Image[] images = gameObject.GetComponentsInChildren<Image>(true);
            if (images.Length == 0)
            {
                return;
            }

            Color tint = new Color(color.r, color.g, color.b, alpha);
            images[0].color = Color.Lerp(images[0].color, tint, 0.45f);
        }

        internal void ApplyTeamPlayerColors()
        {
            PlayerInfo[] players = UnityEngine.Object.FindObjectsOfType<PlayerInfo>();
            for (int i = 0; i < players.Length; i++)
            {
                PlayerInfo player = players[i];
                ulong guid = GetGuidFromObject(player);
                if (player == null || guid == 0UL)
                {
                    continue;
                }

                PlayerCosmeticsSwitcher switcher = GetCosmeticsSwitcher(player);
                if (switcher == null)
                {
                    continue;
                }

                if (!ShouldColorPlayerSkins)
                {
                    RestoreOriginalSkinColor(guid, switcher);
                    continue;
                }

                if (!originalSkinColors.ContainsKey(guid))
                {
                    originalSkinColors[guid] = switcher.CurrentSkinColorIndex;
                }

                int originalIndex;
                originalSkinColors.TryGetValue(guid, out originalIndex);
                int skinColorIndex = FindClosestCosmeticSkinColor(switcher, GetTeamColor(guid), originalIndex, GetTeamIndex(guid));
                if (skinColorIndex < 0)
                {
                    continue;
                }

                if (switcher.CurrentSkinColorIndex != skinColorIndex)
                {
                    switcher.SetSkinColor(skinColorIndex);
                }

                TryApplyTeamSkinColor(switcher);
                appliedSkinColors[guid] = skinColorIndex;
            }
        }

        internal void TryApplyTeamSkinColor(PlayerCosmeticsSwitcher switcher)
        {
            if (!ShouldColorPlayerSkins || switcher == null)
            {
                return;
            }

            try
            {
                PlayerMovement movement = CosmeticsPlayerMovementField == null ? null : CosmeticsPlayerMovementField.GetValue(switcher) as PlayerMovement;
                PlayerInfo playerInfo = movement == null ? null : movement.PlayerInfo;
                ulong guid = GetGuidFromObject(playerInfo);
                if (guid == 0UL)
                {
                    return;
                }

                Color teamColor = GetTeamColor(guid);
                MaterialPropertyBlock block = CosmeticsSkinColorPropsField == null ? null : CosmeticsSkinColorPropsField.GetValue(switcher) as MaterialPropertyBlock;
                if (block == null)
                {
                    block = new MaterialPropertyBlock();
                    if (CosmeticsSkinColorPropsField != null)
                    {
                        CosmeticsSkinColorPropsField.SetValue(switcher, block);
                    }
                }

                block.SetColor("_Color", teamColor);
                Renderer headRenderer = CosmeticsHeadRendererField == null ? null : CosmeticsHeadRendererField.GetValue(switcher) as Renderer;
                Renderer bodyRenderer = CosmeticsBodyRendererField == null ? null : CosmeticsBodyRendererField.GetValue(switcher) as Renderer;
                if (headRenderer != null)
                {
                    headRenderer.SetPropertyBlock(block);
                }

                if (bodyRenderer != null)
                {
                    bodyRenderer.SetPropertyBlock(block);
                }

                string logKey = guid + ":" + GetTeamIndex(guid) + ":skin";
                if (skinColorLogMessages.Add(logKey))
                {
                    Logger.LogInfo("Applied team skin color for " + guid + " using " + GetTeamName(guid) + ".");
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Could not apply team skin color: " + ex.Message);
            }
        }

        private static PlayerCosmeticsSwitcher GetCosmeticsSwitcher(PlayerInfo player)
        {
            if (player == null)
            {
                return null;
            }

            PlayerCosmetics cosmetics = player.Cosmetics;
            if (cosmetics == null)
            {
                return null;
            }

            return cosmetics.Switcher;
        }

        private void RestoreOriginalSkinColor(ulong guid, PlayerCosmeticsSwitcher switcher)
        {
            int original;
            if (switcher == null || !originalSkinColors.TryGetValue(guid, out original))
            {
                return;
            }

            if (switcher.CurrentSkinColorIndex != original)
            {
                switcher.SetSkinColor(original);
            }

            originalSkinColors.Remove(guid);
            appliedSkinColors.Remove(guid);
        }

        private static int FindClosestCosmeticSkinColor(PlayerCosmeticsSwitcher switcher, Color target, int originalIndex, int teamIndex)
        {
            if (switcher == null || CosmeticsSettingsField == null || SkinColorsField == null || SkinColorBaseColorField == null)
            {
                return -1;
            }

            object settings = CosmeticsSettingsField.GetValue(switcher);
            Array skinColors = settings == null ? null : SkinColorsField.GetValue(settings) as Array;
            if (skinColors == null || skinColors.Length == 0)
            {
                return -1;
            }

            int bestIndex = -1;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < skinColors.Length; i++)
            {
                object skinColor = null;
                if (GetSkinColorMethod != null)
                {
                    try
                    {
                        skinColor = GetSkinColorMethod.Invoke(switcher, new object[] { i });
                    }
                    catch
                    {
                        skinColor = null;
                    }
                }

                if (skinColor == null)
                {
                    skinColor = skinColors.GetValue(i);
                }

                object value = SkinColorBaseColorField.GetValue(skinColor);
                if (!(value is Color))
                {
                    continue;
                }

                Color candidate = (Color)value;
                float distance = ColorDistanceSquared(candidate, target);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }
            }

            if (skinColors.Length > 1 && bestIndex == originalIndex)
            {
                bestIndex = Math.Abs(teamIndex + 1) % skinColors.Length;
                if (bestIndex == originalIndex)
                {
                    bestIndex = (bestIndex + 1) % skinColors.Length;
                }
            }

            return bestIndex;
        }

        private static float ColorDistanceSquared(Color left, Color right)
        {
            float dr = left.r - right.r;
            float dg = left.g - right.g;
            float db = left.b - right.b;
            return dr * dr + dg * dg + db * db;
        }

        private sealed class TeamSnapshot
        {
            public readonly string Name;
            public readonly Color Color;
            public int PlayerCount;
            public int CourseScore;
            public int MatchScore;
            public int Strokes;

            public TeamSnapshot(string name, Color color)
            {
                Name = name;
                Color = color;
            }
        }

        private struct TeamHelloMessage : NetworkMessage
        {
            public ulong playerGuid;
            public string version;
        }

        private struct TeamSyncMessage : NetworkMessage
        {
            public int revision;
            public string payload;
        }
    }

    [HarmonyPatch(typeof(BNetworkManager), "OnStartServer")]
    internal static class BNetworkManagerOnStartServerPatch
    {
        private static void Postfix()
        {
            if (Plugin.Instance != null)
            {
                Plugin.Instance.RegisterNetworkHandlers(true);
                Plugin.Instance.ClearModdedClientConnections();
            }
        }
    }

    [HarmonyPatch(typeof(BNetworkManager), "OnStartClient")]
    internal static class BNetworkManagerOnStartClientPatch
    {
        private static void Postfix()
        {
            if (Plugin.Instance != null)
            {
                Plugin.Instance.RegisterNetworkHandlers(true);
            }
        }
    }

    [HarmonyPatch(typeof(BNetworkManager), "OnStopServer")]
    internal static class BNetworkManagerOnStopServerPatch
    {
        private static void Postfix()
        {
            if (Plugin.Instance != null)
            {
                Plugin.Instance.ClearModdedClientConnections();
            }
        }
    }

    [HarmonyPatch(typeof(BNetworkManager), "OnServerDisconnect")]
    internal static class BNetworkManagerOnServerDisconnectPatch
    {
        private static void Prefix(NetworkConnectionToClient connection)
        {
            if (Plugin.Instance != null)
            {
                Plugin.Instance.RemoveModdedClientConnection(connection);
            }
        }
    }

    [HarmonyPatch(typeof(MatchSetupMenu), "Start")]
    internal static class MatchSetupMenuStartPatch
    {
        private static void Postfix(MatchSetupMenu __instance)
        {
            if (Plugin.Instance != null)
            {
                Plugin.Instance.TrySetupModeDropdown(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(MatchSetupPlayer), "AssignPlayer")]
    internal static class MatchSetupPlayerAssignPlayerPatch
    {
        private static void Postfix(MatchSetupPlayer __instance)
        {
            if (Plugin.Instance != null)
            {
                Plugin.Instance.TryMakeSetupPlayerDraggable(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(DropdownOption), "SetOptions")]
    internal static class DropdownOptionSetOptionsPatch
    {
        private static void Postfix(DropdownOption __instance)
        {
            if (Plugin.Instance != null)
            {
                Plugin.Instance.TryPatchModeDropdownOption(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(DropdownOption), "OnValueChanged")]
    internal static class DropdownOptionOnValueChangedPatch
    {
        private static void Postfix(DropdownOption __instance)
        {
            if (Plugin.Instance != null)
            {
                Plugin.Instance.HandleModeDropdownChanged(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(ReorderableList), "AssignElement")]
    internal static class ReorderableListAssignElementPatch
    {
        private static void Postfix(ReorderableList __instance, ReorderableListElement element)
        {
            if (Plugin.Instance != null)
            {
                Plugin.Instance.TryHandleAssignedElement(__instance, element);
            }
        }
    }

    [HarmonyPatch(typeof(ReorderableListElement), "OnPointerUp")]
    internal static class ReorderableListElementOnPointerUpPatch
    {
        private static void Postfix()
        {
            if (Plugin.Instance != null)
            {
                Plugin.Instance.CommitPendingSetupDrop();
            }
        }
    }

    [HarmonyPatch(typeof(ReorderableList), "RefreshList")]
    internal static class ReorderableListRefreshListPatch
    {
        private static void Prefix()
        {
            if (Plugin.Instance != null)
            {
                Plugin.Instance.TryForceCurrentTeamDropTarget();
            }
        }
    }

    [HarmonyPatch(typeof(TMP_Dropdown), "Show")]
    internal static class TmpDropdownShowPatch
    {
        private static void Prefix(TMP_Dropdown __instance)
        {
            if (Plugin.Instance != null)
            {
                Plugin.Instance.TryPatchModeDropdown(__instance, "TMP_Dropdown.Show");
            }
        }
    }

    [HarmonyPatch(typeof(NameTagUi), "LateUpdate")]
    internal static class NameTagColorPatch
    {
        private static readonly FieldInfo PlayerInfoField = typeof(NameTagUi).GetField("playerInfo", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo TagField = typeof(NameTagUi).GetField("tag", BindingFlags.Instance | BindingFlags.NonPublic);

        private static void Postfix(NameTagUi __instance)
        {
            Plugin plugin = Plugin.Instance;
            if (plugin == null || !plugin.ShouldColorNametags)
            {
                return;
            }

            PlayerInfo playerInfo = PlayerInfoField == null ? null : PlayerInfoField.GetValue(__instance) as PlayerInfo;
            TMP_Text tag = TagField == null ? null : TagField.GetValue(__instance) as TMP_Text;
            ulong guid = Plugin.GetGuidFromObject(playerInfo);
            if (guid == 0UL || tag == null)
            {
                return;
            }

            tag.color = plugin.GetTeamColor(guid);
        }
    }

    [HarmonyPatch(typeof(PlayerCosmeticsSwitcher), "SetSkinColor")]
    internal static class PlayerSkinColorPatch
    {
        private static void Postfix(PlayerCosmeticsSwitcher __instance)
        {
            if (Plugin.Instance != null)
            {
                Plugin.Instance.TryApplyTeamSkinColor(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(ScoreboardEntry), "PopulateWith")]
    internal static class ScoreboardEntryPopulatePatch
    {
        internal static readonly Dictionary<GameObject, ulong> EntryGuids = new Dictionary<GameObject, ulong>();

        private static void Postfix(ScoreboardEntry __instance, CourseManager.PlayerState playerState)
        {
            Plugin plugin = Plugin.Instance;
            if (__instance != null && __instance.gameObject != null)
            {
                EntryGuids[__instance.gameObject] = playerState.playerGuid;
            }

            if (plugin == null || !plugin.ShouldColorScoreboard || playerState.playerGuid == 0UL)
            {
                return;
            }

            Color color = plugin.GetTeamColor(playerState.playerGuid);
            string prefix = "[" + plugin.GetTeamName(playerState.playerGuid) + "] ";

            FieldInfo nameField = typeof(ScoreboardEntry).GetField("name", BindingFlags.Instance | BindingFlags.NonPublic);
            TMP_Text name = nameField == null ? null : nameField.GetValue(__instance) as TMP_Text;
            if (name != null)
            {
                if (!name.text.StartsWith(prefix))
                {
                    name.text = prefix + name.text;
                }

                name.color = color;
            }

            Plugin.TryTintImages(__instance.gameObject, color, 0.22f);
        }
    }

    [HarmonyPatch(typeof(Scoreboard), "Refresh")]
    internal static class ScoreboardRefreshPatch
    {
        private static readonly FieldInfo EntryParentField = typeof(Scoreboard).GetField("entryParent", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RankingField = typeof(ScoreboardEntry).GetField("ranking", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo NameField = typeof(ScoreboardEntry).GetField("name", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo CourseScoreField = typeof(ScoreboardEntry).GetField("courseScore", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo MatchScoreField = typeof(ScoreboardEntry).GetField("matchScore", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo StrokesField = typeof(ScoreboardEntry).GetField("strokes", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo KnockoutsField = typeof(ScoreboardEntry).GetField("knockouts", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo WinsField = typeof(ScoreboardEntry).GetField("wins", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FinishesField = typeof(ScoreboardEntry).GetField("finishes", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PingField = typeof(ScoreboardEntry).GetField("ping", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PingIconField = typeof(ScoreboardEntry).GetField("pingIcon", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PlayerIconField = typeof(ScoreboardEntry).GetField("playerIcon", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly List<GameObject> TeamHeaders = new List<GameObject>();
        private static readonly Dictionary<ulong, int> LastMatchScores = new Dictionary<ulong, int>();
        private static readonly Dictionary<ulong, int> TotalMatchScores = new Dictionary<ulong, int>();

        private static void Prefix()
        {
            ClearHeaders();
        }

        private static void Postfix(Scoreboard __instance)
        {
            Plugin plugin = Plugin.Instance;
            if (plugin == null || !plugin.ShouldUseNativeScoreboardHeaders)
            {
                return;
            }

            Transform entryParent = EntryParentField == null ? null : EntryParentField.GetValue(__instance) as Transform;
            if (entryParent == null)
            {
                return;
            }

            Dictionary<int, List<GameObject>> rowsByTeam = new Dictionary<int, List<GameObject>>();
            Dictionary<int, TeamTotals> totalsByTeam = new Dictionary<int, TeamTotals>();
            Dictionary<int, Color> colorByTeam = new Dictionary<int, Color>();
            Dictionary<int, string> nameByTeam = new Dictionary<int, string>();

            for (int i = 0; i < entryParent.childCount; i++)
            {
                GameObject row = entryParent.GetChild(i).gameObject;
                if (row == null || !row.activeSelf)
                {
                    continue;
                }

                ulong guid;
                if (!ScoreboardEntryPopulatePatch.EntryGuids.TryGetValue(row, out guid) || guid == 0UL)
                {
                    continue;
                }

                CourseManager.PlayerState state;
                if (!plugin.TryGetPlayerState(guid, out state))
                {
                    continue;
                }

                int team = plugin.GetTeamIndex(guid);
                if (!rowsByTeam.ContainsKey(team))
                {
                    rowsByTeam[team] = new List<GameObject>();
                    totalsByTeam[team] = new TeamTotals();
                    colorByTeam[team] = plugin.GetTeamColor(guid);
                    nameByTeam[team] = plugin.GetTeamName(guid);
                }

                rowsByTeam[team].Add(row);
                TeamTotals totals = totalsByTeam[team];
                totals.CourseScore += state.courseScore;
                totals.MatchScore += state.matchScore;
                totals.TotalScore += GetTrackedTotalScore(state.playerGuid, state.matchScore);
                totals.Strokes += state.matchStrokes;
                totals.Knockouts += state.matchKnockouts;
                totals.Wins += state.wins;
                totals.Finishes += state.finishes;
                totals.PlayerCount++;
                totalsByTeam[team] = totals;
            }

            List<int> teams = new List<int>(rowsByTeam.Keys);
            teams.Sort(delegate(int left, int right)
            {
                int score = totalsByTeam[right].TotalScore.CompareTo(totalsByTeam[left].TotalScore);
                return score != 0 ? score : left.CompareTo(right);
            });

            int sibling = 0;
            for (int i = 0; i < teams.Count; i++)
            {
                int team = teams[i];
                List<GameObject> rows = rowsByTeam[team];
                if (rows.Count == 0)
                {
                    continue;
                }

                GameObject header = UnityEngine.Object.Instantiate(rows[0], entryParent);
                header.name = "ModernTeamMode_Header_" + team;
                TeamHeaders.Add(header);
                SetupHeader(header, nameByTeam[team], totalsByTeam[team], colorByTeam[team]);
                header.transform.SetSiblingIndex(sibling++);

                for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                {
                    rows[rowIndex].transform.SetSiblingIndex(sibling++);
                    Plugin.TryTintImages(rows[rowIndex], colorByTeam[team], 0.22f);
                }
            }
        }

        private static void SetupHeader(GameObject header, string teamName, TeamTotals totals, Color color)
        {
            string displayName = Plugin.FormatTeamDisplayName(teamName);
            Graphic[] graphics = header.GetComponentsInChildren<Graphic>(true);
            for (int i = 0; i < graphics.Length; i++)
            {
                graphics[i].color = i == 0 ? new Color(color.r, color.g, color.b, 0.34f) : new Color(color.r, color.g, color.b, 0.12f);
            }

            ScoreboardEntry entry = header.GetComponent<ScoreboardEntry>();
            if (entry == null)
            {
                return;
            }

            SetText(entry, RankingField, "");
            SetText(entry, NameField, displayName, color, true);
            SetText(entry, CourseScoreField, totals.TotalScore.ToString(), color, true);
            SetText(entry, MatchScoreField, "");
            SetText(entry, StrokesField, totals.Strokes.ToString(), color, true);
            SetText(entry, KnockoutsField, totals.Knockouts.ToString(), color, true);
            SetText(entry, WinsField, totals.Wins.ToString(), color, true);
            SetText(entry, FinishesField, totals.Finishes.ToString(), color, true);
            SetText(entry, PingField, "");
            Graphic pingIcon = PingIconField == null ? null : PingIconField.GetValue(entry) as Graphic;
            if (pingIcon != null)
            {
                pingIcon.enabled = false;
            }

            Image playerIcon = PlayerIconField == null ? null : PlayerIconField.GetValue(entry) as Image;
            if (playerIcon != null)
            {
                playerIcon.color = color;
            }
        }

        private static void SetText(ScoreboardEntry entry, FieldInfo field, string value)
        {
            SetText(entry, field, value, Color.white, false);
        }

        private static void SetText(ScoreboardEntry entry, FieldInfo field, string value, Color color, bool bold)
        {
            TMP_Text text = field == null ? null : field.GetValue(entry) as TMP_Text;
            if (text == null)
            {
                return;
            }

            text.text = bold ? "<b>" + value + "</b>" : value;
            text.color = color;
        }

        private static int GetTrackedTotalScore(ulong playerGuid, int currentMatchScore)
        {
            if (playerGuid == 0UL)
            {
                return currentMatchScore;
            }

            int lastScore;
            int totalScore;
            if (!LastMatchScores.TryGetValue(playerGuid, out lastScore))
            {
                LastMatchScores[playerGuid] = currentMatchScore;
                TotalMatchScores[playerGuid] = currentMatchScore;
                return currentMatchScore;
            }

            TotalMatchScores.TryGetValue(playerGuid, out totalScore);
            if (currentMatchScore >= lastScore)
            {
                totalScore += currentMatchScore - lastScore;
            }
            else
            {
                totalScore += currentMatchScore;
            }

            LastMatchScores[playerGuid] = currentMatchScore;
            TotalMatchScores[playerGuid] = totalScore;
            return totalScore;
        }

        private struct TeamTotals
        {
            public int CourseScore;
            public int MatchScore;
            public int TotalScore;
            public int Strokes;
            public int Knockouts;
            public int Wins;
            public int Finishes;
            public int PlayerCount;
        }

        private static void ClearHeaders()
        {
            for (int i = 0; i < TeamHeaders.Count; i++)
            {
                if (TeamHeaders[i] != null)
                {
                    UnityEngine.Object.Destroy(TeamHeaders[i]);
                }
            }

            TeamHeaders.Clear();
        }
    }

    [HarmonyPatch(typeof(Hittable), "IsHittableBySwing")]
    internal static class IsHittableBySwingPatch
    {
        private static void Postfix(Hittable __instance, PlayerGolfer hitter, ref bool __result)
        {
            Plugin plugin = Plugin.Instance;
            if (__result && plugin != null && plugin.ShouldBlockHit(__instance, hitter))
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(Hittable), "HitWithGolfSwing")]
    internal static class HitWithGolfSwingPatch
    {
        private static bool Prefix(Hittable __instance, PlayerGolfer hitter)
        {
            Plugin plugin = Plugin.Instance;
            return plugin == null || !plugin.ShouldBlockHit(__instance, hitter);
        }
    }

    [HarmonyPatch(typeof(Hittable), "HitWithSwingProjectile")]
    internal static class HitWithSwingProjectilePatch
    {
        private static bool Prefix(Hittable __instance, PlayerGolfer responsiblePlayer)
        {
            Plugin plugin = Plugin.Instance;
            return plugin == null || !plugin.ShouldBlockHit(__instance, responsiblePlayer);
        }
    }

    [HarmonyPatch(typeof(Hittable), "HitWithDive")]
    internal static class HitWithDivePatch
    {
        private static bool Prefix(Hittable __instance, PlayerMovement hitter)
        {
            Plugin plugin = Plugin.Instance;
            return plugin == null || !plugin.ShouldBlockHit(__instance, hitter);
        }
    }

    [HarmonyPatch(typeof(Hittable), "HitWithItem")]
    internal static class HitWithItemPatch
    {
        private static bool Prefix(Hittable __instance, PlayerInventory itemUser)
        {
            Plugin plugin = Plugin.Instance;
            return plugin == null || !plugin.ShouldBlockHit(__instance, itemUser);
        }
    }

    [HarmonyPatch(typeof(Hittable), "HitWithRocketLauncherBackBlast")]
    internal static class HitWithRocketLauncherBackBlastPatch
    {
        private static bool Prefix(Hittable __instance, PlayerInventory rocketLauncherUser)
        {
            Plugin plugin = Plugin.Instance;
            return plugin == null || !plugin.ShouldBlockHit(__instance, rocketLauncherUser);
        }
    }

    [HarmonyPatch(typeof(Hittable), "HitWithRocketDriverSwingPostHitSpin")]
    internal static class HitWithRocketDriverSwingPostHitSpinPatch
    {
        private static bool Prefix(Hittable __instance, PlayerGolfer hitter)
        {
            Plugin plugin = Plugin.Instance;
            return plugin == null || !plugin.ShouldBlockHit(__instance, hitter);
        }
    }
}
