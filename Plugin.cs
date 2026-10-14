using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

// ============================================================================
//  BALANCE - fixed item parameters (edit here, no config needed)
// ============================================================================
static class Balance
{
    public const string ItemName = "oiled canvas";
    public const int ModelIndex = 222;          // 222 = wool bale
    public const int Price = 450;
    public const float InventoryScale = 0.25f;  // multiplier of the wool bale's slot scale
    public const float HoldDistance = 1.3f;
    public const float StainStrength = 0.5f;

    public const float LeakStart = 50f;         // below this health (%) the canvas starts leaking
    public const float WearPerHold = 25f;       // health % lost per one full hold of water blocked
    public const float DailyWear = 1.5f;        // health % lost per in-game day while fitted
    public const float RepairCostFull = 0.25f;  // share of a full oakum barrel for a 0 -> 100% repair

    public const float ShopGap = 0.05f;         // gap (m) between the counter end and the canvas bales
    public const int ShopStack = 2;             // canvas bales stacked next to a shop counter
	public const float ShopMaxShift = 1.5f;     // how far (m) the stack may move away from the counter to find a free spot
	public const float ShopMaxCounter = 3f;     // a "counter" longer than this (m, half length) is a floor or terrain, not a counter
	// hand-placed canvas stacks: shop spawner path = x|y|z|yaw (relative to the oakum spawner), or "skip"
    public const string ShopPlacements =
        "island 9 E (dragon cliffs) scenery/shop item spawner (229)=-1.101|-0.086|2.061|128.742;" +
        "island 1 A (gold rock) scenery/shop item (218)=-0.977|-0.878|0.58|120.995;" +
        "island scenery Lagoon Shipyard/shop item (244)=1.42|-0.001|0.958|-116.547";
}

// ============================================================================
//  PLUGIN: config, debug tools
// ============================================================================
[BepInPlugin("yourname.hatchtarp", "HatchTarp", "0.4.1")]
public class Plugin : BaseUnityPlugin
{
    internal static Plugin Instance;
    internal static ManualLogSource Log;

    internal static ConfigEntry<int> CfgPrefabIndex;
    internal static ConfigEntry<float> CfgRadius;
    internal static ConfigEntry<string> CfgLeakBoats;
    internal static ConfigEntry<float> CfgLeak;
    internal static ConfigEntry<bool> CfgBattens;
    internal static ConfigEntry<bool> CfgDebug;

    const string OldLeakBoats = "dhow large";
    const string DefaultLeakBoats = "dhow large, chronian";

    internal static WaveSplashZone[] Zones = new WaveSplashZone[0];

    float nextTick;
    bool hudOn = true, markersOn;
    readonly Dictionary<WaveSplashZone, GameObject> markers = new Dictionary<WaveSplashZone, GameObject>();

    void Awake()
    {
        Instance = this;
        Log = Logger;

        CfgPrefabIndex = Config.Bind("Item", "PrefabIndex", 20517,
            "Item slot in PrefabsDirectory. Do NOT change once the canvas exists in your saves.");
        CfgRadius = Config.Bind("Hatches", "ZoneRadius", 2.5f,
            "Max distance (m) between a hatch and a water overflow zone for the hatch to cover it.");
        CfgLeakBoats = Config.Bind("Hatches", "OpenCompanionwayBoats", DefaultLeakBoats,
            "Boats with an open companionway to the hold (comma-separated, part of the boat name). Covered hatches on them still let some water in.");
        CfgLeak = Config.Bind("Hatches", "CompanionwayLeak", 0.45f,
            "Share of water (0..1) that still gets into the hold on such boats.");
        CfgBattens = Config.Bind("Visual", "Battens", true,
            "Draw wooden battens and nails around a fitted canvas.");
        CfgDebug = Config.Bind("Debug", "DebugTools", false,
            "F4 - spawn a canvas, F9 - overflow zone markers, F10 - debug window.");

        // old config from 0.3.0 still has the old default - update it
        if (CfgLeakBoats.Value.Trim().ToLowerInvariant() == OldLeakBoats)
            CfgLeakBoats.Value = DefaultLeakBoats;

        Sun.OnNewDay += Hatches.DailyWear;
        new Harmony("yourname.hatchtarp").PatchAll();
        Log.LogInfo("HatchTarp 0.4.1 loaded");
    }

    void Update()
    {
        if (!Tarp.Injected) Tarp.TryInjectFallback();

        if (Time.unscaledTime > nextTick)
        {
            nextTick = Time.unscaledTime + 2f;
            Zones = FindObjectsOfType<WaveSplashZone>();
            Hatches.Tick();
            Shops.Tick();
        }

        if (!CfgDebug.Value) return;

        if (Input.GetKeyDown(KeyCode.F4))
        {
            var cam = Camera.main;
            if (cam) GiveTarp(100f, cam.transform.position + cam.transform.forward, null);
        }
        if (Input.GetKeyDown(KeyCode.F9)) ToggleMarkers();
        if (Input.GetKeyDown(KeyCode.F10)) hudOn = !hudOn;

        if (markersOn)
            foreach (var kv in markers)
                if (kv.Value)
                    kv.Value.GetComponent<Renderer>().material.color =
                        Hatches.CoveredZones.ContainsKey(kv.Key) ? Color.green : Color.red;
    }

    // ---- put a canvas into the player's hands ----
    internal void GiveTarp(float health, Vector3 pos, GoPointer pointer)
    {
        if (!Tarp.Ready) { Log.LogWarning("Oiled canvas is not registered - can't give it"); return; }
        if (pointer == null) pointer = Tarp.MousePointer();
        if (pointer == null || Tarp.HeldItem(pointer) != null) return;
        StartCoroutine(GiveRoutine(health, pos, pointer));
    }

    IEnumerator GiveRoutine(float health, Vector3 pos, GoPointer pointer)
    {
        // spawn high above the boat so the item's physics body can't push the hull
        var go = Instantiate(Tarp.Template, pos + Vector3.up * 500f, Quaternion.identity);
        var item = go.GetComponent<ShipItemTarp>();
        item.health = health;
        Freeze(item, true);

        // wait for ShipItem.LoadAfterDelay
        for (int i = 0; i < 4; i++)
        {
            yield return null;
            if (!item) yield break;
            Freeze(item, true);
        }

        item.health = health;
        item.Sell(); // mark as bought and register for saving

        item.transform.position = pos;
        try { item.ResetRigidbody(); } catch { }
        Freeze(item, true);

        if (pointer && Tarp.HeldItem(pointer) == null) Tarp.PickUp(pointer, item);

        // keep collisions off while the item travels to the hand
        float until = Time.time + 0.6f;
        while (Time.time < until)
        {
            if (!item) yield break;
            Freeze(item, true);
            yield return null;
        }
        Freeze(item, false);
    }

    static void Freeze(ShipItem item, bool on)
    {
        if (!item || item.itemRigidbodyC == null) return;
        var rb = item.itemRigidbodyC.GetComponent<Rigidbody>();
        if (!rb) return;
        rb.detectCollisions = !on;
        if (on && !rb.isKinematic)
        {
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
    }

    // ---- debug ----
    void ToggleMarkers()
    {
        markersOn = !markersOn;
        foreach (var m in markers.Values) if (m) Destroy(m);
        markers.Clear();
        if (!markersOn) return;

        foreach (var z in FindObjectsOfType<WaveSplashZone>())
        {
            var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            DestroyImmediate(s.GetComponent<Collider>());
            s.transform.SetParent(z.transform, false);
            s.transform.localScale = Vector3.one * 0.3f;
            markers[z] = s;
        }
    }

    void OnGUI()
    {
        if (!CfgDebug.Value || !hudOn) return;

        var cam = Camera.main;
        var sb = new StringBuilder();
        sb.AppendLine("HatchTarp 0.3.1  [F4 spawn canvas, F9 markers, F10 window]");
        sb.AppendLine("Item: " + (Tarp.Ready ? "registered, slot " + CfgPrefabIndex.Value : "NOT registered"));
        int lines = 2;

        if (GameState.lastBoat)
        {
            var dmg = GameState.lastBoat.GetComponent<BoatDamage>();
            if (dmg)
            {
                float leak = Leak.For(dmg);
                sb.AppendLine($"Boat: {GameState.lastBoat.name}  water = {dmg.waterLevel:F3}" +
                              (leak > 0f ? $"  (open companionway, leak {leak:P0})" : ""));
                lines++;
            }
        }

        if (cam)
        {
            sb.AppendLine("--- hatches nearby ---"); lines++;
            foreach (var td in FindObjectsOfType<GPButtonTrapdoor>())
            {
                if (Vector3.Distance(cam.transform.position, td.transform.position) > 15f) continue;
                sb.AppendLine("  " + Hatches.Describe(td)); lines++;
            }
            sb.AppendLine("--- overflow zones nearby ---"); lines++;
            foreach (var z in Zones)
            {
                if (!z) continue;
                float d = Vector3.Distance(cam.transform.position, z.transform.position);
                if (d > 40f) continue;
                string st = Hatches.CoveredZones.ContainsKey(z) ? $"COVERED, passes {Hatches.Pass(z, null):P0}" : "open";
                sb.AppendLine($"  {z.name}  {d:F1} m  {st}");
                lines++;
            }
        }

        GUI.Box(new Rect(10, 10, 640, 14 + lines * 20), "");
        GUI.Label(new Rect(18, 14, 630, lines * 20 + 10), sb.ToString());
    }
}

// ============================================================================
//  ITEM CLASS
// ============================================================================
public class ShipItemTarp : ShipItem
{
    public override void UpdateLookText()
    {
        base.UpdateLookText();
        if (sold && !held) lookText = $"{name}\n{health:0}%";
    }
}

// ============================================================================
//  ITEM REGISTRATION in PrefabsDirectory
// ============================================================================
static class Tarp
{
    internal static GameObject Template;
    internal static bool Injected;
    internal static bool Ready;
    internal static float OakumFull = 1f; // amount of a fresh oakum barrel

