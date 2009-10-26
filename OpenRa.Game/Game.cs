using System.Collections.Generic;
using OpenRa.FileFormats;
using OpenRa.Game.Graphics;
using OpenRa.TechTree;
using System.Drawing;
using System.Linq;
using IrrKlang;
using IjwFramework.Collections;
using System;
using IjwFramework.Types;
using OpenRa.Game.Traits;
using OpenRa.Game.GameRules;

namespace OpenRa.Game
{
	static class Game
	{
		public static readonly int CellSize = 24;

		public static World world;
		public static Map map;
		static TreeCache treeCache;
		public static TerrainRenderer terrain;
		public static Viewport viewport;
		public static PathFinder pathFinder;
		public static Network network;
		public static WorldRenderer worldRenderer;
		public static Controller controller;

		static int localPlayerIndex;

		public static Dictionary<int, Player> players = new Dictionary<int, Player>();

		public static Player LocalPlayer { get { return players[localPlayerIndex]; } }
		public static BuildingInfluenceMap BuildingInfluence;
		public static UnitInfluenceMap UnitInfluence;

		static ISoundEngine soundEngine;

		public static void Initialize(string mapName, Renderer renderer, int2 clientSize, int localPlayer)
		{
			Rules.LoadRules(mapName);

			for (int i = 0; i < 8; i++)
				players.Add(i, new Player(i, string.Format("Multi{0}", i), Race.Soviet));

			localPlayerIndex = localPlayer;

			var mapFile = new IniFile(FileSystem.Open(mapName));
			map = new Map(mapFile);
			FileSystem.Mount(new Package(map.Theater + ".mix"));

			viewport = new Viewport(clientSize, map.Size, renderer);

			terrain = new TerrainRenderer(renderer, map, viewport);
			world = new World();
			treeCache = new TreeCache(map);

			foreach (TreeReference treeReference in map.Trees)
				world.Add(new Actor(treeReference, treeCache, map.Offset));

			BuildingInfluence = new BuildingInfluenceMap(8);
			UnitInfluence = new UnitInfluenceMap();

			LoadMapBuildings(mapFile);
			LoadMapUnits(mapFile);

			pathFinder = new PathFinder(map, terrain.tileSet);

			network = new Network();

			controller = new Controller();
			worldRenderer = new WorldRenderer(renderer);

			soundEngine = new ISoundEngine();
			sounds = new Cache<string, ISoundSource>(LoadSound);

			PlaySound("intro.aud", false);
		}

		static void LoadMapBuildings( IniFile mapfile )
		{
			foreach( var s in mapfile.GetSection( "STRUCTURES", true ) )
			{
				//num=owner,type,health,location,facing,trigger,unknown,shouldRepair
				var parts = s.Value.ToLowerInvariant().Split( ',' );
				var loc = int.Parse( parts[ 3 ] );
				world.Add( new Actor( parts[ 1 ], new int2( loc % 128 - map.Offset.X, loc / 128-map.Offset.Y ), players[ 0 ] ) );
			}
		}

		static void LoadMapUnits( IniFile mapfile )
		{
			foreach( var s in mapfile.GetSection( "UNITS", true ) )
			{
				//num=owner,type,health,location,facing,action,trigger
				var parts = s.Value.ToLowerInvariant().Split( ',' );
				var loc = int.Parse( parts[ 3 ] );
				world.Add( new Actor( parts[ 1 ], new int2( loc % 128 - map.Offset.X, loc / 128 - map.Offset.Y ), players[ 0 ] ) );
			}
		}

		static Cache<string, ISoundSource> sounds;

		static ISoundSource LoadSound(string filename)
		{
			var data = AudLoader.LoadSound(FileSystem.Open(filename));
			return soundEngine.AddSoundSourceFromPCMData(data, filename,
				new AudioFormat()
				{
					ChannelCount = 1,
					FrameCount = data.Length / 2,
					Format = SampleFormat.Signed16Bit,
					SampleRate = 22050
				});
		}

		public static void PlaySound(string name, bool loop)
		{
			var sound = sounds[name];
			// todo: positioning
			soundEngine.Play2D(sound, loop, false, false);
		}

		public static void Tick()
		{
			var stuffFromOtherPlayers = network.Tick();	// todo: actually use the orders!
			world.Update();
			UnitInfluence.Tick();

			viewport.DrawRegions();
		}

