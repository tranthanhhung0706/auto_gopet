using System;
using MelonLoader;
using Il2CppTMPro;
using UnityEngine;

[assembly: MelonInfo(typeof(GoPetMod.Main), "GoPetMod", "1.0.0", "you")]
[assembly: MelonGame(null, null)]

namespace GoPetMod
{
    public class Main : MelonMod
    {
        private readonly FpsCounter _fps = new FpsCounter();

        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("GoPetMod loaded. F10: bật/tắt FPS, F9: bật/tắt tự hồi phục.");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            LoggerInstance.Msg($"Scene loaded: {sceneName} ({buildIndex})");
        }

        public override void OnUpdate()
        {
            _fps.Update();
            if (Keys.Pressed(k => k.f9Key, KeyCode.F9))
                AutoHeal.Toggle();
            AutoHeal.Update();
        }

    }

    // IMGUI (GUI.Label) is stripped from this build, so the counter is drawn with a uGUI canvas + TextMeshPro.
    internal class FpsCounter
    {
        private const float SampleSeconds = 0.5f;

        private bool _visible = true;
        private int _frames;
        private float _elapsed;
        private float _fps;
        private float _ms;
        private float _retryAt;
        private GameObject _root;
        private TextMeshProUGUI _text;

        public void Update()
        {
            _frames++;
            _elapsed += Time.unscaledDeltaTime;

            if (TogglePressed())
            {
                _visible = !_visible;
                if (_root != null)
                    _root.SetActive(_visible);
            }

            if (_elapsed < SampleSeconds)
                return;

            _fps = _frames / _elapsed;
            _ms = _elapsed * 1000f / _frames;
            _frames = 0;
            _elapsed = 0f;

            if (_root == null && Time.unscaledTime >= _retryAt)
                CreateOverlay();
            if (_text == null)
                return;

            _text.text = $"{_fps:0} FPS ({_ms:0.0} ms)";
            _text.color = _fps >= 50 ? Color.green : _fps >= 30 ? Color.yellow : Color.red;
        }

        private void CreateOverlay()
        {
            var font = FindFont();
            if (font == null)
            {
                // Fonts may not be loaded yet on the first scene; try again shortly.
                _retryAt = Time.unscaledTime + 1f;
                return;
            }

            _root = new GameObject("GoPetMod_FPS");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = (RenderMode)RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue;

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(_root.transform, false);
            _text = textGo.AddComponent<TextMeshProUGUI>();
            _text.font = font;
            _text.fontSize = 24;
            _text.fontStyle = (FontStyles)FontStyles.Bold;
            _text.raycastTarget = false;
            _text.outlineWidth = 0.2f;
            _text.outlineColor = new Color32(0, 0, 0, 255);

            var rect = _text.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(10f, -10f);
            rect.sizeDelta = new Vector2(400f, 40f);

            _root.SetActive(_visible);
            MelonLogger.Msg($"FPS overlay created (font: {font.name}).");
        }

        private static TMP_FontAsset FindFont()
        {
            var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            return fonts.Length > 0 ? fonts[0] : null;
        }

        private bool TogglePressed() => Keys.Pressed(k => k.f10Key, KeyCode.F10);
    }

    internal static class Keys
    {
        private static bool _legacyInputBroken;

        // The game may use the new Input System only; the legacy API then throws, so fall back to it.
        public static bool Pressed(Func<UnityEngine.InputSystem.Keyboard, UnityEngine.InputSystem.Controls.KeyControl> key, int legacy)
        {
            try
            {
                var keyboard = UnityEngine.InputSystem.Keyboard.current;
                if (keyboard != null)
                    return key(keyboard).wasPressedThisFrame;
            }
            catch (Exception)
            {
            }

            if (_legacyInputBroken)
                return false;
            try
            {
                return Input.GetKeyDown((KeyCode)legacy);
            }
            catch (Exception)
            {
                _legacyInputBroken = true;
                return false;
            }
        }
    }
}