    static float nextFallback;

    static readonly FieldInfo dirField = AccessTools.Field(typeof(PrefabsDirectory), "directory");
    static readonly MethodInfo populate = AccessTools.Method(typeof(PrefabsDirectory), "PopulateShipItems");
    static readonly FieldInfo heldField = AccessTools.Field(typeof(GoPointer), "heldItem");
    static readonly MethodInfo dropItem = AccessTools.Method(typeof(GoPointer), "DropItem", new Type[0]);
    static readonly MethodInfo pickUp = AccessTools.Method(typeof(GoPointer), "PickUpItem", new[] { typeof(PickupableItem) });

    internal static PickupableItem HeldItem(GoPointer p) =>
        p && heldField != null ? heldField.GetValue(p) as PickupableItem : null;

    internal static void Drop(GoPointer p) { if (p && dropItem != null) dropItem.Invoke(p, null); }
    internal static void PickUp(GoPointer p, PickupableItem item) { if (p && pickUp != null) pickUp.Invoke(p, new object[] { item }); }

    internal static GoPointer MousePointer()
    {
        foreach (var p in UnityEngine.Object.FindObjectsOfType<GoPointer>())
            if (p.type == GoPointer.PointerType.crosshairMouse) return p;
        return null;
    }

    internal static void TryInjectFallback()
    {
        if (Time.unscaledTime < nextFallback) return;
        nextFallback = Time.unscaledTime + 1f;
        var pd = UnityEngine.Object.FindObjectOfType<PrefabsDirectory>();
        if (!pd) return;
        Inject(pd);
        if (Ready && populate != null) populate.Invoke(pd, null);
    }

    internal static void Inject(PrefabsDirectory pd)
    {
        if (dirField == null)
        {
            Plugin.Log.LogError("PrefabsDirectory.directory not found!");
            Injected = true;
            return;
        }
        var dir = dirField.GetValue(pd) as GameObject[];
        if (dir == null) return;

        int idx = Plugin.CfgPrefabIndex.Value;

        if (Template == null)
        {
            Template = ItemModel.Build(dir, idx);
            if (Template == null) return;
        }

        if (idx >= dir.Length)
        {
            Array.Resize(ref dir, idx + 1);
            dirField.SetValue(pd, dir);
        }

        if (dir[idx] != null && dir[idx] != Template)
        {
            Plugin.Log.LogError($"Slot {idx} is taken by '{dir[idx].name}'. Change PrefabIndex in the config!");
            Injected = true;
            Ready = false;
            return;
        }

        dir[idx] = Template;
        Injected = true;
        Ready = true;
        Plugin.Log.LogInfo($"Oiled canvas registered in slot {idx} (directory length {dir.Length})");
    }
}

[HarmonyPatch(typeof(PrefabsDirectory), "PopulateShipItems")]
static class PrefabsDirectoryPatch
{
    static void Prefix(PrefabsDirectory __instance) => Tarp.Inject(__instance);
}

// ============================================================================
//  ITEM MODEL: looks of the wool bale, handling of the oakum
// ============================================================================
static class ItemModel
{
    static GameObject holder;

    internal static GameObject Build(GameObject[] dir, int idx)
    {
        var oakum = dir.FirstOrDefault(g => g != null && g.GetComponent<ShipItemOakum>() != null);
        if (oakum == null) { Plugin.Log.LogError("Oakum prefab not found!"); return null; }

        GameObject src = oakum;
        int mi = Balance.ModelIndex;
        if (mi >= 0 && mi < dir.Length && dir[mi] != null && dir[mi].GetComponent<ShipItem>() != null)
            src = dir[mi];
        else
            Plugin.Log.LogWarning($"Model item {mi} not found, using the oakum model");

        if (holder == null)
        {
            holder = new GameObject("HatchTarp templates");
            holder.SetActive(false); // Awake is not called inside an inactive object
            UnityEngine.Object.DontDestroyOnLoad(holder);
        }

        var go = UnityEngine.Object.Instantiate(src, holder.transform);
        go.name = idx + " oiled canvas";

        var old = go.GetComponent<ShipItem>();
        var tarp = go.AddComponent<ShipItemTarp>();

        // 1) copy all fields from the model's own script
        foreach (var f in AllFields(typeof(ShipItem)))
            f.SetValue(tarp, f.GetValue(old));

        // 2) handling from the oakum (plain values only)
        var ob = oakum.GetComponent<ShipItem>();
        tarp.big = ob.big;
        tarp.mass = ob.mass;
        tarp.furniturePlaceHeight = ob.furniturePlaceHeight;
        tarp.category = ob.category;
        tarp.wallAttachment = false;
        tarp.sold = false;
        tarp.nailed = false;

        // 3) redirect references from the old script to the new one
        foreach (var c in go.GetComponentsInChildren<Component>(true))
        {
            if (c == null || c == old || c == tarp) continue;
            foreach (var f in AllFields(c.GetType()))
            {
                if (!f.FieldType.IsAssignableFrom(typeof(ShipItemTarp))) continue;
                if (ReferenceEquals(f.GetValue(c), old)) f.SetValue(c, tarp);
            }
        }
        UnityEngine.Object.DestroyImmediate(old);

        // 4) not a mission cargo
        foreach (var g in go.GetComponentsInChildren<Good>(true))
            UnityEngine.Object.DestroyImmediate(g);

        // 5) fixed parameters
        tarp.name = Balance.ItemName;
        tarp.health = 100f;
        tarp.amount = 0f;
        tarp.value = Balance.Price;
        tarp.inventoryScale *= Balance.InventoryScale;
        tarp.holdDistance = Balance.HoldDistance;
        Tarp.OakumFull = ob.amount > 0f ? ob.amount : 1f;

        var sp = go.GetComponent<SaveablePrefab>();
        if (sp) Traverse.Create(sp).Field("prefabIndex").SetValue(idx);
        else Plugin.Log.LogError("The model has no SaveablePrefab!");

        // 6) darken, keeping the texture
        var tint = new Color(0.55f, 0.47f, 0.36f, 1f);
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                if (!mats[i]) continue;
                var m = new Material(mats[i]);
                if (m.HasProperty("_Color")) m.color = m.color * tint;
                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.35f);
                mats[i] = m;
            }
            r.sharedMaterials = mats;
        }

        Plugin.Log.LogInfo($"Canvas template: model '{src.name}', handling '{oakum.name}', price {tarp.value}, oakum barrel = {Tarp.OakumFull}");
        Plugin.Log.LogInfo("Colliders: " + string.Join(", ",
            go.GetComponentsInChildren<Collider>(true).Select(c => $"{c.GetType().Name} on '{c.name}'")));
        return go;
    }

    internal static IEnumerable<FieldInfo> AllFields(Type t)
    {
        for (; t != null && t != typeof(MonoBehaviour) && t != typeof(Component); t = t.BaseType)
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                yield return f;
    }
}

// ============================================================================
//  HATCHES: cover / uncover / wear / repair / save
// ============================================================================
class TarpCover
{
    public GPButtonTrapdoor td;
    public string key;
    public float health;
    public GameObject visual;
    public string oldLook;
    public List<WaveSplashZone> zones;
}

