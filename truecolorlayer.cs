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
        public bool EnableBlur { get; set; } = false;
        public int MaxChunksPerTick { get; set; } = 8;
    }

    // ─── Map layer ────────────────────────────────────────────────────────────
    public class TrueColorLayer : RGBMapLayer
    {
        private ICoreClientAPI? capi;
        private MapDB? mapdb;
        private readonly object chunksToGenLock = new object();
        private UniqueQueue<FastVec2i> chunksToGen = new UniqueQueue<FastVec2i>();
        private HashSet<FastVec2i> curVisibleChunks = new HashSet<FastVec2i>();
        private ConcurrentQueue<ReadyMapPiece> readyMapPieces = new ConcurrentQueue<ReadyMapPiece>();
        private Dictionary<FastVec2i, MapPieceDB> toSaveList = new Dictionary<FastVec2i, MapPieceDB>();
        private readonly object toSaveListLock = new object();
        private HashSet<FastVec2i> chunksBeingGenerated = new HashSet<FastVec2i>();
        
        // Dedicated DB Load Queue
        private System.Threading.CancellationTokenSource? dbCts;
        private ConcurrentQueue<FastVec2i> dbLoadQueue = new ConcurrentQueue<FastVec2i>();
        private ConcurrentDictionary<FastVec2i, bool> chunksInDbQueue = new ConcurrentDictionary<FastVec2i, bool>();

        private ConcurrentDictionary<long, MultiChunkMapComponent> loadedMapData = new ConcurrentDictionary<long, MultiChunkMapComponent>();
        private ConcurrentDictionary<FastVec2i, byte> missingNeighboursMap = new ConcurrentDictionary<FastVec2i, byte>();
        private readonly object visibleChunksLock = new object();
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
                api.Event.ChunkDirty += OnChunkDirty;
                api.World.Logger.Notification("[TrueColorLayer] Loading map cache db...");
                mapdb = new MapDB(api.World.Logger);
                string mapDbPath = GetMapDbFilePath();
                string? error = null;
                mapdb.OpenOrCreate(mapDbPath, ref error, requireWriteAccess: true, corruptionProtection: true, doIntegrityCheck: false);
                if (error != null)
                    throw new Exception($"Cannot open {mapDbPath}: {error}");
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
            
            dbCts = new System.Threading.CancellationTokenSource();
            System.Threading.Tasks.Task.Run(() => DbLoadLoop(dbCts.Token));

            capi!.World.Logger.Notification($"[TrueColorLayer] Map opened. Chunks cached: {loadedMapData.Count}");
        }

        private void DbLoadLoop(System.Threading.CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (dbLoadQueue.TryDequeue(out FastVec2i cord))
                {
                    chunksInDbQueue.TryRemove(cord, out _);
                    try
                    {
                        MapPieceDB piece = mapdb!.GetMapPiece(cord);
                        if (piece?.Pixels != null)
                        {
                            LoadFromChunkPixels(cord, piece.Pixels);
                        }
                        else
                        {
                            lock (chunksToGenLock) { chunksToGen.Enqueue(cord.Copy()); }
                        }
                    }
                    catch (Exception ex)
                    {
                        capi?.World.Logger.VerboseDebug("[TrueColorLayer] DB load error: " + ex);
                    }
                }
                else
                {
                    System.Threading.Thread.Sleep(10);
                }
            }
        }

        public override void OnMapClosedClient()
        {
            dbCts?.Cancel();
            lock (toSaveListLock)
            {
                if (toSaveList.Count > 0)
                {
                    mapdb?.SetMapPieces(toSaveList);
                    toSaveList.Clear();
                }
            }
            lock (chunksToGenLock) { chunksToGen.Clear(); chunksBeingGenerated.Clear(); }
            lock (visibleChunksLock) { curVisibleChunks.Clear(); }
            missingNeighboursMap.Clear();
            dbLoadQueue.Clear();
            chunksInDbQueue.Clear();
            this.Active = false;
        }

        // ThreadStatic so each thread pool thread gets its own buffer
        [ThreadStatic] static byte[]? shadowMapReusable;
        [ThreadStatic] static byte[]? tempReusable;

        // Thread-safe block-classification cache
        static readonly ConcurrentDictionary<int, bool> snowCache = new ConcurrentDictionary<int, bool>();
        static readonly ConcurrentDictionary<int, bool> plantCache = new ConcurrentDictionary<int, bool>();

        // ─── Core image generation ────────────────────────────────────────────
        public int[] GenerateChunkImage(FastVec2i chunkPos, IMapChunk mc, IMapChunk? neibW, IMapChunk? neibN, IMapChunk? neibNW)
        {
            int cs = GlobalConstants.ChunkSize;
            int[] result = new int[cs * cs];

            shadowMapReusable ??= new byte[cs * cs];
            byte[] shadowMap = shadowMapReusable;
            Array.Fill(shadowMap, (byte)128);

            var world = capi!.World;
            bool snowSkip = TrueColorLayerModSystem.Config?.DisableSnowInWinter == true;

            // Reuse ONE BlockPos for all GetBlock calls — avoids ~hundreds of heap allocations
            // per chunk render. BlockPos.Set() mutates in place and returns this.
            BlockPos bp = new BlockPos(0, 0, 0);

            for (int i = 0; i < result.Length; i++)
            {
                int lx = i % cs;
                int lz = i / cs;

                int surfaceY = mc.RainHeightMap[lz * cs + lx];
                if (surfaceY <= 0) { result[i] = 0; continue; }

                int worldX = chunkPos.X * cs + lx;
                int worldZ = chunkPos.Y * cs + lz;

                // ── Hill shading (RainHeightMap — no BlockAccessor calls needed) ─
                int hHere = surfaceY;

                int hW = (lx > 0)
                    ? mc.RainHeightMap[lz * cs + (lx - 1)]
                    : (neibW != null ? neibW.RainHeightMap[lz * cs + (cs - 1)] : surfaceY);

                int hN = (lz > 0)
                    ? mc.RainHeightMap[(lz - 1) * cs + lx]
                    : (neibN != null ? neibN.RainHeightMap[(cs - 1) * cs + lx] : surfaceY);

                int hNW;
                if      (lx > 0 && lz > 0) hNW = mc.RainHeightMap[(lz - 1) * cs + (lx - 1)];
                else if (lx > 0)            hNW = neibN  != null ? neibN.RainHeightMap[(cs - 1) * cs + (lx - 1)] : surfaceY;
                else if (lz > 0)            hNW = neibW  != null ? neibW.RainHeightMap[(lz - 1) * cs + (cs - 1)] : surfaceY;
                else                        hNW = neibNW != null ? neibNW.RainHeightMap[(cs - 1) * cs + (cs - 1)] : surfaceY;

                int diffW  = hHere - hW;
                int diffN  = hHere - hN;
                int diffNW = hHere - hNW;

                float slopedir  = Math.Sign(diffW) + Math.Sign(diffN) + Math.Sign(diffNW);
                float steepness = Math.Max(Math.Max(Math.Abs(diffW), Math.Abs(diffN)), Math.Abs(diffNW));
                float heightFactor = Math.Min(1f, steepness / 24f);

                float b;
                if      (slopedir > 0) b = 1.15f + heightFactor * 0.5f;
                else if (slopedir < 0) b = 0.85f - heightFactor * 0.5f;
                else                   b = 1f;

                shadowMap[i] = (byte)GameMath.Clamp((int)(128 * b), 0, 255);

                // ── Block colour ──────────────────────────────────────────────
                var block = world.BlockAccessor.GetBlock(bp.Set(worldX, surfaceY, worldZ));

                // 0) If the heightmap points to air (stale data), dig down until we find a block
                if (block == null || block.Id == 0)
                {
                    for (int y = surfaceY - 1; y > 0; y--)
                    {
                        var bb = world.BlockAccessor.GetBlock(bp.Set(worldX, y, worldZ));
                        if (bb == null) break;
                        if (bb.Id != 0) { block = bb; surfaceY = y; break; }
                    }
                    bp.Set(worldX, surfaceY, worldZ);
                }

                // 1) Skip seasonal snow/ice — reveal terrain beneath
                if (snowSkip && block != null && (IsSeasonalSnow(block) || IsSeasonalIce(block)))
                {
                    for (int y = surfaceY - 1; y > 0; y--)
                    {
                        var bb = world.BlockAccessor.GetBlock(bp.Set(worldX, y, worldZ));
                        if (bb == null) break;
                        if (bb.Id == 0) continue;
                        if (!IsSeasonalSnow(bb) && !IsSeasonalIce(bb)) { block = bb; surfaceY = y; break; }
                    }
                    // Keep bp pointing at the correct position for GetColor below
                    bp.Set(worldX, surfaceY, worldZ);
                }

                // 2) Skip non-solid vegetation — show solid block beneath
                if (block != null && IsFlowerOrVegetation(block))
                {
                    for (int y = surfaceY - 1; y > 0; y--)
                    {
                        var bb = world.BlockAccessor.GetBlock(bp.Set(worldX, y, worldZ));
                        if (bb == null) break;
                        if (bb.Id == 0) continue;
                        if (!IsFlowerOrVegetation(bb)) { block = bb; surfaceY = y; break; }
                    }
                    bp.Set(worldX, surfaceY, worldZ);
                }

                if (block == null || block.Id == 0) { result[i] = 0; continue; }

                int color = block.GetColor(capi!, bp);

                // 3) Last-resort: still 0 or near-white — dig one more block
                if (color == 0 || (color & 0x00FFFFFF) >= 0x00F4F4F4)
                {
                    for (int y = surfaceY - 1; y > 0; y--)
                    {
                        var bb = world.BlockAccessor.GetBlock(bp.Set(worldX, y, worldZ));
                        if (bb == null) break;
                        if (bb.Id == 0) continue;
                        int c2 = bb.GetColor(capi!, bp);
                        if (c2 != 0 && (c2 & 0x00FFFFFF) < 0x00F4F4F4) { color = c2; break; }
                    }
                }

                result[i] = color != 0 ? (color & 0x00FFFFFF) | (255 << 24) : 0;
            }

            if (TrueColorLayerModSystem.Config?.EnableBlur == true)
            {
                ApplySimpleBlur(shadowMap, cs, cs);
            }

            for (int i = 0; i < result.Length; i++)
            {
                if (result[i] == 0) continue;
                result[i] = ColorUtil.ColorMultiply3Clamped(result[i], shadowMap[i] / 128f);
            }

            return result;
        }

        private void ApplySimpleBlur(byte[] map, int width, int height)
        {
            tempReusable ??= new byte[map.Length];
            byte[] temp = tempReusable;
            int radius = 2;

            // Horizontal pass
            for (int z = 0; z < height; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    int sum = 0, count = 0;
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int nx = x + dx;
                        if (nx >= 0 && nx < width) { sum += map[z * width + nx]; count++; }
                    }
                    temp[z * width + x] = (byte)(sum / count);
                }
            }

            // Vertical pass
            for (int x = 0; x < width; x++)
            {
                for (int z = 0; z < height; z++)
                {
                    int sum = 0, count = 0;
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        int nz = z + dz;
                        if (nz >= 0 && nz < height) { sum += temp[nz * width + x]; count++; }
                    }
                    map[z * width + x] = (byte)(sum / count);
                }
            }
        }

        // ─── Block classification ─────────────────────────────────────────────

        /// <summary>Returns true for seasonal surface snow (not permafrost/glacier).</summary>
        private bool IsSeasonalSnow(Block block)
        {
            if (block == null) return false;
            if (snowCache.TryGetValue(block.Id, out bool cached)) return cached;

            string path = block.Code?.Path?.ToLowerInvariant() ?? "";

            if (path.Contains("glacier") || path.Contains("permafrost"))
                return snowCache[block.Id] = false;

            if (block.BlockMaterial == EnumBlockMaterial.Snow)
                return snowCache[block.Id] = true;

            bool result = (path == "snow" || path.StartsWith("snow-")) && !path.Contains("block");
            return snowCache[block.Id] = result;
        }

        /// <summary>Returns true for seasonal lake ice (not glacier/permafrost ice).</summary>
        private bool IsSeasonalIce(Block block)
        {
            if (block == null) return false;
            string path = block.Code?.Path?.ToLowerInvariant() ?? "";
            if (path.Contains("glacier") || path.Contains("permafrost")) return false;
            if (block.BlockMaterial == EnumBlockMaterial.Ice) return true;
            return path.Contains("ice") && (path == "ice" || path.StartsWith("ice-") || path.Contains("lakeice"));
        }

        /// <summary>
        /// Returns true for non-solid decoration blocks (flowers, grass, mushrooms, etc.)
        /// that don't have a meaningful map colour and should be skipped in favour of the
        /// solid block beneath them.
        /// </summary>
        private bool IsFlowerOrVegetation(Block? block)
        {
            if (block == null) return false;
            if (plantCache.TryGetValue(block.Id, out bool cached)) return cached;

            // Primary check: Plant material covers almost all non-solid vegetation
            if (block.BlockMaterial == EnumBlockMaterial.Plant)
                return plantCache[block.Id] = true;

            string path = block.Code?.Path?.ToLowerInvariant() ?? "";

            // Code-path fallback for any modded blocks that missed the material assignment
            bool result =
                path.Contains("flower") || path.Contains("mushroom") ||
                path.Contains("fern")   || path.Contains("tallgrass") ||
                path.Contains("clover") || path.Contains("bush")     ||
                path.Contains("sapling")|| path.Contains("seaweed")  ||
                path.Contains("kelp")   || path.Contains("algae")    ||
                path.Contains("reeds")  || path.Contains("cattail")  ||
                path.Contains("crystals")|| path.Contains("coral")   ||
                path.Contains("starfish")|| path.Contains("shell")   ||
                path.Contains("bones")  || path.Contains("skull")    ||
                path.Contains("feather")|| path.Contains("egg")      ||
                path.Contains("nest")   || path.Contains("berry")    ||
                path.Contains("berries")||
                (path.Contains("grass") && (path.Contains("-") || path.Contains("short")));

            return plantCache[block.Id] = result;
        }

        // ─── Main thread update ───────────────────────────────────────────────
        public override void OnTick(float dt)
        {
            genAccum += dt;
            if (genAccum > 0.05f)
            {
                genAccum = 0;
                int processed = 0;
                int maxChunks = TrueColorLayerModSystem.Config?.MaxChunksPerTick ?? 8;
                if (maxChunks <= 0) maxChunks = 8;

                while (processed < maxChunks && chunksToGen.Count > 0)
                {
                    FastVec2i cord;
                    lock (chunksToGenLock)
                    {
                        if (chunksToGen.Count == 0) break;
                        cord = chunksToGen.Dequeue();
                        if (chunksBeingGenerated.Contains(cord)) continue;
                        chunksBeingGenerated.Add(cord.Copy());
                    }

                    var testPos = new BlockPos(cord.X * GlobalConstants.ChunkSize, 1, cord.Y * GlobalConstants.ChunkSize);
                    if (!api.World.BlockAccessor.IsValidPos(testPos)) 
                    { 
                        lock (chunksToGenLock) { chunksBeingGenerated.Remove(cord); }
                        continue; // Do not increment processed for invalid chunks
                    }

                    IMapChunk mc = capi!.World.BlockAccessor.GetMapChunk(cord.X, cord.Y);
                    
                    if (mc == null)
                    {
                        // Chunk not in memory. Drop it from queue to prevent infinite loops.
                        // It will be re-added naturally by OnChunkDirty if the player visits it.
                        lock (chunksToGenLock) { chunksBeingGenerated.Remove(cord); }
                        continue;
                    }

                    IMapChunk? neibW  = capi!.World.BlockAccessor.GetMapChunk(cord.X - 1, cord.Y);
                    IMapChunk? neibN  = capi!.World.BlockAccessor.GetMapChunk(cord.X,     cord.Y - 1);
                    IMapChunk? neibNW = capi!.World.BlockAccessor.GetMapChunk(cord.X - 1, cord.Y - 1);

                    System.Threading.Tasks.Task.Run(() => 
                    {
                        try
                        {
                            int[] pixels = GenerateChunkImage(cord, mc, neibW, neibN, neibNW);
                            if (pixels != null)
                            {
                                lock (toSaveListLock)
                                {
                                    toSaveList[cord.Copy()] = new MapPieceDB() { Pixels = pixels };
                                }
                                LoadFromChunkPixels(cord, pixels);

                                byte missingFlags = 0;
                                if (neibW == null) missingFlags |= 1;
                                if (neibN == null) missingFlags |= 2;
                                if (neibNW == null) missingFlags |= 4;

                                if (missingFlags > 0)
                                    missingNeighboursMap[cord.Copy()] = missingFlags;

                                var east      = new FastVec2i(cord.X + 1, cord.Y);
                                var south     = new FastVec2i(cord.X,     cord.Y + 1);
                                var southeast = new FastVec2i(cord.X + 1, cord.Y + 1);

                                lock (chunksToGenLock)
                                lock (visibleChunksLock)
                                {
                                    if (missingNeighboursMap.TryGetValue(east, out byte eFlags) && (eFlags & 1) != 0)
                                    {
                                        eFlags &= 0xFE;
                                        if (eFlags == 0) missingNeighboursMap.TryRemove(east, out _);
                                        else missingNeighboursMap[east] = eFlags;
                                        if (curVisibleChunks.Contains(east)) chunksToGen.Enqueue(east);
                                    }
                                    if (missingNeighboursMap.TryGetValue(south, out byte sFlags) && (sFlags & 2) != 0)
                                    {
                                        sFlags &= 0xFD;
                                        if (sFlags == 0) missingNeighboursMap.TryRemove(south, out _);
                                        else missingNeighboursMap[south] = sFlags;
                                        if (curVisibleChunks.Contains(south)) chunksToGen.Enqueue(south);
                                    }
                                    if (missingNeighboursMap.TryGetValue(southeast, out byte seFlags) && (seFlags & 4) != 0)
                                    {
                                        seFlags &= 0xFB;
                                        if (seFlags == 0) missingNeighboursMap.TryRemove(southeast, out _);
                                        else missingNeighboursMap[southeast] = seFlags;
                                        if (curVisibleChunks.Contains(southeast)) chunksToGen.Enqueue(southeast);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            capi!.World.Logger.VerboseDebug("[TrueColorLayer] Background generation skipped a chunk: " + ex.Message);
                        }
                        finally
                        {
                            lock (chunksToGenLock) { chunksBeingGenerated.Remove(cord); }
                        }
                    });

                    processed++;
                }

                Dictionary<FastVec2i, MapPieceDB>? listToSave = null;
                lock (toSaveListLock)
                {
                    if (toSaveList.Count > 50)
                    {
                        listToSave = toSaveList;
                        toSaveList = new Dictionary<FastVec2i, MapPieceDB>();
                    }
                }

                if (listToSave != null)
                {
                    System.Threading.Tasks.Task.Run(() => 
                    {
                        try { mapdb!.SetMapPieces(listToSave); }
                        catch (Exception ex) { capi!.World.Logger.Warning("[TrueColorLayer] Error saving map pieces: " + ex.Message); }
                    });
                }
            }

            if (!readyMapPieces.IsEmpty)
            {
                int q = Math.Min(readyMapPieces.Count, 200);
                HashSet<MultiChunkMapComponent> modified = new HashSet<MultiChunkMapComponent>();
                while (q-- > 0)
                {
                    if (readyMapPieces.TryDequeue(out var mapPiece))
                    {
                        int mcX = mapPiece.Cord.X / MultiChunkMapComponent.ChunkLen;
                        int mcY = mapPiece.Cord.Y / MultiChunkMapComponent.ChunkLen;
                        long mcordKey = ((long)mcX << 32) | (uint)mcY;

                        if (!loadedMapData.TryGetValue(mcordKey, out var mccomp))
                        {
                            FastVec2i baseCord = new FastVec2i(
                                mcX * MultiChunkMapComponent.ChunkLen,
                                mcY * MultiChunkMapComponent.ChunkLen);
                            loadedMapData[mcordKey] = mccomp = new MultiChunkMapComponent(capi, baseCord);
                        }

                        mccomp.setChunk(mapPiece.Cord.X - (mcX * MultiChunkMapComponent.ChunkLen), 
                                        mapPiece.Cord.Y - (mcY * MultiChunkMapComponent.ChunkLen), 
                                        mapPiece.Pixels);
                        modified.Add(mccomp);
                    }
                }
                foreach (var mccomp in modified) mccomp.FinishSetChunks();
            }

            mtThread1secAccum += dt;
            if (mtThread1secAccum > 1)
            {
                List<long> toRemove = new List<long>();
                foreach (var val in loadedMapData)
                {
                    var mcmp = val.Value;
                    bool isVisible;
                    lock (visibleChunksLock) { isVisible = mcmp.IsVisible(curVisibleChunks); }

                    if (!mcmp.AnyChunkSet || !isVisible)
                    {
                        mcmp.TTL--;
                        if (mcmp.TTL <= 0) { toRemove.Add(val.Key); mcmp.ActuallyDispose(); }
                    }
                    else
                    {
                        mcmp.TTL = MultiChunkMapComponent.MaxTTL;
                    }
                }
                foreach (var val in toRemove) loadedMapData.TryRemove(val, out _);
                mtThread1secAccum = 0;
            }
        }

        // ─── View change ──────────────────────────────────────────────────────
        public override void OnViewChangedClient(List<FastVec2i> nowVisible, List<FastVec2i> nowHidden)
        {
            lock (visibleChunksLock)
            {
                foreach (var val in nowVisible) curVisibleChunks.Add(val);
                foreach (var val in nowHidden)  curVisibleChunks.Remove(val);
            }

            foreach (FastVec2i cord in nowVisible)
            {
                long tmpMccoord = ((long)(cord.X / MultiChunkMapComponent.ChunkLen) << 32) | (uint)(cord.Y / MultiChunkMapComponent.ChunkLen);

                int dx = cord.X % MultiChunkMapComponent.ChunkLen;
                int dz = cord.Y % MultiChunkMapComponent.ChunkLen;
                if (dx < 0 || dz < 0) continue;

                if (loadedMapData.TryGetValue(tmpMccoord, out var mcomp) && mcomp.IsChunkSet(dx, dz))
                    continue; // already rendered

                if (chunksInDbQueue.TryAdd(cord, true))
                {
                    dbLoadQueue.Enqueue(cord.Copy());
                }
            }

            foreach (FastVec2i cord in nowHidden)
            {
                if (cord.X < 0 || cord.Y < 0) continue;
                long mcord = ((long)(cord.X / MultiChunkMapComponent.ChunkLen) << 32) | (uint)(cord.Y / MultiChunkMapComponent.ChunkLen);
                if (loadedMapData.TryGetValue(mcord, out var mc))
                    mc.unsetChunk(cord.X % MultiChunkMapComponent.ChunkLen, cord.Y % MultiChunkMapComponent.ChunkLen);
            }
        }

        // ─── Render & mouse ───────────────────────────────────────────────────
        public override void Render(GuiElementMap mapElem, float dt)
        {
            if (!Active) return;
            foreach (var kvp in loadedMapData) kvp.Value.Render(mapElem, dt);
        }

        public override void OnMouseMoveClient(MouseEvent args, GuiElementMap mapElem, StringBuilder hoverText)
        {
            if (!Active) return;
            foreach (var kvp in loadedMapData) kvp.Value.OnMouseMove(args, mapElem, hoverText);

            // Append climate info using the field-level capi (no shadowed local)
            var world = capi!.World;
            Vec3d worldPos = new Vec3d();
            mapElem.TranslateViewPosToWorldPos(new Vec2f(args.X, args.Y), ref worldPos);
            BlockPos pos = new BlockPos((int)worldPos.X, 0, (int)worldPos.Z);

            IMapChunk? mc = world.BlockAccessor.GetMapChunk(
                pos.X / GlobalConstants.ChunkSize,
                pos.Z / GlobalConstants.ChunkSize);

            pos.Y = mc != null
                ? mc.RainHeightMap[GameMath.Mod(pos.Z, GlobalConstants.ChunkSize) * GlobalConstants.ChunkSize + GameMath.Mod(pos.X, GlobalConstants.ChunkSize)]
                : world.BlockAccessor.GetTerrainMapheightAt(pos);

            if (pos.Y > 0)
            {
                ClimateCondition cond = world.BlockAccessor.GetClimateAt(pos, EnumGetClimateMode.NowValues, world.Calendar.TotalDays);
                if (cond != null)
                {
                    if (hoverText.Length > 0 && hoverText[hoverText.Length - 1] != '\n')
                        hoverText.AppendLine();
                    hoverText.Append($"Temp: {cond.Temperature:F1}°C, Rainfall: {cond.Rainfall * 100:F0}%");
                }
            }
        }

        private void LoadFromChunkPixels(FastVec2i cord, int[] pixels)
        {
            readyMapPieces.Enqueue(new ReadyMapPiece { Pixels = pixels, Cord = cord });
        }

        private void OnChunkDirty(Vec3i chunkCoord, IWorldChunk chunk, EnumChunkDirtyReason reason)
        {
            long tmpMccoord = ((long)(chunkCoord.X / MultiChunkMapComponent.ChunkLen) << 32) | (uint)(chunkCoord.Z / MultiChunkMapComponent.ChunkLen);

            bool isVisible;
            lock (visibleChunksLock) { isVisible = curVisibleChunks.Contains(new FastVec2i(chunkCoord.X, chunkCoord.Z)); }

            if (!loadedMapData.ContainsKey(tmpMccoord) && !isVisible)
                return;

            lock (chunksToGenLock)
            {
                chunksToGen.Enqueue(new FastVec2i(chunkCoord.X, chunkCoord.Z));
            }
        }

        public override void Dispose()
        {
            dbCts?.Cancel();
            if (capi != null) capi.Event.ChunkDirty -= OnChunkDirty;
            
            if (loadedMapData != null)
                foreach (var val in loadedMapData.Values) val?.ActuallyDispose();
            lock (toSaveListLock)
            {
                if (toSaveList.Count > 0)
                {
                    mapdb?.SetMapPieces(toSaveList);
                    toSaveList.Clear();
                }
            }
            mapdb?.Dispose();
            base.Dispose();
        }

        public override void OnShutDown()
        {
            MultiChunkMapComponent.DisposeStatic();
        }
    }

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

            if (Harmony.HasAnyPatches(PatchId)) return;
            _harmony = new Harmony(PatchId);
            try
            {
                _harmony.PatchAll(System.Reflection.Assembly.GetExecutingAssembly());
                api.Logger.Notification("[TrueColorLayer] Harmony patches applied.");
            }
            catch (Exception ex)
            {
                api.Logger.Warning("[TrueColorLayer] Harmony patching failed: " + ex.ToString());
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

    // ─── Harmony patch: tab order ─────────────────────────────────────────────
    [HarmonyPatch]
    public static class TrueColorLayerTabOrderPatch
    {
        [HarmonyTargetMethod]
        public static MethodBase TargetMethod()
        {
            return typeof(WorldMapManager)
                .GetMethod("getTabsOrdered", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        }

        [HarmonyPostfix]
        public static void Postfix(ref List<string> __result)
        {
            if (__result == null) return;

            __result.Remove("truecolorlayer");
            int i = __result.FindIndex(x => x == "terrain");

            if (TrueColorLayerModSystem.Config != null && TrueColorLayerModSystem.Config.ReplaceDefaultMap)
            {
                if (i >= 0) { __result.Insert(i, "truecolorlayer"); __result.Remove("terrain"); }
                else          __result.Add("truecolorlayer");
            }
            else
            {
                int insertAt = i >= 0 ? Math.Min(i + 1, __result.Count) : __result.Count;
                __result.Insert(insertAt, "truecolorlayer");
            }
        }
    }
}