		public static bool IsCellBuildable(int2 a, UnitMovementType umt)
		{
			if (BuildingInfluence.GetBuildingAt(a) != null) return false;
			if (UnitInfluence.GetUnitAt(a) != null) return false;

			a += map.Offset;

			return map.IsInMap(a.X, a.Y) &&
				TerrainCosts.Cost(umt,
					terrain.tileSet.GetWalkability(map.MapTiles[a.X, a.Y])) < double.PositiveInfinity;
		}

		static IEnumerable<Actor> FindUnits(float2 a, float2 b)
		{
			var min = float2.Min(a, b);
			var max = float2.Max(a, b);

			var rect = new RectangleF(min.X, min.Y, max.X - min.X, max.Y - min.Y);

			return world.Actors
				.Where(x => x.Bounds.IntersectsWith(rect));
		}

		public static IEnumerable<Actor> FindUnitsInCircle(float2 a, float r)
		{
			return FindUnits(a - new float2(r, r), a + new float2(r, r))
				.Where(x => (x.CenterLocation - a).LengthSquared < r * r);
		}

		public static IEnumerable<int2> FindTilesInCircle(int2 a, int r)
		{
			var min = a - new int2(r, r);
			var max = a + new int2(r, r);

			for (var j = min.Y; j <= max.Y; j++)
				for (var i = min.X; i <= max.X; i++)
					if (r * r >= (new int2(i, j) - a).LengthSquared)
						yield return new int2(i, j);
		}

		public static IEnumerable<Actor> SelectUnitsInBox(float2 a, float2 b)
		{
			return FindUnits(a, b).Where(x => x.Owner == LocalPlayer && x.traits.Contains<Traits.Mobile>());
		}

		public static IEnumerable<Actor> SelectUnitOrBuilding(float2 a)
		{
			var q = FindUnits(a, a);
			return q.Where(x => x.traits.Contains<Traits.Mobile>()).Concat(q).Take(1);
		}

		public static int GetDistanceToBase(int2 b, Player p)
		{
			var building = BuildingInfluence.GetNearestBuilding(b);
			if (building == null || building.Owner != p)
				return int.MaxValue;

			return BuildingInfluence.GetDistanceToBuilding(b);
		}

		public static Random SharedRandom = new Random();		/* for things that require sync */
		public static Random CosmeticRandom = new Random();		/* for things that are just fluff */

		public static readonly Pair<VoicePool, VoicePool> SovietVoices =
			Pair.New(
				new VoicePool("ackno", "affirm1", "noprob", "overout", "ritaway", "roger", "ugotit"),
				new VoicePool("await1", "ready", "report1", "yessir1"));

		static int2? FindAdjacentTile(Actor a, UnitMovementType umt)
		{
			var tiles = Footprint.Tiles(a);
			var min = tiles.Aggregate(int2.Min) - new int2(1, 1);
			var max = tiles.Aggregate(int2.Max) + new int2(1, 1);

			for (var j = min.Y; j <= max.Y; j++)
				for (var i = min.X; i <= max.X; i++)
					if (IsCellBuildable(new int2(i, j), umt))
						return new int2(i, j);

			return null;
		}

		public static void BuildUnit(Player player, string name)
		{
			var producerTypes = Rules.UnitInfo[name].BuiltAt;
			var producer = world.Actors
				.FirstOrDefault(a => a.unitInfo != null 
					&& producerTypes.Contains(a.unitInfo.Name) && a.Owner == player);

			if (producer == null)
				throw new InvalidOperationException("BuildUnit without suitable production structure!");

			Actor unit;

			if (producerTypes.Contains("spen") || producerTypes.Contains("syrd"))
			{
				var space = FindAdjacentTile(producer, Rules.UnitInfo[name].WaterBound ? 
					UnitMovementType.Float : UnitMovementType.Wheel );	/* hackety hack */

				if (space == null)
					throw new NotImplementedException("Nowhere to place this unit.");

				unit = new Actor(name, space.Value, player);
				var mobile = unit.traits.Get<Mobile>();
				mobile.facing = SharedRandom.Next(256);
			}
			else
			{
				unit = new Actor(name, (1 / 24f * producer.CenterLocation).ToInt2(), player);
				var mobile = unit.traits.Get<Mobile>();
				mobile.facing = 128;
				mobile.QueueActivity(new Traits.Mobile.MoveTo(unit.Location + new int2(0, 3)));
			}

			world.AddFrameEndTask(_ => world.Add(unit));

			if (producer.traits.Contains<RenderWarFactory>())
				producer.traits.Get<RenderWarFactory>().EjectUnit();
		}
	}
}
