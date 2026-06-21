global using BTD_Mod_Helper.Extensions;
using System.Globalization;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using Il2CppTMPro;
using BTD_Mod_Helper;
using BTD_Mod_Helper.Api.ModOptions;
using Il2CppAssets.Scripts.Simulation;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using NoCashLimit;

[assembly: MelonInfo(typeof(NoCashLimit.NoCashLimit), ModHelperData.Name, ModHelperData.Version, ModHelperData.RepoOwner)]
[assembly: MelonGame("Ninja Kiwi", "BloonsTD6")]
[assembly: MelonGame("Ninja Kiwi", "BloonsTD6-Epic")]

namespace NoCashLimit;

public class NoCashLimit : BloonsTD6Mod
{
    /// <summary>The 32-bit signed integer maximum: 2,147,483,647.</summary>
    public const long IntLimit = int.MaxValue;

    /// <summary>Cash level at which we start trying to lock onto the on-screen counter.</summary>
    private const long LockMin = 1_000_000;

    public static readonly ModSettingBool Enabled = new(true)
    {
        displayName = "Remove Cash Limit"
    };

    public static readonly ModSettingBool Diagnostics = new(false)
    {
        displayName = "Diagnostics",
        description = "Logs what the mod is doing: which on-screen text it locked onto, or a dump of the " +
                      "HUD text if it can't find the cash counter. Turn this on if the fix isn't working."
    };

    public static readonly ModSettingBool LogWhenUncapping = new(false)
    {
        displayName = "Log when a data cap is removed",
        description = "Logs whenever the mod catches and restores cash the game tried to cap at the data level."
    };

    // The on-screen cash counter, discovered at runtime (no hard-coded UI class needed).
    private TMP_Text? cashText;
    private float nextScan;

    public override void OnApplicationStart()
    {
        ModHelper.Msg<NoCashLimit>("NoCashLimit loaded! Cash can now display and grow past 2,147,483,647.");
    }

    /// <summary>
    /// Safely reads the local player's cash without ever throwing. Returns false when there is no active
    /// simulation / cash manager yet (loading, menus, pre-game), which is what was causing the
    /// NullReferenceException inside GetCash().
    /// </summary>
    private static bool TryGetCash(out double cash)
    {
        cash = 0;
        var inGame = InGame.instance;
        if (inGame == null) return false;

        var sim = inGame.GetSimulation();
        if (sim == null) return false;

        var managers = sim.cashManagers;
        if (managers == null || managers.Count == 0) return false;

        var manager = inGame.GetCashManager();
        if (manager == null || manager.cash == null) return false;

        cash = manager.cash.Value;
        return true;
    }

    /// <summary>
    /// Cash is stored as a <c>double</c>, so the wallet itself isn't integer-limited. What breaks past
    /// ~2.1 billion is the on-screen counter, which renders the value through a 32-bit int. This runs in
    /// LateUpdate (after the game writes the counter), discovers which HUD text is the cash counter by
    /// matching it against the known cash value while the value is still shown correctly, then keeps that
    /// text correct once cash exceeds the int limit.
    /// </summary>
    public override void OnLateUpdate()
    {
        if (!Enabled) return;

        try
        {
            if (!TryGetCash(out var cash))
            {
                cashText = null;
                return;
            }

            // (1) Lock onto the counter. Below the limit it shows the true value, so match that;
            //     if we're already above it, the counter is usually pinned to int.MaxValue.
            if (cashText == null && cash >= LockMin && Time.time >= nextScan)
            {
                nextScan = Time.time + 0.5f;
                // Below the limit the counter shows the true value, so match that. Once past the limit
                // the counter is broken: on PC the double->int cast yields int.MinValue, so it displays
                // -2,147,483,648 (some builds clamp to int.MaxValue instead). Matching either lets us
                // lock on even when joining/continuing a game that's already over the limit.
                var targets = cash <= IntLimit
                    ? new[] { (long)cash }
                    : new[] { IntLimit + 1, IntLimit }; // 2,147,483,648 (wrap) or 2,147,483,647 (clamp)
                cashText = FindTextShowing(targets);

                if (Diagnostics)
                {
                    if (cashText != null)
                        ModHelper.Msg<NoCashLimit>($"Locked onto cash counter '{cashText.name}' (showing \"{cashText.text}\").");
                    else if (cash > IntLimit)
                        DumpNumericText(); // couldn't find it - help us see what the counter actually shows
                }
            }

            // (2) Keep the counter correct past the int limit.
            var ct = cashText;
            if (ct != null && cash > IntLimit)
            {
                var prefix = LeadingNonDigits(ct.text);
                ct.text = prefix + cash.ToString("N0", CultureInfo.InvariantCulture);
            }
        }
        catch
        {
            cashText = null; // something got destroyed; re-discover next frame
        }
    }

    private static TMP_Text? FindTextShowing(params long[] targets)
    {
        foreach (var t in UnityEngine.Object.FindObjectsOfType<TMP_Text>())
        {
            if (t == null) continue;
            var digits = ExtractDigits(t.text);
            foreach (var target in targets)
                if (digits == target) return t;
        }
        return null;
    }

    private static void DumpNumericText()
    {
        ModHelper.Msg<NoCashLimit>("--- could not lock onto the cash counter; numeric HUD text follows ---");
        var shown = 0;
        foreach (var t in UnityEngine.Object.FindObjectsOfType<TMP_Text>())
        {
            if (t == null || ExtractDigits(t.text) < 0) continue;
            ModHelper.Msg<NoCashLimit>($"  '{t.name}' = \"{t.text}\"");
            if (++shown >= 25) break;
        }
    }

    // Reads all digit characters out of a string (ignoring $, commas, spaces, sign). -1 if none.
    private static long ExtractDigits(string? s)
    {
        if (string.IsNullOrEmpty(s)) return -1;
        long n = 0;
        var any = false;
        foreach (var ch in s)
        {
            if (ch < '0' || ch > '9') continue;
            n = n * 10 + (ch - '0');
            any = true;
        }
        return any ? n : -1;
    }

    // Returns the leading run of non-digit characters (e.g. a "$") so we can preserve it.
    private static string LeadingNonDigits(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var i = 0;
        while (i < s.Length && (s[i] < '0' || s[i] > '9') && s[i] != '-') i++;
        return s.Substring(0, i);
    }

    /// <summary>
    /// Safety net for the data layer: if anything ever clamps the wallet itself down to int.MaxValue
    /// when cash is earned, restore the full double. In normal play this never triggers.
    /// </summary>
    [HarmonyPatch(typeof(Simulation), "AddCash")]
    internal static class Simulation_AddCash
    {
        [HarmonyPrefix]
        public static void Prefix(double c, out double __state)
        {
            __state = double.NaN;
            if (!Enabled || c <= 0) return;
            if (TryGetCash(out var before)) __state = before;
        }

        [HarmonyPostfix]
        public static void Postfix(double c, double __state)
        {
            if (double.IsNaN(__state)) return;
            if (!TryGetCash(out var actual)) return;

            var expected = __state + c;
            if (actual + 0.5 >= expected) return; // not capped - nothing to do

            InGame.instance.SetCash(expected);

            if (LogWhenUncapping)
            {
                ModHelper.Msg<NoCashLimit>(
                    $"Restored capped cash: {actual.ToString("N0", CultureInfo.InvariantCulture)} -> " +
                    $"{expected.ToString("N0", CultureInfo.InvariantCulture)}");
            }
        }
    }
}