static class Hatches
{
    // zone -> canvases that cover it (all hatches near the zone must be covered)
    internal static readonly Dictionary<WaveSplashZone, List<TarpCover>> CoveredZones = new Dictionary<WaveSplashZone, List<TarpCover>>();
    static readonly Dictionary<GPButtonTrapdoor, TarpCover> covers = new Dictionary<GPButtonTrapdoor, TarpCover>();
    static readonly Dictionary<string, float> pending = new Dictionary<string, float>();
    static bool needRead;
    const string ModKey = "HatchTarp.covers";

    static readonly FieldInfo inMotionField = AccessTools.Field(typeof(GPButtonTrapdoor), "inMotion");
    static readonly FieldInfo pointedAtByField = AccessTools.Field(typeof(GoPointerButton), "pointedAtBy");

    internal static GoPointer PointerOf(GoPointerButton b) =>
        pointedAtByField != null ? pointedAtByField.GetValue(b) as GoPointer : null;

    static bool InMotion(GPButtonTrapdoor td) =>
        inMotionField != null && (bool)inMotionField.GetValue(td);

    internal static bool IsCovered(GPButtonTrapdoor td) => covers.ContainsKey(td);

    // ---- geometry ----
    static Transform Root(Component c)
    {
        var so = c.GetComponentInParent<SaveableObject>();
        return so ? so.transform : c.transform.root;
    }

    static string Key(GPButtonTrapdoor td)
    {
        var so = td.GetComponentInParent<SaveableObject>();
        string boat = so ? so.sceneIndex.ToString() : td.transform.root.name;
        var root = Root(td);
        string path = td.name;
        for (var t = td.transform.parent; t != null && t != root; t = t.parent) path = t.name + "/" + path;
        return boat + "|" + path;
    }

    // true = horizontal (deck) hatch, false = vertical (door)
    internal static bool Geometry(GPButtonTrapdoor td, out int axis, out float sign, out Vector3 center)
    {
        axis = 2; sign = 1f; center = td.transform.position;
        var box = td.GetComponent<BoxCollider>();
        if (!box) return false;
        center = box.bounds.center;

        var s = Vector3.Scale(box.size, td.transform.lossyScale);
        s = new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
        axis = (s.x <= s.y && s.x <= s.z) ? 0 : (s.y <= s.z ? 1 : 2);

        var local = Vector3.zero;
        local[axis] = 1f;
        var w = td.transform.TransformVector(local).normalized;
        float d = Vector3.Dot(w, Vector3.up);
        sign = d >= 0 ? 1f : -1f;
        return Mathf.Abs(d) > 0.7f;
    }

    internal static List<WaveSplashZone> ZonesNear(GPButtonTrapdoor td)
    {
        var list = new List<WaveSplashZone>();
        Geometry(td, out _, out _, out var c);
        foreach (var z in Root(td).GetComponentsInChildren<WaveSplashZone>(true))
            if (Vector3.Distance(z.transform.position, c) <= Plugin.CfgRadius.Value) list.Add(z);
        return list;
    }

    static List<GPButtonTrapdoor> HatchesNear(WaveSplashZone z)
    {
        var list = new List<GPButtonTrapdoor>();
        foreach (var td in Root(z).GetComponentsInChildren<GPButtonTrapdoor>(true))
        {
            if (!Geometry(td, out _, out _, out var c)) continue;
            if (Vector3.Distance(z.transform.position, c) <= Plugin.CfgRadius.Value) list.Add(td);
        }
        return list;
    }

    internal static string Describe(GPButtonTrapdoor td)
    {
        bool flat = Geometry(td, out _, out _, out _);
        int n = flat ? ZonesNear(td).Count : 0;
        string state = covers.TryGetValue(td, out var c) ? $"COVERED {c.health:0}%" : (td.IsOpen() ? "open" : "closed");
        return $"{td.name}: {state}, {(flat ? "deck" : "vertical")}, zones nearby: {n}";
    }

    // ---- water: how much passes through a covered zone ----
    internal static float Pass(WaveSplashZone z, BoatDamage d)
    {
        if (!CoveredZones.TryGetValue(z, out var list)) return 1f;
        float worst = 100f;
        foreach (var c in list) worst = Mathf.Min(worst, c.health);
        float wearLeak = worst >= Balance.LeakStart ? 0f : (Balance.LeakStart - worst) / Balance.LeakStart;
        float boatLeak = d ? Leak.For(d) : 0f;
        return 1f - (1f - boatLeak) * (1f - wearLeak);
    }

    internal static void Wear(WaveSplashZone z, BoatDamage d, float heightDiff, float pass)
    {
        if (!CoveredZones.TryGetValue(z, out var list)) return;
        if (!GameState.playing || GameState.justStarted || GameState.justWokeUp) return;
        // same formula as BoatDamage.Overflow: water the canvas kept out this frame
        float blocked = d.waterIntakeRate * Time.deltaTime * 1.4f * heightDiff * (1f - pass);
        float loss = blocked * Balance.WearPerHold;
        foreach (var c in list) c.health = Mathf.Max(0f, c.health - loss);
    }

    internal static void DailyWear()
    {
        foreach (var c in covers.Values) c.health = Mathf.Max(0f, c.health - Balance.DailyWear);
        foreach (var k in pending.Keys.ToList()) pending[k] = Mathf.Max(0f, pending[k] - Balance.DailyWear);
    }

    // ---- cover / uncover / repair ----
    internal static bool CanCover(GPButtonTrapdoor td, ShipItemTarp tarp)
    {
        if (IsCovered(td)) return false;
        if (td.IsOpen() || InMotion(td)) return false;
        if (!Geometry(td, out _, out _, out _)) return false;
        if (ZonesNear(td).Count == 0) return false;
        if (tarp != null && tarp.health <= 1f) return false;
        return true;
    }

    internal static void Cover(GPButtonTrapdoor td, float health, bool silent = false)
    {
        var c = new TarpCover
        {
            td = td,
            key = Key(td),
            health = health,
            zones = ZonesNear(td),
            oldLook = td.lookText
        };
        c.visual = CanvasVisual.Make(td);
        covers[td] = c;

        Rebuild();
        RefreshAll();
        WriteModData();

        if (silent) return;
        try { UISoundPlayer.instance.PlayUISound(UISounds.oakum, 1f, 1f); } catch { }
    }

    internal static void Uncover(GPButtonTrapdoor td, GoPointer pointer)
    {
        if (!covers.TryGetValue(td, out var c)) return;
        covers.Remove(td);

        Vector3 pos = c.visual ? c.visual.transform.position + Vector3.up * 0.5f : td.transform.position;
        if (c.visual) UnityEngine.Object.Destroy(c.visual);
        td.lookText = c.oldLook ?? "";

        Rebuild();
        RefreshAll();
        WriteModData();
        Plugin.Instance.GiveTarp(c.health, pos, pointer);
    }

    internal static void Repair(GPButtonTrapdoor td, ShipItemOakum oak)
    {
        if (!covers.TryGetValue(td, out var c)) return;
        float missing = 100f - c.health;
        if (missing < 1f) return;

        float costFull = Tarp.OakumFull * Balance.RepairCostFull;
        float use = Mathf.Min(costFull * missing / 100f, oak.amount);
        if (use <= 0.0001f) return;

        oak.amount -= use;
        c.health = Mathf.Min(100f, c.health + use / costFull * 100f);
        oak.UpdateLookText();
        RefreshLook(c);
        WriteModData();

        try { UISoundPlayer.instance.PlayUISound(UISounds.oakum, 1f, 1f); } catch { }
    }

    // text shown when the player looks at a covered hatch
    static void RefreshLook(TarpCover c)
    {
        if (!c.td) return;
        string s = $"{Balance.ItemName}\n{c.health:0}%";
        if (c.health < Balance.LeakStart) s += " (leaking)";
        if (c.zones != null && c.zones.Any(z => z && !CoveredZones.ContainsKey(z)))
            s += "\ncover the next hatch too";
        c.td.lookText = s;
    }

    static void RefreshAll()
    {
        foreach (var c in covers.Values) RefreshLook(c);
    }

    static void Rebuild()
    {
        CoveredZones.Clear();
        foreach (var c in covers.Values)
            foreach (var z in c.zones)
            {
                if (!z || CoveredZones.ContainsKey(z)) continue;
                var hs = HatchesNear(z);
                if (hs.Count > 0 && hs.All(h => covers.ContainsKey(h)))
                    CoveredZones[z] = hs.Select(h => covers[h]).ToList();
            }
    }

    // ---- saving ----
    internal static void WriteModData()
    {
        if (needRead || GameState.modData == null) return; // save not read yet - don't overwrite
        var parts = new List<string>();
        foreach (var c in covers.Values)
            parts.Add(c.key + "|" + c.health.ToString("0.###", CultureInfo.InvariantCulture));
        foreach (var kv in pending)
            parts.Add(kv.Key + "|" + kv.Value.ToString("0.###", CultureInfo.InvariantCulture));
        GameState.modData[ModKey] = string.Join(";", parts);
    }

