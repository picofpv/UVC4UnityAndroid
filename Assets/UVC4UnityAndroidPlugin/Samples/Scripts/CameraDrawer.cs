﻿//#define ENABLE_LOG

using Serenegiant.UVC;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/*
 * AndroidでUVC機器からの映像をUnityのテクスチャとして取得するための
 * プラグイン(uvc-plugin-unity)へアクセスするためのヘルパークラスです。
 * 
 * プラグイン側では非UI Frgmentを使ってライフサイクルをコントロールしています。
 * Unityのメインアクティビティがフレームワーク版のActivityを使用しているため
 * プラグインでもフレームワーク版のFragmentを使用しています。
 * 
 * 現在の実装では接続されたUVC機器のうち１つだけしかハンドリングできません
 * ただしプラグイン自体は複数UVC機器に対応しているのでプラグインからの
 * イベント処理時に複数機器対応に変更すれば動作可能です。
 *
 * uvc-plugin-unity aar内で永続的パーミッションを保持するためのActivityを
 * AndroidManifest.xmlへ設定してあります。
 * もし永続的パーミッションを保持する必要がなければ、
 * 作成中のアプリのAndroidManifest.xmlへ
 * 次の行を追加してください。
 * 
 * <activity android:name="com.serenegiant.uvcplugin.UsbPermissionActivity" tools:node="remove"/>
 * 
 */

namespace Serenegiant
{

	[RequireComponent(typeof(AndroidUtils))]
	public class CameraDrawer : MonoBehaviour, ICameraEventHandler
	{
		// THETA VのH.264映像: 3840x1920@30fps, H.264
		// THETA SのH.264映像: 1920x1080@30fps, H.264
		// 普通のUVC機器: 1280x720/1920x1080 MJPEG

		/**
		 * IUVCSelectorがセットされていないとき
		 * またはIUVCSelectorが解像度選択時にnullを
		 * 返したときのデフォルトの解像度(幅)
		 */
		public int DefaultWidth = 1280;
		/**
		 * IUVCSelectorがセットされていないとき
		 * またはIUVCSelectorが解像度選択時にnullを
		 * 返したときのデフォルトの解像度(高さ)
		 */
		public int DefaultHeight = 720;
		/**
		 * UVC機器とのネゴシエーション時に
		 * H.264を優先してネゴシエーションするかどうか
		 * Android実機のみ有効
		 * true:	H.264 > MJPEG > YUV
		 * false:	MJPEG > H.264 > YUV
		 */
		public bool PreferH264 = true;

		/**
		 * UVC機器を使わないようにするかどうか
		 * true: Android実機でもUVC機器を使わない
		 */
		public bool DisableUVC = false;
		/**
		 * UVC機器からの映像の描画先Materialを保持しているGameObject
		 * 設定していない場合はこのスクリプトを割当てたのと同じGameObjecを使う。
		 */
		public List<RenderTargetSettings> CameraRenderSettings;

		/**
		 * UVC機器とその解像度を選択するためのインターフェース
		 */
		public IUVCSelector UVCSelector;

		//--------------------------------------------------------------------------------
		private const string TAG = "CameraDrawer#";

		private class CameraInfo
		{
			public readonly int cameraIx;
			public bool isActive;

			public CameraInfo(int ix)
			{
				cameraIx = ix;
			}
		}
	
		/**
		 * カメラ毎の設定保持用
		 */
		private class TargetInfo
		{
			public readonly int Count;
			/**
			 * UVC機器からの映像の描画先Material
			 * TargetGameObjectから取得する
			 * 優先順位：
			 *	 TargetGameObjectのSkybox
			 *	 > TargetGameObjectのRenderer
			 *	 > TargetGameObjectのRawImage
			 *	 > TargetGameObjectのMaterial
			 * いずれの方法でも取得できなければStartでUnityExceptionを投げる
			 */
			public readonly UnityEngine.Object[] TargetMaterials;
			/**
			 * オリジナルのテクスチャ
			 * UVCカメラ映像受け取り用テクスチャをセットする前に
			 * GetComponent<Renderer>().material.mainTextureに設定されていた値
			 */
			public readonly Texture[] SavedTextures;

			public Quaternion[] quaternions;

			public TargetInfo(int targetNums)
			{
				Count = targetNums;
				TargetMaterials = new UnityEngine.Object[targetNums];
				SavedTextures = new Texture[targetNums];
				quaternions = new Quaternion[targetNums];
		}

