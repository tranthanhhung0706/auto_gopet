using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Il2CppGoPet.Core.Protocol;
using MelonLoader;
using UnityEngine;

namespace GoPetMod
{
    // Auto "Hồi phục": when HP drops below the threshold, send the same request as the heal button in the
    // radial menu. That button calls fu.bex(true) -> op.ghu(true) -> packet cmd 81 [0x2D, 1] (rest on);
    // moving sends [0x2D, 0] (rest off).
    internal static class AutoHeal
    {
        // HP percent below which auto-heal acts; set in game from the main menu ("Tự hồi phục") and saved in
        // UserData/MelonPreferences.cfg.
        private static MelonPreferences_Entry<int> _percentPref;
        public static int Percent => _percentPref?.Value ?? 90;
        private static float Threshold => Percent / 100f;
        // Below this HP ratio the game's own auto-attack ("Tự Động") is paused, like pressing its II button,
        // so the fight ends and resting is allowed.
        private static float PauseAutoThreshold => Threshold;
        // Auto paused by the mod is started again once HP is back to this ratio (1 = full).
        private const float ResumeAutoThreshold = 1f;
        private const float RetrySeconds = 5f;
        private const float MaxRetrySeconds = 30f;
        // The server refuses to rest while in combat; wait until HP has stopped dropping for this long.
        private const float QuietSeconds = 3f;
        private const float RejectWindowSeconds = 3f;
        private const string CombatRejectText = "nghỉ ngơi";
        private const int RestCmd = 81;
        private const byte RestSub = 0x2D;

        public static bool Enabled = true;

        public static void Init()
        {
            var category = MelonPreferences.CreateCategory("GoPetMod");
            _percentPref = category.CreateEntry("AutoHealPercent", 90, "Auto heal below this HP percent");
        }

        public static void SetPercent(int percent)
        {
            _percentPref.Value = percent;
            MelonPreferences.Save();
            Enabled = true;
            _wasLow = false;
            ShowStatus();
        }

        public static void Disable()
        {
            Enabled = false;
            ShowStatus();
        }

        private static fu _session;
        private static jy _auto;
        private static bool _pausedByMod;
        private static bool _resting;
        private static float _lastSendAt = -100f;
        private static int _hpAtSend;
        private static bool _wasLow;
        private static int _prevHp = -1;
        private static float _lastHpDropAt = -100f;
        private static float _retrySeconds = RetrySeconds;

        public static void Toggle()
        {
            Enabled = !Enabled;
            ShowStatus();
        }

        // Uses the game's own small banner (GoPet.Ui.Banner), so the text is drawn with the game's font.
        private static void ShowStatus()
        {
            var text = Enabled ? $"Tự hồi phục: BẬT, dưới {Percent}% máu" : "Tự hồi phục: TẮT";
            MelonLogger.Msg($"[AutoHeal] {text}");
            try
            {
                Il2CppGoPet.Ui.Banner.bel(text);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[AutoHeal] banner failed: {e.Message}");
            }
        }

