#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime;
using System.Threading;
using OpenRA.Graphics;
using OpenRA.Network;
using OpenRA.Primitives;
using OpenRA.Server;
using OpenRA.Support;
using OpenRA.Widgets;

namespace OpenRA
{
	[IncludeStaticFluentReferences(typeof(Server.Server), typeof(Player), typeof(UnitOrders), typeof(OrderManager))]
	public static class Game
	{
		[FluentReference("filename")]
		const string SavedScreenshot = "notification-saved-screenshot";

		public const int TimestepJankThreshold = 250; // Don't catch up for delays larger than 250ms

		public static InstalledMods Mods { get; private set; }
		public static ExternalMods ExternalMods { get; private set; }

		public static ModData ModData;
		public static Settings Settings;
		public static CursorManager Cursor;
		public static bool HideCursor;

		static WorldRenderer worldRenderer;
		static string modLaunchWrapper;

		internal static OrderManager OrderManager;
		static Server.Server server;

		/// <summary>The current in-process server (if any). Used by the room bridge for browser-hosted MP.</summary>
		public static Server.Server InProcessServer => server;

		/// <summary>
		/// Callback to create a relay room for browser-hosted MP.
		/// Set by the web platform during initialization.
		/// </summary>
		public static Action CreateRelayRoom;

		/// <summary>
		/// Callback to tick remote guest connections (drain server outbound to relay).
		/// Set by the web platform during initialization.
		/// </summary>
		public static Action TickRemoteConnections;

		public static MersenneTwister CosmeticRandom = new(); // not synced

		/// <summary>
		/// Factory for creating IConnection on WASM (WebSocket-based).
		/// Set by the platform during initialization.
		/// </summary>
		public static Func<ConnectionTarget, IConnection> CreateWebConnection;

		/// <summary>
		/// Pending in-process connection for WASM skirmish.
		/// Set by CreateLocalServer on WASM, consumed by JoinServer.
		/// </summary>
		internal static IConnection PendingInProcessConnection;

		/// <summary>
		/// Room ID to auto-join as guest on boot. Set by web platform if ?room=X URL param present.
		/// </summary>
		public static string PendingRoomJoin;

		/// <summary>
		/// Factory to create a guest room connection. Set by web platform during initialization.
		/// </summary>
		public static Func<string, IConnection> CreateRoomConnection;

		/// <summary>
		/// Auto-join a relay room after the main menu loads.
		/// Called once from OnShellmapLoaded if PendingRoomJoin is set.
		/// </summary>
		public static void TryAutoJoinRoom()
		{
			var roomId = PendingRoomJoin;
			if (string.IsNullOrEmpty(roomId) || CreateRoomConnection == null)
				return;

			PendingRoomJoin = null;
			Console.WriteLine($"[room-join] Auto-joining room: {roomId}");

			RunAfterTick(() =>
			{
				try
				{
					PendingInProcessConnection = CreateRoomConnection(roomId);
					var om = JoinServer(new ConnectionTarget("room", 0), "");

					void OnStateChanged(OrderManager orderManager, string password, NetworkConnection conn)
					{
						var state = orderManager.Connection.ConnectionState;
						if (state == ConnectionState.Connected)
						{
							ConnectionStateChanged -= OnStateChanged;
							Console.WriteLine("[room-join] Connected to room, opening lobby");

							RunAfterTick(() =>
							{
								OpenWindow("SERVER_LOBBY", new WidgetArgs
								{
									{ "onStart", (Action)(() => { }) },
									{ "onExit", (Action)(() => Disconnect()) },
									{ "skirmishMode", false }
								});
							});
						}
						else if (state == ConnectionState.NotConnected)
						{
							ConnectionStateChanged -= OnStateChanged;
							Console.WriteLine($"[room-join] Connection failed: {orderManager.Connection.ErrorMessage}");
						}
					}

					ConnectionStateChanged += OnStateChanged;
				}
				catch (Exception ex)
				{
					Console.Error.WriteLine($"[room-join] Auto-join failed: {ex.Message}");
				}
			});
		}

		/// <summary>
		/// WASM transition overlay callbacks. Set by WasmMain during initialization.
		/// </summary>
		public static Action<string> ShowTransitionOverlay;
		public static Action HideTransitionOverlay;

		public static Renderer Renderer;
		public static Sound Sound;

		public static string EngineVersion { get; private set; }
		public static LocalPlayerProfile LocalPlayerProfile;

		static bool takeScreenshot = false;
		internal static bool HeadlessBotMode = false;
		static Benchmark benchmark = null;

		public static event Action OnShellmapLoaded = () => { };

		public static OrderManager JoinServer(ConnectionTarget endpoint, string password, bool recordReplay = true)
		{
			IConnection newConnection;
			if (PendingInProcessConnection != null)
			{
				// In-process connection for WASM skirmish (set by CreateLocalServer)
				newConnection = PendingInProcessConnection;
				PendingInProcessConnection = null;
			}
			else if (OperatingSystem.IsBrowser())
			{
				// On WASM, use a platform-provided IConnection factory
				newConnection = CreateWebConnection(endpoint);
			}
			else
			{
				var networkConn = new NetworkConnection(endpoint);
				if (recordReplay)
					networkConn.StartRecording(() => TimestampedFilename());
				newConnection = networkConn;
			}

			var om = new OrderManager(newConnection);
			JoinInner(om);
			CurrentServerSettings.Password = password;
			CurrentServerSettings.Target = endpoint;

			lastConnectionState = ConnectionState.PreConnecting;
			ConnectionStateChanged(OrderManager, password, newConnection as NetworkConnection);

			return om;
		}

		public static string TimestampedFilename(bool includemilliseconds = false, string extra = "")
		{
			var format = includemilliseconds ? "yyyy-MM-ddTHHmmssfffZ" : "yyyy-MM-ddTHHmmssZ";
			return ModData.Manifest.Id + extra + "-" + DateTime.UtcNow.ToString(format, CultureInfo.InvariantCulture);
		}

