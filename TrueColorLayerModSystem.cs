using System.Reflection;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace TrueColorLayer
{
    // ─── Configuration ────────────────────────────────────────────────────────
    public class TrueColorLayerConfig
    {
        public bool ReplaceDefaultMap { get; set; } = false;
    }

    // ─── Harmony patch: tab order and replacement ─────────────────────────────
    [HarmonyPatch]
    public static class TrueColorLayerTabOrderPatch
    {
        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            return typeof(WorldMapManager)
                .GetMethod("getTabsOrdered",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        }

        [HarmonyPostfix]
        public static void Postfix(ref List<string> __result)
        {
            if (__result == null) return;
            int i = __result.FindIndex(x => x == "terrain");
            __result.Remove("truecolorlayer");

            if (TrueColorLayerModSystem.Config != null && TrueColorLayerModSystem.Config.ReplaceDefaultMap)
            {
                if (i >= 0)
                {
                    __result.Insert(i, "truecolorlayer");
                    __result.Remove("terrain"); // Hide the original paper map
                }
                else
                {
                    __result.Add("truecolorlayer");
                }
            }
            else
            {
                __result.Insert(i >= 0 ? Math.Min(i + 1, __result.Count) : __result.Count, "truecolorlayer");
            }
        }
    }

    // ─── Harmony patch: use a separate SQLite DB for the Accurate Map ────────
    [HarmonyPatch(typeof(ChunkMapLayer), nameof(ChunkMapLayer.getMapDbFilePath))]
    public static class TrueColorLayerDbPatch
    {
        public static void Postfix(ChunkMapLayer __instance, ref string __result)
        {
            if (__instance is AccurateColorMapLayer)
            {
                __result = __result.Replace(".db", "-accurate.db");
            }
        }
    }

    // ─── Map layer: Inherit the original map renderer but force color mode ─────
    public class AccurateColorMapLayer : ChunkMapLayer
    {
        public override string Title => "True Color";
        public override string LayerGroupCode => "truecolorlayer";

        public AccurateColorMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink)
        {
        }

        public override void OnMapOpenedClient()
        {
            base.OnMapOpenedClient();
            
            // Force the original renderer to use color-accurate mode for this specific layer
            var field = typeof(ChunkMapLayer).GetField("colorAccurate", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null)
            {
                field.SetValue(this, true);
            }
        }

        public override void OnMouseMoveClient(MouseEvent args, GuiElementMap mapElem, StringBuilder hoverText)
        {
            base.OnMouseMoveClient(args, mapElem, hoverText);

            var capi = mapElem.Api as ICoreClientAPI;
            if (capi == null) return;

            Vec3d worldPos = new Vec3d();
            mapElem.TranslateViewPosToWorldPos(new Vec2f(args.X, args.Y), ref worldPos);
            
            BlockPos pos = new BlockPos((int)worldPos.X, 0, (int)worldPos.Z);
            
            // Find surface Y
            IMapChunk mc = capi.World.BlockAccessor.GetMapChunk(pos.X / GlobalConstants.ChunkSize, pos.Z / GlobalConstants.ChunkSize);
            if (mc != null)
            {
                pos.Y = mc.RainHeightMap[(pos.Z % GlobalConstants.ChunkSize) * GlobalConstants.ChunkSize + (pos.X % GlobalConstants.ChunkSize)];
            }
            else
            {
                pos.Y = capi.World.BlockAccessor.GetTerrainMapheightAt(pos);
            }

            if (pos.Y > 0)
            {
                ClimateCondition cond = capi.World.BlockAccessor.GetClimateAt(pos, EnumGetClimateMode.ForSuppliedDate_TemperatureOnly, capi.World.Calendar.TotalDays);
                if (cond != null)
                {
                    hoverText.AppendLine($"Temp: {Math.Round(cond.Temperature, 1)}°C, Rainfall: {Math.Round(cond.Rainfall * 100)}%");
                }
            }
        }
    }

    // ─── Mod entry point ──────────────────────────────────────────────────────
    public class TrueColorLayerModSystem : ModSystem
    {
        private const string PatchId = "truecolorlayer.patches";
        private Harmony? _harmony;
        public static TrueColorLayerConfig Config { get; private set; }

        public override void StartPre(ICoreAPI api)
        {
            base.StartPre(api);
            if (api.Side != EnumAppSide.Client) return;

            try
            {
                Config = api.LoadModConfig<TrueColorLayerConfig>("truecolorlayer.json") ?? new TrueColorLayerConfig();
            }
            catch
            {
                Config = new TrueColorLayerConfig();
            }
            api.StoreModConfig(Config, "truecolorlayer.json");

            if (Harmony.HasAnyPatches(PatchId)) return;
            _harmony = new Harmony(PatchId);
            try
            {
                _harmony.PatchAll(Assembly.GetExecutingAssembly());
                api.Logger.Notification("[TrueColorLayer] Harmony patches applied.");
            }
            catch (Exception ex)
            {
                api.Logger.Warning("[TrueColorLayer] Harmony patching failed: " + ex.Message);
            }
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            var mapMan = api.ModLoader.GetModSystem<WorldMapManager>();
            if (mapMan == null) return;
            mapMan.RegisterMapLayer<AccurateColorMapLayer>("truecolorlayer", 1.5);
            api.Logger.Notification("[TrueColorLayer] Map layer registered.");
        }

        public override void Dispose()
        {
            _harmony?.UnpatchAll(PatchId);
            base.Dispose();
        }
    }
}