    static void ReadModData()
    {
        pending.Clear();
        if (GameState.modData == null) return;
        if (!GameState.modData.TryGetValue(ModKey, out var s) || string.IsNullOrEmpty(s)) return;

        foreach (var e in s.Split(';'))
        {
            int i = e.LastIndexOf('|');
            if (i <= 0) continue;
            float.TryParse(e.Substring(i + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var h);
            pending[e.Substring(0, i)] = h;
        }
        Plugin.Log.LogInfo($"Covered hatches read from save: {pending.Count}");
    }

    internal static void OnLoadGame()
    {
        foreach (var c in covers.Values)
        {
            if (c.visual) UnityEngine.Object.Destroy(c.visual);
            if (c.td) c.td.lookText = c.oldLook ?? "";
        }
        covers.Clear();
        pending.Clear();
        CoveredZones.Clear();
        needRead = true;
    }

    internal static void Tick()
    {
        if (needRead)
        {
            if (!GameState.playing || GameState.currentlyLoading) return;
            needRead = false;
            ReadModData();
        }

        // hatch gone (boat unloaded) - keep it for saving
        bool removed = false;
        foreach (var td in covers.Keys.ToList())
        {
            if (td) continue;
            var c = covers[td];
            pending[c.key] = c.health;
            covers.Remove(td);
            removed = true;
        }
        if (removed) Rebuild();

        RefreshAll();

        if (pending.Count == 0) return;
        foreach (var td in UnityEngine.Object.FindObjectsOfType<GPButtonTrapdoor>())
        {
            if (covers.ContainsKey(td) || td.IsOpen()) continue;
            string k = Key(td);
            if (!pending.TryGetValue(k, out var h)) continue;
            pending.Remove(k);
            Cover(td, h, true);
            Plugin.Log.LogInfo("Restored canvas on hatch " + k);
        }
    }
}

[HarmonyPatch(typeof(SaveLoadManager), "LoadModData")]
static class LoadModDataPatch
{
    static void Postfix() => Hatches.OnLoadGame();
}

[HarmonyPatch(typeof(SaveLoadManager), "SaveModData")]
static class SaveModDataPatch
{
    static void Prefix() => Hatches.WriteModData();
}

// Left click on a hatch: the game calls OnActivate (open/close) first...
[HarmonyPatch(typeof(GPButtonTrapdoor), "OnActivate", new Type[0])]
static class TrapdoorActivatePatch
{
    static bool Prefix(GPButtonTrapdoor __instance)
    {
        var pointer = Hatches.PointerOf(__instance);
        var held = Tarp.HeldItem(pointer);

        if (Hatches.IsCovered(__instance))
        {
            if (held == null && pointer != null) Hatches.Uncover(__instance, pointer);
            else if (held is ShipItemOakum oak && oak.sold) Hatches.Repair(__instance, oak);
            return false; // a covered hatch never opens
        }

        if (held is ShipItemTarp tarp && tarp.sold && Hatches.CanCover(__instance, tarp))
            return false; // covered in OnItemClick
        return true;
    }
}

// ...and then OnItemClick (clicked with an item)
[HarmonyPatch(typeof(GoPointerButton), "OnItemClick")]
static class TrapdoorItemClickPatch
{
    static bool Prefix(GoPointerButton __instance, PickupableItem heldItem, ref bool __result)
    {
        if (!(__instance is GPButtonTrapdoor td)) return true;
        if (!(heldItem is ShipItemTarp tarp) || !tarp.sold) return true;
        if (!Hatches.CanCover(td, tarp)) return true;

        Hatches.Cover(td, tarp.health);
        Tarp.Drop(tarp.held);
        tarp.DestroyItem();

        __result = false; // we removed the item ourselves
        return false;
    }
}

// ============================================================================
//  OVERFLOW ZONES: a covered zone lets in only what leaks through
// ============================================================================
[HarmonyPatch(typeof(WaveSplashZone), "Update")]
static class ZoneUpdatePatch
{
    static readonly MethodInfo overflow = AccessTools.Method(typeof(BoatDamage), nameof(BoatDamage.Overflow));
    static readonly MethodInfo gate = AccessTools.Method(typeof(ZoneUpdatePatch), nameof(Gate));
    static readonly MethodInfo setEmissionMethod = AccessTools.Method(typeof(WaveSplashZone), "SetEmission");
    static readonly FieldInfo audioField = AccessTools.Field(typeof(WaveSplashZone), "audio");
    static readonly FieldInfo damageField = AccessTools.Field(typeof(WaveSplashZone), "damage");

    // replace "damage.Overflow(heightDiff)" with "Gate(damage, heightDiff, this)"
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        int replaced = 0;
        foreach (var ins in instructions)
        {
            if (ins.Calls(overflow))
            {
                var loadThis = new CodeInstruction(OpCodes.Ldarg_0);
                loadThis.labels.AddRange(ins.labels);
                ins.labels.Clear();
                yield return loadThis;
                yield return new CodeInstruction(OpCodes.Call, gate);
                replaced++;
                continue;
            }
            yield return ins;
        }
        if (replaced == 0) Plugin.Log.LogError("ZoneUpdatePatch: Overflow call not found!");
        else Plugin.Log.LogInfo($"ZoneUpdatePatch: Overflow calls replaced: {replaced}");
    }

    static void Gate(BoatDamage damage, float heightDiff, WaveSplashZone zone)
    {
        float pass = Hatches.Pass(zone, damage);
        if (pass < 1f) Hatches.Wear(zone, damage, heightDiff, pass);

        if (pass >= 0.999f) damage.Overflow(heightDiff);
        else if (pass > 0.001f) damage.Overflow(heightDiff * pass);
    }

    static void Postfix(WaveSplashZone __instance)
    {
        if (!Hatches.CoveredZones.ContainsKey(__instance)) return;

        // no splash particles over a covered hatch
        if (setEmissionMethod != null) setEmissionMethod.Invoke(__instance, new object[] { false });

        // water still comes in (open companionway / worn canvas) - keep the sound
        var dmg = damageField != null ? damageField.GetValue(__instance) as BoatDamage : null;
        if (Hatches.Pass(__instance, dmg) > 0.001f) return;

        if (audioField != null)
        {
            var a = audioField.GetValue(__instance) as AudioSource;
            if (a) a.volume = 0f;
        }
    }
}

// ============================================================================
//  LEAK through an open companionway (boats from the config)
// ============================================================================
static class Leak
{
    static readonly Dictionary<BoatDamage, float> cache = new Dictionary<BoatDamage, float>();
    static string snapshot;

    internal static float For(BoatDamage d)
    {
        if (!d) return 0f;
        string cfg = Plugin.CfgLeakBoats.Value + "|" + Plugin.CfgLeak.Value;
        if (cfg != snapshot) { cache.Clear(); snapshot = cfg; }
        if (cache.TryGetValue(d, out var v)) return v;

        string boat = d.name.Replace("(Clone)", "").ToLowerInvariant();
        v = 0f;
        foreach (var part in Plugin.CfgLeakBoats.Value.Split(','))
        {
            string s = part.Trim().ToLowerInvariant();
            if (s.Length > 0 && boat.Contains(s)) { v = Mathf.Clamp01(Plugin.CfgLeak.Value); break; }
        }
        cache[d] = v;
        return v;
    }
}

// ============================================================================
//  CANVAS ON A HATCH: wrinkled cloth, folded edges, battens with nails
// ============================================================================
static class CanvasVisual
{
    const float Overhang = 0.045f;
    const float Drop = 0.07f;
    const float Lift = 0.005f;
    const float Wrinkle = 0.005f;
    const float Cell = 0.03f;
    const int EdgeSteps = 6;
    const float TexTile = 1.6f;

    static Material cloth, wood, iron;

