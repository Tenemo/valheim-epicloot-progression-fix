using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EpicLootProgressionFix
{
    // Shows the protection bubble on the health bar as thin, sharp ice cracks, at the bar's own scale: it starts at the bar's empty
    // end and covers as much health as the bubble absorbs, continuing past the bar's full end when the bubble is larger.
    // The bubble's remaining amount appears in blue just above the health number.
    internal static class ProtectionHealthBar
    {
        private static readonly int ShieldHash = "Staff_shield".GetStableHashCode();
        private static readonly AccessTools.FieldRef<GuiBar, RectTransform> Bar = AccessTools.FieldRefAccess<GuiBar, RectTransform>("m_bar");
        private static readonly AccessTools.FieldRef<GuiBar, float> Width = AccessTools.FieldRefAccess<GuiBar, float>("m_width");
        private static readonly Color Cracks = new Color(.45f, .7f, .9f, .56f), ShieldBlue = new Color(.55f, .85f, 1f);
        private const float Margin = 12f, OutlineWidth = .25f, ExtraBold = .15f;

        private sealed class View
        {
            internal RectTransform Root;
            internal readonly List<RawImage> Images = new List<RawImage>();
        }

        private static View inner, outer;
        private static RectTransform clip;
        private static TMP_Text shieldText, healthText;
        private static Material shieldMaterial;
        private static Texture2D cracks, glints, glow;
        private static Rect number;
        private static float gameBold = -1f, healthOutline;
        private static int shownAmount = -1;
        private static Color healthOutlineColor;
        private static bool healthStyled;
        private static readonly Vector3[] Corners = new Vector3[4];

        [HarmonyPatch(typeof(Hud), "UpdateHealth")]
        private static class Refresh
        {
            private static void Postfix(Hud __instance, Player player)
            {
                var shield = player ? player.GetSEMan().GetStatusEffect(ShieldHash) as SE_Shield : null;
                float remaining = shield ? Mathf.Max(0f, GameFields.ShieldCapacity(shield) - GameFields.ShieldDamage(shield)) : 0f;
                bool shown = remaining > 0f;
                var bar = Bar(__instance.m_healthBarFast);
                if (shown && (inner == null || !inner.Root || !shieldText)) Build(__instance, bar);
                if (inner != null && inner.Root) inner.Root.gameObject.SetActive(shown);
                if (shieldText) shieldText.gameObject.SetActive(shown);
                bool covered = false;
                if (shown) covered = Layout(__instance, bar, player, remaining);
                else if (clip) clip.gameObject.SetActive(false);
                StyleHealth(__instance.m_healthText, covered);
            }
        }

        private static void Outline(TMP_Text text, float width, Color color, float bold)
        {
            text.outlineWidth = width;
            text.outlineColor = color;
            text.fontMaterial.SetFloat(ShaderUtilities.ID_FaceDilate, bold);
            text.UpdateMeshPadding();
            text.SetVerticesDirty();
        }

        // While the ice covers the health number, a bolder black outline keeps it readable; it's restored once uncovered.
        private static void StyleHealth(TMP_Text text, bool covered)
        {
            if (healthStyled && (!covered || healthText != text))
            {
                if (healthText) Outline(healthText, healthOutline, healthOutlineColor, gameBold);
                healthStyled = false;
            }
            if (!covered || healthStyled || !text) return;
            healthText = text;
            healthOutline = text.outlineWidth;
            healthOutlineColor = text.outlineColor;
            Outline(text, OutlineWidth, Color.black, gameBold + ExtraBold);
            healthStyled = true;
        }

        private static void Build(Hud hud, RectTransform bar)
        {
            if (inner != null && inner.Root) Object.Destroy(inner.Root.gameObject);
            if (clip) Object.Destroy(clip.gameObject);
            if (shieldText) Object.Destroy(shieldText.gameObject);
            if (shieldMaterial) Object.Destroy(shieldMaterial);
            MakeTextures();
            // The game's own digit weight, read before the health number is ever restyled.
            var health = hud.m_healthText;
            if (gameBold < 0f) gameBold = health.fontSharedMaterial.GetFloat(ShaderUtilities.ID_FaceDilate);
            // The health number is the fill's own child: the ice goes first among the fill's children, so it draws above the
            // fill and below the number.
            inner = NewView(bar);
            outer = null;
            clip = null;
            Transform masked = null;
            for (var node = bar.parent; node; node = node.parent)
                if (node.GetComponent<Mask>() || node.GetComponent<RectMask2D>()) masked = node;
            var top = masked ? masked.parent : bar.parent;
            if (masked)
            {
                // Past the bar's full end the ice draws outside the bar's mask, in a clip of its own.
                clip = new GameObject("ProtectionOverflow", typeof(RectTransform), typeof(RectMask2D)).GetComponent<RectTransform>();
                clip.SetParent(top, false);
                clip.SetSiblingIndex(masked.GetSiblingIndex() + 1);
                clip.anchorMin = clip.anchorMax = new Vector2(.5f, .5f);
                clip.pivot = Vector2.zero;
                outer = NewView(clip);
            }
            // The shield number copies the health number, outside the mask and drawn last.
            shieldText = Object.Instantiate(health.gameObject, top, false).GetComponent<TMP_Text>();
            shieldText.name = "ProtectionNumber";
            shownAmount = -1;
            shieldText.transform.SetAsLastSibling();
            shieldText.fontMaterial = shieldMaterial = new Material(health.fontSharedMaterial);
            shieldText.color = ShieldBlue;
            Outline(shieldText, OutlineWidth, Color.black, gameBold + ExtraBold);
        }

        private static View NewView(Transform parent)
        {
            var view = new View { Root = new GameObject("ProtectionIce", typeof(RectTransform)).GetComponent<RectTransform>() };
            view.Root.SetParent(parent, false);
            view.Root.SetSiblingIndex(0);
            view.Root.anchorMin = view.Root.anchorMax = new Vector2(.5f, .5f);
            view.Root.pivot = Vector2.zero;
            void Add(string name, Texture texture, Color color)
            {
                var image = new GameObject(name, typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
                image.transform.SetParent(view.Root, false);
                image.raycastTarget = false;
                image.texture = texture;
                image.color = color;
                image.rectTransform.anchorMin = image.rectTransform.anchorMax = image.rectTransform.pivot = Vector2.zero;
                view.Images.Add(image);
            }
            Add("Cracks", cracks, Cracks);
            Add("Glints", glints, Color.white);
            Add("Top", Texture2D.whiteTexture, new Color(.88f, .98f, 1f, .475f));
            Add("Bottom", Texture2D.whiteTexture, new Color(.7f, .9f, 1f, .4f));
            Add("EdgeGlow", glow, new Color(.75f, .95f, 1f, .85f));
            Add("Edge", Texture2D.whiteTexture, new Color(.95f, 1f, 1f, 1f));
            return view;
        }

        // Puts a pivot-zero rectangle at a point in the bar's own frame, with the bar's rotation and scale.
        private static void Align(RectTransform rect, RectTransform bar, Vector3 origin, float x, float y)
        {
            rect.rotation = bar.rotation;
            Vector3 scale = bar.lossyScale, parent = rect.parent.lossyScale;
            rect.localScale = new Vector3(scale.x / parent.x, scale.y / parent.y, scale.z / parent.z);
            rect.position = origin + bar.TransformVector(new Vector3(x, y, 0f));
        }

        private static void Place(Graphic image, float x, float y, float width, float height)
        {
            image.rectTransform.anchoredPosition = new Vector2(x, y);
            image.rectTransform.sizeDelta = new Vector2(Mathf.Max(0f, width), Mathf.Max(0f, height));
        }

        // Lays the ice out for the bubble's remaining amount and reports whether it covers the health number.
        private static bool Layout(Hud hud, RectTransform bar, Player player, float remaining)
        {
            bar.GetWorldCorners(Corners);
            Vector3 origin = Corners[0];
            float height = bar.rect.height, full = Width(hud.m_healthBarFast);
            float length = Mathf.Min(remaining / player.GetMaxHealth() * full, ScreenRoom(bar, origin));
            MeasureNumber(hud.m_healthText, bar);
            PlaceShieldNumber(hud, bar, origin, height, remaining);
            Align(inner.Root, bar, origin, 0f, 0f);
            inner.Root.sizeDelta = new Vector2(length, height);
            Draw(inner, length, height);
            float overflow = length - full;
            if (clip) clip.gameObject.SetActive(overflow > 0f);
            if (outer != null && overflow > 0f)
            {
                Align(clip, bar, origin, full, -Margin);
                clip.sizeDelta = new Vector2(overflow + Margin, height + 2f * Margin);
                outer.Root.localRotation = Quaternion.identity;
                outer.Root.localScale = Vector3.one;
                outer.Root.localPosition = new Vector3(-full, Margin, 0f);
                outer.Root.sizeDelta = new Vector2(length, height);
                Draw(outer, length, height);
            }
            return number.center.x < length;
        }

        // How far the ice can reach along the bar before the screen's outer eighth, where the hotbar sits, so a huge bubble stops
        // short of it.
        private static float ScreenRoom(RectTransform bar, Vector3 origin)
        {
            var canvas = bar.GetComponentInParent<Canvas>();
            if (!canvas) return float.MaxValue;
            var screen = (RectTransform)canvas.rootCanvas.transform;
            Vector2 start = screen.InverseTransformPoint(origin), step = screen.InverseTransformVector(bar.TransformVector(Vector3.right));
            var whole = screen.rect;
            var bounds = new Rect(whole.x + whole.width * .125f, whole.y + whole.height * .125f, whole.width * .75f, whole.height * .75f);
            float room = float.MaxValue;
            if (step.x > 1e-4f) room = Mathf.Min(room, (bounds.xMax - start.x) / step.x);
            if (step.x < -1e-4f) room = Mathf.Min(room, (bounds.xMin - start.x) / step.x);
            if (step.y > 1e-4f) room = Mathf.Min(room, (bounds.yMax - start.y) / step.y);
            if (step.y < -1e-4f) room = Mathf.Min(room, (bounds.yMin - start.y) / step.y);
            return Mathf.Max(0f, room);
        }

        // The health number's text box in the bar's frame, measured from the bar's corner.
        private static void MeasureNumber(TMP_Text text, RectTransform bar)
        {
            Vector2 centre = text.rectTransform.rect.center, half = new Vector2(text.preferredWidth, text.preferredHeight) / 2f;
            Vector2 low = Vector2.positiveInfinity, high = Vector2.negativeInfinity;
            for (int corner = 0; corner < 4; corner++)
            {
                var offset = new Vector2((corner & 1) == 0 ? -half.x : half.x, (corner & 2) == 0 ? -half.y : half.y);
                Vector2 point = (Vector2)bar.InverseTransformPoint(text.transform.TransformPoint(centre + offset)) - bar.rect.min;
                low = Vector2.Min(low, point); high = Vector2.Max(high, point);
            }
            number = Rect.MinMaxRect(low.x, low.y, high.x, high.y);
        }

        // One line above the health number, upright like it.
        private static void PlaceShieldNumber(Hud hud, RectTransform bar, Vector3 origin, float height, float remaining)
        {
            var health = hud.m_healthText.transform;
            var text = shieldText.transform;
            int amount = Mathf.CeilToInt(remaining);
            if (amount != shownAmount) shieldText.text = (shownAmount = amount).ToString();
            text.rotation = health.rotation;
            Vector3 scale = health.lossyScale, parent = text.parent.lossyScale;
            text.localScale = new Vector3(scale.x / parent.x, scale.y / parent.y, 1f);
            var point = origin + bar.TransformVector(new Vector3(number.center.x + number.width * 1.15f, height / 2f, 0f));
            text.position = point - text.TransformVector(shieldText.rectTransform.rect.center);
        }

        private static void Draw(View view, float length, float height)
        {
            var images = view.Images;
            float time = Time.unscaledTime, pulse = .8f + .2f * Mathf.Sin(time * 2.2f);
            const float edge = 1.5f;
            Place(images[0], 0f, 0f, length, height);
            images[0].uvRect = new Rect(0f, 0f, length / (height * 2.2f), 1f);
            Place(images[1], 0f, 0f, length, height);
            images[1].uvRect = new Rect(.37f, .11f, length / 60f, height / 60f);
            images[1].color = new Color(1f, 1f, 1f, .25f + .25f * Mathf.Abs(Mathf.Sin(time * 1.7f)));
            Place(images[2], 0f, height - edge, length, edge);
            Place(images[3], 0f, 0f, length, edge);
            Place(images[4], length - 10f, -3f, 20f, height + 6f);
            images[4].color = new Color(.75f, .95f, 1f, .85f * pulse);
            Place(images[5], length - 1.5f, -1f, 3f, height + 2f);
        }

        private static void MakeTextures()
        {
            if (cracks && glints && glow) return;
            // Ice cracks: the edges between cells of a tiling Voronoi pattern, drawn at four times the cells' grid so the lines
            // stay thin and sharp on the bar while the red health beneath them stays clear.
            const int detail = 4;
            var random = new System.Random(11);
            var seeds = new Vector2[11];
            for (int i = 0; i < seeds.Length; i++) seeds[i] = new Vector2(random.Next(64) * detail, random.Next(32) * detail);
            cracks = New(64 * detail, 32 * detail, TextureWrapMode.Repeat, (x, y) =>
            {
                float first = float.MaxValue, second = float.MaxValue;
                foreach (var seed in seeds)
                {
                    float dx = Mathf.Abs(x - seed.x), dy = Mathf.Abs(y - seed.y);
                    dx = Mathf.Min(dx, 64f * detail - dx); dy = Mathf.Min(dy, 32f * detail - dy);
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);
                    if (distance < first) { second = first; first = distance; }
                    else if (distance < second) second = distance;
                }
                float gap = (second - first) / 2f;
                return new Color(1f, 1f, 1f, 1f - Mathf.SmoothStep(0f, 1f, (gap - 1.2f) / 1.2f));
            }, true);
            // Four-pointed glints.
            var points = new Vector2[7];
            for (int i = 0; i < points.Length; i++) points[i] = new Vector2(random.Next(64), random.Next(64));
            glints = New(64, 64, TextureWrapMode.Repeat, (x, y) =>
            {
                float alpha = 0f;
                foreach (var point in points)
                {
                    float dx = Mathf.Abs(x - point.x), dy = Mathf.Abs(y - point.y);
                    dx = Mathf.Min(dx, 64f - dx); dy = Mathf.Min(dy, 64f - dy);
                    alpha = Mathf.Max(alpha, Mathf.Exp(-(dx * dx + dy * dy) / 3f) + .6f * Mathf.Exp(-dx * dx / .6f - dy * dy / 18f) + .6f * Mathf.Exp(-dy * dy / .6f - dx * dx / 18f));
                }
                return new Color(1f, 1f, 1f, Mathf.Clamp01(alpha));
            });
            glow = New(32, 4, TextureWrapMode.Clamp, (x, y) => new Color(1f, 1f, 1f, Mathf.Exp(-Mathf.Pow((x / 31f - .5f) / .2f, 2f))));
        }

        private static Texture2D New(int width, int height, TextureWrapMode wrap, System.Func<int, int, Color> pixel, bool mipmaps = false)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, mipmaps)
            {
                wrapMode = wrap, filterMode = mipmaps ? FilterMode.Trilinear : FilterMode.Bilinear, anisoLevel = mipmaps ? 4 : 1, hideFlags = HideFlags.HideAndDontSave
            };
            var pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    pixels[y * width + x] = pixel(x, y);
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }
    }
}
