using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace TrueColorLayer
{
    // ─── Configuration ────────────────────────────────────────────────────────
    public class TrueColorLayerConfig
    {
        public bool ReplaceDefaultMap { get; set; } = false;
        public bool DisableSnowInWinter { get; set; } = true;
    }

    // ─── Map layer: Custom implementation based on RGBMapLayer ──────────────
    public class TrueColorLayer : RGBMapLayer
    {
        private ICoreClientAPI? capi;
        private MapDB? mapdb;
        private object chunksToGenLock = new object();
        private UniqueQueue<FastVec2i> chunksToGen = new UniqueQueue<FastVec2i>();
        private HashSet<FastVec2i> curVisibleChunks = new HashSet<FastVec2i>();
        private ConcurrentQueue<ReadyMapPiece> readyMapPieces = new ConcurrentQueue<ReadyMapPiece>();
        private Dictionary<FastVec2i, MapPieceDB> toSaveList = new Dictionary<FastVec2i, MapPieceDB>();
        private ConcurrentDictionary<FastVec2i, MultiChunkMapComponent> loadedMapData = new ConcurrentDictionary<FastVec2i, MultiChunkMapComponent>();
        private float mtThread1secAccum;
        private float genAccum;

        public override string Title => "Colored";
        public override string LayerGroupCode => "truecolorlayer";
        public override EnumMapAppSide DataSide => EnumMapAppSide.Client;
        public override EnumMinMagFilter MinFilter => EnumMinMagFilter.Linear;
        public override EnumMinMagFilter MagFilter => EnumMinMagFilter.Nearest;
        public override MapLegendItem[] LegendItems => System.Array.Empty<MapLegendItem>();

        public TrueColorLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink)
        {
            this.Active = false;
            capi = api as ICoreClientAPI ?? throw new InvalidOperationException("TrueColorLayer requires client-side API");
            
            if (api.Side == EnumAppSide.Client)
            {
                api.World.Logger.Notification("[TrueColorLayer] Loading map cache db...");
                mapdb = new MapDB(api.World.Logger);
                string mapDbPath = GetMapDbFilePath();
                string? error = null;
                mapdb.OpenOrCreate(mapDbPath, ref error, requireWriteAccess: true, corruptionProtection: true, doIntegrityCheck: false);
                if (error != null)
                {
                    throw new Exception($"Cannot open {mapDbPath}: {error}");
                }
            }
        }

        private string GetMapDbFilePath()
        {
            string dir = Path.Combine(GamePaths.DataPath, "Maps");
            GamePaths.EnsurePathExists(dir);
            return Path.Combine(dir, api.World.SavegameIdentifier + "-truecolor.db");
        }

        public override void OnMapOpenedClient()
        {
            base.OnMapOpenedClient();
            this.Active = true;
            // Don't clear loadedMapData - preserve rendered chunks between map opens
            capi!.World.Logger.Notification($"[TrueColorLayer] Map opened. loadedMapData count: {loadedMapData.Count}, Active: {Active}");
        }

        public override void OnMapClosedClient()
        {
            lock (chunksToGenLock)
            {
                chunksToGen.Clear();
            }
            curVisibleChunks.Clear();
            this.Active = false;
        }

        [ThreadStatic]
        static byte[] shadowMapReusable = null!;
        [ThreadStatic]
        static byte[] tempReusable = null!;
        
        // Thread-safe cache for IsSeasonalSnow results
        static ConcurrentDictionary<string, bool> snowCache = new ConcurrentDictionary<string, bool>();
        
        // Generate chunk image with snow-skipping and height-based shading
        public int[] GenerateChunkImage(FastVec2i chunkPos, IMapChunk mc)
        {
            int chunksize = GlobalConstants.ChunkSize;
            int[] result = new int[chunksize * chunksize];
            
            // Reuse shadow map buffer
            shadowMapReusable ??= new byte[result.Length];
            byte[] shadowMap = shadowMapReusable;
            for (int i = 0; i < shadowMap.Length; i++)
            {
                shadowMap[i] = 128;
            }

            // Get neighboring chunks for proper edge shading
            var world = capi!.World;
            IMapChunk chunkNeibW = world.BlockAccessor.GetMapChunk(chunkPos.X - 1, chunkPos.Y);
            IMapChunk chunkNeibN = world.BlockAccessor.GetMapChunk(chunkPos.X, chunkPos.Y - 1);

            for (int i = 0; i < result.Length; i++)
            {
                int lx = i % chunksize;
                int lz = i / chunksize;
                int heightIndex = lz * chunksize + lx;

                int surfaceY = mc.RainHeightMap[heightIndex];
                if (surfaceY <= 0)
                {
                    result[i] = 0;
                    continue;
                }

                // World-space position — BlockAccessor requires world coords, not chunk-local
                BlockPos surfacePos = new BlockPos(
                    chunkPos.X * chunksize + lx,
                    surfaceY,
                    chunkPos.Y * chunksize + lz);
 
                // Calculate height-based shading
                float b = 1f;

                int topX = lx - 1;
                int leftZ = lz - 1;
                
                // Handle chunk boundaries - FIXED: use else-if to avoid overwriting
                IMapChunk leftTopMapChunk = mc;
                IMapChunk rightTopMapChunk = mc;
                IMapChunk leftBotMapChunk = mc;

                if (topX < 0 && leftZ < 0)
                {
                    // Corner case: use diagonal neighbor
                    leftTopMapChunk = world.BlockAccessor.GetMapChunk(chunkPos.X - 1, chunkPos.Y - 1) ?? mc;
                    rightTopMapChunk = chunkNeibW ?? mc;
                    leftBotMapChunk = chunkNeibN ?? mc;
                }
                else if (topX < 0)
                {
                    // Left edge: use left neighbor
                    leftTopMapChunk = chunkNeibW ?? mc;
                    rightTopMapChunk = chunkNeibW ?? mc;
                    leftBotMapChunk = mc;
                }
                else if (leftZ < 0)
                {
                    // Top edge: use top neighbor
                    leftTopMapChunk = chunkNeibN ?? mc;
                    rightTopMapChunk = mc;
                    leftBotMapChunk = chunkNeibN ?? mc;
                }

                int actualTopX = GameMath.Mod(topX, chunksize);
                int actualLeftZ = GameMath.Mod(leftZ, chunksize);

                int leftTop = leftTopMapChunk == null ? 0 : (surfaceY - leftTopMapChunk.RainHeightMap[actualLeftZ * chunksize + actualTopX]);
                int rightTop = rightTopMapChunk == null ? 0 : (surfaceY - rightTopMapChunk.RainHeightMap[lz * chunksize + actualTopX]);
                int leftBot = leftBotMapChunk == null ? 0 : (surfaceY - leftBotMapChunk.RainHeightMap[actualLeftZ * chunksize + lx]);

                float slopedir = Math.Sign(leftTop) + Math.Sign(rightTop) + Math.Sign(leftBot);
                float steepness = Math.Max(Math.Max(Math.Abs(leftTop), Math.Abs(rightTop)), Math.Abs(leftBot));

                float heightFactor = Math.Min(1.0f, steepness / 8.0f);
                if (slopedir > 0)
                    b = 1.12f + heightFactor * 0.4f; // Brighter: slopes facing up/light
                else if (slopedir < 0)
                    b = 0.88f - heightFactor * 0.4f; // Darker: slopes facing down/away
                else
                    b = 1.0f; // Flat

                shadowMap[i] = (byte)Math.Max(0, Math.Min(255, 128 * b));

                // Get surface block (world-space coords already set in surfacePos)
                var block = world.BlockAccessor.GetBlock(surfacePos);

                // Skip seasonal snow/ice — show the terrain underneath instead
                if (TrueColorLayerModSystem.Config?.DisableSnowInWinter == true &&
                    block != null && (IsSeasonalSnow(block) || IsSeasonalIce(block)))
                {
                    for (int y = surfaceY - 1; y > Math.Max(0, surfaceY - 10); y--)
                    {
                        var belowPos = new BlockPos(surfacePos.X, y, surfacePos.Z);
                        var blockBelow = world.BlockAccessor.GetBlock(belowPos);
                        if (blockBelow == null) continue;
                        if (!IsSeasonalSnow(blockBelow) && !IsSeasonalIce(blockBelow))
                        {
                            block = blockBelow;
                            surfacePos = belowPos;
                            break;
                        }
                    }
                }

                // Sample block color (vegetation uses its own color; fallback to block color)
                if (block != null && block.Id != 0)
                {
                    int color = block.GetColor(capi!, surfacePos);
                    result[i] = color != 0 ? (color & 0x00FFFFFF) | (255 << 24) : 0;
                }
                else
                {
                    result[i] = 0;
                }
            }

            // Apply simple blur to shadow map for smooth transitions
            ApplySimpleBlur(shadowMap, chunksize, chunksize);

            // Combine colors with shadow map
            for (int i = 0; i < result.Length; i++)
            {
                if (result[i] == 0) continue;
                float shadow = (shadowMap[i] / 128f) - 1f;
                result[i] = ColorUtil.ColorMultiply3Clamped(result[i], shadow + 1f);
            }

            return result;
        }

        // Apply simple blur to shadow map for smooth transitions
        private void ApplySimpleBlur(byte[] map, int width, int height)
        {
            // Reuse temp buffer
            tempReusable ??= new byte[map.Length];
            byte[] temp = tempReusable;
            int radius = 2;

            // Horizontal pass
            for (int z = 0; z < height; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    int sum = 0;
                    int count = 0;
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int nx = x + dx;
                        if (nx >= 0 && nx < width)
                        {
                            sum += map[z * width + nx];
                            count++;
                        }
                    }
                    temp[z * width + x] = (byte)(sum / count);
                }
            }

            // Vertical pass
            for (int x = 0; x < width; x++)
            {
                for (int z = 0; z < height; z++)
                {
                    int sum = 0;
                    int count = 0;
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        int nz = z + dz;
                        if (nz >= 0 && nz < height)
                        {
                            sum += temp[nz * width + x];
                            count++;
                        }
                    }
                    map[z * width + x] = (byte)(sum / count);
                }
            }
        }

        // Check if block is seasonal snow (not permafrost/glacier)
        private bool IsSeasonalSnow(Block block)
        {
            if (block == null) return false;
            
            string code = block.Code?.ToString() ?? "";
            
            // Thread-safe cache check
            if (snowCache.TryGetValue(code, out bool cached)) return cached;

            string path = block.Code?.Path?.ToLowerInvariant() ?? "";

            // Preserve permafrost/glacier — not seasonal snow
            if (path.Contains("glacier") || path.Contains("permafrost"))
            {
                snowCache.TryAdd(code, false);
                return false;
            }

            // Snow block material is the authoritative check
            if (block.BlockMaterial == EnumBlockMaterial.Snow)
            {
                snowCache.TryAdd(code, true);
                return true;
            }

            // Code-path fallback: "snow", "snow-N" but not "snowblock" (building material)
            bool result = (path == "snow" || path.StartsWith("snow-")) && !path.Contains("block");
            snowCache.TryAdd(code, result);
            return result;
        }

        // Check if block is seasonal lake ice (not glacier/permafrost ice)
        private bool IsSeasonalIce(Block block)
        {
            if (block == null) return false;
            
            string path = block.Code?.Path?.ToLowerInvariant() ?? "";

            // Glacier/permafrost ice is permanent — not seasonal, so preserve it
            if (path.Contains("glacier") || path.Contains("permafrost"))
            {
                return false;
            }
            
            // Check by block material - Ice material includes lake ice
            if (block.BlockMaterial == EnumBlockMaterial.Ice)
            {
                return true;
            }
            
            // Check by code path for ice blocks (like "lakeice", "ice", etc.)
            if (path.Contains("ice") && (path == "ice" || path.StartsWith("ice-") || path.Contains("lakeice")))
            {
                return true;
            }
            
            return false;
        }

        // Check if block is a flower or vegetation that should show its color on map
        private bool IsFlowerOrVegetation(Block? block)
        {
            if (block == null) return false;
            
            string path = block.Code?.Path?.ToLowerInvariant() ?? "";
            
            // Check by block material - Plant material is vegetation
            if (block.BlockMaterial == EnumBlockMaterial.Plant)
            {
                return true;
            }
            
            // Check by code path for flowers and vegetation
            if (path.Contains("flower") || path.Contains("mushroom") || 
                path.Contains("fern") || path.Contains("tallgrass") ||
                path.Contains("clover") || path.Contains("bush") ||
                path.Contains("sapling") || path.Contains("seaweed") ||
                path.Contains("kelp") || path.Contains("algae") ||
                path.Contains("reeds") || path.Contains("cattail") ||
                path.Contains("crystals") || path.Contains("coral") ||
                path.Contains("starfish") || path.Contains("shell") ||
                path.Contains("bones") || path.Contains("skull") ||
                path.Contains("feather") || path.Contains("egg") ||
                path.Contains("nest") || path.Contains("berries") ||
                path.Contains("berry"))
            {
                return true;
            }
            
            // Also check for short grass and plant variants
            if (path.Contains("grass") && (path.Contains("-") || path.Contains("short")))
            {
                return true;
            }
            
return false;
        }

        // Main thread update (from VS-GeologyMap)
        public override void OnTick(float dt)
        {
            genAccum += dt;
            if (genAccum > 0.05f)
            {
                genAccum = 0;
                int processed = 0;
                while (processed < 10 && chunksToGen.Count > 0)
                {
                    FastVec2i cord;
                    lock (chunksToGenLock)
                    {
                        if (chunksToGen.Count == 0) break;
                        cord = chunksToGen.Dequeue();
                    }

                    var testPos = new BlockPos(cord.X * GlobalConstants.ChunkSize, 1, cord.Y * GlobalConstants.ChunkSize);
                    if (!api.World.BlockAccessor.IsValidPos(testPos)) 
                    {
                        processed++;
                        continue;
                    }

                    IMapChunk mc = capi!.World.BlockAccessor.GetMapChunk(cord.X, cord.Y);
                    if (mc == null)
                    {
                        try
                        {
                            MapPieceDB piece = mapdb!.GetMapPiece(cord);
                            if (piece?.Pixels != null)
                            {
                                LoadFromChunkPixels(cord, piece.Pixels);
                            }
                        }
                        catch { }
                        processed++;
                        continue;
                    }

                    int[] pixels = GenerateChunkImage(cord, mc);
                    if (pixels != null)
                    {
                        toSaveList[cord.Copy()] = new MapPieceDB() { Pixels = pixels };
                        LoadFromChunkPixels(cord, pixels);
                    }
                    processed++;
                }

                if (toSaveList.Count > 50)
                {
                    mapdb!.SetMapPieces(toSaveList);
                    toSaveList.Clear();
                }
            }

            if (!readyMapPieces.IsEmpty)
            {
                int q = Math.Min(readyMapPieces.Count, 200);
                List<MultiChunkMapComponent> modified = new List<MultiChunkMapComponent>();
                
                while (q-- > 0)
                {
                    if (readyMapPieces.TryDequeue(out var mapPiece))
                    {
                        FastVec2i mcord = new FastVec2i(
                            mapPiece.Cord.X / MultiChunkMapComponent.ChunkLen,
                            mapPiece.Cord.Y / MultiChunkMapComponent.ChunkLen);
                        FastVec2i baseCord = new FastVec2i(
                            mcord.X * MultiChunkMapComponent.ChunkLen,
                            mcord.Y * MultiChunkMapComponent.ChunkLen);

                        if (!loadedMapData.TryGetValue(mcord, out var mccomp))
                        {
                            loadedMapData[mcord] = mccomp = new MultiChunkMapComponent(capi, baseCord);
                        }

                        mccomp.setChunk(
                            mapPiece.Cord.X - baseCord.X,
                            mapPiece.Cord.Y - baseCord.Y,
                            mapPiece.Pixels);
                        modified.Add(mccomp);
                    }
                }

                foreach (var mccomp in modified) mccomp.FinishSetChunks();
            }

            mtThread1secAccum += dt;
            if (mtThread1secAccum > 1)
            {
                List<FastVec2i> toRemove = new List<FastVec2i>();
                foreach (var val in loadedMapData)
                {
                    var mcmp = val.Value;
                    if (!mcmp.AnyChunkSet || !mcmp.IsVisible(curVisibleChunks))
                    {
                        mcmp.TTL--;
                        if (mcmp.TTL <= 0)
                        {
                            toRemove.Add(val.Key);
                            mcmp.ActuallyDispose();
                        }
                    }
                    else
                    {
                        mcmp.TTL = MultiChunkMapComponent.MaxTTL;
                    }
                }

                foreach (var val in toRemove)
                {
                    loadedMapData.TryRemove(val, out _);
                }

                mtThread1secAccum = 0;
            }
        }

        // Handle view changes (from VS-GeologyMap)
        public override void OnViewChangedClient(List<FastVec2i> nowVisible, List<FastVec2i> nowHidden)
        {
            capi?.World.Logger.Notification($"[TrueColorLayer] OnViewChanged: {nowVisible.Count} visible, {nowHidden.Count} hidden. loadedMapData count: {loadedMapData.Count}");
            
            foreach (var val in nowVisible)
            {
                curVisibleChunks.Add(val);
            }

            foreach (var val in nowHidden)
            {
                curVisibleChunks.Remove(val);
            }

            lock (chunksToGenLock)
            {
                foreach (FastVec2i cord in nowVisible)
                {
                    FastVec2i tmpMccoord = new FastVec2i(
                        cord.X / MultiChunkMapComponent.ChunkLen,
                        cord.Y / MultiChunkMapComponent.ChunkLen);
                    
                    int dx = cord.X % MultiChunkMapComponent.ChunkLen;
                    int dz = cord.Y % MultiChunkMapComponent.ChunkLen;
                    if (dx < 0 || dz < 0) continue;

                    if (loadedMapData.TryGetValue(tmpMccoord, out var mcomp))
                    {
                        if (mcomp.IsChunkSet(dx, dz)) 
                        {
                            capi?.World.Logger.Notification($"[TrueColorLayer] Chunk {cord} already loaded, skipping");
                            continue;
                        }
                    }

                    capi?.World.Logger.Notification($"[TrueColorLayer] Chunk {cord} NOT in loadedMapData, will generate");
                    chunksToGen.Enqueue(cord.Copy());
                }
            }

            foreach (FastVec2i cord in nowHidden)
            {
                if (cord.X < 0 || cord.Y < 0) continue;
                FastVec2i mcord = new FastVec2i(
                    cord.X / MultiChunkMapComponent.ChunkLen,
                    cord.Y / MultiChunkMapComponent.ChunkLen);

                if (loadedMapData.TryGetValue(mcord, out var mc))
                {
                    mc.unsetChunk(
                        cord.X % MultiChunkMapComponent.ChunkLen,
                        cord.Y % MultiChunkMapComponent.ChunkLen);
                }
            }
        }

        // Render the map
        public override void Render(GuiElementMap mapElem, float dt)
        {
            if (!Active) return;
            foreach (var kvp in loadedMapData)
            {
                kvp.Value.Render(mapElem, dt);
            }
        }

        // Handle mouse movement
        public override void OnMouseMoveClient(MouseEvent args, GuiElementMap mapElem, StringBuilder hoverText)
        {
            if (!Active) return;

            foreach (var kvp in loadedMapData)
            {
                kvp.Value.OnMouseMove(args, mapElem, hoverText);
            }

            // Add climate info — use the field-level capi, not a shadowed local
            {
                Vec3d worldPos = new Vec3d();
                mapElem.TranslateViewPosToWorldPos(new Vec2f(args.X, args.Y), ref worldPos);

                BlockPos pos = new BlockPos((int)worldPos.X, 0, (int)worldPos.Z);

                var world = capi!.World;
                IMapChunk mc = world.BlockAccessor.GetMapChunk(
                    pos.X / GlobalConstants.ChunkSize,
                    pos.Z / GlobalConstants.ChunkSize);
                
                if (mc != null)
                {
                    int lx = GameMath.Mod(pos.X, GlobalConstants.ChunkSize);
                    int lz = GameMath.Mod(pos.Z, GlobalConstants.ChunkSize);
                    pos.Y = mc.RainHeightMap[lz * GlobalConstants.ChunkSize + lx];
                }
                else
                {
                    pos.Y = world.BlockAccessor.GetTerrainMapheightAt(pos);
                }
                
                if (pos.Y > 0)
                {
                    ClimateCondition cond = world.BlockAccessor.GetClimateAt(
                        pos, EnumGetClimateMode.NowValues, world.Calendar.TotalDays);
                    if (cond != null)
                    {
                        if (hoverText.Length > 0 && hoverText[hoverText.Length - 1] != '\n')
                        {
                            hoverText.AppendLine();
                        }
                        hoverText.Append($"Temp: {cond.Temperature:F1}°C, Rainfall: {cond.Rainfall * 100:F0}%");
                    }
                }
            }
        }

        private void LoadFromChunkPixels(FastVec2i cord, int[] pixels)
        {
            readyMapPieces.Enqueue(new ReadyMapPiece
            {
                Pixels = pixels,
                Cord = cord
            });
        }

        public override void Dispose()
        {
            if (loadedMapData != null)
            {
                foreach (var val in loadedMapData.Values)
                {
                    val?.ActuallyDispose();
                }
            }
            base.Dispose();
        }

        public override void OnShutDown()
        {
            MultiChunkMapComponent.DisposeStatic();
            mapdb?.Dispose();
        }
    }

    // ─── Multi-chunk map component for rendering ────────────────────────────
    // Using MultiChunkMapComponent directly - the layer is set via the base class

    // ─── Mod entry point ──────────────────────────────────────────────────────
    public class TrueColorLayerModSystem : ModSystem
    {
        private const string PatchId = "truecolorlayer.patches";
        private Harmony? _harmony;
        public static TrueColorLayerConfig Config { get; private set; } = null!;

        public override void StartPre(ICoreAPI api)
        {
            base.StartPre(api);
            if (api.Side != EnumAppSide.Client) return;

            try
            {
                Config = api.LoadModConfig<TrueColorLayerConfig>("truecolorlayer.json") ?? new TrueColorLayerConfig();
                api.Logger.Notification($"[TrueColorLayer] Config loaded. DisableSnowInWinter={Config.DisableSnowInWinter}, ReplaceDefaultMap={Config.ReplaceDefaultMap}");
            }
            catch
            {
                Config = new TrueColorLayerConfig();
                api.Logger.Warning("[TrueColorLayer] Failed to load config, using defaults.");
            }
            api.StoreModConfig(Config, "truecolorlayer.json");

            // Only patch tab ordering (no more snow patch needed!)
            if (Harmony.HasAnyPatches(PatchId)) return;
            _harmony = new Harmony(PatchId);
            try
            {
                _harmony.PatchAll(System.Reflection.Assembly.GetExecutingAssembly());
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
            mapMan.RegisterMapLayer<TrueColorLayer>("truecolorlayer", 1.5);
            api.Logger.Notification("[TrueColorLayer] Map layer registered.");
        }

        public override void Dispose()
        {
            _harmony?.UnpatchAll(PatchId);
            base.Dispose();
        }
    }

    // ─── Harmony patch: tab order (only for UI, not for rendering) ──────────
    [HarmonyPatch]
    public static class TrueColorLayerTabOrderPatch
    {
        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            return typeof(WorldMapManager)
                .GetMethod("getTabsOrdered",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        }

        [HarmonyPostfix]
        public static void Postfix(ref List<string> __result)
        {
            if (__result == null) return;

            __result.Remove("truecolorlayer");
            int i = __result.FindIndex(x => x == "terrain");

            if (TrueColorLayerModSystem.Config != null && TrueColorLayerModSystem.Config.ReplaceDefaultMap)
            {
                if (i >= 0)
                {
                    __result.Insert(i, "truecolorlayer");
                    __result.Remove("terrain");
                }
                else
                {
                    __result.Add("truecolorlayer");
                }
            }
            else
            {
                int insertAt = i >= 0 ? Math.Min(i + 1, __result.Count) : __result.Count;
                __result.Insert(insertAt, "truecolorlayer");
            }
        }
    }
}
