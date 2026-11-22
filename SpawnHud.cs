using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using PanoramaManager;

namespace SpawnProtection;

/// <summary>
/// The spawn protection pill: the private panel under the crosshair that replaces the center HTML
/// "YOU ARE SPAWN PROTECTED / N SECONDS LEFT" and its 12-segment text bar. The layout is
/// hud/panorama/layout/custom_game/spawnprotect_hud.xml, shipped in the workshop addon.
///
/// <para><b>One layout, opened once per player and left open.</b> Its root is an invisible full-screen
/// panel; the pill inside carries its own "show". The pill is "hidden" (collapsed) while not in use and
/// un-collapsed a moment before it shows, so it always has a resting place to slide in from.</para>
///
/// <para><b>Everything is drawn from state.</b> PanoramaManager rebuilds a player's panel by itself after
/// every respawn, a round restart or a map change, and drops every per-viewer variable and class on the
/// way (PanelAction.Restored). Each viewer's pill is a plain record here and <see cref="Restore"/> writes
/// it back. Every timed step (the entry, the bar's next step, the end of the red line) is a time in that
/// record, run by <see cref="Tick"/>, so nothing is left behind in a timer closure.</para>
///
/// <para><b>Held, then draining.</b> The plugin starts its countdown only after freeze time, so a spawn
/// shows the pill with a full, still bar (<see cref="Protect"/>) and the drain begins when the plugin's
/// timer does (<see cref="Drain"/>).</para>
///
/// <para><b>The bar is a clip ladder with one fixed glide.</b> spawnprotect_hud.css carries s0..s50 (2% a
/// step) on a clip with a 0.3s linear transition that no class changes, and <see cref="TickBar"/> names the
/// step for where the protection will be one glide from now, every <see cref="BarEverySeconds"/>. It
/// replaced one long transition (snap to at-N, then dur-N and "run" 0.35s later), which did not draw in
/// game: nothing has shown a class-swapped transition-duration working there.</para>
///
/// <para>This class only draws. Who is protected, and for how long, is the plugin's call.</para>
/// </summary>
internal sealed class SpawnHud : IDisposable
{
    private const string Layout = "panorama/layout/custom_game/spawnprotect_hud.vxml_c";

    private const string PillId = "sp_pill";
    private const string BarId = "sp_bar";

    /// <summary>Un-collapse first, then "show": a transition needs the client to have seen the pill at
    /// its resting offset (ToastService found one server frame is not enough).</summary>
    private const float EntrySeconds = 0.06f;

    /// <summary>The exit transition, before the pill collapses.</summary>
    private const float ExitSeconds = 0.35f;

    /// <summary>How long the red "Protection ended" line stays.</summary>
    private const float EndedSeconds = 1.5f;

    /// <summary>s0..s50 in spawnprotect_hud.css: 2% a step, s50 full.</summary>
    private const int BarSteps = 50;

    /// <summary>
    /// The .hn-bar transition-duration in spawnprotect_hud.css. Every step is aimed this far ahead of the
    /// clock, so the fill arrives where the protection is instead of trailing it, and reaches s0 as it ends.
    /// </summary>
    private const float GlideSeconds = 0.3f;

    /// <summary>How often the bar gets its next step. Under <see cref="GlideSeconds"/>, so the fill never
    /// stops between steps; Tick runs every 0.1s.</summary>
    private const float BarEverySeconds = 0.2f;

    private enum Kind { None, Protected, Ended }

    private static readonly string[] States = ["live", "ended"];

    private sealed class Viewer
    {
        public Kind Kind;

        /// <summary>"show" is on.</summary>
        public bool Shown;

        /// <summary>When to write the pill and turn "show" on; -1 when nothing is pending.</summary>
        public float ShowAt = -1;

        /// <summary>When to collapse a pill that has finished leaving; -1 when nothing is pending.</summary>
        public float CollapseAt = -1;

        /// <summary>Ended: when the pill goes.</summary>
        public float Until;

        // protected
        /// <summary>The seconds shown while the bar is held (freeze time).</summary>
        public float HeldSeconds;

        /// <summary>The plugin's countdown is running: <see cref="Deadline"/> and <see cref="Total"/> are set.</summary>
        public bool Draining;
        public float Deadline;
        public float Total = 1;
        public int Number = -1;

        // ended
        public string EndSub = "";

        // bar: the s- step on sp_bar as last written (-1 = none), so the next write can take it off again
        public int BarStep = -1;

        /// <summary>When the bar may get its next step.</summary>
        public float BarNextAt;

        /// <summary>The next step is written even when it has not moved: after a rebuild the client may not
        /// have it. On an entity that kept it, the write changes nothing.</summary>
        public bool BarResend;
    }

    private readonly BasePlugin _plugin;
    private readonly Dictionary<int, Viewer> _viewers = [];
    private PanelHandle? _panel;

