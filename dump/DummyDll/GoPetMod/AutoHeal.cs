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
    // Auto "Hồi phục": when HP or MP drops below its threshold, send the same request as the heal button in the
    // radial menu. That button calls fu.bex(true) -> op.ghu(true) -> packet cmd 81 [0x2D, 1] (rest on);
    // moving sends [0x2D, 0] (rest off).
    internal static class AutoHeal
    {
        // HP percent below which auto-heal acts; set in game from the main menu ("Tự hồi phục") and saved in
        // UserData/MelonPreferences.cfg.
        private static MelonPreferences_Entry<int> _percentPref;
        public static int Percent => _percentPref?.Value ?? 90;
        private static float Threshold => Percent / 100f;
        // Same for MP; 0 means MP is not checked.
        private static MelonPreferences_Entry<int> _mpPercentPref;
        public static int MpPercent => _mpPercentPref?.Value ?? 0;
        private static float MpThreshold => MpPercent / 100f;
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
            _mpPercentPref = category.CreateEntry("AutoHealMpPercent", 0, "Auto heal below this MP percent (0 = off)");
        }

        public static void SetMpPercent(int percent)
        {
            _mpPercentPref.Value = percent;
            MelonPreferences.Save();
            _wasLow = false;
            ShowStatus();
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
        private static int _lastMap = int.MinValue;
        // The game's own auto never runs on these maps: jy.fhn/fhm/duq/fho all skip map 12 and 31.
        private static readonly int[] NoAutoMaps = { 31 };
        private static bool _resting;
        private static float _lastSendAt = -100f;
        private static int _hpAtSend;
        private static int _mpAtSend;
        private static int _prevMp = -1;
        private static bool _wasLow;
        private static int _prevHp = -1;
        // Regen measurement: every HP increase is logged with its size and the time since the previous one.
        private static float _lastHpGainAt = -1f;
        private static int _regenStartHp = -1;
        private static float _regenStartAt;
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
            var text = Enabled
                ? $"Tự hồi phục: BẬT, HP dưới {Percent}%, " + (MpPercent > 0 ? $"MP dưới {MpPercent}%" : "MP tắt")
                : "Tự hồi phục: TẮT";
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

            int hp, maxHp, mp, maxMp;
            try
            {
                var map = _session.jsd.nog;
                if (map != _lastMap)
                {
                    _lastMap = map;
                    var noAuto = Array.IndexOf(NoAutoMaps, map) >= 0;
                    MelonLogger.Msg($"[AutoHeal] map {map}{(noAuto ? " (game tắt Tự Động ở map này)" : "")}");
                }
                var vitals = _session.jsd.noh;
                hp = vitals.Hp;
                maxHp = vitals.MaxHp;
                mp = vitals.Mp;
                maxMp = vitals.MaxMp;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[AutoHeal] read HP/MP failed: {e.Message}");
                _session = null;
                return;
            }

            // HP 0 means dead (revive is a different flow) or vitals not loaded yet.
            if (hp <= 0 || maxHp <= 0)
                return;

            var now = Time.unscaledTime;
            if (_prevHp >= 0 && hp < _prevHp)
            {
                _lastHpDropAt = now;
                _lastHpGainAt = -1f;
                _regenStartHp = -1;
            }
            else if (_prevHp >= 0 && hp > _prevHp)
                LogRegen(hp, maxHp, now);
            _prevHp = hp;
            // MP going down means skills are still being cast, i.e. still fighting.
            if (_prevMp >= 0 && mp < _prevMp)
                _lastHpDropAt = now;
            _prevMp = mp;

            var checkMp = MpPercent > 0 && maxMp > 0;
            var lowHp = hp < maxHp * Threshold;
            var lowMp = checkMp && mp < maxMp * MpThreshold;
            var low = lowHp || lowMp;
            var vitalsText = $"HP {hp}/{maxHp}, MP {mp}/{maxMp}";

            if (hp < maxHp * PauseAutoThreshold || lowMp)
                PauseAuto(vitalsText, lowHp ? "Máu thấp" : "MP thấp");
            else if (_pausedByMod && hp >= maxHp * ResumeAutoThreshold && (!checkMp || mp >= maxMp * ResumeAutoThreshold))
                ResumeAuto(vitalsText);

            if (low != _wasLow)
            {
                _wasLow = low;
                MelonLogger.Msg($"[AutoHeal] {vitalsText} " + (low ? $"thấp (HP<{Percent}%{(checkMp ? $" hoặc MP<{MpPercent}%" : "")})" : "đủ"));
            }
            if (!low)
            {
                _retrySeconds = RetrySeconds;
                return;
            }

            if (now - _lastSendAt < _retrySeconds || now - _lastHpDropAt < QuietSeconds)
                return;
            var recovering = hp > _hpAtSend || mp > _mpAtSend;
            // Already resting and HP/MP is going up: leave it alone.
            if (_resting && recovering)
                return;

            // The previous request did not get HP/MP going up (refused in combat, or cancelled): wait longer next time.
            if (_lastSendAt > 0 && !recovering)
                _retrySeconds = Math.Min(_retrySeconds * 2f, MaxRetrySeconds);

            try
            {
                if (_session.bex(true))
                {
                    _lastSendAt = now;
                    _hpAtSend = hp;
                    _mpAtSend = mp;
                    MelonLogger.Msg($"[AutoHeal] Hồi phục ({vitalsText}, lần sau chờ {_retrySeconds:0}s)");
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
        private static void PauseAuto(string vitalsText, string reason)
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
                MelonLogger.Msg($"[AutoHeal] Tạm dừng Tự Động ({vitalsText})");
                Il2CppGoPet.Ui.Banner.bel($"{reason}: tạm dừng Tự Động");
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
        private static void ResumeAuto(string vitalsText)
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
                MelonLogger.Msg($"[AutoHeal] Chạy tiếp Tự Động ({vitalsText})");
                Il2CppGoPet.Ui.Banner.bel("Đã hồi đầy: chạy tiếp Tự Động");
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

        private static void LogRegen(int hp, int maxHp, float now)
        {
            var gain = hp - _prevHp;
            if (_lastHpGainAt < 0f)
            {
                _regenStartHp = _prevHp;
                _regenStartAt = now;
                MelonLogger.Msg($"[Regen] +{gain} HP -> {hp}/{maxHp} (nghỉ: {_resting})");
            }
            else
            {
                var dt = now - _lastHpGainAt;
                var total = now - _regenStartAt;
                var avg = total > 0f ? (hp - _regenStartHp) / total : 0f;
                MelonLogger.Msg($"[Regen] +{gain} HP sau {dt:0.00}s -> {hp}/{maxHp} | TB {avg:0.0} HP/s ({avg * 100f / maxHp:0.00}%/s) (nghỉ: {_resting})");
            }
            _lastHpGainAt = now;
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