		static void JoinInner(OrderManager om)
		{
			// Refresh static classes before the game starts.
			TextNotificationsManager.Clear();
			UnitOrders.Clear();

			// HACK: The shellmap World and OrderManager are owned by the main menu's WorldRenderer instead of Game.
			// This allows us to switch Game.OrderManager from the shellmap to the new network connection when joining
			// a lobby, while keeping the OrderManager that runs the shellmap intact.
			// A matching check in World.Dispose (which is called by WorldRenderer.Dispose) makes sure that we dispose
			// the shellmap's OM when a lobby game actually starts.
			if (OrderManager?.World == null || OrderManager.World.Type != WorldType.Shellmap)
				OrderManager?.Dispose();

			OrderManager = om;
		}

		public static void JoinReplay(string replayFile)
		{
			JoinInner(new OrderManager(new ReplayConnection(replayFile)));
		}

		static void JoinLocal()
		{
			JoinInner(new OrderManager(new EchoConnection()));

			// Add a spectator client for the local player
			// On the shellmap this player is controlling the map via scripted orders
			OrderManager.LobbyInfo.Clients.Add(new Session.Client
			{
				Index = OrderManager.Connection.LocalClientId,
				Name = Settings.Player.Name,
				PreferredColor = Settings.Player.Color,
				Color = Settings.Player.Color,
				Faction = "Random",
				SpawnPoint = 0,
				Team = 0,
				State = Session.ClientState.Ready
			});
		}

		// More accurate replacement for Environment.TickCount
		static readonly Stopwatch Stopwatch = Stopwatch.StartNew();
		public static long RunTime => Stopwatch.ElapsedMilliseconds;

		public static int RenderFrame = 0;
		public static int NetFrameNumber => OrderManager.NetFrameNumber;
		public static int LocalTick => OrderManager.LocalFrameNumber;

		public static event Action<ConnectionTarget> OnRemoteDirectConnect = _ => { };
		public static event Action<OrderManager, string, NetworkConnection> ConnectionStateChanged = (om, pass, conn) => { };
		static ConnectionState lastConnectionState = ConnectionState.PreConnecting;
		public static int LocalClientId => OrderManager.Connection.LocalClientId;

		public static void RemoteDirectConnect(ConnectionTarget endpoint)
		{
			OnRemoteDirectConnect(endpoint);
		}

		// Hacky workaround for orderManager visibility
		public static Widget OpenWindow(World world, string widget)
		{
			return Ui.OpenWindow(widget, new WidgetArgs() { { "world", world }, { "orderManager", OrderManager }, { "worldRenderer", worldRenderer } });
		}

		// Who came up with the great idea of making these things
		// impossible for the things that want them to access them directly?
		public static Widget OpenWindow(string widget, WidgetArgs args)
		{
			return Ui.OpenWindow(widget, new WidgetArgs(args)
			{
				{ "world", worldRenderer.World },
				{ "orderManager", OrderManager },
				{ "worldRenderer", worldRenderer },
			});
		}

		// Load a widget with world, orderManager, worldRenderer args, without adding it to the widget tree
		public static Widget LoadWidget(World world, string id, Widget parent, WidgetArgs args)
		{
			return ModData.WidgetLoader.LoadWidget(new WidgetArgs(args)
			{
				{ "modData", ModData },
				{ "world", world },
				{ "orderManager", OrderManager },
				{ "worldRenderer", worldRenderer },
			}, parent, id);
		}

		public static event Action LobbyInfoChanged = () => { };

		internal static void SyncLobbyInfo()
		{
			LobbyInfoChanged();
		}

		public static event Action BeforeGameStart = () => { };
		public static event Action AfterGameStart = () => { };
		internal static void StartGame(string uid, WorldType type)
		{
			var preview = ModData.MapCache[uid];
			if (preview.Status != MapStatus.Available)
				throw new InvalidDataException($"Invalid map uid: {uid}");

			StartGame(preview.ToMap(), type);
		}

		internal static void StartGame(Map map, WorldType type)
		{
			// Dispose of the old world before creating a new one.
			worldRenderer?.Dispose();

			Cursor?.SetCursor(null);
			BeforeGameStart();

			using (new PerfTimer("NewWorld"))
			{
				ModData.PrepareMap(map);

				// The depth buffer needs to be initialized with enough range to cover:
				//  - the height of the screen
				//  - the z-offset of tiles from MaxTerrainHeight below the bottom of the screen (pushed into view)
				//  - additional z-offset from actors on top of MaxTerrainHeight terrain
				//  - a small margin so that tiles rendered partially above the top edge of the screen aren't pushed behind the clip plane
				// We need an offset of mapGrid.MaximumTerrainHeight * mapGrid.TileSize.Height / 2 to cover the terrain height
				// and choose to use mapGrid.MaximumTerrainHeight * mapGrid.TileSize.Height / 4 for each of the actor and top-edge cases
				var margin = 0;
				if (map.Grid.EnableDepthBuffer)
					margin = map.Rules.TerrainInfo.TileSize.Height * map.Grid.MaximumTerrainHeight;

				Renderer.SetDepthMargin(margin);
				OrderManager.World = new World(map, ModData, OrderManager, type);
			}

			OrderManager.World.GameOver += FinishBenchmark;

			worldRenderer = new WorldRenderer(ModData, OrderManager.World);

			// Proactively collect memory during loading to reduce peak memory.
			GC.Collect();

			using (new PerfTimer("LoadComplete"))
				OrderManager.World.LoadComplete(worldRenderer);

			// Proactively collect memory during loading to reduce peak memory.
			GC.Collect();

			if (OrderManager.GameStarted)
				return;

			Ui.MouseFocusWidget = null;
			Ui.KeyboardFocusWidget = null;

			OrderManager.StartGame();
			worldRenderer.RefreshPalette();
			Cursor?.SetCursor(ChromeMetrics.Get<string>("DefaultCursor"));

			// Now loading is completed, now is the ideal time to run a GC and compact the LOH.
			if (!OperatingSystem.IsBrowser())
				GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
			GC.Collect();

			// PostLoadComplete is designed for anything that should trigger at the very end of loading.
			// e.g. audio notifications that the game is starting.
			OrderManager.World.PostLoadComplete(worldRenderer);

			AfterGameStart();

			// Hide transition overlay after all heavy loading is done.
			// The overlay was shown in a previous tick (at button click),
			// giving the browser time to paint it before the blocking work.
			HideTransitionOverlay?.Invoke();
		}

