using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using BalatroBridge.Table;
using Godot;
using MegaCrit.Sts2.Core.Helpers;

namespace BalatroBridge.Content;

/// <summary>
/// Relic and potion icons cut from Balatro's sprite sheets. StS2 loads these icons by resource
/// path, so each generated texture is put in Godot's resource cache under a res:// path
/// (TakeOverPath), and the relic or potion reports that path. Loading a cached path never touches
/// the disk. The images are composed on a worker thread at startup (<see cref="Prepare"/>), so
/// showing an icon for the first time doesn't stall a frame.
/// </summary>
internal static class BalatroIcons
{
    public enum Kind
    {
        Small,   // relic bar and potion belt: drawn into a ~60x60 slot, aspect kept
        Outline, // a white silhouette StS2 draws behind the small icon at 50% black
        Big,     // inspect screens and rewards: 256x256 like StS2's own big icons
    }

    private static readonly ConcurrentDictionary<(BalatroItem, Kind), Lazy<Image?>> Images = new();
    private static readonly Dictionary<string, Texture2D> Made = new();

    private static string MissingIconPath => ImageHelper.GetImagePath("powers/missing_power.png");

    /// <summary>Composes every kind of icon for the given items. Call on a worker thread.</summary>
    public static void Prepare(IEnumerable<BalatroItem> items)
    {
        foreach (BalatroItem item in items)
        {
            foreach (Kind kind in Enum.GetValues<Kind>()) ImageFor(item, kind);
        }
    }

    /// <summary>
    /// The icon's resource path (<paramref name="path"/> if the model's path is fixed), making its
    /// texture first if needed. StS2 reads icon paths at startup off the main thread; textures
    /// are only made on the main thread, just before use.
    /// </summary>
    public static string PathFor(BalatroItem item, Kind kind, string? path = null)
    {
        path ??= $"res://balatro_bridge/icons/{item.Key}_{kind.ToString().ToLowerInvariant()}.tres";
        if (OS.GetThreadCallerId() != OS.GetMainThreadId()) return path;
        if (Made.TryGetValue(path, out Texture2D? made) && GodotObject.IsInstanceValid(made)) return path;

        Image? image = ImageFor(item, kind);
        if (image == null) return MissingIconPath;
        // StS2's AssetCache unloads textures it had to load outside preloading when the player
        // returns to the menu, unless they are AtlasTextures; wrapping keeps these alive.
        var texture = new AtlasTexture { Atlas = ImageTexture.CreateFromImage(image), Region = new Rect2(Vector2.Zero, image.GetSize()) };
        texture.TakeOverPath(path);
        Made[path] = texture; // the resource cache only holds weak references
        return path;
    }

    /// <summary>
    /// Puts every consumable's icon where StS2 looks for its potion's art (a path fixed by the
    /// potion's id), so the potion belt finds it in the resource cache. Call on the main thread.
    /// </summary>
    public static void PlacePotionIcons()
    {
        foreach (BalatroItem item in BalatroContent.Consumables)
        {
            string entry = MegaCrit.Sts2.Core.Models.ModelDb.GetEntry(item.Model).ToLowerInvariant();
            PathFor(item, Kind.Small, ImageHelper.GetImagePath("atlases/potion_atlas.sprites/" + entry + ".tres"));
            PathFor(item, Kind.Outline, ImageHelper.GetImagePath("atlases/potion_outline_atlas.sprites/" + entry + ".tres"));
            PathFor(item, Kind.Big, ImageHelper.GetImagePath("potions/large/" + entry + ".png"));
        }
    }

    /// <summary>The composed image (base art, then its soul layer), or null if Balatro's art is missing.</summary>
    public static Image? ImageFor(BalatroItem item, Kind kind) =>
        Images.GetOrAdd((item, kind), _ => new Lazy<Image?>(() => Compose(item, kind))).Value;

    private static Image? Compose(BalatroItem item, Kind kind)
    {
        Image? art = Cut(item.Sprite);
        if (art == null) return null;
        if (item.Soul is BalatroSprite soul && Cut(soul) is Image overlay) art.BlendRect(overlay, new Rect2I(Vector2I.Zero, overlay.GetSize()), Vector2I.Zero);

        int size = kind == Kind.Big ? 256 : 128;
        int pad = kind == Kind.Big ? 12 : 8; // room for the outline
        int box = size - 2 * pad;
        float scale = Math.Min((float)box / art.GetWidth(), (float)box / art.GetHeight());
        int width = Math.Max(1, (int)Math.Round(art.GetWidth() * scale));
        int height = Math.Max(1, (int)Math.Round(art.GetHeight() * scale));
        // Pixel art stays crisp when blown up (stake chips); card art is shrunk smoothly.
        art.Resize(width, height, scale > 1 ? Image.Interpolation.Nearest : Image.Interpolation.Lanczos);

        Image canvas = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        canvas.BlitRect(art, new Rect2I(0, 0, width, height), new Vector2I((size - width) / 2, (size - height) / 2));
        return kind == Kind.Outline ? Silhouette(canvas, radius: 3) : canvas;
    }

    private static Image? Cut(BalatroSprite sprite)
    {
        Image? sheet = BalatroSheets.Get(sprite.Path);
        if (sheet == null) return null;
        Image art = sheet.GetRegion(new Rect2I(sprite.Column * sprite.Width, sprite.Row * sprite.Height, sprite.Width, sprite.Height));
        art.Convert(Image.Format.Rgba8);
        return art;
    }

    /// <summary>White, with each pixel as opaque as the most opaque pixel within the radius.</summary>
    internal static Image Silhouette(Image image, int radius)
    {
        int w = image.GetWidth(), h = image.GetHeight();
        byte[] source = image.GetData();
        var result = new byte[source.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                byte alpha = 0;
                for (int dy = -radius; dy <= radius && alpha < 255; dy++)
                {
                    int sy = y + dy;
                    if (sy < 0 || sy >= h) continue;
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int sx = x + dx;
                        if (sx < 0 || sx >= w || dx * dx + dy * dy > radius * radius) continue;
                        alpha = Math.Max(alpha, source[(sy * w + sx) * 4 + 3]);
                    }
                }
                int i = (y * w + x) * 4;
                result[i] = result[i + 1] = result[i + 2] = 255;
                result[i + 3] = alpha;
            }
        }
        return Image.CreateFromData(w, h, false, Image.Format.Rgba8, result);
    }
}