		public void RestoreTexture()
			{
				for (int i = 0; i < Count; i++)
				{
					var target = TargetMaterials[i];
					if (target is Material)
					{
						(target as Material).mainTexture = SavedTextures[i];
					}
					else if (target is RawImage)
					{
						(target as RawImage).texture = SavedTextures[i];
					}
					SavedTextures[i] = null;
					quaternions[i] = Quaternion.identity;
				}
			}

			public void ClearTextures()
			{
				for (int i = 0; i < SavedTextures.Length; i++)
				{
					SavedTextures[i] = null;
				}
			}
		}

		/**
		 * カメラ毎の設定値
		 */
		private TargetInfo[] targetInfos;

		/**
		 * ハンドリングしているカメラ情報
		 * string(deviceName) - CameraInfo ペアを保持する
		 */
		private Dictionary<string, CameraInfo> cameraInfos = new Dictionary<string, CameraInfo>();

		//--------------------------------------------------------------------------------

		private WebCamController webCamController;

		//================================================================================

		// Start is called before the first frame update
		IEnumerator Start()
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}Start:");
#endif
			UpdateTarget();

			yield return Restart();

			yield break;
		}

		// Update is called once per frame
		void Update()
		{
			// FIXME 映像の回転制御 未実装	
			// XXX UVC機器の場合は端末の画面回転と映像の回転は関係がないのでWebCamControllerの場合のみ処理する
			if (webCamController != null)
			{
				foreach (var info in cameraInfos)
				{
					if (info.Value.isActive)
					{   // カメラから映像取得中
						var settings = CameraRenderSettings[info.Value.cameraIx];
						var q = webCamController.AngleAxis(info.Key);

						// FIXME 未実装
					}
				}
			}
		}

		void OnApplicationFocus()
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnApplicationFocus:");
#endif
		}

		void OnApplicationPause(bool pauseStatus)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnApplicationPause:{pauseStatus}");
#endif
			if (pauseStatus)
			{
				OnPauseEvent();
			} else {
				OnResumeEvent();
			}
		}

		void OnApplicationQuits()
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnApplicationQuits:");
#endif
		}

		void OnDestroy()
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnDestroy:");
#endif
			CloseAll();
		}

		//================================================================================
		// 他のコンポーネントからの操作用
		/**
		 * 映像描画先のMaterialを再取得する
		 */
		public void ResetMaterial()
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}ResetMaterial:");
#endif
			bool[] prevs = new bool[cameraInfos.Count];
			int i = 0;
			foreach (var elm in cameraInfos)
			{
				prevs[i++] = elm.Value.isActive;
				if (elm.Value.isActive)
				{
					StopPreview(elm.Key);
				}
			}
			UpdateTarget();
			i = 0;
			foreach (var elm in cameraInfos)
			{
				if (prevs[i++])
				{
					StartPreview(elm.Key);
				}
			}
		}

		public IEnumerator Restart()
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}Restart:");
#endif
			CloseAll();
			webCamController = new WebCamController(this, gameObject, DefaultWidth, DefaultHeight);
			yield return webCamController.Initialize();
			yield break;
		}
		//================================================================================

		/**
		 * UVC機器が接続された
		 * @param args UVC機器の識別文字列
		 */
		public void OnEventAttach(string args)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnEventAttach[{Time.frameCount}]:(" + args + ")");
#endif
			// FIXME RenderTargetSettings.WebCameraDeviceKeywordによるフィルタリングをできるようにする
			if (!String.IsNullOrEmpty(args)
				&& ((UVCSelector == null) || UVCSelector.CanSelect(GetInfo(args))))
			{   // argsはdeviceName
				var info = CreateIfNotExist(args);
				if (webCamController != null)
				{
					webCamController.OnEventAttach(args);
				}
			}
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnEventAttach[{Time.frameCount}]:finished");
#endif
		}

		/**
		 * UVC機器へのアクセスのためのパーミッションを取得できた
		 * @param args UVC機器の識別文字列
		 */
		public void OnEventPermission(string args)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnEventPermission:({args})");
#endif
			if (!String.IsNullOrEmpty(args))
			{   // argsはdeviceName
				Open(args);
			}
		}

		/**
		 * UVC機器をオープンした
		 * @param args UVC機器の識別文字列
		 */
		public void OnEventConnect(string args)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnEventConnect:({args})");
#endif
		}

		/**
		 * UVC機器をクローズした
		 * @param args UVC機器の識別文字列
		 */
		public void OnEventDisconnect(string args)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnEventDisconnect:({args})");