		public static void RestartGame()
		{
			var replay = OrderManager.Connection as ReplayConnection;
			var replayName = replay?.Filename;
			var lobbyInfo = OrderManager.LobbyInfo;

			// Reseed the RNG so this isn't an exact repeat of the last game
			lobbyInfo.GlobalSettings.RandomSeed = CosmeticRandom.Next();

			// Note: the map may have been changed on disk outside the game, changing its UID.
			// Use the updated UID if we have tracked the update instead of failing.
			lobbyInfo.GlobalSettings.Map = ModData.MapCache.GetUpdatedMap(lobbyInfo.GlobalSettings.Map);
			if (lobbyInfo.GlobalSettings.Map == null)
			{
				Disconnect();
				Ui.ResetAll();
				LoadShellMap();
				return;
			}

			var orders = new[]
			{
					Order.Command($"sync_lobby {lobbyInfo.Serialize()}"),
					Order.Command("startgame")
			};

			// Disconnect from the current game
			Disconnect();
			Ui.ResetAll();

			// Restart the game with the same replay/mission
			if (replay != null)
				JoinReplay(replayName);
			else
				CreateAndStartLocalServer(lobbyInfo.GlobalSettings.Map, orders);
		}

		public static void CreateAndStartLocalServer(string mapUID, IEnumerable<Order> setupOrders)
		{
			OrderManager om = null;

			void LobbyReady()
			{
				LobbyInfoChanged -= LobbyReady;
				foreach (var o in setupOrders)
					om.IssueOrder(o);
			}

			LobbyInfoChanged += LobbyReady;

			om = JoinServer(CreateLocalServer(mapUID), "");
		}

		public static bool IsHost
		{
			get
			{
				var id = OrderManager.Connection.LocalClientId;
				var client = OrderManager.LobbyInfo.ClientWithIndex(id);
				return client != null && client.IsAdmin;
			}
		}

		static Modifiers modifiers;
		public static Modifiers GetModifierKeys() { return modifiers; }
		internal static void HandleModifierKeys(Modifiers mods) { modifiers = mods; }

		public static void InitializeSettings(Arguments args)
		{
			Settings = new Settings(Path.Combine(Platform.SupportDir, "settings.yaml"), args);
		}

		public static RunStatus InitializeAndRun(string[] args)
		{
			Initialize(new Arguments(args));

			// Proactively collect memory during loading to reduce peak memory.
			GC.Collect();
			return Run();
		}

		static void Initialize(Arguments args)
		{
			var engineDirArg = args.GetValue("Engine.EngineDir", null);
			if (!string.IsNullOrEmpty(engineDirArg))
				Platform.OverrideEngineDir(engineDirArg);

			var supportDirArg = args.GetValue("Engine.SupportDir", null);
			if (!string.IsNullOrEmpty(supportDirArg))
				Platform.OverrideSupportDir(supportDirArg);

			Console.WriteLine($"Platform is {Platform.CurrentPlatform} ({Platform.CurrentArchitecture})");

			// Load the engine version as early as possible so it can be written to exception logs
			try
			{
				EngineVersion = File.ReadAllText(Path.Combine(Platform.EngineDir, "VERSION")).Trim();
			}
			catch { }

			if (string.IsNullOrEmpty(EngineVersion))
				EngineVersion = "Unknown";

			Console.WriteLine($"Engine version is {EngineVersion}");
			Console.WriteLine($"Runtime: {Platform.RuntimeVersion}");

			// Special case handling of Game.Mod argument: if it matches a real filesystem path
			// then we use this to override the mod search path, and replace it with the mod id
			var modID = args.GetValue("Game.Mod", null);
			var explicitModPaths = Array.Empty<string>();
			if (modID != null && (File.Exists(modID) || Directory.Exists(modID)))
			{
				explicitModPaths = [modID];
				modID = Path.GetFileNameWithoutExtension(modID);
			}

			InitializeSettings(args);

			Log.AddChannel("perf", "perf.log");
			Log.AddChannel("debug", "debug.log");
			Log.AddChannel("server", "server.log", true);
			Log.AddChannel("sound", "sound.log");
			Log.AddChannel("graphics", "graphics.log");
			Log.AddChannel("geoip", "geoip.log");
			Log.AddChannel("nat", "nat.log");
			Log.AddChannel("client", "client.log");

			Nat.Initialize();

			var modSearchArg = args.GetValue("Engine.ModSearchPaths", null);
			var modSearchPaths = modSearchArg != null ?
				FieldLoader.GetValue<ImmutableArray<string>>("Engine.ModsPath", modSearchArg) :
				[Path.Combine(Platform.EngineDir, "mods")];

			Mods = new InstalledMods(modSearchPaths, explicitModPaths);
			Console.WriteLine("Internal mods:");
			foreach (var mod in Mods)
				Console.WriteLine($"\t{mod.Key} ({mod.Value.Metadata.Version})");

			modLaunchWrapper = args.GetValue("Engine.LaunchWrapper", null);

			ExternalMods = new ExternalMods();

			if (modID == null)
				throw new InvalidOperationException("Game.Mod argument missing.");

			if (Mods.TryGetValue(modID, out var manifest))
			{
				var launchPath = args.GetValue("Engine.LaunchPath", null);
				var launchArgs = new List<string>();

				// Sanitize input from platform-specific launchers
				// Process.Start requires paths to not be quoted, even if they contain spaces
				if (launchPath != null && launchPath[0] == '"' && launchPath[^1] == '"')
					launchPath = launchPath[1..^1];

				// Metadata registration requires an explicit launch path
				if (launchPath != null)
					ExternalMods.Register(Mods[modID], launchPath, launchArgs, ModRegistration.User);

				ExternalMods.ClearInvalidRegistrations(ModRegistration.User);
			}
			else
				throw new InvalidOperationException($"Unknown or invalid mod '{modID}'.");

			Console.WriteLine("External mods:");
			foreach (var mod in ExternalMods)
				Console.WriteLine($"\t{mod.Key} ({mod.Value.Version})");

			var platforms = new[] { Settings.Game.Platform, "Default", null };
			foreach (var p in platforms)
			{
				if (p == null)
					throw new InvalidOperationException("Failed to initialize platform-integration library. Check graphics.log for details.");

				Settings.Game.Platform = p;
				try
				{
					var platform = CreatePlatform(p);
					Renderer = new Renderer(platform, Settings.Graphics, manifest.RendererConstants.VertexBatchSize);
					Sound = new Sound(platform, Settings.Sound);

					break;
				}
				catch (Exception e)
				{
					Log.Write("graphics", $"{e}");
					Console.WriteLine("Renderer initialization failed. Check graphics.log for details.");

					Renderer?.Dispose();

					Sound?.Dispose();
				}
			}

			InitializeMod(manifest, args);
		}

