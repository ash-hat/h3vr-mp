using BepInEx.Logging;
using FistVR;
using H3MP.Configs;
using H3MP.Messages;
using H3MP.Models;
using H3MP.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using UnityEngine;

namespace H3MP.Peers
{
	public class Puppet : IRenderUpdatable, IDisposable
	{
		// Should slowly shrink to provide better reliability.
		private const double INTERP_DELAY_EMA_ALPHA = 1d / 10;

		private static readonly int[][] _rendererColorIDs;

		static Puppet()
		{
			var rendererColorNames = new string[][]
			{
				new string[]
				{
					"_RimColor",
					"_EmissionColor"
				},
				new string[]
				{
					"_RimColor"
				}
			};

			_rendererColorIDs = new int[rendererColorNames.Length][];
			for (var i = 0; i < _rendererColorIDs.Length; ++i)
			{
				ref int[] colors = ref _rendererColorIDs[i];

				colors = new int[rendererColorNames[i].Length];
				for (var j = 0; j < colors.Length; ++j)
				{
					colors[j] = Shader.PropertyToID(rendererColorNames[i][j]);
				}
			}
		}

		private readonly ManualLogSource _log;
		private readonly double _minInterpDelay;

		private readonly GameObject _root;
		private readonly GameObject _head;
		private readonly GameObject _handLeft;
		private readonly GameObject _handRight;

		private readonly Func<ServerTime> _timeGetter;
		private readonly ExponentialMovingAverage _interpDelay;
		private readonly Snapshots<PlayerTransformsMessage> _snapshots;

		private ServerTime Time => _timeGetter();

		private static GameObject CreateRoot(ClientPuppetConfig config)
		{
			var root = new GameObject("Puppet Root");
			GameObject.DontDestroyOnLoad(root);

			root.transform.localScale = config.RootScale.Value;

			return root;
		}

		private class ReplacementPlayerSosigBody : MonoBehaviour
		{
			// Token: 0x06000F63 RID: 3939 RVA: 0x000655A8 File Offset: 0x000639A8
			public void ApplyOutfit(SosigOutfitConfig o)
			{
				if (this.m_curClothes.Count > 0)
				{
					for (int i = this.m_curClothes.Count - 1; i >= 0; i--)
					{
						if (this.m_curClothes[i] != null)
						{
							UnityEngine.Object.Destroy(this.m_curClothes[i]);
						}
					}
				}
				this.m_curClothes.Clear();
				this.SpawnAccesoryToLink(o.Headwear, o.Chance_Headwear);
				this.SpawnAccesoryToLink(o.Facewear, o.Chance_Facewear);
				this.SpawnAccesoryToLink(o.Eyewear, o.Chance_Eyewear);
			}

			// Token: 0x06000F64 RID: 3940 RVA: 0x000656C0 File Offset: 0x00063AC0
			private void SpawnAccesoryToLink(List<FVRObject> gs, float chance)
			{
				if (UnityEngine.Random.Range(0f, 1f) > chance)
				{
					return;
				}
				if (gs.Count < 1)
				{
					return;
				}
				GameObject gameObject = UnityEngine.Object.Instantiate<GameObject>(gs[UnityEngine.Random.Range(0, gs.Count)].GetGameObject());
				this.m_curClothes.Add(gameObject);
                UnityEngine.Component[] componentsInChildren = gameObject.GetComponentsInChildren<UnityEngine.Component>(true);
				for (int i = componentsInChildren.Length - 1; i >= 0; i--)
				{
					if (componentsInChildren[i] is Transform || componentsInChildren[i] is MeshFilter || componentsInChildren[i] is MeshRenderer)
					{
						continue;
					}

					UnityEngine.Object.Destroy(componentsInChildren[i]);
				}
				gameObject.transform.parent = transform;
				gameObject.transform.localPosition = Vector3.zero;
				gameObject.transform.localRotation = Quaternion.identity;
			}

			// Token: 0x04001BF4 RID: 7156
			private List<GameObject> m_curClothes = new List<GameObject>();
		}

		void MoveToLayer(Transform root, int layer)
		{
			root.gameObject.layer = layer;
			foreach (Transform child in root)
				MoveToLayer(child, layer);
		}