    public SpawnHud(BasePlugin plugin)
    {
        _plugin = plugin;
    }

    public bool Enabled => _panel != null;

    /// <summary>Spawns the layout once. A second Spawn would orphan the first entity, so this is guarded.</summary>
    public void Init()
    {
        if (_panel != null)
            return;

        try
        {
            Panorama.Init(_plugin);
            _panel = Panorama.Spawn(Layout, new LayoutContract
            {
                // Unique server-wide: dialog variables are keyed by panel id
                RootPanelId = "SpawnProtRoot",
                RevealClass = "show",
                RowCount = 0,
                CaptureInput = false,
                HideHud = HideHudFlags.None,
                SharedText = false,
            });
            _panel.OnEvent += OnPanelEvent;
            _plugin.Logger.LogInformation("Spawn protection HUD ready");
        }
        catch (Exception ex)
        {
            // The plugin works without its HUD: the caller falls back to the center text when Enabled is false
            _plugin.Logger.LogWarning("Spawn protection HUD failed to start, using the center text instead: {Message}", ex.Message);
            _panel = null;
        }
    }

    public void Dispose()
    {
        _panel?.Dispose();
        _panel = null;
        _viewers.Clear();
    }

    private void OnPanelEvent(PanelEvent e)
    {
        if (e.Action == PanelAction.Restored && e.Player.IsValid)
            Restore(e.Player);
    }

    // ------------------------------------------------------------------ viewers

    private static bool IsHuman(CCSPlayerController? player) =>
        player is { IsValid: true, IsBot: false, IsHLTV: false } && player.Connected == PlayerConnectedState.Connected;

    private Viewer ViewerOf(CCSPlayerController player)
    {
        if (!_viewers.TryGetValue(player.Slot, out var viewer))
        {
            viewer = new Viewer();
            _viewers[player.Slot] = viewer;
        }

        return viewer;
    }

    /// <summary>Opens the player's session if it is not open yet, which also redraws what they should see.</summary>
    private bool EnsureOpen(CCSPlayerController player)
    {
        if (_panel == null || !IsHuman(player))
            return false;

        if (!_panel.IsOpenFor(player))
        {
            _panel.Open(player);
            // Open takes off every per-viewer class this handle set for the slot, the bar step included
            ViewerOf(player).BarStep = -1;
            Restore(player);
        }

        return _panel.IsOpenFor(player);
    }

    /// <summary>The pill is showing this player's protection, so the center text must not.</summary>
    public bool IsShowing(int slot) => _viewers.TryGetValue(slot, out var viewer) && viewer.Kind == Kind.Protected;

    public void OnDisconnect(int slot)
    {
        _viewers.Remove(slot);
        _panel?.Close(slot);
    }

    /// <summary>Map start: the clock restarts on the new map, so every stored time is meaningless now.</summary>
    public void Clear()
    {
        foreach (var viewer in _viewers.Values)
        {
            viewer.Kind = Kind.None;
            viewer.Shown = false;
            viewer.ShowAt = -1;
            viewer.CollapseAt = -1;
            viewer.Draining = false;
            viewer.BarNextAt = 0;
            viewer.BarResend = true;
        }
    }

    /// <summary>
    /// Writes the pill back for one player, from state. Called on open and after the library rebuilt the
    /// panel. Every class it may have lost is written again; the bar's step is sent again on the next draw.
    /// </summary>
    public void Restore(CCSPlayerController player)
    {
        if (_panel == null || !_panel.IsOpenFor(player))
            return;

        var viewer = ViewerOf(player);
        var now = Server.CurrentTime;

        if (viewer.Kind == Kind.Ended && now >= viewer.Until)
            viewer.Kind = Kind.None;

        viewer.Shown = false;
        viewer.Number = -1;
        // The step record is NOT reset: a kept entity still wears it, and the next write takes exactly that
        // off. On a rebuilt one the removal is a no-op. The step itself is sent again either way.
        viewer.BarNextAt = 0;
        viewer.BarResend = true;
        Cls(player, PillId, "show", false);

        if (viewer.Kind == Kind.None)
        {
            viewer.ShowAt = -1;
            viewer.CollapseAt = -1;
            Cls(player, PillId, "hidden", true);
            return;
        }

        Cls(player, PillId, "hidden", false);
        viewer.CollapseAt = -1;
        viewer.ShowAt = now + EntrySeconds;
    }

    // ------------------------------------------------------------------ what the plugin calls