		public static IPlatform RegisteredPlatform;

		public static IPlatform CreatePlatform(string platformName)
		{
			// Allow pre-registered platform (used by WASM where DLL loading is not available)
			if (RegisteredPlatform != null)
				return RegisteredPlatform;

			var rendererPath = Path.Combine(Platform.BinDir, "OpenRA.Platforms." + platformName + ".dll");

			var loader = new AssemblyLoader(rendererPath);
			var platformType = loader.LoadDefaultAssembly().GetTypes().SingleOrDefault(t => typeof(IPlatform).IsAssignableFrom(t));

			if (platformType == null)
				throw new InvalidOperationException("Platform dll must include exactly one IPlatform implementation.");

			return (IPlatform)platformType.GetConstructor(Type.EmptyTypes).Invoke(null);
		}

		public static void InitializeMod(Manifest manifest, Arguments args)
		{
			// Clear static state if we have switched mods
			LobbyInfoChanged = () => { };
			ConnectionStateChanged = (om, p, conn) => { };
			BeforeGameStart = () => { };
			OnRemoteDirectConnect = endpoint => { };
			delayedActions = new ActionQueue();

			Ui.ResetAll();

			worldRenderer?.Dispose();
			worldRenderer = null;
			server?.Shutdown();
			OrderManager?.Dispose();

			if (ModData != null)
			{
				ModData.ModFiles.UnmountAll();
				ModData.Dispose();
			}

			ModData = null;

			Console.WriteLine($"Loading mod: {manifest.Id}");

			Sound.StopVideo();

			Console.WriteLine("[init] Creating ModData...");
			ModData = new ModData(manifest, Mods, true);
			Console.WriteLine("[init] ModData created");

			LocalPlayerProfile = new LocalPlayerProfile(Path.Combine(Platform.SupportDir, Settings.Game.AuthProfile), ModData.GetOrCreate<PlayerDatabase>());

			// On WASM/browser, skip the content installation check — content is bundled
			if (!OperatingSystem.IsBrowser() && !ModData.LoadScreen.BeforeLoad(ModData))
				return;

			Console.WriteLine("[init] InitializeLoaders...");
			ModData.InitializeLoaders(ModData.DefaultFileSystem);
			Console.WriteLine("[init] InitializeFonts...");
			Renderer.InitializeFonts(ModData);

			Console.WriteLine("[init] LoadMaps...");
			if (OperatingSystem.IsBrowser())
			{
				Console.WriteLine("[init] WASM: deferring map loading to WasmMain (async with progress)");
				// Maps will be loaded asynchronously by WasmMain.Initialize() after this returns.
				// This allows the browser event loop to run between map batches, keeping the UI responsive.
			}
			else
				using (new PerfTimer("LoadMaps"))
					ModData.MapCache.LoadMaps(ModData);
			Console.WriteLine("[init] Maps loaded");

			Cursor?.Dispose();
			Console.WriteLine("[init] Creating CursorManager...");
			try
			{
				Cursor = new CursorManager(ModData);
				Console.WriteLine("[init] CursorManager created");
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"[init] CursorManager failed: {ex.Message}");
				if (OperatingSystem.IsBrowser())
					Console.Error.WriteLine("[init] Continuing without CursorManager on WASM");
				else
					throw;
			}

			var metadata = ModData.Manifest.Metadata;
			if (!string.IsNullOrEmpty(metadata.WindowTitleTranslated))
				Renderer.Window.SetWindowTitle(metadata.WindowTitleTranslated);

			PerfHistory.Items["render"].HasNormalTick = false;
			PerfHistory.Items["batches"].HasNormalTick = false;
			PerfHistory.Items["render_world"].HasNormalTick = false;
			PerfHistory.Items["render_widgets"].HasNormalTick = false;
			PerfHistory.Items["render_flip"].HasNormalTick = false;
			PerfHistory.Items["terrain_lighting"].HasNormalTick = false;

			Console.WriteLine("[init] JoinLocal...");
			JoinLocal();
			Console.WriteLine("[init] StartGame...");
			ModData.LoadScreen.StartGame(args);
		}

		public static void LoadEditor(string uid)
		{
			JoinLocal();
			StartGame(uid, WorldType.Editor);
		}

		public static void LoadEditor(Map map)
		{
			JoinLocal();
			StartGame(map, WorldType.Editor);
		}

		public static void LoadShellMap()
		{
			// On WASM, try to load shell map but fall back gracefully
			if (OperatingSystem.IsBrowser())
			{
				try
				{
					var shellmap = ChooseShellmap();
					Console.WriteLine($"[init] Loading shell map: {shellmap}");
					using (new PerfTimer("StartGame"))
					{
						StartGame(shellmap, WorldType.Shellmap);
						OnShellmapLoaded();
					}

					return;
				}
				catch (Exception ex)
				{
					Console.Error.WriteLine($"[init] Shell map failed: {ex.Message}");
					Console.Error.WriteLine($"[init] Shell map stack: {ex.StackTrace}");
					OnShellmapLoaded();
					return;
				}
			}

			var shellmap2 = ChooseShellmap();
			using (new PerfTimer("StartGame"))
			{
				StartGame(shellmap2, WorldType.Shellmap);
				OnShellmapLoaded();
			}
		}

		static string ChooseShellmap()
		{
			var shellmaps = ModData.MapCache
				.Where(m => m.Status == MapStatus.Available && m.Visibility.HasFlag(MapVisibility.Shellmap))
				.Select(m => m.Uid);

			var shellmap = shellmaps.RandomOrDefault(CosmeticRandom);
			if (shellmap == null)
				throw new InvalidDataException("No valid shellmaps available");

			return shellmap;
		}