    internal static GameObject Make(GPButtonTrapdoor td)
    {
        Bounds b;
        var mf = td.GetComponent<MeshFilter>();
        if (mf && mf.sharedMesh) b = mf.sharedMesh.bounds;
        else
        {
            var box = td.GetComponent<BoxCollider>();
            b = box ? new Bounds(box.center, box.size) : new Bounds(Vector3.zero, new Vector3(1f, 0.05f, 1f));
        }

        Vector3 ls = td.transform.lossyScale;
        Vector3 ws = new Vector3(Mathf.Abs(b.size.x * ls.x), Mathf.Abs(b.size.y * ls.y), Mathf.Abs(b.size.z * ls.z));
        int n = (ws.x <= ws.y && ws.x <= ws.z) ? 0 : (ws.y <= ws.z ? 1 : 2);
        int u = (n + 1) % 3, v = (n + 2) % 3;

        Vector3 eN = Vector3.zero, eV = Vector3.zero;
        eN[n] = 1f; eV[v] = 1f;
        float sign = Vector3.Dot(td.transform.TransformDirection(eN), Vector3.up) >= 0 ? 1f : -1f;
        float su = Mathf.Max(1e-4f, Mathf.Abs(ls[u]));
        float sv = Mathf.Max(1e-4f, Mathf.Abs(ls[v]));
        float sn = Mathf.Max(1e-4f, Mathf.Abs(ls[n]));

        Vector3 top = b.center;
        top[n] += sign * b.extents[n];

        var root = new GameObject("HatchTarp canvas");
        root.transform.SetParent(td.transform, false);
        root.transform.position = td.transform.TransformPoint(top);
        root.transform.rotation = Quaternion.LookRotation(
            td.transform.TransformDirection(eV), td.transform.TransformDirection(eN) * sign);
        root.transform.localScale = new Vector3(1f / su, 1f / sn, 1f / sv);

        float A = b.extents[u] * su, B = b.extents[v] * sv;

        // follow the real slope of the hatch model
        Transform parent = root.transform;
        if (HatchSurface.Fit(td, root.transform, ws[n], A, B, out float ka, out float kb, out float kc))
        {
            Plugin.Log.LogInfo($"Hatch '{td.name}': slope x {ka:F3}, z {kb:F3}, height {kc:F3}");
            if (Mathf.Abs(ka) > 0.005f || Mathf.Abs(kb) > 0.005f || Mathf.Abs(kc) > 0.003f)
            {
                var tilt = new GameObject("tilt").transform;
                tilt.SetParent(root.transform, false);
                tilt.localPosition = new Vector3(0f, kc, 0f);
                tilt.localRotation = Quaternion.FromToRotation(Vector3.up, new Vector3(-ka, 1f, -kb).normalized);
                A *= Mathf.Sqrt(1f + ka * ka);
                B *= Mathf.Sqrt(1f + kb * kb);
                parent = tilt;
            }
        }

        BuildCloth(parent, A, B, td.name.GetHashCode());
        if (Plugin.CfgBattens.Value) BuildBattens(parent, A, B);
        return root;
    }

    static void Axis(float half, out float[] pos, out float[] drop, out float[] edge)
    {
        int inner = Mathf.Clamp(Mathf.RoundToInt(2f * half / Cell), 6, 60);
        int count = inner + 1 + 2 * EdgeSteps;
        pos = new float[count]; drop = new float[count]; edge = new float[count];
        int k = 0;
        for (int i = EdgeSteps; i >= 1; i--) Edge(-1, i, ref k, half, pos, drop, edge);
        for (int i = 0; i <= inner; i++) { pos[k] = -half + 2f * half * i / inner; k++; }
        for (int i = 1; i <= EdgeSteps; i++) Edge(1, i, ref k, half, pos, drop, edge);
    }

    static void Edge(int side, int i, ref int k, float half, float[] pos, float[] drop, float[] edge)
    {
        float t = (float)i / EdgeSteps;
        float a = t * Mathf.PI * 0.5f;
        pos[k] = side * (half + Overhang * Mathf.Sin(a));
        drop[k] = Drop * (1f - Mathf.Cos(a));
        edge[k] = t;
        k++;
    }

    static void BuildCloth(Transform root, float A, float B, int seed)
    {
        Axis(A, out var xs, out var dx, out var tx);
        Axis(B, out var zs, out var dz, out var tz);
        int nx = xs.Length, nz = zs.Length, half = nx * nz;
        float ox = (seed & 255) * 0.37f, oz = ((seed >> 8) & 255) * 0.37f;
        var uvOff = new Vector2((seed & 1023) / 1023f, ((seed >> 10) & 1023) / 1023f);

        var verts = new Vector3[half * 2];
        var uvs = new Vector2[half * 2];
        for (int j = 0; j < nz; j++)
            for (int i = 0; i < nx; i++)
            {
                float x = xs[i], z = zs[j];
                float t = Mathf.Max(tx[i], tz[j]);
                float nearEdge = Mathf.Clamp01(Mathf.Max(Mathf.Abs(x) / A, Mathf.Abs(z) / B));
                float folds = (Mathf.PerlinNoise(ox + x * 2.2f, oz + z * 2.2f) - 0.5f) * 2f;
                float fine = (Mathf.PerlinNoise(ox + x * 9f, oz + z * 9f) - 0.5f) * 0.4f;
                float y = Lift
                        + (folds + fine) * Wrinkle * (0.35f + 0.65f * nearEdge) * (1f - 0.6f * t)
                        - Mathf.Max(dx[i], dz[j]);

                int id = j * nx + i;
                verts[id] = verts[id + half] = new Vector3(x, y, z);
                uvs[id] = uvs[id + half] = new Vector2(x, z) / TexTile + uvOff;
            }

        var tris = new int[(nx - 1) * (nz - 1) * 12];
        int k = 0;
        for (int j = 0; j < nz - 1; j++)
            for (int i = 0; i < nx - 1; i++)
            {
                int a = j * nx + i, b = a + 1, c = a + nx, d = c + 1;
                tris[k++] = a; tris[k++] = c; tris[k++] = b;
                tris[k++] = b; tris[k++] = c; tris[k++] = d;
                tris[k++] = a + half; tris[k++] = b + half; tris[k++] = c + half;
                tris[k++] = b + half; tris[k++] = d + half; tris[k++] = c + half;
            }

        var mesh = new Mesh { name = "HatchTarp cloth" };
        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        var go = new GameObject("cloth");
        go.transform.SetParent(root, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = ClothMat();
        r.shadowCastingMode = ShadowCastingMode.On;
        r.receiveShadows = true;
    }

    static void BuildBattens(Transform root, float A, float B)
    {
        const float W = 0.035f, H = 0.018f, NailStep = 0.22f;
        float y = Lift + Wrinkle * 1.3f + H * 0.5f;
        float gx = A - W * 0.5f;
        float gz = B - W * 0.5f;

        Bar(root, new Vector3(gx, y, 0), new Vector3(W, H, 2 * B));
        Bar(root, new Vector3(-gx, y, 0), new Vector3(W, H, 2 * B));
        Bar(root, new Vector3(0, y, gz), new Vector3(2 * A - 2 * W, H, W));
        Bar(root, new Vector3(0, y, -gz), new Vector3(2 * A - 2 * W, H, W));

        float ny = y + H * 0.5f;
        var head = new Vector3(0.014f, 0.006f, 0.014f);

        int cz = Mathf.Max(2, Mathf.RoundToInt(2 * B / NailStep));
        for (int i = 0; i < cz; i++)
        {
            float z = Mathf.Lerp(-B, B, (i + 0.5f) / cz);
            Nail(root, new Vector3(gx, ny, z), head);
            Nail(root, new Vector3(-gx, ny, z), head);
        }
        int cx = Mathf.Max(2, Mathf.RoundToInt(2 * (A - W) / NailStep));
        for (int i = 0; i < cx; i++)
        {
            float x = Mathf.Lerp(-A + W, A - W, (i + 0.5f) / cx);
            Nail(root, new Vector3(x, ny, gz), head);
            Nail(root, new Vector3(x, ny, -gz), head);
        }
    }

    static void Bar(Transform root, Vector3 pos, Vector3 size) =>
        Prim(PrimitiveType.Cube, root, pos, size, WoodMat());

    static void Nail(Transform root, Vector3 pos, Vector3 size) =>
        Prim(PrimitiveType.Sphere, root, pos, size, IronMat());

    static void Prim(PrimitiveType type, Transform root, Vector3 pos, Vector3 size, Material mat)
    {
        var go = GameObject.CreatePrimitive(type);
        UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(root, false);
        go.transform.localPosition = pos;
        go.transform.localScale = size;
        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = ShadowCastingMode.On;
    }

    static Shader Std()
    {
        var s = Shader.Find("Standard");
        if (s) return s;
        var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
        s = tmp.GetComponent<Renderer>().sharedMaterial.shader;
        UnityEngine.Object.DestroyImmediate(tmp);
        return s;
    }

    static Material ClothMat()
    {
        if (cloth) return cloth;
        cloth = new Material(Std()) { color = Color.white, mainTexture = CanvasTexture() };
        if (cloth.HasProperty("_Glossiness")) cloth.SetFloat("_Glossiness", 0.4f);
        if (cloth.HasProperty("_Metallic")) cloth.SetFloat("_Metallic", 0f);
        return cloth;
    }

    static Material WoodMat()
    {
        if (wood) return wood;
        wood = new Material(Std()) { color = new Color(0.22f, 0.15f, 0.09f) };
        if (wood.HasProperty("_Glossiness")) wood.SetFloat("_Glossiness", 0.15f);
        return wood;
    }

    static Material IronMat()
    {
        if (iron) return iron;
        iron = new Material(Std()) { color = new Color(0.13f, 0.12f, 0.11f) };
        if (iron.HasProperty("_Glossiness")) iron.SetFloat("_Glossiness", 0.35f);
        if (iron.HasProperty("_Metallic")) iron.SetFloat("_Metallic", 0.6f);
        return iron;
    }

    // large oil stains, worn spots, soft folds
    static Texture2D CanvasTexture()
    {
        const int S = 512;
        float st = Balance.StainStrength;
        var tex = new Texture2D(S, S, TextureFormat.RGB24, true);
        var px = new Color[S * S];
        var rnd = new System.Random(1234);

        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float u = (float)x / S, v = (float)y / S;
                float oil = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.42f, 0.70f, Fbm(u, v, 3, 5, 11)));
                float wear = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.55f, 0.78f, Fbm(u, v, 2, 4, 23)));
                float warp = (Fbm(u, v, 2, 3, 37) - 0.5f) * 0.12f;
                float folds = Fbm(u + warp, 0.31f, 7, 3, 51);
                float grain = 0.97f + 0.03f * (float)rnd.NextDouble();

                float k = (1f - 0.28f * st * oil) * (1f + 0.12f * st * wear)
                        * (1f - 0.12f * st + 0.24f * st * folds) * grain * (1f - 0.1f * st);
                px[y * S + x] = new Color(0.38f * k, 0.31f * k, 0.19f * k);
            }

        tex.SetPixels(px);
        tex.Apply(true);
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Trilinear;
        tex.anisoLevel = 4;
        return tex;
    }

    static float Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777215f;
        }
    }

    static int Mod(int a, int m) { int r = a % m; return r < 0 ? r + m : r; }

    static float Noise(float u, float v, int p, int seed)
    {
        float x = u * p, y = v * p;
        int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
        float fx = x - x0, fy = y - y0;
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);
        int xa = Mod(x0, p), xb = Mod(x0 + 1, p), ya = Mod(y0, p), yb = Mod(y0 + 1, p);
        float a = Hash(xa, ya, seed), b = Hash(xb, ya, seed);
        float c = Hash(xa, yb, seed), d = Hash(xb, yb, seed);
        return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
    }

    static float Fbm(float u, float v, int p, int octaves, int seed)
    {
        float sum = 0f, amp = 0.5f, norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            sum += amp * Noise(u, v, p, seed + i * 17);
            norm += amp;
            amp *= 0.5f;
            p *= 2;
        }
        return sum / norm;
    }
}

