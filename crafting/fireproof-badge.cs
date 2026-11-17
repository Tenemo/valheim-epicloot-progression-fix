using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EpicLootProgressionFix
{
    // While the player is in the Ashlands, the hammer menu marks pieces that can't catch fire with a small shield holding a
    // flame in the bottom-left corner of their button, and the hovered piece's description says what that means. Cinders and
    // fire read only the piece's burnable flag, and AshlandsAmbientWear spares the same pieces from heat wear.
    internal static class FireproofBadge
    {
        internal const string Line = "<color=#FFA040>Fireproof:</color> cinders can't set it alight and Ashlands heat doesn't wear it down.";
        private const string Name = "FireproofBadge";
        // The badge's share of the button, and the shield rim's and dark edge's shares of the icon.
        private const float Share = .33f, Rim = .045f, Edge = .03f;
        private static Sprite sprite;
        private static bool shownInAshlands;

        internal static bool Fireproof(Piece piece)
        {
            var wear = piece ? piece.GetComponent<WearNTear>() : null;
            return wear && !wear.m_burnable;
        }

        private static bool InAshlands => Player.m_localPlayer && Player.m_localPlayer.GetCurrentBiome() == Heightmap.Biome.AshLands;

        private static void Show(BuildUiPieceButton button, bool inAshlands)
        {
            bool show = inAshlands && Fireproof(button.Piece);
            var badge = button.transform.Find(Name);
            if (!badge)
            {
                if (!show) return;
                var holder = new GameObject(Name, typeof(RectTransform), typeof(Image));
                var rect = (RectTransform)holder.transform;
                rect.SetParent(button.transform, false);
                rect.anchorMin = rect.pivot = Vector2.zero;
                rect.anchorMax = new Vector2(Share, Share);
                rect.offsetMin = rect.offsetMax = Vector2.one;
                var image = holder.GetComponent<Image>();
                image.sprite = sprite ??= Draw();
                image.preserveAspect = true;
                image.raycastTarget = false;
                badge = rect;
            }
            if (badge.gameObject.activeSelf != show) badge.gameObject.SetActive(show);
        }

        [HarmonyPatch(typeof(BuildUiPieceButton), nameof(BuildUiPieceButton.Setup))]
        private static class Buttons
        {
            private static void Postfix(BuildUiPieceButton __instance) => Show(__instance, InAshlands);
        }

        // Walking into or out of the Ashlands with the menu open.
        [HarmonyPatch(typeof(BuildUi), "Update")]
        private static class Biome
        {
            private static void Postfix(List<BuildUiPieceButton> ___m_pieceButtons)
            {
                bool inAshlands = InAshlands;
                if (inAshlands == shownInAshlands) return;
                shownInAshlands = inAshlands;
                foreach (var button in ___m_pieceButtons) Show(button, inAshlands);
            }
        }

        [HarmonyPatch(typeof(Hud), "SetupPieceInfo")]
        private static class Description
        {
            private static void Postfix(Piece piece, TMP_Text ___m_pieceDescription)
            {
                if (piece && Fireproof(piece) && InAshlands) ___m_pieceDescription.text += "\n" + Line;
            }
        }

        // A cream heater-shield outline with an orange and yellow flame inside, edged in dark ink, in the unit square with y up.
        private static Sprite Draw()
        {
            Color cream = new Color32(240, 232, 214, 255), orange = new Color32(255, 118, 26, 255), yellow = new Color32(255, 214, 74, 255);
            Color ink = new Color(.06f, .05f, .04f, .9f);
            bool Outline(float u, float v) => Shield(u, v, 0f) && !Shield(u, v, Rim);
            bool Fire(float u, float v) => Flame(u, v, .5f, .27f, .56f);
            Color Paint(float u, float v) =>
                Fire(u, v) ? (Drop((u - .5f) / .28f, (v - .29f) / .28f, .6f) ? yellow : orange)
                : Outline(u, v) ? cream
                : Near(u, v, (x, y) => Outline(x, y) || Fire(x, y)) ? ink : Color.clear;
            const int pixels = 64, samples = 4;
            var texture = new Texture2D(pixels, pixels, TextureFormat.RGBA32, true) { name = Name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            var colors = new Color[pixels * pixels];
            for (int y = 0; y < pixels; y++)
                for (int x = 0; x < pixels; x++)
                {
                    Color sum = Color.clear;
                    for (int sampleY = 0; sampleY < samples; sampleY++)
                        for (int sampleX = 0; sampleX < samples; sampleX++)
                        {
                            var sample = Paint((x + (sampleX + .5f) / samples) / pixels, (y + (sampleY + .5f) / samples) / pixels);
                            sum += new Color(sample.r * sample.a, sample.g * sample.a, sample.b * sample.a, sample.a);
                        }
                    sum /= samples * samples;
                    colors[y * pixels + x] = sum.a > 0f ? new Color(sum.r / sum.a, sum.g / sum.a, sum.b / sum.a, sum.a) : Color.clear;
                }
            texture.SetPixels(colors);
            texture.Apply(true, true);
            return Sprite.Create(texture, new Rect(0, 0, pixels, pixels), new Vector2(.5f, .5f));
        }

        // Straight sides down to the middle, then curving to a point; inset shrinks it evenly.
        private static bool Shield(float u, float v, float inset)
        {
            float top = .94f - inset, bottom = .04f + inset * 1.5f, half = .38f - inset;
            if (v > top || v < bottom) return false;
            float width = v >= .52f ? half : half * Mathf.Pow((v - bottom) / (.52f - bottom), .62f);
            return Mathf.Abs(u - .5f) <= width;
        }

        // A teardrop leaning right with a smaller tongue on each side, standing at (centre, bottom).
        private static bool Flame(float u, float v, float centre, float bottom, float height)
        {
            float x = (u - centre) / height, y = (v - bottom) / height;
            return Drop(x, y, .3f) || Drop((x + .2f) / .55f, (y - .02f) / .55f, -.5f) || Drop((x - .21f) / .45f, (y - .03f) / .45f, .5f);
        }

        // A round base of radius .3 centred at (0, .3) tapering to a tip at y 1 that leans by lean.
        private static bool Drop(float x, float y, float lean)
        {
            if (y < 0f || y > 1f) return false;
            if (y <= .3f) return x * x + (y - .3f) * (y - .3f) <= .09f;
            float t = (y - .3f) / .7f;
            return Mathf.Abs(x - lean * .25f * t * t) <= .3f * Mathf.Pow(1f - t, 1.15f);
        }

        private static bool Near(float u, float v, System.Func<float, float, bool> shape)
        {
            for (int step = 0; step < 8; step++)
            {
                float angle = step * Mathf.PI / 4f;
                if (shape(u + Mathf.Cos(angle) * Edge, v + Mathf.Sin(angle) * Edge)) return true;
            }
            return false;
        }
    }
}