		// WASM shell map loading broken into discrete steps for async progress reporting.
		// Each step is called separately from WasmMain with browser yields between them
		// so the progress bar updates visually during the long loading phase.

		public static Map ShellMapPrepareAssets()
		{
			var uid = ChooseShellmap();
			Console.WriteLine($"[init] Loading shell map: {uid}");

			var preview = ModData.MapCache[uid];
			if (preview.Status != MapStatus.Available)
				throw new InvalidDataException($"Invalid shellmap uid: {uid}");

			var map = preview.ToMap();

			worldRenderer?.Dispose();
			Cursor?.SetCursor(null);
			BeforeGameStart();

			using (new PerfTimer("PrepareMap"))
				ModData.PrepareMap(map);

			return map;
		}

		public static void ShellMapCreateWorld(Map map)
		{
			using (new PerfTimer("NewWorld"))
			{
				var margin = 0;
				if (map.Grid.EnableDepthBuffer)
					margin = map.Rules.TerrainInfo.TileSize.Height * map.Grid.MaximumTerrainHeight;

				Renderer.SetDepthMargin(margin);
				OrderManager.World = new World(map, ModData, OrderManager, WorldType.Shellmap);
			}

			OrderManager.World.GameOver += FinishBenchmark;
		}

		public static void ShellMapCreateRenderer()
		{
			worldRenderer = new WorldRenderer(ModData, OrderManager.World);
			GC.Collect();
		}

		public static void ShellMapLoadComplete()
		{
			using (new PerfTimer("LoadComplete"))
				OrderManager.World.LoadComplete(worldRenderer);
			GC.Collect();
		}

		public static void ShellMapFinalize()
		{
			if (!OrderManager.GameStarted)
			{
				Ui.MouseFocusWidget = null;
				Ui.KeyboardFocusWidget = null;

				OrderManager.StartGame();
				worldRenderer.RefreshPalette();
				Cursor?.SetCursor(ChromeMetrics.Get<string>("DefaultCursor"));
			}

			if (!OperatingSystem.IsBrowser())
				GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
			GC.Collect();

			OrderManager.World.PostLoadComplete(worldRenderer);
			AfterGameStart();
			HideTransitionOverlay?.Invoke();
			OnShellmapLoaded();
		}

		public static void NotifyShellmapLoaded() => OnShellmapLoaded();

		public static void SwitchToExternalMod(ExternalMod mod, string[] launchArguments = null, Action onFailed = null)
		{
			try
			{
				var path = mod.LaunchPath;
				var args = launchArguments != null ? mod.LaunchArgs.Append(launchArguments) : mod.LaunchArgs;
				if (modLaunchWrapper != null)
				{
					path = modLaunchWrapper;
					args = new[] { mod.LaunchPath }.Concat(args);
				}

				var p = Process.Start(path, args.Select(a => "\"" + a + "\"").JoinWith(" "));
				if (p == null || p.HasExited)
					onFailed();
				else
				{
					p.Close();
					Exit();
				}
			}
			catch (Exception e)
			{
				Log.Write("debug", "Failed to switch to external mod.");
				Log.Write("debug", "Error was: " + e.Message);
				onFailed();
			}
		}

		static RunStatus state = RunStatus.Running;
		public static event Action OnQuit = () => { };

		// Note: These delayed actions should only be used by widgets or disposing objects
		// - things that depend on a particular world should be queuing them on the world actor.
		static volatile ActionQueue delayedActions = new();

		public static void RunAfterTick(Action a) { delayedActions.Add(a, RunTime); }
		public static void RunAfterDelay(int delayMilliseconds, Action a) { delayedActions.Add(a, RunTime + delayMilliseconds); }

		static void TakeScreenshotInner()
		{
			using (new PerfTimer("Renderer.SaveScreenshot"))
			{
				var mod = ModData.Manifest.Metadata;
				var directory = Path.Combine(Platform.SupportDir, "Screenshots", ModData.Manifest.Id, mod.Version);
				Directory.CreateDirectory(directory);

				var filename = TimestampedFilename(true);
				var path = Path.Combine(directory, $"{filename}.png");
				Log.Write("debug", "Taking screenshot " + path);

				Renderer.SaveScreenshot(path);
				TextNotificationsManager.Debug(FluentProvider.GetMessage(SavedScreenshot, "filename", filename));
			}
		}

		static void InnerLogicTick(OrderManager orderManager)
		{
			var tick = RunTime;

			var world = orderManager.World;

			if (Ui.LastTickTime.ShouldAdvance(tick))
			{
				Ui.LastTickTime.AdvanceTickTime(tick);
				Sync.RunUnsynced(world, Ui.Tick);
				Cursor?.Tick();
			}

			if (orderManager.LastTickTime.ShouldAdvance(tick))
			{
				if (orderManager.GameStarted && orderManager.LocalFrameNumber == 0)
					PerfHistory.Reset(); // Remove history that occurred whilst the new game was loading.

				using (var sample = new PerfSample("tick_time"))
				{
					orderManager.LastTickTime.AdvanceTickTime(tick);

					Sound.Tick();

					Sync.RunUnsynced(world, orderManager.TickImmediate);

					if (world == null)
					{
						if (orderManager.GameStarted)
							PerfHistory.Reset(); // Remove old history when a new game starts.
						return;
					}

					if (orderManager.TryTick())
					{
						Sync.RunUnsynced(world, () => world.OrderGenerator.Tick(world));

						world.Tick();

						PerfHistory.Tick(!world.Paused);
					}

					// Wait until we have done our first world Tick before TickRendering
					if (orderManager.LocalFrameNumber > 0)
						Sync.RunUnsynced(world, () => world.TickRender(worldRenderer));
				}

				benchmark?.Tick(LocalTick);
			}
		}

