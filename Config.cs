namespace SpawnProtection
{
	using CounterStrikeSharp.API.Core;
	using System.Text.Json.Serialization;

	public sealed class PluginConfig : BasePluginConfig
	{
		[JsonPropertyName("protection-time")]
		public float SpawnProtTime { get; set; } = 10.0f;

		[JsonPropertyName("disable-during-warmup")]
		public bool DisableDuringWarmup { get; set; } = true;

		[JsonPropertyName("spawn-prot-end-announce")]
		public bool SpawnProtEndAnnouce { get; set; } = true;

		[JsonPropertyName("display-timer")]
		public bool CenterHtmlMessage { get; set; } = true;

		[JsonPropertyName("render-models-transparent")]
		public bool TransparentModel { get; set; } = true;

		[JsonPropertyName("ct-protection-only")]
		public bool CTProtOnly { get; set; } = false;

		[JsonPropertyName("stop-on-player-move")]
		public bool StopProtectionOnMove { get; set; } = false;

		[JsonPropertyName("stop-on-weapon-fire")]
		public bool StopProtectionOnWeaponFire { get; set; } = false;

		// Optional: a config without it gets the defaults (Plugin.Hud.cs). Not written back - CSS only writes
		// the file when there is none, and ConfigVersion is not bumped, so config.Update() never runs for it.
		// Keep it that way: the mounted SpawnProtection.json says ConfigVersion 5, so a Version of 6 or more
		// here would make every server rewrite that shared file on load.
		[JsonPropertyName("Hud")]
		public HudConfig Hud { get; set; } = new();

		[JsonPropertyName("ConfigVersion")]
		public override int Version { get; set; } = 1;
	}
}