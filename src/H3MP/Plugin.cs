using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Discord;
using HarmonyLib;
using H3MP.Configs;
using H3MP.HarmonyPatches;
using H3MP.Messages;
using H3MP.Models;
using H3MP.Networking;
using H3MP.Networking.Extensions;
using H3MP.Peers;
using H3MP.Utils;
using LiteNetLib;
using System;
using System.Collections;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace H3MP
{
	[BepInPlugin(Plugin.GUID, Plugin.NAME, Plugin.VERSION)]
	[BepInProcess("h3vr.exe")]
	public class Plugin : BaseUnityPlugin
	{
		public const string GUID = "Ash.H3MP";
		public const string NAME = "H3MP";
		public const string VERSION = "0.1.1";

		private const long DISCORD_APP_ID = 762557783768956929; // 3rd party RPC application
		private const uint STEAM_APP_ID = 450540; // H3VR

		[DllImport("kernel32.dll")]
		private static extern IntPtr LoadLibrary(string path);

		// Unity moment
		public static Plugin Instance { get; private set; }

		private readonly RootConfig _config;

		private readonly Version _version;
		private readonly RandomNumberGenerator _rng;

		private readonly ManualLogSource _clientLog;
		private readonly ManualLogSource _serverLog;
		private readonly ManualLogSource _discordLog;

		private readonly ChangelogPanel _changelogPanel;

		private readonly UniversalMessageList<H3Client, H3Server> _messages;

		public Discord.Discord DiscordClient { get; }

		public ActivityManager ActivityManager { get; }

		public StatefulActivity Activity { get; }

		public H3Server Server { get; private set; }

		public H3Client Client { get; private set; }

		public Plugin()
		{
			Logger.LogDebug("Binding configs...");
			{
				TomlTypeConverter.AddConverter(typeof(IPAddress), new TypeConverter
				{
					ConvertToObject = (raw, type) => IPAddress.Parse(raw),
					ConvertToString = (value, type) => ((IPAddress) value).ToString()
				});

				_config = new RootConfig(Config);
			}

			Logger.LogDebug("Initializing utilities...");
			{
				_version = new Version(VERSION);
				_rng = RandomNumberGenerator.Create();

				_clientLog = BepInEx.Logging.Logger.CreateLogSource(NAME + "-CL");
				_serverLog = BepInEx.Logging.Logger.CreateLogSource(NAME + "-SV");
				_discordLog = BepInEx.Logging.Logger.CreateLogSource(NAME + "-DC");
			}

			Logger.LogDebug("Initializing Discord game SDK...");
			{
				LoadLibrary("BepInEx\\plugins\\H3MP\\" + Discord.Constants.DllName + ".dll");

				DiscordClient = new Discord.Discord(DISCORD_APP_ID, (ulong) CreateFlags.Default);
				DiscordClient.SetLogHook(Discord.LogLevel.Debug, (level, message) =>
				{
					switch (level)
					{
						case Discord.LogLevel.Error:
							_discordLog.LogError(message);
							break;
						case Discord.LogLevel.Warn:
							_discordLog.LogWarning(message);
							break;
						case Discord.LogLevel.Info:
							_discordLog.LogInfo(message);
							break;
						case Discord.LogLevel.Debug:
							_discordLog.LogDebug(message);
							break;

						default:
							throw new NotImplementedException(level.ToString());
					}
				});

				ActivityManager = DiscordClient.GetActivityManager();
				Activity = new StatefulActivity(ActivityManager, DiscordCallbackHandler);

				ActivityManager.RegisterSteam(STEAM_APP_ID);

				ActivityManager.OnActivityJoinRequest += OnJoinRequested;
				ActivityManager.OnActivityJoin += OnJoin;
			}

			Logger.LogDebug("Creating message table...");
			{
				_messages = new UniversalMessageList<H3Client, H3Server>(_clientLog, _serverLog)
					// =======
					// Client
					// =======
					// Time synchronization (reliable adds latency)
					.AddClient<PingMessage>(0, DeliveryMethod.Sequenced, H3Server.OnClientPing)
					// Player movement
					.AddClient<Timestamped<PlayerTransformsMessage>>(1, DeliveryMethod.Sequenced, H3Server.OnPlayerMove)
					// Asset management
					.AddClient<LevelChangeMessage>(2, DeliveryMethod.ReliableOrdered, H3Server.OnLevelChange)
					//
					// =======
					// Server
					// =======
					// Time synchronization (reliable adds latency)
					.AddServer<Timestamped<PingMessage>>(0, DeliveryMethod.Sequenced, H3Client.OnServerPong)
					// Player movement
					.AddServer<PlayerMovesMessage>(1, DeliveryMethod.Sequenced, H3Client.OnPlayersMove)
					// Asset management
					.AddServer<InitMessage>(2, DeliveryMethod.ReliableOrdered, H3Client.OnInit)
					.AddServer<LevelChangeMessage>(2, DeliveryMethod.ReliableOrdered, H3Client.OnLevelChange)
					.AddServer<PlayerJoinMessage>(2, DeliveryMethod.ReliableOrdered, H3Client.OnPlayerJoin)
					.AddServer<PlayerLeaveMessage>(2, DeliveryMethod.ReliableOrdered, H3Client.OnPlayerLeave)
				;
			}

			Logger.LogDebug("Initializing shared Harmony state...");
			HarmonyState.Init(Activity);

			Logger.LogDebug("Hooking into sceneLoaded...");
			_changelogPanel = new ChangelogPanel(Logger, StartCoroutine, _version);
		}

		private void DiscordCallbackHandler(Result result)
		{
			if (result == Result.Ok)
			{
				return;
			}

			Debug.LogError($"Discord activity update failed ({result})");
		}

		private void OnJoin(string rawSecret)
		{
			const string errorPrefix = "Failed to handle join event: ";

			_discordLog.LogDebug($"Received Discord join secret \"{rawSecret}\"");

			byte[] data;
			try
			{
				data = Convert.FromBase64String(rawSecret);
			}
			catch
			{
				_discordLog.LogError(errorPrefix + "could not parse base 64 secret.");
				return;
			}

			bool success = JoinSecret.TryParse(data, out var secret, out var version);
			if (!_version.CompatibleWith(version))
			{
				_discordLog.LogError(errorPrefix + $"version incompatibility detected (you: {_version}; host: {version})");
				return;
			}

			if (!success)
			{
				_discordLog.LogError(errorPrefix + "join secret was malformed.");
				return;
			}

			ConnectRemote(secret);
		}

		private void OnJoinRequested(ref User user)
		{
			// All friends can join
			// TODO: Change this.
			ActivityManager.SendRequestReply(user.Id, ActivityJoinRequestReply.Yes, DiscordCallbackHandler);
		}

		private IEnumerator _HostUnsafe()
		{
			_serverLog.LogDebug("Starting server...");

			var config = _config.Host;

			var binding = config.Binding;
			var ipv4 = binding.IPv4.Value;
			var ipv6 = binding.IPv6.Value;
			var port = binding.Port.Value;
			var localhost = new IPEndPoint(ipv4 == IPAddress.Any ? IPAddress.Loopback : ipv4, port);

			IPEndPoint publicEndPoint;
			{
				IPAddress publicAddress;
				{
					var getter = config.PublicBinding.GetAddress();
					foreach (object o in getter._Run()) yield return o;

					var result = getter.Result;

					if (!result.Key)
					{
						_serverLog.LogFatal($"Failed to get public IP address to host server with: {result.Value}");
						yield break;
					}

					// Safe to parse, already checked by AddressGetter
					publicAddress = IPAddress.Parse(result.Value);
				}

				ushort publicPort = config.PublicBinding.Port.Value;
				if (publicPort == 0)
				{
					publicPort = port;
				}

				publicEndPoint = new IPEndPoint(publicAddress, publicPort);
			}


			float ups = 1 / Time.fixedDeltaTime; // 90
			double tps = config.TickRate.Value;
			if (tps <= 0)
			{
				_serverLog.LogFatal("The configurable tick rate must be a positive value.");
				yield break;
			}

			if (tps > ups)
			{
				tps = ups;
				_serverLog.LogWarning($"The configurable tick rate ({tps:.00}) is greater than the local fixed update rate ({ups:.00}). The config will be ignored and the fixed update rate will be used instead; running a tick rate higher than your own fixed update rate has no benefits.");
			}

			double tickDeltaTime = 1 / tps;

			Server = new H3Server(_serverLog, _config.Host, _rng, _messages.Server, _messages.ChannelsCount, _version, tickDeltaTime, publicEndPoint);
			_serverLog.LogInfo($"Now hosting on {publicEndPoint}!");

			ConnectLocal(localhost, Server.Secret, Server.HostKey);
		}

		private IEnumerator _Host()
		{
			Logger.LogDebug("Killing peers...");

			Client?.Dispose();
			Client = null;

			Server?.Dispose();
			Server = null;

			return _HostUnsafe();
		}

		private void Connect(IPEndPoint endPoint, Key32? hostKey, JoinSecret secret, OnH3ClientDisconnect onDisconnect)
		{
			_clientLog.LogInfo($"Connecting to {endPoint}...");

			float ups = 1 / Time.fixedDeltaTime;
			double tps = 1 / secret.TickDeltaTime;

			_clientLog.LogDebug($"Fixed update rate: {ups:.00} u/s");
			_clientLog.LogDebug($"Tick rate: {tps:.00} t/s");

			var request = new ConnectionRequestMessage(secret.Key, hostKey);
			Client = new H3Client(_clientLog, _config.Client, Activity, _messages.Client, _messages.ChannelsCount, secret.TickDeltaTime, _version, endPoint, request, onDisconnect);
		}

		private void ConnectLocal(IPEndPoint endPoint, JoinSecret secret, Key32 hostKey)
		{
			Connect(endPoint, hostKey, secret, info =>
			{
				_clientLog.LogError("Disconnected from local server. Something probably caused the frame to hang for more than 5s (debugging breakpoint?). Restarting host...");

				StartCoroutine(_Host());
			});
		}

		private void ConnectRemote(JoinSecret secret)
		{
			Client?.Dispose();

			Connect(secret.EndPoint, null, secret, info =>
			{
				_clientLog.LogError("Disconnected from remote server.");

				if (_config.AutoHost.Value)
				{
					Logger.LogDebug("Autostarting host from client disconnection...");

					StartCoroutine(_Host());
				}
			});
		}

		private void Awake()
		{
			Instance = this;

			new Harmony(Info.Metadata.GUID).PatchAll();
		}

		private void Start()
		{
			if (_config.AutoHost.Value)
			{
				Logger.LogDebug("Autostarting host from game launch...");

				StartCoroutine(_Host());
			}					
		}

		private void Update()
		{
			Client?.RenderUpdate();
		}

		private void FixedUpdate()
		{
			DiscordClient.RunCallbacks();

			Client?.Update();
			Server?.Update();
		}

		private void OnDestroy()
		{
			DiscordClient.Dispose();
			_changelogPanel.Dispose();

			Server?.Dispose();
			Client?.Dispose();
		}
	}
}