// ============================================================================
//  HATCH SURFACE: measures the real slope of the hatch model.
//  Vanilla meshes are not readable, so the hatch is rendered from two sides
//  by a tiny hidden camera and the top edge of its silhouette is fitted.
// ============================================================================
static class HatchSurface
{
    const int W = 256, H = 128;
    const float Dist = 6f;
    static readonly Color Bg = new Color(1f, 0f, 1f, 0f); // magenta = empty
	const float TopPercentile = 0.92f; // the canvas lies above this share of the hatch top (the rest = handles, rings)
    const float Clearance = 0.004f;    // extra gap (m) so the wrinkles don't dip into the hatch
    const float MaxLift = 0.03f;       // max lift (m) above the fitted line
    // top surface in root's local metres: y = a*x + b*z + c
    internal static bool Fit(GPButtonTrapdoor td, Transform root, float thick, float A, float B,
                             out float a, out float b, out float c)
    {
        a = b = c = 0f;
        var rends = td.GetComponentsInChildren<Renderer>(false)
            .Where(r => r.enabled && !(r is ParticleSystemRenderer) && !Ours(r.transform, td.transform))
            .ToList();
        if (rends.Count == 0) return false;

        int layer = FreeLayer();
        var oldLayers = rends.Select(r => r.gameObject.layer).ToList();
        var rt = RenderTexture.GetTemporary(W, H, 16, RenderTextureFormat.ARGB32);
        var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
        var go = new GameObject("HatchTarp probe") { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            foreach (var r in rends) r.gameObject.layer = layer;

            var cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Bg;
            cam.cullingMask = 1 << layer;
            cam.renderingPath = RenderingPath.Forward;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.useOcclusionCulling = false;
            cam.targetTexture = rt;
            cam.aspect = (float)W / H;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = Dist * 2f;

            // looking along x: height over z;  looking along z: height over x
            var pz = Profile(cam, rt, tex, root, root.right, thick, B, true);
            var px = Profile(cam, rt, tex, root, root.forward, thick, A, false);
            if (!Line(pz, B, out float kb, out float cz, out float tz)) return false;
            if (!Line(px, A, out float ka, out float cx, out float tx)) return false;

            a = Mathf.Clamp(ka, -0.35f, 0.35f);
            b = Mathf.Clamp(kb, -0.35f, 0.35f);
            // each profile is the highest point across the other axis - remove that part;
            // take the higher of the two estimates so nothing pokes through the canvas
            float fromX = cx + tx - Mathf.Abs(b) * B;
            float fromZ = cz + tz - Mathf.Abs(a) * A;
            c = Mathf.Max(fromX, fromZ) + Clearance;
            c = Mathf.Clamp(c, -thick, 0.04f);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("Hatch surface measure failed: " + e.Message);
            return false;
        }
        finally
        {
            for (int i = 0; i < rends.Count; i++)
                if (rends[i]) rends[i].gameObject.layer = oldLayers[i];
            RenderTexture.ReleaseTemporary(rt);
            UnityEngine.Object.DestroyImmediate(tex);
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    static List<Vector2> Profile(Camera cam, RenderTexture rt, Texture2D tex, Transform root,
                                 Vector3 look, float thick, float span, bool alongZ)
    {
        Vector3 center = root.TransformPoint(0f, -thick * 0.5f, 0f);
        cam.transform.position = center - look * Dist;
        cam.transform.rotation = Quaternion.LookRotation(look, root.up);
        cam.orthographicSize = Mathf.Max(thick * 0.6f + 0.03f, (span * 1.1f + 0.05f) / cam.aspect);

        var prev = RenderTexture.active;
        cam.Render();
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, W, H), 0, 0, false);
        RenderTexture.active = prev;
        var px = tex.GetPixels32();

        var list = new List<Vector2>();
        for (int i = 0; i < W; i++)
            for (int j = H - 1; j >= 0; j--)
            {
                var p = px[j * W + i];
                if (Mathf.Abs(p.r - 255) + p.g + Mathf.Abs(p.b - 255) < 40) continue; // background
                var w = cam.ViewportToWorldPoint(new Vector3((i + 0.5f) / W, (j + 1f) / H, Dist));
                var l = root.InverseTransformPoint(w);
                list.Add(new Vector2(alongZ ? l.z : l.x, l.y));
                break;
            }
        return list;
    }

    // straight line through the top edge; bumps (handles, rings) are dropped for the slope,
    // then the line is lifted onto the top of the surface
    static bool Line(List<Vector2> pts, float half, out float k, out float m, out float top)
    {
        k = m = top = 0f;
        var use = pts.Where(p => Mathf.Abs(p.x) < half * 0.85f).ToList(); // skip rounded edges
        if (use.Count < 12) return false;

        for (int it = 0; it < 3; it++)
        {
            Lsq(use, out k, out m);
            float kk = k, mm = m;
            var res = use.Select(p => p.y - (kk * p.x + mm)).ToList();
            float med = res.Select(x => Mathf.Abs(x)).OrderBy(x => x).ElementAt(res.Count / 2);
            var keep = use.Where((p, i) => res[i] < 2f * med + 0.004f).ToList();
            if (keep.Count < 12 || keep.Count == use.Count) break;
            use = keep;
        }
        Lsq(use, out k, out m);

        // how high the surface rises above the line over almost the whole width
        float k2 = k, m2 = m;
        var all = pts.Where(p => Mathf.Abs(p.x) < half * 0.97f)
                     .Select(p => p.y - (k2 * p.x + m2))
                     .OrderBy(x => x)
                     .ToList();
        if (all.Count > 0)
        {
            int i = Mathf.Min(all.Count - 1, (int)(all.Count * TopPercentile));
            top = Mathf.Clamp(all[i], 0f, MaxLift);
        }
        return true;
    }