#endif
			// このイベントはUnity側からclose要求を送ったとき以外でも発生するので
			// 念のためにCloseを呼んでおく
			Close(args);
		}

		/**
		 * UVC機器が取り外された
		 * @param args UVC機器の識別文字列
		 */
		public void OnEventDetach(string args)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnEventReady:({args})");
#endif
			if (!String.IsNullOrEmpty(args))
			{   // argsはdeviceName
				Remove(args);
				Close(args);
			}
		}

		/**
		 * 映像を受け取れるようになった
		 * @param args UVC機器の識別文字列
		 */
		public void OnEventReady(string args)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnEventReady:({args})");
#endif
			StartPreview(args);
		}

		/**
		 * UVC機器からの映像取得を開始した
		 * @param args UVC機器の識別文字列
		 */
		public void OnStartPreview(string args)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnStartPreview:({args})");
#endif
			Texture tex = null;
			if (webCamController != null)
			{
				tex = webCamController.GetTexture();
			}
			if (tex != null)
			{
				HandleOnStartPreview(args, tex);
			}
		}

		/**
		 * UVC機器からの映像取得を終了した
		 * @param args UVC機器の識別文字列
		 */
		public void OnStopPreview(string args)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnStopPreview:({args})");
#endif
			HandleOnStopPreview(args);
		}

		/**
		 * UVC機器からのステータスイベントを受信した
		 * @param args UVC機器の識別文字列+ステータス
		 */
		public void OnReceiveStatus(string args)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnReceiveStatus:({args})");
#endif
		}

		/**
		 * UVC機器からのボタンイベントを受信した
		 * @param args UVC機器の識別文字列＋ボタンイベント
		 */
		public void OnButtonEvent(string args)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnButtonEvent:({args})");
#endif
		}

		/**
		 * onResumeイベント
		 */
		public IEnumerator OnResumeEvent()
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnResumeEvent:attachedDeviceName={AttachedDeviceName},activeDeviceName={ActiveDeviceName}");
#endif
			if (webCamController != null)
			{
				yield return webCamController.OnResumeEvent();
			}
		}

		/**
		 * onPauseイベント
		 */
		public void OnPauseEvent()
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnPauseEvent:");
#endif
			CloseAll();
			if (webCamController != null)
			{
				webCamController.OnPauseEvent();
			}
		}

		//================================================================================
		/**
		 * 描画先を更新
		 */
		private void UpdateTarget()
		{
			bool found = false;
			if ((CameraRenderSettings != null) && (CameraRenderSettings.Count > 0))
			{
				UVCSelector = GetUVCSelector(CameraRenderSettings);
#if (!NDEBUG && DEBUG && ENABLE_LOG)
				Console.WriteLine($"{TAG}UpdateTarget:UVCSelector={UVCSelector}");
#endif
				targetInfos = new TargetInfo[CameraRenderSettings.Count];
				int j = 0;
				foreach (var targets in CameraRenderSettings)
				{
					if (targets != null)
					{
						targetInfos[j] = new TargetInfo(targets.RenderTargets.Count);
						int i = 0;
						foreach (var target in targets.RenderTargets)
						{
							var material = targetInfos[j].TargetMaterials[i++] = GetTargetMaterial(target);
							if (material != null)
							{
								found = true;
							}
#if (!NDEBUG && DEBUG && ENABLE_LOG)
							Console.WriteLine($"{TAG}UpdateTarget:material={material}");
#endif
						}
					}
					j++;
				}
			}
			if (!found)
			{   // 描画先が1つも見つからなかったときはこのスクリプトが
				// AddComponentされているGameObjectからの取得を試みる
				// XXX RenderTargetsにgameObjectをセットする？
				targetInfos = new TargetInfo[1];
				targetInfos[0] = new TargetInfo(1);
				targetInfos[0].TargetMaterials[0] = GetTargetMaterial(gameObject);
				found = targetInfos[0].TargetMaterials[0] != null;
			}

			if (!found)
			{
				throw new UnityException("no target material found.");
			}
		}

		/**
		 * テクスチャとして映像を描画するMaterialを取得する
		 * 指定したGameObjectにSkybox/Renderer/RawImage/MaterialがあればそれからMaterialを取得する
		 * それぞれが複数割り当てられている場合最初に見つかった使用可能ものを返す
		 * 優先度: Skybox > Renderer > RawImage > Material
		 * @param target
		 * @return 見つからなければnullを返す
		 */
		UnityEngine.Object GetTargetMaterial(GameObject target/*NonNull*/)
		{
			// Skyboxの取得を試みる
			var skyboxs = target.GetComponents<Skybox>();
			if (skyboxs != null)
			{
				foreach (var skybox in skyboxs)
				{
					if (skybox.isActiveAndEnabled && (skybox.material != null))
					{
						RenderSettings.skybox = skybox.material;
						return skybox.material;
					}
				}
			}
			// Skyboxが取得できなければRendererの取得を試みる
			var renderers = target.GetComponents<Renderer>();
			if (renderers != null)
			{
				foreach (var renderer in renderers)
				{
					if (renderer.enabled && (renderer.material != null))
					{
						return renderer.material;
					}

				}
			}
			// SkyboxもRendererも取得できなければRawImageの取得を試みる
			var rawImages = target.GetComponents<RawImage>();
			if (rawImages != null)
			{
				foreach (var rawImage in rawImages)
				{
					if (rawImage.enabled && (rawImage.material != null))
					{
						return rawImage;
					}

				}
			}
			// SkyboxもRendererもRawImageも取得できなければMaterialの取得を試みる
			var material = target.GetComponent<Material>();
			if (material != null)
			{
				return material;
			}
			return null;
		}

		/**
		 * IUVCSelectorを取得する
		 * UVCSelectorが設定されていればそれを返す
		 * UVCSelectorが見つからないときはTargetGameObjectから取得を試みる
		 * さらに見つからなければこのスクリプトがaddされているGameObjectから取得を試みる
		 * @return 見つからなければnull
		 */
		IUVCSelector GetUVCSelector(List<RenderTargetSettings> targetList)
		{
			if (UVCSelector != null)
			{
				return UVCSelector;
			}

			IUVCSelector selector;
			foreach (var targets in targetList)
			{
				if (targets != null)
				{
					foreach (var target in targets.RenderTargets)
					{
						if (target != null)
						{
							selector = target.GetComponent(typeof(IUVCSelector)) as IUVCSelector;
							if (selector != null)
							{
								return selector;
							}

						}
					}
				}
			}

			selector = GetComponent(typeof(IUVCSelector)) as IUVCSelector;
			return selector;
		}

		//--------------------------------------------------------------------------------
		private int FindCameraIx(string deviceName)
		{
			var info = Get(deviceName);
			return info != null ? info.cameraIx : -1;
		}

		/**
		 * 指定したカメラ/UVC機器をOpenする
		 * @param deviceName カメラ識別用文字列
		 */
		private void Open(string deviceName)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}Open:{deviceName}");