		static void LogicTick()
		{
			PerformDelayedActions();

			if (OrderManager.Connection is NetworkConnection nc && nc.ConnectionState != lastConnectionState)
			{
				lastConnectionState = nc.ConnectionState;
				ConnectionStateChanged(OrderManager, null, nc);
			}
			else if (OrderManager.Connection is not NetworkConnection && OrderManager.Connection is not EchoConnection
				&& OrderManager.Connection.ConnectionState != lastConnectionState)
			{
				lastConnectionState = OrderManager.Connection.ConnectionState;
				ConnectionStateChanged(OrderManager, null, null);
			}

			InnerLogicTick(OrderManager);
			if (worldRenderer != null && OrderManager.World != worldRenderer.World)
				InnerLogicTick(worldRenderer.World.OrderManager);

			// Tick in-process server for WASM/headless skirmish (after client tick).
			// This processes the orders the client just sent and queues ACKs
			// for the next tick's Receive(), keeping frames synchronized.
			if ((OperatingSystem.IsBrowser() || HeadlessBotMode) && server != null)
			{
				try
				{
					server.TickInProcess();

					// Drain outbound data from remote guest connections (browser-hosted MP)
					TickRemoteConnections?.Invoke();
				}
				catch (Exception ex)
				{
					Console.Error.WriteLine($"[InProcess] Tick error: {ex}");
					Log.Write("server", $"[InProcess] Tick error: {ex}");
				}
			}
		}

		public static void PerformDelayedActions()
		{
			delayedActions.PerformActions(RunTime);
		}

		public static void TakeScreenshot()
		{
			takeScreenshot = true;
		}

		static void RenderTick()
		{
			using (new PerfSample("render"))
			{
				++RenderFrame;

				// Prepare renderables (i.e. render voxels) before calling BeginFrame
				using (new PerfSample("render_prepare"))
				{
					worldRenderer?.BeginFrame();

					// World rendering is disabled while the loading screen is displayed
					if (worldRenderer != null && !worldRenderer.World.IsLoadingGameSave)
					{
						worldRenderer.Viewport.Tick();
						worldRenderer.PrepareRenderables();
					}

					Ui.PrepareRenderables();
					worldRenderer?.EndFrame();
				}

				// worldRenderer is null during the initial install/download screen
				// World rendering is disabled while the loading screen is displayed
				// Use worldRenderer.World instead of OrderManager.World to avoid a rendering mismatch while processing orders
				if (worldRenderer != null && !worldRenderer.World.IsLoadingGameSave)
				{
					Renderer.BeginWorld(worldRenderer.Viewport.CenterLocation, worldRenderer.Viewport.ViewportSize);
					Sound.SetListenerPosition(worldRenderer.Viewport.CenterPosition);
					using (new PerfSample("render_world"))
						worldRenderer.Draw();
				}

				using (new PerfSample("render_widgets"))
				{
					Renderer.BeginUI();

					if (worldRenderer != null && !worldRenderer.World.IsLoadingGameSave)
						worldRenderer.DrawAnnotations();

					Ui.Draw();

					if (HideCursor)
						Cursor?.SetCursor(null);
					else
					{
						Cursor?.SetCursor(Ui.Root.GetCursorOuter(Viewport.LastMousePos) ?? "default");
						Cursor?.Render(Renderer);
					}
				}

				using (new PerfSample("render_flip"))
					Renderer.EndFrame(new DefaultInputHandler(OrderManager.World));

				if (takeScreenshot)
				{
					takeScreenshot = false;
					TakeScreenshotInner();
				}
			}

			var isActive = !(worldRenderer?.World.Paused ?? true);
			PerfHistory.Items["render"].Tick(isActive);
			PerfHistory.Items["batches"].Tick(isActive);
			PerfHistory.Items["render_world"].Tick(isActive);
			PerfHistory.Items["render_widgets"].Tick(isActive);
			PerfHistory.Items["render_flip"].Tick(isActive);
			PerfHistory.Items["terrain_lighting"].Tick(isActive);
		}

		/// <summary>Get player economy/combat stats via reflection (Game.cs can't reference Mods.Common types).</summary>
		static (int Earned, int Spent, int KillsCost, int DeathsCost, int ArmyValue, int AssetsValue, int Income) GetPlayerStats(Player p)
		{
			var earned = 0; var spent = 0; var killsCost = 0; var deathsCost = 0;
			var armyValue = 0; var assetsValue = 0; var income = 0;

			// PlayerResources implements ISync (OpenRA.Game), get Earned/Spent
			foreach (var trait in p.PlayerActor.TraitsImplementing<ISync>())
			{
				var t = trait.GetType();
				if (t.Name != "PlayerResources")
					continue;
				earned = (int)(t.GetField("Earned")?.GetValue(trait) ?? 0);
				spent = (int)(t.GetField("Spent")?.GetValue(trait) ?? 0);
				break;
			}

			// PlayerStatistics implements ITick (OpenRA.Traits), get combat stats
			foreach (var trait in p.PlayerActor.TraitsImplementing<Traits.ITick>())
			{
				var t = trait.GetType();
				if (t.Name != "PlayerStatistics")
					continue;
				killsCost = (int)(t.GetField("KillsCost")?.GetValue(trait) ?? 0);
				deathsCost = (int)(t.GetField("DeathsCost")?.GetValue(trait) ?? 0);
				armyValue = (int)(t.GetField("ArmyValue")?.GetValue(trait) ?? 0);
				assetsValue = (int)(t.GetField("AssetsValue")?.GetValue(trait) ?? 0);
				income = (int)(t.GetField("Income")?.GetValue(trait) ?? 0);
				break;
			}

			return (earned, spent, killsCost, deathsCost, armyValue, assetsValue, income);
		}