    /// <summary>
    /// The player spawned protected: the pill comes in with a full, still bar and <paramref name="seconds"/>
    /// on it, and stays like that until <see cref="Drain"/>. Returns false when the pill cannot be shown to
    /// this player, so the caller keeps the center text.
    /// </summary>
    public bool Protect(CCSPlayerController player, float seconds)
    {
        if (!EnsureOpen(player))
            return false;

        var viewer = ViewerOf(player);
        var now = Server.CurrentTime;

        viewer.Kind = Kind.Protected;
        viewer.HeldSeconds = Math.Max(seconds, 0.1f);
        viewer.Draining = false;
        viewer.Number = -1;

        // A bar left draining or emptied by the last spawn glides back to full first
        BarTo(player, viewer, 1f);

        Cls(player, PillId, "hidden", false);
        viewer.CollapseAt = -1;

        if (viewer.Shown)
            Draw(player, viewer);
        else
            viewer.ShowAt = now + EntrySeconds;

        return true;
    }

    /// <summary>
    /// The plugin's countdown started: the bar drains over <paramref name="seconds"/> from now.
    /// <paramref name="total"/> is the whole protection time, what a full bar stands for, so a countdown
    /// the plugin restarts partway starts from a part-full bar.
    /// </summary>
    public void Drain(CCSPlayerController player, float seconds, float total)
    {
        if (_panel == null || !_viewers.TryGetValue(player.Slot, out var viewer) || viewer.Kind != Kind.Protected)
            return;

        var deadline = Server.CurrentTime + Math.Max(seconds, 0.1f);

        // The same countdown again: nothing changes. One the plugin restarted needs nothing more either: the
        // bar's next step is computed from the new deadline, and the glide takes it there.
        if (viewer.Draining && Math.Abs(viewer.Deadline - deadline) <= 0.5f)
            return;

        viewer.Draining = true;
        viewer.Deadline = deadline;
        viewer.Total = Math.Max(Math.Max(total, seconds), 0.1f);
        viewer.BarNextAt = 0;
    }

    /// <summary>
    /// The protection ran out or was stopped: the pill turns into the red "Protection ended" line with
    /// <paramref name="sub"/> under it for a moment, then goes. Nothing happens when no pill is up.
    /// </summary>
    public void Ended(CCSPlayerController player, string sub)
    {
        if (_panel == null || !_viewers.TryGetValue(player.Slot, out var viewer) || viewer.Kind != Kind.Protected)
            return;

        viewer.Kind = Kind.Ended;
        viewer.EndSub = sub;
        viewer.Until = (viewer.ShowAt >= 0 ? viewer.ShowAt : Server.CurrentTime) + EndedSeconds;

        // In place, like the preview. A pill still waiting for its entry is written when it shows.
        if (viewer.Shown && _panel.IsOpenFor(player))
            Draw(player, viewer);
    }

    /// <summary>Takes the pill away with no red line (death, round end, a quiet stop).</summary>
    public void Hide(CCSPlayerController player)
    {
        if (_viewers.TryGetValue(player.Slot, out var viewer) && viewer.Kind != Kind.None)
            Leave(player, viewer, Server.CurrentTime);
    }