		private GameObject CreateBody(GameObject prefab, ClientPuppetLimbConfig config)
		{
			var body = GameObject.Instantiate(prefab);
			GameObject.Destroy(body.GetComponent<PlayerSosigBody>());
			body.SetActive(true);
			MoveToLayer(body.transform, default);
			body.AddComponent<ReplacementPlayerSosigBody>().ApplyOutfit(ManagerSingleton<IM>.Instance.odicSosigObjsByID[SosigEnemyID.MF_RedHots_Spy].OutfitConfig.First());

			// Components
			var transform = body.transform;
			var collider = body.GetComponent<Collider>();

			// Parent before scale (don't parent after)
			transform.parent = _root.transform;
			transform.localScale = config.Scale.Value;

			// No collision
			GameObject.Destroy(collider);

			return body;
		}

		private static GameObject GetBodyPrefabFrom(FVRPlayerBody body)
		{
			return body.PlayerSosigBodyPrefab.GetGameObject();
		}

		private GameObject CreateController(GameObject prefab, ClientPuppetLimbConfig config)
		{
			// Spawn controller
			var controller = GameObject.Instantiate(prefab);
			controller.SetActive(true);
			var controllerTransform = controller.transform;

			// Parent before scale (don't parent after)
			controllerTransform.parent = _root.transform;
			controllerTransform.localScale = config.Scale.Value;

			// Colors?
			float? hueDelta = null;
			for (var i = 0; i < _rendererColorIDs.Length; ++i)
			{
				var renderer = controllerTransform.GetChild(i).GetComponent<Renderer>();
				var material = renderer.material;
				var colorIDs = _rendererColorIDs[i];

				foreach (var colorID in colorIDs)
				{
					// Get current controller color values
					Color.RGBToHSV(material.GetColor(colorID), out float h, out float s, out float v);

					// If this is the first color, use it as the baseline hue shift.
					if (!hueDelta.HasValue)
					{
						hueDelta = config.Color.Value - h;
					}

					// Apply hue delta to controller hue.
					float shiftedH = (h + hueDelta.Value) % 1f;
					var value = Color.HSVToRGB(shiftedH, s, v);

					// Set controller color values.
					material.SetColor(colorID, value);
				}
			}

			return controller;
		}

		private static GameObject GetControllerFrom(Transform hand)
		{
			return hand.GetComponent<FVRViveHand>().Display_Controller_Index;
		}

		internal Puppet(ManualLogSource log, ClientPuppetConfig config, Func<ServerTime> timeGetter, double tickDeltaTime)
		{
			_log = log;
			// A tick step for the remote client transmitting, server tranceiving, and local client receiving, each.
			// Sometimes the stars align and no tick delay is achieved, but not on average, so the minimum should be when all the stars are perfectly not aligned.
			// Any further tweaking should be just because of network delay.
			//
			// If input-based movement is achieved, this can be reduced to 2.
			_minInterpDelay = 3 * tickDeltaTime;

			// Unity objects
			_root = CreateRoot(config);
			_head = CreateBody(GetBodyPrefabFrom(GM.CurrentPlayerBody),config.Head);

			_handLeft = CreateController(GetControllerFrom(GM.CurrentPlayerBody.LeftHand), config.HandLeft);
			_handRight = CreateController(GetControllerFrom(GM.CurrentPlayerBody.RightHand), config.HandRight);

			// .NET objects
			_timeGetter = timeGetter;
			_interpDelay = new ExponentialMovingAverage(_minInterpDelay, INTERP_DELAY_EMA_ALPHA);
			var killer = new TimeSnapshotKiller<PlayerTransformsMessage>(() => Time.Now, 5);
			_snapshots = new Snapshots<PlayerTransformsMessage>(killer);
		}

		public void ProcessTransforms(Timestamped<PlayerTransformsMessage> message)
		{
			var time = Time;
			if (!(time is null))
			{
				var messageDelay = time.Now - message.Timestamp;
				var interpDelay = _interpDelay.Value;

				if (messageDelay > interpDelay)
				{
					_interpDelay.Reset(messageDelay);
					_log.LogDebug($"A puppet's interpolation delay jumped ({interpDelay * 1000:N0} ms -> {messageDelay * 1000:N0} ms)");
				}
				else
				{
					_interpDelay.Push(Math.Max(messageDelay, _minInterpDelay));
				}
			}

			_snapshots.Push(message.Timestamp, message.Content);
		}

		public void RenderUpdate()
		{
			var time = Time;
			if (time is null)
			{
				return;
			}

			var snapshot = _snapshots[time.Now - _interpDelay.Value];

			snapshot.Head.Apply(_head.transform);
			snapshot.HandLeft.Apply(_handLeft.transform);
			snapshot.HandRight.Apply(_handRight.transform);
		}

		public void Dispose()
		{
			GameObject.Destroy(_root);
		}
	}
}