		static void Loop()
		{
			Console.Error.WriteLine("[headless] Loop() entered");
			var loopLogTimer = RunTime;
			var headlessBots = !string.IsNullOrEmpty(new LaunchArguments(new Arguments(Environment.GetCommandLineArgs())).Bots);
			var gameOverReported = false;
			var lastSnapTick = 0;
			// The game loop mainly does two things: logic updates and
			// drawing on the screen.
			// ---
			// We ideally want the logic to run every 'Timestep' ms and
			// rendering to be done at 'MaxFramerate', so 1000 / MaxFramerate ms.
			// Any additional free time is used in 'Sleep' so we don't
			// consume more CPU/GPU resources than necessary.
			// ---
			// In case logic or rendering takes more time than the ideal
			// and we're getting behind, we can skip rendering some frames
			// but there's a fail-safe minimum FPS to make sure the screen
			// gets updated at least that often.
			// ---
			// TODO: Separate world/UI rendering
			// It would be nice to separate the world rendering from the UI rendering
			// so that we can update the UI more often than the world. This would
			// help make the game playable (mouse/controls) even in low world
			// framerates.
			// It's not possible at the moment because the render buffer is cleared
			// before rendering and we don't keep the last rendered world buffer.

			// When the logic has fallen behind by this much, skip the pending
			// updates and start fresh.
			// For example, if we want to update logic every 10 ms but each loop
			// temporarily takes 100 ms, the 'nextLogic' timestamp will be too low
			// and the current timestamp ('now') will have moved on. Even if the
			// update time returns to normal, it will take a long time to catch up
			// (if ever).
			// This also means that the 'logicInterval' cannot be longer than this
			// value.
			const int MaxLogicTicksBehind = 250;

			// Try to maintain at least this many FPS during replays, even if it slows down logic.
			// However, if the user has enabled a framerate limit that is even lower
			// than this, then that limit will be used.
			const int MinReplayFps = 10;

			// Timestamps for when the next logic and rendering should run
			var nextLogic = RunTime;
			var nextRender = RunTime;
			var forcedNextRender = RunTime;
			var renderBeforeNextTick = false;

			while (state == RunStatus.Running)
			{
				// Headless bot mode: skip all timing, run logic as fast as possible
				if (headlessBots)
				{
					// Periodic headless diagnostic logging
					var now0 = RunTime;
					if (now0 - loopLogTimer > 5000)
					{
						loopLogTimer = now0;
						var gs = OrderManager?.GameStarted ?? false;
						var frame = OrderManager?.NetFrameNumber ?? -1;
						Console.Error.WriteLine($"[headless] frame={frame} gameStarted={gs} world={OrderManager?.World != null}");
					}

					// Periodic telemetry snapshots (every 2000 ticks) for phase-aware AI evaluation
					if (OrderManager?.World != null && !gameOverReported)
					{
						var snapWorld = OrderManager.World;
						var tick = snapWorld.WorldTick;
						if (tick > 0 && tick % 2000 == 0 && tick != lastSnapTick)
						{
							lastSnapTick = tick;
							foreach (var p in snapWorld.Players)
							{
								if (p.InternalName == "Everyone" || p.InternalName == "Neutral" || p.InternalName == "Creeps")
									continue;
								var stats = GetPlayerStats(p);
								Console.WriteLine($"SNAP:{tick}:{p.InternalName}|earned={stats.Earned}|spent={stats.Spent}|army={stats.ArmyValue}|assets={stats.AssetsValue}|kills={stats.KillsCost}|deaths={stats.DeathsCost}|income={stats.Income}");
							}
						}
					}

					// Auto-exit when bot game is over
					if (!gameOverReported && OrderManager?.World != null && OrderManager.World.IsGameOver)
					{
						gameOverReported = true;
						var world = OrderManager.World;
						Console.WriteLine("GAME_OVER");
						Console.WriteLine($"FRAME:{world.WorldTick}");
						foreach (var p in world.Players)
						{
							if (p.InternalName == "Everyone" || p.InternalName == "Neutral" || p.InternalName == "Creeps")
								continue;
							var stats = GetPlayerStats(p);
							Console.WriteLine($"PLAYER:{p.InternalName}|{p.PlayerName}|{p.WinState}|{p.Faction.InternalName}|earned={stats.Earned}|spent={stats.Spent}|killCost={stats.KillsCost}|deathCost={stats.DeathsCost}|army={stats.ArmyValue}|assets={stats.AssetsValue}");
						}

						Console.Error.WriteLine($"[headless] Game over at frame {world.WorldTick}, exiting.");
						// Finalize replay before shutdown (in-process server has no background thread)
						server?.EndGame();
						Disconnect();
						state = RunStatus.Success;
						continue;
					}

					// Force tick time to always advance (bypass wall-clock rate limiting)
					OrderManager.LastTickTime.Value = 0;
					Ui.LastTickTime.Value = 0;

					LogicTick();
					continue;
				}

				var logicInterval = Ui.Timestep;
				var logicWorld = worldRenderer?.World;

				// ReplayTimestep = 0 means the replay is paused: we need to keep logicInterval as UI.Timestep to avoid breakage
				if (logicWorld != null && (!logicWorld.IsReplay || logicWorld.ReplayTimestep != 0))
					logicInterval = logicWorld == OrderManager.World ? OrderManager.SuggestedTimestep : logicWorld.Timestep;

				// Ideal time between screen updates
				var renderInterval = logicInterval;
				if (!Settings.Graphics.CapFramerateToGameFps)
				{
					var maxFramerate = Settings.Graphics.CapFramerate ? Settings.Graphics.MaxFramerate.Clamp(1, 1000) : 1000;
					renderInterval = 1000 / maxFramerate;
				}

				// Tick as fast as possible while restoring game saves, capping rendering at 5 FPS
				if (OrderManager.World != null && OrderManager.World.IsLoadingGameSave)
				{
					logicInterval = 1;
					renderInterval = 200;
				}

				var now = RunTime;

				// If the logic has fallen behind too much, skip it and catch up
				if (now - nextLogic > MaxLogicTicksBehind)
					nextLogic = now;

				// When's the next update (logic or render)
				var nextUpdate = Math.Min(nextLogic, nextRender);
				if (now >= nextUpdate)
				{
					var forceRender = renderBeforeNextTick || now >= forcedNextRender;

					if (now >= nextLogic && !renderBeforeNextTick)
					{
						nextLogic += logicInterval;

						LogicTick();

						// Force at least one render per tick during regular gameplay
						if (OrderManager.World != null && !OrderManager.World.IsLoadingGameSave && !OrderManager.World.IsReplay)
							renderBeforeNextTick = true;
					}

					var haveSomeTimeUntilNextLogic = now < nextLogic;
					var isTimeToRender = now >= nextRender;
					if (!Renderer.WindowIsSuspended)
					{
						if (isTimeToRender || forceRender)
						{
							if (haveSomeTimeUntilNextLogic || forceRender)
								RenderTick();

							nextRender = now + renderInterval;

							// Pick the minimum allowed FPS (the lower between 'minReplayFPS'
							// and the user's max frame rate) and convert it to maximum time
							// allowed between screen updates.
							// We do this before rendering to include the time rendering takes
							// in this interval.
							var maxRenderInterval = Math.Max(1000 / MinReplayFps, renderInterval);
							forcedNextRender = now + maxRenderInterval;

							renderBeforeNextTick = false;
						}
					}
					else
					{
						// Simulate a render tick if it was time to render but we skip actually rendering
						if (isTimeToRender || forceRender)
						{
							// Make sure that nextUpdate is set to a proper minimum interval
							nextRender = now + renderInterval;

							// Still process SDL events to allow a restore to come through
							Renderer.Window.PumpInput(new NullInputHandler());

							// Ensure that we still logic tick despite not rendering
							renderBeforeNextTick = false;
						}
						else
						{
							// Avoid busy wait.
							Thread.Sleep((int)(nextRender - now));
						}
					}
				}
				else
					Thread.Sleep((int)(nextUpdate - now));
			}
		}

