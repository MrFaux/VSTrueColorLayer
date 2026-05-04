$lines = Get-Content "C:\Users\rober\.gemini\antigravity\scratch\AccurateMap\truecolorlayer.cs"
$newContent = $lines[0..811]
$newContent += ""
$newContent += "    // ─── Harmony patch: force worldgen climate for summer colors ──────────────"
$newContent += '    [HarmonyPatch(typeof(IBlockAccessor), "GetClimateAt", new Type[] { typeof(BlockPos), typeof(EnumGetClimateMode), typeof(double) })]'
$newContent += "    public static class ForceWorldGenClimatePatch"
$newContent += "    {"
$newContent += "        [HarmonyPrefix]"
$newContent += "        public static void Prefix(ref EnumGetClimateMode mode)"
$newContent += "        {"
$newContent += "            if (TrueColorLayerModSystem.Config?.AlwaysSummerColors == true)"
$newContent += "            {"
$newContent += "                mode = EnumGetClimateMode.WorldGenValues;"
$newContent += "            }"
$newContent += "        }"
$newContent += "    }"
$newContent += "}"
$newContent | Set-Content "C:\Users\rober\.gemini\antigravity\scratch\AccurateMap\truecolorlayer.cs" -Encoding UTF8
Write-Host "File fixed successfully"