    static void Lsq(List<Vector2> p, out float k, out float m)
    {
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        int n = p.Count;
        foreach (var q in p) { sx += q.x; sy += q.y; sxx += q.x * q.x; sxy += q.x * q.y; }
        double d = n * sxx - sx * sx;
        k = Math.Abs(d) < 1e-9 ? 0f : (float)((n * sxy - sx * sy) / d);
        m = (float)((sy - k * sx) / n);
    }

    static bool Ours(Transform t, Transform stop)
    {
        for (; t != null && t != stop; t = t.parent)
            if (t.name == "HatchTarp canvas") return true;
        return false;
    }

    static int FreeLayer()
    {
        for (int l = 31; l >= 8; l--)
            if (string.IsNullOrEmpty(LayerMask.LayerToName(l))) return l;
        return 31;
    }
}

// ============================================================================
//  SHOPS: a stack of canvas bales on the ground next to the counter with oakum
// ============================================================================
static class Shops
{
    class Group
    {
        public string key;
        public ShopItemSpawner anchor;
        public List<ShopItemSpawner> members;
    }

    class Spot
    {
        public Vector3 pos;
        public Quaternion rot;
        public Bounds bale;
        public float height;   // one bale + small gap
        public float dist;
        public float ground;
    }

    static readonly HashSet<ShopItemSpawner> done = new HashSet<ShopItemSpawner>();
    static readonly HashSet<GameObject> ours = new HashSet<GameObject>();
    static readonly Dictionary<ShopItemSpawner, float> firstSeen = new Dictionary<ShopItemSpawner, float>();
    static Dictionary<string, string> placements;

    internal static void Tick()
    {
        if (!Tarp.Ready || !GameState.playing) return;
        done.RemoveWhere(s => s == null);
        ours.RemoveWhere(g => g == null);
        foreach (var k in firstSeen.Keys.Where(k => k == null).ToList()) firstSeen.Remove(k);

        var oakums = UnityEngine.Object.FindObjectsOfType<ShopItemSpawner>()
            .Where(s => s.itemPrefab != null && s.itemPrefab.GetComponent<ShipItemOakum>() != null)
            .ToList();

        foreach (var s in oakums)
        {
            if (done.Contains(s)) continue;

            // wait until the shop has put its goods out (other mods' items too), max 20 s
            if (!firstSeen.ContainsKey(s)) firstSeen[s] = Time.unscaledTime;
            if (!Stocked(s) && Time.unscaledTime - firstSeen[s] < 20f) continue;

            var members = oakums
                .Where(o => Vector3.Distance(o.transform.position, s.transform.position) < 3f)
                .ToList();
            foreach (var m in members) done.Add(m);

            var anchor = members.OrderBy(m => Path(m.transform), StringComparer.Ordinal).First();
            Build(new Group { key = Path(anchor.transform), anchor = anchor, members = members });
        }
    }

    // an oakum barrel is lying at the spawner = the shop is stocked
    static bool Stocked(ShopItemSpawner s)
    {
        foreach (var c in Physics.OverlapSphere(s.transform.position, 0.6f, ~0, QueryTriggerInteraction.Ignore))
            if (c.GetComponentInParent<ShipItemOakum>() != null) return true;
        return false;
    }

    static void Build(Group g)
    {
        try
        {
            if (Placements().TryGetValue(g.key, out var v))
            {
                if (v == "skip") { Plugin.Log.LogInfo($"Canvas shop '{g.key}': disabled"); return; }
                if (Manual(g, v)) return;
                Plugin.Log.LogWarning($"Canvas shop '{g.key}': bad placement '{v}', using automatic");
            }
            Auto(g);
        }
        catch (Exception e) { Plugin.Log.LogError($"Canvas shop '{g.key}' failed: {e}"); }
    }

    // ---- hand-made placement from Balance.ShopPlacements ----