#endif
			var info = Get(deviceName);
			if (info != null)
			{
				info.isActive = false;
				if (webCamController != null)
				{
					webCamController.Open(deviceName);
				}
			}
		}

		/**
		 * 指定したカメラ/UVC機器をCloseする
		 * @param deviceName カメラ識別用文字列
		 */
		private void Close(string deviceName)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}Close:{deviceName}");
#endif
			var info = Get(deviceName);
			if (info != null)
			{
				info.isActive = false;
				if (webCamController != null)
				{
					webCamController.Close(deviceName);
				}
			}
		}

		/**
		 * Openしているすべてのカメラ/UVC機器をCloseする
		 */
		private void CloseAll()
		{
			List<string> keys = new List<string>(cameraInfos.Keys);
			foreach (var deviceName in keys)
			{
				Close(deviceName);
			}
		}
	
		/**
		 * UVC機器/カメラからの映像受け取り開始要求をする
		 * IUVCSelectorが設定されているときはUVCSelector#SelectSizeから映像サイズの取得を試みる
		 * IUVCSelectorが設定されていないかUVCSelector#SelectSizeがnullを返したときは
		 * スクリプトに設定されているVideoWidth,VideoHeightを使う
		 * @param deviceName カメラ識別文字列
		 */
		private void StartPreview(string deviceName)
		{
			int width = DefaultWidth;
			int height = DefaultHeight;

			var supportedVideoSize = GetSupportedVideoSize(deviceName);
			if (supportedVideoSize == null)
			{
				throw new ArgumentException("fauled to get supported video size");
			}

			// 解像度の選択処理
			if (UVCSelector != null)
			{
				var size = UVCSelector.SelectSize(GetInfo(deviceName), supportedVideoSize);
#if (!NDEBUG && DEBUG && ENABLE_LOG)
				Console.WriteLine($"{TAG}StartPreview:selected={size}");
#endif
				if (size != null)
				{
					width = size.Width;
					height = size.Height;
				}
			}

			// 対応解像度のチェック
			if (supportedVideoSize.Find(width, height/*,minFps=0.1f, maxFps=121.0f*/) == null)
			{   // 指定した解像度に対応していない
#if (!NDEBUG && DEBUG && ENABLE_LOG)
				Console.WriteLine($"{TAG}StartPreview:{width}x{height} is NOT supported.");
				Console.WriteLine($"{TAG}Info={GetInfo(deviceName)}");
				Console.WriteLine($"{TAG}supportedVideoSize={supportedVideoSize}");
#endif
				throw new ArgumentOutOfRangeException($"{width}x{height} is NOT supported.");
			}
			if (webCamController != null)
			{
				webCamController.StartPreview(deviceName, width, height);
			}
		}

		/**
		 * UVC機器/カメラからの映像受けとりを終了要求をする
		 * @param deviceName カメラ識別文字列
		 */
		private void StopPreview(string deviceName)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}StopPreview:{deviceName}");