    /// <summary>Round end: every pill goes quietly, the way the center text stopped.</summary>
    public void HideAll()
    {
        if (_panel == null)
            return;

        var now = Server.CurrentTime;
        foreach (var (slot, viewer) in _viewers)
        {
            if (viewer.Kind == Kind.None)
                continue;

            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is { IsValid: true })
                Leave(player, viewer, now);
            else
                viewer.Kind = Kind.None;
        }
    }

    private void Leave(CCSPlayerController player, Viewer viewer, float now)
    {
        viewer.Kind = Kind.None;
        viewer.Draining = false;
        viewer.ShowAt = -1;

        if (_panel == null || !_panel.IsOpenFor(player))
        {
            viewer.Shown = false;
            return;
        }

        if (!viewer.Shown)
        {
            // Never came in: nothing to animate
            Cls(player, PillId, "hidden", true);
            viewer.CollapseAt = -1;
            return;
        }

        // The content stays as it is, so the pill fades out showing what it showed
        Cls(player, PillId, "show", false);
        viewer.Shown = false;
        viewer.CollapseAt = now + ExitSeconds;
    }

    // ------------------------------------------------------------------ the clock

    /// <summary>Runs every timed step. The plugin calls this ten times a second while the HUD is on.</summary>
    public void Tick()
    {
        if (_panel == null || _viewers.Count == 0)
            return;

        var now = Server.CurrentTime;

        foreach (var (slot, viewer) in _viewers)
        {
            if (viewer.Kind == Kind.None && viewer.CollapseAt < 0)
                continue;

            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is not { IsValid: true } || !_panel.IsOpenFor(player))
                continue;

            if (viewer.CollapseAt >= 0 && now >= viewer.CollapseAt)
            {
                viewer.CollapseAt = -1;
                if (viewer.Kind == Kind.None)
                {
                    Cls(player, PillId, "hidden", true);
                    // Out of sight now, so the bar can glide back to full for the next spawn
                    BarTo(player, viewer, 1f);
                }
            }

            if (viewer.ShowAt >= 0 && now >= viewer.ShowAt && viewer.Kind != Kind.None)
            {
                viewer.ShowAt = -1;
                Draw(player, viewer);
                Cls(player, PillId, "show", true);
                viewer.Shown = true;
            }

            switch (viewer.Kind)
            {
                case Kind.Protected:
                    if (viewer.Shown)
                        TickProtected(player, viewer, now);
                    break;

                case Kind.Ended:
                    if (now >= viewer.Until && viewer.ShowAt < 0)
                        Leave(player, viewer, now);
                    break;
            }
        }
    }

    private void TickProtected(CCSPlayerController player, Viewer viewer, float now)
    {
        // Until the plugin's timer starts, the seconds it will count from. Never 0 while still protected:
        // the plugin stops on its own 0.1s timer, a moment after the deadline.
        var seconds = viewer.Draining ? viewer.Deadline - now : viewer.HeldSeconds;
        var number = Math.Max(1, (int)Math.Ceiling(seconds - 0.05f));

        if (number != viewer.Number)
        {
            viewer.Number = number;
            Set(player, "sp_n", number.ToString());
        }

        // Held: the bar sits full and still. Draining: the next step.
        if (!viewer.Draining)
            BarTo(player, viewer, 1f);
        else
            TickBar(player, viewer, now);
    }

    // ------------------------------------------------------------------ the bar

    /// <summary>
    /// The drain: every <see cref="BarEverySeconds"/>, the step for where the protection will be one glide
    /// from now. The glide outlasts the gap between steps, so the fill never stops, and the last steps
    /// before the deadline are already s0. A drain picked up halfway (a respawn redraw) needs nothing
    /// special: its first step is where it should be, and the glide takes the fill there.
    /// </summary>
    private void TickBar(CCSPlayerController player, Viewer viewer, float now)
    {
        if (now < viewer.BarNextAt && !viewer.BarResend)
            return;

        viewer.BarNextAt = now + BarEverySeconds;
        BarTo(player, viewer, (viewer.Deadline - now - GlideSeconds) / viewer.Total);
    }

    /// <summary>
    /// Turns on the s- step for <paramref name="fraction"/> (1 = full) and the previous one off. Only a change
    /// is written, unless <see cref="Viewer.BarResend"/> asks for the step again. Both writes land in one
    /// update, so the client never sees two steps on, which would paint whichever the stylesheet lists last.
    /// </summary>
    private void BarTo(CCSPlayerController player, Viewer viewer, float fraction)
    {
        var step = Math.Clamp((int)Math.Round(fraction * BarSteps, MidpointRounding.AwayFromZero), 0, BarSteps);
        var resend = viewer.BarResend;
        viewer.BarResend = false;

        if (viewer.BarStep == step)
        {
            if (resend)
                Cls(player, BarId, $"s{step}", true);
            return;
        }

        if (viewer.BarStep >= 0)
            Cls(player, BarId, $"s{viewer.BarStep}", false);
        Cls(player, BarId, $"s{step}", true);
        viewer.BarStep = step;
    }

    // ------------------------------------------------------------------ drawing

    /// <summary>Writes the pill's text and state for what the viewer holds now.</summary>
    private void Draw(CCSPlayerController player, Viewer viewer)
    {
        switch (viewer.Kind)
        {
            case Kind.Protected:
                Set(player, "sp_label", Text("hud.protected.label", "Spawn protected"));
                Set(player, "sp_sub", Text("hud.protected.sub", "You can't be damaged"));
                OneOf(player, PillId, "st-", "live", States);
                // The number and the bar come from the clock, at once
                viewer.Number = -1;
                TickProtected(player, viewer, Server.CurrentTime);
                break;

            case Kind.Ended:
                Set(player, "sp_label", Text("hud.ended.label", "Protection ended"));
                Set(player, "sp_sub", viewer.EndSub);
                OneOf(player, PillId, "st-", "ended", States);
                break;
        }
    }

    private void Set(CCSPlayerController player, string name, string value) => _panel!.SetVariableFor(player, name, value);

    private void Cls(CCSPlayerController player, string id, string cls, bool on) => _panel!.SetClassFor(player, id, cls, on);

    /// <summary>Exactly one class of a group on.</summary>
    private void OneOf(CCSPlayerController player, string id, string prefix, string value, string[] all)
    {
        foreach (var candidate in all)
            Cls(player, id, prefix + candidate, candidate == value);
    }

    /// <summary>A phrase from lang/, or the English fallback when the server's lang files don't have it yet.</summary>
    public string Text(string key, string fallback)
    {
        var text = _plugin.Localizer[key];
        return text.ResourceNotFound ? fallback : text.Value;
    }
}