        public static void Update()
        {
            if (!Enabled || _session == null)
                return;

            int hp, maxHp;
            try
            {
                var vitals = _session.jsd.noh;
                hp = vitals.Hp;
                maxHp = vitals.MaxHp;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[AutoHeal] read HP failed: {e.Message}");
                _session = null;
                return;
            }

            // HP 0 means dead (revive is a different flow) or vitals not loaded yet.
            if (hp <= 0 || maxHp <= 0)
                return;

            var now = Time.unscaledTime;
            if (_prevHp >= 0 && hp < _prevHp)
                _lastHpDropAt = now;
            _prevHp = hp;

            if (hp < maxHp * PauseAutoThreshold)
                PauseAuto(hp, maxHp);
            else if (_pausedByMod && hp >= maxHp * ResumeAutoThreshold)
                ResumeAuto(hp, maxHp);

            var low = hp < maxHp * Threshold;
            if (low != _wasLow)
            {
                _wasLow = low;
                MelonLogger.Msg($"[AutoHeal] HP {hp}/{maxHp} {(low ? "<" : ">=")} {Percent}%");
            }
            if (!low)
            {
                _retrySeconds = RetrySeconds;
                return;
            }

            if (now - _lastSendAt < _retrySeconds || now - _lastHpDropAt < QuietSeconds)
                return;
            // Already resting and HP is going up: leave it alone.
            if (_resting && hp > _hpAtSend)
                return;

            // The previous request did not get HP going up (refused in combat, or cancelled): wait longer next time.
            if (_lastSendAt > 0 && hp <= _hpAtSend)
                _retrySeconds = Math.Min(_retrySeconds * 2f, MaxRetrySeconds);

            try
            {
                if (_session.bex(true))
                {
                    _lastSendAt = now;
                    _hpAtSend = hp;
                    MelonLogger.Msg($"[AutoHeal] Hồi phục (HP {hp}/{maxHp}, lần sau chờ {_retrySeconds:0}s)");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[AutoHeal] send failed: {e.Message}");
                _lastSendAt = now;
            }
        }

        // Same as pressing II on the "Tự Động" HUD. dw.eet() maps the HUD click zones: Pause -> jy.duq(map),
        // Close (X) -> jy.jmc() (turns auto off), Toggle -> collapse the panel. jy.duq(map) only acts while auto is
        // on (qi.nmg) and flips jy.lix ("Đang tạm dừng"), firing the UI event; calling it again resumes.
        private static void PauseAuto(int hp, int maxHp)
        {
            if (_auto == null || _session == null)
                return;
            try
            {
                var settings = _auto.oom;
                if (settings == null || !settings.nmg || _auto.lix)
                    return;
                _auto.duq(_session.jsd.nog);
                if (!_auto.lix)
                    return;
                _pausedByMod = true;
                MelonLogger.Msg($"[AutoHeal] Tạm dừng Tự Động (HP {hp}/{maxHp})");
                Il2CppGoPet.Ui.Banner.bel("Máu thấp: tạm dừng Tự Động");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[AutoHeal] pause auto failed: {e.Message}");
                _auto = null;
            }
        }

        // Undo PauseAuto by pressing II again (jy.duq flips the pause back).
        // Rest is cancelled first ([0x2D, 0]); otherwise the server still thinks we rest and refuses the attack
        // ("Mỗi lần chỉ được đánh 1 người").
        private static void ResumeAuto(int hp, int maxHp)
        {
            _pausedByMod = false;
            if (_auto == null || _session == null)
                return;
            try
            {
                var settings = _auto.oom;
                // Auto turned off (X) or already resumed by hand.
                if (settings == null || !settings.nmg || !_auto.lix)
                    return;
                _session.bex(false);
                _resting = false;
                _auto.duq(_session.jsd.nog);
                MelonLogger.Msg($"[AutoHeal] Chạy tiếp Tự Động (HP {hp}/{maxHp})");
                Il2CppGoPet.Ui.Banner.bel("Đầy máu: chạy tiếp Tự Động");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[AutoHeal] resume auto failed: {e.Message}");
            }
        }

        // jy wraps the auto-play settings; dw.Tick calls jy.fhn(map) every frame, so grab the instance there.
        [HarmonyPatch(typeof(jy), nameof(jy.fhn))]
        private static class AutoPatch
        {
            private static void Postfix(jy __instance)
            {
                if (_auto != null && _auto.Pointer == __instance.Pointer)
                    return;
                _auto = __instance;
                MelonLogger.Msg("[AutoHeal] auto captured");
            }
        }

        // Server refused because we are still fighting: hide the popup it caused and back off.
        private static bool OnServerMessage(string text)
        {
            if (text == null || !text.Contains(CombatRejectText) || Time.unscaledTime - _lastSendAt > RejectWindowSeconds)
                return true;
            MelonLogger.Msg("[AutoHeal] server: đang chiến đấu, ẩn thông báo");
            return false;
        }

        // dw has several obfuscated duplicates of its ServerMessage handler; patch every one of them.
        [HarmonyPatch]
        private static class ServerMessagePatch
        {
            private static IEnumerable<MethodBase> TargetMethods() =>
                typeof(dw).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(m => m.ReturnType == typeof(void) && m.GetParameters() is { Length: 1 } p && p[0].ParameterType == typeof(ServerMessage));

            private static bool Prefix(ServerMessage a)
            {
                try
                {
                    return OnServerMessage(a.Text);
                }
                catch (Exception)
                {
                    return true;
                }
            }
        }

        // fu.dxf(py) is the session's incoming-packet handler; it runs constantly, so it is a reliable place to grab the instance.
        [HarmonyPatch(typeof(fu), "dxf")]
        private static class ReceivePatch
        {
            private static void Postfix(fu __instance)
            {
                if (_session != null && _session.Pointer == __instance.Pointer)
                    return;
                _session = __instance;
                _resting = false;
                MelonLogger.Msg("[AutoHeal] session captured");
                ShowStatus();
            }
        }

        // Track the rest state from outgoing packets (manual click, movement cancelling it, or our own request).
        // Packet contents are not logged: the login packet carries the account password.
        [HarmonyPatch(typeof(jo), nameof(jo.dlg))]
        private static class SendPatch
        {
            private static void Prefix(py a)
            {
                try
                {
                    if (a.njc != RestCmd)
                        return;
                    var body = a.Body();
                    if (body != null && body.Length >= 2 && body[0] == RestSub)
                        _resting = body[1] != 0;
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