		static RunStatus Run()
		{
			if (Settings.Graphics.MaxFramerate < 1)
			{
				Settings.Graphics.MaxFramerate = new GraphicSettings().MaxFramerate;
				Settings.Graphics.CapFramerate = false;
			}

			try
			{
				Loop();
			}
			finally
			{
				// Ensure that the active replay is properly saved
				OrderManager?.Dispose();
			}

			worldRenderer?.Dispose();
			ModData.Dispose();
			ChromeProvider.Deinitialize();

			Sound.Dispose();
			Renderer.Dispose();

			OnQuit();

			return state;
		}

		public static void Exit()
		{
			state = RunStatus.Success;
		}

		public static void Disconnect()
		{
			OrderManager.World?.TraitDict.PrintReport();

			OrderManager.Dispose();
			CloseServer();
			JoinLocal();
		}

		public static void CloseServer()
		{
			server?.Shutdown();
		}

		public static T CreateObject<T>(string name)
		{
			return ModData.ObjectCreator.CreateObject<T>(name);
		}

		public static ConnectionTarget CreateServer(ServerSettings settings)
		{
			if (OperatingSystem.IsBrowser())
			{
				// WASM: master server is unreachable from browser (CORS), disable to prevent error spam
				settings.AdvertiseOnline = false;

				// WASM: create an in-process server + room for browser-hosted MP
				server = new Server.Server(settings, ModData, ServerType.Multiplayer);

				// Accept the host as an in-process connection
				var serverConn = server.AcceptInProcessConnection();
				PendingInProcessConnection = new InProcessClientConnection(server, serverConn);

				// Create a relay room so remote guests can connect
				CreateRelayRoom?.Invoke();

				// Return dummy target (in-process connection used instead)
				return new ConnectionTarget(new[] { new DnsEndPoint("127.0.0.1", 0) });
			}

			var endpoints = new List<IPEndPoint>
			{
				new(IPAddress.IPv6Any, settings.ListenPort),
				new(IPAddress.Any, settings.ListenPort)
			};
			server = new Server.Server(endpoints, settings, ModData, ServerType.Multiplayer);

			return server.GetEndpointForLocalConnection();
		}

		public static ConnectionTarget CreateLocalServer(string map, bool isSkirmish = false)
		{
			var settings = new ServerSettings()
			{
				Name = "Skirmish Game",
				Map = map,
				AdvertiseOnline = false,
				AdvertiseOnLocalNetwork = !isSkirmish
			};

			if (OperatingSystem.IsBrowser() || HeadlessBotMode)
			{
				// WASM/headless: create an in-process server (no TCP, no threads)
				server = new Server.Server(settings, ModData, isSkirmish ? ServerType.Skirmish : ServerType.Local);

				// Accept in-process connection (validates client + runs lobby traits)
				var serverConn = server.AcceptInProcessConnection();

				// Store the client-side connection for JoinServer to pick up
				PendingInProcessConnection = new InProcessClientConnection(server, serverConn);

				// Return a dummy connection target (not used for in-process)
				return new ConnectionTarget(new[] { new DnsEndPoint("127.0.0.1", 0) });
			}

			// Always connect to local games using the same loopback connection
			// Exposing multiple endpoints introduces a race condition on the client's PlayerIndex (sometimes 0, sometimes 1)
			// This would break the Restart button, which relies on the PlayerIndex always being the same for local servers
			var endpoints = new List<IPEndPoint>
			{
				new(IPAddress.Loopback, 0)
			};
			server = new Server.Server(endpoints, settings, ModData, isSkirmish ? ServerType.Skirmish : ServerType.Local);

			return server.GetEndpointForLocalConnection();
		}

		public static bool IsCurrentWorld(World world)
		{
			return OrderManager != null && OrderManager.World == world && !world.Disposing;
		}

		public static bool SetClipboardText(string text)
		{
			return Renderer.Window.SetClipboardText(text);
		}

		public static void BenchmarkMode(string prefix)
		{
			benchmark = new Benchmark(prefix);
		}

		public static void LoadMap(string launchMap, string bots = null)
		{
			var orders = new List<Order>
			{
				Order.Command("option gamespeed fastest"),
			};

			// If bots are specified, set up a spectator bot-only game
			if (!string.IsNullOrEmpty(bots))
			{
				HeadlessBotMode = true;
				var botTypes = bots.Split(',');
				orders.Add(Order.Command("spectate"));
				for (var i = 0; i < botTypes.Length; i++)
					orders.Add(Order.Command($"slot_bot Multi{i} 0 {botTypes[i].Trim()}"));
			}

			orders.Add(Order.Command($"state {Session.ClientState.Ready}"));

			var map = ModData.MapCache.SingleOrDefault(m => m.Uid == launchMap || Path.GetFileName(m.Path) == launchMap);
			if (map == null)
				throw new ArgumentException($"Could not find map '{launchMap}'.");

			Console.Error.WriteLine($"[headless] LoadMap: {launchMap} uid={map.Uid} bots={bots} inProcess={HeadlessBotMode}");
			CreateAndStartLocalServer(map.Uid, orders);
		}

		public static void FinishBenchmark()
		{
			if (benchmark != null)
			{
				benchmark.Write();
				Exit();
			}
		}
	}

	public static class CurrentServerSettings
	{
		public static string Password;
		public static ConnectionTarget Target;
		public static ExternalMod ServerExternalMod;
	}
}
