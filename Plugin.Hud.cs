namespace SpawnProtection
{
	using System.Text.Json.Serialization;
	using CounterStrikeSharp.API;
	using CounterStrikeSharp.API.Core;
	using CounterStrikeSharp.API.Modules.Timers;

	/// <summary>"Hud" in SpawnProtection.json. Optional: a config without it gets these defaults.</summary>
	public sealed class HudConfig
	{
		/// <summary>The spawn protection pill under the crosshair. False brings the old center text back unchanged.</summary>
		[JsonPropertyName("Enabled")]
		public bool Enabled { get; set; } = true;
	}

	/// <summary>How a protection came to an end, for the pill's last line.</summary>
	public enum ProtectionEnd
	{
		/// <summary>No red line: the pill just goes (death, round end, a reload).</summary>
		Quiet,

		/// <summary>The protection time ran out.</summary>
		Expired,

		/// <summary>stop-on-weapon-fire.</summary>
		Fired,

		/// <summary>stop-on-player-move.</summary>
		Moved,
	}

	/// <summary>
	/// The HUD side of the plugin: hands the protection state to <see cref="SpawnHud"/>, which only draws.
	/// The timing is the plugin's own and unchanged: the pill shows at spawn with a full bar, the bar
	/// drains when CreateProtectionTimer starts (after freeze time), and the pill turns into the red
	/// "Protection ended" line when StopSpawnProtection runs.
	///
	/// <para><b>Why the old "PROTECTION ENDED" line never showed:</b> the center text is printed from the
	/// OnTick loop over protectedPlayers, and StopSpawnProtection takes the player out of that set on the
	/// timer tick that ends the protection, so the ended branch only runs in the last 0.1s before it - and
	/// not at all when protection stops early (fire, move). spawn-prot-end-announce was never read. The
	/// pill keeps the line for 1.5s on its own clock, and spawn-prot-end-announce now switches it.</para>
	/// </summary>
	public sealed partial class SpawnProt
	{
		private SpawnHud? _hud;

		private bool HudOn => _hud is { Enabled: true };

		private void HudLoad()
		{
			// Nothing to draw when the timer display is off
			if (!Config.Hud.Enabled || !Config.CenterHtmlMessage)
				return;

			_hud = new SpawnHud(this);
			_hud.Init();

			if (_hud.Enabled)
				AddTimer(0.1f, () => _hud?.Tick(), TimerFlags.REPEAT);
		}

		public override void Unload(bool hotReload)
		{
			_hud?.Dispose();
			_hud = null;
		}

		/// <summary>The pill is up for this player, so the center text stays quiet.</summary>
		private bool HudShows(CCSPlayerController player) => HudOn && _hud!.IsShowing(player.Slot);

		/// <summary>At spawn: the pill with a full, still bar. Same gate as the center text's OnTick loop.</summary>
		private void HudProtect(CCSPlayerController player, PlayerState state)
		{
			if (!HudOn)
				return;

			if (Config.DisableDuringWarmup && (IsWarmup || gameRules is null))
				return;

			_hud!.Protect(player, state.ProtectionTimer);
		}

		/// <summary>CreateProtectionTimer started counting: the bar drains over what is left.</summary>
		private void HudDrain(CCSPlayerController player, PlayerState state)
		{
			if (HudOn && state.ProtectionState == ProtectionState.Protected)
				_hud!.Drain(player, state.ProtectionTimer, Config.SpawnProtTime);
		}

		private void HudEnd(CCSPlayerController player, ProtectionEnd end)
		{
			if (!HudOn || !player.IsValid)
				return;

			if (end == ProtectionEnd.Quiet || !Config.SpawnProtEndAnnouce)
			{
				_hud!.Hide(player);
				return;
			}

			var sub = end switch
			{
				ProtectionEnd.Fired => _hud!.Text("hud.ended.fired", "You fired your weapon"),
				ProtectionEnd.Moved => _hud!.Text("hud.ended.moved", "You moved"),
				_ => _hud!.Text("hud.ended.sub", "You can be damaged now"),
			};
			_hud.Ended(player, sub);
		}

		private void HudRoundEnd() => _hud?.HideAll();

		private void HudMapStart() => _hud?.Clear();

		private void HudDisconnect(CCSPlayerController player) => _hud?.OnDisconnect(player.Slot);
	}
}