#endif

			HandleOnStopPreview(deviceName);
			if (webCamController != null)
			{
				webCamController.StopPreview(deviceName);
			}
		}

		/**
		 * 映像取得開始時の処理
		 * @param tex 映像を受け取るテクスチャ
		 */
		private void HandleOnStartPreview(string deviceName, Texture tex)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}HandleOnStartPreview:({tex})");
#endif
			int cameraIx = FindCameraIx(deviceName);
			if ((cameraIx < targetInfos.Length) && (targetInfos[cameraIx] != null))
			{
				if (targetInfos[cameraIx].Count > 0)
				{
					int i = 0;
					foreach (var target in targetInfos[cameraIx].TargetMaterials)
					{
						if (target is Material)
						{
							targetInfos[cameraIx].SavedTextures[i++] = (target as Material).mainTexture;
							(target as Material).mainTexture = tex;
						}
						else if (target is RawImage)
						{
							targetInfos[cameraIx].SavedTextures[i++] = (target as RawImage).texture;
							(target as RawImage).texture = tex;
						}
					}
				}
				else
				{
					targetInfos[cameraIx].ClearTextures();
				}
			} else 
			{
				throw new ArgumentOutOfRangeException();
			}
		}

		/**
		 * 映像取得が終了したときのUnity側の処理
		 * @param deviceName カメラの識別文字列
		 */
		private void HandleOnStopPreview(string deviceName)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}HandleOnStopPreview:{deviceName}");
#endif
			int cameraIx = 0;	// FIXME deviceNameから探す
			// 描画先のテクスチャをもとに戻す
			if ((cameraIx < targetInfos.Length) && (targetInfos[cameraIx] != null))
			{
				targetInfos[cameraIx].RestoreTexture();
			}
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}HandleOnStopPreview:finished");
#endif
		}

		/*NonNull*/
		private CameraInfo CreateIfNotExist(string deviceName)
		{
			if (!cameraInfos.ContainsKey(deviceName))
			{
				int n = cameraInfos.Count;
				int cameraIx = 0;
				foreach (var info in cameraInfos.Values)
				{
					if (info.cameraIx == cameraIx)
					{
						cameraIx++;
					}
				}
				cameraInfos[deviceName] = new CameraInfo(cameraIx);
			}
			return cameraInfos[deviceName];
		}

		/*Nullable*/
		private CameraInfo Get(string deviceName)
		{
			return cameraInfos.ContainsKey(deviceName) ? cameraInfos[deviceName] : null;
		}

		/*Nullable*/
		private CameraInfo Remove(string deviceName)
		{
			CameraInfo info = null;

			if (cameraInfos.ContainsKey(deviceName))
			{
				info = cameraInfos[deviceName];
				cameraInfos.Remove(deviceName);
			}

			return info;
		}

		/**
 * 指定したUVC機器の情報(今はvidとpid)をJSON文字列として取得する
 * @param deviceName UVC機器の識別文字列
 */
		private UVCInfo GetInfo(string deviceName)
		{
			if (webCamController != null)
			{
				return webCamController.GetInfo(deviceName);
			}
			return null;
		}

		/**
		 * 指定したUVC機器の対応解像度をjson文字列として取得する
		 * @param deviceName UVC機器の識別文字列
		 */
		private SupportedFormats GetSupportedVideoSize(string deviceName)
		{
			if (webCamController != null)
			{
				return webCamController.GetSupportedVideoSize(deviceName);
			}
			return null;
		}

	}   // CameraDrawer

}	// namespace Serenegiant.UVC