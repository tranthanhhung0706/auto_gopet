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
    // Auto "Hồi phục": when HP drops below the threshold, do what the heal button in the radial menu does.
    // The button runs dw.ciy(): fu.dyh(true) -> op.ghu(true) -> packet cmd 81 [0x2D, 1] (rest on), then lf.fup()
    // marks the client as resting. dw.Tick sends rest off [0x2D, 0] on movement only while that flag is set, so
    // sending the packet without the flag left the server resting and later attacks failed
    // ("Mỗi lần chỉ được đánh 1 người").
    internal static class AutoHeal
    {
        private const float Threshold = 0.5f;
        private const float RetrySeconds = 5f;
        private const float MaxRetrySeconds = 30f;
        // The server refuses to rest while in combat; wait until HP has stopped dropping for this long.
        private const float QuietSeconds = 3f;
        private const float RejectWindowSeconds = 3f;
        private const string CombatRejectText = "nghỉ ngơi";
        private const int RestCmd = 81;
        private const byte RestSub = 0x2D;

        public static bool Enabled = true;

        private static fu _session;
        private static dw _game;
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
            MelonLogger.Msg($"[AutoHeal] {(Enabled ? "BẬT" : "TẮT")}");
        }

        public static void Update()
        {
            if (!Enabled || _session == null || _game == null)
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

            var low = hp < maxHp * Threshold;
            if (low != _wasLow)
            {
                _wasLow = low;
                MelonLogger.Msg($"[AutoHeal] HP {hp}/{maxHp} {(low ? "< 50%" : ">= 50%")}");
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
                _game.ciy();
                _lastSendAt = now;
                _hpAtSend = hp;
                MelonLogger.Msg($"[AutoHeal] Hồi phục (HP {hp}/{maxHp}, lần sau chờ {_retrySeconds:0}s)");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[AutoHeal] send failed: {e.Message}");
                _lastSendAt = now;
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

        // dw is the in-game controller (not a MonoBehaviour); its Tick runs every frame.
        [HarmonyPatch(typeof(dw), nameof(dw.Tick))]
        private static class GameTickPatch
        {
            private static void Postfix(dw __instance)
            {
                if (_game != null && _game.Pointer == __instance.Pointer)
                    return;
                _game = __instance;
                MelonLogger.Msg("[AutoHeal] game captured");
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
