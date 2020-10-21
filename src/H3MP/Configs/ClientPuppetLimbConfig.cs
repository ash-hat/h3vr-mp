using BepInEx.Configuration;
using UnityEngine;

namespace H3MP.Configs
{
	public class ClientPuppetLimbConfig
	{
		public ConfigEntry<Vector3> Scale { get; }

		public ConfigEntry<float> Color { get; }

		public ClientPuppetLimbConfig(ConfigFile config, string section, float defaultSize, float defaultColor)
		{
			Scale = config.Bind(section, nameof(Scale), defaultSize * Vector3.one, "The scale of the limb.");
			Color = config.Bind(section, nameof(Color), defaultColor, "The hue of the limb.");
		}
	}
}
