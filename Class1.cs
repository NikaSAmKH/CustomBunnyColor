using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CustomBunnyColor
{
    [BepInPlugin("com.modder.custombunnycolor", "Custom Bunny Color", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static ConfigEntry<string> P1_Hex;
        public static ConfigEntry<string> P2_Hex;
        public static ManualLogSource Log;

        private static Harmony harmony;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo("=== Custom Bunny Color Loading ===");

            P1_Hex = Config.Bind("Colors", "Player 1", "FF0000", "P1 Hex Color Code");
            P2_Hex = Config.Bind("Colors", "Player 2", "0000FF", "P2 Hex Color Code");

            harmony = new Harmony("com.modder.custombunnycolor");
            PatchPlayerType();

            SceneManager.sceneLoaded += OnSceneLoaded;

            // Something in this game's own scene-transition logic appears to wipe
            // out non-registered objects very early (even DontDestroyOnLoad ones),
            // faster than we can protect against directly. So instead of trying to
            // make ColorMenuUI merely survive, we re-ensure it exists after every
            // single scene load. Cheap, and self-healing against whatever is
            // destroying it.
            EnsureMenuUIExists();
        }

        private void Update()
        {
            // Safety net: if something outside our control keeps destroying the
            // menu object, this catches it within one frame instead of waiting
            // for the next scene load.
            if (menuUIInstance == null)
            {
                EnsureMenuUIExists();
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Log.LogInfo($"Scene loaded: {scene.name}");
            if (scene.name == "Menu")
            {
                // Intro Ragdolls
                var introContainer = GameObject.Find("Ragdolls (Intro)");
                if (introContainer != null && introContainer.GetComponent<MenuBunnyColorManager>() == null)
                {
                    introContainer.AddComponent<MenuBunnyColorManager>();
                    Log.LogInfo("Attached MenuBunnyColorManager to Ragdolls (Intro)");
                }

                // Story Mode Ragdolls
                var storyContainer = GameObject.Find("Ragdolls (Story Mode)");
                if (storyContainer != null && storyContainer.GetComponent<MenuBunnyColorManager>() == null)
                {
                    storyContainer.AddComponent<MenuBunnyColorManager>();
                    Log.LogInfo("Attached MenuBunnyColorManager to Ragdolls (Story Mode)");
                }
            }

            EnsureMenuUIExists();
        }

        private static ColorMenuUI menuUIInstance;

        private void EnsureMenuUIExists()
        {
            // FindObjectOfType is a fallback in case our cached reference went stale
            // (e.g. the object was destroyed by something outside our control and we
            // never got a callback for it).
            if (menuUIInstance == null)
            {
                menuUIInstance = UnityEngine.Object.FindObjectOfType<ColorMenuUI>();
            }

            if (menuUIInstance == null)
            {
                GameObject menuHost = new GameObject("CustomBunnyColor_MenuUI");
                menuHost.transform.SetParent(null);
                UnityEngine.Object.DontDestroyOnLoad(menuHost);
                menuUIInstance = menuHost.AddComponent<ColorMenuUI>();
                Log.LogInfo("EnsureMenuUIExists: (re)created CustomBunnyColor_MenuUI");
            }
        }

        private void PatchPlayerType()
        {
            try
            {
                Type playerType = AccessTools.TypeByName("SBM.Shared.Player");
                if (playerType == null) playerType = AccessTools.TypeByName("Player");

                if (playerType != null)
                {
                    MethodInfo updateMethod = AccessTools.Method(playerType, "Update");
                    if (updateMethod != null)
                    {
                        MethodInfo postfix = typeof(Plugin).GetMethod(nameof(PlayerUpdatePostfix), BindingFlags.NonPublic | BindingFlags.Static);
                        harmony.Patch(updateMethod, postfix: new HarmonyMethod(postfix));
                        Log.LogInfo("Successfully hooked Player.Update()");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.LogError($"Patch error: {ex.Message}");
            }
        }

        private static void PlayerUpdatePostfix(Component __instance)
        {
            if (__instance == null || __instance.gameObject == null) return;
            if (__instance.gameObject.GetComponent<BunnyColorController>() == null)
            {
                var controller = __instance.gameObject.AddComponent<BunnyColorController>();
                controller.Init(__instance);
            }
        }

        public static Color GetPlayerColor(int playerNum)
        {
            string hex = (playerNum == 1) ? P1_Hex.Value : P2_Hex.Value;
            if (!hex.StartsWith("#")) hex = "#" + hex;
            if (ColorUtility.TryParseHtmlString(hex, out Color col)) return col;
            return playerNum == 1 ? Color.red : Color.blue;
        }
    }

    /// <summary>
    /// Toggleable in-game menu for picking P1/P2 bunny colors via a hue/SV
    /// color picker (plus a hex field for typing exact values). Lives on a
    /// dedicated persistent GameObject so it survives scene loads.
    /// </summary>
    public class ColorMenuUI : MonoBehaviour
    {
        public KeyCode toggleKey = KeyCode.F2;
        private bool menuOpen = false;

        private string p1HexInput;
        private string p2HexInput;
        private bool p1Valid = true;
        private bool p2Valid = true;

        // HSV state per player, driven by the picker and kept in sync with the hex field
        private float p1Hue, p1Sat, p1Val;
        private float p2Hue, p2Sat, p2Val;

        private const int SV_SIZE = 180;
        private const int HUE_WIDTH = 22;
        private const int HUE_GAP = 8;
        // Actual content width inside one panel: (SV frame) + gap + (hue frame),
        // where each frame is its element size + 4 for the border drawn around it.
        private const int PANEL_CONTENT_WIDTH = (SV_SIZE + 4) + HUE_GAP + (HUE_WIDTH + 4); // 218
        private const int PANEL_PADDING = 14;
        private const int PANEL_WIDTH = PANEL_CONTENT_WIDTH + PANEL_PADDING * 2; // 246
        private const int PANEL_GAP = 24;
        private const int WINDOW_PADDING = 20;
        private const int WINDOW_WIDTH = WINDOW_PADDING * 2 + PANEL_WIDTH * 2 + PANEL_GAP;
        private const int WINDOW_HEIGHT = 490;

        private Rect windowRect = new Rect(40, 40, WINDOW_WIDTH, WINDOW_HEIGHT);

        private static readonly Color AccentP1 = new Color(1f, 0.45f, 0.25f);
        private static readonly Color AccentP2 = new Color(0.35f, 0.6f, 1f);

        private Texture2D hueTexture;
        private Texture2D svTexture1;
        private Texture2D svTexture2;
        private float svTex1CachedHue = -1f;
        private float svTex2CachedHue = -1f;

        private Texture2D windowBgTexture;
        private Texture2D panelBgTexture;
        private Texture2D swatchFrameTexture;

        private GUIStyle windowStyle;
        private GUIStyle titleStyle;
        private GUIStyle subtitleStyle;
        private GUIStyle panelStyle;
        private GUIStyle headerLabelStyle;
        private GUIStyle hexLabelStyle;
        private GUIStyle hexFieldStyle;
        private GUIStyle invalidStyle;
        private GUIStyle buttonStyle;
        private bool stylesReady = false;

        // Cursor state saved from just before we opened the menu, so we can
        // hand control back to the game exactly as we found it
        private CursorLockMode previousLockState;
        private bool previousCursorVisible;

        private void Awake()
        {
            Plugin.Log.LogInfo("ColorMenuUI.Awake() fired - component is alive.");
        }

        private void OnDestroy()
        {
            Plugin.Log.LogWarning($"ColorMenuUI.OnDestroy() fired - component is being destroyed! Scene at time of destroy: {gameObject.scene.name}");
        }

        private void OnDisable()
        {
            Plugin.Log.LogWarning("ColorMenuUI.OnDisable() fired - component was disabled.");
        }

        private void Start()
        {
            Plugin.Log.LogInfo("ColorMenuUI.Start() fired.");
            p1HexInput = Plugin.P1_Hex.Value;
            p2HexInput = Plugin.P2_Hex.Value;
            SyncHsvFromHex(1);
            SyncHsvFromHex(2);
            BuildHueTexture();
        }

        private void Update()
        {
            if (Input.GetKeyDown(toggleKey))
            {
                SetMenuOpen(!menuOpen);
            }

            // Force the cursor to stay unlocked/visible every frame the menu is
            // open, in case the game's own code tries to re-lock it on top of us.
            if (menuOpen)
            {
                if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
                if (!Cursor.visible) Cursor.visible = true;
            }
        }

        private void SetMenuOpen(bool open)
        {
            menuOpen = open;

            if (menuOpen)
            {
                // Remember exactly how the game had the cursor set up so we can restore it
                previousLockState = Cursor.lockState;
                previousCursorVisible = Cursor.visible;

                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;

                p1HexInput = Plugin.P1_Hex.Value;
                p2HexInput = Plugin.P2_Hex.Value;
                SyncHsvFromHex(1);
                SyncHsvFromHex(2);
            }
            else
            {
                Cursor.lockState = previousLockState;
                Cursor.visible = previousCursorVisible;
            }

            Plugin.Log.LogInfo($"Toggle key pressed. menuOpen is now {menuOpen}");
        }

        // ---------------------------------------------------------------
        // Styling
        // ---------------------------------------------------------------

        private void EnsureStyles()
        {
            if (stylesReady) return;

            windowBgTexture = MakeRoundedTexture(48, 18, new Color(0.09f, 0.09f, 0.115f, 0.96f));
            panelBgTexture = MakeRoundedTexture(40, 12, new Color(1f, 1f, 1f, 0.05f));
            swatchFrameTexture = MakeRoundedTexture(24, 6, Color.white);

            windowStyle = new GUIStyle(GUI.skin.window);
            windowStyle.normal.background = windowBgTexture;
            windowStyle.onNormal.background = windowBgTexture;
            windowStyle.border = new RectOffset(18, 18, 18, 18);
            windowStyle.padding = new RectOffset(WINDOW_PADDING, WINDOW_PADDING, 36, WINDOW_PADDING);
            windowStyle.normal.textColor = new Color(0.95f, 0.95f, 0.97f);
            windowStyle.fontStyle = FontStyle.Bold;
            windowStyle.fontSize = 16;
            windowStyle.alignment = TextAnchor.UpperCenter;

            titleStyle = new GUIStyle(GUI.skin.label);
            titleStyle.fontSize = 13;
            titleStyle.fontStyle = FontStyle.Normal;
            titleStyle.normal.textColor = new Color(0.7f, 0.72f, 0.78f);
            titleStyle.alignment = TextAnchor.MiddleCenter;

            panelStyle = new GUIStyle(GUI.skin.box);
            panelStyle.normal.background = panelBgTexture;
            panelStyle.border = new RectOffset(12, 12, 12, 12);
            panelStyle.padding = new RectOffset(PANEL_PADDING, PANEL_PADDING, 12, 14);
            panelStyle.margin = new RectOffset(0, 0, 0, 0);

            headerLabelStyle = new GUIStyle(GUI.skin.label);
            headerLabelStyle.fontSize = 16;
            headerLabelStyle.fontStyle = FontStyle.Bold;
            headerLabelStyle.normal.textColor = Color.white;

            hexLabelStyle = new GUIStyle(GUI.skin.label);
            hexLabelStyle.fontSize = 12;
            hexLabelStyle.normal.textColor = new Color(0.75f, 0.77f, 0.8f);
            hexLabelStyle.alignment = TextAnchor.MiddleLeft;

            hexFieldStyle = new GUIStyle(GUI.skin.textField);
            hexFieldStyle.fontSize = 13;
            hexFieldStyle.alignment = TextAnchor.MiddleCenter;

            invalidStyle = new GUIStyle(GUI.skin.label);
            invalidStyle.fontSize = 11;
            invalidStyle.normal.textColor = new Color(1f, 0.45f, 0.4f);

            buttonStyle = new GUIStyle(GUI.skin.button);
            buttonStyle.fontSize = 13;
            buttonStyle.fontStyle = FontStyle.Bold;
            buttonStyle.padding = new RectOffset(10, 10, 8, 8);

            stylesReady = true;
        }

        private void DrawAccentDivider(float width)
        {
            Rect r = GUILayoutUtility.GetRect(width, 3);
            Rect left = new Rect(r.x, r.y, r.width / 2f, r.height);
            Rect right = new Rect(r.x + r.width / 2f, r.y, r.width / 2f, r.height);
            Color oldC = GUI.color;
            GUI.color = new Color(AccentP1.r, AccentP1.g, AccentP1.b, 0.6f);
            GUI.DrawTexture(left, Texture2D.whiteTexture);
            GUI.color = new Color(AccentP2.r, AccentP2.g, AccentP2.b, 0.6f);
            GUI.DrawTexture(right, Texture2D.whiteTexture);
            GUI.color = oldC;
        }

        private void OnGUI()
        {
            if (!menuOpen) return;

            if (hueTexture == null) BuildHueTexture();
            EnsureStyles();

            windowRect = GUI.Window(0xB00B1E5, windowRect, DrawWindow, "Custom Bunny Color", windowStyle);

            // Keep the window fully on-screen even after dragging
            windowRect.x = Mathf.Clamp(windowRect.x, 0, Mathf.Max(0, Screen.width - windowRect.width));
            windowRect.y = Mathf.Clamp(windowRect.y, 0, Mathf.Max(0, Screen.height - windowRect.height));
        }

        private void DrawWindow(int id)
        {
            GUILayout.Label($"Toggle with [{toggleKey}]  |  cursor is free while open", titleStyle);
            GUILayout.Space(8);
            DrawAccentDivider(WINDOW_WIDTH - WINDOW_PADDING * 2);
            GUILayout.Space(14);

            GUILayout.BeginHorizontal();
            DrawPlayerPicker("Player 1", 1, AccentP1);
            GUILayout.Space(PANEL_GAP);
            DrawPlayerPicker("Player 2", 2, AccentP2);
            GUILayout.EndHorizontal();

            GUILayout.Space(16);

            if (GUILayout.Button("Reset to Defaults", buttonStyle, GUILayout.Height(30)))
            {
                p1HexInput = "FF9EBA";
                p2HexInput = "F9CB53";
                SyncHsvFromHex(1);
                SyncHsvFromHex(2);
                ApplyIfValid(1);
                ApplyIfValid(2);
            }

            GUI.DragWindow(new Rect(0, 0, 10000, 30));
        }

        private void DrawPlayerPicker(string label, int playerNum, Color accent)
        {
            GUILayout.BeginVertical(panelStyle, GUILayout.Width(PANEL_WIDTH));

            Color preview = Color.HSVToRGB(
                playerNum == 1 ? p1Hue : p2Hue,
                playerNum == 1 ? p1Sat : p2Sat,
                playerNum == 1 ? p1Val : p2Val);

            // Header row: colored accent dot + name
            GUILayout.BeginHorizontal();
            Rect dotRect = GUILayoutUtility.GetRect(12, 12, GUILayout.Width(12), GUILayout.Height(12));
            Color oldC = GUI.color;
            GUI.color = accent;
            GUI.DrawTexture(dotRect, Texture2D.whiteTexture);
            GUI.color = oldC;
            GUILayout.Space(6);
            GUILayout.Label(label, headerLabelStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(8);

            GUILayout.BeginHorizontal();

            // SV square with a subtle frame
            Rect svFrame = GUILayoutUtility.GetRect(SV_SIZE + 4, SV_SIZE + 4, GUILayout.Width(SV_SIZE + 4), GUILayout.Height(SV_SIZE + 4));
            GUI.color = new Color(0f, 0f, 0f, 0.4f);
            GUI.DrawTexture(svFrame, Texture2D.whiteTexture);
            GUI.color = Color.white;
            Rect svRect = new Rect(svFrame.x + 2, svFrame.y + 2, SV_SIZE, SV_SIZE);
            Texture2D svTex = GetSvTexture(playerNum);
            GUI.DrawTexture(svRect, svTex);
            DrawSvCursor(svRect, playerNum);
            HandleSvInput(svRect, playerNum);

            GUILayout.Space(HUE_GAP);

            // Hue bar with matching frame
            Rect hueFrame = GUILayoutUtility.GetRect(HUE_WIDTH + 4, SV_SIZE + 4, GUILayout.Width(HUE_WIDTH + 4), GUILayout.Height(SV_SIZE + 4));
            GUI.color = new Color(0f, 0f, 0f, 0.4f);
            GUI.DrawTexture(hueFrame, Texture2D.whiteTexture);
            GUI.color = Color.white;
            Rect hueRect = new Rect(hueFrame.x + 2, hueFrame.y + 2, HUE_WIDTH, SV_SIZE);
            GUI.DrawTexture(hueRect, hueTexture);
            DrawHueCursor(hueRect, playerNum);
            HandleHueInput(hueRect, playerNum);

            GUILayout.EndHorizontal();

            GUILayout.Space(10);

            // Live preview swatch with frame
            Rect swatchOuter = GUILayoutUtility.GetRect(PANEL_WIDTH - 28, 34);
            GUI.color = new Color(0f, 0f, 0f, 0.35f);
            GUI.DrawTexture(swatchOuter, swatchFrameTexture);
            GUI.color = preview;
            Rect swatchInner = new Rect(swatchOuter.x + 2, swatchOuter.y + 2, swatchOuter.width - 4, swatchOuter.height - 4);
            GUI.DrawTexture(swatchInner, swatchFrameTexture);
            GUI.color = Color.white;

            GUILayout.Space(10);

            // Hex field, kept in sync both ways
            GUILayout.BeginHorizontal();
            GUILayout.Label("HEX", hexLabelStyle, GUILayout.Width(32));
            string currentHex = (playerNum == 1) ? p1HexInput : p2HexInput;
            string newHex = GUILayout.TextField(currentHex, 8, hexFieldStyle, GUILayout.Height(24));
            if (newHex != currentHex)
            {
                if (playerNum == 1) p1HexInput = newHex; else p2HexInput = newHex;
                SyncHsvFromHex(playerNum);
                ApplyIfValid(playerNum);
            }
            GUILayout.EndHorizontal();

            bool valid = (playerNum == 1) ? p1Valid : p2Valid;
            if (!valid)
            {
                GUILayout.Label("Invalid hex code", invalidStyle);
            }

            GUILayout.EndVertical();
        }

        private void HandleSvInput(Rect svRect, int playerNum)
        {
            Event e = Event.current;
            bool isDragEvent = (e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && svRect.Contains(e.mousePosition);
            if (!isDragEvent) return;

            // Standard input mapping: X = saturation, Y = value (top = value 1).
            // Map mouse Y so top of the SV rect corresponds to value = 1.
            float s = Mathf.Clamp01((e.mousePosition.x - svRect.x) / svRect.width);
            float v = Mathf.Clamp01(1f - (e.mousePosition.y - svRect.y) / svRect.height);

            if (playerNum == 1) { p1Sat = s; p1Val = v; } else { p2Sat = s; p2Val = v; }

            SyncHexFromHsv(playerNum);
            ApplyIfValid(playerNum);
            e.Use();
        }

        private void HandleHueInput(Rect hueRect, int playerNum)
        {
            Event e = Event.current;
            bool isDragEvent = (e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && hueRect.Contains(e.mousePosition);
            if (!isDragEvent) return;
            // Invert Y so mouse mapping matches the hue texture orientation
            float h = Mathf.Clamp01((e.mousePosition.y - hueRect.y) / hueRect.height);

            if (playerNum == 1) p1Hue = h; else p2Hue = h;

            SyncHexFromHsv(playerNum);
            ApplyIfValid(playerNum);
            e.Use();
        }

        private void DrawSvCursor(Rect svRect, int playerNum)
        {
            float s = (playerNum == 1) ? p1Sat : p2Sat;
            float v = (playerNum == 1) ? p1Val : p2Val;
            float hue = (playerNum == 1) ? p1Hue : p2Hue;

            float cx = svRect.x + s * svRect.width;
            float cy = svRect.y + (1f - v) * svRect.height;

            // Ring cursor: dark outer ring, white inner ring, so it's visible on any background color
            const float outer = 12f;
            const float inner = 9f;
            Color oldC = GUI.color;

            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            GUI.DrawTexture(new Rect(cx - outer / 2f, cy - outer / 2f, outer, outer), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(cx - inner / 2f, cy - inner / 2f, inner, inner), Texture2D.whiteTexture);
            GUI.color = Color.HSVToRGB(hue, s, v);
            const float core = 5f;
            GUI.DrawTexture(new Rect(cx - core / 2f, cy - core / 2f, core, core), Texture2D.whiteTexture);

            GUI.color = oldC;
        }

        private void DrawHueCursor(Rect hueRect, int playerNum)
        {
            float h = (playerNum == 1) ? p1Hue : p2Hue;
            float cy = hueRect.y + h * hueRect.height;

            Color oldC = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            GUI.DrawTexture(new Rect(hueRect.x - 3, cy - 3, hueRect.width + 6, 6), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(hueRect.x - 2, cy - 2, hueRect.width + 4, 4), Texture2D.whiteTexture);
            GUI.color = oldC;
        }

        private Texture2D GetSvTexture(int playerNum)
        {
            float hue = (playerNum == 1) ? p1Hue : p2Hue;

            if (playerNum == 1)
            {
                if (svTexture1 == null || !Mathf.Approximately(svTex1CachedHue, hue))
                {
                    svTexture1 = BuildSvTexture(hue);
                    svTex1CachedHue = hue;
                }
                return svTexture1;
            }
            else
            {
                if (svTexture2 == null || !Mathf.Approximately(svTex2CachedHue, hue))
                {
                    svTexture2 = BuildSvTexture(hue);
                    svTex2CachedHue = hue;
                }
                return svTexture2;
            }
        }

        private Texture2D BuildSvTexture(float hue)
        {
            Texture2D tex = new Texture2D(SV_SIZE, SV_SIZE, TextureFormat.RGB24, false);
            tex.wrapMode = TextureWrapMode.Clamp;

            // Build texture with vertical output flipped so the visual appears inverted
            // (top shows low value, bottom shows high value) while input remains standard.
            for (int y = 0; y < SV_SIZE; y++)
            {
                float v = (float)y / (SV_SIZE - 1);
                for (int x = 0; x < SV_SIZE; x++)
                {
                    float s = (float)x / (SV_SIZE - 1);
                    tex.SetPixel(x, y, Color.HSVToRGB(hue, s, v));
                }
            }
            tex.Apply();
            return tex;
        }

        private void BuildHueTexture()
        {
            hueTexture = new Texture2D(1, SV_SIZE, TextureFormat.RGB24, false);
            hueTexture.wrapMode = TextureWrapMode.Clamp;

            for (int y = 0; y < SV_SIZE; y++)
            {
                float h = 1f - (float)y / (SV_SIZE - 1);
                hueTexture.SetPixel(0, y, Color.HSVToRGB(h, 1f, 1f));
            }
            hueTexture.Apply();
        }

        /// <summary>
        /// Builds a small rounded-rect tile meant to be used as a 9-sliced
        /// GUIStyle background (via style.border) so it stretches cleanly to
        /// any panel size without re-generating a full-size texture.
        /// </summary>
        private Texture2D MakeRoundedTexture(int size, int radius, Color fill)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float alpha = RoundedAlpha(x, y, size, size, radius);
                    tex.SetPixel(x, y, new Color(fill.r, fill.g, fill.b, fill.a * alpha));
                }
            }
            tex.Apply();
            return tex;
        }

        private float RoundedAlpha(int x, int y, int w, int h, int r)
        {
            float fx = x + 0.5f;
            float fy = y + 0.5f;
            float dx = 0f, dy = 0f;

            if (fx < r) dx = r - fx;
            else if (fx > w - r) dx = fx - (w - r);

            if (fy < r) dy = r - fy;
            else if (fy > h - r) dy = fy - (h - r);

            float dist = Mathf.Sqrt(dx * dx + dy * dy);
            if (dist <= r - 1f) return 1f;
            if (dist >= r + 1f) return 0f;
            return 1f - (dist - (r - 1f)) / 2f;
        }

        private void SyncHsvFromHex(int playerNum)
        {
            string hex = (playerNum == 1) ? p1HexInput : p2HexInput;
            if (string.IsNullOrEmpty(hex))
            {
                if (playerNum == 1) p1Valid = false; else p2Valid = false;
                return;
            }

            string normalized = hex.StartsWith("#") ? hex : "#" + hex;
            bool ok = ColorUtility.TryParseHtmlString(normalized, out Color parsed);

            if (playerNum == 1) p1Valid = ok; else p2Valid = ok;
            if (!ok) return;

            Color.RGBToHSV(parsed, out float h, out float s, out float v);
            if (playerNum == 1) { p1Hue = h; p1Sat = s; p1Val = v; }
            else { p2Hue = h; p2Sat = s; p2Val = v; }
        }

        private void SyncHexFromHsv(int playerNum)
        {
            float h = (playerNum == 1) ? p1Hue : p2Hue;
            float s = (playerNum == 1) ? p1Sat : p2Sat;
            float v = (playerNum == 1) ? p1Val : p2Val;

            Color rgb = Color.HSVToRGB(h, s, v);
            string hex = ColorUtility.ToHtmlStringRGB(rgb);

            if (playerNum == 1) { p1HexInput = hex; p1Valid = true; }
            else { p2HexInput = hex; p2Valid = true; }
        }

        private void ApplyIfValid(int playerNum)
        {
            string hex = (playerNum == 1) ? p1HexInput : p2HexInput;
            string normalized = hex.StartsWith("#") ? hex : "#" + hex;

            if (!ColorUtility.TryParseHtmlString(normalized, out _))
            {
                return;
            }

            string stored = normalized.TrimStart('#');

            if (playerNum == 1) Plugin.P1_Hex.Value = stored;
            else Plugin.P2_Hex.Value = stored;
        }
    }

    public class MenuBunnyColorManager : MonoBehaviour
    {
        private void Update()
        {
            // Intro Menu Ragdolls
            Transform r1Intro = transform.Find("RagdollMenu_1");
            if (r1Intro != null) ApplyColorToTransform(r1Intro, Plugin.GetPlayerColor(1));

            Transform r2Intro = transform.Find("RagdollMenu_2");
            if (r2Intro != null) ApplyColorToTransform(r2Intro, Plugin.GetPlayerColor(2));

            // Story Mode Ragdolls
            Transform r1Story = transform.Find("RagdollStoryMode_P1");
            if (r1Story != null) ApplyColorToTransform(r1Story, Plugin.GetPlayerColor(1));

            Transform r2Story = transform.Find("RagdollStoryMode_P2");
            if (r2Story != null) ApplyColorToTransform(r2Story, Plugin.GetPlayerColor(2));
        }

        private void ApplyColorToTransform(Transform root, Color targetColor)
        {
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;

                string n = r.gameObject.name.ToLower();
                if (n.Contains("blood") || n.Contains("gore") || n.Contains("splat") || n.Contains("eye") || n.Contains("mouth") ||
                    n.Contains("face") || n.Contains("tail") || n.Contains("paw") || n.Contains("hand") || n.Contains("foot") ||
                    n.Contains("headband") || n.Contains("sweat") || n.Contains("drip") || n.Contains("water") || n.Contains("grab")) continue;

                foreach (Material mat in r.materials)
                {
                    if (mat == null) continue;
                    string mName = mat.name.ToLower();
                    if (mName.Contains("blood") || mName.Contains("gore") || mName.Contains("splat") || mName.Contains("eye") ||
                        mName.Contains("mouth") || mName.Contains("face") || mName.Contains("tail") || mName.Contains("white") ||
                        mName.Contains("sweat") || mName.Contains("drip") || mName.Contains("grab")) continue;

                    Color clr = GetMatColor(mat);
                    float sat = Mathf.Max(clr.r, Mathf.Max(clr.g, clr.b)) - Mathf.Min(clr.r, Mathf.Min(clr.g, clr.b));

                    if (sat > 0.05f || mName.Contains("suit") || mName.Contains("body") || mName.Contains("player"))
                    {
                        SetMatColor(mat, targetColor);
                    }
                }
            }
        }

        private Color GetMatColor(Material mat)
        {
            if (mat.HasProperty("_Color")) return mat.GetColor("_Color");
            if (mat.HasProperty("_BaseColor")) return mat.GetColor("_BaseColor");
            return mat.color;
        }

        private void SetMatColor(Material mat, Color clr)
        {
            mat.color = clr;
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", clr);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", clr);
            if (mat.HasProperty("_TintColor")) mat.SetColor("_TintColor", clr);
            if (mat.HasProperty("_MainColor")) mat.SetColor("_MainColor", clr);
        }
    }

    public class DespawnSmokeMarker : MonoBehaviour { public int assignedPlayerNumber = 1; }

    public class BunnyColorController : MonoBehaviour
    {
        public static List<BunnyColorController> activeControllers = new List<BunnyColorController>();
        public int playerNumber = 1;

        private void OnEnable() { if (!activeControllers.Contains(this)) activeControllers.Add(this); }
        private void OnDisable() { activeControllers.Remove(this); }

        public void Init(Component p)
        {
            playerNumber = (p.gameObject.name.Contains("2") || p.gameObject.name.Contains("[P2]")) ? 2 : 1;
            if (!activeControllers.Contains(this)) activeControllers.Add(this);
        }

        private void LateUpdate()
        {
            Color targetColor = Plugin.GetPlayerColor(playerNumber);
            ApplySuitColor(transform, targetColor);

            GameObject ragdoll = GameObject.Find($"Player {playerNumber} Ragdoll");
            if (ragdoll != null && ragdoll.activeInHierarchy)
            {
                ApplySuitColor(ragdoll.transform, targetColor);
            }

            if (activeControllers.Count > 0 && activeControllers[0] == this)
            {
                ApplyGlobalSmokeColorsToAll();
            }
        }

        private void ApplySuitColor(Transform root, Color targetColor)
        {
            foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                string n = ps.gameObject.name.ToLower();
                if (n.Contains("blood") || n.Contains("gore") || n.Contains("splat") || n.Contains("bleed") ||
                    n.Contains("jump") || n.Contains("step") || n.Contains("walk") || n.Contains("run") || n.Contains("foot") ||
                    n.Contains("sweat") || n.Contains("drip") || n.Contains("water") || n.Contains("persp") || n.Contains("grab")) continue;

                var main = ps.main;
                main.startColor = new ParticleSystem.MinMaxGradient(targetColor);

                var colLifetime = ps.colorOverLifetime;
                if (colLifetime.enabled) colLifetime.enabled = false;

                UpdateParticles(ps, targetColor);

                var renderer = ps.GetComponent<ParticleSystemRenderer>();
                if (renderer != null && renderer.sharedMaterial != null)
                {
                    string matName = renderer.sharedMaterial.name.ToLower();
                    if (!matName.Contains("blood") && !matName.Contains("sweat") && !matName.Contains("drip") && !matName.Contains("grab"))
                    {
                        SetMatColor(renderer.material, targetColor);
                    }
                }
            }

            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;

                string n = r.gameObject.name.ToLower();
                if (n.Contains("blood") || n.Contains("gore") || n.Contains("splat") || n.Contains("eye") || n.Contains("mouth") ||
                    n.Contains("face") || n.Contains("tail") || n.Contains("paw") || n.Contains("hand") || n.Contains("foot") ||
                    n.Contains("headband") || n.Contains("sweat") || n.Contains("drip") || n.Contains("water") || n.Contains("grab")) continue;

                foreach (Material mat in r.materials)
                {
                    if (mat == null) continue;
                    string mName = mat.name.ToLower();
                    if (mName.Contains("blood") || mName.Contains("gore") || mName.Contains("splat") || mName.Contains("eye") ||
                        mName.Contains("mouth") || mName.Contains("face") || mName.Contains("tail") || mName.Contains("white") ||
                        mName.Contains("sweat") || mName.Contains("drip") || mName.Contains("grab")) continue;

                    Color clr = GetMatColor(mat);
                    float sat = Mathf.Max(clr.r, Mathf.Max(clr.g, clr.b)) - Mathf.Min(clr.r, Mathf.Min(clr.g, clr.b));

                    if (sat > 0.05f || mName.Contains("suit") || mName.Contains("body") || mName.Contains("player"))
                    {
                        SetMatColor(mat, targetColor);
                    }
                }
            }
        }

        private static void ApplyGlobalSmokeColorsToAll()
        {
            foreach (var obj in UnityEngine.Object.FindObjectsOfType<GameObject>(true))
            {
                if (obj == null || !obj.activeInHierarchy) continue;
                if (obj.name.ToLower().Contains("ragdolldespawn"))
                {
                    DespawnSmokeMarker marker = obj.GetComponent<DespawnSmokeMarker>();
                    if (marker == null)
                    {
                        marker = obj.AddComponent<DespawnSmokeMarker>();
                        float minDist = float.MaxValue;
                        int pNum = 1;

                        foreach (var ctrl in activeControllers)
                        {
                            if (ctrl == null) continue;
                            float d1 = Vector3.Distance(ctrl.transform.position, obj.transform.position);
                            float d2 = float.MaxValue;
                            GameObject rag = GameObject.Find($"Player {ctrl.playerNumber} Ragdoll");
                            if (rag != null) d2 = Vector3.Distance(rag.transform.position, obj.transform.position);

                            float closest = Mathf.Min(d1, d2);
                            if (closest < minDist) { minDist = closest; pNum = ctrl.playerNumber; }
                        }
                        marker.assignedPlayerNumber = pNum;
                    }

                    Color smokeTargetColor = Plugin.GetPlayerColor(marker.assignedPlayerNumber);
                    foreach (var ps in obj.GetComponentsInChildren<ParticleSystem>(true))
                    {
                        var main = ps.main;
                        main.startColor = new ParticleSystem.MinMaxGradient(smokeTargetColor);
                        var colLife = ps.colorOverLifetime;
                        if (colLife.enabled) colLife.enabled = false;
                        UpdateParticles(ps, smokeTargetColor);

                        var renderer = ps.GetComponent<ParticleSystemRenderer>();
                        if (renderer != null && renderer.sharedMaterial != null) SetMatColor(renderer.material, smokeTargetColor);
                    }
                }
            }
        }

        private static void UpdateParticles(ParticleSystem ps, Color clr)
        {
            ParticleSystem.Particle[] arr = new ParticleSystem.Particle[ps.main.maxParticles];
            int count = ps.GetParticles(arr);
            for (int i = 0; i < count; i++) arr[i].startColor = clr;
            ps.SetParticles(arr, count);
        }

        private Color GetMatColor(Material mat)
        {
            if (mat.HasProperty("_Color")) return mat.GetColor("_Color");
            if (mat.HasProperty("_BaseColor")) return mat.GetColor("_BaseColor");
            return mat.color;
        }

        private static void SetMatColor(Material mat, Color clr)
        {
            mat.color = clr;
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", clr);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", clr);
            if (mat.HasProperty("_TintColor")) mat.SetColor("_TintColor", clr);
            if (mat.HasProperty("_MainColor")) mat.SetColor("_MainColor", clr);
        }
    }
}