    static bool Manual(Group g, string value)
    {
        var parts = value.Split('|');
        if (parts.Length != 4) return false;
        var f = new float[4];
        for (int i = 0; i < 4; i++)
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out f[i])) return false;

        var t = g.anchor.transform;
        Vector3 floor = t.TransformPoint(new Vector3(f[0], f[1], f[2]));
        Quaternion rot = Quaternion.Euler(0f, Yaw(t.forward) + f[3], 0f);
        Bounds bale = Footprint(Tarp.Template, rot);

        Vector3 pos = floor - Flat(bale.center);
        pos.y = floor.y - bale.min.y + 0.01f;
        Stack(g, pos, rot, bale.size.y + 0.01f);
        Plugin.Log.LogInfo($"Canvas shop stack ({Balance.ShopStack}) '{g.key}': manual placement");
        return true;
    }

    static Dictionary<string, string> Placements()
    {
        if (placements != null) return placements;
        placements = new Dictionary<string, string>();
        foreach (var e in Balance.ShopPlacements.Split(';'))
        {
            int i = e.LastIndexOf('=');
            if (i <= 0) continue;
            placements[e.Substring(0, i).Trim()] = e.Substring(i + 1).Trim();
        }
        return placements;
    }

    static string Path(Transform t)
    {
        string p = t.name;
        for (var x = t.parent; x != null; x = x.parent) p = x.name + "/" + p;
        return p;
    }

    static float Yaw(Vector3 dir) => Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;

    // ---- automatic placement ----

    static void Auto(Group g)
    {
        var s = g.anchor;
        Vector3 oak = Vector3.zero;
        foreach (var m in g.members) oak += m.transform.position;
        oak /= g.members.Count;

        // counter under the oakum barrels (a huge collider = floor/terrain, not a counter)
        var counter = Below(oak + Vector3.up * 0.3f, 2f);
        Vector3 center = Vector3.zero, axis = Vector3.right;
        float half = 0f, bottom = 0f;
        bool onCounter = counter != null && counter.GetType().Name != "TerrainCollider";
        if (onCounter)
        {
            CounterBox(counter, out center, out axis, out half, out bottom);
            if (half > Balance.ShopMaxCounter) onCounter = false;
        }
        if (!onCounter) RowBox(s, g.members, out center, out axis, out half, out bottom);

        // the end of the counter closest to the oakum goes first
        float near = Vector3.Dot(Flat(oak - center), axis) >= 0f ? 1f : -1f;
        float fromY = oak.y + 0.3f;
        var areas = ShopAreas();
        var spawners = UnityEngine.Object.FindObjectsOfType<ShopItemSpawner>()
            .Where(x => x.itemPrefab != null && !ours.Contains(x.gameObject))
            .ToList();

        Spot found = null, fallback = null;
        foreach (float side in new[] { near, -near })
        {
            foreach (var sp in Candidates(center, axis * side, half, bottom, fromY))
            {
                if (areas.Count > 0 && !InShop(sp.pos + Vector3.up * sp.bale.extents.y, areas)) continue;
                if (fallback == null) fallback = sp;
                if (!Blocked(sp, spawners)) { found = sp; break; }
            }
            if (found != null) break;
        }

        string where = onCounter
            ? $"counter '{counter.name}' half {half:F2}"
            : $"barrel row (under the barrels: '{(counter ? counter.name : "none")}') half {half:F2}";

        var spot = found ?? fallback;
        if (spot == null)
        {
            Plugin.Log.LogWarning($"Canvas shop '{g.key}' skipped: no spot inside the shop area, {where}");
            return;
        }

        Stack(g, spot.pos, spot.rot, spot.height);

        string msg = $"Canvas shop stack ({Balance.ShopStack}) '{g.key}' at {spot.pos}: {where}, " +
                     $"offset {spot.dist:F2}, ground {spot.ground:F3}, free spot {found != null}";
        if (found != null) Plugin.Log.LogInfo(msg);
        else Plugin.Log.LogWarning(msg + " - may clip");
    }

    // positions past the counter end: first moving outwards, then a little back towards the counter
    static IEnumerable<Spot> Candidates(Vector3 center, Vector3 dir, float half, float bottom, float fromY)
    {
        Quaternion rotA = Quaternion.LookRotation(dir, Vector3.up);
        Quaternion rotB = rotA * Quaternion.Euler(0f, 90f, 0f);
        Bounds fa = Footprint(Tarp.Template, rotA);
        Bounds fb = Footprint(Tarp.Template, rotB);
        bool turn = Along(fb, dir) < Along(fa, dir) - 0.01f;
        Quaternion rot = turn ? rotB : rotA;
        Bounds bale = turn ? fb : fa;
        float baleHalf = Along(bale, dir);

        float start = half + Balance.ShopGap + baleHalf;
        var dists = new List<float>();
        for (float d = start; d <= start + Balance.ShopMaxShift + 0.001f; d += 0.05f) dists.Add(d);
        for (float d = start - 0.05f; d >= Mathf.Max(half * 0.5f, start - 1f); d -= 0.05f) dists.Add(d);

        foreach (var d in dists)
        {
            Vector3 target = Flat(center) + dir * d;
            float ground = Ground(target, fromY, bottom);
            Vector3 pos = target - Flat(bale.center);
            pos.y = ground - bale.min.y + 0.01f;
            yield return new Spot { pos = pos, rot = rot, bale = bale, height = bale.size.y + 0.01f, dist = d, ground = ground };
        }
    }

    // something solid or another shop's goods in the volume of the whole stack
    static bool Blocked(Spot sp, List<ShopItemSpawner> spawners)
    {
        float stackH = sp.height * Balance.ShopStack;
        float bottomY = sp.pos.y + sp.bale.min.y;
        var c = new Vector3(sp.pos.x + sp.bale.center.x, bottomY + stackH * 0.5f + 0.02f, sp.pos.z + sp.bale.center.z);
        var e = new Vector3(sp.bale.extents.x * 0.95f, stackH * 0.5f - 0.04f, sp.bale.extents.z * 0.95f);

        foreach (var col in Physics.OverlapBox(c, e, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore))
        {
            if (col.GetComponentInParent<ShipItemTarp>() != null) continue;
            if (col.GetComponentInParent<CharacterController>() != null) continue;
            return true;
        }

        const float margin = 0.25f;
        foreach (var s in spawners)
        {
            if (!s) continue;
            var p = s.transform.position;
            if (Mathf.Abs(p.x - c.x) < e.x + margin &&
                Mathf.Abs(p.z - c.z) < e.z + margin &&
                p.y > bottomY - 0.3f && p.y < bottomY + stackH + 0.3f)
                return true;
        }
        return false;
    }

    static void Stack(Group g, Vector3 pos, Quaternion rot, float h)
    {
        for (int i = 0; i < Balance.ShopStack; i++)
            Spawn(g.anchor, pos + Vector3.up * (h * i), rot);
    }

    static GameObject Spawn(ShopItemSpawner s, Vector3 pos, Quaternion rot)
    {
        var go = new GameObject("HatchTarp shop spawner");
        go.SetActive(false);
        go.transform.SetParent(s.transform.parent, true);
        go.transform.position = pos;
        go.transform.rotation = rot;

        var sp = go.AddComponent<ShopItemSpawner>();
        sp.itemPrefab = Tarp.Template;
        sp.availableAtNight = s.availableAtNight;
        sp.priceMult = s.priceMult;

        ours.Add(go);
        go.SetActive(true);
        return go;
    }

    // ---- counter geometry ----

    // first solid thing under a point (no items, no player, no triggers)
    static Collider Below(Vector3 from, float length)
    {
        var hits = Physics.RaycastAll(from, Vector3.down, length, ~0, QueryTriggerInteraction.Ignore);
        foreach (var h in hits.OrderBy(x => x.distance))
        {
            if (h.collider.GetComponentInParent<ShipItem>() != null) continue;
            if (h.collider.GetComponentInParent<CharacterController>() != null) continue;
            return h.collider;
        }
        return null;
    }

    // ground under a point; falls back to the counter bottom
    static float Ground(Vector3 at, float fromY, float fallback)
    {
        var from = new Vector3(at.x, fromY, at.z);
        var hits = Physics.RaycastAll(from, Vector3.down, 4f, ~0, QueryTriggerInteraction.Ignore);
        foreach (var h in hits.OrderBy(x => x.distance))
        {
            if (h.collider.GetComponentInParent<ShipItem>() != null) continue;
            if (h.collider.GetComponentInParent<CharacterController>() != null) continue;
            return Mathf.Abs(h.point.y - fallback) < 0.6f ? h.point.y : fallback;
        }
        return fallback;
    }

    // center, long horizontal axis and half length of the counter
    static void CounterBox(Collider c, out Vector3 center, out Vector3 axis, out float half, out float bottom)
    {
        Bounds lb;
        Matrix4x4 m;
        if (c is BoxCollider bc) { lb = new Bounds(bc.center, bc.size); m = c.transform.localToWorldMatrix; }
        else if (c is MeshCollider mc && mc.sharedMesh) { lb = mc.sharedMesh.bounds; m = c.transform.localToWorldMatrix; }
        else { lb = c.bounds; m = Matrix4x4.identity; }

        center = m.MultiplyPoint3x4(lb.center);
        bottom = c.bounds.min.y;

        var ax = new[]
        {
            m.MultiplyVector(new Vector3(lb.extents.x, 0f, 0f)),
            m.MultiplyVector(new Vector3(0f, lb.extents.y, 0f)),
            m.MultiplyVector(new Vector3(0f, 0f, lb.extents.z))
        };
        int up = 0;
        for (int i = 1; i < 3; i++)
            if (Mathf.Abs(ax[i].normalized.y) > Mathf.Abs(ax[up].normalized.y)) up = i;

        Vector3 longest = Vector3.zero;
        for (int i = 0; i < 3; i++)
        {
            if (i == up) continue;
            var f = Flat(ax[i]);
            if (f.sqrMagnitude > longest.sqrMagnitude) longest = f;
        }
        half = longest.magnitude;
        axis = half > 1e-4f ? longest / half : Vector3.right;
    }

    // no real counter (barrels on the floor): the row of oakum barrels plays the counter
    static void RowBox(ShopItemSpawner s, List<ShopItemSpawner> group,
                       out Vector3 center, out Vector3 axis, out float half, out float bottom)
    {
        var pts = group.Select(g => g.transform.position).ToList();
        center = Vector3.zero;
        foreach (var p in pts) center += p;
        center /= pts.Count;

        axis = Flat(s.transform.right);
        float best = 0f;
        foreach (var p in pts)
            foreach (var q in pts)
            {
                float d = Vector3.Distance(Flat(p), Flat(q));
                if (d > best) { best = d; axis = Flat(q - p); }
            }
        if (axis.sqrMagnitude < 1e-6f) axis = Vector3.right;
        axis.Normalize();

        Bounds barrel = Footprint(s.itemPrefab, s.transform.rotation);
        half = best * 0.5f + Along(barrel, axis);
        bottom = center.y + barrel.min.y;
        center += Flat(barrel.center);
    }

    // ---- shop area (items outside it are not sold by the shopkeeper) ----

    static List<Collider> ShopAreas()
    {
        var list = new List<Collider>();
        foreach (var a in UnityEngine.Object.FindObjectsOfType<ShopArea>())
            foreach (var c in a.GetComponents<Collider>())
                if (c.isTrigger) list.Add(c);
        return list;
    }

    static bool InShop(Vector3 p, List<Collider> areas)
    {
        foreach (var c in areas)
        {
            if (!c) continue;
            if (c is MeshCollider mc && !mc.convex) { if (c.bounds.Contains(p)) return true; continue; }
            if ((c.ClosestPoint(p) - p).sqrMagnitude < 1e-6f) return true;
        }
        return false;
    }

    // ---- bale size ----

    static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

    // half length of the bounds along a horizontal direction
    static float Along(Bounds b, Vector3 dir) =>
        Mathf.Abs(dir.x) * b.extents.x + Mathf.Abs(dir.z) * b.extents.z;

    // world-axis bounds of a prefab's meshes, relative to its pivot, when placed with rotation rot
    static Bounds Footprint(GameObject prefab, Quaternion rot)
    {
        var res = new Bounds(Vector3.zero, Vector3.zero);
        if (!prefab) return res;

        var root = prefab.transform;
        Matrix4x4 place = Matrix4x4.TRS(Vector3.zero, rot, root.localScale) * root.worldToLocalMatrix;
        bool any = false;

        foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            if (!mf.sharedMesh || !mf.GetComponent<MeshRenderer>()) continue;
            Add(ref res, ref any, mf.sharedMesh.bounds, place * mf.transform.localToWorldMatrix);
        }
        foreach (var sm in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (!sm.sharedMesh) continue;
            Add(ref res, ref any, sm.sharedMesh.bounds, place * sm.transform.localToWorldMatrix);
        }
        return res;
    }

    static void Add(ref Bounds res, ref bool any, Bounds local, Matrix4x4 m)
    {
        Vector3 c = local.center, e = local.extents;
        for (int i = 0; i < 8; i++)
        {
            var p = m.MultiplyPoint3x4(c + new Vector3(
                (i & 1) == 0 ? -e.x : e.x,
                (i & 2) == 0 ? -e.y : e.y,
                (i & 4) == 0 ? -e.z : e.z));
            if (!any) { res = new Bounds(p, Vector3.zero); any = true; }
            else res.Encapsulate(p);
        }
    }
}